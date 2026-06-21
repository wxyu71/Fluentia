using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Velopack;
using Velopack.Sources;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fluentia.Models;
using Fluentia.Services;
using Fluentia.Views;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Fluentia;

public partial class MainWindow : Window
{
    private sealed class IncomingFileTransferBuffer
    {
        public required InputCommand Header { get; init; }
        public List<byte[]> Chunks { get; } = new();
        public long BytesReceived { get; set; }
    }

    private sealed class TransferProgressFile
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }
        public string Status { get; set; } = "queued";
    }

    private sealed class TransferProgressBatch
    {
        public required string Direction { get; init; }
        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
        public bool Expanded { get; set; }
        public List<TransferProgressFile> Files { get; } = new();
    }

    private readonly RoomManager _roomManager;
    private readonly DesktopBlePairingService _desktopBlePairingService;
    private readonly DesktopSettingsStore _settingsStore;
    private readonly WindowActivationCoordinator _windowActivationCoordinator;
    private readonly Channel<InputCommand> _cmdChannel =
        Channel.CreateUnbounded<InputCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, IncomingFileTransferBuffer> _fileTransfers = new();

    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private MenuItem? _trayShowItem;
    private MenuItem? _trayRefreshItem;
    private MenuItem? _trayExitItem;
    private DispatcherTimer? _sessionTimer;
    private DispatcherTimer? _disconnectTimer;
    private DispatcherTimer? _receivedFilesRevealTimer;
    private DispatcherTimer? _transferProgressHideTimer;
    private DispatcherTimer? _trayCreationRetryTimer;

    private DateTime _sessionCreatedAt;
    private DateTime _sessionExpiresAt;
    private int _devTapCount;
    private DateTime _lastDevTap = DateTime.MinValue;
    private bool _devMode;
    private bool _mobileConnected;
    private bool _handshakePending;
    private bool _serverConnected;
    private bool _settingsOpen;
    private bool _closeToTray = true;
    private bool _launchAtStartup;
    private bool _networkAvailable = true;
    private bool _isShuttingDown;
    private bool _isApplyingLanguageSelection;
    private bool _qrVisible = true;
    private Window? _qrPreviewWindow;
    private bool _outgoingTransferPaused;
    private bool _outgoingTransferCancelRequested;
    private string? _deviceCode;
    private string? _activeOutgoingTransferId;
    private string? _activeOutgoingUiFileId;
    private int _trayCreationRetriesRemaining;
    private string _fileSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private CancellationTokenSource? _inputTargetRecoveryCts;
    private CancellationTokenSource? _commandQueueCts;
    private CancellationTokenSource? _focusClearCts;
    private IntPtr _inputTargetWindow;
    private IntPtr _windowHandle;
    private TransferProgressBatch? _transferProgressBatch;
    private TaskCompletionSource<bool>? _outgoingTransferResumeTcs;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValue = "Fluentia";
    private static readonly string AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? MsgTypes.ProtocolVersion;
    private static readonly TimeSpan DiffBatchWindow = TimeSpan.FromMilliseconds(100);

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook;
    private IntPtr _lastForegroundWindow;
    private IntPtr _lastExternalForegroundWindow;
    private bool _pendingClearOnReconnect;
    private string _appliedInputBuffer = string.Empty;
    private bool _manualInputTargetRecoveryNotified;

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fluentia", "settings.json");
    private static readonly string SessionBackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fluentia", "protected-session.backup");
    private static readonly string TrayIconSourceFile = Path.Combine(AppContext.BaseDirectory, "fluentia-icon-source.png");
    private string _regexFilterMarkdown = string.Empty;
    private bool _persistedSessionLost;

    private static string L(string key, params object[] args) => LocalizationService.Get(key, args);

    private static string TruncateForLog(string? text, int maxLen = 40)
    {
        if (text == null) return "<null>";
        return text.Length <= maxLen ? text : text[..maxLen] + $"…({text.Length})";
    }

    public MainWindow()
    {
        InitializeComponent();

        _roomManager = new RoomManager();
        _desktopBlePairingService = new DesktopBlePairingService(
            () => _roomManager.GetBleSessionInfo(),
            msg => _roomManager.HandleBleEncryptedMessage(msg),
            message =>
            {
                if (_devMode)
                {
                    _ = Dispatcher.BeginInvoke(() => AppendLog(message));
                }
            });
        _settingsStore = new DesktopSettingsStore(SettingsFile, SessionBackupFile);
        _windowActivationCoordinator = new WindowActivationCoordinator(
            GetForegroundWindow,
            SetForegroundWindow,
            IsWindow,
            IsWindowVisible,
            message =>
            {
                if (_devMode)
                {
                    _ = Dispatcher.BeginInvoke(() => AppendLog(message));
                }
            });
        LoadSettings();
        ApplyLocalizedText();
        _windowHandle = new WindowInteropHelper(this).EnsureHandle();

        SetupRoomManagerEvents();
        SetupTrayIcon();
        SetupSessionTimer();
        UpdateNetworkAvailability();
        RefreshVisualState();

        Closing += MainWindow_Closing;
        Loaded += async (_, _) =>
        {
            await AutoConnectAsync();
            if (_persistedSessionLost)
            {
                ShowPersistedSessionLostPrompt();
            }
        };
        MouseEnter += (_, _) => ShowTrafficIcons(true);
        MouseLeave += (_, _) => ShowTrafficIcons(false);

        _commandQueueCts = new CancellationTokenSource();
        _ = Task.Run(() => ProcessCommandQueue(_commandQueueCts.Token));

        _lastForegroundWindow = GetForegroundWindow();
        RememberExternalForegroundWindow(_lastForegroundWindow);
        _winEventDelegate = OnForegroundWindowChanged;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        App.ThemeChanged += App_ThemeChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;

        // Check for updates in the background
        _ = AutoCheckForUpdatesAsync();
    }

    private void SetupRoomManagerEvents()
    {
        _roomManager.OnServerConnectionChanged += (connected) => Dispatcher.BeginInvoke(() =>
        {
            _serverConnected = connected;
            if (!connected)
            {
                _handshakePending = false;
                _inputTargetWindow = IntPtr.Zero;
                CancelOngoingTransfers();
            }

            RefreshStatusFromState();
            RefreshVisualState();
        });

        _roomManager.OnSessionCreated += (token) => Dispatcher.BeginInvoke(() =>
        {
            _persistedSessionLost = false;
            _deviceCode = null;
            _mobileConnected = false;
            _handshakePending = false;
            _qrVisible = true;
            _inputTargetWindow = IntPtr.Zero;
            _sessionCreatedAt = DateTime.Now;
            _sessionExpiresAt = ResolveSessionExpiry(_sessionCreatedAt);
            PersistSettings();
            UpdateQRCode();
            UpdateSessionCountdown();
            SetStatus(L("StatusWaitingPhone"), false);
            RefreshVisualState();
            _ = _roomManager.RequestDeviceCode();
        });

        _roomManager.OnDeviceCodeCreated += (code) => Dispatcher.BeginInvoke(() =>
        {
            if (_mobileConnected || _handshakePending || _roomManager.EncryptionReady)
            {
                return;
            }

            _deviceCode = code;
            if (_devMode) AppendLog($"Device code ready: {code}");
            RefreshVisualState();
        });

        _roomManager.OnDeviceCodePending += (code, verifyId, userAgent) => Dispatcher.BeginInvoke(() =>
        {
            RememberExternalForegroundWindow(GetForegroundWindow());
            Show();
            WindowState = WindowState.Normal;
            Activate();

            var dialog = new ConfirmConnectionDialog(verifyId, userAgent) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _ = _roomManager.ConfirmDeviceCode(code);
            }
            else
            {
                _ = _roomManager.RejectDeviceCode(code);
            }
        });

        _roomManager.OnMobileConnected += (deviceId) => Dispatcher.BeginInvoke(() =>
        {
            _mobileConnected = true;
            _handshakePending = true;
            _deviceCode = null;
            _disconnectTimer?.Stop();
            _inputTargetWindow = IntPtr.Zero;
            SetStatus(L("StatusPhoneDetected"), false);
            if (_devMode)
            {
                AppendLog($"Phone connected: {deviceId[..Math.Min(8, deviceId.Length)]}...");
            }
            RefreshVisualState();
        });

        _roomManager.OnMobileDisconnected += () => Dispatcher.BeginInvoke(() =>
        {
            _mobileConnected = false;
            _handshakePending = false;
            _inputTargetWindow = IntPtr.Zero;
            CancelOngoingTransfers();

            if (_roomManager.HasTrustedSession && !IsSessionExpired())
            {
                _deviceCode = null;
                _disconnectTimer?.Stop();
                SetStatus(CanUseConfiguredServer()
                    ? L("StatusPhoneDisconnectedWaiting")
                    : L("StatusPhoneDisconnectedOffline"), false);
            }
            else
            {
                SetStatus(L("StatusPhoneDisconnected"), false);

                if (_serverConnected && !IsSessionExpired())
                {
                    UpdateQRCode();
                    _ = _roomManager.RequestDeviceCode();
                }

                StartDisconnectReminderTimer();
            }

            RefreshVisualState();
        });

        _roomManager.OnEncryptionReady += () => Dispatcher.BeginInvoke(() =>
        {
            _mobileConnected = true;
            _handshakePending = false;
            _deviceCode = null;
            _inputTargetWindow = IntPtr.Zero;
            PersistSettings();
            ShowQrArea(false);
            CloseQrPreviewIfOpen();
            SetStatus(L("StatusEncrypted"), true);
            RefreshVisualState();
            Hide();
            _ = InitializeBlePairingAsync();
            BeginInputTargetRecovery();

            // Send any pending clear message that failed during reconnect
            if (_pendingClearOnReconnect)
            {
                _pendingClearOnReconnect = false;
                _appliedInputBuffer = string.Empty;
                DebugLogger.Log("RECONNECT: sending pending resync to mobile");
                _ = _roomManager.SendToMobileAsync(JsonSerializer.Serialize(new { type = "clear", reason = "resync" }));
            }
        });

        _roomManager.OnSessionRecovered += () => Dispatcher.BeginInvoke(() =>
        {
            _qrVisible = true;

            if (_sessionCreatedAt == default)
            {
                _sessionCreatedAt = DateTime.Now;
            }

            _sessionExpiresAt = ResolveSessionExpiry(_sessionCreatedAt);

            UpdateQRCode();
            UpdateSessionCountdown();
            RefreshStatusFromState();
            RefreshVisualState();

            if (!_roomManager.HasTrustedSession)
            {
                _ = _roomManager.RequestDeviceCode();
            }
        });

        _roomManager.OnInputCommand += (cmd) =>
        {
            if (cmd.Type == "ble_auth" && !string.IsNullOrWhiteSpace(cmd.PublicKey))
            {
                _desktopBlePairingService.AuthorizeRemotePublicKey(cmd.PublicKey);
                _ = _roomManager.SendToMobileAsync(new InputCommand
                {
                    Type = "ble_auth_ok",
                    PublicKey = cmd.PublicKey,
                }.Serialize());
                if (_devMode)
                {
                    _ = Dispatcher.BeginInvoke(() => AppendLog("BLE upgrade authorized from encrypted session"));
                }
                return;
            }

            DebugLogger.Log($"ENCRYPTED RX: type={cmd.Type}, count={cmd.Count}, text=\"{TruncateForLog(cmd.Text)}\"");
            _cmdChannel.Writer.TryWrite(cmd);
        };

        _roomManager.OnStatusChanged += (status) => Dispatcher.BeginInvoke(() =>
        {
            if (_devMode) AppendLog(status);
        });

        _roomManager.OnError += (error) => Dispatcher.BeginInvoke(() =>
        {
            _handshakePending = false;
            SetStatus(L("StatusErrorFormat", error), false);
            RefreshVisualState();
            if (_devMode) AppendLog($"Error: {error}");
        });

        _roomManager.OnVersionIncompatible += (error) => Dispatcher.BeginInvoke(async () =>
        {
            _mobileConnected = false;
            _handshakePending = false;
            _inputTargetWindow = IntPtr.Zero;
            SetStatus(error, false);
            RefreshVisualState();

            // Attempt auto-update when version is incompatible
            try
            {
                var mgr = GetUpdateManager(ServerUrlBox?.Text?.Trim());
                if (mgr.IsInstalled)
                {
                    SetStatus(L("StatusCheckingUpdate"), false);
                    var info = await mgr.CheckForUpdatesAsync();
                    if (info != null)
                    {
                        SetStatus(L("StatusUpdateAvailable"), false);
                        await mgr.DownloadUpdatesAsync(info);
                        var result = System.Windows.MessageBox.Show(
                            L("StatusUpdateReady"),
                            L("AppName"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes)
                        {
                            mgr.ApplyUpdatesAndRestart(info);
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Update check failed — fall through to show the error MessageBox
            }

            System.Windows.MessageBox.Show(error, L("VersionIncompatibleTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private async Task InitializeBlePairingAsync()
    {
        try
        {
            await _desktopBlePairingService.StartAsync();
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"BLE unavailable: {ex.Message}");
            }
        }
    }

    private void SetupSessionTimer()
    {
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _sessionTimer.Tick += SessionTimer_Tick;
        _sessionTimer.Start();
    }

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        UpdateSessionCountdown();

        if (_roomManager.CurrentToken != null && !_mobileConnected && IsSessionExpired() && _serverConnected)
        {
            _ = _roomManager.RefreshSession();
        }
    }

    private async Task AutoConnectAsync()
    {
        if (_serverConnected) return;
        await TryConnectAsync(autoConnect: true);
    }

    private async Task TryConnectAsync(bool autoConnect)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!CanUseConfiguredServer())
        {
            SetStatus(L("StatusNetworkDisconnected"), false);
            RefreshVisualState();
            return;
        }

        ConnectBtn.IsEnabled = false;
        ConnectBtn.Content = L("ButtonConnecting");

        try
        {
            await _roomManager.ConnectAsync(url);
            SetStatus(L("StatusConnectingServer"), false);
            PersistSettings();
        }
        catch (Exception ex)
        {
            SetStatus(autoConnect ? L("StatusAutoConnectFailed") : L("StatusConnectionFailed"), false);
            if (_devMode)
            {
                AppendLog($"Connection error: {ex.Message}");
            }
        }
        finally
        {
            RefreshVisualState();
        }
    }


    private async Task ProcessCommandQueue(CancellationToken cancellationToken)
    {
        InputCommand? deferredCommand = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var cmd = deferredCommand ?? await _cmdChannel.Reader.ReadAsync(cancellationToken);
                deferredCommand = null;

                if (cmd.Type == "diff")
                {
                    DebugLogger.Log($"DIFF in: backspace={cmd.Count}, insert=\"{TruncateForLog(cmd.Text)}\", buffer=\"{TruncateForLog(_appliedInputBuffer)}\"");
                    var nextText = ApplyDiffToBuffer(_appliedInputBuffer, cmd.Count, cmd.Text);

                    while (true)
                    {
                        var waitForMore = _cmdChannel.Reader.WaitToReadAsync().AsTask();
                        var completed = await Task.WhenAny(waitForMore, Task.Delay(DiffBatchWindow));
                        if (completed != waitForMore || !await waitForMore)
                        {
                            break;
                        }

                        while (_cmdChannel.Reader.TryRead(out var queuedCommand))
                        {
                            if (queuedCommand.Type == "diff")
                            {
                                nextText = ApplyDiffToBuffer(nextText, queuedCommand.Count, queuedCommand.Text);
                                continue;
                            }

                            // Non-diff command (enter, clipboard, etc.) encountered.
                            // Drain remaining queued diffs to reach the final state
                            // before deferring the command — prevents intermediate
                            // "delete all, retype all" flickering on Enter.
                            deferredCommand = queuedCommand;
                            while (_cmdChannel.Reader.TryRead(out var remaining))
                            {
                                if (remaining.Type == "diff")
                                {
                                    nextText = ApplyDiffToBuffer(nextText, remaining.Count, remaining.Text);
                                }
                                else if (deferredCommand == null)
                                {
                                    deferredCommand = remaining;
                                }
                            }
                            break;
                        }

                        if (deferredCommand != null)
                        {
                            break;
                        }
                    }

                    DebugLogger.Log($"DIFF batch done: nextText=\"{TruncateForLog(nextText)}\"");
                    FlushBufferedDiff(nextText);
                    continue;
                }

                DebugLogger.Log($"CMD: type={cmd.Type}" + (cmd.Type == "enter" ? "" : $", count={cmd.Count}, text=\"{TruncateForLog(cmd.Text)}\""));
                HandleInputCommand(cmd);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"DIFF LOOP: unhandled {ex.GetType().Name}: {ex.Message}");
                if (_devMode)
                {
                    _ = Dispatcher.BeginInvoke(() => AppendLog($"Injection error: {ex.Message}"));
                }
            }
        }
    }

    private static string ApplyDiffToBuffer(string current, int backspace, string? insertText)
    {
        var safeBackspace = Math.Max(0, Math.Min(backspace, current.Length));
        var prefixLength = current.Length - safeBackspace;
        return current[..prefixLength] + (insertText ?? string.Empty);
    }

    private void FlushBufferedDiff(string nextText)
    {
        try
        {
            if (!EnsureInputTarget())
            {
                // Diff dropped — reset PC state and tell mobile to resync.
                // Without resetting _appliedInputBuffer, the PC's baseline would
                // carry stale text from a previous window, causing the mobile's
                // resync diff to be applied on top of garbage (first-char swallow).
                DebugLogger.Log($"FLUSH: target FAILED, dropping diff. Resetting buffer and sending resync to mobile.");
                _appliedInputBuffer = string.Empty;
                _ = _roomManager.SendToMobileAsync(JsonSerializer.Serialize(new { type = "clear", reason = "resync" }));
                return;
            }

            // Prefix-only optimization: find longest common prefix, then
            // backspace everything after it and insert the new remainder.
            // We intentionally do NOT use suffix matching — it is incorrect
            // when strings contain repeated patterns (e.g. "ABAB"→"BAB"
            // would match suffix "ABA" and produce wrong results).
            // The mobile diff engine also uses prefix-only, so both sides
            // are consistent.
            var prefix = 0;
            var limit = Math.Min(_appliedInputBuffer.Length, nextText.Length);
            while (prefix < limit && _appliedInputBuffer[prefix] == nextText[prefix])
            {
                prefix++;
            }

            var backspace = _appliedInputBuffer.Length - prefix;
            var insert = nextText[prefix..];
            DebugLogger.Log($"FLUSH: buffer=\"{TruncateForLog(_appliedInputBuffer)}\" -> next=\"{TruncateForLog(nextText)}\", prefix={prefix}, bs={backspace}, insert=\"{TruncateForLog(insert)}\"");
            TextInjector.ApplyDiff(backspace, insert);
            _appliedInputBuffer = nextText;
        }
        catch (Exception ex)
        {
            // An exception here would leave _appliedInputBuffer stale, causing
            // the next diff to compute against a wrong baseline (first-char
            // swallow).  Reset everything and ask mobile to resync.
            DebugLogger.Log($"FLUSH: EXCEPTION {ex.GetType().Name}: {ex.Message}. Resetting target+buffer, sending resync.");
            _inputTargetWindow = IntPtr.Zero;
            _appliedInputBuffer = string.Empty;
            _ = _roomManager.SendToMobileAsync(JsonSerializer.Serialize(new { type = "clear", reason = "resync" }));
        }
    }

    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "diff":
                break;

            case "enter":
                if (!EnsureInputTarget()) return;
                DebugLogger.Log($"ENTER: injecting Enter, resetting buffer from \"{TruncateForLog(_appliedInputBuffer)}\"");
                TextInjector.SendEnter();
                _appliedInputBuffer = string.Empty;
                // NOTE: intentionally NOT clearing _inputTargetWindow here.
                // The user just pressed Enter — they're still in the same
                // target window. Clearing it forces EnsureInputTarget to
                // re-establish the target, which can fail during the brief
                // focus transition after SendEnter, dropping the first
                // diff batch of the next paragraph (the "first-char swallow"
                // bug). Keeping the window handle avoids this race.
                break;

            case "backspace":
                if (!EnsureInputTarget()) return;
                if (cmd.Count > 0)
                {
                    TextInjector.SendBackspace(cmd.Count);
                    _appliedInputBuffer = ApplyDiffToBuffer(_appliedInputBuffer, cmd.Count, string.Empty);
                }
                break;

            case "clipboard":
                if (!string.IsNullOrEmpty(cmd.Text))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try { Clipboard.SetText(cmd.Text); }
                        catch { /* Safe to ignore: clipboard operations may fail if another process holds the clipboard */ }
                    });
                }
                break;

            case "regex_config":
                Dispatcher.BeginInvoke(() =>
                {
                    if (!RegexRuleImportService.TryImport(cmd.Text ?? string.Empty, out var result, out var error))
                    {
                        SetStatus(L("StatusRegexConfigInvalidFormat", error ?? L("StatusRegexConfigInvalid")), false);
                        MessageBox.Show(error ?? L("StatusRegexConfigInvalid"), L("RegexConfigErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _regexFilterMarkdown = result!.NormalizedMarkdown;
                    PersistSettings();
                    SetStatus(L("StatusRegexConfigSavedFormat", result.Rules.Count), true);
                });
                break;

            case "clear":
                DebugLogger.Log("CLEAR cmd from mobile: resetting inputTarget and buffer");
                _inputTargetWindow = IntPtr.Zero;
                _appliedInputBuffer = string.Empty;
                break;

            case "file_start":
                if (!string.IsNullOrEmpty(cmd.TransferId))
                {
                    // Reject files exceeding server-configured max size
                    if (_roomManager.MaxFileMB > 0 && cmd.FileSize > (long)_roomManager.MaxFileMB * 1024 * 1024)
                    {
                        if (_devMode)
                        {
                            _ = Dispatcher.BeginInvoke(() => AppendLog($"Rejected file_start: {cmd.FileName} ({cmd.FileSize} bytes) exceeds MaxFileMB={_roomManager.MaxFileMB}"));
                        }
                        break;
                    }

                    CancelPendingReceivedFilesReveal();
                    _fileTransfers[cmd.TransferId] = new IncomingFileTransferBuffer { Header = cmd };
                    _ = Dispatcher.BeginInvoke(() =>
                        EnsureIncomingTransferBatch(cmd.TransferId, cmd.FileName ?? "received_file", cmd.FileSize));
                    if (_devMode)
                    {
                        _ = Dispatcher.BeginInvoke(() => AppendLog($"File transfer started: {cmd.FileName} ({cmd.FileSize} bytes)"));
                    }
                }
                break;

            case "file_chunk":
                if (!string.IsNullOrEmpty(cmd.TransferId) &&
                    !string.IsNullOrEmpty(cmd.ChunkData) &&
                    _fileTransfers.TryGetValue(cmd.TransferId, out var transfer))
                {
                    byte[] chunkBytes;
                    try
                    {
                        chunkBytes = Convert.FromBase64String(cmd.ChunkData);
                    }
                    catch
                    {
                        break;
                    }

                    transfer.Chunks.Add(chunkBytes);
                    transfer.BytesReceived += chunkBytes.Length;
                    _ = Dispatcher.BeginInvoke(() =>
                        UpdateTransferProgress(cmd.TransferId, transfer.BytesReceived, cmd.IsLast ? "completed" : "active"));

                    if (cmd.IsLast)
                    {
                        _fileTransfers.Remove(cmd.TransferId);
                        HandleReceivedFile(transfer.Header, CombineChunks(transfer.Chunks));
                    }
                }
                break;

            case "file_abort":
                if (!string.IsNullOrEmpty(cmd.TransferId))
                {
                    _fileTransfers.Remove(cmd.TransferId);
                    _ = Dispatcher.BeginInvoke(() => UpdateTransferProgress(cmd.TransferId, 0, "cancelled"));
                }
                break;
        }
    }

    private bool EnsureInputTarget()
    {
        var currentForeground = GetForegroundWindow();
        if (currentForeground == _windowHandle && _windowActivationCoordinator.TryRestoreImmediately(_windowHandle))
        {
            currentForeground = GetForegroundWindow();
        }

        if (currentForeground == IntPtr.Zero)
        {
            DebugLogger.Log("TARGET: foreground=0x0, starting recovery");
            BeginInputTargetRecovery();
            return false;
        }

        if (currentForeground == _windowHandle)
        {
            // Fluentia is foreground (e.g. Hide() hasn't completed yet).
            // If we have no input target, try the last known external window
            // instead of dropping the diff.
            if (_inputTargetWindow == IntPtr.Zero && _lastExternalForegroundWindow != IntPtr.Zero
                && IsWindow(_lastExternalForegroundWindow))
            {
                _inputTargetWindow = _lastExternalForegroundWindow;
                CancelInputTargetRecovery();
                _manualInputTargetRecoveryNotified = false;
                SetStatus(L("StatusEncrypted"), true);
                DebugLogger.Log($"TARGET: Fluentia is foreground, using last external 0x{_inputTargetWindow:X}");
                return true;
            }
            DebugLogger.Log($"TARGET: Fluentia is foreground, no fallback. inputTarget=0x{_inputTargetWindow:X}, lastExt=0x{_lastExternalForegroundWindow:X}");
            BeginInputTargetRecovery();
            return false;
        }

        if (_inputTargetWindow == IntPtr.Zero)
        {
            CancelInputTargetRecovery();
            _manualInputTargetRecoveryNotified = false;
            _inputTargetWindow = currentForeground;
            SetStatus(L("StatusEncrypted"), true);
            DebugLogger.Log($"TARGET: first capture 0x{currentForeground:X}");
            return true;
        }

        if (currentForeground != _inputTargetWindow)
        {
            DebugLogger.Log($"TARGET: focus changed 0x{_inputTargetWindow:X} -> 0x{currentForeground:X}, dropping diff");
            BeginInputTargetRecovery();
            _ = ResetMobileInputAfterFocusChangeAsync();
            return false;
        }

        return true;
    }

    private async Task ResetMobileInputAfterFocusChangeAsync()
    {
        if (_inputTargetWindow == IntPtr.Zero) return;

        // Cancel any pending focus-clear debounce
        _focusClearCts?.Cancel();
        _focusClearCts?.Dispose();
        var cts = new CancellationTokenSource();
        _focusClearCts = cts;

        // Debounce: if focus returns to the same window within 300ms, abort
        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            DebugLogger.Log("FOCUS-CLEAR: cancelled (focus returned within 300ms)");
            return;
        }

        // NOTE: intentionally NOT clearing _inputTargetWindow here.
        // Same rationale as the ENTER handler (line 684): clearing it forces
        // EnsureInputTarget to re-establish the target during the focus
        // transition, which can fail and drop the first diff batch (the
        // "first-char swallow" bug).  Keeping the window handle lets
        // EnsureInputTarget succeed immediately when the user returns to
        // the same window.  If they switch to a DIFFERENT window,
        // EnsureInputTarget will detect the mismatch and trigger a new clear.
        _appliedInputBuffer = string.Empty;
        DebugLogger.Log("FOCUS-CLEAR: sending clear to mobile, buffer reset (target preserved)");

        var sent = await _roomManager.SendToMobileAsync(JsonSerializer.Serialize(new { type = "clear", reason = "focus" }));
        DebugLogger.Log($"FOCUS-CLEAR: send result={sent}");
        if (!sent)
        {
            // Encryption not ready or transport down — queue for later
            _pendingClearOnReconnect = true;
        }
    }

    private static byte[] CombineChunks(List<byte[]> chunks)
    {
        int total = 0;
        foreach (var chunk in chunks) total += chunk.Length;

        var result = new byte[total];
        int position = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, position, chunk.Length);
            position += chunk.Length;
        }

        return result;
    }


    private void UpdateNetworkAvailability()
    {
        _networkAvailable = NetworkInterface.GetIsNetworkAvailable();
    }

    private bool CanUseConfiguredServer()
    {
        var url = ServerUrlBox?.Text?.Trim() ?? string.Empty;
        if (_networkAvailable) return true;
        return IsLoopback(url);
    }

    private static bool IsLoopback(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath);
            if (key == null) return;

            if (enabled)
            {
                key.SetValue(StartupRegistryValue, GetStartupCommand());
            }
            else
            {
                key.DeleteValue(StartupRegistryValue, false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyStartupRegistration failed: {ex.Message}");
        }
    }

    private static string GetStartupCommand()
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        return $"\"{processPath}\"";
    }

    private void ToggleSettingsPanel(bool open)
    {
        if (_settingsOpen == open) return;
        _settingsOpen = open;

        SettingsPanel.Visibility = Visibility.Visible;
        SettingsScrim.Visibility = Visibility.Visible;
        SettingsPanel.IsHitTestVisible = true;
        SettingsScrim.IsHitTestVisible = true;

        var duration = TimeSpan.FromMilliseconds(320);
        var easing = new QuinticEase { EasingMode = EasingMode.EaseOut };

        var panelAnimation = new DoubleAnimation
        {
            To = open ? 0 : 360,
            Duration = duration,
            EasingFunction = easing,
        };

        if (!open)
        {
            panelAnimation.Completed += (_, _) =>
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsScrim.Visibility = Visibility.Collapsed;
                SettingsPanel.IsHitTestVisible = false;
                SettingsScrim.IsHitTestVisible = false;
            };
        }

        SettingsPanelTranslateTransform.BeginAnimation(TranslateTransform.XProperty, panelAnimation);
        MainContentTranslateTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = open ? -18 : 0,
            Duration = duration,
            EasingFunction = easing,
        });
        MainContentScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            To = open ? 0.985 : 1,
            Duration = duration,
            EasingFunction = easing,
        });
        MainContentScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            To = open ? 0.985 : 1,
            Duration = duration,
            EasingFunction = easing,
        });
        SettingsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = open ? 0.44 : 0,
            Duration = duration,
            EasingFunction = easing,
        });
    }

    private void UpdateCloseButtonToolTip()
    {
        BtnClose.ToolTip = _closeToTray ? L("TooltipHideToTray") : L("TooltipQuit");
    }

    private Color GetThemeColor(string resourceKey)
    {
        return (Color)FindResource(resourceKey);
    }

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        var color = GetThemeColor(connected ? "Success" : "Danger");
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        StatusIndicator.Fill = brush;
    }

    private void AppendLog(string text)
    {
        if (!_devMode) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text += $"[{timestamp}] {text}\n";
        var lines = LogText.Text.Split('\n');
        if (lines.Length > 100)
        {
            LogText.Text = string.Join('\n', lines[^50..]);
        }
    }

    private void ShowTrafficIcons(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CloseX.Visibility = visibility;
        MinLine.Visibility = visibility;
        MaxDiamond.Visibility = visibility;
    }

    private void App_ThemeChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() => SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer()));
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateNetworkAvailability();
            if (!CanUseConfiguredServer())
            {
                SetStatus(L("StatusNetworkDisconnected"), false);
            }
            RefreshVisualState();
        });
    }

    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateNetworkAvailability();
            RefreshVisualState();
        });
    }

    private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == _lastForegroundWindow) return;

        // If focus returns to the input target window, cancel any pending debounced clear
        if (_inputTargetWindow != IntPtr.Zero && hwnd == _inputTargetWindow)
        {
            _focusClearCts?.Cancel();
            _focusClearCts?.Dispose();
            _focusClearCts = null;
        }

        _lastForegroundWindow = hwnd;
        RememberExternalForegroundWindow(hwnd);
        if (_devMode)
        {
            _ = Dispatcher.BeginInvoke(() => AppendLog($"Focus -> 0x{hwnd:X}"));
        }

        if (_inputTargetWindow != IntPtr.Zero && hwnd != _inputTargetWindow)
        {
            _ = Dispatcher.BeginInvoke(() => { _ = ResetMobileInputAfterFocusChangeAsync(); });
        }
    }

    private void RememberExternalForegroundWindow(IntPtr hwnd)
    {
        _windowActivationCoordinator.RememberExternalWindow(hwnd, _windowHandle);
        _lastExternalForegroundWindow = _windowActivationCoordinator.LastExternalWindow;

        if (hwnd != IntPtr.Zero && hwnd != _windowHandle)
        {
            if (_manualInputTargetRecoveryNotified)
            {
                SetStatus(L("StatusEncrypted"), true);
            }
            CancelInputTargetRecovery();
            _manualInputTargetRecoveryNotified = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T typed)
            {
                return typed;
            }

            source = source switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        return null;
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source)
    {
        return FindAncestor<System.Windows.Controls.Button>(source) != null ||
            FindAncestor<System.Windows.Controls.TextBox>(source) != null ||
            FindAncestor<System.Windows.Controls.ComboBox>(source) != null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Safe to ignore: DragMove throws if mouse button is released during drag
            }
        }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
    }

    private void TrafficClose_Click(object sender, RoutedEventArgs e)
    {
        RequestWindowClose();
    }

    private void TrafficMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void TrafficMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Title_DevTap(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastDevTap).TotalSeconds > 2)
        {
            _devTapCount = 0;
        }

        _lastDevTap = now;
        _devTapCount += 1;

        if (_devTapCount >= 5 && !_devMode)
        {
            _devMode = true;
            LogContainer.Visibility = Visibility.Visible;
            AppendLog("Developer mode enabled");
            _devTapCount = 0;
        }
    }

    private void DeviceCode_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeviceCodeText.Text)) return;

        try
        {
            Clipboard.SetText(DeviceCodeText.Text);
            SetStatus(L("StatusConnectionCodeCopied"), true);
        }
        catch
        {
            // Safe to ignore: clipboard operations may fail if another process holds the clipboard
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerUrlBox.Text))
        {
            MessageBox.Show(L("MessageEnterServerUrl"), L("AppName"));
            return;
        }

        await TryConnectAsync(autoConnect: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _mobileConnected = false;
        _handshakePending = false;
        _deviceCode = null;
        _qrVisible = true;
        _inputTargetWindow = IntPtr.Zero;
        await _roomManager.RefreshSession();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettingsPanel(!_settingsOpen);
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettingsPanel(false);
    }

    private void SettingsScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSettingsPanel(false);
    }


    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var path = SavePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SetStatus(L("StatusInvalidSavePath"), false);
            return;
        }

        _fileSavePath = path;
        _closeToTray = CloseToTrayToggle.IsChecked == true;
        _launchAtStartup = LaunchAtStartupToggle.IsChecked == true;
        DebugLogger.Enabled = DebugLoggingToggle.IsChecked == true;
        ApplyStartupRegistration(_launchAtStartup);
        PersistSettings();
        UpdateCloseButtonToolTip();
        ToggleSettingsPanel(false);
        SetStatus(L("StatusSettingsSaved"), true);
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLanguageSelection) return;
        if (LanguageSelector.SelectedItem is not ComboBoxItem selected || selected.Tag is not string language)
        {
            return;
        }

        LocalizationService.SetLanguagePreference(language);
        ApplyLocalizedText();
        RefreshVisualState();
        PersistSettings();
    }

    private void UpdateLanguageSelectorSelection()
    {
        _isApplyingLanguageSelection = true;
        try
        {
            var target = LocalizationService.CurrentLanguageSetting;
            foreach (var item in LanguageSelector.Items)
            {
                if (item is ComboBoxItem comboItem && string.Equals(comboItem.Tag as string, target, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageSelector.SelectedItem = comboItem;
                    return;
                }
            }

            LanguageSelector.SelectedIndex = 0;
        }
        finally
        {
            _isApplyingLanguageSelection = false;
        }
    }

    private void ApplyLocalizedText()
    {
        Title = $"{L("MainWindowTitle")} · v{AppVersion}";
        TitleLabel.Text = $"{L("AppName")} · v{AppVersion}";
        BtnMinimize.ToolTip = L("TooltipMinimize");
        BtnMaximize.ToolTip = L("TooltipMaximizeRestore");
        SettingsTitleButton.ToolTip = L("TooltipOpenSettings");
        HeroTitleText.Text = L("HeroTitle");
        ConnectionHintText.Text = L("ConnectionHintConnectToRelay");
        ConnectionSectionTitleText.Text = L("SectionConnection");
        ServerUrlLabelText.Text = L("LabelServerUrl");
        ConnectBtn.Content = L("ButtonConnect");
        SendFileButton.Content = L("ButtonSendFile");
        PairingSectionTitleText.Text = L("SectionPairing");
        PairingSectionSubtitleText.Text = L("PairingSubtitle");
        QrExpandBtn.Content = L("ButtonShowQr");
        PairingNoticeTitle.Text = L("PairingNoticeReady");
        DeviceCodePanel.ToolTip = L("TooltipCopyDeviceCode");
        DeviceCodeTitleText.Text = L("DeviceCodeTitle");
        DeviceCodeHintText.Text = L("DeviceCodeHint");
        DiagnosticsTitleText.Text = L("DiagnosticsTitle");
        SettingsHeaderText.Text = L("SettingsTitle");
        SettingsSubheaderText.Text = L("SettingsSubtitle");
        CloseSettingsButton.ToolTip = L("TooltipCloseSettings");
        CloseBehaviorTitleText.Text = L("CloseBehaviorTitle");
        CloseBehaviorBodyText.Text = L("CloseBehaviorBody");
        StartupTitleText.Text = L("StartupTitle");
        StartupBodyText.Text = L("StartupBody");
        DebugLoggingTitleText.Text = L("DebugLoggingTitle");
        DebugLoggingBodyText.Text = L("DebugLoggingBody");
        UpdateTitleText.Text = L("UpdateTitle");
        UpdateBodyText.Text = L("UpdateBody");
        CheckUpdateButton.Content = L("ButtonCheckUpdate");
        LanguageTitleText.Text = L("SettingsLanguageTitle");
        LanguageBodyText.Text = L("SettingsLanguageBody");
        LanguageSystemOption.Content = L("LanguageSystem");
        LanguageEnglishOption.Content = L("LanguageEnglish");
        LanguageChineseOption.Content = L("LanguageChinese");
        SessionTitleText.Text = L("SettingsSessionTitle");
        SessionBodyText.Text = L("SettingsSessionBody");
        NewSessionSettingsButton.Content = L("ButtonCreateNewSession");
        ReceivedFilesTitleText.Text = L("ReceivedFilesTitle");
        ReceivedFilesBodyText.Text = L("ReceivedFilesBody");
        BrowseSavePathButton.Content = L("ButtonBrowse");
        SaveSettingsButton.Content = L("ButtonSaveSettings");
        RefreshTrayMenuText();
        UpdateLanguageSelectorSelection();
        RefreshStatusFromState();
        UpdateCloseButtonToolTip();
        RefreshTransferProgressCard();
    }

    private UpdateManager? _updateManager;
    private bool _updateCheckInProgress;
    private bool _portableUpdateNotified;

    private UpdateManager GetUpdateManager(string? serverUrl = null)
    {
        if (_updateManager != null) return _updateManager;

        // Always use GitHub Releases for update checks.
        // Self-hosted /updates/ on the relay server is unreliable (404 when
        // UPDATE_DIR is misconfigured or files are missing).  GitHub is
        // reachable for all users — even behind GFW the Velopack
        // GithubSource uses the GitHub API which is not blocked.
        _updateManager = new UpdateManager(new GithubSource(
            "https://github.com/wxyu71/Fluentia", null, false));
        return _updateManager;
    }

    /// <summary>
    /// Automatic update check on startup. Silent on failure; only prompts if an update is ready.
    /// For portable installs, shows a one-time tray notification.
    /// </summary>
    private async Task AutoCheckForUpdatesAsync()
    {
        try
        {
            var mgr = GetUpdateManager(ServerUrlBox?.Text?.Trim());
            if (!mgr.IsInstalled)
            {
                // Portable install — show a one-time tray notification
                if (!_portableUpdateNotified && _trayIcon != null)
                {
                    _portableUpdateNotified = true;
                    try
                    {
                        _trayIcon.ShowNotification(
                            L("TrayNotificationPortableUpdateTitle"),
                            L("TrayNotificationPortableUpdateBody"),
                            NotificationIcon.Info,
                            null,
                            true,
                            false,
                            false,
                            false,
                            TimeSpan.FromSeconds(8));
                    }
                    catch
                    {
                        // Tray notification failure is non-critical
                    }
                }
                return;
            }

            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return;

            await mgr.DownloadUpdatesAsync(info);

            var result = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                L("StatusUpdateReady"),
                L("AppName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information));

            if (result == MessageBoxResult.Yes)
            {
                mgr.ApplyUpdatesAndRestart(info);
            }
        }
        catch
        {
            // Automatic check: silently ignore failures
        }
    }

    /// <summary>
    /// Manual update check triggered by the user. Always shows feedback.
    /// </summary>
    private async Task ManualCheckForUpdatesAsync()
    {
        if (_updateCheckInProgress) return;
        _updateCheckInProgress = true;

        try
        {
            UpdateManager mgr;
            try
            {
                mgr = GetUpdateManager(ServerUrlBox?.Text?.Trim());
            }
            catch (Exception ex)
            {
                UpdateUpdateStatus(L("StatusUpdateCheckFailed", ex.Message), false);
                return;
            }

            if (!mgr.IsInstalled)
            {
                UpdateUpdateStatus(L("StatusUpdateNotInstalled"), false);
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/wxyu71/Fluentia/releases/latest")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Browser open failure is non-critical
                }
                return;
            }

            CheckUpdateButton.IsEnabled = false;
            UpdateUpdateStatus(L("StatusCheckingUpdate"), true);

            var info = await mgr.CheckForUpdatesAsync();
            if (info == null)
            {
                UpdateUpdateStatus(L("StatusUpToDate"), true);
                return;
            }

            UpdateUpdateStatus(L("StatusUpdateAvailable"), true);
            await mgr.DownloadUpdatesAsync(info);

            UpdateUpdateStatus(L("StatusUpdateReady"), true);

            var result = System.Windows.MessageBox.Show(
                L("StatusUpdateReady"),
                L("AppName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                UpdateUpdateStatus(L("StatusUpdateApplied"), true);
                mgr.ApplyUpdatesAndRestart(info);
            }
        }
        catch (Exception ex)
        {
            UpdateUpdateStatus(L("StatusUpdateCheckFailed", ex.Message), false);
        }
        finally
        {
            _updateCheckInProgress = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void UpdateUpdateStatus(string text, bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateStatusText.Text = text;
            UpdateStatusText.Visibility = Visibility.Visible;
        });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ManualCheckForUpdatesAsync();
    }
}
