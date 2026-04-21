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
    private bool _qrVisible = true;
    private string? _deviceCode;
    private string _fileSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private IntPtr _inputTargetWindow;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fluentia", "settings.json");

    public MainWindow()
    {
        InitializeComponent();

        _roomManager = new RoomManager();
        SetupRoomManagerEvents();
        SetupTrayIcon();
        SetupSessionTimer();
        LoadSettings();
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
                SetStatus("Network disconnected", false);
            }
            else if (connected && _roomManager.CurrentToken == null)
            {
                SetStatus("Preparing session", false);
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
            UpdateQRCode();
            UpdateSessionCountdown();
            SetStatus("Waiting for a phone", false);
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
            SetStatus("Phone detected", false);
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
            SetStatus("Phone disconnected", false);

            if (_serverConnected && !IsSessionExpired())
            {
                UpdateQRCode();
                await _roomManager.RequestDeviceCode();
            }

            StartDisconnectReminderTimer();
            RefreshVisualState();
        });

        _roomManager.OnEncryptionReady += () => Dispatcher.Invoke(() =>
        {
            _mobileConnected = true;
            _handshakePending = false;
            _deviceCode = null;
            _inputTargetWindow = IntPtr.Zero;
            ShowQrArea(false);
            SetStatus("E2E encrypted", true);
            RefreshVisualState();
            Hide();
        });

        _roomManager.OnInputCommand += (cmd) => _cmdChannel.Writer.TryWrite(cmd);

        _roomManager.OnStatusChanged += (status) => Dispatcher.Invoke(() =>
        {
            if (_devMode) AppendLog(status);
        });

        _roomManager.OnError += (error) => Dispatcher.Invoke(() =>
        {
            _handshakePending = false;
            SetStatus($"Error: {error}", false);
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
            SetStatus("Network disconnected", false);
            RefreshVisualState();
            return;
        }

        ConnectBtn.IsEnabled = false;
        ConnectBtn.Content = "Connecting...";

        try
        {
            await _roomManager.ConnectAsync(url);
            SetStatus("Connecting to server", false);
            PersistSettings();
        }
        catch (Exception ex)
        {
            SetStatus(autoConnect ? "Automatic connection failed" : "Connection failed", false);
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

        ServerUrlBox.IsEnabled = !_serverConnected && !_handshakePending;
        ConnectBtn.IsEnabled = !_serverConnected && !_handshakePending && canUseServer;
        ConnectBtn.Content = _serverConnected ? "Connected" : canUseServer ? "Connect" : "Offline";
        NewSessionBtn.IsEnabled = _serverConnected && !_handshakePending;
        QrExpandBtn.IsEnabled = canPair;

        if (!canUseServer)
        {
            ConnectionHintText.Text = "Reconnect this PC to the network before generating a pairing session.";
        }
        else if (_serverConnected && _roomManager.EncryptionReady)
        {
            ConnectionHintText.Text = "Your phone is paired and ready. Create a new session only when you want to invalidate the current secret.";
        }
        else if (_serverConnected && hasSession)
        {
            ConnectionHintText.Text = $"Session ready. The current pairing secret stays reusable for up to {_roomManager.SessionMaxAgeDays} days.";
        }
        else if (_serverConnected)
        {
            ConnectionHintText.Text = "Connected to the relay. Preparing a reusable pairing session.";
        }
        else
        {
            ConnectionHintText.Text = "Connect to your relay server to prepare a reusable session.";
        }
    }

    private void UpdatePairingSurface()
    {
        DeviceCodePanel.Visibility = Visibility.Collapsed;
        QrContainer.Visibility = Visibility.Collapsed;
        QrExpandBtn.Visibility = Visibility.Collapsed;

        if (!CanUseConfiguredServer())
        {
            ShowPairingNotice("Network disconnected", "Reconnect this PC before showing a QR code or a connection code.");
            return;
        }

        if (!_serverConnected)
        {
            ShowPairingNotice("Server not connected", "Connect to your relay server to generate a secure pairing session.");
            return;
        }

        if (_roomManager.CurrentToken == null)
        {
            ShowPairingNotice("Preparing session", "A secure session is being created. Pairing controls will appear when it is ready.");
            return;
        }

        if (IsSessionExpired() && !_mobileConnected)
        {
            ShowPairingNotice("Session expired", "Create a new session to continue pairing your phone.");
            return;
        }

        if (_roomManager.EncryptionReady)
        {
            ShowPairingNotice("Secure channel ready", "Your phone is connected. Pairing controls stay hidden until you invalidate the current secret.");
            return;
        }

        if (_mobileConnected || _handshakePending)
        {
            ShowPairingNotice("Securing connection", "The phone is completing key exchange. Pairing controls are temporarily hidden to prevent duplicate actions.");
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
            QrTimerText.Text = "Session expired";
            QrTimerText.Foreground = new SolidColorBrush((Color)FindResource("Danger"));
            return;
        }

        QrTimerText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        if (remaining.TotalDays >= 1)
        {
            QrTimerText.Text = $"Valid for {Math.Ceiling(remaining.TotalDays)} days";
        }
        else if (remaining.TotalHours >= 1)
        {
            QrTimerText.Text = $"Valid for {Math.Ceiling(remaining.TotalHours)} hours";
        }
        else
        {
            QrTimerText.Text = $"Valid for {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes";
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
            QrHintText.Text = "No active session";
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
        QrHintText.Text = $"Session {_roomManager.CurrentToken} · reusable for up to {_roomManager.SessionMaxAgeDays} days";
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
        if (currentForeground == IntPtr.Zero) return false;

        var ownHandle = new WindowInteropHelper(this).Handle;
        if (currentForeground == ownHandle) return false;

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
            ToolTipText = "Fluentia",
        };
        _trayIcon.TrayLeftMouseDown += TrayIcon_TrayLeftMouseDown;

        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "Show" };
        showItem.Click += ShowWindow_Click;
        var refreshItem = new MenuItem { Header = "New Session" };
        refreshItem.Click += Refresh_Click;
        var exitItem = new MenuItem { Header = "Quit Fluentia" };
        exitItem.Click += Exit_Click;
        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.ForceCreate();
        SetTrayIconColor(disconnected: !_serverConnected || !CanUseConfiguredServer());
    }

    private void SetTrayIconColor(bool disconnected)
    {
        if (_trayIcon == null) return;

        _appIcon?.Dispose();
        _appIcon = CreateAppIcon(disconnected ? GetThemeColor("Danger") : GetThemeColor("Accent"));
        _trayIcon.Icon = _appIcon;
        _trayIcon.ToolTipText = disconnected ? "Fluentia — Disconnected" : "Fluentia";
    }

    private System.Drawing.Icon CreateAppIcon(Color color)
    {
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bmp))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
            graphics.FillEllipse(brush, 0, 0, 31, 31);
            graphics.DrawString(
                "F",
                new System.Drawing.Font("Segoe UI", 16f, System.Drawing.FontStyle.Bold),
                System.Drawing.Brushes.White,
                new System.Drawing.PointF(6f, 2f));
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
            }
        }
        catch
        {
        }

        _launchAtStartup = IsLaunchAtStartupEnabled() || _launchAtStartup;
        SavePathBox.Text = _fileSavePath;
        CloseToTrayToggle.IsChecked = _closeToTray;
        LaunchAtStartupToggle.IsChecked = _launchAtStartup;
    }

    private void PersistSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var payload = JsonSerializer.Serialize(new
            {
                savePath = _fileSavePath,
                serverUrl = ServerUrlBox.Text.Trim(),
                closeToTray = _closeToTray,
                launchAtStartup = _launchAtStartup,
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

        var duration = TimeSpan.FromMilliseconds(260);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

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
            To = open ? -22 : 0,
            Duration = duration,
            EasingFunction = easing,
        });
        MainContentScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            To = open ? 0.97 : 1,
            Duration = duration,
            EasingFunction = easing,
        });
        MainContentScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            To = open ? 0.97 : 1,
            Duration = duration,
            EasingFunction = easing,
        });
        SettingsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = open ? 0.62 : 0,
            Duration = duration,
            EasingFunction = easing,
        });
    }

    private void UpdateCloseButtonToolTip()
    {
        BtnClose.ToolTip = _closeToTray ? "Hide to notification area" : "Quit Fluentia";
    }

    private Color GetThemeColor(string resourceKey)
    {
        return (Color)FindResource(resourceKey);
    }

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(GetThemeColor(connected ? "Success" : "Danger"));
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
                SetStatus("Network disconnected", false);
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
        if (_devMode)
        {
            _ = Dispatcher.BeginInvoke(() => AppendLog($"Focus -> 0x{hwnd:X}"));
        }

        if (_inputTargetWindow != IntPtr.Zero && hwnd != _inputTargetWindow)
        {
            _ = Dispatcher.BeginInvoke(() => { _ = ResetMobileInputAfterFocusChangeAsync(); });
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            DragMove();
        }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
    }

    private void TrafficClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RequestWindowClose();
    }

    private void TrafficMinimize_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        WindowState = WindowState.Minimized;
    }

    private void TrafficMaximize_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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
            SetStatus("Connection code copied", true);
        }
        catch
        {
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerUrlBox.Text))
        {
            MessageBox.Show("Please enter a server URL.", "Fluentia");
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
            Description = "Select the destination for received files",
            SelectedPath = SavePathBox.Text,
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SavePathBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var path = SavePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SetStatus("Invalid save path", false);
            return;
        }

        _fileSavePath = path;
        _closeToTray = CloseToTrayToggle.IsChecked == true;
        _launchAtStartup = LaunchAtStartupToggle.IsChecked == true;
        ApplyStartupRegistration(_launchAtStartup);
        PersistSettings();
        UpdateCloseButtonToolTip();
        ToggleSettingsPanel(false);
        SetStatus("Settings saved", true);
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
