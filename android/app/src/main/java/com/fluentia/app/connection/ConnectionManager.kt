package com.fluentia.app.connection

import com.fluentia.app.crypto.CryptoService
import com.fluentia.app.data.constants.AppConstants
import com.fluentia.app.data.model.CommandType
import com.fluentia.app.data.model.ConnectionInfo
import com.fluentia.app.data.model.InputCommand
import com.fluentia.app.data.model.WsMessage
import com.fluentia.app.transport.OkHttpWebSocketTransport
import com.fluentia.app.transport.ReadyState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import javax.inject.Inject
import javax.inject.Singleton

enum class ConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Preempted,
}

@Singleton
class ConnectionManager @Inject constructor(
    private val okHttpClient: OkHttpClient,
    private val crypto: CryptoService,
) {
    private val json = Json { encodeDefaults = false; ignoreUnknownKeys = true }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _state = MutableStateFlow(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private val _encryptionReady = MutableStateFlow(false)
    val encryptionReady: StateFlow<Boolean> = _encryptionReady.asStateFlow()

    private var transport: OkHttpWebSocketTransport? = null
    private var heartbeatJob: Job? = null
    private var reconnectJob: Job? = null
    private var handshakeJob: Job? = null
    private var connectionInfo: ConnectionInfo? = null
    private var reconnectAttempts = 0
    private var intentionalClose = false
    private var deviceId: String = ""

    fun connect(info: ConnectionInfo, deviceId: String) {
        if (_state.value == ConnectionState.Connected || _state.value == ConnectionState.Connecting) return
        this.connectionInfo = info
        this.deviceId = deviceId
        this.intentionalClose = false
        this.reconnectAttempts = 0
        doConnect()
    }

    fun disconnect() {
        intentionalClose = true
        cleanup()
        _state.value = ConnectionState.Disconnected
    }

    suspend fun sendEncrypted(command: InputCommand) {
        val plaintext = json.encodeToString(command)
        val wsMsg = if (crypto.isRatchetReady()) {
            val enc = crypto.encryptRatcheted(plaintext)
            WsMessage(type = "encrypted", payload = enc.payload, nonce = enc.nonce, seq = enc.seq)
        } else {
            val enc = crypto.encrypt(plaintext)
            WsMessage(type = "encrypted", payload = enc.payload, nonce = enc.nonce)
        }
        transport?.send(json.encodeToString(wsMsg))
    }

    private fun doConnect() {
        val info = connectionInfo ?: return
        _state.value = ConnectionState.Connecting

        val wsUrl = info.s
        val t = OkHttpWebSocketTransport(okHttpClient, wsUrl)
        transport = t

        t.onOpen = {
            val joinMsg = WsMessage(
                type = "join_session",
                token = info.t,
                deviceId = deviceId,
                version = AppConstants.PROTOCOL_VERSION,
            )
            scope.launch { t.send(json.encodeToString(joinMsg)) }
            startHandshakeTimeout()
        }

        t.onMessage = { text ->
            scope.launch { handleMessage(text) }
        }

        t.onClose = {
            stopHeartbeat()
            if (!intentionalClose && _state.value != ConnectionState.Preempted) {
                _state.value = ConnectionState.Disconnected
                scheduleReconnect()
            }
        }

        t.onError = { }

        t.connect()
    }

    private suspend fun handleMessage(text: String) {
        val msg = try {
            json.decodeFromString<WsMessage>(text)
        } catch (_: Exception) {
            return
        }

        when (msg.type) {
            "joined" -> {
                // Server confirmed join. If PC's public key is included, set it.
                if (msg.publicKey != null && !crypto.hasPeerKey()) {
                    crypto.setPeerPublicKey(msg.publicKey)
                }
            }
            "peer_joined" -> {
                if (msg.role == "pc") {
                    // PC connected — initiate key exchange
                    val keyExchangeMsg = WsMessage(
                        type = "key_exchange",
                        publicKey = crypto.getPublicKeyBase64(),
                    )
                    transport?.send(json.encodeToString(keyExchangeMsg))
                    beginSecureHandshake()
                }
            }
            "peer_left" -> {
                if (msg.role == "pc") {
                    stopHeartbeat()
                    _state.value = ConnectionState.Disconnected
                }
            }
            "key_exchange" -> {
                if (msg.publicKey != null) {
                    crypto.setPeerPublicKey(msg.publicKey)
                }
            }
            "encrypted" -> {
                handleEncrypted(msg)
            }
            "preempted" -> {
                intentionalClose = true
                cleanup()
                _state.value = ConnectionState.Preempted
            }
            "error" -> {
                // Log error
            }
            "pong" -> {
                // Heartbeat response — handled by heartbeat job
            }
        }
    }

    private suspend fun beginSecureHandshake() {
        // Init send ratchet, encrypt seed with crypto_box, send as ratchet_init
        val seed = crypto.initRatchet()
        val ratchetCmd = InputCommand(type = CommandType.RATCHET_INIT, seed = seed)
        sendEncrypted(ratchetCmd)
    }

    private suspend fun handleEncrypted(msg: WsMessage) {
        val payload = msg.payload ?: return
        val nonce = msg.nonce ?: return

        val plaintext = try {
            if (msg.seq != null && crypto.isRecvRatchetReady()) {
                crypto.decryptRatcheted(payload, nonce, msg.seq)
            } else {
                crypto.decrypt(payload, nonce)
            }
        } catch (_: Exception) {
            return
        }

        val command = try {
            json.decodeFromString<InputCommand>(plaintext)
        } catch (_: Exception) {
            return
        }

        when (command.type) {
            CommandType.PC_RATCHET_INIT -> {
                val seed = command.seed ?: return
                crypto.initRecvRatchet(seed)
                // Send handshake_ack
                val ack = InputCommand(type = CommandType.HANDSHAKE_ACK)
                sendEncrypted(ack)
                _encryptionReady.value = true
                _state.value = ConnectionState.Connected
                cancelHandshakeTimeout()
                startHeartbeat()
            }
            CommandType.CLEAR -> {
                // PC requests input clear — handled by UI layer
            }
            // Other command types handled by UI layer
        }
    }

    private fun startHeartbeat() {
        stopHeartbeat()
        heartbeatJob = scope.launch {
            while (isActive) {
                delay(AppConstants.HEARTBEAT_INTERVAL_MS)
                val pingMsg = WsMessage(type = "ping")
                try {
                    transport?.send(json.encodeToString(pingMsg))
                } catch (_: Exception) {
                    break
                }
            }
        }
    }

    private fun stopHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    private fun startHandshakeTimeout() {
        cancelHandshakeTimeout()
        handshakeJob = scope.launch {
            delay(AppConstants.HANDSHAKE_TIMEOUT_MS)
            if (_state.value == ConnectionState.Connecting) {
                // Handshake timed out
                cleanup()
                _state.value = ConnectionState.Disconnected
                scheduleReconnect()
            }
        }
    }

    private fun cancelHandshakeTimeout() {
        handshakeJob?.cancel()
        handshakeJob = null
    }

    private fun scheduleReconnect() {
        if (intentionalClose) return
        if (reconnectAttempts >= AppConstants.MAX_RECONNECT_ATTEMPTS) return

        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(AppConstants.FIXED_RECONNECT_DELAY_MS)
            if (!intentionalClose && _state.value == ConnectionState.Disconnected) {
                reconnectAttempts++
                doConnect()
            }
        }
    }

    private fun cleanup() {
        stopHeartbeat()
        cancelHandshakeTimeout()
        reconnectJob?.cancel()
        reconnectJob = null
        transport?.close()
        transport = null
        _encryptionReady.value = false
    }
}
