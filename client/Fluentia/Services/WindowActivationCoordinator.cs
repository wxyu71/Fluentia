using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentia.Services;

public sealed record WindowRestoreResult(bool Restored, bool CandidateInvalid, IntPtr WindowHandle);

public sealed class WindowActivationCoordinator
{
    private readonly Func<IntPtr> _getForegroundWindow;
    private readonly Func<IntPtr, bool> _setForegroundWindow;
    private readonly Func<IntPtr, bool> _isWindow;
    private readonly Func<IntPtr, bool> _isWindowVisible;
    private readonly Action<string>? _log;

    public WindowActivationCoordinator(
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr, bool> setForegroundWindow,
        Func<IntPtr, bool> isWindow,
        Func<IntPtr, bool> isWindowVisible,
        Action<string>? log = null)
    {
        _getForegroundWindow = getForegroundWindow;
        _setForegroundWindow = setForegroundWindow;
        _isWindow = isWindow;
        _isWindowVisible = isWindowVisible;
        _log = log;
    }

    public IntPtr LastExternalWindow { get; private set; }

    public void RememberExternalWindow(IntPtr hwnd, IntPtr selfWindow)
    {
        if (!IsValidCandidate(hwnd, selfWindow))
        {
            return;
        }

        LastExternalWindow = hwnd;
    }

    public bool TryRestoreImmediately(IntPtr selfWindow)
    {
        if (!IsValidCandidate(LastExternalWindow, selfWindow))
        {
            LastExternalWindow = IntPtr.Zero;
            return false;
        }

        return _setForegroundWindow(LastExternalWindow);
    }

    public async Task<WindowRestoreResult> RestorePreviousExternalWindowAsync(
        IntPtr selfWindow,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        if (!IsValidCandidate(LastExternalWindow, selfWindow))
        {
            LastExternalWindow = IntPtr.Zero;
            return new WindowRestoreResult(false, true, IntPtr.Zero);
        }

        var candidate = LastExternalWindow;

        for (var attempt = 1; attempt <= maxAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_setForegroundWindow(candidate))
            {
                _log?.Invoke($"Restored target window 0x{candidate:X} on attempt {attempt}.");
                return new WindowRestoreResult(true, false, candidate);
            }

            var currentForeground = _getForegroundWindow();
            if (IsValidCandidate(currentForeground, selfWindow))
            {
                LastExternalWindow = currentForeground;
                _log?.Invoke($"Foreground recovered to 0x{currentForeground:X} while retrying.");
                return new WindowRestoreResult(true, false, currentForeground);
            }

            if (!IsValidCandidate(candidate, selfWindow))
            {
                LastExternalWindow = IntPtr.Zero;
                return new WindowRestoreResult(false, true, IntPtr.Zero);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        _log?.Invoke($"Failed to restore target window 0x{candidate:X} after {maxAttempts} attempts.");
        return new WindowRestoreResult(false, false, IntPtr.Zero);
    }

    private bool IsValidCandidate(IntPtr hwnd, IntPtr selfWindow)
    {
        if (hwnd == IntPtr.Zero || hwnd == selfWindow)
        {
            return false;
        }

        return _isWindow(hwnd) && _isWindowVisible(hwnd);
    }
}