using System.Runtime.InteropServices;

namespace Fluentia.Services;

/// <summary>
/// Injects keyboard input into the foreground window via Win32 SendInput.
/// No focus management — the caller must ensure Fluentia is hidden (tray mode).
/// </summary>
public static class TextInjector
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Diagnostic logging callback — set by MainWindow.</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    // ── INPUT struct with correct 64-bit layout (40 bytes) ──
    // The Windows INPUT union is as large as MOUSEINPUT (32 bytes on x64).
    // Without padding, KEYBDINPUT-only union would be 24 bytes → wrong array stride.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]  public uint Type;
        [FieldOffset(8)]  public KEYBDINPUT ki;
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

    private static void Inject(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;

        var fg = GetForegroundWindow();
        DiagnosticLog?.Invoke($"[Inject] fg=0x{fg:X}, events={inputs.Length}, structSize={Marshal.SizeOf<INPUT>()}");

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
        {
            int err = Marshal.GetLastWin32Error();
            DiagnosticLog?.Invoke($"[Inject] FAIL sent={sent}/{inputs.Length} err={err}");
        }
    }

    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var list = new List<INPUT>();
        foreach (char c in text)
        {
            if (c == '\n')
            {
                list.Add(MakeVkInput(0x0D, false));
                list.Add(MakeVkInput(0x0D, true));
                continue;
            }

            list.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE }
            });
            list.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        Inject(list.ToArray());
    }

    public static void SendBackspace(int count)
    {
        if (count <= 0) return;

        var list = new List<INPUT>(count * 2);
        for (int i = 0; i < count; i++)
        {
            list.Add(MakeVkInput(VK_BACK, false));
            list.Add(MakeVkInput(VK_BACK, true));
        }

        Inject(list.ToArray());
    }

    private static INPUT MakeVkInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u }
        };
    }
}
