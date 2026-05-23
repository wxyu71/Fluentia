using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Fluentia;

public partial class MainWindow
{
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
            QrTimerText.Text = string.Empty;
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

        QrTimerText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
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
                    if (!modules[row][col])
                    {
                        continue;
                    }

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

    private void ShowQrArea(bool show)
    {
        _qrVisible = show;
        RefreshVisualState();
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
}