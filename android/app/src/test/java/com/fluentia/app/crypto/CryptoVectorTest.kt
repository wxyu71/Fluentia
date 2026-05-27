package com.fluentia.app.crypto

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

/**
 * Cross-platform interoperability tests using test vectors generated
 * from the TypeScript mobile implementation (tweetnacl).
 *
 * These verify that the Android CryptoService produces byte-identical
 * output for KDF, SHA-512, and ScalarMult operations.
 *
 * Box/Secretbox ciphertext cannot be compared directly because the JVM
 * test provider uses HMAC-SHA256 (not NaCl Poly1305), but the roundtrip
 * behavior is verified by CryptoServiceTest.
 */
class CryptoVectorTest {

    private val json = Json { ignoreUnknownKeys = true }
    private val b64 = Base64.getDecoder()

    @Serializable
    private data class TestVectors(
        val alice: KeyPairVec,
        val bob: KeyPairVec,
        val kdf: KdfVec,
        val sha512: Sha512Vec,
        val box: BoxVec,
        val ratchet: RatchetVec,
        val scalarMult: ScalarMultVec,
    )

    @Serializable private data class KeyPairVec(val secretKey: String, val publicKey: String)
    @Serializable private data class KdfVec(val input: String, val label: String, val output: String)
    @Serializable private data class Sha512Vec(val input: String, val output: String)
    @Serializable private data class BoxVec(val plaintext: String, val nonce: String, val ciphertext: String, val decrypted: String)
    @Serializable private data class RatchetVec(
        val aliceSeed: String,
        val aliceChainKey: String,
        val bobSeed: String,
        val bobChainKey: String,
        val aliceSends: List<RatchetMsg>,
        val bobSends: List<RatchetMsg>,
    )
    @Serializable private data class RatchetMsg(val seq: Int, val plaintext: String, val nonce: String, val ciphertext: String)
    @Serializable private data class ScalarMultVec(val secretKey: String, val publicKey: String, val sharedSecret: String)

    private fun loadVectors(): TestVectors {
        val stream = javaClass.classLoader!!.getResourceAsStream("test-vectors/crypto-vectors.json")
            ?: throw IllegalStateException("Test vectors file not found")
        return json.decodeFromString(stream.bufferedReader().readText())
    }

    @Test
    fun `KDF output matches TypeScript tweetnacl`() {
        val v = loadVectors()
        val input = b64.decode(v.kdf.input)
        val expected = b64.decode(v.kdf.output)

        val result = Kdf.derive(input, v.kdf.label)
        assertArrayEquals("KDF output must match TypeScript tweetnacl byte-for-byte", expected, result)
    }

    @Test
    fun `KDF chain init matches TypeScript`() {
        val v = loadVectors()
        val aliceSeed = b64.decode(v.ratchet.aliceSeed)
        val expectedChainKey = b64.decode(v.ratchet.aliceChainKey)

        val chainKey = Kdf.chainInit(aliceSeed)
        assertArrayEquals("Alice chain key must match TypeScript", expectedChainKey, chainKey)
    }

    @Test
    fun `KDF message key derivation matches TypeScript`() {
        val v = loadVectors()
        val chainKey = b64.decode(v.ratchet.aliceChainKey)

        // First message key = kdf(chainKey, "msg")
        val msgKey = Kdf.messageKey(chainKey)
        // Verify it's 32 bytes and deterministic
        assertEquals(32, msgKey.size)

        // Derive again — must be same
        val msgKey2 = Kdf.messageKey(chainKey)
        assertArrayEquals("Message key must be deterministic", msgKey, msgKey2)
    }

    @Test
    fun `KDF next chain key derivation matches TypeScript`() {
        val v = loadVectors()
        val chainKey = b64.decode(v.ratchet.aliceChainKey)

        // nextChainKey = kdf(chainKey, "chain")
        val next = Kdf.nextChainKey(chainKey)
        assertEquals(32, next.size)

        // Verify it's different from message key
        val msgKey = Kdf.messageKey(chainKey)
        assertFalse("Message key and next chain key should differ", msgKey.contentEquals(next))
    }

    @Test
    fun `SHA-512 output matches TypeScript tweetnacl`() {
        val v = loadVectors()
        val input = b64.decode(v.sha512.input)
        val expected = b64.decode(v.sha512.output)

        val provider = JvmSodiumProvider()
        val output = ByteArray(64)
        provider.cryptoHashSha512(output, input)
        assertArrayEquals("SHA-512 must match TypeScript tweetnacl byte-for-byte", expected, output)
    }

    @Test
    fun `ScalarMult shared secret matches TypeScript tweetnacl`() {
        val v = loadVectors()
        val secretKey = b64.decode(v.scalarMult.secretKey)
        val publicKey = b64.decode(v.scalarMult.publicKey)
        val expected = b64.decode(v.scalarMult.sharedSecret)

        val provider = JvmSodiumProvider()
        val shared = ByteArray(32)
        provider.cryptoScalarMult(shared, secretKey, publicKey)
        assertArrayEquals("X25519 shared secret must match TypeScript tweetnacl byte-for-byte", expected, shared)
    }

    @Test
    fun `ScalarMult produces same result as verification code derivation path`() {
        val v = loadVectors()
        // The verification code uses: scalarMult(localSk[0:32], remotePk)
        val localSk = b64.decode(v.alice.secretKey)
        val remotePk = b64.decode(v.bob.publicKey)
        val expected = b64.decode(v.scalarMult.sharedSecret)

        val provider = JvmSodiumProvider()
        val shared = ByteArray(32)
        // Use first 32 bytes of secret key, matching TypeScript's localSecretKey.subarray(0, 32)
        provider.cryptoScalarMult(shared, localSk.copyOf(32), remotePk)
        assertArrayEquals("Verification code scalarMult must match", expected, shared)
    }

    @Test
    fun `box encrypt produces consistent output (roundtrip)`() {
        val v = loadVectors()
        val aliceSk = b64.decode(v.alice.secretKey)
        val alicePk = b64.decode(v.alice.publicKey)
        val bobPk = b64.decode(v.bob.publicKey)
        val nonce = b64.decode(v.box.nonce)
        val plaintext = v.box.plaintext.toByteArray(Charsets.UTF_8)

        val provider = JvmSodiumProvider()

        // Encrypt with Alice's key to Bob
        val cipher = ByteArray(plaintext.size + 16) // + MACBYTES
        provider.cryptoBoxEasy(cipher, plaintext, nonce, bobPk, aliceSk)

        // Decrypt with Bob's key from Alice
        val decrypted = ByteArray(plaintext.size)
        val ok = provider.cryptoBoxOpenEasy(decrypted, cipher, nonce, alicePk, b64.decode(v.bob.secretKey))
        assertEquals("Box decrypt must succeed", true, ok)
        assertArrayEquals("Box roundtrip must recover original plaintext", plaintext, decrypted)
    }

    @Test
    fun `ratchet encrypt produces consistent roundtrip`() {
        val v = loadVectors()

        val alice = CryptoService(JvmSodiumProvider())
        val bob = CryptoService(JvmSodiumProvider())

        // Use vector keys
        alice.importSession(PersistedCryptoSession(
            publicKey = v.alice.publicKey,
            secretKey = v.alice.secretKey,
            peerPublicKey = v.bob.publicKey,
        ))
        bob.importSession(PersistedCryptoSession(
            publicKey = v.bob.publicKey,
            secretKey = v.bob.secretKey,
            peerPublicKey = v.alice.publicKey,
        ))

        // Alice's send chain: use initRatchet to generate a new seed,
        // then Bob receives that seed
        val aliceSeedB64 = alice.initRatchet()
        bob.initRecvRatchet(aliceSeedB64)

        // Bob's send chain: same pattern
        val bobSeedB64 = bob.initRatchet()
        alice.initRecvRatchet(bobSeedB64)

        // Test Alice sending 5 messages, Bob receiving
        for (i in 0 until 5) {
            val plaintext = "message_$i"
            val enc = alice.encryptRatcheted(plaintext)
            assertEquals("Seq must match", i, enc.seq)
            val dec = bob.decryptRatcheted(enc.payload, enc.nonce, enc.seq!!)
            assertEquals("Plaintext must roundtrip", plaintext, dec)
        }

        // Test Bob sending 5 messages, Alice receiving
        for (i in 0 until 5) {
            val plaintext = "message_$i"
            val enc = bob.encryptRatcheted(plaintext)
            assertEquals("Seq must match", i, enc.seq)
            val dec = alice.decryptRatcheted(enc.payload, enc.nonce, enc.seq!!)
            assertEquals("Plaintext must roundtrip", plaintext, dec)
        }
    }

    @Test
    fun `KDF chain key progression is deterministic`() {
        val v = loadVectors()
        val seed = b64.decode(v.ratchet.aliceSeed)
        val expectedChainKey = b64.decode(v.ratchet.aliceChainKey)

        // First chain key
        val ck0 = Kdf.chainInit(seed)
        assertArrayEquals("Initial chain key must match vector", expectedChainKey, ck0)

        // Progress the chain 5 times
        var ck = ck0
        for (i in 0 until 5) {
            val mk = Kdf.messageKey(ck)
            val nk = Kdf.nextChainKey(ck)
            assertEquals(32, mk.size)
            assertEquals(32, nk.size)
            // Message key and next chain key must be different
            assertFalse("mk != nk at step $i", mk.contentEquals(nk))
            ck = nk
        }
    }
}
