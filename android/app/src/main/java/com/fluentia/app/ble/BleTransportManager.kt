package com.fluentia.app.ble

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.os.Build
import android.os.PowerManager
import com.fluentia.app.data.model.BleConstants
import com.fluentia.app.transport.ReadyState
import com.fluentia.app.transport.TransportConnection
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.util.UUID

data class BleEnvelope(
    val type: String,
    val publicKey: String? = null,
    val token: String? = null,
    val serverUrl: String? = null,
    val payload: String? = null,
    val nonce: String? = null,
    val seq: Int? = null,
    val code: String? = null,
    val approved: Boolean? = null,
    val version: String? = null,
)

class BleTransportManager(
    private val context: Context,
) : TransportConnection {

    private val json = Json { encodeDefaults = false; ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private var gatt: BluetoothGatt? = null
    private var notifyCharacteristic: BluetoothGattCharacteristic? = null
    private var writeCharacteristic: BluetoothGattCharacteristic? = null

    private var _readyState = ReadyState.CLOSED
    override val readyState: Int get() = _readyState

    override var onOpen: (() -> Unit)? = null
    override var onMessage: ((String) -> Unit)? = null
    override var onClose: (() -> Unit)? = null
    override var onError: (() -> Unit)? = null

    var onRssiUpdate: ((Int) -> Unit)? = null
    var onSuccess: (() -> Unit)? = null
    var onFailure: (() -> Unit)? = null

    private var consecutiveFailures = 0
    private var pollingJob: kotlinx.coroutines.Job? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private val bluetoothAdapter by lazy {
        val mgr = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
        mgr.adapter
    }

    @SuppressLint("MissingPermission")
    fun scanAndConnect(onDeviceFound: (BluetoothDevice) -> Unit) {
        val scanner = bluetoothAdapter?.bluetoothLeScanner ?: return
        _readyState = ReadyState.CONNECTING

        val filter = ScanFilter.Builder()
            .setServiceUuid(android.os.ParcelUuid(BleConstants.SERVICE_UUID))
            .build()

        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .build()

        val callback = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, result: ScanResult) {
                scanner.stopScan(this)
                onDeviceFound(result.device)
                connectToDevice(result.device)
            }

            override fun onScanFailed(errorCode: Int) {
                _readyState = ReadyState.CLOSED
                onFailure?.invoke()
            }
        }

        scanner.startScan(listOf(filter), settings, callback)
    }

    @SuppressLint("MissingPermission")
    fun connectToDevice(device: BluetoothDevice) {
        acquireWakeLock()
        gatt = device.connectGatt(context, true, gattCallback, BluetoothDevice.TRANSPORT_LE)
    }

    private val gattCallback = object : BluetoothGattCallback() {
        @SuppressLint("MissingPermission")
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    gatt.discoverServices()
                }
                BluetoothProfile.STATE_DISCONNECTED -> {
                    _readyState = ReadyState.CLOSED
                    stopPolling()
                    releaseWakeLock()
                    // Don't close gatt — let autoConnect handle reconnection
                    if (status != BluetoothGatt.GATT_SUCCESS) {
                        onFailure?.invoke()
                        consecutiveFailures++
                    }
                    onClose?.invoke()
                }
            }
        }

        @SuppressLint("MissingPermission")
        override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
            if (status != BluetoothGatt.GATT_SUCCESS) return

            val service = gatt.getService(BleConstants.SERVICE_UUID) ?: return
            notifyCharacteristic = service.getCharacteristic(BleConstants.NOTIFY_CHARACTERISTIC_UUID)
            writeCharacteristic = service.getCharacteristic(BleConstants.WRITE_CHARACTERISTIC_UUID)

            if (notifyCharacteristic != null && writeCharacteristic != null) {
                gatt.setCharacteristicNotification(notifyCharacteristic, true)
                // Enable notifications via CCCD descriptor
                val descriptor = notifyCharacteristic!!.getDescriptor(
                    UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
                )
                if (descriptor != null) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        gatt.writeDescriptor(descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
                    } else {
                        @Suppress("DEPRECATION")
                        descriptor.value = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
                        @Suppress("DEPRECATION")
                        gatt.writeDescriptor(descriptor)
                    }
                }
                _readyState = ReadyState.OPEN
                consecutiveFailures = 0
                onSuccess?.invoke()
                onOpen?.invoke()
                startPolling()
            }
        }

        override fun onCharacteristicChanged(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic, value: ByteArray) {
            if (characteristic.uuid == BleConstants.NOTIFY_CHARACTERISTIC_UUID) {
                val text = String(value, Charsets.UTF_8)
                onMessage?.invoke(text)
            }
        }

        @Suppress("DEPRECATION")
        override fun onCharacteristicChanged(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic) {
            if (characteristic.uuid == BleConstants.NOTIFY_CHARACTERISTIC_UUID) {
                @Suppress("DEPRECATION")
                val text = String(characteristic.value, Charsets.UTF_8)
                onMessage?.invoke(text)
            }
        }

        override fun onReadRemoteRssi(gatt: BluetoothGatt, rssi: Int, status: Int) {
            if (status == BluetoothGatt.GATT_SUCCESS) {
                onRssiUpdate?.invoke(rssi)
            }
        }
    }

    @SuppressLint("MissingPermission")
    override suspend fun send(data: String): Boolean {
        val char = writeCharacteristic ?: return false
        val g = gatt ?: return false
        val bytes = data.toByteArray(Charsets.UTF_8)

        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            g.writeCharacteristic(char, bytes, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) == android.bluetooth.BluetoothStatusCodes.SUCCESS
        } else {
            @Suppress("DEPRECATION")
            char.value = bytes
            @Suppress("DEPRECATION")
            char.writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
            @Suppress("DEPRECATION")
            g.writeCharacteristic(char)
        }
    }

    fun sendEnvelope(envelope: BleEnvelope) {
        scope.launch {
            val jsonStr = json.encodeToString(envelope)
            val success = send(jsonStr)
            if (success) {
                onSuccess?.invoke()
            } else {
                onFailure?.invoke()
                consecutiveFailures++
                if (consecutiveFailures >= 3) {
                    close()
                }
            }
        }
    }

    private fun startPolling() {
        stopPolling()
        pollingJob = scope.launch {
            while (_readyState == ReadyState.OPEN) {
                delay(500)
                sendEnvelope(BleEnvelope(type = "poll"))
            }
        }
    }

    private fun stopPolling() {
        pollingJob?.cancel()
        pollingJob = null
    }

    @SuppressLint("MissingPermission")
    override fun close(code: Int, reason: String) {
        stopPolling()
        releaseWakeLock()
        _readyState = ReadyState.CLOSING
        gatt?.close()
        gatt = null
        _readyState = ReadyState.CLOSED
    }

    @SuppressLint("MissingPermission")
    fun readRssi() {
        gatt?.readRemoteRssi()
    }

    private fun acquireWakeLock() {
        val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "fluentia:ble_transport").apply {
            acquire(30 * 60 * 1000L) // 30 min timeout
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
    }
}
