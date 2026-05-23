using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fluentia.Models;
using Fluentia.Services;
using Fluentia.Views;
using H.NotifyIcon;
using Microsoft.Win32;
using QRCoder;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
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
    private bool _outgoingTransferPaused;
    private bool _outgoingTransferCancelRequested;
    private string? _deviceCode;
    private string? _activeOutgoingTransferId;
    private string? _activeOutgoingUiFileId;
    private int _trayCreationRetriesRemaining;
    private string _fileSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
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
    private static readonly TimeSpan DiffBatchWindow = TimeSpan.FromMilliseconds(45);

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook;
    private IntPtr _lastForegroundWindow;
    private IntPtr _lastExternalForegroundWindow;
    private string _appliedInputBuffer = string.Empty;

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

    public MainWindow()
    {
        InitializeComponent();

        _roomManager = new RoomManager();
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

        TextInjector.DiagnosticLog = (message) =>
        {
            if (_devMode) _ = Dispatcher.BeginInvoke(() => AppendLog(message));
        };

        Task.Run(ProcessCommandQueue);

        _lastForegroundWindow = GetForegroundWindow();
        RememberExternalForegroundWindow(_lastForegroundWindow);
        _winEventDelegate = OnForegroundWindowChanged;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        App.ThemeChanged += App_ThemeChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
    }

    private void SetupRoomManagerEvents()
    {
        _roomManager.OnServerConnectionChanged += (connected) => Dispatcher.Invoke(() =>
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

        _roomManager.OnSessionCreated += (token) => Dispatcher.Invoke(async () =>
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
            await _roomManager.RequestDeviceCode();
        });

        _roomManager.OnDeviceCodeCreated += (code) => Dispatcher.Invoke(() =>
        {
            if (_mobileConnected || _handshakePending || _roomManager.EncryptionReady)
            {
                return;
            }

            _deviceCode = code;
            if (_devMode) AppendLog($"Device code ready: {code}");
            RefreshVisualState();
        });

        _roomManager.OnDeviceCodePending += (code, verifyId, userAgent) => Dispatcher.Invoke(() =>
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

        _roomManager.OnMobileConnected += (deviceId) => Dispatcher.Invoke(() =>
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

        _roomManager.OnMobileDisconnected += () => Dispatcher.Invoke(async () =>
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
                    await _roomManager.RequestDeviceCode();
                }

                StartDisconnectReminderTimer();
            }

            RefreshVisualState();
        });

        _roomManager.OnEncryptionReady += () => Dispatcher.Invoke(() =>
        {
            _mobileConnected = true;
            _handshakePending = false;
            _deviceCode = null;
            _inputTargetWindow = IntPtr.Zero;
            PersistSettings();
            ShowQrArea(false);
            SetStatus(L("StatusEncrypted"), true);
            RefreshVisualState();
            Hide();
            _ = Dispatcher.BeginInvoke(RestorePreviousExternalWindow, DispatcherPriority.ApplicationIdle);
        });

        _roomManager.OnSessionRecovered += () => Dispatcher.Invoke(async () =>
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
                await _roomManager.RequestDeviceCode();
            }
        });

        _roomManager.OnInputCommand += (cmd) => _cmdChannel.Writer.TryWrite(cmd);

        _roomManager.OnStatusChanged += (status) => Dispatcher.Invoke(() =>
        {
            if (_devMode) AppendLog(status);
        });

        _roomManager.OnError += (error) => Dispatcher.Invoke(() =>
        {
            _handshakePending = false;
            SetStatus(L("StatusErrorFormat", error), false);
            RefreshVisualState();
            if (_devMode) AppendLog($"Error: {error}");
        });

        _roomManager.OnVersionIncompatible += (error) => Dispatcher.Invoke(() =>
        {
            _mobileConnected = false;
            _handshakePending = false;
            _inputTargetWindow = IntPtr.Zero;
            SetStatus(error, false);
            RefreshVisualState();
            MessageBox.Show(error, L("VersionIncompatibleTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        });
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

    private void RefreshVisualState()
    {
        UpdateControlState();
        UpdateSessionCountdown();
        UpdatePairingSurface();
        UpdateCloseButtonToolTip();
        SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer());
    }

    private void UpdateControlState()
    {
        var canUseServer = CanUseConfiguredServer();
        var hasSession = _roomManager.CurrentToken != null;
        var canPair = canUseServer && _serverConnected && hasSession && !_mobileConnected && !_handshakePending && !IsSessionExpired();
        var canRotateSession = _serverConnected && !_handshakePending &&
            (_roomManager.EncryptionReady || _roomManager.HasTrustedSession || IsSessionExpired());
        var canSendFile = _roomManager.FileTransferEnabled && _roomManager.EncryptionReady;

        ServerUrlBox.IsEnabled = !_serverConnected && !_handshakePending;
        ConnectBtn.IsEnabled = !_serverConnected && !_handshakePending && canUseServer;
        ConnectBtn.Content = _serverConnected ? L("ButtonConnected") : canUseServer ? L("ButtonConnect") : L("ButtonOffline");
        QrExpandBtn.IsEnabled = canPair;
        SendFileButton.Visibility = canSendFile ? Visibility.Visible : Visibility.Collapsed;
        SendFileButton.IsEnabled = canSendFile;
        NewSessionSettingsButton.IsEnabled = canRotateSession;

        if (!canUseServer)
        {
            ConnectionHintText.Text = L("ConnectionHintNoNetwork");
        }
        else if (_serverConnected && _roomManager.HasTrustedSession && !_mobileConnected)
        {
            ConnectionHintText.Text = L("ConnectionHintTrustedWaiting");
        }
        else if (_serverConnected && _roomManager.EncryptionReady)
        {
            ConnectionHintText.Text = L("ConnectionHintEncrypted");
        }
        else if (_serverConnected && hasSession)
        {
            ConnectionHintText.Text = L("ConnectionHintSessionReady", _roomManager.SessionMaxAgeDays);
        }
        else if (_serverConnected)
        {
            ConnectionHintText.Text = L("ConnectionHintConnectedPreparing");
        }
        else
        {
            ConnectionHintText.Text = L("ConnectionHintConnectToRelay");
        }
    }

    private void UpdatePairingSurface()
    {
        DeviceCodePanel.Visibility = Visibility.Collapsed;
        QrContainer.Visibility = Visibility.Collapsed;
        QrExpandBtn.Visibility = Visibility.Collapsed;

        if (!CanUseConfiguredServer())
        {
            ShowPairingNotice(L("PairingNoticeNetworkTitle"), L("PairingNoticeNetworkBody"));
            return;
        }

        if (!_serverConnected)
        {
            ShowPairingNotice(L("PairingNoticeServerTitle"), L("PairingNoticeServerBody"));
            return;
        }

        if (_roomManager.CurrentToken == null)
        {
            ShowPairingNotice(L("PairingNoticePreparingTitle"), L("PairingNoticePreparingBody"));
            return;
        }

        if (IsSessionExpired() && !_mobileConnected)
        {
            ShowPairingNotice(L("PairingNoticeExpiredTitle"), L("PairingNoticeExpiredBody"));
            return;
        }

        if (_roomManager.HasTrustedSession && !_mobileConnected && !_handshakePending)
        {
            ShowPairingNotice(L("PairingNoticeWaitingReconnectTitle"), L("PairingNoticeWaitingReconnectBody"));
            return;
        }

        if (_roomManager.EncryptionReady)
        {
            ShowPairingNotice(L("PairingNoticeReadyTitle"), L("PairingNoticeReadyBody"));
            return;
        }

        if (_mobileConnected || _handshakePending)
        {
            ShowPairingNotice(L("PairingNoticeSecuringTitle"), L("PairingNoticeSecuringBody"));
            return;
        }

        PairingNoticeCard.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_deviceCode))
        {
            DeviceCodePanel.Visibility = Visibility.Visible;
            DeviceCodeText.Text = _deviceCode;
        }

        if (_qrVisible)
        {
            QrContainer.Visibility = Visibility.Visible;
        }
        else
        {
            QrExpandBtn.Visibility = Visibility.Visible;
        }
    }

    private void ShowPairingNotice(string title, string body)
    {
        PairingNoticeTitle.Text = title;
        PairingNoticeText.Text = body;
        PairingNoticeCard.Visibility = Visibility.Visible;
    }

    private void UpdateSessionCountdown()
    {
        if (_roomManager.CurrentToken == null)
        {
            QrTimerText.Text = "";
            return;
        }

        if (_sessionExpiresAt == default)
        {
            if (_sessionCreatedAt == default)
            {
                _sessionCreatedAt = DateTime.Now;
            }

            _sessionExpiresAt = ResolveSessionExpiry(_sessionCreatedAt);
        }

        var remaining = _sessionExpiresAt - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            QrTimerText.Text = L("QrSessionExpired");
            QrTimerText.Foreground = new SolidColorBrush((Color)FindResource("Danger"));
            return;
        }

        QrTimerText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        if (remaining.TotalDays >= 1)
        {
            QrTimerText.Text = L("QrValidDays", Math.Ceiling(remaining.TotalDays));
        }
        else if (remaining.TotalHours >= 1)
        {
            QrTimerText.Text = L("QrValidHours", Math.Ceiling(remaining.TotalHours));
        }
        else
        {
            QrTimerText.Text = L("QrValidMinutes", Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        }
    }

    private bool IsSessionExpired()
    {
        return _sessionExpiresAt != default && DateTime.Now >= _sessionExpiresAt;
    }

    private void UpdateQRCode()
    {
        var qrData = _roomManager.GetQRData();
        if (string.IsNullOrEmpty(qrData))
        {
            QrHintText.Text = L("QrNoActiveSession");
            QrCodeImage.Source = null;
            return;
        }

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(qrData, QRCodeGenerator.ECCLevel.L);

        var modules = qrCodeData.ModuleMatrix;
        int size = modules.Count;
        const int targetPx = 420;
        int moduleSize = Math.Max(4, targetPx / (size + 3));
        int margin = Math.Max(moduleSize, (int)Math.Round(moduleSize * 1.5));
        int canvasSize = size * moduleSize;
        int totalSize = canvasSize + margin * 2;

        var drawingVisual = new DrawingVisual();
        using (var dc = drawingVisual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, totalSize, totalSize));

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    if (!modules[row][col]) continue;
                    dc.DrawRectangle(Brushes.Black, null, new Rect(
                        margin + col * moduleSize,
                        margin + row * moduleSize,
                        moduleSize,
                        moduleSize));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(totalSize, totalSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();

        QrCodeImage.Source = bitmap;
    QrHintText.Text = L("QrSessionHint", _roomManager.CurrentToken!, _roomManager.SessionMaxAgeDays);
    }

    private async Task ProcessCommandQueue()
    {
        InputCommand? deferredCommand = null;

        while (true)
        {
            try
            {
                var cmd = deferredCommand ?? await _cmdChannel.Reader.ReadAsync();
                deferredCommand = null;

                if (cmd.Type == "diff")
                {
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

                            deferredCommand = queuedCommand;
                            break;
                        }

                        if (deferredCommand != null)
                        {
                            break;
                        }
                    }

                    FlushBufferedDiff(nextText);
                    continue;
                }

                HandleInputCommand(cmd);
            }
            catch (Exception ex)
            {
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
        if (!EnsureInputTarget())
        {
            return;
        }

        var prefix = 0;
        var limit = Math.Min(_appliedInputBuffer.Length, nextText.Length);
        while (prefix < limit && _appliedInputBuffer[prefix] == nextText[prefix])
        {
            prefix++;
        }

        var backspace = _appliedInputBuffer.Length - prefix;
        var insert = nextText[prefix..];
        TextInjector.ApplyDiff(backspace, insert);
        _appliedInputBuffer = nextText;
    }

    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "diff":
                break;

            case "enter":
                if (!EnsureInputTarget()) return;
                TextInjector.SendEnter();
                _inputTargetWindow = IntPtr.Zero;
                _appliedInputBuffer = string.Empty;
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
                    Dispatcher.Invoke(() =>
                    {
                        try { Clipboard.SetText(cmd.Text); }
                        catch { }
                    });
                }
                break;

            case "regex_config":
                Dispatcher.Invoke(() =>
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
                _inputTargetWindow = IntPtr.Zero;
                _appliedInputBuffer = string.Empty;
                break;

            case "file_start":
                if (!string.IsNullOrEmpty(cmd.TransferId))
                {
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
        if (currentForeground == _windowHandle && TryRestorePreviousExternalWindow())
        {
            currentForeground = GetForegroundWindow();
        }

        if (currentForeground == IntPtr.Zero) return false;

        if (currentForeground == _windowHandle) return false;

        if (_inputTargetWindow == IntPtr.Zero)
        {
            _inputTargetWindow = currentForeground;
            return true;
        }

        if (currentForeground != _inputTargetWindow)
        {
            _ = ResetMobileInputAfterFocusChangeAsync();
            return false;
        }

        return true;
    }

    private async Task ResetMobileInputAfterFocusChangeAsync()
    {
        if (_inputTargetWindow == IntPtr.Zero) return;

        _inputTargetWindow = IntPtr.Zero;
        if (_devMode)
        {
            AppendLog("Foreground app changed, clearing mobile editor state.");
        }

        await _roomManager.SendToMobileAsync(JsonSerializer.Serialize(new { type = "clear" }));
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

    private void CancelTransferProgressHide()
    {
        _transferProgressHideTimer?.Stop();
    }

    private void ScheduleTransferProgressHide()
    {
        if (_transferProgressHideTimer == null)
        {
            _transferProgressHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _transferProgressHideTimer.Tick += (_, _) =>
            {
                _transferProgressHideTimer?.Stop();
                _transferProgressBatch = null;
                TransferProgressCard.Visibility = Visibility.Collapsed;
                TransferProgressDetailsPanel.Children.Clear();
            };
        }

        _transferProgressHideTimer.Stop();
        _transferProgressHideTimer.Start();
    }

    private void BeginTransferProgressBatch(string direction, IEnumerable<(string Id, string Name, long TotalBytes)> files)
    {
        CancelTransferProgressHide();
        _transferProgressBatch = new TransferProgressBatch
        {
            Direction = direction,
            Expanded = files.Skip(1).Any(),
        };

        foreach (var file in files)
        {
            _transferProgressBatch.Files.Add(new TransferProgressFile
            {
                Id = file.Id,
                Name = file.Name,
                TotalBytes = Math.Max(0, file.TotalBytes),
                Status = "queued",
            });
        }

        RefreshTransferProgressCard();
    }

    private void EnsureIncomingTransferBatch(string transferId, string fileName, long totalBytes)
    {
        CancelTransferProgressHide();

        if (_transferProgressBatch == null || _transferProgressBatch.Direction != "receive")
        {
            _transferProgressBatch = new TransferProgressBatch
            {
                Direction = "receive",
                Expanded = false,
            };
        }

        var existing = _transferProgressBatch.Files.FirstOrDefault(file => file.Id == transferId);
        if (existing == null)
        {
            _transferProgressBatch.Files.Add(new TransferProgressFile
            {
                Id = transferId,
                Name = fileName,
                TotalBytes = Math.Max(0, totalBytes),
                Status = "active",
            });
        }
        else
        {
            existing.Name = fileName;
            existing.TotalBytes = Math.Max(existing.TotalBytes, totalBytes);
            existing.Status = "active";
        }

        RefreshTransferProgressCard();
    }

    private void UpdateTransferProgress(string fileId, long transferredBytes, string? status = null)
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        var file = _transferProgressBatch.Files.FirstOrDefault(item => item.Id == fileId);
        if (file == null)
        {
            return;
        }

        file.TransferredBytes = Math.Max(file.TransferredBytes, Math.Min(file.TotalBytes > 0 ? file.TotalBytes : transferredBytes, transferredBytes));
        if (status != null)
        {
            file.Status = status;
        }

        RefreshTransferProgressCard();

        if (_transferProgressBatch.Files.All(item => item.Status is "completed" or "cancelled"))
        {
            ScheduleTransferProgressHide();
        }
    }

    private void CancelPendingTransferProgress()
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        foreach (var file in _transferProgressBatch.Files.Where(item => item.Status is not "completed"))
        {
            file.Status = "cancelled";
        }

        RefreshTransferProgressCard();
        ScheduleTransferProgressHide();
    }

    private void CancelOngoingTransfers()
    {
        _fileTransfers.Clear();
        _outgoingTransferCancelRequested = true;
        _outgoingTransferPaused = false;
        _outgoingTransferResumeTcs?.TrySetResult(true);
        _outgoingTransferResumeTcs = null;
        _activeOutgoingTransferId = null;
        _activeOutgoingUiFileId = null;
        CancelPendingTransferProgress();
    }

    private Task WaitForOutgoingTransferResumeAsync()
    {
        if (!_outgoingTransferPaused)
        {
            return Task.CompletedTask;
        }

        _outgoingTransferResumeTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _outgoingTransferResumeTcs.Task;
    }

    private int? EstimateTransferSecondsLeft(long totalBytes, long transferredBytes, DateTime startedAtUtc)
    {
        if (totalBytes <= 0 || transferredBytes <= 0 || transferredBytes >= totalBytes)
        {
            return null;
        }

        var elapsed = DateTime.UtcNow - startedAtUtc;
        if (elapsed.TotalSeconds < 0.35)
        {
            return null;
        }

        var bytesPerSecond = transferredBytes / elapsed.TotalSeconds;
        if (bytesPerSecond <= 0)
        {
            return null;
        }

        return Math.Max(1, (int)Math.Round((totalBytes - transferredBytes) / bytesPerSecond));
    }

    private void RefreshTransferProgressCard()
    {
        if (_transferProgressBatch == null || _transferProgressBatch.Files.Count == 0)
        {
            TransferProgressCard.Visibility = Visibility.Collapsed;
            TransferProgressDetailsPanel.Children.Clear();
            return;
        }

        TransferProgressCard.Visibility = Visibility.Visible;

        var fileCount = _transferProgressBatch.Files.Count;
        var totalBytes = _transferProgressBatch.Files.Sum(file => Math.Max(0, file.TotalBytes));
        var transferredBytes = _transferProgressBatch.Files.Sum(file => Math.Max(0, file.TransferredBytes));
        var percent = totalBytes > 0
            ? Math.Max(0, Math.Min(100, (int)Math.Round(transferredBytes * 100d / totalBytes)))
            : (_transferProgressBatch.Files.All(file => file.Status == "completed") ? 100 : 0);

        var isSend = _transferProgressBatch.Direction == "send";
        var isCompleted = _transferProgressBatch.Files.All(file => file.Status == "completed");
        var isCancelled = _transferProgressBatch.Files.All(file => file.Status == "cancelled");
        var badgeWasVisible = TransferSuccessBadge.Visibility == Visibility.Visible;

        TransferProgressTitleText.Text = isCompleted
            ? L(isSend ? "TransferUploadedFilesFormat" : "TransferReceivedFilesFormat", fileCount)
            : L(isSend ? "TransferUploadingFilesFormat" : "TransferReceivingFilesFormat", fileCount);

        if (isCompleted)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressComplete");
        }
        else if (isCancelled)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressCancelled");
        }
        else if (isSend && _outgoingTransferPaused)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressPausedFormat", percent);
        }
        else
        {
            var secondsLeft = EstimateTransferSecondsLeft(totalBytes, transferredBytes, _transferProgressBatch.StartedAtUtc);
            TransferProgressSubtitleText.Text = secondsLeft.HasValue
                ? L("TransferProgressSecondsLeftFormat", percent, secondsLeft.Value)
                : percent == 0
                    ? L("TransferProgressPreparing")
                    : $"{percent}% · {L("TransferProgressTransferring")}";
        }

        TransferProgressScaleTransform.ScaleX = percent / 100d;
        var showSlackLine = _outgoingTransferPaused && isSend && !isCompleted && !isCancelled;
        TransferProgressLine.Background = new SolidColorBrush(showSlackLine
                ? (Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF8B77FF")!
                : (Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B55E7")!);
        TransferProgressLine.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = isCompleted ? 0 : (showSlackLine ? 0 : 1),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        TransferProgressSlackLine.Visibility = showSlackLine ? Visibility.Visible : Visibility.Collapsed;
        TransferProgressSlackLine.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = showSlackLine ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        TransferSuccessBadge.Visibility = isCompleted ? Visibility.Visible : Visibility.Collapsed;
        if (isCompleted && !badgeWasVisible)
        {
            var popAnimation = new DoubleAnimationUsingKeyFrames();
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            TransferSuccessBadgeScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, popAnimation);
            TransferSuccessBadgeScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, popAnimation);
        }

        TransferPauseButton.Visibility = isSend && !isCompleted && !isCancelled ? Visibility.Visible : Visibility.Collapsed;
        TransferCancelButton.Visibility = isSend && !isCompleted && !isCancelled ? Visibility.Visible : Visibility.Collapsed;
        TransferExpandButton.Visibility = fileCount > 1 ? Visibility.Visible : Visibility.Collapsed;

        TransferPauseGlyph.Text = _outgoingTransferPaused ? "↻" : "⏸";
        TransferPauseButton.ToolTip = L(_outgoingTransferPaused ? "TooltipResumeTransfer" : "TooltipPauseTransfer");
        TransferCancelButton.ToolTip = L("TooltipCancelTransfer");
        TransferExpandGlyph.Text = _transferProgressBatch.Expanded ? "⤡" : "⤢";
        TransferExpandButton.ToolTip = L(_transferProgressBatch.Expanded ? "TooltipCollapseTransfer" : "TooltipExpandTransfer");

        RebuildTransferProgressDetails();
        AnimateTransferActions(!isCompleted && !isCancelled ? TransferProgressCard.IsMouseOver : false);
    }

    private void RebuildTransferProgressDetails()
    {
        TransferProgressDetailsPanel.Children.Clear();
        if (_transferProgressBatch == null || !_transferProgressBatch.Expanded || _transferProgressBatch.Files.Count <= 1)
        {
            TransferProgressDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TransferProgressDetailsPanel.Visibility = Visibility.Visible;
    TransferProgressDetailsPanel.Opacity = 0;
    TransferProgressDetailsPanel.RenderTransform = new TranslateTransform(0, 8);

        foreach (var file in _transferProgressBatch.Files)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFF0F3FB")!),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0,
            };
            row.RenderTransform = new TranslateTransform(0, 10);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copyStack = new StackPanel();
            copyStack.Children.Add(new TextBlock
            {
                Text = file.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF1D2235")!),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var filePercent = file.TotalBytes > 0
                ? Math.Max(0, Math.Min(100, (int)Math.Round(file.TransferredBytes * 100d / file.TotalBytes)))
                : (file.Status == "completed" ? 100 : 0);

            copyStack.Children.Add(new TextBlock
            {
                Text = file.Status switch
                {
                    "completed" => $"{filePercent}% · {L("TransferProgressReady")}",
                    "cancelled" => $"{filePercent}% · {L("TransferProgressCancelled")}",
                    _ => $"{filePercent}% · {L("TransferProgressTransferring")}",
                },
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6F7693")!),
            });

            grid.Children.Add(copyStack);

            var percentText = new TextBlock
            {
                Text = $"{filePercent}%",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5146BA")!),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(percentText, 1);
            grid.Children.Add(percentText);

            row.Child = grid;
            TransferProgressDetailsPanel.Children.Add(row);
        }

        TransferProgressDetailsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (TransferProgressDetailsPanel.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        for (var index = 0; index < TransferProgressDetailsPanel.Children.Count; index += 1)
        {
            if (TransferProgressDetailsPanel.Children[index] is not Border row || row.RenderTransform is not TranslateTransform rowTranslate)
            {
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(index * 45);
            row.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                BeginTime = delay,
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

            rowTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                BeginTime = delay,
                From = 10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private void AnimateTransferActions(bool show)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        var offsetAnimation = new DoubleAnimation
        {
            To = show ? 0 : 8,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        TransferProgressActions.BeginAnimation(OpacityProperty, opacityAnimation);
        TransferProgressActionsTransform.BeginAnimation(TranslateTransform.XProperty, offsetAnimation);
    }

    private void HandleReceivedFile(InputCommand header, byte[] data)
    {
        var fileName = SanitizeFileName(header.FileName ?? "received_file");
        var mimeType = header.MimeType ?? "application/octet-stream";
        var savePath = Path.Combine(_fileSavePath, fileName);
        int suffix = 1;

        while (File.Exists(savePath))
        {
            var extension = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            savePath = Path.Combine(_fileSavePath, $"{name}_{suffix++}{extension}");
        }

        try
        {
            File.WriteAllBytes(savePath, data);
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                _ = Dispatcher.BeginInvoke(() => AppendLog($"File save failed: {ex.Message}"));
            }
            return;
        }

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = ms;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    Clipboard.SetImage(image);
                }
                catch
                {
                }
            });
        }

        if (_devMode)
        {
            _ = Dispatcher.BeginInvoke(() => AppendLog($"File received: {savePath}"));
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            SetStatus(L("StatusFileReceivedFormat", Path.GetFileName(savePath)), true);
            ScheduleReceivedFilesReveal();
        });
    }

    private void ScheduleReceivedFilesReveal()
    {
        if (_receivedFilesRevealTimer == null)
        {
            _receivedFilesRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _receivedFilesRevealTimer.Tick += (_, _) =>
            {
                _receivedFilesRevealTimer?.Stop();
                RevealReceivedFilesFolder();
            };
        }

        _receivedFilesRevealTimer.Stop();
        _receivedFilesRevealTimer.Start();
    }

    private void CancelPendingReceivedFilesReveal()
    {
        _receivedFilesRevealTimer?.Stop();
    }

    private void RevealReceivedFilesFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_fileSavePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"Open folder failed: {ex.Message}");
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder();
        foreach (var character in name)
        {
            if (Array.IndexOf(invalid, character) < 0)
            {
                builder.Append(character);
            }
        }

        var result = builder.ToString().Trim().TrimStart('.');
        return string.IsNullOrEmpty(result) ? "received_file" : result;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = L("TrayTooltipConnected"),
        };
        _trayIcon.TrayLeftMouseDown += TrayIcon_TrayLeftMouseDown;

        var menu = new ContextMenu();
        _trayShowItem = new MenuItem { Header = L("TrayShow") };
        _trayShowItem.Click += ShowWindow_Click;
        _trayRefreshItem = new MenuItem { Header = L("TrayNewSession") };
        _trayRefreshItem.Click += Refresh_Click;
        _trayExitItem = new MenuItem { Header = L("TrayQuit") };
        _trayExitItem.Click += Exit_Click;
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayRefreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_trayExitItem);

        _trayIcon.ContextMenu = menu;
        EnsureTrayIconCreated();
        RefreshTrayMenuText();
        SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer());
    }

    private void EnsureTrayIconCreated()
    {
        if (_trayIcon == null)
        {
            return;
        }

        TryForceCreateTrayIcon();

        if (_trayCreationRetryTimer == null)
        {
            _trayCreationRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trayCreationRetryTimer.Tick += (_, _) =>
            {
                if (_trayIcon == null || _trayCreationRetriesRemaining <= 0)
                {
                    _trayCreationRetryTimer?.Stop();
                    return;
                }

                _trayCreationRetriesRemaining -= 1;
                TryForceCreateTrayIcon();
            };
        }

        _trayCreationRetriesRemaining = 12;
        _trayCreationRetryTimer.Stop();
        _trayCreationRetryTimer.Start();
    }

    private void TryForceCreateTrayIcon()
    {
        if (_trayIcon == null)
        {
            return;
        }

        try
        {
            _trayIcon.ForceCreate();
            SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer());
        }
        catch
        {
        }
    }

    private void RefreshTrayMenuText()
    {
        if (_trayShowItem != null) _trayShowItem.Header = L("TrayShow");
        if (_trayRefreshItem != null) _trayRefreshItem.Header = L("TrayNewSession");
        if (_trayExitItem != null) _trayExitItem.Header = L("TrayQuit");
    }

    private void SetTrayIconColor(bool disconnected)
    {
        if (_trayIcon == null) return;

        _appIcon?.Dispose();
        _appIcon = CreateAppIcon(disconnected ? GetThemeColor("Danger") : GetThemeColor("Accent"));
        _trayIcon.Icon = _appIcon;
        _trayIcon.ToolTipText = disconnected ? L("TrayTooltipDisconnected") : L("TrayTooltipConnected");
    }

    private System.Drawing.Icon CreateAppIcon(Color color)
    {
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bmp))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(System.Drawing.Color.Transparent);

            if (!TryDrawTrayBaseIcon(graphics))
            {
                using var backgroundPath = CreateRoundedRectPath(2.5f, 2.5f, 27f, 27f, 7.5f);
                using var backgroundBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(20, 78, 140));
                graphics.FillPath(backgroundBrush, backgroundPath);

                using var outlinePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(245, 245, 247), 2.4f)
                {
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                using var phonePath = CreateRoundedRectPath(6f, 7f, 7f, 15f, 2.6f);
                using var desktopPath = CreateRoundedRectPath(17f, 10f, 8.5f, 9f, 2.2f);
                graphics.DrawPath(outlinePen, phonePath);
                graphics.DrawPath(outlinePen, desktopPath);

                using var beamPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(126, 205, 255), 2.8f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                graphics.DrawLine(beamPen, 13.5f, 15.5f, 18.2f, 15.5f);
                graphics.DrawLines(beamPen, new[]
                {
                    new System.Drawing.PointF(15.5f, 12.4f),
                    new System.Drawing.PointF(18.5f, 15.5f),
                    new System.Drawing.PointF(15.5f, 18.6f),
                });

                using var homeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, 245, 247));
                graphics.FillEllipse(homeBrush, 8.4f, 18.9f, 1.8f, 1.8f);
            }

            using var badgeRingBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 245, 239, 232));
            graphics.FillEllipse(badgeRingBrush, 20.7f, 20.1f, 8f, 8f);

            using var stateBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
            graphics.FillEllipse(stateBrush, 22.2f, 21.6f, 5.2f, 5.2f);
        }

        var hIcon = bmp.GetHicon();
        var tempIcon = System.Drawing.Icon.FromHandle(hIcon);
        using var stream = new MemoryStream();
        tempIcon.Save(stream);
        stream.Position = 0;
        var icon = new System.Drawing.Icon(stream);
        DestroyIcon(hIcon);
        return icon;
    }

    private static bool TryDrawTrayBaseIcon(System.Drawing.Graphics graphics)
    {
        if (!File.Exists(TrayIconSourceFile))
        {
            return false;
        }

        try
        {
            using var source = new System.Drawing.Bitmap(TrayIconSourceFile);
            graphics.DrawImage(source, new System.Drawing.RectangleF(1f, 1f, 30f, 30f));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectPath(float x, float y, float width, float height, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void StartDisconnectReminderTimer()
    {
        _disconnectTimer?.Stop();
        _disconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_roomManager.MobileExpirySecs) };
        _disconnectTimer.Tick += (_, _) =>
        {
            _disconnectTimer?.Stop();
            if (_mobileConnected) return;

            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        _disconnectTimer.Start();
    }

    private void ShowQrArea(bool show)
    {
        _qrVisible = show;
        RefreshVisualState();
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

    private void LoadSettings()
    {
        var migrateLegacySession = false;
        var hadProtectedSession = false;
        var recoveredFromBackup = false;
        var restoredFromPrimary = false;

        try
        {
            if (File.Exists(SettingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                if (doc.RootElement.TryGetProperty("savePath", out var savePathElement))
                {
                    var value = savePathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    {
                        _fileSavePath = value;
                    }
                }

                if (doc.RootElement.TryGetProperty("serverUrl", out var serverUrlElement))
                {
                    var value = serverUrlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        ServerUrlBox.Text = value;
                    }
                }

                if (doc.RootElement.TryGetProperty("closeToTray", out var closeToTrayElement))
                {
                    _closeToTray = closeToTrayElement.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("launchAtStartup", out var startupElement))
                {
                    _launchAtStartup = startupElement.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("language", out var languageElement))
                {
                    LocalizationService.SetLanguagePreference(languageElement.GetString());
                }

                if (doc.RootElement.TryGetProperty("regexFilterMarkdown", out var regexMarkdownElement))
                {
                    _regexFilterMarkdown = regexMarkdownElement.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("sessionCreatedAtUtc", out var sessionCreatedElement) &&
                    DateTime.TryParse(sessionCreatedElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var restoredCreatedAt))
                {
                    _sessionCreatedAt = restoredCreatedAt.ToLocalTime();
                }

                if (doc.RootElement.TryGetProperty("sessionExpiresAtUtc", out var sessionExpiresElement) &&
                    DateTimeOffset.TryParse(sessionExpiresElement.GetString(), out var restoredExpiresAt))
                {
                    _sessionExpiresAt = restoredExpiresAt.LocalDateTime;
                }
                else if (_sessionCreatedAt != default)
                {
                    _sessionExpiresAt = ResolveSessionExpiry(_sessionCreatedAt);
                }

                PersistedDesktopSession? restoredSession = null;
                if (doc.RootElement.TryGetProperty("protectedSession", out var protectedSessionElement))
                {
                    hadProtectedSession = !string.IsNullOrWhiteSpace(protectedSessionElement.GetString());
                    restoredSession = DesktopSessionProtector.Unprotect(protectedSessionElement.GetString() ?? string.Empty);
                }

                if (restoredSession == null &&
                    doc.RootElement.TryGetProperty("sessionToken", out var tokenElement) &&
                    doc.RootElement.TryGetProperty("sessionPublicKey", out var publicKeyElement) &&
                    doc.RootElement.TryGetProperty("sessionPrivateKey", out var privateKeyElement))
                {
                    var token = tokenElement.GetString();
                    var publicKey = publicKeyElement.GetString();
                    var privateKey = privateKeyElement.GetString();
                    var trusted = doc.RootElement.TryGetProperty("sessionTrusted", out var trustedElement) && trustedElement.GetBoolean();

                    if (!string.IsNullOrWhiteSpace(token) &&
                        !string.IsNullOrWhiteSpace(publicKey) &&
                        !string.IsNullOrWhiteSpace(privateKey))
                    {
                        restoredSession = new PersistedDesktopSession(token, publicKey, privateKey, trusted);
                        migrateLegacySession = true;
                    }
                }

                if (restoredSession != null)
                {
                    _persistedSessionLost = false;
                    restoredFromPrimary = true;
                    _roomManager.RestorePersistedSession(restoredSession);
                }
            }

            if (!restoredFromPrimary && File.Exists(SessionBackupFile))
            {
                var backupProtectedSession = File.ReadAllText(SessionBackupFile);
                if (!string.IsNullOrWhiteSpace(backupProtectedSession))
                {
                    var restoredFromBackup = DesktopSessionProtector.Unprotect(backupProtectedSession);
                    if (restoredFromBackup != null)
                    {
                        recoveredFromBackup = true;
                        _persistedSessionLost = false;
                        _roomManager.RestorePersistedSession(restoredFromBackup);
                    }
                    else if (hadProtectedSession)
                    {
                        _persistedSessionLost = true;
                    }
                }
            }
            else if (hadProtectedSession)
            {
                _persistedSessionLost = true;
            }
        }
        catch
        {
        }

        _launchAtStartup = IsLaunchAtStartupEnabled() || _launchAtStartup;
        SavePathBox.Text = _fileSavePath;
        CloseToTrayToggle.IsChecked = _closeToTray;
        LaunchAtStartupToggle.IsChecked = _launchAtStartup;
        UpdateLanguageSelectorSelection();

        if (migrateLegacySession || recoveredFromBackup)
        {
            PersistSettings();
        }
    }

    private void PersistSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var persistedSession = _roomManager.ExportPersistedSession();
            var payload = JsonSerializer.Serialize(new
            {
                savePath = _fileSavePath,
                serverUrl = ServerUrlBox.Text.Trim(),
                closeToTray = _closeToTray,
                launchAtStartup = _launchAtStartup,
                language = LocalizationService.CurrentLanguageSetting,
                regexFilterMarkdown = _regexFilterMarkdown,
                protectedSession = persistedSession == null ? null : DesktopSessionProtector.Protect(persistedSession),
                sessionCreatedAtUtc = _sessionCreatedAt == default ? null : _sessionCreatedAt.ToUniversalTime().ToString("O"),
                sessionExpiresAtUtc = _sessionExpiresAt == default ? null : _sessionExpiresAt.ToUniversalTime().ToString("O"),
            });
            File.WriteAllText(SettingsFile, payload);

            if (persistedSession == null)
            {
                if (File.Exists(SessionBackupFile))
                {
                    File.Delete(SessionBackupFile);
                }
            }
            else
            {
                File.WriteAllText(SessionBackupFile, DesktopSessionProtector.Protect(persistedSession));
            }
        }
        catch
        {
        }
    }

    private void ShowPersistedSessionLostPrompt()
    {
        SetStatus(L("StatusSavedSessionLost"), false);
        MessageBox.Show(L("SavedSessionLostBody"), L("SavedSessionLostTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        _persistedSessionLost = false;
    }

    private DateTime ResolveSessionExpiry(DateTime fallbackCreatedAt)
    {
        return _roomManager.SessionExpiresAtUtc?.LocalDateTime ?? fallbackCreatedAt.AddDays(_roomManager.SessionMaxAgeDays);
    }

    private bool IsLaunchAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
        return key?.GetValue(StartupRegistryValue) is string existing && !string.IsNullOrWhiteSpace(existing);
    }

    private void ApplyStartupRegistration(bool enabled)
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
        StatusIndicator.Fill = new SolidColorBrush(GetThemeColor(connected ? "Success" : "Danger"));
    }

    private void RefreshStatusFromState()
    {
        if (!CanUseConfiguredServer())
        {
            SetStatus(L("StatusNetworkDisconnected"), false);
            return;
        }

        if (_roomManager.EncryptionReady)
        {
            SetStatus(L("StatusEncrypted"), true);
            return;
        }

        if (_handshakePending || _mobileConnected)
        {
            SetStatus(L("StatusPhoneDetected"), false);
            return;
        }

        if (_roomManager.HasTrustedSession && _roomManager.CurrentToken != null)
        {
            SetStatus(CanUseConfiguredServer()
                ? L("StatusPhoneDisconnectedWaiting")
                : L("StatusPhoneDisconnectedOffline"), false);
            return;
        }

        if (_serverConnected && _roomManager.CurrentToken == null)
        {
            SetStatus(L("StatusPreparingSession"), false);
            return;
        }

        if (_roomManager.CurrentToken != null)
        {
            SetStatus(L("StatusWaitingPhone"), false);
            return;
        }

        SetStatus(L("StatusNotConnected"), false);
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
        if (hwnd == IntPtr.Zero || hwnd == _windowHandle) return;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return;
        _lastExternalForegroundWindow = hwnd;
    }

    private bool TryRestorePreviousExternalWindow()
    {
        if (_lastExternalForegroundWindow == IntPtr.Zero || _lastExternalForegroundWindow == _windowHandle)
        {
            return false;
        }

        if (!IsWindow(_lastExternalForegroundWindow) || !IsWindowVisible(_lastExternalForegroundWindow))
        {
            _lastExternalForegroundWindow = IntPtr.Zero;
            return false;
        }

        return SetForegroundWindow(_lastExternalForegroundWindow);
    }

    private void RestorePreviousExternalWindow()
    {
        _ = TryRestorePreviousExternalWindow();
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

    private void TransferProgressCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TransferProgressCardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = -2,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        AnimateTransferActions(true);
    }

    private void TransferProgressCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TransferProgressCardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (_transferProgressBatch?.Files.All(file => file.Status is "completed" or "cancelled") == true)
        {
            AnimateTransferActions(false);
            return;
        }

        AnimateTransferActions(false);
    }

    private void TransferPause_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch?.Direction != "send")
        {
            return;
        }

        _outgoingTransferPaused = !_outgoingTransferPaused;
        var nextGlyph = _outgoingTransferPaused ? "↻" : "⏸";
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            TransferPauseGlyph.Text = nextGlyph;
            TransferPauseGlyph.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        };
        TransferPauseGlyph.BeginAnimation(OpacityProperty, fadeOut);

        if (!_outgoingTransferPaused)
        {
            _outgoingTransferResumeTcs?.TrySetResult(true);
            _outgoingTransferResumeTcs = null;
        }

        RefreshTransferProgressCard();
    }

    private async void TransferCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch?.Direction != "send")
        {
            return;
        }

        _outgoingTransferCancelRequested = true;
        _outgoingTransferPaused = false;
        _outgoingTransferResumeTcs?.TrySetResult(true);
        _outgoingTransferResumeTcs = null;

        if (!string.IsNullOrEmpty(_activeOutgoingTransferId))
        {
            await _roomManager.SendToMobileAsync(new InputCommand
            {
                Type = "file_abort",
                TransferId = _activeOutgoingTransferId,
            }.Serialize());
        }

        CancelPendingTransferProgress();
    }

    private void TransferExpand_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        _transferProgressBatch.Expanded = !_transferProgressBatch.Expanded;
        RefreshTransferProgressCard();
    }

    private void QrExpand_Click(object sender, RoutedEventArgs e)
    {
        if (!_qrVisible)
        {
            ShowQrArea(true);
            return;
        }

        ShowQrPreviewWindow();
    }

    private void QrContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ShowQrPreviewWindow();
    }

    private void ShowQrPreviewWindow()
    {
        if (QrCodeImage.Source == null)
        {
            return;
        }

        var qrMetaText = string.IsNullOrWhiteSpace(_deviceCode)
            ? QrTimerText.Text
            : $"{QrTimerText.Text}\n{L("DeviceCodeTitle")}: {_deviceCode}";

        var panel = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.Image
                    {
                        Source = QrCodeImage.Source,
                        Width = 640,
                        Height = 640,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true,
                    },
                    new TextBlock
                    {
                        Text = qrMetaText,
                        Margin = new Thickness(0, 18, 0, 0),
                        Foreground = Brushes.Black,
                        FontSize = 16,
                        TextAlignment = TextAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = L("QrHintClickToCollapse"),
                        Margin = new Thickness(0, 10, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(95, 99, 104)),
                        FontSize = 13,
                        TextAlignment = TextAlignment.Center,
                    },
                }
            }
        };

        var preview = new Window
        {
            Owner = this,
            Width = 760,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 8, 10, 14)),
                Padding = new Thickness(24),
                Child = panel,
            }
        };

        preview.MouseLeftButtonDown += (_, _) => preview.Close();
        preview.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                preview.Close();
            }
        };

        preview.ShowDialog();
    }

    private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = L("BrowseDialogDescription"),
            SelectedPath = SavePathBox.Text,
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SavePathBox.Text = dialog.SelectedPath;
        }
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_roomManager.EncryptionReady || !_roomManager.FileTransferEnabled)
        {
            SetStatus(L("StatusFileTransferUnavailable"), false);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("DialogSelectFileTitle"),
            Filter = L("DialogFileFilter"),
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await SendFilesToMobileAsync(dialog.FileNames);
        }
    }

    private async Task SendFilesToMobileAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        _outgoingTransferPaused = false;
        _outgoingTransferCancelRequested = false;
        _activeOutgoingTransferId = null;
        _activeOutgoingUiFileId = null;
        _outgoingTransferResumeTcs = null;

        BeginTransferProgressBatch("send", filePaths.Select(path =>
        {
            var info = new FileInfo(path);
            return (Id: path, Name: info.Name, TotalBytes: info.Exists ? info.Length : 0L);
        }));

        var sentCount = 0;
        foreach (var filePath in filePaths)
        {
            var sent = await SendFileToMobileAsync(filePath, filePath);
            if (!sent)
            {
                return;
            }

            sentCount += 1;
            if (sentCount < filePaths.Count)
            {
                await Task.Delay(40);
            }
        }

        if (sentCount > 1)
        {
            SetStatus(L("StatusFilesSentFormat", sentCount), true);
        }
    }

    private async Task<bool> SendFileToMobileAsync(string filePath, string uiFileId)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (_roomManager.MaxFileMB > 0 && fileInfo.Length > (long)_roomManager.MaxFileMB * 1024 * 1024)
            {
                SetStatus(L("StatusFileTooLargeFormat", _roomManager.MaxFileMB), false);
                CancelPendingTransferProgress();
                return false;
            }

            var transferId = Guid.NewGuid().ToString("N");
            _activeOutgoingTransferId = transferId;
            _activeOutgoingUiFileId = uiFileId;
            UpdateTransferProgress(uiFileId, 0, "active");
            if (!await _roomManager.SendToMobileAsync(new InputCommand
            {
                Type = "file_start",
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                MimeType = GuessMimeType(fileInfo.Extension),
            }.Serialize()))
            {
                SetStatus(L("StatusFileSendFailed"), false);
                CancelPendingTransferProgress();
                return false;
            }

            SetStatus(L("StatusSendingFileFormat", fileInfo.Name), true);

            const int chunkSize = 16 * 1024;
            if (fileInfo.Length == 0)
            {
                if (_outgoingTransferCancelRequested)
                {
                    await _roomManager.SendToMobileAsync(new InputCommand { Type = "file_abort", TransferId = transferId }.Serialize());
                    UpdateTransferProgress(uiFileId, 0, "cancelled");
                    CancelPendingTransferProgress();
                    return false;
                }

                if (!await _roomManager.SendToMobileAsync(new InputCommand
                {
                    Type = "file_chunk",
                    TransferId = transferId,
                    ChunkIndex = 0,
                    ChunkData = string.Empty,
                    IsLast = true,
                }.Serialize()))
                {
                    SetStatus(L("StatusFileSendFailed"), false);
                    CancelPendingTransferProgress();
                    return false;
                }

                UpdateTransferProgress(uiFileId, 0, "completed");
            }
            else
            {
                using var stream = File.OpenRead(filePath);
                var effectiveChunkSize = fileInfo.Length <= 3 * 1024 * 1024 ? 8 * 1024 : chunkSize;
                var interChunkDelayMs = fileInfo.Length <= 3 * 1024 * 1024 ? 12 : 4;
                var buffer = new byte[effectiveChunkSize];
                var chunkIndex = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    if (_outgoingTransferCancelRequested)
                    {
                        await _roomManager.SendToMobileAsync(new InputCommand { Type = "file_abort", TransferId = transferId }.Serialize());
                        UpdateTransferProgress(uiFileId, stream.Position, "cancelled");
                        CancelPendingTransferProgress();
                        return false;
                    }

                    if (_outgoingTransferPaused)
                    {
                        await WaitForOutgoingTransferResumeAsync();
                    }

                    var chunkBytes = bytesRead == buffer.Length ? buffer[..bytesRead] : buffer[..bytesRead];
                    if (!await _roomManager.SendToMobileAsync(new InputCommand
                    {
                        Type = "file_chunk",
                        TransferId = transferId,
                        ChunkIndex = chunkIndex++,
                        ChunkData = Convert.ToBase64String(chunkBytes),
                        IsLast = stream.Position == stream.Length,
                    }.Serialize()))
                    {
                        SetStatus(L("StatusFileSendFailed"), false);
                        CancelPendingTransferProgress();
                        return false;
                    }

                    UpdateTransferProgress(uiFileId, stream.Position, stream.Position == stream.Length ? "completed" : (_outgoingTransferPaused ? "paused" : "active"));

                    await Task.Delay(interChunkDelayMs);
                }
            }

            if (_devMode)
            {
                AppendLog($"File sent to mobile: {fileInfo.FullName}");
            }

            SetStatus(L("StatusFileSentFormat", fileInfo.Name), true);
            _activeOutgoingTransferId = null;
            _activeOutgoingUiFileId = null;
            return true;
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"File send failed: {ex.Message}");
            }

            SetStatus(L("StatusFileSendFailed"), false);
            CancelPendingTransferProgress();
            _activeOutgoingTransferId = null;
            _activeOutgoingUiFileId = null;
            return false;
        }
    }

    private static string GuessMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
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
        Title = L("MainWindowTitle");
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

    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        ShowWindow();
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        ShowWindow();
    }

    private void ShowWindow()
    {
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RequestWindowClose()
    {
        if (_closeToTray)
        {
            EnsureTrayIconCreated();
            Hide();
        }
        else
        {
            ExitApplication();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        CleanupResources();
        Application.Current.Shutdown();
    }

    private void CleanupResources()
    {
        _sessionTimer?.Stop();
        _disconnectTimer?.Stop();
        _receivedFilesRevealTimer?.Stop();
        _trayCreationRetryTimer?.Stop();

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        App.ThemeChanged -= App_ThemeChanged;
        _roomManager.Dispose();
        _trayIcon?.Dispose();
        _appIcon?.Dispose();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown) return;

        e.Cancel = true;
        RequestWindowClose();
    }
}
