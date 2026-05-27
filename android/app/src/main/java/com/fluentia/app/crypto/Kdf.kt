package com.fluentia.app.crypto

import java.security.MessageDigest

/**
 * KDF: SHA-512(key || label)[0:32]
 * Pure Java implementation — no platform dependencies.
 * Must match the TypeScript (tweetnacl) and C# (Sodium.Core) implementations exactly.
 */
object Kdf {

    private val sha512 = MessageDigest.getInstance("SHA-512")

    @Synchronized
    fun derive(key: ByteArray, label: String): ByteArray {
        val labelBytes = label.toByteArray(Charsets.UTF_8)
        val input = key + labelBytes
        val hash = sha512.digest(input)
        return hash.copyOf(32)
    }

    fun chainInit(seed: ByteArray): ByteArray = derive(seed, "fluentia_chain_v1")
    fun messageKey(chainKey: ByteArray): ByteArray = derive(chainKey, "msg")
    fun nextChainKey(chainKey: ByteArray): ByteArray = derive(chainKey, "chain")
}
