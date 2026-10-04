using System;
using System.Runtime.InteropServices;

namespace Paster.Interop;

internal static class Win32
{
    // ---- hotkeys -------------------------------------------------------

    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLOSE = 0x0010;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- focus ---------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>
    /// True while any of Ctrl, Shift, Alt or Win is physically held. The hotkey
    /// modifiers are usually still down when the user hits their digit, and they
    /// would otherwise contaminate the synthetic Ctrl+V.
    /// </summary>
    public static bool ModifiersHeld()
    {
        foreach (var vk in new[] { VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;

        return false;
    }

    // ---- window styles -------------------------------------------------

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    // ---- synthetic input ----------------------------------------------

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_V = 0x56;
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_UP = 0x26;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_PRIOR = 0x21;
    public const ushort VK_NEXT = 0x22;
    public const ushort VK_END = 0x23;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_INSERT = 0x2D;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_ESCAPE = 0x1B;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Builds one key event. Both the scan code and the extended-key flag are
    /// filled in: a bare virtual key with wScan 0 is enough for ordinary
    /// characters, but targets that read the scan code (and the arrow keys,
    /// which are extended) will not see the event as a real keypress.
    /// </summary>
    private static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags
                }
            }
        };
    }

    private static bool IsExtended(ushort vk) => vk is
        VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN or
        VK_HOME or VK_END or VK_INSERT or VK_DELETE or VK_PRIOR or VK_NEXT;

    /// <summary>Sends Ctrl+V as one atomic input batch.</summary>
    public static void SendCtrlV() => Send(
        Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));

    /// <summary>Presses and releases a key, n times, in one batch.</summary>
    public static void SendKeyTaps(ushort vk, int count)
    {
        if (count <= 0) return;

        var inputs = new INPUT[count * 2];
        for (var n = 0; n < count; n++)
        {
            inputs[n * 2] = Key(vk, false);
            inputs[n * 2 + 1] = Key(vk, true);
        }

        Send(inputs);
    }

    /// <summary>
    /// Holds or releases Shift. VK_LSHIFT rather than the generic VK_SHIFT:
    /// synthesised generic modifiers are not reliably reflected in the key
    /// state a target reads, which leaves arrow keys moving the caret instead
    /// of extending a selection. The caller must space this from the keys it
    /// modifies - batching them together is what makes it look ignored.
    /// </summary>
    public static void SendShift(bool down) => Send(Key(VK_LSHIFT, !down));

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    /// <summary>
    /// Pulls a window back to the foreground. SetForegroundWindow alone is
    /// refused unless the caller owns the foreground, so fall back to briefly
    /// attaching to the target's input queue.
    /// </summary>
    public static void Focus(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        if (GetForegroundWindow() == hWnd) return;

        if (SetForegroundWindow(hWnd)) return;

        var target = GetWindowThreadProcessId(hWnd, out _);
        var self = GetCurrentThreadId();
        if (target == 0 || target == self) return;

        if (!AttachThreadInput(self, target, true)) return;
        try
        {
            SetForegroundWindow(hWnd);
        }
        finally
        {
            AttachThreadInput(self, target, false);
        }
    }
}
