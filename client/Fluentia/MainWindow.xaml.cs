using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fluentia.Models;
using Fluentia.Services;
using H.NotifyIcon;
using QRCoder;

namespace Fluentia;

public partial class MainWindow : Window
{
    private readonly RoomManager _roomManager;
    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private readonly Channel<InputCommand> _cmdChannel =
        Channel.CreateUnbounded<InputCommand>(new UnboundedChannelOptions { SingleReader = true });

    // QR timer — 5-minute validity
    private DispatcherTimer? _qrTimer;
    private DateTime _qrCreatedAt;
    private const int QR_VALIDITY_SECONDS = 300; // 5 minutes

    // Dev mode — multi-tap title to enable
    private int _devTapCount;
    private DateTime _lastDevTap = DateTime.MinValue;
    private bool _devMode;

    // Mobile connection state for QR collapse/blur
    private bool _mobileConnected;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Foreground window change detection
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook;
    private IntPtr _lastForegroundWindow;

    public MainWindow()
    {
        InitializeComponent();
        _roomManager = new RoomManager();
        SetupRoomManagerEvents();
        SetupTrayIcon();
        SetupQRTimer();
        Closing += MainWindow_Closing;

        // Wire diagnostic logging — only in dev mode
        TextInjector.DiagnosticLog = (msg) =>
        {
            if (_devMode) Dispatcher.BeginInvoke(() => AppendLog(msg));
        };

        Task.Run(ProcessCommandQueue);

        // Show traffic-light icons on hover
        MouseEnter += (_, _) => ShowTrafficIcons(true);
        MouseLeave += (_, _) => ShowTrafficIcons(false);

        // Foreground window change detection — notify mobile to reset diff state
        _lastForegroundWindow = GetForegroundWindow();
        _winEventDelegate = OnForegroundWindowChanged;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == _lastForegroundWindow) return;
        _lastForegroundWindow = hwnd;

        // Notify mobile to reset diff tracking when PC focus changes
        if (_mobileConnected && _roomManager.EncryptionReady)
        {
            var cmd = System.Text.Json.JsonSerializer.Serialize(new { type = "focus_change" });
            _ = _roomManager.SendToMobileAsync(cmd);
            if (_devMode) Dispatcher.BeginInvoke(() => AppendLog("Focus changed → sync reset sent"));
        }
    }

    private void ShowTrafficIcons(bool show)
    {
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        CloseX.Visibility = vis;
        MinLine.Visibility = vis;
        MaxDiamond.Visibility = vis;
    }

    private async Task ProcessCommandQueue()
    {
        await foreach (var cmd in _cmdChannel.Reader.ReadAllAsync())
        {
            try { HandleInputCommand(cmd); }
            catch (Exception ex)
            {
                if (_devMode) Dispatcher.BeginInvoke(() => AppendLog($"Inject error: {ex.Message}"));
            }
        }
    }

    private void SetupTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(
                new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(10, 132, 255)),
                0, 0, 31, 31);
            g.DrawString("F",
                new System.Drawing.Font("Segoe UI", 16f, System.Drawing.FontStyle.Bold),
                System.Drawing.Brushes.White,
                new System.Drawing.PointF(6f, 2f));
        }
        var hIcon = bmp.GetHicon();
        var tempIcon = System.Drawing.Icon.FromHandle(hIcon);
        using var ms = new MemoryStream();
        tempIcon.Save(ms);
        ms.Position = 0;
        _appIcon = new System.Drawing.Icon(ms);
        DestroyIcon(hIcon);

        _trayIcon = new TaskbarIcon
        {
            Icon = _appIcon,
            ToolTipText = "Fluentia",
        };
        _trayIcon.TrayLeftMouseDown += TrayIcon_TrayLeftMouseDown;

        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "Show" };
        showItem.Click += ShowWindow_Click;
        var refreshItem = new MenuItem { Header = "Refresh Room" };
        refreshItem.Click += Refresh_Click;
        var exitItem = new MenuItem { Header = "Quit Fluentia" };
        exitItem.Click += Exit_Click;
        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenu = menu;
        _trayIcon.ForceCreate();
    }

    private void SetupQRTimer()
    {
        _qrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _qrTimer.Tick += QrTimer_Tick;
    }

    private void QrTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.Now - _qrCreatedAt).TotalSeconds;
        var remaining = QR_VALIDITY_SECONDS - (int)elapsed;
        if (remaining <= 0)
        {
            // Refresh QR
            _qrTimer?.Stop();
            _ = _roomManager.RefreshRoom();
            return;
        }
        var min = remaining / 60;
        var sec = remaining % 60;
        QrTimerText.Text = $"{min}:{sec:D2}";
        QrTimerText.Foreground = remaining < 30
            ? new SolidColorBrush(Color.FromRgb(255, 69, 58))
            : (Brush)FindResource("TextSecondaryBrush");
    }

    private void SetupRoomManagerEvents()
    {
        _roomManager.OnRoomCreated += (token) => Dispatcher.Invoke(async () =>
        {
            UpdateQRCode();
            ShowQrArea(true);
            _qrCreatedAt = DateTime.Now;
            _qrTimer?.Start();
            // Request device code for manual pairing
            await _roomManager.RequestDeviceCode();
        });

        _roomManager.OnDeviceCodeCreated += (code) => Dispatcher.Invoke(() =>
        {
            DeviceCodePanel.Visibility = Visibility.Visible;
            DeviceCodeText.Text = code;
            if (_devMode) AppendLog($"Device code: {code}");
        });

        _roomManager.OnDeviceCodePending += (code, verifyId, userAgent) => Dispatcher.Invoke(() =>
        {
            // Show confirmation dialog with verification ID
            Show();
            Activate();
            var result = MessageBox.Show(
                $"A device wants to connect:\n\nDevice: {userAgent}\nVerification ID: {verifyId}\n\nDo you approve this connection?",
                "Fluentia — Connection Request",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
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
            SetStatus("Mobile connected", true);
            // Blur the QR and collapse
            QrBlurOverlay.Visibility = Visibility.Visible;
            SetTrayIconColor(disconnected: false);
            if (_devMode) AppendLog($"Mobile {deviceId[..Math.Min(8, deviceId.Length)]}... connected");
        });

        _roomManager.OnMobileDisconnected += () => Dispatcher.Invoke(() =>
        {
            _mobileConnected = false;
            SetStatus("Mobile disconnected", false);
            QrBlurOverlay.Visibility = Visibility.Collapsed;
            ShowQrArea(true);
            UpdateQRCode();
            _qrCreatedAt = DateTime.Now;
            _qrTimer?.Start();
            // Auto-show window and change tray icon to disconnected state
            SetTrayIconColor(disconnected: true);
            Show();
            Activate();
        });

        _roomManager.OnEncryptionReady += () => Dispatcher.Invoke(() =>
        {
            SetStatus("E2E Encrypted", true);
            // Collapse QR after encryption
            ShowQrArea(false);
            if (_devMode) AppendLog("E2E encryption established");
            Hide();
        });

        _roomManager.OnInputCommand += (cmd) => _cmdChannel.Writer.TryWrite(cmd);

        _roomManager.OnStatusChanged += (status) => Dispatcher.Invoke(() =>
        {
            if (_devMode) AppendLog(status);
        });

        _roomManager.OnError += (error) => Dispatcher.Invoke(() =>
        {
            SetStatus($"Error: {error}", false);
            if (_devMode) AppendLog($"ERROR: {error}");
        });
    }

    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "diff":
                TextInjector.ApplyDiff(cmd.Count, cmd.Text ?? "");
                break;

            case "text_commit":
                if (!string.IsNullOrEmpty(cmd.Text))
                    TextInjector.TypeText(cmd.Text);
                break;

            case "backspace":
                if (cmd.Count > 0)
                    TextInjector.SendBackspace(cmd.Count);
                break;

            case "clipboard":
                if (!string.IsNullOrEmpty(cmd.Text))
                {
                    Dispatcher.Invoke(() =>
                    {
                        try { Clipboard.SetText(cmd.Text); }
                        catch { /* clipboard busy */ }
                    });
                }
                break;

            case "clear":
                break;
        }
    }

    private void ShowQrArea(bool show)
    {
        if (show)
        {
            QrContainer.Visibility = Visibility.Visible;
            QrExpandBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            QrContainer.Visibility = Visibility.Collapsed;
            QrExpandBtn.Visibility = _mobileConnected ? Visibility.Visible : Visibility.Collapsed;
            _qrTimer?.Stop();
        }
    }

    private void UpdateQRCode()
    {
        var qrData = _roomManager.GetQRData();
        if (qrData == null)
        {
            QrHintText.Text = "No room available";
            return;
        }

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(qrData, QRCodeGenerator.ECCLevel.H);

        // Telegram-style QR: render with rounded modules and accent color
        var modules = qrCodeData.ModuleMatrix;
        int size = modules.Count;
        int moduleSize = 8; // px per module
        int canvasSize = size * moduleSize;
        int margin = moduleSize * 2;
        int totalSize = canvasSize + margin * 2;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // White background with rounded corners
            dc.DrawRoundedRectangle(Brushes.White, null,
                new Rect(0, 0, totalSize, totalSize), 16, 16);

            var accentBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            var darkBrush = new SolidColorBrush(Color.FromRgb(30, 30, 32));
            double r = moduleSize * 0.38; // rounded dot radius

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    if (!modules[row][col]) continue;

                    double cx = margin + col * moduleSize + moduleSize / 2.0;
                    double cy = margin + row * moduleSize + moduleSize / 2.0;

                    // Finder patterns (top-left, top-right, bottom-left 7x7 blocks) use dark color
                    bool isFinder = (row < 7 && col < 7) ||
                                    (row < 7 && col >= size - 7) ||
                                    (row >= size - 7 && col < 7);

                    var brush = isFinder ? darkBrush : accentBrush;
                    dc.DrawEllipse(brush, null, new Point(cx, cy), r, r);
                }
            }

            // Center logo area: clear a circle and draw "F" logo
            double logoRadius = totalSize * 0.12;
            double centerX = totalSize / 2.0;
            double centerY = totalSize / 2.0;
            dc.DrawEllipse(Brushes.White, null, new Point(centerX, centerY), logoRadius + 4, logoRadius + 4);
            dc.DrawEllipse(accentBrush, null, new Point(centerX, centerY), logoRadius, logoRadius);

            var ft = new FormattedText("F",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                logoRadius * 1.2,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(centerX - ft.Width / 2, centerY - ft.Height / 2));
        }

        var rtb = new RenderTargetBitmap(totalSize, totalSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        QrCodeImage.Source = rtb;
        QrCodeImage.Width = 280;
        QrCodeImage.Height = 280;
        QrHintText.Text = $"Scan with Fluentia mobile\nRoom: {_roomManager.CurrentToken}";
    }

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(48, 209, 88)
            : Color.FromRgb(255, 69, 58));
    }

    private void AppendLog(string text)
    {
        if (!_devMode) return;
        var time = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text += $"[{time}] {text}\n";
        var lines = LogText.Text.Split('\n');
        if (lines.Length > 100)
            LogText.Text = string.Join('\n', lines[^50..]);
    }

    private static string Truncate(string text, int maxLen)
    {
        text = text.Replace("\n", "↵").Replace("\r", "");
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }

    // ── Title bar interactions ──

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

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }

    private void TrafficClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide(); // minimize to tray
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
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // Dev mode — 5 rapid taps on title
    private void Title_DevTap(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastDevTap).TotalSeconds > 2) _devTapCount = 0;
        _lastDevTap = now;
        _devTapCount++;

        if (_devTapCount >= 5 && !_devMode)
        {
            _devMode = true;
            LogContainer.Visibility = Visibility.Visible;
            AppendLog("Developer mode enabled");
            _devTapCount = 0;
        }
    }

    // QR blur click — reveal QR
    private void QrBlur_Click(object sender, MouseButtonEventArgs e)
    {
        QrBlurOverlay.Visibility = Visibility.Collapsed;
    }

    // QR expand — show collapsed QR
    private void QrExpand_Click(object sender, RoutedEventArgs e)
    {
        ShowQrArea(true);
    }

    // Copy device code to clipboard
    private void DeviceCode_Click(object sender, MouseButtonEventArgs e)
    {
        var code = DeviceCodeText.Text;
        if (!string.IsNullOrEmpty(code))
        {
            try
            {
                Clipboard.SetText(code);
                SetStatus("Device code copied", true);
            }
            catch { /* clipboard busy */ }
        }
    }

    // ── UI Event Handlers ──

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Please enter a server URL", "Fluentia");
            return;
        }

        ConnectBtn.IsEnabled = false;
        ConnectBtn.Content = "Connecting...";
        try
        {
            await _roomManager.ConnectAsync(url);
            SetStatus("Connected", false);
        }
        catch (Exception ex)
        {
            SetStatus("Connection failed", false);
            if (_devMode) AppendLog($"Connection error: {ex.Message}");
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            ConnectBtn.Content = "Connect";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _roomManager.RefreshRoom();
    }

    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SetTrayIconColor(bool disconnected)
    {
        if (_trayIcon == null) return;

        var accentColor = disconnected
            ? System.Drawing.Color.FromArgb(255, 100, 100) // red
            : System.Drawing.Color.FromArgb(10, 132, 255); // accent blue

        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(new System.Drawing.SolidBrush(accentColor), 0, 0, 31, 31);
            g.DrawString("F",
                new System.Drawing.Font("Segoe UI", 16f, System.Drawing.FontStyle.Bold),
                System.Drawing.Brushes.White,
                new System.Drawing.PointF(6f, 2f));
        }
        var hIcon = bmp.GetHicon();
        var icon = System.Drawing.Icon.FromHandle(hIcon);
        using var ms = new MemoryStream();
        icon.Save(ms);
        ms.Position = 0;
        var newIcon = new System.Drawing.Icon(ms);
        DestroyIcon(hIcon);

        _trayIcon.Icon = newIcon;
        _trayIcon.ToolTipText = disconnected ? "Fluentia — Disconnected" : "Fluentia";
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _qrTimer?.Stop();
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        _roomManager.Dispose();
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
