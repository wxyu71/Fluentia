using System.IO;
using System.Windows;
using System.Windows.Interop;
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
    private DispatcherTimer? _focusTracker;

    public MainWindow()
    {
        InitializeComponent();
        _roomManager = new RoomManager();
        SetupRoomManagerEvents();
        SetupTrayIcon();
        Closing += MainWindow_Closing;
        // Register our HWND and start tracking the foreground window
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            TextInjector.SetFluentiaHwnd(hwnd);
            _focusTracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _focusTracker.Tick += (_, _) => TextInjector.RecordForegroundWindow();
            _focusTracker.Start();
        };
    }

    private void SetupTrayIcon()
    {
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");

        // Create a 16x16 icon using GDI+
        // System.Drawing.Icon requires ICO format; use GetHicon() on a Bitmap instead
        using var bmp = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            // Purple circle
            g.FillEllipse(
                new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(99, 102, 241)),
                0, 0, 15, 15);
            // "F" letter
            g.DrawString("F",
                new System.Drawing.Font("Segoe UI", 8f, System.Drawing.FontStyle.Bold),
                System.Drawing.Brushes.White,
                new System.Drawing.PointF(1.5f, 1f));
        }

        // GetHicon() returns an unmanaged icon handle; wrap it for proper disposal
        var hIcon = bmp.GetHicon();
        _trayIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
    }

    private void SetupRoomManagerEvents()
    {
        _roomManager.OnRoomCreated += (token) => Dispatcher.Invoke(() =>
        {
            UpdateQRCode();
        });

        _roomManager.OnMobileConnected += (deviceId) => Dispatcher.Invoke(() =>
        {
            SetStatus("Mobile connected", true);
            AppendLog($"Mobile device {deviceId[..Math.Min(8, deviceId.Length)]}... connected");
        });

        _roomManager.OnMobileDisconnected += () => Dispatcher.Invoke(() =>
        {
            SetStatus("Mobile disconnected", false);
            AppendLog("Mobile disconnected");
            // Regenerate QR with new keys
            UpdateQRCode();
        });

        _roomManager.OnEncryptionReady += () => Dispatcher.Invoke(() =>
        {
            SetStatus("E2E encrypted 🔒", true);
            AppendLog("End-to-end encryption established");
        });

        // Text injection must NOT run on the UI thread — we need the target
        // app's window to hold focus, not Fluentia. Run on a thread-pool thread.
        _roomManager.OnInputCommand += (cmd) => Task.Run(() => HandleInputCommand(cmd));

        _roomManager.OnStatusChanged += (status) => Dispatcher.Invoke(() =>
        {
            AppendLog(status);
        });

        _roomManager.OnError += (error) => Dispatcher.Invoke(() =>
        {
            SetStatus($"Error: {error}", false);
            AppendLog($"ERROR: {error}");
        });
    }

    // Called from a thread-pool thread — do NOT touch UI controls directly here
    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "text_commit":
                if (!string.IsNullOrEmpty(cmd.Text))
                {
                    TextInjector.TypeText(cmd.Text);
                    var preview = Truncate(cmd.Text, 40);
                    Dispatcher.BeginInvoke(() => AppendLog($"→ \"{preview}\""));
                }
                break;

            case "backspace":
                if (cmd.Count > 0)
                {
                    TextInjector.SendBackspace(cmd.Count);
                    var n = cmd.Count;
                    Dispatcher.BeginInvoke(() => AppendLog($"← Backspace x{n}"));
                }
                break;

            case "clear":
                Dispatcher.BeginInvoke(() => AppendLog("○ Clear signal received"));
                break;
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
        QrHintText.Text = $"Scan with Fluentia mobile app\nRoom: {_roomManager.CurrentToken?[..8]}...";
    }

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(34, 197, 94)    // green
            : Color.FromRgb(239, 68, 68));  // red
    }

    private void AppendLog(string text)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text += $"[{time}] {text}\n";

        // Keep log size manageable
        var lines = LogText.Text.Split('\n');
        if (lines.Length > 100)
        {
            LogText.Text = string.Join('\n', lines[^50..]);
        }
    }

    private static string Truncate(string text, int maxLen)
    {
        text = text.Replace("\n", "↵").Replace("\r", "");
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
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
            SetStatus("Connected", false); // waiting for mobile
        }
        catch (Exception ex)
        {
            SetStatus("Connection failed", false);
            AppendLog($"Connection error: {ex.Message}");
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            ConnectBtn.Content = "Connect & Create Room";
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

    private void TrayIcon_TrayRightMouseDown(object sender, RoutedEventArgs e)
    {
        // Context menu shows automatically
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _roomManager.Dispose();
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing
        e.Cancel = true;
        Hide();
    }
}
