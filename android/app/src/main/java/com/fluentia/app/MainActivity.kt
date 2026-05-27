package com.fluentia.app

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.fluentia.app.connection.ConnectionManager
import com.fluentia.app.data.model.ConnectionInfo
import com.fluentia.app.service.FluentiaForegroundService
import com.fluentia.app.ui.screens.InputScreen
import com.fluentia.app.ui.screens.ScanScreen
import com.fluentia.app.ui.theme.FluentiaTheme
import dagger.hilt.android.AndroidEntryPoint
import java.util.UUID
import javax.inject.Inject

@AndroidEntryPoint
class MainActivity : ComponentActivity() {

    @Inject lateinit var connectionManager: ConnectionManager

    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { _ -> }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        enableEdgeToEdge()

        // Request notification permission for foreground service
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        val deviceId = getOrCreateDeviceId()

        setContent {
            FluentiaTheme {
                var connectedInfo by remember { mutableStateOf<ConnectionInfo?>(null) }

                if (connectedInfo == null) {
                    ScanScreen(
                        onConnected = { info ->
                            connectedInfo = info
                            FluentiaForegroundService.start(this@MainActivity, info, deviceId)
                        },
                    )
                } else {
                    InputScreen(
                        connectionManager = connectionManager,
                        onNavigateToScan = {
                            connectedInfo = null
                        },
                    )
                }
            }
        }
    }

    private fun getOrCreateDeviceId(): String {
        val prefs = getSharedPreferences("fluentia", MODE_PRIVATE)
        var id = prefs.getString("device_id", null)
        if (id == null) {
            id = UUID.randomUUID().toString().replace("-", "")
            prefs.edit().putString("device_id", id).apply()
        }
        return id
    }
}
