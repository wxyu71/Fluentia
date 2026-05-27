package com.fluentia.app.crypto

import com.goterl.lazysodium.interfaces.Box
import com.goterl.lazysodium.interfaces.Hash
import com.goterl.lazysodium.interfaces.SecretBox

interface SodiumProvider {
    fun cryptoBoxKeypair(): Pair<ByteArray, ByteArray> // (publicKey, secretKey)
    fun cryptoBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean
    fun cryptoBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean
    fun cryptoSecretBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, key: ByteArray): Boolean
    fun cryptoSecretBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, key: ByteArray): Boolean
    fun cryptoHashSha512(out: ByteArray, input: ByteArray)
    fun cryptoScalarMult(shared: ByteArray, secretKey: ByteArray, publicKey: ByteArray): Boolean
    fun randomBytes(size: Int): ByteArray

    companion object {
        const val BOX_PUBLIC_KEY_BYTES = Box.PUBLICKEYBYTES
        const val BOX_SECRET_KEY_BYTES = Box.SECRETKEYBYTES
        const val BOX_NONCE_BYTES = Box.NONCEBYTES
        const val BOX_MAC_BYTES = Box.MACBYTES
        const val SECRET_BOX_NONCE_BYTES = SecretBox.NONCEBYTES
        const val SECRET_BOX_MAC_BYTES = SecretBox.MACBYTES
        const val SHA512_BYTES = Hash.SHA512_BYTES
        const val SCALAR_MULT_BYTES = 32
    }
}
