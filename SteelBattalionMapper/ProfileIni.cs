using System.Globalization;

namespace SteelBattalionMapper;

internal sealed class ProfileIni
{
    private readonly int _profile;
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public int Profile => _profile;
    public string Path => _path;

    public ProfileIni(int profile)
    {
        _profile = Math.Clamp(profile, 1, 5);
        _path = System.IO.Path.Combine(FindMainFolder(), $"Profile{_profile}.ini");
        EnsureExists();
        Load();
    }

    public string Get(string section, string key, string fallback = "")
    {
        if (_sections.TryGetValue(section, out var values) &&
            values.TryGetValue(key, out string? value))
            return value;
        return fallback;
    }

    public double GetDouble(string section, string key, double fallback)
        => double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : fallback;

    public int GetInt(string section, string key, int fallback)
        => int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    public void Set(string section, string key, string value)
    {
        if (!_sections.TryGetValue(section, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = values;
        }
        values[key] = value;
        Save();
    }

    public IEnumerable<(int Button, string Value)> EnumerateBindings()
    {
        if (!_sections.TryGetValue("Bindings", out var values))
            yield break;

        foreach (var pair in values)
        {
            string number = pair.Key.Split('.', 2)[0].Trim();
            if (int.TryParse(number, out int button) && button is >= 1 and <= 34)
                yield return (button, pair.Value.Trim());
        }
    }

    public void SetBinding(int button, string value)
    {
        string key = $"{button:00}.{SanitizeKey(RuntimeBindings.ButtonName(button))}";
        Set("Bindings", key, value);
    }

    public void RemoveBinding(int button)
    {
        if (!_sections.TryGetValue("Bindings", out var values))
            return;

        string? existing = values.Keys.FirstOrDefault(k =>
        {
            string number = k.Split('.', 2)[0].Trim();
            return int.TryParse(number, out int b) && b == button;
        });

        if (existing is not null)
        {
            values.Remove(existing);
            Save();
        }
    }

    private void EnsureExists()
    {
        if (File.Exists(_path))
            return;

        File.WriteAllText(_path, DefaultText(_profile));
    }

    private void Load()
    {
        _sections.Clear();
        string section = "";

        foreach (string original in File.ReadAllLines(_path))
        {
            string line = original.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!_sections.ContainsKey(section))
                    _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || string.IsNullOrEmpty(section))
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            _sections[section][key] = value;
        }
    }

    private void Save()
    {
        var lines = new List<string>
        {
            "; Steel Battalion Mapper profile configuration",
            "; Edit while the mapper is stopped, or switch away from this profile before editing.",
            "; Binding values: Keyboard:KEYNAME or Mouse:Left/Right/Middle/X1/X2",
            "; Examples: Keyboard:Space   Keyboard:Q   Mouse:Right",
            ""
        };

        foreach (string section in OrderedSections())
        {
            if (!_sections.TryGetValue(section, out var values))
                continue;

            lines.Add($"[{section}]");
            foreach (var pair in values)
                lines.Add($"{pair.Key}={pair.Value}");
            lines.Add("");
        }

        File.WriteAllLines(_path, lines);
    }

    private IEnumerable<string> OrderedSections()
    {
        string[] preferred = { "Profile", "Bindings", "Movement", "AC6" };
        foreach (string name in preferred)
            if (_sections.ContainsKey(name)) yield return name;
        foreach (string name in _sections.Keys)
            if (!preferred.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return name;
    }

    public static void ResetProfile(int profile)
    {
        string path = System.IO.Path.Combine(FindMainFolder(), $"Profile{Math.Clamp(profile, 1, 5)}.ini");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        _ = new ProfileIni(profile);
    }

    public static void ResetAll()
    {
        for (int i = 1; i <= 5; i++) ResetProfile(i);
    }

    public static string FindMainFolder()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            try
            {
                DirectoryInfo? dir = new DirectoryInfo(start);
                for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
                {
                    if (File.Exists(System.IO.Path.Combine(dir.FullName, "START-STEEL-BATTALION-MAPPER.cmd")))
                        return dir.FullName;
                }
            }
            catch { }
        }

        return Environment.CurrentDirectory;
    }

    private static string SanitizeKey(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static string DefaultText(int profile)
    {
        string name = profile == 5 ? "Armored Core VI" : $"General Profile {profile}";
        string bindings = profile == 5 ? Ac6Bindings : GenericBindings;
        string extra = profile == 5 ? Ac6Settings : GenericSettings;

        return $"""
; Steel Battalion Mapper - Profile {profile}
; This file is intentionally player-editable.
; Binding values use Keyboard:KEY or Mouse:BUTTON.
; Common key names: A-Z, 0-9, Space, Enter, Escape, Tab, Shift, Control,
; Left, Right, Up, Down, F1-F12, Plus, Minus, Numpad0-Numpad9.
; Mouse buttons: Left, Right, Middle, X1, X2.

[Profile]
Name={name}
Enabled=true

[Bindings]
{bindings}

{extra}
""";
    }

    private const string GenericBindings = """
03.LockOn=Keyboard:Space
08.OpenClose=Keyboard:E
09.MapZoomInOut=Keyboard:Z
10.ModeSelect=Keyboard:Tab
11.SubMonitorModeSelect=Keyboard:C
12.MainMonitorZoomIn=Keyboard:Plus
13.MainMonitorZoomOut=Keyboard:Minus
14.FSS=Keyboard:F
15.Manipulator=Keyboard:G
16.LineColorChange=Keyboard:L
17.Washing=Keyboard:X
18.Extinguisher=Keyboard:C
19.Chaff=Keyboard:V
20.TankDetach=Keyboard:T
21.Override=Keyboard:O
22.NightScope=Keyboard:N
23.F1=Keyboard:F1
24.F2=Keyboard:F2
25.F3=Keyboard:F3
26.MainWeaponControl=Keyboard:Q
27.SubWeaponControl=Keyboard:G
28.MagazineChange=Keyboard:R
29.Comm1=Keyboard:1
30.Comm2=Keyboard:2
31.Comm3=Keyboard:3
32.Comm4=Keyboard:4
33.Comm5=Keyboard:5
34.SightChangeClick=Keyboard:M
""";

    private const string GenericSettings = """
[Movement]
; Larger values create a larger neutral area in ALT WASD mode.
AltDriveDeadzone=0.10
; Higher values make straight W/A/S/D sectors wider before diagonals engage.
AltDriveDiagonalEntryRatio=0.62
""";

    private const string Ac6Bindings = """
01.MainWeapon=Mouse:Left
02.TriggerFire=Mouse:Right
03.LockOn=Mouse:Middle
05.CockpitHatch=Keyboard:F
06.Ignition=Keyboard:I
07.Start=Keyboard:Control
10.ModeSelect=Keyboard:Escape
18.Extinguisher=Keyboard:C
19.Chaff=Keyboard:V
20.TankDetach=Keyboard:P
26.MainWeaponControl=Keyboard:Q
27.SubWeaponControl=Keyboard:E
28.MagazineChange=Keyboard:R
29.Comm1=Keyboard:1
30.Comm2=Keyboard:2
31.Comm3=Keyboard:3
32.Comm4=Keyboard:4
33.Comm5=Keyboard:5
""";

    private const string Ac6Settings = """
[Movement]
AltDriveDeadzone=0.10
AltDriveDiagonalEntryRatio=0.62

[AC6]
; Sight Change mouse tuning.
SightMouseSensitivityScale=0.42
SightMouseSmoothing=0.18
; Hold Sight Change click + push hard left/right to Quick Turn.
QuickTurnThreshold=0.88
QuickTurnRearm=0.70
; Camera burst. Tune this if your in-game mouse sensitivity changes.
QuickTurnMousePixels=560
QuickTurnLeadMs=25
QuickTurnHoldMs=85
""";
}
