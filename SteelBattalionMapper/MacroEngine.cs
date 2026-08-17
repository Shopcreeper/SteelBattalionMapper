// SteelBattalionMapper
// Created and maintained by SHOPCREEPER (@Shopcreeper)
// https://github.com/Shopcreeper
//
// This attribution comment has no effect on runtime behavior.

namespace SteelBattalionMapper;

internal sealed class MacroEngine
{
    private readonly ControlConfig _config;
    private readonly Dictionary<string, bool> _chordWasActive =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> ControllerButtons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MainWeapon"] = 1,
            ["Trigger"] = 2,
            ["LockOn"] = 3,
            ["Eject"] = 4,
            ["CockpitHatch"] = 5,
            ["Ignition"] = 6,
            ["Start"] = 7,
            ["OpenClose"] = 8,
            ["MapZoom"] = 9,
            ["ModeSelect"] = 10,
            ["SubMonitor"] = 11,
            ["MainMonitorZoomIn"] = 12,
            ["MainMonitorZoomOut"] = 13,
            ["FSS"] = 14,
            ["Manipulator"] = 15,
            ["LineColor"] = 16,
            ["Washing"] = 17,
            ["Extinguisher"] = 18,
            ["Chaff"] = 19,
            ["TankDetach"] = 20,
            ["Override"] = 21,
            ["NightScope"] = 22,
            ["F1"] = 23,
            ["F2"] = 24,
            ["F3"] = 25,
            ["MainWeaponControl"] = 26,
            ["SubWeaponControl"] = 27,
            ["MagazineChange"] = 28,
            ["Comm1"] = 29,
            ["Comm2"] = 30,
            ["Comm3"] = 31,
            ["Comm4"] = 32,
            ["Comm5"] = 33,
            ["SightClick"] = 34,
            ["SightChangeClick"] = 34,
            ["FilterControl"] = 35,
            ["OxygenSupply"] = 36,
            ["FuelFlowRate"] = 37,
            ["BufferMaterial"] = 38,
            ["VTLocation"] = 39,
        };

    public MacroEngine(ControlConfig config)
    {
        _config = config;
    }

    public async Task<HashSet<int>> ProcessControllerChordsAsync(
        SteelBattalionState raw,
        KeyboardMouseOutput output,
        CancellationToken token)
    {
        var suppressed = new HashSet<int>();

        foreach (var pair in _config.GetSection("CONTROLLER_CHORDS"))
        {
            string chordName = pair.Key.Trim();
            string action = pair.Value.Trim();

            int[] buttons = ParseControllerChord(chordName);
            if (buttons.Length < 2 || string.IsNullOrWhiteSpace(action))
                continue;

            bool active = buttons.All(raw.Button);
            bool wasActive =
                _chordWasActive.TryGetValue(chordName, out bool previous) &&
                previous;

            if (active)
            {
                foreach (int button in buttons)
                    suppressed.Add(button);

                if (!wasActive)
                    await ExecuteActionAsync(action, output, token);
            }

            _chordWasActive[chordName] = active;
        }

        return suppressed;
    }

    public async Task ExecuteActionAsync(
        string action,
        KeyboardMouseOutput output,
        CancellationToken token,
        HashSet<string>? recursionGuard = null)
    {
        action = action.Trim();
        if (action.Length == 0 ||
            action.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        recursionGuard ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (action.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase))
        {
            string name = action["Macro:".Length..].Trim();
            if (!recursionGuard.Add(name))
                throw new InvalidOperationException(
                    $"Macro recursion detected at '{name}'.");

            string? body = _config.Get("MACROS", name);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException(
                    $"Macro '{name}' was not found in [MACROS].");

            await ExecuteActionAsync(body, output, token, recursionGuard);
            recursionGuard.Remove(name);
            return;
        }

        // A semicolon separates sequential steps.
        if (action.Contains(';'))
        {
            foreach (string rawStep in action.Split(';'))
            {
                string step = rawStep.Trim();
                if (step.Length == 0)
                    continue;

                await ExecuteStepAsync(step, output, token, recursionGuard);
            }
            return;
        }

        await ExecuteStepAsync(action, output, token, recursionGuard);
    }

    private async Task ExecuteStepAsync(
        string step,
        KeyboardMouseOutput output,
        CancellationToken token,
        HashSet<string> recursionGuard)
    {
        if (step.StartsWith("Macro:", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteActionAsync(step, output, token, recursionGuard);
            return;
        }

        if (step.StartsWith("Wait:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(step["Wait:".Length..].Trim(), out int ms))
                await Task.Delay(Math.Clamp(ms, 0, 5000), token);
            return;
        }

        if (step.Equals("WheelUp", StringComparison.OrdinalIgnoreCase))
        {
            output.MouseWheel(1);
            return;
        }

        if (step.Equals("WheelDown", StringComparison.OrdinalIgnoreCase))
        {
            output.MouseWheel(-1);
            return;
        }

        if (TrySplitCommand(step, "Tap", out string? tapKey) &&
            ControlConfig.TryParseKeyName(tapKey!, out ushort tapVk))
        {
            output.PressKeyBriefly(tapVk, 70);
            return;
        }

        if (TrySplitCommand(step, "KeyDown", out string? downKey) &&
            ControlConfig.TryParseKeyName(downKey!, out ushort downVk))
        {
            output.SetKey(downVk, true);
            return;
        }

        if (TrySplitCommand(step, "KeyUp", out string? upKey) &&
            ControlConfig.TryParseKeyName(upKey!, out ushort upVk))
        {
            output.SetKey(upVk, false);
            return;
        }

        if (TrySplitCommand(step, "MouseClick", out string? mouseClick) &&
            ControlConfig.TryParseMouseName(mouseClick!, out var clickButton))
        {
            output.SetMouseButton(clickButton, true);
            await Task.Delay(45, token);
            output.SetMouseButton(clickButton, false);
            return;
        }

        if (TrySplitCommand(step, "MouseDown", out string? mouseDown) &&
            ControlConfig.TryParseMouseName(mouseDown!, out var downButton))
        {
            output.SetMouseButton(downButton, true);
            return;
        }

        if (TrySplitCommand(step, "MouseUp", out string? mouseUp) &&
            ControlConfig.TryParseMouseName(mouseUp!, out var upButton))
        {
            output.SetMouseButton(upButton, false);
            return;
        }

        // Plain mouse name means one click.
        if (ControlConfig.TryParseMouseName(step, out var mouse))
        {
            output.SetMouseButton(mouse, true);
            await Task.Delay(45, token);
            output.SetMouseButton(mouse, false);
            return;
        }

        // '+' means an ordinary simultaneous keyboard chord.
        if (step.Contains('+'))
        {
            var keys = new List<ushort>();

            foreach (string name in step.Split('+'))
            {
                if (!ControlConfig.TryParseKeyName(name, out ushort vk))
                    throw new InvalidOperationException(
                        $"Unknown key '{name}' in macro chord '{step}'.");
                keys.Add(vk);
            }

            foreach (ushort key in keys)
                output.SetKey(key, true);

            await Task.Delay(70, token);

            for (int i = keys.Count - 1; i >= 0; i--)
                output.SetKey(keys[i], false);

            return;
        }

        // Plain key means one tap.
        if (ControlConfig.TryParseKeyName(step, out ushort plainKey))
        {
            output.PressKeyBriefly(plainKey, 70);
            return;
        }

        throw new InvalidOperationException(
            $"Unknown macro action '{step}'.");
    }

    private static int[] ParseControllerChord(string chord)
    {
        var result = new List<int>();

        foreach (string token in chord.Split('+'))
        {
            string name = NormalizeControllerName(token);
            if (!ControllerButtons.TryGetValue(name, out int button))
                return Array.Empty<int>();

            result.Add(button);
        }

        return result.Distinct().ToArray();
    }

    private static string NormalizeControllerName(string value)
        => value.Trim()
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "");

    private static bool TrySplitCommand(
        string step,
        string command,
        out string? value)
    {
        string prefix = command + ":";
        if (step.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = step[prefix.Length..].Trim();
            return true;
        }

        value = null;
        return false;
    }
}
