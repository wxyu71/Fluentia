package com.fluentia.app.transport

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString

class OkHttpWebSocketTransport(
    private val client: OkHttpClient,
    private val url: String,
) : TransportConnection {

    private var ws: WebSocket? = null
    private var _readyState = ReadyState.CLOSED

    override val readyState: Int get() = _readyState
    override var onOpen: (() -> Unit)? = null
    override var onMessage: ((String) -> Unit)? = null
    override var onClose: (() -> Unit)? = null
    override var onError: (() -> Unit)? = null

    fun connect() {
        _readyState = ReadyState.CONNECTING
        val request = Request.Builder().url(url).build()
        ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                _readyState = ReadyState.OPEN
                onOpen?.invoke()
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                onMessage?.invoke(text)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                onMessage?.invoke(bytes.utf8())
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                _readyState = ReadyState.CLOSING
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                _readyState = ReadyState.CLOSED
                onClose?.invoke()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                _readyState = ReadyState.CLOSED
                onError?.invoke()
                onClose?.invoke()
            }
        })
    }

    override suspend fun send(data: String): Boolean {
        return ws?.send(data) ?: false
    }

    override fun close(code: Int, reason: String) {
        _readyState = ReadyState.CLOSING
        ws?.close(code, reason)
        ws = null
    }
}
