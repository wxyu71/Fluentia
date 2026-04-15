using System.Runtime.InteropServices;

namespace Fluentia.Services;

public static class TextInjector
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
    /// </summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // For surrogate pairs, we need to handle them carefully
        foreach (var c in text)
        {
            if (c == '\n')
            {
                // Send Enter key
                SendKey(0x0D, false);
                continue;
            }

            var inputs = new INPUT[2];
            int size = Marshal.SizeOf<INPUT>();

            // Key down
            inputs[0].Type = INPUT_KEYBOARD;
            inputs[0].U.ki.wScan = (ushort)c;
            inputs[0].U.ki.dwFlags = KEYEVENTF_UNICODE;

            // Key up
            inputs[1].Type = INPUT_KEYBOARD;
            inputs[1].U.ki.wScan = (ushort)c;
            inputs[1].U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

            SendInput(2, inputs, size);
        }
    }

    /// <summary>
    /// Sends the specified number of Backspace key presses.
    /// </summary>
    public static void SendBackspace(int count)
    {
        if (count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            SendKey(VK_BACK, false);
        }
    }

    private static void SendKey(ushort vk, bool extended)
    {
        var inputs = new INPUT[2];
        int size = Marshal.SizeOf<INPUT>();

        inputs[0].Type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = vk;

        inputs[1].Type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = vk;
        inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(2, inputs, size);
    }
}
