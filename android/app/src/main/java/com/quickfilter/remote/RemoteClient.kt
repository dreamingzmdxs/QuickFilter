package com.quickfilter.remote

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit

/**
 * 遥控客户端：连接电脑端 WebSocket，先认证 PIN，之后发送手势/撤销指令。
 * 消息以原始 JSON 文本回调给 Listener（后台线程，需自行切 UI 线程）。
 */
class RemoteClient(
    private val host: String,
    private val port: Int,
    private val pin: String,
    private val deviceName: String,
    private val listener: Listener
) {
    interface Listener {
        fun onMessage(text: String)
        fun onDisconnected(reason: String?)
    }

    private val client = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)   // 长连接，客户端主动断开才结束
        .pingInterval(10, TimeUnit.SECONDS)
        .build()

    private var ws: WebSocket? = null

    fun connect() {
        ws = client.newWebSocket(
            Request.Builder().url("ws://$host:$port/ws").build(),
            object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send("""{"type":"auth","pin":"$pin","device":"$deviceName"}""")
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    listener.onMessage(text)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    listener.onDisconnected(t.message ?: "连接失败")
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    listener.onDisconnected(reason.ifEmpty { "连接已关闭" })
                }
            }
        )
    }

    fun sendGesture(action: String) {
        ws?.send("""{"type":"gesture","action":"$action"}""")
    }

    fun sendUndo() {
        ws?.send("""{"type":"undo"}""")
    }

    fun close() {
        ws?.close(1000, "bye")
        ws = null
    }
}
