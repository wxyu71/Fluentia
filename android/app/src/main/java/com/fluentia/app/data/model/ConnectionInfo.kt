package com.fluentia.app.data.model

import kotlinx.serialization.Serializable

@Serializable
data class ConnectionInfo(
    val s: String,  // server WebSocket URL
    val t: String,  // room token
    val k: String,  // PC's public key (base64)
)
