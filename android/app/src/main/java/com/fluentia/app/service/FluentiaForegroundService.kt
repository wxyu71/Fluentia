package com.fluentia.app.service

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.fluentia.app.FluentiaApp
import com.fluentia.app.MainActivity
import com.fluentia.app.R
import com.fluentia.app.connection.ConnectionManager
import com.fluentia.app.connection.ConnectionState
import com.fluentia.app.data.model.ConnectionInfo
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import javax.inject.Inject

@AndroidEntryPoint
class FluentiaForegroundService : Service() {

    @Inject lateinit var connectionManager: ConnectionManager

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> {
                val infoJson = intent.getStringExtra(EXTRA_CONNECTION_INFO_JSON)
                val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID) ?: ""
                if (infoJson != null) {
                    val info = Json.decodeFromString<ConnectionInfo>(infoJson)
                    startForegroundWithNotification("连接中...")
                    connectionManager.connect(info, deviceId)
                    observeConnectionState()
                }
            }
            ACTION_DISCONNECT -> {
                connectionManager.disconnect()
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
        return START_STICKY
    }

    private fun observeConnectionState() {
        scope.launch {
            connectionManager.state.collect { state ->
                val text = when (state) {
                    ConnectionState.Disconnected -> "已断开"
                    ConnectionState.Connecting -> "连接中..."
                    ConnectionState.Connected -> "已连接到 PC"
                    ConnectionState.Preempted -> "已被其他设备替代"
                }
                updateNotification(text)
            }
        }
    }

    private fun startForegroundWithNotification(text: String) {
        val notification = buildNotification(text)
        startForeground(
            NOTIFICATION_ID,
            notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(android.app.NotificationManager::class.java)
        nm.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val disconnectIntent = PendingIntent.getService(
            this, 1,
            Intent(this, FluentiaForegroundService::class.java).apply { action = ACTION_DISCONNECT },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        return NotificationCompat.Builder(this, FluentiaApp.CHANNEL_ID)
            .setContentTitle("Fluentia")
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(openIntent)
            .addAction(R.drawable.ic_notification, "断开", disconnectIntent)
            .setOngoing(true)
            .setSilent(true)
            .build()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val NOTIFICATION_ID = 1001
        const val ACTION_CONNECT = "com.fluentia.app.CONNECT"
        const val ACTION_DISCONNECT = "com.fluentia.app.DISCONNECT"
        const val EXTRA_CONNECTION_INFO_JSON = "connection_info_json"
        const val EXTRA_DEVICE_ID = "device_id"

        fun start(context: Context, info: ConnectionInfo, deviceId: String) {
            val intent = Intent(context, FluentiaForegroundService::class.java).apply {
                action = ACTION_CONNECT
                putExtra(EXTRA_CONNECTION_INFO_JSON, Json.encodeToString(info))
                putExtra(EXTRA_DEVICE_ID, deviceId)
            }
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            val intent = Intent(context, FluentiaForegroundService::class.java).apply {
                action = ACTION_DISCONNECT
            }
            context.startService(intent)
        }
    }
}
