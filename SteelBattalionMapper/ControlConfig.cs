// SteelBattalionMapper
// Created and maintained by SHOPCREEPER (@Shopcreeper)
// https://github.com/Shopcreeper
//
// This attribution comment has no effect on runtime behavior.

using System.Globalization;

namespace SteelBattalionMapper;

internal sealed class ControlConfig
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public ControlConfig()
    {
        _path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "SteelBattalionControls.ini");

        EnsureDefaultFile();
        Load();
    }

    public string? Get(string section, string key)
    {
        string full = $"{section}.{key}";
        return _values.TryGetValue(full, out string? value)
            ? value
            : null;
    }

    public bool TryGetKeyBinding(
        string section,
        string key,
        out ushort virtualKey)
    {
        virtualKey = 0;
        string? value = Get(section, key);

        if (string.IsNullOrWhiteSpace(value))
            return false;

        return TryParseVirtualKey(value, out virtualKey);
    }

    public bool TryGetMouseBinding(
        string section,
        string key,
        out KeyboardMouseOutput.MouseButton mouseButton)
    {
        mouseButton = default;
        string? value = Get(section, key);

        if (string.IsNullOrWhiteSpace(value))
            return false;

        return TryParseMouse(value, out mouseButton);
    }

    public string GetAction(
        string section,
        string key,
        string fallback)
    {
        string? value = Get(section, key);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    public IReadOnlyDictionary<string, string> GetSection(string section)
    {
        string prefix = section + ".";
        return _values
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryParseKeyName(string value, out ushort key)
        => TryParseVirtualKey(value, out key);

    public static bool TryParseMouseName(
        string value,
        out KeyboardMouseOutput.MouseButton button)
        => TryParseMouse(value, out button);

    private void Load()
    {
        _values.Clear();
        string section = "";

        foreach (string raw in File.ReadAllLines(_path))
        {
            string line = raw.Trim();

            if (line.Length == 0 ||
                line.StartsWith(";") ||
                line.StartsWith("#"))
            {
                continue;
            }

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            if (section.Length == 0 || key.Length == 0)
                continue;

            _values[$"{section}.{key}"] = value;
        }
    }

    private void EnsureDefaultFile()
    {
        if (!File.Exists(_path))
            File.WriteAllText(_path, DefaultIni);
    }

    private static bool TryParseMouse(
        string value,
        out KeyboardMouseOutput.MouseButton button)
    {
        string v = NormalizeToken(value);

        button = v switch
        {
            "MOUSELEFT" or "LEFTMOUSE" or "LMB"
                => KeyboardMouseOutput.MouseButton.Left,
            "MOUSERIGHT" or "RIGHTMOUSE" or "RMB"
                => KeyboardMouseOutput.MouseButton.Right,
            "MOUSEMIDDLE" or "MIDDLEMOUSE" or "MMB"
                => KeyboardMouseOutput.MouseButton.Middle,
            "MOUSEX1" or "X1"
                => KeyboardMouseOutput.MouseButton.X1,
            "MOUSEX2" or "X2"
                => KeyboardMouseOutput.MouseButton.X2,
            _ => default
        };

        return v is
            "MOUSELEFT" or "LEFTMOUSE" or "LMB" or
            "MOUSERIGHT" or "RIGHTMOUSE" or "RMB" or
            "MOUSEMIDDLE" or "MIDDLEMOUSE" or "MMB" or
            "MOUSEX1" or "X1" or
            "MOUSEX2" or "X2";
    }

    private static bool TryParseVirtualKey(
        string value,
        out ushort key)
    {
        key = 0;
        string v = NormalizeToken(value);

        var map = new Dictionary<string, ushort>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SPACE"] = KeyboardMouseOutput.Keys.Space,
            ["ENTER"] = KeyboardMouseOutput.Keys.Enter,
            ["ESC"] = KeyboardMouseOutput.Keys.Escape,
            ["ESCAPE"] = KeyboardMouseOutput.Keys.Escape,
            ["TAB"] = KeyboardMouseOutput.Keys.Tab,
            ["CTRL"] = KeyboardMouseOutput.Keys.Control,
            ["CONTROL"] = KeyboardMouseOutput.Keys.Control,
            ["SHIFT"] = KeyboardMouseOutput.Keys.Shift,
            ["ALT"] = KeyboardMouseOutput.Keys.Alt,
            ["LEFT"] = KeyboardMouseOutput.Keys.Left,
            ["RIGHT"] = KeyboardMouseOutput.Keys.Right,
            ["UP"] = KeyboardMouseOutput.Keys.Up,
            ["DOWN"] = KeyboardMouseOutput.Keys.Down,
            ["PLUS"] = KeyboardMouseOutput.Keys.OemPlus,
            ["EQUALS"] = KeyboardMouseOutput.Keys.OemPlus,
            ["MINUS"] = KeyboardMouseOutput.Keys.OemMinus,
            ["F1"] = KeyboardMouseOutput.Keys.F1,
            ["F2"] = KeyboardMouseOutput.Keys.F2,
            ["F3"] = KeyboardMouseOutput.Keys.F3,
        };

        for (char c = 'A'; c <= 'Z'; c++)
            map[c.ToString()] = (ushort)c;

        for (char c = '0'; c <= '9'; c++)
            map[c.ToString()] = (ushort)c;

        if (map.TryGetValue(v, out key))
            return true;

        if (v.StartsWith("VK0X", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(
                v[4..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out key))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeToken(string value)
        => value.Trim()
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "")
            .ToUpperInvariant();

    public const string DefaultIni = """
; ============================================================
; STEEL BATTALION MAPPER - CONTROL CONFIGURATION
; ============================================================
;
; Human-editable base control map.
;
; Common values:
;   A B C ... Z
;   0 1 2 ... 9
;   Space Enter Escape Tab Ctrl Shift Alt
;   Left Right Up Down
;   F1 F2 F3
;   Plus Minus
;   MouseLeft MouseRight MouseMiddle MouseX1 MouseX2
;
; Runtime rebindings saved by the mapper take precedence over
; these values. Resetting controls clears runtime rebindings and
; exposes these INI values again.
;

[AIMING_LEVER]
MainWeapon=MouseLeft
Trigger=MouseRight
LockOn=Space

[ROTATION_LEVER]
Left=A
Right=D

[SIGHT_CHANGE]
Left=Left
Right=Right
Up=Up
Down=Down
Click=M

[PEDALS]
Throttle=W
Brake=S
Clutch=Space

[TUNER]
Clockwise=MouseWheelUp
CounterClockwise=MouseWheelDown

[GEAR_LEVER]
Reverse=ToggleYInvert
Neutral=Sensitivity50
Gear1=Sensitivity65
Gear2=Sensitivity100
Gear3=Sensitivity130
Gear4=Sensitivity165
Gear5=Sensitivity200

[PANEL_BUTTONS]
OpenClose=E
MapZoom=Z
ModeSelect=Tab
SubMonitor=C
MainMonitorZoomIn=Plus
MainMonitorZoomOut=Minus
FSS=F
Manipulator=G
LineColor=L
Washing=X
Extinguisher=C
Chaff=V
TankDetach=T
Override=O
NightScope=N
F1=F1
F2=F2
F3=F3
MainWeaponControl=Q
SubWeaponControl=G
MagazineChange=R
Comm1=1
Comm2=2
Comm3=3
Comm4=4
Comm5=5

; ============================================================
; PROTECTED / SYSTEM CONTROLS
; ============================================================
; These buttons are separated because they also perform mapper-
; level system functions. You CAN change their normal keyboard
; output, but their physical system behavior remains active.
;
; Mapper-level functions include:
;   Start + panel control -> runtime rebinding
;   Start + Trigger       -> swap Main Weapon / Trigger
;   Start + Tuner         -> fine sensitivity trim
;   Ignition + Cockpit    -> arm factory reset
;   Eject                 -> confirm factory reset when armed
;
[PROTECTED_SYSTEM_CONTROLS]
Eject=Escape
CockpitHatch=H
Ignition=I
Start=Enter

; ============================================================
; SUBSYSTEM SWITCHES
; ============================================================
; These are primarily subsystem power/lockout controls.
; Their normal system behavior remains active even if a keyboard
; output is assigned here.
;
; Change None to a supported key if you deliberately want the
; switch to ALSO emit a key on state change in a future build.
; For this build these entries are documented/reserved only.
;
[SUBSYSTEM_SWITCHES]
FilterControl=None
OxygenSupply=None
FuelFlowRate=None
BufferMaterial=None
VTLocation=None

; ============================================================
; MACROS
; ============================================================
; A macro may be:
;
; 1) A key chord:
;      SaveScreenshot=Ctrl+Shift+S
;
; 2) A timed sequence:
;      ReloadThenSlot1=Tap:R; Wait:80; Tap:1
;
; 3) Explicit key states:
;      Example=KeyDown:Ctrl; Tap:F; KeyUp:Ctrl
;
; 4) Mouse actions:
;      ExampleMouse=MouseClick:Left; Wait:60; MouseClick:Right
;
; Supported steps:
;   Tap:KEY
;   KeyDown:KEY
;   KeyUp:KEY
;   MouseClick:Left|Right|Middle|X1|X2
;   MouseDown:...
;   MouseUp:...
;   WheelUp
;   WheelDown
;   Wait:MILLISECONDS
;
; A plain combination such as Ctrl+Shift+S is treated as one
; simultaneous keyboard chord.
;
[MACROS]
; QuickSave=Ctrl+S
; RadioOne=Ctrl+Shift+1
; ExampleSequence=Tap:R; Wait:80; Tap:1

; ============================================================
; CONTROLLER CHORDS
; ============================================================
; These use PHYSICAL Steel Battalion buttons as modifiers.
;
; Syntax:
;   ControllerButton+ControllerButton=ACTION
;
; ACTION may be:
;   a normal key        (F)
;   a keyboard chord    (Ctrl+Shift+S)
;   a mouse action      (MouseClick:Left)
;   a named macro       (Macro:RadioOne)
;
; Example:
;   Hold Override, then press Comm1:
;     Override+Comm1=Macro:RadioOne
;
; When a controller chord fires, the normal actions of its
; constituent buttons are suppressed for that press.
;
[CONTROLLER_CHORDS]
; Override+Comm1=Macro:RadioOne
; Override+Comm2=Ctrl+2
""";
}
