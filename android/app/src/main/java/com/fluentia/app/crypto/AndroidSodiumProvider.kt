package com.fluentia.app.crypto

import com.goterl.lazysodium.LazySodiumAndroid
import com.goterl.lazysodium.SodiumAndroid
import com.goterl.lazysodium.interfaces.Box
import com.goterl.lazysodium.interfaces.DiffieHellman
import com.goterl.lazysodium.interfaces.SecretBox

class AndroidSodiumProvider : SodiumProvider {

    private val sodium = LazySodiumAndroid(SodiumAndroid())

    override fun cryptoBoxKeypair(): Pair<ByteArray, ByteArray> {
        val pk = ByteArray(SodiumProvider.BOX_PUBLIC_KEY_BYTES)
        val sk = ByteArray(SodiumProvider.BOX_SECRET_KEY_BYTES)
        sodium.cryptoBoxKeypair(pk, sk)
        return pk to sk
    }

    override fun cryptoBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean {
        return sodium.cryptoBoxEasy(cipher, message, message.size.toLong(), nonce, publicKey, secretKey)
    }

    override fun cryptoBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, publicKey: ByteArray, secretKey: ByteArray): Boolean {
        return sodium.cryptoBoxOpenEasy(message, cipher, cipher.size.toLong(), nonce, publicKey, secretKey)
    }

    override fun cryptoSecretBoxEasy(cipher: ByteArray, message: ByteArray, nonce: ByteArray, key: ByteArray): Boolean {
        return sodium.cryptoSecretBoxEasy(cipher, message, message.size.toLong(), nonce, key)
    }

    override fun cryptoSecretBoxOpenEasy(message: ByteArray, cipher: ByteArray, nonce: ByteArray, key: ByteArray): Boolean {
        return sodium.cryptoSecretBoxOpenEasy(message, cipher, cipher.size.toLong(), nonce, key)
    }

    override fun cryptoHashSha512(out: ByteArray, input: ByteArray) {
        sodium.cryptoHashSha512(out, input, input.size.toLong())
    }

    override fun randomBytes(size: Int): ByteArray {
        return sodium.randomBytesBuf(size)
    }

    override fun cryptoScalarMult(shared: ByteArray, secretKey: ByteArray, publicKey: ByteArray): Boolean {
        return sodium.cryptoScalarMult(shared, secretKey, publicKey)
    }
}
