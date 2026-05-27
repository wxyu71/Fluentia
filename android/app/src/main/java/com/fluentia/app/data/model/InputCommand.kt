package com.fluentia.app.data.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class InputCommand(
    val type: String,
    val text: String? = null,
    val count: Int? = null,
    val seed: String? = null,
    val publicKey: String? = null,
    val fileName: String? = null,
    val fileSize: Long? = null,
    val mimeType: String? = null,
    val chunkIndex: Int? = null,
    val chunkData: String? = null,
    val isLast: Boolean? = null,
    val transferId: String? = null,
)

object CommandType {
    const val DIFF = "diff"
    const val ENTER = "enter"
    const val BACKSPACE = "backspace"
    const val CLEAR = "clear"
    const val CLIPBOARD = "clipboard"
    const val REGEX_CONFIG = "regex_config"
    const val RATCHET_INIT = "ratchet_init"
    const val PC_RATCHET_INIT = "pc_ratchet_init"
    const val HANDSHAKE_ACK = "handshake_ack"
    const val BLE_AUTH = "ble_auth"
    const val BLE_AUTH_OK = "ble_auth_ok"
    const val FILE_START = "file_start"
    const val FILE_CHUNK = "file_chunk"
    const val FILE_ABORT = "file_abort"
}
