using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Fluentia;

public partial class MainWindow
{
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
        _desktopBlePairingService.Dispose();
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