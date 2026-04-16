using System.Collections.Generic;
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
using Fluentia.Views;
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

    // In-progress file transfers: transferId → (header cmd, accumulated chunks)
    private readonly Dictionary<string, (InputCommand Header, List<byte[]> Chunks)> _fileTransfers = new();

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
        var prev = _lastForegroundWindow;
        _lastForegroundWindow = hwnd;

        if (!_mobileConnected || !_roomManager.EncryptionReady) return;

        // When focus moves to a NEW window, notify mobile to pause sync.
        // When focus returns to the previous window (user switched back), notify to resume.
        // We detect "return" by checking if the new window handle is the one we were on before prev.
        // Simplified rule: always send focus_change. Send focus_regained with a short delay
        // after the window settles, so mobile can resume and flush buffered text.
        _ = Task.Run(async () =>
        {
            var cmd = System.Text.Json.JsonSerializer.Serialize(new { type = "focus_change" });
            await _roomManager.SendToMobileAsync(cmd);
            if (_devMode) Dispatcher.BeginInvoke(() => AppendLog($"Focus → new window"));

            // After 400ms of settling, send focus_regained so mobile flushes any buffered diff
            await Task.Delay(400);
            if (_lastForegroundWindow == hwnd) // focus didn't change again
            {
                var regained = System.Text.Json.JsonSerializer.Serialize(new { type = "focus_regained" });
                await _roomManager.SendToMobileAsync(regained);
                if (_devMode) Dispatcher.BeginInvoke(() => AppendLog("Focus settled → resume sync"));
            }
        });
        _ = prev; // suppress unused warning
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
            // Show custom confirmation dialog — not a plain system MessageBox
            Show();
            Activate();
            var dlg = new ConfirmConnectionDialog(verifyId, userAgent) { Owner = this };
            if (dlg.ShowDialog() == true)
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

            case "file_start":
                // Begin accumulating a new file transfer
                if (!string.IsNullOrEmpty(cmd.TransferId))
                {
                    _fileTransfers[cmd.TransferId] = (cmd, new List<byte[]>());
                    if (_devMode) Dispatcher.BeginInvoke(() =>
                        AppendLog($"File transfer started: {cmd.FileName} ({cmd.FileSize} bytes)"));
                }
                break;

            case "file_chunk":
                if (!string.IsNullOrEmpty(cmd.TransferId) && !string.IsNullOrEmpty(cmd.ChunkData)
                    && _fileTransfers.TryGetValue(cmd.TransferId, out var transfer))
                {
                    try { transfer.Chunks.Add(Convert.FromBase64String(cmd.ChunkData)); }
                    catch { break; } // ignore malformed chunk

                    if (cmd.IsLast)
                    {
                        _fileTransfers.Remove(cmd.TransferId);
                        var allBytes = CombineChunks(transfer.Chunks);
                        HandleReceivedFile(transfer.Header, allBytes);
                    }
                }
                break;

            case "file_abort":
                if (!string.IsNullOrEmpty(cmd.TransferId))
                    _fileTransfers.Remove(cmd.TransferId);
                break;
        }
    }

    private static byte[] CombineChunks(List<byte[]> chunks)
    {
        int total = 0;
        foreach (var c in chunks) total += c.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var c in chunks) { Buffer.BlockCopy(c, 0, result, pos, c.Length); pos += c.Length; }
        return result;
    }

    private void HandleReceivedFile(InputCommand header, byte[] data)
    {
        var fileName = SanitizeFileName(header.FileName ?? "received_file");
        var mimeType = header.MimeType ?? "application/octet-stream";

        // Save to ~/Downloads — path traversal is prevented by SanitizeFileName
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var savePath = Path.Combine(userProfile, "Downloads", fileName);
        int suffix = 1;
        while (File.Exists(savePath))
        {
            var ext = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            savePath = Path.Combine(userProfile, "Downloads", $"{name}_{suffix++}{ext}");
        }

        try { File.WriteAllBytes(savePath, data); }
        catch (Exception ex)
        {
            if (_devMode) Dispatcher.BeginInvoke(() => AppendLog($"File save failed: {ex.Message}"));
            return;
        }

        // Images also go to clipboard
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Clipboard.SetImage(bmp);
                }
                catch { /* clipboard busy or unsupported image format */ }
            });
        }

        if (_devMode)
            Dispatcher.BeginInvoke(() => AppendLog(
                $"File received → {savePath}" +
                (mimeType.StartsWith("image/") ? " (copied to clipboard)" : "")));
    }

    private static string SanitizeFileName(string name)
    {
        // Strip path separators and invalid chars — prevents path traversal attacks
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
            if (Array.IndexOf(invalid, ch) < 0) sb.Append(ch);
        var result = sb.ToString().Trim().TrimStart('.');
        return string.IsNullOrEmpty(result) ? "received_file" : result;
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

        // ECCLevel.L = minimum error correction → minimum QR version → fewest alignment patterns.
        // For our ~64-byte compact payload this yields version 4 (29×29) with exactly
        // 3 large finder patterns (corners) and 1 small alignment pattern — exactly as requested.
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(qrData, QRCodeGenerator.ECCLevel.L);

        var modules = qrCodeData.ModuleMatrix;
        int size = modules.Count;

        // Choose module size so the final image fills ~280px
        const int targetPx = 280;
        int moduleSize = Math.Max(4, targetPx / (size + 4)); // 4-module quiet zone
        int margin = moduleSize * 2;                          // 2-module quiet zone each side
        int canvasSize = size * moduleSize;
        int totalSize = canvasSize + margin * 2;

        // Pre-classify each module: finder pattern, alignment pattern, or data
        var (finderSet, alignSet) = ClassifyQrPatterns(modules, size);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Pure white background — scanners need maximum contrast
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, totalSize, totalSize));

            var darkBrush = Brushes.Black; // pure black for all modules

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    if (!modules[row][col]) continue; // white (light) module — skip

                    double x = margin + col * moduleSize;
                    double y = margin + row * moduleSize;
                    double ms = moduleSize;

                    if (finderSet.Contains((row, col)))
                    {
                        // Finder pattern modules: draw as solid square (sharp edges for scanner)
                        dc.DrawRectangle(darkBrush, null, new Rect(x, y, ms, ms));
                    }
                    else if (alignSet.Contains((row, col)))
                    {
                        // Alignment pattern: also solid square
                        dc.DrawRectangle(darkBrush, null, new Rect(x, y, ms, ms));
                    }
                    else
                    {
                        // Data modules: Telegram-style rounded dots
                        double cx = x + ms / 2.0;
                        double cy = y + ms / 2.0;
                        double r = ms * 0.42;
                        dc.DrawEllipse(darkBrush, null, new Point(cx, cy), r, r);
                    }
                }
            }
        }

        var rtb = new RenderTargetBitmap(totalSize, totalSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        QrCodeImage.Source = rtb;
        QrCodeImage.Width = targetPx;
        QrCodeImage.Height = targetPx;
        QrHintText.Text = $"Scan with Fluentia mobile\nRoom: {_roomManager.CurrentToken}";
    }

    /// <summary>
    /// Returns two HashSets: modules that belong to finder patterns and alignment patterns.
    /// Finder patterns occupy the three 7×7 corners (plus 1-module separator ring).
    /// Alignment patterns are the 5×5 blocks defined in the QR spec for version ≥ 2.
    /// </summary>
    private static (HashSet<(int, int)> finders, HashSet<(int, int)> aligns)
        ClassifyQrPatterns(List<System.Collections.BitArray> modules, int size)
    {
        var finders = new HashSet<(int, int)>();
        var aligns = new HashSet<(int, int)>();

        // 3 finder patterns: top-left, top-right, bottom-left (each 7×7 + 1-wide separator)
        void AddFinder(int startRow, int startCol)
        {
            for (int r = startRow - 1; r <= startRow + 7; r++)
                for (int c = startCol - 1; c <= startCol + 7; c++)
                    if (r >= 0 && r < size && c >= 0 && c < size)
                        finders.Add((r, c));
        }
        AddFinder(0, 0);           // top-left
        AddFinder(0, size - 7);    // top-right
        AddFinder(size - 7, 0);    // bottom-left

        // Alignment pattern centers — for version 4 the single center is at (16, 16)
        // We detect them heuristically: a 5×5 block where modules[c][c] is set (center on),
        // bordered by a ring of dark modules. Use ZXing's module matrix to find.
        // Simple approach: every set of 5×5 that looks like a "bullseye" not inside a finder.
        for (int r = 2; r < size - 2; r++)
        {
            for (int c = 2; c < size - 2; c++)
            {
                if (finders.Contains((r, c))) continue;
                // Check for alignment pattern centre pattern: dark–light–dark–light–dark in both axes
                if (modules[r][c] &&
                    modules[r - 2][c - 2] && modules[r - 2][c + 2] &&
                    modules[r + 2][c - 2] && modules[r + 2][c + 2] &&
                    !modules[r - 1][c - 1] && !modules[r - 1][c + 1] &&
                    !modules[r + 1][c - 1] && !modules[r + 1][c + 1])
                {
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                            aligns.Add((r + dr, c + dc));
                }
            }
        }

        return (finders, aligns);
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
