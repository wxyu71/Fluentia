package com.fluentia.app.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.fluentia.app.connection.ConnectionState

@Composable
fun ConnectionStatusBar(
    connectionState: ConnectionState,
    encryptionReady: Boolean,
    modifier: Modifier = Modifier,
) {
    val (text, color) = when (connectionState) {
        ConnectionState.Disconnected -> "未连接" to Color.Gray
        ConnectionState.Connecting -> "连接中..." to Color(0xFFFFA726)
        ConnectionState.Connected -> if (encryptionReady) "已加密连接" to Color(0xFF66BB6A) else "已连接" to Color(0xFF42A5F5)
        ConnectionState.Preempted -> "已被其他设备替代" to Color(0xFFEF5350)
    }

    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(8.dp)
                .clip(CircleShape)
                .background(color),
        )
        Spacer(modifier = Modifier.width(8.dp))
        Text(
            text = text,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
