package com.fluentia.app.ble

import com.fluentia.app.crypto.CryptoService
import com.fluentia.app.crypto.SodiumProvider
import com.fluentia.app.data.constants.AppConstants
import com.fluentia.app.data.model.ConnectionInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.util.Base64

enum class BlePairingState {
    Idle,
    Scanning,
    Connecting,
    WaitingVerification,
    WaitingApproval,
    Connected,
    Failed,
}

class BlePairingManager(
    private val crypto: CryptoService,
    private val sodium: SodiumProvider,
    private val transport: BleTransportManager,
) {
    private val json = Json { encodeDefaults = false; ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _state = MutableStateFlow(BlePairingState.Idle)
    val state: StateFlow<BlePairingState> = _state.asStateFlow()

    private val _verificationCode = MutableStateFlow<String?>(null)
    val verificationCode: StateFlow<String?> = _verificationCode.asStateFlow()

    private val _connectionInfo = MutableStateFlow<ConnectionInfo?>(null)
    val connectionInfo: StateFlow<ConnectionInfo?> = _connectionInfo.asStateFlow()

    var onBleAuthRequired: ((String) -> Unit)? = null

    fun startPairing() {
        _state.value = BlePairingState.Scanning

        transport.onOpen = {
            _state.value = BlePairingState.WaitingVerification
            sendNotifyReady()
        }

        transport.onMessage = { text ->
            scope.launch { handleBleMessage(text) }
        }

        transport.onClose = {
            if (_state.value != BlePairingState.Connected) {
                _state.value = BlePairingState.Failed
            }
        }

        transport.scanAndConnect { _ ->
            _state.value = BlePairingState.Connecting
        }
    }

    private fun sendNotifyReady() {
        val envelope = BleEnvelope(
            type = "notify_ready",
            publicKey = crypto.getPublicKeyBase64(),
            version = AppConstants.PROTOCOL_VERSION,
        )
        transport.sendEnvelope(envelope)
    }

    private suspend fun handleBleMessage(text: String) {
        val envelope = try {
            json.decodeFromString<BleEnvelope>(text)
        } catch (_: Exception) {
            return
        }

        when (envelope.type) {
            "notify_ready" -> {
                if (envelope.publicKey != null) {
                    crypto.setPeerPublicKey(envelope.publicKey)
                    val code = deriveVerificationCode(envelope.publicKey)
                    _verificationCode.value = code
                    _state.value = BlePairingState.WaitingVerification
                }
            }
            "verified" -> {
                if (envelope.serverUrl != null && envelope.token != null && envelope.publicKey != null) {
                    _connectionInfo.value = ConnectionInfo(
                        s = envelope.serverUrl,
                        t = envelope.token,
                        k = envelope.publicKey,
                    )
                    _state.value = BlePairingState.Connected
                }
            }
            "error" -> {
                _state.value = BlePairingState.Failed
            }
        }
    }

    fun approveBleAuth() {
        val envelope = BleEnvelope(
            type = "client_hello",
            publicKey = crypto.getPublicKeyBase64(),
            version = AppConstants.PROTOCOL_VERSION,
        )
        transport.sendEnvelope(envelope)
        _state.value = BlePairingState.WaitingApproval
    }

    /**
     * Derive a 6-digit verification code from the X25519 shared secret.
     * Algorithm (matching TypeScript deriveBleVerificationCode in ble.ts):
     *   shared = scalarMult(localSecretKey[0:32], remotePublicKey)
     *   combined = shared || localPublicKey || remotePublicKey
     *   hash = SHA-512(combined)
     *   code = (hash[0] << 16 | hash[1] << 8 | hash[2]) % 1000000
     *   return code zero-padded to 6 digits
     */
    private fun deriveVerificationCode(remotePublicKeyBase64: String): String {
        val localSecretKey = crypto.getSecretKeyBytes()
        val localPublicKey = crypto.getPublicKeyBytes()
        val remotePublicKey = Base64.getDecoder().decode(remotePublicKeyBase64)

        // X25519 scalar multiplication: shared = scalarMult(sk[0:32], remotePk)
        val shared = ByteArray(SodiumProvider.SCALAR_MULT_BYTES)
        sodium.cryptoScalarMult(shared, localSecretKey.copyOf(32), remotePublicKey)

        // combined = shared || localPublicKey || remotePublicKey
        val combined = shared + localPublicKey + remotePublicKey

        // SHA-512 hash
        val hash = ByteArray(SodiumProvider.SHA512_BYTES)
        sodium.cryptoHashSha512(hash, combined)

        // First 3 bytes as big-endian 24-bit integer, mod 1000000
        val numeric = ((hash[0].toInt() and 0xFF) shl 16 or
            ((hash[1].toInt() and 0xFF) shl 8) or
            (hash[2].toInt() and 0xFF)) % 1000000

        return numeric.toString().padStart(6, '0')
    }

    fun stopPairing() {
        transport.close()
        _state.value = BlePairingState.Idle
        _verificationCode.value = null
        _connectionInfo.value = null
    }
}
