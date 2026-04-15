using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private System.Drawing.Icon? _appIcon; // prevent GC of the icon handle
    private readonly object _injectLock = new(); // serialize all injection ops

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public MainWindow()
    {
        InitializeComponent();
        _roomManager = new RoomManager();
        SetupRoomManagerEvents();
        SetupTrayIcon();
        Closing += MainWindow_Closing;

        // Wire diagnostic logging from TextInjector → our log pane
        TextInjector.DiagnosticLog = (msg) => Dispatcher.BeginInvoke(() => AppendLog(msg));
    }

    private void SetupTrayIcon()
    {
        // Build a 32×32 icon in memory → save as ICO → reload as proper Icon
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(
                new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(99, 102, 241)),
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

        // Create TaskbarIcon entirely in code (XAML resource approach doesn't
        // reliably register with the shell notification area on all Windows versions).
        _trayIcon = new TaskbarIcon
        {
            Icon = _appIcon,
            ToolTipText = "Fluentia - Voice Input Relay",
        };
        _trayIcon.TrayLeftMouseDown += TrayIcon_TrayLeftMouseDown;

        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "Show" };
        showItem.Click += ShowWindow_Click;
        var refreshItem = new MenuItem { Header = "Refresh Room" };
        refreshItem.Click += Refresh_Click;
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += Exit_Click;
        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenu = menu;

        _trayIcon.ForceCreate();
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
            AppendLog("End-to-end encryption established — hiding to tray");
            // Auto-hide so Fluentia never holds focus during input relay
            Hide();
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

    // Called from a thread-pool thread — do NOT touch UI controls directly here.
    // Uses lock to serialize commands so rapid diffs don't interleave.
    private void HandleInputCommand(InputCommand cmd)
    {
        lock (_injectLock)
        {
            switch (cmd.Type)
            {
                case "diff":
                    // Atomic diff: backspace + insert in one SendInput call
                    TextInjector.ApplyDiff(cmd.Count, cmd.Text ?? "");
                    var bs = cmd.Count;
                    var ins = cmd.Text ?? "";
                    var preview = Truncate(ins, 30);
                    Dispatcher.BeginInvoke(() => AppendLog($"⇄ diff bs={bs} ins=\"{preview}\""));
                    break;

                case "text_commit":
                    if (!string.IsNullOrEmpty(cmd.Text))
                    {
                        TextInjector.TypeText(cmd.Text);
                        var p = Truncate(cmd.Text, 40);
                        Dispatcher.BeginInvoke(() => AppendLog($"→ \"{p}\""));
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
