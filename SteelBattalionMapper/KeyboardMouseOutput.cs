using System.Runtime.InteropServices;

namespace SteelBattalionMapper;

internal sealed class KeyboardMouseOutput : IDisposable
{
    private readonly HashSet<ushort> _keysDown = new();
    private bool _leftMouse;
    private bool _rightMouse;
    private bool _middleMouse;
    private bool _x1Mouse;
    private bool _x2Mouse;

    private double _mouseRemainderX;
    private double _mouseRemainderY;

    public void SetKey(ushort vk, bool down)
    {
        bool already = _keysDown.Contains(vk);
        if (already == down)
            return;

        SendKeyboard(vk, down);

        if (down) _keysDown.Add(vk);
        else _keysDown.Remove(vk);
    }

    public void TapKey(ushort vk)
    {
        SendKeyboard(vk, true);
        SendKeyboard(vk, false);
    }

    public void PressKeyBriefly(ushort vk, int holdMilliseconds = 70)
    {
        // A zero-duration down/up pair can be missed by games that poll key
        // state once per frame. Keep the key physically "down" long enough to
        // cross several typical frame boundaries, but far below normal Windows
        // keyboard-repeat delay.
        holdMilliseconds = Math.Clamp(holdMilliseconds, 30, 150);

        SendKeyboard(vk, true);
        Thread.Sleep(holdMilliseconds);
        SendKeyboard(vk, false);
    }

    public void TapChord(params ushort[] keys)
    {
        foreach (ushort key in keys)
            SendKeyboard(key, true);

        for (int i = keys.Length - 1; i >= 0; i--)
            SendKeyboard(keys[i], false);
    }

    public void ClickMouseButton(MouseButton button)
    {
        SetMouseButton(button, true);
        SetMouseButton(button, false);
    }

    public void SetMouseButton(MouseButton button, bool down)
    {
        bool current = button switch
        {
            MouseButton.Left => _leftMouse,
            MouseButton.Right => _rightMouse,
            MouseButton.Middle => _middleMouse,
            MouseButton.X1 => _x1Mouse,
            MouseButton.X2 => _x2Mouse,
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

        if (current == down)
            return;

        switch (button)
        {
            case MouseButton.Left:
                _leftMouse = down;
                break;
            case MouseButton.Right:
                _rightMouse = down;
                break;
            case MouseButton.Middle:
                _middleMouse = down;
                break;
            case MouseButton.X1:
                _x1Mouse = down;
                break;
            case MouseButton.X2:
                _x2Mouse = down;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(button));
        }

        uint flag = (button, down) switch
        {
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (MouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            (MouseButton.X1, true) => MOUSEEVENTF_XDOWN,
            (MouseButton.X1, false) => MOUSEEVENTF_XUP,
            (MouseButton.X2, true) => MOUSEEVENTF_XDOWN,
            (MouseButton.X2, false) => MOUSEEVENTF_XUP,
            _ => 0
        };

        uint mouseData = button switch
        {
            MouseButton.X1 => XBUTTON1,
            MouseButton.X2 => XBUTTON2,
            _ => 0u
        };

        SendMouse(0, 0, mouseData, flag);
    }

    public void MoveMouse(double normalizedX, double normalizedY, double sensitivity = 1.0)
    {
        // Aiming Lever shaping:
        // - strong 2.1 curve for gentle near-center aiming
        // - full travel still reaches high mouse speed
        // - vertical motion is slightly slower than horizontal
        // - microscopic post-curve motion is suppressed instead of eventually
        //   accumulating into cursor drift
        sensitivity = Math.Clamp(sensitivity, 0.10, 3.00);

        const double curveExponent = 2.10;
        const double baseSpeed = 22.0;
        const double verticalScale = 0.90;
        const double microscopicVelocity = 0.08;

        double sx =
            Math.Sign(normalizedX) *
            Math.Pow(Math.Abs(normalizedX), curveExponent) *
            baseSpeed *
            sensitivity;

        double sy =
            Math.Sign(normalizedY) *
            Math.Pow(Math.Abs(normalizedY), curveExponent) *
            baseSpeed *
            sensitivity *
            verticalScale;

        if (Math.Abs(sx) < microscopicVelocity)
            sx = 0;

        if (Math.Abs(sy) < microscopicVelocity)
            sy = 0;

        if (sx == 0)
            _mouseRemainderX = 0;
        else
            _mouseRemainderX += sx;

        if (sy == 0)
            _mouseRemainderY = 0;
        else
            _mouseRemainderY += sy;

        int dx = (int)Math.Truncate(_mouseRemainderX);
        int dy = (int)Math.Truncate(_mouseRemainderY);

        _mouseRemainderX -= dx;
        _mouseRemainderY -= dy;

        if (dx != 0 || dy != 0)
            SendMouse(dx, dy, 0, MOUSEEVENTF_MOVE);
    }

    public void ResetMouseMotion()
    {
        _mouseRemainderX = 0;
        _mouseRemainderY = 0;
    }

    public void MoveMousePixels(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        SendMouse(dx, dy, 0, MOUSEEVENTF_MOVE);
    }

    public void MouseWheel(int detents)
    {
        if (detents == 0)
            return;

        SendMouse(0, 0, unchecked((uint)(detents * WHEEL_DELTA)), MOUSEEVENTF_WHEEL);
    }

    public void Dispose()
    {
        foreach (ushort key in _keysDown.ToArray())
        {
            try { SendKeyboard(key, false); } catch { }
        }
        _keysDown.Clear();

        try { if (_leftMouse) SetMouseButton(MouseButton.Left, false); } catch { }
        try { if (_rightMouse) SetMouseButton(MouseButton.Right, false); } catch { }
        try { if (_middleMouse) SetMouseButton(MouseButton.Middle, false); } catch { }
    }

    private static void SendKeyboard(ushort vk, bool down)
    {
        // Inject scan codes rather than VK-only events. This more closely
        // resembles physical keyboard input and is better recognized by games.
        ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);

        if (scan == 0)
            throw new InvalidOperationException(
                $"Could not map virtual key 0x{vk:X2} to a keyboard scan code.");

        uint flags = KEYEVENTF_SCANCODE;

        if (!down)
            flags |= KEYEVENTF_KEYUP;

        if (IsExtendedKey(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;

        INPUT input = new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendChecked(input);
    }

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.PageUp or Keys.PageDown
            or Keys.Home or Keys.End
            or Keys.Insert or Keys.Delete => true,
        _ => false
    };

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        INPUT input = new()
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendChecked(input);
    }

    private static void SendChecked(INPUT input)
    {
        int size = Marshal.SizeOf<INPUT>();
        int expected = IntPtr.Size == 8 ? 40 : 28;

        if (size != expected)
            throw new InvalidOperationException(
                $"Windows INPUT structure is {size} bytes; expected {expected}.");

        uint sent = SendInput(1, new[] { input }, size);
        if (sent != 1)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows SendInput failed (Win32 error {error}).");
        }
    }

    internal enum MouseButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2
    }

    internal static class Keys
    {
        public const ushort Escape = 0x1B;
        public const ushort Enter = 0x0D;
        public const ushort Space = 0x20;
        public const ushort Tab = 0x09;
        public const ushort Shift = 0x10;
        public const ushort Control = 0x11;

        public const ushort Left = 0x25;
        public const ushort Up = 0x26;
        public const ushort Right = 0x27;
        public const ushort Down = 0x28;

        public const ushort PageUp = 0x21;
        public const ushort PageDown = 0x22;
        public const ushort Home = 0x24;
        public const ushort End = 0x23;
        public const ushort Insert = 0x2D;
        public const ushort Delete = 0x2E;

        public const ushort D0 = 0x30;
        public const ushort D1 = 0x31;
        public const ushort D2 = 0x32;
        public const ushort D3 = 0x33;
        public const ushort D4 = 0x34;
        public const ushort D5 = 0x35;

        public const ushort A = 0x41;
        public const ushort C = 0x43;
        public const ushort D = 0x44;
        public const ushort E = 0x45;
        public const ushort F = 0x46;
        public const ushort G = 0x47;
        public const ushort H = 0x48;
        public const ushort I = 0x49;
        public const ushort L = 0x4C;
        public const ushort M = 0x4D;
        public const ushort N = 0x4E;
        public const ushort O = 0x4F;
        public const ushort P = 0x50;
        public const ushort Q = 0x51;
        public const ushort R = 0x52;
        public const ushort S = 0x53;
        public const ushort T = 0x54;
        public const ushort V = 0x56;
        public const ushort W = 0x57;
        public const ushort X = 0x58;
        public const ushort Z = 0x5A;

        public const ushort F1 = 0x70;
        public const ushort F2 = 0x71;
        public const ushort F3 = 0x72;
        public const ushort F6 = 0x75;
        public const ushort F7 = 0x76;
        public const ushort F8 = 0x77;
        public const ushort F9 = 0x78;
        public const ushort F10 = 0x79;

        public const ushort Numpad0 = 0x60;
        public const ushort Numpad1 = 0x61;
        public const ushort Numpad2 = 0x62;
        public const ushort Numpad3 = 0x63;
        public const ushort Numpad4 = 0x64;
        public const ushort Numpad5 = 0x65;
        public const ushort Numpad6 = 0x66;

        public const ushort OemMinus = 0xBD;
        public const ushort OemPlus = 0xBB;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
