// SteelBattalionMapper
// Created and maintained by SHOPCREEPER (@Shopcreeper)
// https://github.com/Shopcreeper
//
// This attribution comment has no effect on runtime behavior.

using System.Runtime.InteropServices;
using System.Text.Json;

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
    private readonly string _path =
        Path.Combine(AppContext.BaseDirectory, "SteelBattalionBindings.json");

    public RuntimeBindings() => Load();

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

        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            Save();
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
        try
        {
            if (!File.Exists(_path))
                return;

            BindingFile? data =
                JsonSerializer.Deserialize<BindingFile>(File.ReadAllText(_path));

            if (data?.Bindings is null)
                return;

            foreach (BindingEntry e in data.Bindings)
                _bindings[e.Button] = e;
        }
        catch
        {
            _bindings.Clear();
        }
    }

    private void Save()
    {
        var data = new BindingFile
        {
            Version = 2,
            Bindings = _bindings.Values.OrderBy(x => x.Button).ToList()
        };

        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(
                data,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly Dictionary<KeyboardMouseOutput.MouseButton, int> MouseVirtualKeys = new()
    {
        [KeyboardMouseOutput.MouseButton.Left] = 0x01,
        [KeyboardMouseOutput.MouseButton.Right] = 0x02,
        [KeyboardMouseOutput.MouseButton.Middle] = 0x04,
        [KeyboardMouseOutput.MouseButton.X1] = 0x05,
        [KeyboardMouseOutput.MouseButton.X2] = 0x06
    };

    private sealed class BindingFile
    {
        public int Version { get; set; }
        public List<BindingEntry> Bindings { get; set; } = new();
    }

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
