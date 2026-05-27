package com.fluentia.app.transport

object ReadyState {
    const val CONNECTING = 0
    const val OPEN = 1
    const val CLOSING = 2
    const val CLOSED = 3
}

interface TransportConnection {
    val readyState: Int
    var onOpen: (() -> Unit)?
    var onMessage: ((String) -> Unit)?
    var onClose: (() -> Unit)?
    var onError: (() -> Unit)?
    suspend fun send(data: String): Boolean
    fun close(code: Int = 1000, reason: String = "")
}
