using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fluentia.Models;
using Fluentia.Services;
using H.NotifyIcon;
using QRCoder;

namespace Fluentia;

public partial class MainWindow : Window
{
    private readonly RoomManager _roomManager;
    private TaskbarIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _roomManager = new RoomManager();
        SetupRoomManagerEvents();
        SetupTrayIcon();
        Closing += MainWindow_Closing;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");

        // Generate a simple icon programmatically
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(99, 102, 241)),
                null,
                new System.Windows.Point(8, 8), 8, 8);
            ctx.DrawText(
                new FormattedText("F",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    10, Brushes.White,
                    VisualTreeHelper.GetDpi(visual).PixelsPerDip),
                new System.Windows.Point(4, 1));
        }

        var bmp = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(ms);
        ms.Seek(0, SeekOrigin.Begin);

        var icon = new System.Drawing.Icon(ms);
        _trayIcon.Icon = icon;
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

        _roomManager.OnInputCommand += (cmd) => Dispatcher.Invoke(() =>
        {
            HandleInputCommand(cmd);
        });

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

    private void HandleInputCommand(InputCommand cmd)
    {
        switch (cmd.Type)
        {
            case "text_commit":
                if (!string.IsNullOrEmpty(cmd.Text))
                {
                    TextInjector.TypeText(cmd.Text);
                    AppendLog($"→ \"{Truncate(cmd.Text, 40)}\"");
                }
                break;

            case "backspace":
                if (cmd.Count > 0)
                {
                    TextInjector.SendBackspace(cmd.Count);
                    AppendLog($"← Backspace x{cmd.Count}");
                }
                break;

            case "clear":
                AppendLog("○ Clear signal received");
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
        var qrBytes = qrCode.GetGraphic(10, "#FFFFFF", "#12121F");

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
