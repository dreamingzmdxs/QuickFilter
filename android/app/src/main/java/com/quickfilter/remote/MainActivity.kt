package com.quickfilter.remote

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import android.util.Log
import org.json.JSONException
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.CopyOnWriteArrayList

class MainActivity : Activity() {

    private val devices = CopyOnWriteArrayList<DiscoveredDevice>()
    private lateinit var adapter: ArrayAdapter<String>
    private lateinit var historyAdapter: ArrayAdapter<String>
    private val handler = Handler(Looper.getMainLooper())
    @Volatile private var running = true
    private var socket: DatagramSocket? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        adapter = ArrayAdapter<String>(this, android.R.layout.simple_list_item_1)
        historyAdapter = ArrayAdapter<String>(this, android.R.layout.simple_list_item_1)
        findViewById<ListView>(R.id.devicesList).apply {
            this.adapter = this@MainActivity.adapter
            setOnItemClickListener { _, _, pos, _ -> onDeviceTap(adapter.getItem(pos) as? String) }
        }
        findViewById<ListView>(R.id.historyList).apply {
            this.adapter = this@MainActivity.historyAdapter
            setOnItemClickListener { _, _, pos, _ -> onHistoryTap(historyAdapter.getItem(pos) as? String) }
        }
        findViewById<Button>(R.id.connectBtn).setOnClickListener { manualConnect() }
        refreshHistory()

        Thread { discoveryLoop() }.start()
    }

    override fun onDestroy() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
        super.onDestroy()
    }

    /** 主动广播 discover-query + 被动监听桌面端每 3 秒的 beacon，收集电脑列表。 */
    private fun discoveryLoop() {
        try {
            val sock = DatagramSocket(47800).apply { soTimeout = 1000 }
            socket = sock
            Log.i(TAG, "发现 socket 已绑定 47800")
            val query = "discover-query".toByteArray()
            while (running) {
                try {
                    sock.send(DatagramPacket(query, query.size, InetAddress.getByName("255.255.255.255"), 47800))
                } catch (e: Exception) {
                    Log.e(TAG, "发送查询失败", e)
                }
                try {
                    while (true) {
                        val buf = ByteArray(4096)
                        val pkt = DatagramPacket(buf, buf.size)
                        sock.receive(pkt)
                        val text = String(buf, 0, pkt.length)
                        try {
                            val json = JSONObject(text)
                            if (json.optString("app", "") == "QuickFilter" && json.optString("type", "") == "discover") {
                                val dev = DiscoveredDevice(
                                    pkt.address.hostAddress ?: "",
                                    json.optInt("port", 47900),
                                    json.optString("machine", "电脑"))
                                handler.post { addOrUpdate(dev) }
                            }
                        } catch (_: JSONException) {
                            // 非 JSON 数据包（如自己 discover-query 广播的回显）直接忽略
                        }
                    }
                } catch (_: SocketTimeoutException) {
                    // 每轮查询后等待 1 秒
                } catch (e: Exception) {
                    Log.e(TAG, "接收循环异常", e)
                    return
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "发现线程启动失败", e)
        }
    }

    private companion object { const val TAG = "QFRemote" }

    private fun addOrUpdate(dev: DiscoveredDevice) {
        val existing = devices.firstOrNull { it.ip == dev.ip && it.port == dev.port }
        if (existing != null) devices.remove(existing)
        devices.add(0, dev)
        if (adapter.count != devices.size || existing == null) refreshList()
    }

    private fun refreshList() {
        adapter.clear()
        devices.forEach { adapter.add(it.toString()) }
        adapter.notifyDataSetChanged()
    }

    private fun onDeviceTap(display: String?) {
        val ipAndPort = display?.substringAfterLast("（")?.removeSuffix("）")?.split(":")
            ?: return
        if (ipAndPort.size < 2) return
        showPinDialog(ipAndPort[0], ipAndPort[1].toIntOrNull() ?: 47900)
    }

    private fun manualConnect() {
        val ip = findViewById<EditText>(R.id.manualIp).text.toString().trim()
        if (ip.isEmpty()) {
            Toast.makeText(this, "请先填写电脑 IP", Toast.LENGTH_SHORT).show()
            return
        }
        val port = findViewById<EditText>(R.id.manualPort).text.toString().trim().toIntOrNull() ?: 47900
        showPinDialog(ip, port)
    }

    private fun onHistoryTap(display: String?) {
        val ipAndPort = display?.substringAfterLast("（")?.removeSuffix("）")?.split(":")
            ?: return
        if (ipAndPort.size < 2) return
        val ip = ipAndPort[0]
        val port = ipAndPort[1].toIntOrNull() ?: 47900
        val entry = ConnectionHistory(this).load().firstOrNull { it.ip == ip && it.port == port }
        if (entry != null && entry.pin.isNotEmpty()) {
            // 记住 PIN → 快速直连
            startActivity(Intent(this, ControlActivity::class.java).apply {
                putExtra("host", entry.ip)
                putExtra("port", entry.port)
                putExtra("pin", entry.pin)
            })
        } else {
            showPinDialog(ip, port)
        }
    }

    private fun refreshHistory() {
        val list = ConnectionHistory(this).load()
        historyAdapter.clear()
        list.forEach { historyAdapter.add("${it.machine}（${it.ip}:${it.port}）") }
        historyAdapter.notifyDataSetChanged()
        val visible = list.isNotEmpty()
        findViewById<ListView>(R.id.historyList).visibility = if (visible) View.VISIBLE else View.GONE
        findViewById<TextView>(R.id.historyTitle).visibility = if (visible) View.VISIBLE else View.GONE
    }

    private fun showPinDialog(ip: String, port: Int) {
        val input = EditText(this).apply {
            hint = "4 位 PIN（电脑界面上显示）"
        }
        AlertDialog.Builder(this)
            .setTitle("连接 $ip:$port")
            .setView(input)
            .setCancelable(true)
            .setPositiveButton("连接") { _, _ ->
                val pin = input.text.toString().trim()
                if (pin.isEmpty()) {
                    Toast.makeText(this, "请输入 PIN", Toast.LENGTH_SHORT).show()
                    return@setPositiveButton
                }
                startActivity(Intent(this, ControlActivity::class.java).apply {
                    putExtra("host", ip)
                    putExtra("port", port)
                    putExtra("pin", pin)
                })
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun updateStatus(text: String) {
        findViewById<TextView>(R.id.scanStatus)?.text = text
    }
}
