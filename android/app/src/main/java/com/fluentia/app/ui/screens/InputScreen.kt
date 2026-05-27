package com.fluentia.app.ui.screens

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Backspace
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import com.fluentia.app.connection.ConnectionManager
import com.fluentia.app.connection.ConnectionState
import com.fluentia.app.data.constants.AppConstants
import com.fluentia.app.data.model.CommandType
import com.fluentia.app.data.model.InputCommand
import com.fluentia.app.diff.DiffEngine
import com.fluentia.app.ui.components.ConnectionStatusBar
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch

@OptIn(FlowPreview::class)
@Composable
fun InputScreen(
    connectionManager: ConnectionManager,
    onNavigateToScan: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val connectionState by connectionManager.state.collectAsState()
    val encryptionReady by connectionManager.encryptionReady.collectAsState()
    val scope = rememberCoroutineScope()

    var currentText by remember { mutableStateOf("") }
    var lastSentText by remember { mutableStateOf("") }

    // Debounced diff sending
    LaunchedEffect(connectionManager) {
        snapshotFlow { currentText }
            .drop(1)
            .distinctUntilChanged()
            .debounce(AppConstants.DIFF_DEBOUNCE_MS)
            .collect { newText ->
                if (connectionState == ConnectionState.Connected && newText != lastSentText) {
                    val diff = DiffEngine.computeDiff(lastSentText, newText)
                    if (diff.backspace > 0 || diff.insert.isNotEmpty()) {
                        connectionManager.sendEncrypted(
                            InputCommand(type = CommandType.DIFF, count = diff.backspace, text = diff.insert)
                        )
                    }
                    lastSentText = newText
                }
            }
    }

    // Reset text when disconnected
    LaunchedEffect(connectionState) {
        if (connectionState == ConnectionState.Disconnected || connectionState == ConnectionState.Preempted) {
            lastSentText = ""
        }
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .imePadding(),
    ) {
        ConnectionStatusBar(
            connectionState = connectionState,
            encryptionReady = encryptionReady,
        )

        if (connectionState == ConnectionState.Disconnected || connectionState == ConnectionState.Preempted) {
            onNavigateToScan()
            return
        }

        Spacer(modifier = Modifier.height(16.dp))

        OutlinedTextField(
            value = currentText,
            onValueChange = { currentText = it },
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp)
                .weight(1f),
            placeholder = { Text("输入文本，按 Enter 发送到 PC...") },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(
                onSend = {
                    if (currentText.isNotEmpty()) {
                        if (currentText != lastSentText) {
                            val diff = DiffEngine.computeDiff(lastSentText, currentText)
                            if (diff.backspace > 0 || diff.insert.isNotEmpty()) {
                                scope.launch {
                                    connectionManager.sendEncrypted(
                                        InputCommand(type = CommandType.DIFF, count = diff.backspace, text = diff.insert)
                                    )
                                }
                            }
                            lastSentText = currentText
                        }
                        scope.launch {
                            connectionManager.sendEncrypted(InputCommand(type = CommandType.ENTER))
                        }
                        currentText = ""
                        lastSentText = ""
                    }
                },
            ),
            trailingIcon = {
                IconButton(onClick = {
                    if (currentText.isNotEmpty()) {
                        currentText = currentText.dropLast(1)
                        scope.launch {
                            connectionManager.sendEncrypted(InputCommand(type = CommandType.BACKSPACE, count = 1))
                        }
                    }
                }) {
                    Icon(Icons.AutoMirrored.Filled.Backspace, contentDescription = "退格")
                }
            },
        )
    }
}
