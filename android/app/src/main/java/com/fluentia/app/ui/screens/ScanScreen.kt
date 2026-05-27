package com.fluentia.app.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.fluentia.app.data.model.ConnectionInfo
import com.fluentia.app.ui.components.QrCodeAnalyzer
import kotlinx.serialization.json.Json

@Composable
fun ScanScreen(
    onConnected: (ConnectionInfo) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var hasCameraPermission by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        hasCameraPermission = granted
    }

    var manualCode by remember { mutableStateOf("") }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        if (!hasCameraPermission) {
            launcher.launch(Manifest.permission.CAMERA)
        }
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text("扫描 PC 端二维码", style = MaterialTheme.typography.headlineSmall)
        Spacer(modifier = Modifier.height(16.dp))

        if (hasCameraPermission) {
            val analyzer = remember {
                QrCodeAnalyzer { raw ->
                    val info = parseQrCode(raw)
                    if (info != null) {
                        onConnected(info)
                    } else {
                        errorMessage = "无效的二维码格式"
                    }
                }
            }

            AndroidView(
                factory = { ctx ->
                    val previewView = PreviewView(ctx)
                    val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)
                    cameraProviderFuture.addListener({
                        val cameraProvider = cameraProviderFuture.get()
                        val preview = androidx.camera.core.Preview.Builder().build().also {
                            it.surfaceProvider = previewView.surfaceProvider
                        }
                        val imageAnalysis = ImageAnalysis.Builder()
                            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                            .build()
                            .also { it.setAnalyzer(ContextCompat.getMainExecutor(ctx), analyzer) }

                        cameraProvider.unbindAll()
                        cameraProvider.bindToLifecycle(
                            lifecycleOwner,
                            CameraSelector.DEFAULT_BACK_CAMERA,
                            preview,
                            imageAnalysis,
                        )
                    }, ContextCompat.getMainExecutor(ctx))
                    previewView
                },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(300.dp),
            )
        } else {
            Text("需要相机权限才能扫描二维码", color = MaterialTheme.colorScheme.error)
        }

        Spacer(modifier = Modifier.height(24.dp))
        Text("或手动输入设备码", style = MaterialTheme.typography.bodyLarge)
        Spacer(modifier = Modifier.height(8.dp))

        OutlinedTextField(
            value = manualCode,
            onValueChange = { manualCode = it.uppercase() },
            label = { Text("8位设备码") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(modifier = Modifier.height(8.dp))

        Button(
            onClick = {
                if (manualCode.length == 8) {
                    // Device code flow — connect via server with code
                    // For Phase 1, show placeholder
                    errorMessage = "设备码连接暂未实现，请使用二维码"
                }
            },
            enabled = manualCode.length == 8,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text("连接")
        }

        if (errorMessage != null) {
            Spacer(modifier = Modifier.height(8.dp))
            Text(errorMessage!!, color = MaterialTheme.colorScheme.error)
        }
    }
}

private val json = Json { ignoreUnknownKeys = true }

private fun parseQrCode(raw: String): ConnectionInfo? {
    // Compact format: F1|token|key or F1|token|key|serverUrl
    if (raw.startsWith("F1|")) {
        val parts = raw.split("|")
        if (parts.size >= 3) {
            val serverUrl = if (parts.size >= 4) parts[3] else "wss://fluentia.app/ws"
            return ConnectionInfo(s = serverUrl, t = parts[1], k = parts[2])
        }
    }

    // JSON format: {"s":"wss://...","t":"token","k":"base64key"}
    return try {
        json.decodeFromString<ConnectionInfo>(raw)
    } catch (_: Exception) {
        null
    }
}
