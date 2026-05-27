package com.fluentia.app.transport

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

enum class TransportKind { BLE, WS }
enum class MessageType { INPUT, FILE, HANDSHAKE, CONTROL }

data class TransportScore(val ble: Int, val ws: Int)

class TransportHealthMonitor {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private var bleScore = 50
    private var wsScore = 50
    private var bleConsecutiveFailures = 0
    private var bleRssi = -50
    private var wsRtt = 100
    private var wsConnected = false
    private var batteryLevel = 1.0f
    private var batteryCharging = true

    private var bleDown = false
    private var wsDown = false
    private var bleDownSince = 0L
    private var wsDownSince = 0L

    companion object {
        private const val BLE_FAILURE_THRESHOLD = 3
        private const val BATTERY_LOW_THRESHOLD = 0.20f
        private const val BATTERY_CRITICAL_THRESHOLD = 0.10f
        private const val RECOVERY_INTERVAL_MS = 30000L
    }

    init {
        startRecoveryTimer()
    }

    fun onBleSuccess() {
        bleConsecutiveFailures = 0
        bleDown = false
        bleDownSince = 0
        recalculateBleScore()
    }

    fun onBleFailure() {
        bleConsecutiveFailures++
        if (bleConsecutiveFailures >= BLE_FAILURE_THRESHOLD) {
            bleDown = true
            bleDownSince = System.currentTimeMillis()
            bleScore = 0
        } else {
            recalculateBleScore()
        }
    }

    fun updateBleRssi(rssi: Int) {
        bleRssi = rssi
        recalculateBleScore()
    }

    fun updateWsRtt(rttMs: Int) {
        wsRtt = rttMs
        wsConnected = true
        wsDown = false
        recalculateWsScore()
    }

    fun setWsConnected(connected: Boolean) {
        wsConnected = connected
        if (!connected) {
            wsDown = true
            wsDownSince = System.currentTimeMillis()
            wsScore = 0
        } else {
            wsDown = false
            recalculateWsScore()
        }
    }

    fun updateBattery(level: Float, charging: Boolean) {
        batteryLevel = level.coerceIn(0f, 1f)
        batteryCharging = charging
        recalculateBleScore()
        recalculateWsScore()
    }

    fun getScores(): TransportScore = TransportScore(ble = bleScore, ws = wsScore)

    fun isTransportAvailable(kind: TransportKind): Boolean = when (kind) {
        TransportKind.BLE -> !bleDown && bleScore > 0
        TransportKind.WS -> !wsDown && wsScore > 0
    }

    fun selectTransport(messageType: MessageType): TransportKind? {
        val bleAvail = isTransportAvailable(TransportKind.BLE)
        val wsAvail = isTransportAvailable(TransportKind.WS)

        if (!bleAvail && !wsAvail) return null
        if (!bleAvail) return TransportKind.WS
        if (!wsAvail) return TransportKind.BLE

        if (batteryLevel < BATTERY_CRITICAL_THRESHOLD && !batteryCharging) {
            return TransportKind.BLE
        }

        return when (messageType) {
            MessageType.INPUT -> if (bleScore >= 40) TransportKind.BLE else TransportKind.WS
            MessageType.FILE -> if (wsScore >= 40) TransportKind.WS else TransportKind.BLE
            MessageType.HANDSHAKE -> if (bleScore > wsScore) TransportKind.BLE else TransportKind.WS
            MessageType.CONTROL -> if (bleScore > wsScore) TransportKind.BLE else TransportKind.WS
        }
    }

    fun isConcurrentAllowed(): Boolean {
        if (batteryCharging) return bleScore >= 70 && wsScore >= 70
        if (batteryLevel < BATTERY_LOW_THRESHOLD) return false
        return bleScore >= 70 && wsScore >= 70
    }

    fun markDown(kind: TransportKind) {
        when (kind) {
            TransportKind.BLE -> {
                bleDown = true
                bleDownSince = System.currentTimeMillis()
                bleScore = 0
            }
            TransportKind.WS -> {
                wsDown = true
                wsDownSince = System.currentTimeMillis()
                wsScore = 0
            }
        }
    }

    private fun recalculateBleScore() {
        if (bleDown) { bleScore = 0; return }
        var score = 50
        when {
            bleRssi >= -50 -> score += 20
            bleRssi >= -65 -> score += 10
            bleRssi >= -80 -> score -= 10
            else -> score -= 30
        }
        score -= bleConsecutiveFailures * 15
        if (batteryLevel < BATTERY_CRITICAL_THRESHOLD && !batteryCharging) score -= 10
        bleScore = score.coerceIn(0, 100)
    }

    private fun recalculateWsScore() {
        if (wsDown || !wsConnected) { wsScore = 0; return }
        var score = 60
        when {
            wsRtt < 100 -> score += 20
            wsRtt < 300 -> score += 10
            wsRtt < 1000 -> score += 0
            else -> score -= 20
        }
        if (!batteryCharging) {
            if (batteryLevel < BATTERY_CRITICAL_THRESHOLD) score -= 40
            else if (batteryLevel < BATTERY_LOW_THRESHOLD) score -= 15
        }
        wsScore = score.coerceIn(0, 100)
    }

    private fun startRecoveryTimer() {
        scope.launch {
            while (true) {
                delay(RECOVERY_INTERVAL_MS)
                val now = System.currentTimeMillis()
                if (bleDown && now - bleDownSince > RECOVERY_INTERVAL_MS) {
                    bleDown = false
                    bleConsecutiveFailures = 0
                    recalculateBleScore()
                }
                if (wsDown && now - wsDownSince > RECOVERY_INTERVAL_MS) {
                    wsDown = false
                    recalculateWsScore()
                }
            }
        }
    }
}
