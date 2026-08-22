using System.Runtime.InteropServices;

namespace SteelBattalionMapper;

internal sealed class RuntimeBindings
{
    internal enum BindingKind
    {
        Keyboard,
        Mouse
    }

    internal sealed class BindingValue
    {
        public BindingKind Kind { get; init; }
        public ushort VirtualKey { get; init; }
        public KeyboardMouseOutput.MouseButton MouseButton { get; init; }
        public string Name { get; init; } = "";
    }

    private readonly Dictionary<int, BindingEntry> _bindings = new();
    private readonly int _profile;
    private ProfileIni _ini;

    public int Profile => _profile;

    public RuntimeBindings(int profile = 1)
    {
        _profile = Math.Clamp(profile, 1, 5);
        _ini = new ProfileIni(_profile);
        Load();
    }

    public BindingValue? Get(int button)
    {
        if (!_bindings.TryGetValue(button, out BindingEntry? entry))
            return null;

        return entry.Kind == "mouse"
            ? new BindingValue
            {
                Kind = BindingKind.Mouse,
                MouseButton = (KeyboardMouseOutput.MouseButton)entry.MouseButton,
                Name = entry.Name
            }
            : new BindingValue
            {
                Kind = BindingKind.Keyboard,
                VirtualKey = entry.VirtualKey,
                Name = entry.Name
            };
    }

    public void SetKeyboard(int button, ushort key)
    {
        _bindings[button] = new BindingEntry
        {
            Button = button,
            Kind = "keyboard",
            VirtualKey = key,
            Name = KeyName(key)
        };
        Save();
    }

    public void SetMouse(int button, KeyboardMouseOutput.MouseButton mouseButton)
    {
        _bindings[button] = new BindingEntry
        {
            Button = button,
            Kind = "mouse",
            MouseButton = (int)mouseButton,
            Name = MouseButtonName(mouseButton)
        };
        Save();
    }

    public void ResetToDefaults()
    {
        _bindings.Clear();

        ProfileIni.ResetProfile(_profile);
        _ini = new ProfileIni(_profile);
        Load();
    }

    public static void ResetAllProfiles()
    {
        ProfileIni.ResetAll();

        // Remove legacy JSON binding files from older mapper releases.
        string root = ProfileIni.FindMainFolder();
        foreach (string file in Directory.GetFiles(root, "SteelBattalionBindings*.json", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static bool IsEligibleControllerButton(int button)
    {
        // Aiming Lever:
        // 1 Main Weapon = fixed mouse action
        // 2 Trigger / Fire = fixed mouse action
        // 3 Lock On = rebindable
        //
        // 4-7 are protected controls.
        if (button is 1 or 2 or 4 or 5 or 6 or 7)
            return false;

        return button is >= 3 and <= 34;
    }

    public static string ButtonName(int button) => button switch
    {
         1 => "Main Weapon",
         2 => "Trigger / Fire",
         3 => "Lock On",
         4 => "Eject",
         5 => "Cockpit Hatch",
         6 => "Ignition",
         7 => "Start",
         8 => "Open / Close",
         9 => "Map Zoom In / Out",
        10 => "Mode Select",
        11 => "Sub Monitor Mode Select",
        12 => "Main Monitor Zoom In",
        13 => "Main Monitor Zoom Out",
        14 => "F.S.S.",
        15 => "Manipulator",
        16 => "Line Color Change",
        17 => "Washing",
        18 => "Extinguisher",
        19 => "Chaff",
        20 => "Tank Detach",
        21 => "Override",
        22 => "Night Scope",
        23 => "F1",
        24 => "F2",
        25 => "F3",
        26 => "Main Weapon Control",
        27 => "Sub Weapon Control",
        28 => "Magazine Change",
        29 => "Comm 1",
        30 => "Comm 2",
        31 => "Comm 3",
        32 => "Comm 4",
        33 => "Comm 5",
        34 => "Sight Change Click",
        _ => $"Button {button}"
    };

    public static HashSet<ushort> SnapshotDownKeys()
    {
        var set = new HashSet<ushort>();
        for (ushort vk = 0x08; vk <= 0xFE; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                set.Add(vk);
        }
        return set;
    }

    public static HashSet<KeyboardMouseOutput.MouseButton> SnapshotDownMouseButtons()
    {
        var set = new HashSet<KeyboardMouseOutput.MouseButton>();
        foreach (var pair in MouseVirtualKeys)
        {
            if ((GetAsyncKeyState(pair.Value) & 0x8000) != 0)
                set.Add(pair.Key);
        }
        return set;
    }

    public static ushort? PollNewPhysicalKey(HashSet<ushort> initiallyDown)
    {
        for (ushort vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06)
                continue;

            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && !initiallyDown.Contains(vk))
                return vk;
        }
        return null;
    }

    public static KeyboardMouseOutput.MouseButton? PollNewPhysicalMouseButton(
        HashSet<KeyboardMouseOutput.MouseButton> initiallyDown)
    {
        foreach (var pair in MouseVirtualKeys)
        {
            bool down = (GetAsyncKeyState(pair.Value) & 0x8000) != 0;
            if (down && !initiallyDown.Contains(pair.Key))
                return pair.Key;
        }
        return null;
    }

    public static string KeyName(ushort vk)
    {
        uint scan = MapVirtualKey(vk, 0);
        long lParam = scan << 16;

        if (vk is >= 0x21 and <= 0x2E)
            lParam |= 1L << 24;

        var sb = new System.Text.StringBuilder(64);
        if (GetKeyNameText((int)lParam, sb, sb.Capacity) > 0)
            return sb.ToString();

        return $"VK_0x{vk:X2}";
    }

    public static string MouseButtonName(KeyboardMouseOutput.MouseButton button)
        => button switch
        {
            KeyboardMouseOutput.MouseButton.Left => "Mouse Left",
            KeyboardMouseOutput.MouseButton.Right => "Mouse Right",
            KeyboardMouseOutput.MouseButton.Middle => "Mouse Middle",
            KeyboardMouseOutput.MouseButton.X1 => "Mouse X1",
            KeyboardMouseOutput.MouseButton.X2 => "Mouse X2",
            _ => button.ToString()
        };

    private void Load()
    {
        _bindings.Clear();
        foreach ((int button, string value) in _ini.EnumerateBindings())
        {
            if (TryParseBinding(value, out BindingEntry? entry))
            {
                entry.Button = button;
                _bindings[button] = entry;
            }
        }
    }

    private void Save()
    {
        // Runtime remapping writes directly back into the active ProfileN.ini.
        foreach (BindingEntry entry in _bindings.Values.OrderBy(x => x.Button))
        {
            string value = entry.Kind == "mouse"
                ? $"Mouse:{MouseButtonName((KeyboardMouseOutput.MouseButton)entry.MouseButton).Replace("Mouse ", "")}" 
                : $"Keyboard:{ConfigKeyName(entry.VirtualKey)}";
            _ini.SetBinding(entry.Button, value);
        }
    }

    private static bool TryParseBinding(string value, out BindingEntry? entry)
    {
        entry = null;
        string[] parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        if (parts[0].Equals("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseMouse(parts[1], out KeyboardMouseOutput.MouseButton mouse))
                return false;
            entry = new BindingEntry
            {
                Kind = "mouse",
                MouseButton = (int)mouse,
                Name = MouseButtonName(mouse)
            };
            return true;
        }

        if (parts[0].Equals("Keyboard", StringComparison.OrdinalIgnoreCase) &&
            TryParseKey(parts[1], out ushort key))
        {
            entry = new BindingEntry
            {
                Kind = "keyboard",
                VirtualKey = key,
                Name = KeyName(key)
            };
            return true;
        }

        return false;
    }

    private static bool TryParseMouse(string name, out KeyboardMouseOutput.MouseButton button)
    {
        string n = name.Trim().Replace("Mouse", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (n.Equals("Left", StringComparison.OrdinalIgnoreCase)) { button = KeyboardMouseOutput.MouseButton.Left; return true; }
        if (n.Equals("Right", StringComparison.OrdinalIgnoreCase)) { button = KeyboardMouseOutput.MouseButton.Right; return true; }
        if (n.Equals("Middle", StringComparison.OrdinalIgnoreCase)) { button = KeyboardMouseOutput.MouseButton.Middle; return true; }
        if (n.Equals("X1", StringComparison.OrdinalIgnoreCase)) { button = KeyboardMouseOutput.MouseButton.X1; return true; }
        if (n.Equals("X2", StringComparison.OrdinalIgnoreCase)) { button = KeyboardMouseOutput.MouseButton.X2; return true; }
        button = default;
        return false;
    }

    private static bool TryParseKey(string name, out ushort key)
    {
        string n = name.Trim();
        if (n.Length == 1)
        {
            char c = char.ToUpperInvariant(n[0]);
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')
            {
                key = (ushort)c;
                return true;
            }
        }

        if (n.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(n[1..], out int function) && function is >= 1 and <= 12)
        {
            key = (ushort)(0x6F + function);
            return true;
        }

        if (n.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(n[6..], out int numpad) && numpad is >= 0 and <= 9)
        {
            key = (ushort)(0x60 + numpad);
            return true;
        }

        var named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Escape"] = KeyboardMouseOutput.Keys.Escape,
            ["Esc"] = KeyboardMouseOutput.Keys.Escape,
            ["Enter"] = KeyboardMouseOutput.Keys.Enter,
            ["Space"] = KeyboardMouseOutput.Keys.Space,
            ["Tab"] = KeyboardMouseOutput.Keys.Tab,
            ["Shift"] = KeyboardMouseOutput.Keys.Shift,
            ["Control"] = KeyboardMouseOutput.Keys.Control,
            ["Ctrl"] = KeyboardMouseOutput.Keys.Control,
            ["Left"] = KeyboardMouseOutput.Keys.Left,
            ["Right"] = KeyboardMouseOutput.Keys.Right,
            ["Up"] = KeyboardMouseOutput.Keys.Up,
            ["Down"] = KeyboardMouseOutput.Keys.Down,
            ["PageUp"] = KeyboardMouseOutput.Keys.PageUp,
            ["PageDown"] = KeyboardMouseOutput.Keys.PageDown,
            ["Home"] = KeyboardMouseOutput.Keys.Home,
            ["End"] = KeyboardMouseOutput.Keys.End,
            ["Insert"] = KeyboardMouseOutput.Keys.Insert,
            ["Delete"] = KeyboardMouseOutput.Keys.Delete,
            ["Plus"] = KeyboardMouseOutput.Keys.OemPlus,
            ["Minus"] = KeyboardMouseOutput.Keys.OemMinus,
            ["F1"] = KeyboardMouseOutput.Keys.F1,
            ["F2"] = KeyboardMouseOutput.Keys.F2,
            ["F3"] = KeyboardMouseOutput.Keys.F3,
            ["F6"] = KeyboardMouseOutput.Keys.F6,
            ["F7"] = KeyboardMouseOutput.Keys.F7,
            ["F8"] = KeyboardMouseOutput.Keys.F8,
            ["F9"] = KeyboardMouseOutput.Keys.F9,
            ["F10"] = KeyboardMouseOutput.Keys.F10,
            ["Numpad0"] = KeyboardMouseOutput.Keys.Numpad0,
            ["Numpad1"] = KeyboardMouseOutput.Keys.Numpad1,
            ["Numpad2"] = KeyboardMouseOutput.Keys.Numpad2,
            ["Numpad3"] = KeyboardMouseOutput.Keys.Numpad3,
            ["Numpad4"] = KeyboardMouseOutput.Keys.Numpad4,
            ["Numpad5"] = KeyboardMouseOutput.Keys.Numpad5,
            ["Numpad6"] = KeyboardMouseOutput.Keys.Numpad6
        };

        return named.TryGetValue(n, out key);
    }

    private static string ConfigKeyName(ushort key)
    {
        if (key is >= 0x30 and <= 0x39 || key is >= 0x41 and <= 0x5A)
            return ((char)key).ToString();

        var names = new Dictionary<ushort, string>
        {
            [KeyboardMouseOutput.Keys.Escape] = "Escape",
            [KeyboardMouseOutput.Keys.Enter] = "Enter",
            [KeyboardMouseOutput.Keys.Space] = "Space",
            [KeyboardMouseOutput.Keys.Tab] = "Tab",
            [KeyboardMouseOutput.Keys.Shift] = "Shift",
            [KeyboardMouseOutput.Keys.Control] = "Control",
            [KeyboardMouseOutput.Keys.Left] = "Left",
            [KeyboardMouseOutput.Keys.Right] = "Right",
            [KeyboardMouseOutput.Keys.Up] = "Up",
            [KeyboardMouseOutput.Keys.Down] = "Down",
            [KeyboardMouseOutput.Keys.OemPlus] = "Plus",
            [KeyboardMouseOutput.Keys.OemMinus] = "Minus",
            [KeyboardMouseOutput.Keys.F1] = "F1",
            [KeyboardMouseOutput.Keys.F2] = "F2",
            [KeyboardMouseOutput.Keys.F3] = "F3"
        };
        return names.TryGetValue(key, out string? value) ? value : $"VK_0x{key:X2}";
    }

    private static readonly Dictionary<KeyboardMouseOutput.MouseButton, int> MouseVirtualKeys = new()
    {
        [KeyboardMouseOutput.MouseButton.Left] = 0x01,
        [KeyboardMouseOutput.MouseButton.Right] = 0x02,
        [KeyboardMouseOutput.MouseButton.Middle] = 0x04,
        [KeyboardMouseOutput.MouseButton.X1] = 0x05,
        [KeyboardMouseOutput.MouseButton.X2] = 0x06
    };


    private sealed class BindingEntry
    {
        public int Button { get; set; }
        public string Kind { get; set; } = "keyboard";
        public ushort VirtualKey { get; set; }
        public int MouseButton { get; set; }
        public string Name { get; set; } = "";
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(
        int lParam,
        System.Text.StringBuilder lpString,
        int cchSize);
}
