using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly RoomManager _roomManager;
    private readonly Channel<InputCommand> _cmdChannel =
        Channel.CreateUnbounded<InputCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, (InputCommand Header, List<byte[]> Chunks)> _fileTransfers = new();

    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private MenuItem? _trayShowItem;
    private MenuItem? _trayRefreshItem;
    private MenuItem? _trayExitItem;
    private DispatcherTimer? _sessionTimer;
    private DispatcherTimer? _disconnectTimer;

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
    private string? _deviceCode;
    private string _fileSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private IntPtr _inputTargetWindow;
    private IntPtr _windowHandle;

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

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook;
    private IntPtr _lastForegroundWindow;
    private IntPtr _lastExternalForegroundWindow;

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fluentia", "settings.json");

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
        Loaded += async (_, _) => await AutoConnectAsync();
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
            }

            if (!connected && !CanUseConfiguredServer())
            {
                SetStatus(L("StatusNetworkDisconnected"), false);
            }
            else if (connected && _roomManager.CurrentToken == null)
            {
                SetStatus(L("StatusPreparingSession"), false);
            }

            RefreshVisualState();
        });

        _roomManager.OnSessionCreated += (token) => Dispatcher.Invoke(async () =>
        {
            _deviceCode = null;
            _mobileConnected = false;
            _handshakePending = false;
            _qrVisible = true;
            _inputTargetWindow = IntPtr.Zero;
            _sessionCreatedAt = DateTime.Now;
            _sessionExpiresAt = _sessionCreatedAt.AddDays(_roomManager.SessionMaxAgeDays);
            PersistSettings();
            UpdateQRCode();
            UpdateSessionCountdown();
            SetStatus(L("StatusWaitingPhone"), false);
            RefreshVisualState();
            await _roomManager.RequestDeviceCode();
        });

        _roomManager.OnDeviceCodeCreated += (code) => Dispatcher.Invoke(() =>
        {
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
            _sessionCreatedAt = DateTime.Now;
            _sessionExpiresAt = _sessionCreatedAt.AddDays(_roomManager.SessionMaxAgeDays);
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
        const int targetPx = 320;
        int moduleSize = Math.Max(4, targetPx / (size + 4));
        int margin = moduleSize * 2;
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
        await foreach (var cmd in _cmdChannel.Reader.ReadAllAsync())
        {
            try
            {
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

    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "diff":
                if (!EnsureInputTarget()) return;
                TextInjector.ApplyDiff(cmd.Count, cmd.Text ?? string.Empty);
                break;

            case "enter":
                if (!EnsureInputTarget()) return;
                TextInjector.SendEnter();
                _inputTargetWindow = IntPtr.Zero;
                break;

            case "backspace":
                if (!EnsureInputTarget()) return;
                if (cmd.Count > 0)
                {
                    TextInjector.SendBackspace(cmd.Count);
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

            case "clear":
                _inputTargetWindow = IntPtr.Zero;
                break;

            case "file_start":
                if (!string.IsNullOrEmpty(cmd.TransferId))
                {
                    _fileTransfers[cmd.TransferId] = (cmd, new List<byte[]>());
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
                    try
                    {
                        transfer.Chunks.Add(Convert.FromBase64String(cmd.ChunkData));
                    }
                    catch
                    {
                        break;
                    }

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
        _trayIcon.ForceCreate();
        RefreshTrayMenuText();
        SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer());
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
            graphics.Clear(System.Drawing.Color.Transparent);

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

            using var stateBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
            graphics.FillEllipse(stateBrush, 22.8f, 22.2f, 5f, 5f);
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

                if (doc.RootElement.TryGetProperty("sessionCreatedAtUtc", out var sessionCreatedElement) &&
                    DateTime.TryParse(sessionCreatedElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var restoredCreatedAt))
                {
                    _sessionCreatedAt = restoredCreatedAt.ToLocalTime();
                    _sessionExpiresAt = _sessionCreatedAt.AddDays(_roomManager.SessionMaxAgeDays);
                }

                if (doc.RootElement.TryGetProperty("sessionToken", out var tokenElement) &&
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
                        _roomManager.RestorePersistedSession(new PersistedDesktopSession(token, publicKey, privateKey, trusted));
                    }
                }
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
                sessionToken = persistedSession?.Token,
                sessionPublicKey = persistedSession?.PublicKey,
                sessionPrivateKey = persistedSession?.PrivateKey,
                sessionTrusted = persistedSession?.TrustedSessionEstablished ?? false,
                sessionCreatedAtUtc = _sessionCreatedAt == default ? null : _sessionCreatedAt.ToUniversalTime().ToString("O"),
            });
            File.WriteAllText(SettingsFile, payload);
        }
        catch
        {
        }
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

    private void QrExpand_Click(object sender, RoutedEventArgs e)
    {
        ShowQrArea(true);
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
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await SendFileToMobileAsync(dialog.FileName);
        }
    }

    private async Task SendFileToMobileAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (_roomManager.MaxFileMB > 0 && fileInfo.Length > (long)_roomManager.MaxFileMB * 1024 * 1024)
            {
                SetStatus(L("StatusFileTooLargeFormat", _roomManager.MaxFileMB), false);
                return;
            }

            var transferId = Guid.NewGuid().ToString("N");
            await _roomManager.SendToMobileAsync(new InputCommand
            {
                Type = "file_start",
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                MimeType = GuessMimeType(fileInfo.Extension),
            }.Serialize());

            SetStatus(L("StatusSendingFileFormat", fileInfo.Name), true);

            const int chunkSize = 32 * 1024;
            if (fileInfo.Length == 0)
            {
                await _roomManager.SendToMobileAsync(new InputCommand
                {
                    Type = "file_chunk",
                    TransferId = transferId,
                    ChunkIndex = 0,
                    ChunkData = string.Empty,
                    IsLast = true,
                }.Serialize());
            }
            else
            {
                using var stream = File.OpenRead(filePath);
                var buffer = new byte[chunkSize];
                var chunkIndex = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    var chunkBytes = bytesRead == buffer.Length ? buffer[..bytesRead] : buffer[..bytesRead];
                    await _roomManager.SendToMobileAsync(new InputCommand
                    {
                        Type = "file_chunk",
                        TransferId = transferId,
                        ChunkIndex = chunkIndex++,
                        ChunkData = Convert.ToBase64String(chunkBytes),
                        IsLast = stream.Position == stream.Length,
                    }.Serialize());
                }
            }

            if (_devMode)
            {
                AppendLog($"File sent to mobile: {fileInfo.FullName}");
            }

            SetStatus(L("StatusFileSentFormat", fileInfo.Name), true);
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"File send failed: {ex.Message}");
            }

            SetStatus(L("StatusFileSendFailed"), false);
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
        TitleLabel.Text = L("AppName");
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
