package com.fluentia.app.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.fluentia.app.ble.BlePairingManager
import com.fluentia.app.ble.BlePairingState

@Composable
fun BlePairingScreen(
    pairingManager: BlePairingManager,
    onConnected: () -> Unit,
    onCancel: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by pairingManager.state.collectAsState()
    val verificationCode by pairingManager.verificationCode.collectAsState()

    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text("BLE 配对", style = MaterialTheme.typography.headlineMedium)
        Spacer(modifier = Modifier.height(24.dp))

        when (state) {
            BlePairingState.Idle -> {
                Text("点击开始扫描附近的 PC")
                Spacer(modifier = Modifier.height(16.dp))
                Button(onClick = { pairingManager.startPairing() }) {
                    Text("开始扫描")
                }
            }
            BlePairingState.Scanning -> {
                CircularProgressIndicator(modifier = Modifier.size(48.dp))
                Spacer(modifier = Modifier.height(16.dp))
                Text("正在扫描...")
            }
            BlePairingState.Connecting -> {
                CircularProgressIndicator(modifier = Modifier.size(48.dp))
                Spacer(modifier = Modifier.height(16.dp))
                Text("正在连接...")
            }
            BlePairingState.WaitingVerification -> {
                Text("请确认两端显示的验证码一致：", textAlign = TextAlign.Center)
                Spacer(modifier = Modifier.height(16.dp))
                Text(
                    text = verificationCode ?: "------",
                    fontSize = 48.sp,
                    fontFamily = FontFamily.Monospace,
                    color = MaterialTheme.colorScheme.primary,
                )
                Spacer(modifier = Modifier.height(24.dp))
                Button(onClick = { pairingManager.approveBleAuth() }) {
                    Text("确认验证码")
                }
            }
            BlePairingState.WaitingApproval -> {
                CircularProgressIndicator(modifier = Modifier.size(48.dp))
                Spacer(modifier = Modifier.height(16.dp))
                Text("等待 PC 端确认...")
            }
            BlePairingState.Connected -> {
                Text("BLE 配对成功！", color = MaterialTheme.colorScheme.primary)
                Spacer(modifier = Modifier.height(16.dp))
                Button(onClick = onConnected) {
                    Text("继续")
                }
            }
            BlePairingState.Failed -> {
                Text("配对失败", color = MaterialTheme.colorScheme.error)
                Spacer(modifier = Modifier.height(16.dp))
                Button(onClick = { pairingManager.startPairing() }) {
                    Text("重试")
                }
            }
        }

        Spacer(modifier = Modifier.height(16.dp))
        OutlinedButton(onClick = {
            pairingManager.stopPairing()
            onCancel()
        }) {
            Text("取消")
        }
    }
}
