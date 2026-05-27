package com.fluentia.app.crypto

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.generators.X25519KeyPairGenerator
import org.bouncycastle.crypto.params.X25519KeyGenerationParameters
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * JVM-compatible SodiumProvider.
 * Uses Bouncy Castle X25519, pure-Java SHA-512, and HMAC-SHA256 for MAC.
 * Provides deterministic roundtrip behavior for testing.
 * Cross-platform NaCl byte-compatibility is verified via test vectors on device.
 */
class JvmSodiumProvider : SodiumProvider {

    private val random = SecureRandom()

    override fun cryptoBoxKeypair(): Pair<ByteArray, ByteArray> {
        val gen = X25519KeyPairGenerator()
        gen.init(X25519KeyGenerationParameters(random))
        val kp = gen.generateKeyPair()
        return (kp.public as X25519PublicKeyParameters).encoded to
            (kp.private as X25519PrivateKeyParameters).encoded
    }

    override fun cryptoBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean {
        val shared = computeSharedSecret(secretKey, publicKey)
        val subKey = sha512trunc32(shared + nonce)
        return doEncrypt(cipher, message, nonce, subKey)
    }

    override fun cryptoBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean {
        val shared = computeSharedSecret(secretKey, publicKey)
        val subKey = sha512trunc32(shared + nonce)
        return doDecrypt(message, cipher, nonce, subKey)
    }

    override fun cryptoSecretBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, key: ByteArray) =
        doEncrypt(cipher, message, nonce, key)

    override fun cryptoSecretBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, key: ByteArray) =
        doDecrypt(message, cipher, nonce, key)

    override fun cryptoHashSha512(out: ByteArray, input: ByteArray) {
        MessageDigest.getInstance("SHA-512").digest(input).copyInto(out)
    }

    override fun randomBytes(size: Int) = ByteArray(size).also { random.nextBytes(it) }

    override fun cryptoScalarMult(shared: ByteArray, secretKey: ByteArray, publicKey: ByteArray): Boolean {
        val a = X25519PrivateKeyParameters(secretKey, 0)
        val b = X25519PublicKeyParameters(publicKey, 0)
        val agr = X25519Agreement().also { it.init(a) }
        agr.calculateAgreement(b, shared, 0)
        return true
    }

    private fun computeSharedSecret(sk: ByteArray, pk: ByteArray): ByteArray {
        val a = X25519PrivateKeyParameters(sk, 0)
        val b = X25519PublicKeyParameters(pk, 0)
        val agr = X25519Agreement().also { it.init(a) }
        return ByteArray(agr.agreementSize).also { agr.calculateAgreement(b, it, 0) }
    }

    private fun sha512trunc32(input: ByteArray) = MessageDigest.getInstance("SHA-512").digest(input).copyOf(32)

    private fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    // Encrypt: XOR with SHA-512-derived keystream + HMAC-SHA256 for auth
    private fun doEncrypt(cipher: ByteArray, message: ByteArray, nonce: ByteArray, key: ByteArray): Boolean {
        val encKey = sha512trunc32(key + nonce + "e".toByteArray())
        val macKey = sha512trunc32(key + nonce + "m".toByteArray())
        val encrypted = xorCrypt(message, encKey)
        val mac = hmacSha256(macKey, encrypted).copyOf(16)
        encrypted.copyInto(cipher)
        mac.copyInto(cipher, encrypted.size)
        return true
    }

    private fun doDecrypt(message: ByteArray, cipher: ByteArray, nonce: ByteArray, key: ByteArray): Boolean {
        if (cipher.size < 16) return false
        val macOff = cipher.size - 16
        val encData = cipher.copyOf(macOff)
        val rMac = cipher.copyOfRange(macOff, cipher.size)
        val macKey = sha512trunc32(key + nonce + "m".toByteArray())
        val cMac = hmacSha256(macKey, encData).copyOf(16)
        if (!rMac.contentEquals(cMac)) return false
        val encKey = sha512trunc32(key + nonce + "e".toByteArray())
        xorCrypt(encData, encKey).copyInto(message)
        return true
    }

    private fun xorCrypt(data: ByteArray, key: ByteArray): ByteArray {
        val out = ByteArray(data.size)
        val blockSize = 32
        var off = 0
        var ctr = 0
        while (off < data.size) {
            val ctrBytes = byteArrayOf(ctr.toByte(), (ctr shr 8).toByte(), (ctr shr 16).toByte(), (ctr shr 24).toByte())
            val ks = sha512trunc32(key + ctrBytes)
            val len = minOf(blockSize, data.size - off)
            for (i in 0 until len) out[off + i] = (data[off + i].toInt() xor ks[i].toInt()).toByte()
            off += len; ctr++
        }
        return out
    }
}
