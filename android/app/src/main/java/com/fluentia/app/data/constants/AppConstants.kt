package com.fluentia.app.data.constants

object AppConstants {
    const val PROTOCOL_VERSION = "1.7.0"

    const val DIFF_DEBOUNCE_MS = 60L

    const val CONNECT_TIMEOUT_MS = 8000L
    const val HANDSHAKE_TIMEOUT_MS = 12000L
    const val HEARTBEAT_INTERVAL_MS = 1000L
    const val HEARTBEAT_TIMEOUT_MS = 2500L
    const val FIXED_RECONNECT_DELAY_MS = 2000L
    const val MAX_RECONNECT_ATTEMPTS = 300
    const val OFFLINE_GRACE_MS = 10000L

    const val CHUNK_SIZE = 64 * 1024
    const val CHUNK_CONCURRENCY = 3
}
