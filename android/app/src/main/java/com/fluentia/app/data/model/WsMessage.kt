package com.fluentia.app.data.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class WsMessage(
    val type: String,
    val token: String? = null,
    @SerialName("deviceId") val deviceId: String? = null,
    val role: String? = null,
    val publicKey: String? = null,
    val payload: String? = null,
    val nonce: String? = null,
    val error: String? = null,
    val version: String? = null,
    @SerialName("min_version") val minVersion: String? = null,
    val expiresAt: String? = null,
    val seq: Int? = null,
    val approved: Boolean? = null,
    val deviceCode: String? = null,
    val verifyId: String? = null,
    val userAgent: String? = null,
)
