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
        _roomManager.OnRoomCreated += (token) => Dispatcher.Invoke(() =>
        {
            UpdateQRCode();
            ShowQrArea(true);
            _qrCreatedAt = DateTime.Now;
            _qrTimer?.Start();
            CopyKeyBtn.Visibility = Visibility.Visible;
        });

        _roomManager.OnMobileConnected += (deviceId) => Dispatcher.Invoke(() =>
        {
            _mobileConnected = true;
            SetStatus("Mobile connected", true);
            // Blur the QR and collapse
            QrBlurOverlay.Visibility = Visibility.Visible;
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
        using var qrCodeData = qrGenerator.CreateQrCode(qrData, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new BitmapByteQRCode(qrCodeData);
        var qrBytes = qrCode.GetGraphic(10, "#000000", "#FFFFFF");

        using var ms = new MemoryStream(qrBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();

        QrCodeImage.Source = bitmap;
        QrHintText.Text = $"Scan with Fluentia mobile\nRoom: {_roomManager.CurrentToken?[..8]}...";
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

    // Copy connection key for manual pairing
    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var qrData = _roomManager.GetQRData();
        if (qrData != null)
        {
            try
            {
                Clipboard.SetText(qrData);
                SetStatus("Connection key copied", true);
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

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _qrTimer?.Stop();
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
