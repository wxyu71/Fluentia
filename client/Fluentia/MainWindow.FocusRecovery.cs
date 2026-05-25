using System;
using System.Threading;
using System.Threading.Tasks;
using H.NotifyIcon.Core;

namespace Fluentia;

public partial class MainWindow
{
    private void BeginInputTargetRecovery()
    {
        if (_inputTargetRecoveryCts != null || _isShuttingDown)
        {
            return;
        }

        SetStatus(L("StatusInputTargetRecovering"), false);

        var cts = new CancellationTokenSource();
        _inputTargetRecoveryCts = cts;
        _ = RestorePreviousExternalWindowAsync(cts);
    }

    private async Task RestorePreviousExternalWindowAsync(CancellationTokenSource cts)
    {
        try
        {
            var result = await _windowActivationCoordinator.RestorePreviousExternalWindowAsync(
                _windowHandle,
                maxAttempts: 6,
                retryDelay: TimeSpan.FromMilliseconds(250),
                cancellationToken: cts.Token);

            if (result.Restored)
            {
                _inputTargetWindow = result.WindowHandle;
                _manualInputTargetRecoveryNotified = false;
                return;
            }

            NotifyManualInputTargetRecoveryNeeded(result.CandidateInvalid);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_inputTargetRecoveryCts, cts))
            {
                _inputTargetRecoveryCts?.Dispose();
                _inputTargetRecoveryCts = null;
            }
        }
    }

    private void CancelInputTargetRecovery()
    {
        if (_inputTargetRecoveryCts == null)
        {
            return;
        }

        _inputTargetRecoveryCts.Cancel();
        _inputTargetRecoveryCts.Dispose();
        _inputTargetRecoveryCts = null;
    }

    private void NotifyManualInputTargetRecoveryNeeded(bool candidateInvalid)
    {
        _inputTargetWindow = IntPtr.Zero;

        if (_manualInputTargetRecoveryNotified)
        {
            return;
        }

        _manualInputTargetRecoveryNotified = true;
        SetStatus(candidateInvalid ? L("StatusInputTargetManualInvalid") : L("StatusInputTargetManual"), false);

        if (_trayIcon == null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                L("TrayNotificationInputTargetTitle"),
                candidateInvalid ? L("TrayNotificationInputTargetBodyInvalid") : L("TrayNotificationInputTargetBody"),
                NotificationIcon.Warning,
                null,
                true,
                false,
                false,
                false,
                TimeSpan.FromSeconds(6));
        }
        catch
        {
        }
    }
}