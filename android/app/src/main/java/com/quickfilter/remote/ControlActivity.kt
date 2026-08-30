package com.quickfilter.remote

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import android.graphics.Color
import android.graphics.drawable.ColorDrawable
import android.util.Log
import coil.load
import org.json.JSONObject
import kotlin.math.abs

/**
 * 遥控操作界面：同屏显示当前图片（Coil 加载 HTTP 图片通道），
 * 左右上下滑动 = 左/右目录/删除/跳过，另有按钮等效操作与撤销。
 * 断线后每 3 秒自动重连。
 */
class ControlActivity : Activity(), RemoteClient.Listener {

    private lateinit var client: RemoteClient
    private lateinit var host: String
    private var port = 47900
    private var pin = ""
    private val handler = Handler(Looper.getMainLooper())
    @Volatile private var destroyed = false

    private lateinit var imageView: ImageView
    private lateinit var statusText: TextView
    private lateinit var progressText: TextView

    /** 已加载的图片 URL：相同的 URL 不重复加载（防止心跳广播触发重复请求导致的闪烁）。 */
    @Volatile private var lastImageUrl: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_control)

        host = intent.getStringExtra("host") ?: "127.0.0.1"
        port = intent.getIntExtra("port", 47900)
        pin = intent.getStringExtra("pin") ?: ""
        imageView = findViewById(R.id.imageView)
        imageView.setBackgroundColor(Color.BLACK)   // 加载间隙始终黑底，杜绝白屏闪烁
        statusText = findViewById(R.id.statusText)
        progressText = findViewById(R.id.progressText)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val gestureDetector = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onFling(e1: MotionEvent?, e2: MotionEvent, vx: Float, vy: Float): Boolean {
                if (e1 == null) return false
                val dx = e2.x - e1.x
                val dy = e2.y - e1.y
                if (abs(dx) > abs(dy)) {
                    if (abs(dx) > 80) sendGesture(if (dx < 0) "left" else "right")
                } else {
                    if (abs(dy) > 80) sendGesture(if (dy < 0) "delete" else "skip")
                }
                return true
            }
        })
        imageView.setOnTouchListener { _, ev ->
            gestureDetector.onTouchEvent(ev)
            true
        }

        findViewById<Button>(R.id.btnLeft).setOnClickListener { sendGesture("left") }
        findViewById<Button>(R.id.btnRight).setOnClickListener { sendGesture("right") }
        findViewById<Button>(R.id.btnDelete).setOnClickListener { sendGesture("delete") }
        findViewById<Button>(R.id.btnSkip).setOnClickListener { sendGesture("skip") }
        findViewById<Button>(R.id.btnUndo).setOnClickListener { client.sendUndo() }

        client = RemoteClient(host, port, pin, Build.MODEL ?: "安卓设备", this)
        connect()
    }

    override fun onDestroy() {
        destroyed = true
        client.close()
        super.onDestroy()
    }

    private fun connect() {
        if (destroyed) return
        statusText.text = "正在连接 $host:$port …"
        client.connect()
    }

    private fun sendGesture(action: String) {
        client.sendGesture(action)
    }

    override fun onMessage(text: String) {
        Log.i("QFRemote", "收到帧(${text.length}B): ${text.take(60)}")
        runOnUiThread {
            try {
                val json = JSONObject(text)
                when (json.optString("type", "")) {
                    "auth-ok" -> {
                        statusText.text = "已连接 $host"
                        // 记录历史：下次可一键快速连接
                        ConnectionHistory(applicationContext).save(
                            ConnectionHistory.Entry(host, port, Build.MODEL ?: "安卓设备", pin))
                        Log.i("QFRemote", "已保存连接历史 $host:$port")
                    }
                    "auth-fail" -> {
                        Toast.makeText(this, "认证失败：${json.optString("error", "PIN 错误")}（PIN 可能已更改，请重新输入）", Toast.LENGTH_LONG).show()
                        // 清除记忆的 PIN：下次点击历史连接将要求重新输入
                        ConnectionHistory(applicationContext).save(
                            ConnectionHistory.Entry(host, port, Build.MODEL ?: "安卓设备", ""))
                        finish()
                    }
                    "state" -> applyState(json)
                    "ack", "pong" -> Unit
                }
            } catch (_: Exception) {
            }
        }
    }

    override fun onDisconnected(reason: String?) {
        Log.w("QFRemote", "断开: $reason")
        if (destroyed) return
        runOnUiThread {
            statusText.text = "连接断开，3 秒后自动重连…（$reason）"
        }
        handler.postDelayed({ if (!destroyed) connect() }, 3000)
    }

    private fun applyState(json: JSONObject) {
        val done = json.optBoolean("done", false)
        val index = json.optInt("index", -1)
        val total = json.optInt("total", 0)
        val counts = json.optJSONObject("counts")
        val l = counts?.optInt("left", 0) ?: 0
        val r = counts?.optInt("right", 0) ?: 0
        val d = counts?.optInt("delete", 0) ?: 0
        val s = counts?.optInt("skip", 0) ?: 0

        progressText.text = if (done) "全部处理完成 ✓"
        else "第 ${index + 1} / $total 张 · 左 $l · 右 $r · 删除 $d · 跳过 $s"

        val image = json.optString("image", "")
        if (image.isNotEmpty() && image != lastImageUrl) {
            lastImageUrl = image
            Log.i("QFRemote", "加载图片: $image")
            imageView.load("http://$host:$port$image") {
                crossfade(true)
                placeholder(ColorDrawable(Color.BLACK))
                error(ColorDrawable(Color.BLACK))
            }
        }
    }
}
