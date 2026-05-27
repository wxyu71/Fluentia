package com.fluentia.app.crypto

import java.util.Base64
import javax.inject.Inject
import javax.inject.Singleton

data class PersistedCryptoSession(
    val publicKey: String,
    val secretKey: String,
    val peerPublicKey: String?,
)

data class EncryptedPayload(
    val payload: String,
    val nonce: String,
    val seq: Int? = null,
)

@Singleton
class CryptoService @Inject constructor(
    private val sodium: SodiumProvider,
) {
    private var keyPair: Pair<ByteArray, ByteArray> // (publicKey, secretKey)
    private var peerPublicKey: ByteArray? = null
    private var ready = false

    private var sendChainKey: ByteArray? = null
    private var sendSeq = 0
    private var ratchetReady = false

    private var recvChainKey: ByteArray? = null
    private var expectedSeq = 0
    private var recvRatchetReady = false

    init {
        keyPair = sodium.cryptoBoxKeypair()
    }

    private fun b64Encode(bytes: ByteArray): String =
        Base64.getEncoder().encodeToString(bytes)

    private fun b64Decode(str: String): ByteArray =
        Base64.getDecoder().decode(str)

    // --- Session persistence ---

    fun importSession(session: PersistedCryptoSession) {
        val pk = b64Decode(session.publicKey)
        val sk = b64Decode(session.secretKey)
        keyPair = pk to sk
        resetPeerState()
        if (session.peerPublicKey != null) {
            peerPublicKey = b64Decode(session.peerPublicKey)
            ready = true
        }
    }

    fun exportSession(): PersistedCryptoSession = PersistedCryptoSession(
        publicKey = b64Encode(keyPair.first),
        secretKey = b64Encode(keyPair.second),
        peerPublicKey = peerPublicKey?.let { b64Encode(it) },
    )

    // --- Public key ---

    fun getPublicKeyBase64(): String = b64Encode(keyPair.first)
    fun getPublicKeyBytes(): ByteArray = keyPair.first.copyOf()
    fun getSecretKeyBytes(): ByteArray = keyPair.second.copyOf()

    fun setPeerPublicKey(base64Key: String) {
        peerPublicKey = b64Decode(base64Key)
        ready = true
    }

    fun hasPeerKey(): Boolean = peerPublicKey != null
    fun isReady(): Boolean = ready
    fun isRatchetReady(): Boolean = ratchetReady
    fun isRecvRatchetReady(): Boolean = recvRatchetReady

    // --- Ratchet init ---

    fun initRatchet(): String {
        val seed = sodium.randomBytes(32)
        sendChainKey = kdf(seed, "fluentia_chain_v1")
        sendSeq = 0
        ratchetReady = true
        return b64Encode(seed)
    }

    fun initRecvRatchet(seedBase64: String) {
        val seed = b64Decode(seedBase64)
        recvChainKey = kdf(seed, "fluentia_chain_v1")
        expectedSeq = 0
        recvRatchetReady = true
    }

    // --- Legacy crypto_box encrypt/decrypt ---

    fun encrypt(plaintext: String): EncryptedPayload {
        val pk = peerPublicKey ?: throw IllegalStateException("Peer public key not set")
        val nonce = sodium.randomBytes(SodiumProvider.BOX_NONCE_BYTES)
        val messageBytes = plaintext.toByteArray(Charsets.UTF_8)
        val cipher = ByteArray(messageBytes.size + SodiumProvider.BOX_MAC_BYTES)
        sodium.cryptoBoxEasy(cipher, messageBytes, nonce, pk, keyPair.second)
        return EncryptedPayload(
            payload = b64Encode(cipher),
            nonce = b64Encode(nonce),
        )
    }

    fun decrypt(payloadBase64: String, nonceBase64: String): String {
        val pk = peerPublicKey ?: throw IllegalStateException("Peer public key not set")
        val cipher = b64Decode(payloadBase64)
        val nonce = b64Decode(nonceBase64)
        val message = ByteArray(cipher.size - SodiumProvider.BOX_MAC_BYTES)
        val ok = sodium.cryptoBoxOpenEasy(message, cipher, nonce, pk, keyPair.second)
        if (!ok) throw RuntimeException("Decryption failed")
        return String(message, Charsets.UTF_8)
    }

    // --- Ratcheted encrypt/decrypt ---

    fun encryptRatcheted(plaintext: String): EncryptedPayload {
        val ck = sendChainKey ?: throw IllegalStateException("Ratchet not initialized")
        val (messageKey, nextChainKey) = ratchetStep(ck)
        sendChainKey = nextChainKey
        val seq = sendSeq++

        val nonce = sodium.randomBytes(SodiumProvider.SECRET_BOX_NONCE_BYTES)
        val messageBytes = plaintext.toByteArray(Charsets.UTF_8)
        val cipher = ByteArray(messageBytes.size + SodiumProvider.SECRET_BOX_MAC_BYTES)
        sodium.cryptoSecretBoxEasy(cipher, messageBytes, nonce, messageKey)
        messageKey.fill(0)

        return EncryptedPayload(
            payload = b64Encode(cipher),
            nonce = b64Encode(nonce),
            seq = seq,
        )
    }

    fun decryptRatcheted(payloadBase64: String, nonceBase64: String, seq: Int): String {
        var ck = recvChainKey ?: throw IllegalStateException("Receive ratchet not initialized")

        while (expectedSeq < seq) {
            val (_, nextChainKey) = ratchetStep(ck)
            ck = nextChainKey
            expectedSeq++
        }
        recvChainKey = ck

        val (messageKey, nextChainKey) = ratchetStep(ck)
        recvChainKey = nextChainKey
        expectedSeq++

        val cipher = b64Decode(payloadBase64)
        val nonce = b64Decode(nonceBase64)
        val message = ByteArray(cipher.size - SodiumProvider.SECRET_BOX_MAC_BYTES)
        val ok = sodium.cryptoSecretBoxOpenEasy(message, cipher, nonce, messageKey)
        messageKey.fill(0)

        if (!ok) throw RuntimeException("Ratcheted decryption failed")
        return String(message, Charsets.UTF_8)
    }

    // --- Reset ---

    fun resetPeerState() {
        peerPublicKey = null
        ready = false
        sendChainKey = null
        sendSeq = 0
        ratchetReady = false
        recvChainKey = null
        expectedSeq = 0
        recvRatchetReady = false
    }

    fun reset() {
        keyPair = sodium.cryptoBoxKeypair()
        resetPeerState()
    }

    // --- KDF: SHA-512(key || label)[0:32] ---

    private fun kdf(key: ByteArray, label: String): ByteArray {
        val labelBytes = label.toByteArray(Charsets.UTF_8)
        val input = key + labelBytes
        val hash = ByteArray(SodiumProvider.SHA512_BYTES)
        sodium.cryptoHashSha512(hash, input)
        return hash.copyOf(32)
    }

    private fun ratchetStep(chainKey: ByteArray): Pair<ByteArray, ByteArray> {
        val messageKey = kdf(chainKey, "msg")
        val nextChainKey = kdf(chainKey, "chain")
        return messageKey to nextChainKey
    }
}
