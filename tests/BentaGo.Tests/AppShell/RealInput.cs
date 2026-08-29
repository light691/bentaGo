using System.Runtime.InteropServices;
using System.Threading;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Real keyboard input for the STA real-input tier: posts WM_KEY*/WM_CHAR
/// into a specific WPF HwndSource window (the same message path WPF
/// processes for OS input). For modifier chords (Ctrl+S, Alt+2) the key
/// state must be real, so this helper falls back to SendInput behind
/// AttachThreadInput + SetForegroundWindow (the exact route the operator's
/// driver uses on the live desktop).
/// </summary>
internal static class RealInput
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmChar = 0x0102;
    private const uint KeyEventFKeyUp = 0x0002;
    private const ushort VkMenu = 0x12;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const uint MapVkVk = 0x00;

    /// <summary>Posts a plain key (no modifiers): works for F1/F12/Esc/Enter/Tab and letters.</summary>
    public static void PostKey(IntPtr hwnd, ushort vk)
    {
        ushort scan = (ushort)MapVirtualKey(vk, MapVkVk);
        IntPtr down = new((scan << 16) | 1);
        IntPtr up = new((scan << 16) | 1 | 0xC0000000);
        uint msgDown = vk == VkMenu ? WmSysKeyDown : WmKeyDown;
        uint msgUp = vk == VkMenu ? WmSysKeyUp : WmKeyUp;
        _ = PostMessage(hwnd, msgDown, new IntPtr(vk), down);
        Thread.Sleep(10);
        _ = PostMessage(hwnd, msgUp, new IntPtr(vk), up);
    }

    /// <summary>Types literal text into the focused field (WM_CHAR, like real typing).</summary>
    public static void TypeText(IntPtr hwnd, string text)
    {
        foreach (char c in text)
        {
            _ = PostMessage(hwnd, WmChar, new IntPtr(c), IntPtr.Zero);
            Thread.Sleep(8);
        }
    }

    /// <summary>
    /// Posts a modifier chord (e.g. Ctrl+S, Alt+2) as WM_KEY*/WM_SYSKEY*
    /// messages in the real press order: modifier down, key down, key up,
    /// modifier up. WPF tracks modifier state from the message stream, so no
    /// foreground claim is needed (deterministic under the test host).
    /// </summary>
    public static void PostChord(IntPtr hwnd, bool control, bool alt, ushort vk)
    {
        if (control) PostKeyDown(hwnd, VkControl, control: true, alt: false);
        if (alt) PostKeyDown(hwnd, VkMenu, control: false, alt: true);
        PostKeyDown(hwnd, vk, control, alt: false);
        PostKeyUp(hwnd, vk, control, alt: false);
        if (alt) PostKeyUp(hwnd, VkMenu, control: false, alt: true);
        if (control) PostKeyUp(hwnd, VkControl, control: true, alt: false);
    }

    public static void PostKeyDown(IntPtr hwnd, ushort vk, bool control = false, bool alt = false)
    {
        ushort scan = (ushort)MapVirtualKey(vk, MapVkVk);
        uint msg = (alt && !control) ? WmSysKeyDown : WmKeyDown;
        _ = PostMessage(hwnd, msg, new IntPtr(vk), new IntPtr((scan << 16) | 1));
        Thread.Sleep(10);
    }

    public static void PostKeyUp(IntPtr hwnd, ushort vk, bool control = false, bool alt = false)
    {
        ushort scan = (ushort)MapVirtualKey(vk, MapVkVk);
        uint msg = (alt && !control) ? WmSysKeyUp : WmKeyUp;
        _ = PostMessage(hwnd, msg, new IntPtr(vk), new IntPtr((scan << 16) | 1 | 0xC0000000));
        Thread.Sleep(10);
    }

    /// <summary>
    /// Real SendInput chord (e.g. Ctrl+S, Alt+2) with the window forced into
    /// the foreground via AttachThreadInput (modifier key state must be real
    /// for WPF to match the combined gesture).
    /// </summary>
    public static void SendChord(IntPtr hwnd, bool control, bool alt, ushort vk)
    {
        AttachThreadInput(GetCurrentThreadId(), GetWindowThreadProcessId(hwnd, out _), true);
        try
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(100);
            if (control) SendKeyDown(VkControl, 0x1D);
            if (alt) SendKeyDown(VkMenu, 0x38);
            SendKeyDown(vk, ScanCodeOf(vk));
            SendKeyUp(vk);
            if (alt) SendKeyUp(VkMenu);
            if (control) SendKeyUp(VkControl);
        }
        finally
        {
            AttachThreadInput(GetCurrentThreadId(), GetWindowThreadProcessId(hwnd, out _), false);
        }
    }

    public static void ClickCenter(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out RECT rect))
        {
            return;
        }

        var point = new POINT { X = rect.Right / 2, Y = rect.Top + 8 };
        _ = ClientToScreen(hwnd, ref point);
        SetCursorPos(point.X, point.Y);
        SendInputMouse(0x0002); // left down
        Thread.Sleep(20);
        SendInputMouse(0x0004); // left up
        Thread.Sleep(120);
    }

    private static ushort ScanCodeOf(ushort vk) => (ushort)MapVirtualKey(vk, MapVkVk);

    private static void SendKeyDown(ushort vk, ushort scan)
    {
        var down = new Input { Type = 1, Data = new InputUnion { Key = new KeybdInput { Vk = vk, Scan = scan } } };
        _ = SendInput(1, [down], Marshal.SizeOf<Input>());
    }

    private static void SendKeyUp(ushort vk)
    {
        var up = new Input { Type = 1, Data = new InputUnion { Key = new KeybdInput { Vk = vk, Scan = 0, Flags = KeyEventFKeyUp } } };
        _ = SendInput(1, [up], Marshal.SizeOf<Input>());
    }

    private static void SendInputMouse(uint mouseEvent)
    {
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = mouseEvent } } };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
// removed: internal decl below
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
}