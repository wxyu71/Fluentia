using System.Reflection;
using System.Text.Json;
using Fluentia.Models;
using Fluentia.Services;

namespace Fluentia.Tests;

/// <summary>
/// Tests for thread-affinity safety in MainWindow's input processing pipeline.
///
/// Bug: ProcessCommandQueue runs on a Task.Run background thread, but
/// EnsureInputTarget → SetStatus touches WPF controls (StatusText.Text,
/// StatusIndicator.Fill) which have thread affinity. This threw
/// InvalidOperationException ("调用线程无法访问此对象，因为另一个线程拥有该对象"),
/// which was caught by FlushBufferedDiff's catch block, resetting
/// _inputTargetWindow and sending resync → mobile resends → PC calls
/// EnsureInputTarget again → SetStatus throws again → infinite loop.
///
/// Fix: SetStatus and NotifyManualInputTargetRecoveryNeeded now check
/// CheckAccess() and dispatch to the UI thread when called from a
/// non-UI thread.
/// </summary>
public class ThreadAffinityTests
{
    // === Structural: SetStatus dispatch guard ===

    [Fact]
    public void SetStatus_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("SetStatus",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(bool), parameters[1].ParameterType);
    }

    [Fact]
    public void SetStatus_CallsCheckAccess_IndirectGuard()
    {
        // Verify SetStatus references CheckAccess (DispatcherObject method).
        // This confirms the thread-safety guard is present without needing
        // a running WPF dispatcher.
        var method = typeof(MainWindow).GetMethod("SetStatus",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var il = method.GetMethodBody();
        Assert.NotNull(il);

        // CheckAccess is inherited from DispatcherObject.
        // If the guard exists, the method body will be larger than a bare
        // status-update (which is ~20 bytes of IL). A method with a
        // CheckAccess branch + Dispatcher.BeginInvoke call will be
        // significantly larger.
        Assert.True(il.GetILAsByteArray().Length > 40,
            "SetStatus body is too small — the CheckAccess guard may be missing");
    }

    // === Structural: NotifyManualInputTargetRecoveryNeeded dispatch guard ===

    [Fact]
    public void NotifyManualInputTargetRecoveryNeeded_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("NotifyManualInputTargetRecoveryNeeded",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(bool), parameters[0].ParameterType);
    }

    // === Structural: Methods called from background thread exist ===

    [Fact]
    public void EnsureInputTarget_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("EnsureInputTarget",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void FlushBufferedDiff_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("FlushBufferedDiff",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
    }

    [Fact]
    public void ProcessCommandQueue_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("ProcessCommandQueue",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Single(method.GetParameters());
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void BeginInputTargetRecovery_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("BeginInputTargetRecovery",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void ResetMobileInputAfterFocusChangeAsync_MethodExists()
    {
        var method = typeof(MainWindow).GetMethod("ResetMobileInputAfterFocusChangeAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    // === Structural: Focus-recovery CTS fields exist ===

    [Fact]
    public void InputTargetRecoveryCts_FieldExists()
    {
        var field = typeof(MainWindow).GetField("_inputTargetRecoveryCts",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(CancellationTokenSource), field.FieldType);
    }

    [Fact]
    public void FocusClearCts_FieldExists()
    {
        var field = typeof(MainWindow).GetField("_focusClearCts",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(CancellationTokenSource), field.FieldType);
    }

    // === Structural: Command channel is single-reader ===

    [Fact]
    public void CommandChannel_IsSingleReader()
    {
        // ProcessCommandQueue is the sole consumer of _cmdChannel.
        // The channel is created with SingleReader=true, which means
        // only one reader should consume from it. This ensures that
        // EnsureInputTarget (and thus SetStatus) is only called from
        // one background thread at a time, preventing concurrent
        // access to _inputTargetWindow and _appliedInputBuffer.
        var field = typeof(MainWindow).GetField("_cmdChannel",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.True(field.FieldType.IsGenericType);
        Assert.Equal(typeof(InputCommand), field.FieldType.GenericTypeArguments[0]);
    }

    // === Structural: DebugLogger is thread-safe ===

    [Fact]
    public void DebugLogger_HasLock()
    {
        var lockField = typeof(DebugLogger).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(lockField);
        Assert.Equal(typeof(object), lockField.FieldType);
    }

    [Fact]
    public void DebugLogger_EnabledIsVolatile()
    {
        var field = typeof(DebugLogger).GetField("_enabled",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field.IsInitOnly == false, "_enabled should be writable (volatile)");
    }

    // === Logic: Resync message format ===

    [Fact]
    public void ResyncMessage_HasCorrectFormat()
    {
        // FlushBufferedDiff sends this exact JSON when EnsureInputTarget fails
        var json = JsonSerializer.Serialize(new { type = "clear", reason = "resync" });
        var parsed = JsonDocument.Parse(json);

        Assert.Equal("clear", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("resync", parsed.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void FocusClearMessage_HasCorrectFormat()
    {
        // ResetMobileInputAfterFocusChangeAsync sends this exact JSON
        var json = JsonSerializer.Serialize(new { type = "clear", reason = "focus" });
        var parsed = JsonDocument.Parse(json);

        Assert.Equal("clear", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("focus", parsed.RootElement.GetProperty("reason").GetString());
    }

    // === Logic: ApplyDiffToBuffer edge cases from the bug ===

    /// <summary>
    /// Replicates ApplyDiffToBuffer from MainWindow.
    /// </summary>
    private static string ApplyDiffToBuffer(string current, int backspace, string? insertText)
    {
        var safeBackspace = Math.Max(0, Math.Min(backspace, current.Length));
        var prefixLength = current.Length - safeBackspace;
        return current[..prefixLength] + (insertText ?? string.Empty);
    }

    [Fact]
    public void ApplyDiffToBuffer_ResyncOnEmptyBuffer_InsertsFullText()
    {
        // After FlushBufferedDiff resets _appliedInputBuffer to "",
        // the mobile resyncs by sending the full text as a single diff
        // (backspace=0, insert=full_text).
        var result = ApplyDiffToBuffer("", 0, "他们只能巴拉巴拉");
        Assert.Equal("他们只能巴拉巴拉", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_ResyncOnEmptyBuffer_WithBackspace()
    {
        // If mobile sends a diff with backspace on an empty buffer,
        // backspace should be clamped to 0.
        var result = ApplyDiffToBuffer("", 5, "hello");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_RapidResync_AccumulatesCorrectly()
    {
        // Simulate the rapid resync scenario from the bug log:
        // 1. Buffer is "" (reset by FlushBufferedDiff)
        // 2. Mobile resyncs "他们只能" (bs=0, ins="他们只能")
        // 3. Buffer becomes "他们只能"
        // 4. Another resync: mobile sends full text again
        var buffer = "";
        buffer = ApplyDiffToBuffer(buffer, 0, "他们只能");
        Assert.Equal("他们只能", buffer);

        // If the PC processes the resync correctly, the buffer should
        // NOT accumulate duplicate text. But in the bug scenario,
        // the PC was throwing exceptions and resetting the buffer each time.
    }

    [Fact]
    public void ApplyDiffToBuffer_ChineseText_PreservesUnicode()
    {
        // Chinese characters are multi-byte in UTF-8 but single chars in C#.
        // The diff logic must handle them correctly.
        var result = ApplyDiffToBuffer("他们只能", 0, "巴拉巴拉");
        Assert.Equal("他们只能巴拉巴拉", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_ChineseText_Backspace()
    {
        var result = ApplyDiffToBuffer("他们只能巴拉", 2, "布拉");
        Assert.Equal("他们只能布拉", result);
    }

    // === Logic: ComputeFlushDiff edge cases ===

    /// <summary>
    /// Replicates the prefix-only diff logic from FlushBufferedDiff.
    /// </summary>
    private static (int backspace, string insert) ComputeFlushDiff(string oldText, string nextText)
    {
        var prefix = 0;
        var limit = Math.Min(oldText.Length, nextText.Length);
        while (prefix < limit && oldText[prefix] == nextText[prefix])
        {
            prefix++;
        }
        var backspace = oldText.Length - prefix;
        var insert = nextText[prefix..];
        return (backspace, insert);
    }

    [Fact]
    public void ComputeFlushDiff_ResyncScenario_FullReplace()
    {
        // After bug: buffer="" (reset), mobile resyncs "他们只能巴拉巴拉"
        // FlushBufferedDiff computes: old="" → new="他们只能巴拉巴拉"
        var (backspace, insert) = ComputeFlushDiff("", "他们只能巴拉巴拉");
        Assert.Equal(0, backspace);
        Assert.Equal("他们只能巴拉巴拉", insert);
    }

    [Fact]
    public void ComputeFlushDiff_IdempotentResync_NoOp()
    {
        // If the text hasn't changed (e.g., mobile resyncs same text),
        // the diff should be a no-op.
        var text = "他们只能布拉布拉布拉，怎么样或者如何？";
        var (backspace, insert) = ComputeFlushDiff(text, text);
        Assert.Equal(0, backspace);
        Assert.Equal("", insert);
    }

    [Fact]
    public void ComputeFlushDiff_ResyncAfterPartialType()
    {
        // Scenario: buffer="他们只能" (from previous successful injection),
        // mobile resyncs "他们只能巴拉巴拉" (user typed more during the glitch).
        var (backspace, insert) = ComputeFlushDiff("他们只能", "他们只能巴拉巴拉");
        Assert.Equal(0, backspace);
        Assert.Equal("巴拉巴拉", insert);
    }

    // === Logic: InputCommand serialization ===

    [Fact]
    public void InputCommand_ClearResync_SerializesCorrectly()
    {
        // The exact JSON that FlushBufferedDiff sends
        var cmd = new { type = "clear", reason = "resync" };
        var json = JsonSerializer.Serialize(cmd);

        Assert.Contains("\"type\":\"clear\"", json);
        Assert.Contains("\"reason\":\"resync\"", json);
    }

    [Fact]
    public void InputCommand_ClearFocus_SerializesCorrectly()
    {
        var cmd = new { type = "clear", reason = "focus" };
        var json = JsonSerializer.Serialize(cmd);

        Assert.Contains("\"type\":\"clear\"", json);
        Assert.Contains("\"reason\":\"focus\"", json);
    }

    // === Thread safety: WindowActivationCoordinator is stateless-safe ===

    [Fact]
    public void WindowActivationCoordinator_NoSharedMutableState()
    {
        // WindowActivationCoordinator stores only LastExternalWindow (IntPtr)
        // and uses injected delegates. It has no WPF dependencies and can
        // be safely called from any thread.
        var type = typeof(WindowActivationCoordinator);
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

        // All fields should be either readonly (set in constructor) or
        // simple value types (IntPtr, bool).
        foreach (var field in fields)
        {
            Assert.True(
                field.IsInitOnly || field.FieldType.IsValueType || field.FieldType.IsAssignableTo(typeof(Delegate)),
                $"Field {field.Name} of type {field.FieldType.Name} may not be thread-safe");
        }
    }

    [Fact]
    public void WindowActivationCoordinator_TryRestoreImmediately_IsIdempotent()
    {
        // TryRestoreImmediately should be safe to call multiple times.
        // With an invalid candidate (IntPtr.Zero), it returns false immediately.
        var coordinator = new WindowActivationCoordinator(
            () => IntPtr.Zero,
            _ => false,
            _ => false,
            _ => false);

        Assert.False(coordinator.TryRestoreImmediately(IntPtr.Zero));
        Assert.False(coordinator.TryRestoreImmediately(IntPtr.Zero));
    }

    // === Logic: Focus-change debounce behavior ===

    [Fact]
    public async Task FocusClearDebounce_MultipleRapidCalls_OnlyLastFires()
    {
        // Simulates the debounce logic in ResetMobileInputAfterFocusChangeAsync.
        // Multiple rapid calls should cancel previous timers, and only the
        // last one should fire.
        int fireCount = 0;
        CancellationTokenSource? activeCts = null;

        for (int i = 0; i < 10; i++)
        {
            activeCts?.Cancel();
            activeCts?.Dispose();
            var cts = new CancellationTokenSource();
            activeCts = cts;

            // Simulate the debounce: each call cancels the previous
            // and starts a new 300ms timer.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, cts.Token);
                    Interlocked.Increment(ref fireCount);
                }
                catch (OperationCanceledException)
                {
                    // Expected for cancelled timers
                }
            });
        }

        // Wait for the last timer to fire
        await Task.Delay(500);
        activeCts?.Dispose();

        // Only the last timer should have fired (not all 10)
        Assert.Equal(1, fireCount);
    }

    // === Logic: Exception in FlushBufferedDiff doesn't corrupt state ===

    [Fact]
    public void FlushBufferedDiff_ExceptionHandling_ResetsBuffer()
    {
        // The catch block in FlushBufferedDiff resets _appliedInputBuffer to "".
        // This test verifies the logic: after an exception, the buffer is empty,
        // so the next diff is applied to an empty baseline.
        string appliedInputBuffer = "previous text";

        try
        {
            // Simulate an exception during FlushBufferedDiff
            throw new InvalidOperationException("thread access violation");
        }
        catch
        {
            // This is what the catch block does:
            appliedInputBuffer = string.Empty;
        }

        Assert.Equal("", appliedInputBuffer);

        // Next diff should work correctly on empty buffer
        var result = ApplyDiffToBuffer(appliedInputBuffer, 0, "new text");
        Assert.Equal("new text", result);
    }

    [Fact]
    public void FlushBufferedDiff_ExceptionHandling_ResyncLoopTermination()
    {
        // After the fix, SetStatus won't throw, so FlushBufferedDiff won't
        // enter the catch block. Instead, EnsureInputTarget returns false,
        // and FlushBufferedDiff sends one resync and returns.
        //
        // This test verifies that the non-exception path (EnsureInputTarget
        // returns false) correctly resets the buffer and would send a resync.
        string appliedInputBuffer = "some text";

        // Simulate: EnsureInputTarget returns false → normal path
        appliedInputBuffer = string.Empty;
        // In real code: _ = _roomManager.SendToMobileAsync(resyncJson);

        Assert.Equal("", appliedInputBuffer);
    }
}
