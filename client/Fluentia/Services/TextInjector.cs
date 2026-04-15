using System.Runtime.InteropServices;
using System.Threading;

namespace Fluentia.Services;

public static class TextInjector
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // The HWND of Fluentia's own main window — set on startup
    private static IntPtr _fluentiaHwnd = IntPtr.Zero;
    // Last known non-Fluentia foreground window
    private static IntPtr _targetHwnd = IntPtr.Zero;

    public static void SetFluentiaHwnd(IntPtr hwnd) => _fluentiaHwnd = hwnd;

    /// <summary>
    /// Call this whenever any window gets focus; we remember the last non-Fluentia one.
    /// </summary>
    public static void RecordForegroundWindow()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _fluentiaHwnd)
            _targetHwnd = fg;
    }

    /// <summary>
    /// Sends keyboard inputs to the currently focused window.
    /// If Fluentia itself has focus (shouldn't happen in tray mode), attempt to
    /// restore the last known target window before injecting.
    /// </summary>
    private static void RestoreFocusAndInject(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;

        var fg = GetForegroundWindow();

        // Normal case: some other app has focus → just inject directly
        if (fg != _fluentiaHwnd || _targetHwnd == IntPtr.Zero)
        {
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return;
        }

        // Edge case: Fluentia somehow has focus → try to restore the real target
        // Minimise our own window first to help SetForegroundWindow succeed
        ShowWindow(_fluentiaHwnd, SW_MINIMIZE);
        Thread.Sleep(50);
        SetForegroundWindow(_targetHwnd);
        Thread.Sleep(50);

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_MINIMIZE = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_BACK = 0x08;

    /// <summary>
    /// Types a Unicode string by simulating keyboard input at the current cursor position.
    /// Restores focus to the last known target window before sending.
    /// </summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var list = new List<INPUT>();
        int size = Marshal.SizeOf<INPUT>();

        foreach (var c in text)
        {
            if (c == '\n')
            {
                // VK_RETURN key down + up
                list.Add(MakeVkInput(0x0D, false));
                list.Add(MakeVkInput(0x0D, true));
                continue;
            }

            // Unicode key down
            list.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                U = { ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE } }
            });
            // Unicode key up
            list.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                U = { ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            });
        }

        if (list.Count > 0)
            RestoreFocusAndInject(list.ToArray());
    }

    /// <summary>
    /// Sends the specified number of Backspace key presses.
    /// </summary>
    public static void SendBackspace(int count)
    {
        if (count <= 0) return;

        var list = new List<INPUT>(count * 2);
        for (int i = 0; i < count; i++)
        {
            list.Add(MakeVkInput(VK_BACK, false));
            list.Add(MakeVkInput(VK_BACK, true));
        }

        RestoreFocusAndInject(list.ToArray());
    }

    private static INPUT MakeVkInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u } }
        };
    }
}
