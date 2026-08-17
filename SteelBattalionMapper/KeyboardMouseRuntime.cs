// SteelBattalionMapper
// Created and maintained by SHOPCREEPER (@Shopcreeper)
// https://github.com/Shopcreeper
//
// This attribution comment has no effect on runtime behavior.

namespace SteelBattalionMapper;

internal static class KeyboardMouseRuntime
{
    private readonly record struct Centers(
        double AimX, double AimY, double Rotation,
        double SightX, double SightY,
        double Clutch, double Brake, double Throttle);

    private readonly record struct Output(
        double AimX, double AimY, double Rotation,
        double SightX, double SightY,
        double Clutch, double Brake, double Throttle);

    private const double AimDeadzone = 0.09;
    private const double SightDeadzone = 0.05;
    private const double RotationDeadzoneRaw = 1500.0;

    private const double AimXMin = 64, AimXMax = 65472;
    private const double AimYMin = 0, AimYMax = 65472;
    private const double RotationMin = -31232, RotationMax = 32767;
    private const double SightXMin = -28288, SightXMax = 30336;
    private const double SightYMin = -31936, SightYMax = 29632;
    private const double ClutchReleasedCeiling = 17000, ClutchPressed = 63360;
    private const double BrakeReleasedCeiling = 6000, BrakePressed = 65472;
    private const double ThrottleReleasedCeiling = 512, ThrottlePressed = 65472;

    private const double DriveThreshold = 0.18;
    private const double DriveReleaseThreshold = 0.13;
    private const double RotationThreshold = 0.22;
    private const double RotationReleaseThreshold = 0.16;
    private const double SightThreshold = 0.32;
    private const double SightReleaseThreshold = 0.22;
    private const double SightDiagonalRatio = 0.72;
    private const double FineSensitivityStep = 0.05;

    private static readonly TimeSpan PedalStateDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan GearSettleDelay = TimeSpan.FromMilliseconds(55);
    private static readonly TimeSpan SwitchSettleDelay = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan SightCenterGrace = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TunerDetentWindow = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan GearSensitivityRamp = TimeSpan.FromMilliseconds(120);
    private const double AimRestCaptureRadius = 0.105;
    private const double AimRestRawMotionThreshold = 180.0;
    private const double AimCenterFollowRate = 0.025;
    private static readonly TimeSpan AimRestDelay = TimeSpan.FromMilliseconds(500);

    public static async Task RunAsync(
        SteelBattalionUsb usb,
        bool enableLeds,
        int ledEndpoint,
        CancellationToken token)
    {
        Console.Clear();
        Console.WriteLine("Steel Battalion Mapper - KEYBOARD + MOUSE");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine("Center both sticks and release all pedals.");
        Console.WriteLine("Press ENTER to calibrate.");
        Console.ReadLine();

        Centers centers = await CaptureCentersAsync(usb, token, 1500);

        // The Steel Battalion Aiming Lever may not return to exactly the
        // same physical center every time. Track a very slow adaptive center,
        // but only while the lever is sitting essentially inside the deadzone.
        double adaptiveAimCenterX = centers.AimX;
        double adaptiveAimCenterY = centers.AimY;
        double previousAimRawX = centers.AimX;
        double previousAimRawY = centers.AimY;
        DateTime? aimRestSince = null;

        using var output = new KeyboardMouseOutput();

        SteelBattalionLeds? leds = null;
        if (enableLeds)
        {
            try
            {
                leds = new SteelBattalionLeds(usb, ledEndpoint);
                Console.WriteLine($"LED output enabled on endpoint 0x{ledEndpoint:X2}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LED output unavailable: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Keyboard + mouse controller ready.");
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();

        bool[] previous = new bool[49];
        sbyte previousGear = 127;
        byte? previousTuner = null;

        var runtimeBindings = new RuntimeBindings();
        var preferences = new ControllerPreferences();
        var controlConfig = new ControlConfig();
        var macroEngine = new MacroEngine(controlConfig);

        int? bindingTarget = null;
        HashSet<ushort>? keyboardBaseline = null;
        HashSet<KeyboardMouseOutput.MouseButton>? mouseBaseline = null;

        bool previousStartTriggerSwapChord = false;

        // Factory reset safety sequence.
        bool resetArmed = false;
        bool previousResetArmChord = false;
        bool previousResetEject = false;

        // Track the currently active Lock On mouse output so changing the
        // binding can always release the old mouse button cleanly.
        KeyboardMouseOutput.MouseButton? activeLockOnMouse = null;
        KeyboardMouseOutput.MouseButton? activeSightClickMouse = null;

        // Rotation Lever left/right are independently rebindable held controls.

        // Gear-controlled Aiming Lever behavior.
        double aimSensitivity = 1.00;
        int sensitivityGear = 2;
        bool mouseYInverted = false;

        // Digitalized analog controls use hysteresis so they do not chatter at
        // their activation thresholds.
        bool throttleHeld = false;
        bool brakeHeld = false;
        bool clutchHeld = false;
        bool rotationLeftHeld = false;
        bool rotationRightHeld = false;
        bool sightLeftHeld = false;
        bool sightRightHeld = false;
        bool sightUpHeld = false;
        bool sightDownHeld = false;

        // Short temporal filtering for pedal digitalization.
        var throttleFilter = new DelayedBoolFilter(false);
        var brakeFilter = new DelayedBoolFilter(false);
        var clutchFilter = new DelayedBoolFilter(false);

        // Mechanical gear/switch debounce.
        sbyte debouncedGear = 127;
        sbyte pendingGear = debouncedGear;
        DateTime pendingGearSince = DateTime.UtcNow;

        bool debouncedFilter = false;
        bool debouncedOxygen = false;
        bool debouncedFuel = false;
        bool debouncedBuffer = false;
        bool debouncedVt = false;

        var filterSwitch = new DelayedBoolFilter(false);
        var oxygenSwitch = new DelayedBoolFilter(false);
        var fuelSwitch = new DelayedBoolFilter(false);
        var bufferSwitch = new DelayedBoolFilter(false);
        var vtSwitch = new DelayedBoolFilter(false);

        // Neutral-before-reactivation safety.
        bool rotationNeedsNeutral = false;
        bool pedalsNeedNeutral = false;
        bool aimNeedsNeutral = false;
        bool sightNeedsNeutral = true;

        // Sight-change center grace.
        DateTime? sightCenteredSince = null;

        // Tuner detent filtering.
        int lastTunerDirection = 0;
        DateTime lastTunerStepAt = DateTime.MinValue;

        // Smooth gear sensitivity target transition.
        double targetAimSensitivity = aimSensitivity;

        DateTime nextStatus = DateTime.MinValue;

        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[] packet = await usb.ReadPacketAsync(token);
                SteelBattalionState raw = SteelBattalionPacketDecoder.Decode(packet);

                UpdateAdaptiveAimCenter(
                    raw,
                    ref adaptiveAimCenterX,
                    ref adaptiveAimCenterY,
                    ref previousAimRawX,
                    ref previousAimRawY,
                    ref aimRestSince);

                Output n = Normalize(
                    raw,
                    centers,
                    adaptiveAimCenterX,
                    adaptiveAimCenterY);

                // FACTORY RESET:
                // Hold Ignition (6), then press Cockpit Hatch (5).
                // Eject flashes twice to show the reset is armed.
                bool resetArmChord = raw.Button(6) && raw.Button(5);

                if (resetArmChord && !previousResetArmChord)
                {
                    resetArmed = true;

                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine("RESET ARMED: press EJECT to restore default controls.");

                    if (leds is not null)
                    {
                        try { await leds.FlashResetArmedAsync(token); }
                        catch { }
                    }
                }

                previousResetArmChord = resetArmChord;

                bool resetEject = raw.Button(4);

                if (resetArmed && resetEject && !previousResetEject)
                {
                    // Release every configurable held output first.
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.Left, false);
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.Right, false);
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.Middle, false);
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.X1, false);
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.X2, false);
runtimeBindings.ResetToDefaults();
                    preferences.ResetToDefaults();

                    activeLockOnMouse = null;
                    activeSightClickMouse = null;
                    resetArmed = false;

                    // Consume this physical Eject press.
                    previous[4] = true;

                    Console.WriteLine();
                    Console.WriteLine("CONTROLS RESET TO DEFAULTS.");

                    if (leds is not null)
                    {
                        try { await leds.ConfirmBindingAsync(7, token); }
                        catch { }
                    }
                }

                previousResetEject = resetEject;

                // START + TRIGGER is reserved for swapping the two fixed
                // Aiming Lever mouse actions. This gesture does NOT enter
                // ordinary rebinding mode.
                bool startTriggerSwapChord =
                    raw.Button(7) && raw.Button(2);

                if (startTriggerSwapChord && !previousStartTriggerSwapChord)
                {
                    // Release both fixed aiming mouse outputs before swapping so
                    // neither assignment can remain stuck across the mode change.
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.Left, false);
                    output.SetMouseButton(KeyboardMouseOutput.MouseButton.Right, false);

                    preferences.ToggleMainWeaponTriggerSwap();

                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine(
                        preferences.SwapMainWeaponAndTrigger
                            ? "AIMING BUTTONS SWAPPED: Main Weapon = Right Mouse, Trigger = Left Mouse"
                            : "AIMING BUTTONS NORMAL: Main Weapon = Left Mouse, Trigger = Right Mouse");
                }

                previousStartTriggerSwapChord = startTriggerSwapChord;

                // START + eligible controller button enters programming mode.
                if (bindingTarget is null && raw.Button(7))
                {
                    for (int button = 1; button <= 34; button++)
                    {
                        if (!RuntimeBindings.IsEligibleControllerButton(button))
                            continue;

                        if (raw.Button(button) && !previous[button])
                        {
                            bindingTarget = button;
                            keyboardBaseline = RuntimeBindings.SnapshotDownKeys();
                            mouseBaseline = RuntimeBindings.SnapshotDownMouseButtons();

                            if (button == 3 && activeLockOnMouse.HasValue)
                            {
                                output.SetMouseButton(activeLockOnMouse.Value, false);
                                activeLockOnMouse = null;
                            }

                            if (button == 34 && activeSightClickMouse.HasValue)
                            {
                                output.SetMouseButton(activeSightClickMouse.Value, false);
                                activeSightClickMouse = null;
                            }

                            // Panel buttons may themselves be mouse-bound. Clear
                            // all mouse outputs before capturing a new binding so
                            // nothing can remain held across programming mode.
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Left, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Right, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Middle, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.X1, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.X2, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Right, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Middle, false);

                            if (leds is not null)
                            {
                                try { leds.ShowBindingTarget(button); } catch { }
                            }

                            Console.WriteLine();
                            Console.WriteLine();
                            Console.WriteLine($"BINDING: {RuntimeBindings.ButtonName(button)}");
                            Console.WriteLine("Press one keyboard key...");
                            break;
                        }
                    }
                }

                if (bindingTarget.HasValue)
                {
                    ushort? newKey = RuntimeBindings.PollNewPhysicalKey(
                        keyboardBaseline ?? new HashSet<ushort>());

                    KeyboardMouseOutput.MouseButton? newMouse =
                        RuntimeBindings.PollNewPhysicalMouseButton(
                            mouseBaseline ??
                            new HashSet<KeyboardMouseOutput.MouseButton>());

                    if (newKey.HasValue || newMouse.HasValue)
                    {
                        int target = bindingTarget.Value;
                        string bindingName;

                        if (newMouse.HasValue)
                        {
                            runtimeBindings.SetMouse(target, newMouse.Value);
                            bindingName = RuntimeBindings.MouseButtonName(newMouse.Value);
                        }
                        else
                        {
                            runtimeBindings.SetKeyboard(target, newKey!.Value);
                            bindingName = RuntimeBindings.KeyName(newKey.Value);
                        }

                        Console.WriteLine(
                            $"BOUND: {RuntimeBindings.ButtonName(target)} -> " +
                            bindingName);

                        if (leds is not null)
                        {
                            try
                            {
                                // START is the programming button, so it gives
                                // the universal "binding saved" confirmation.
                                await leds.ConfirmBindingAsync(7, token);
                            }
                            catch { }
                        }

                        bindingTarget = null;
                        keyboardBaseline = null;
                        mouseBaseline = null;

                        if (target == 3)
                        {
                            activeLockOnMouse = null;
                            previous[target] = true;
                        }
                        else if (target == 34)
                        {
                            activeSightClickMouse = null;
                            previous[target] = true;
                        }
                        else
                        {
                            previous[target] = raw.Button(target);
                        }
                    }

                    for (int b = 1; b <= 39; b++)
                        previous[b] = raw.Button(b);

                    continue;
                }

                // Five physical subsystem switches.
                // OFF = subsystem disabled, ON = subsystem enabled.
                bool rawFilter = raw.Button(35);
                bool rawOxygen = raw.Button(36);
                bool rawFuel = raw.Button(37);
                bool rawBuffer = raw.Button(38);
                bool rawVt = raw.Button(39);

                bool previousFilterEnabled = debouncedFilter;
                bool previousOxygenEnabled = debouncedOxygen;
                bool previousFuelEnabled = debouncedFuel;
                bool previousBufferEnabled = debouncedBuffer;
                bool previousVtEnabled = debouncedVt;

                debouncedFilter = filterSwitch.Update(rawFilter, SwitchSettleDelay);
                debouncedOxygen = oxygenSwitch.Update(rawOxygen, SwitchSettleDelay);
                debouncedFuel = fuelSwitch.Update(rawFuel, SwitchSettleDelay);
                debouncedBuffer = bufferSwitch.Update(rawBuffer, SwitchSettleDelay);
                debouncedVt = vtSwitch.Update(rawVt, SwitchSettleDelay);

                bool filterEnabled = debouncedFilter;
                bool oxygenEnabled = debouncedOxygen;
                bool fuelEnabled = debouncedFuel;
                bool bufferEnabled = debouncedBuffer;
                bool vtEnabled = debouncedVt;

                // Physical controller-button combinations defined in the INI.
                // When a chord is active, normal actions of its constituent
                // buttons are suppressed for that press.
                HashSet<int> macroSuppressedButtons =
                    await macroEngine.ProcessControllerChordsAsync(
                        raw,
                        output,
                        token);

                if (!previousOxygenEnabled && oxygenEnabled)
                    rotationNeedsNeutral = true;

                if (!previousVtEnabled && vtEnabled)
                    pedalsNeedNeutral = true;

                if (!previousBufferEnabled && bufferEnabled)
                    aimNeedsNeutral = true;


                // BUFFER MATERIAL powers the Right Aiming Lever.
                double aimMagnitude = Math.Sqrt(n.AimX * n.AimX + n.AimY * n.AimY);

                if (aimNeedsNeutral && aimMagnitude <= AimDeadzone * 0.75)
                    aimNeedsNeutral = false;

                if (bufferEnabled && !aimNeedsNeutral)
                {
                    double mouseY =
                        mouseYInverted
                            ? n.AimY
                            : -n.AimY;

                    // Smoothly approach gear target sensitivity.
                    aimSensitivity = ApproachSensitivity(
                        aimSensitivity,
                        targetAimSensitivity,
                        GearSensitivityRamp);

                    output.MoveMouse(
                        n.AimX,
                        mouseY,
                        aimSensitivity);
                }
                else
                {
                    output.ResetMouseMotion();
                }

                // Pedals: digital keyboard output with hysteresis.
                bool rawThrottleHeld = UpdatePositiveHysteresis(
                    throttleHeld,
                    vtEnabled ? n.Throttle : 0,
                    DriveThreshold,
                    DriveReleaseThreshold);

                bool rawBrakeHeld = UpdatePositiveHysteresis(
                    brakeHeld,
                    vtEnabled ? n.Brake : 0,
                    DriveThreshold,
                    DriveReleaseThreshold);

                bool rawClutchHeld = UpdatePositiveHysteresis(
                    clutchHeld,
                    vtEnabled ? n.Clutch : 0,
                    DriveThreshold,
                    DriveReleaseThreshold);

                if (pedalsNeedNeutral &&
                    n.Throttle < DriveReleaseThreshold &&
                    n.Brake < DriveReleaseThreshold &&
                    n.Clutch < DriveReleaseThreshold)
                {
                    pedalsNeedNeutral = false;
                }

                if (!vtEnabled || pedalsNeedNeutral)
                {
                    rawThrottleHeld = false;
                    rawBrakeHeld = false;
                    rawClutchHeld = false;
                }

                throttleHeld = throttleFilter.Update(rawThrottleHeld, PedalStateDelay);
                brakeHeld = brakeFilter.Update(rawBrakeHeld, PedalStateDelay);
                clutchHeld = clutchFilter.Update(rawClutchHeld, PedalStateDelay);

                output.SetKey(
                    ConfigKey(controlConfig, "PEDALS", "Throttle", KeyboardMouseOutput.Keys.W),
                    throttleHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "PEDALS", "Brake", KeyboardMouseOutput.Keys.S),
                    brakeHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "PEDALS", "Clutch", KeyboardMouseOutput.Keys.Space),
                    clutchHeld);

                // OXYGEN SUPPLY powers the Rotation Lever.
                // Fixed A/D mapping with separate engage/release thresholds.
                if (rotationNeedsNeutral &&
                    Math.Abs(n.Rotation) <= RotationReleaseThreshold)
                {
                    rotationNeedsNeutral = false;
                }

                rotationLeftHeld = UpdateNegativeHysteresis(
                    rotationLeftHeld,
                    oxygenEnabled && !rotationNeedsNeutral ? n.Rotation : 0,
                    RotationThreshold,
                    RotationReleaseThreshold);

                rotationRightHeld = UpdatePositiveHysteresis(
                    rotationRightHeld,
                    oxygenEnabled && !rotationNeedsNeutral ? n.Rotation : 0,
                    RotationThreshold,
                    RotationReleaseThreshold);

                output.SetKey(
                    ConfigKey(controlConfig, "ROTATION_LEVER", "Left", KeyboardMouseOutput.Keys.A),
                    rotationLeftHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "ROTATION_LEVER", "Right", KeyboardMouseOutput.Keys.D),
                    rotationRightHeld);

                // Sight Change stick: hysteresis plus cardinal-direction bias.
                double sightMagnitude = Math.Sqrt(n.SightX * n.SightX + n.SightY * n.SightY);

                if (sightNeedsNeutral && sightMagnitude <= SightReleaseThreshold)
                    sightNeedsNeutral = false;

                if (sightNeedsNeutral)
                {
                    sightLeftHeld = false;
                    sightRightHeld = false;
                    sightUpHeld = false;
                    sightDownHeld = false;
                }
                else
                {
                    UpdateSightDirections(
                        n.SightX,
                        n.SightY,
                        ref sightLeftHeld,
                        ref sightRightHeld,
                        ref sightUpHeld,
                        ref sightDownHeld);

                    bool sightCentered =
                        sightMagnitude <= SightReleaseThreshold;

                    if (sightCentered)
                        sightCenteredSince ??= DateTime.UtcNow;
                    else
                        sightCenteredSince = null;

                    if (sightCenteredSince.HasValue &&
                        DateTime.UtcNow - sightCenteredSince.Value >= SightCenterGrace)
                    {
                        sightLeftHeld = false;
                        sightRightHeld = false;
                        sightUpHeld = false;
                        sightDownHeld = false;
                    }
                }

                output.SetKey(
                    ConfigKey(controlConfig, "SIGHT_CHANGE", "Left", KeyboardMouseOutput.Keys.Left),
                    sightLeftHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "SIGHT_CHANGE", "Right", KeyboardMouseOutput.Keys.Right),
                    sightRightHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "SIGHT_CHANGE", "Up", KeyboardMouseOutput.Keys.Up),
                    sightUpHeld);
                output.SetKey(
                    ConfigKey(controlConfig, "SIGHT_CHANGE", "Down", KeyboardMouseOutput.Keys.Down),
                    sightDownHeld);

                // Main Weapon and Trigger are fixed mouse actions.
                // START + Trigger toggles which physical button owns Left/Right.
                //
                // Normal:
                //   Main Weapon -> Left Mouse
                //   Trigger     -> Right Mouse
                //
                // Swapped:
                //   Main Weapon -> Right Mouse
                //   Trigger     -> Left Mouse
                //
                // The START+Trigger swap gesture itself is consumed and does not
                // emit a Trigger mouse click.
                bool suppressTriggerForSwap = raw.Button(7);

                ApplyFixedMouseButton(
                    output,
                    raw,
                    previous,
                    button: 1,
                    enabled: bufferEnabled && !macroSuppressedButtons.Contains(1),
                    mouseButton:
                        preferences.SwapMainWeaponAndTrigger
                            ? ConfigMouse(controlConfig, "AIMING_LEVER", "Trigger", KeyboardMouseOutput.MouseButton.Right)
                            : ConfigMouse(controlConfig, "AIMING_LEVER", "MainWeapon", KeyboardMouseOutput.MouseButton.Left));

                ApplyFixedMouseButton(
                    output,
                    raw,
                    previous,
                    button: 2,
                    enabled: bufferEnabled && !suppressTriggerForSwap && !macroSuppressedButtons.Contains(2),
                    mouseButton:
                        preferences.SwapMainWeaponAndTrigger
                            ? ConfigMouse(controlConfig, "AIMING_LEVER", "MainWeapon", KeyboardMouseOutput.MouseButton.Left)
                            : ConfigMouse(controlConfig, "AIMING_LEVER", "Trigger", KeyboardMouseOutput.MouseButton.Right));

                // Lock On remains rebindable through START + Lock On.
                // Default action stays Right Mouse unless the user changes it.
                ApplyLockOnButton(
                    output,
                    runtimeBindings,
                    controlConfig,
                    raw,
                    previous,
                    ref activeLockOnMouse,
                    bufferEnabled && !macroSuppressedButtons.Contains(3));

                // Common keyboard-oriented panel layout.
                SetMomentary(
                    output,
                    raw,
                    previous,
                    4,
                    ConfigKey(controlConfig, "PROTECTED_SYSTEM_CONTROLS", "Eject", KeyboardMouseOutput.Keys.Escape),
                    bufferEnabled && !resetArmed && !macroSuppressedButtons.Contains(4)); // Eject
                SetMomentary(output, raw, previous, 5, ConfigKey(controlConfig, "PROTECTED_SYSTEM_CONTROLS", "CockpitHatch", KeyboardMouseOutput.Keys.H), bufferEnabled && !resetArmChord && !macroSuppressedButtons.Contains(5));      // Cockpit Hatch
                SetMomentary(output, raw, previous, 6, ConfigKey(controlConfig, "PROTECTED_SYSTEM_CONTROLS", "Ignition", KeyboardMouseOutput.Keys.I), bufferEnabled && !resetArmChord && !macroSuppressedButtons.Contains(6));      // Ignition
                SetMomentary(output, raw, previous, 7, ConfigKey(controlConfig, "PROTECTED_SYSTEM_CONTROLS", "Start", KeyboardMouseOutput.Keys.Enter), bufferEnabled && !macroSuppressedButtons.Contains(7));  // Start
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    8,
                    "PANEL_BUTTONS",
                    "OpenClose",
                    bufferEnabled && !macroSuppressedButtons.Contains(8),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    9,
                    "PANEL_BUTTONS",
                    "MapZoom",
                    bufferEnabled && !macroSuppressedButtons.Contains(9),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    10,
                    "PANEL_BUTTONS",
                    "ModeSelect",
                    bufferEnabled && !macroSuppressedButtons.Contains(10),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    11,
                    "PANEL_BUTTONS",
                    "SubMonitor",
                    bufferEnabled && !macroSuppressedButtons.Contains(11),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    12,
                    "PANEL_BUTTONS",
                    "MainMonitorZoomIn",
                    bufferEnabled && !macroSuppressedButtons.Contains(12),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    13,
                    "PANEL_BUTTONS",
                    "MainMonitorZoomOut",
                    bufferEnabled && !macroSuppressedButtons.Contains(13),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    14,
                    "PANEL_BUTTONS",
                    "FSS",
                    fuelEnabled && !macroSuppressedButtons.Contains(14),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    15,
                    "PANEL_BUTTONS",
                    "Manipulator",
                    fuelEnabled && !macroSuppressedButtons.Contains(15),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    16,
                    "PANEL_BUTTONS",
                    "LineColor",
                    fuelEnabled && !macroSuppressedButtons.Contains(16),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    17,
                    "PANEL_BUTTONS",
                    "Washing",
                    fuelEnabled && !macroSuppressedButtons.Contains(17),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    18,
                    "PANEL_BUTTONS",
                    "Extinguisher",
                    fuelEnabled && !macroSuppressedButtons.Contains(18),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    19,
                    "PANEL_BUTTONS",
                    "Chaff",
                    fuelEnabled && !macroSuppressedButtons.Contains(19),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    20,
                    "PANEL_BUTTONS",
                    "TankDetach",
                    fuelEnabled && !macroSuppressedButtons.Contains(20),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    21,
                    "PANEL_BUTTONS",
                    "Override",
                    fuelEnabled && !macroSuppressedButtons.Contains(21),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    22,
                    "PANEL_BUTTONS",
                    "NightScope",
                    fuelEnabled && !macroSuppressedButtons.Contains(22),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    23,
                    "PANEL_BUTTONS",
                    "F1",
                    fuelEnabled && !macroSuppressedButtons.Contains(23),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    24,
                    "PANEL_BUTTONS",
                    "F2",
                    fuelEnabled && !macroSuppressedButtons.Contains(24),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    25,
                    "PANEL_BUTTONS",
                    "F3",
                    fuelEnabled && !macroSuppressedButtons.Contains(25),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    26,
                    "PANEL_BUTTONS",
                    "MainWeaponControl",
                    fuelEnabled && !macroSuppressedButtons.Contains(26),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    27,
                    "PANEL_BUTTONS",
                    "SubWeaponControl",
                    fuelEnabled && !macroSuppressedButtons.Contains(27),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    28,
                    "PANEL_BUTTONS",
                    "MagazineChange",
                    fuelEnabled && !macroSuppressedButtons.Contains(28),
                    token);

                // Communications 1..5 exactly as requested.
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    29,
                    "PANEL_BUTTONS",
                    "Comm1",
                    fuelEnabled && !macroSuppressedButtons.Contains(29),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    30,
                    "PANEL_BUTTONS",
                    "Comm2",
                    fuelEnabled && !macroSuppressedButtons.Contains(30),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    31,
                    "PANEL_BUTTONS",
                    "Comm3",
                    fuelEnabled && !macroSuppressedButtons.Contains(31),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    32,
                    "PANEL_BUTTONS",
                    "Comm4",
                    fuelEnabled && !macroSuppressedButtons.Contains(32),
                    token);
                await SetRebindableConfiguredAsync(
                    output,
                    runtimeBindings,
                    controlConfig,
                    macroEngine,
                    raw,
                    previous,
                    33,
                    "PANEL_BUTTONS",
                    "Comm5",
                    fuelEnabled && !macroSuppressedButtons.Contains(33),
                    token);

                // Sight Change stick click.
                ApplySightChangeClick(
                    output,
                    runtimeBindings,
                    controlConfig,
                    raw,
                    previous,
                    ref activeSightClickMouse,
                    enabled: !macroSuppressedButtons.Contains(34));

                // The five physical toggle switches are subsystem power controls.
                // They no longer emit F6-F10 keyboard events.

                // FILTER CONTROL enables the Gear Lever as an Aiming Lever
                // sensitivity/mode selector. Gear input is debounced before use.
                if (!filterEnabled)
                {
                    pendingGear = raw.GearRaw;
                    debouncedGear = raw.GearRaw;
                    previousGear = raw.GearRaw;
                }
                else
                {
                    if (raw.GearRaw != pendingGear)
                    {
                        pendingGear = raw.GearRaw;
                        pendingGearSince = DateTime.UtcNow;
                    }

                    if (pendingGear != debouncedGear &&
                        DateTime.UtcNow - pendingGearSince >= GearSettleDelay)
                    {
                        debouncedGear = pendingGear;

                        switch (debouncedGear)
                        {
                            case 1:
                                targetAimSensitivity = 0.65;
                                sensitivityGear = 1;
                                break;

                            case 2:
                                targetAimSensitivity = 1.00;
                                sensitivityGear = 2;
                                break;

                            case 3:
                                targetAimSensitivity = 1.30;
                                sensitivityGear = 3;
                                break;

                            case 4:
                                targetAimSensitivity = 1.65;
                                sensitivityGear = 4;
                                break;

                            case 5:
                                targetAimSensitivity = 2.00;
                                sensitivityGear = 5;
                                break;

                            case -2:
                                mouseYInverted = !mouseYInverted;
                                output.ResetMouseMotion();
                                break;

                            case -1:
                                // Neutral is now a fixed 50% sensitivity preset.
                                targetAimSensitivity = 0.50;
                                sensitivityGear = 0;
                                break;
                        }

                        previousGear = debouncedGear;
                    }
                }

                // Tuner dial:
                //   Normal turn  -> mouse wheel
                //   START + turn -> fine Aiming Lever sensitivity trim
                if (previousTuner.HasValue && raw.Tuner != previousTuner.Value)
                {
                    int delta = raw.Tuner - previousTuner.Value;
                    if (delta > 127) delta -= 256;
                    if (delta < -127) delta += 256;

                    if (delta != 0)
                    {
                        int direction = Math.Sign(delta);
                        DateTime now = DateTime.UtcNow;

                        bool duplicateDetent =
                            direction == lastTunerDirection &&
                            now - lastTunerStepAt < TunerDetentWindow;

                        if (!duplicateDetent)
                        {
                            lastTunerDirection = direction;
                            lastTunerStepAt = now;

                            if (raw.Button(7))
                            {
                                targetAimSensitivity += direction * FineSensitivityStep;
                                targetAimSensitivity = Math.Clamp(
                                    targetAimSensitivity,
                                    0.25,
                                    2.50);

                                Console.WriteLine();
                                Console.WriteLine();
                                Console.WriteLine(
                                    $"AIM FINE TRIM: {targetAimSensitivity * 100:0}%");

                                if (leds is not null)
                                {
                                    int gearLed = sensitivityGear switch
                                    {
                                        1 => 37,
                                        2 => 38,
                                        3 => 39,
                                        4 => 40,
                                        5 => 41,
                                        _ => -1
                                    };

                                    if (gearLed >= 0)
                                    {
                                        try
                                        {
                                            leds.ShowOnly(gearLed, 15);
                                            await Task.Delay(65, token);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            else
                            {
                                output.MouseWheel(direction);
                            }
                        }
                    }
                }

                previousTuner = raw.Tuner;

                if (leds is not null)
                {
                    try { leds.UpdateSubsystems(raw, filterEnabled, oxygenEnabled, fuelEnabled, bufferEnabled, vtEnabled); }
                    catch
                    {
                        leds = null; // Input remains primary.
                    }
                }

                if (DateTime.UtcNow >= nextStatus)
                {
                    nextStatus = DateTime.UtcNow.AddMilliseconds(200);

                    bool w = throttleHeld;
                    bool s = brakeHeld;
                    bool sp = clutchHeld;
                    bool a = rotationLeftHeld;
                    bool d = rotationRightHeld;
                    bool sl = sightLeftHeld;
                    bool sr = sightRightHeld;
                    bool su = sightUpHeld;
                    bool sd = sightDownHeld;

                    Console.Write(
                        $"\rAIM:{(bufferEnabled ? "ACTIVE " : "LOCKED ")} " +
                        $"{(sensitivityGear == 0 ? "N" : $"G{sensitivityGear}")} {aimSensitivity * 100:0}% " +
                        $"Y:{(mouseYInverted ? "INV" : "NOR")} | " +
                        $"KEYS W:{On(w)} S:{On(s)} A:{On(a)} D:{On(d)} SPACE:{On(sp)} | " +
                        $"ARROWS L:{On(sl)} R:{On(sr)} U:{On(su)} D:{On(sd)}   ");
                }
            }
        }
        finally
        {
            try { leds?.AllOff(); } catch { }
        }
    }

    private static string On(bool value) => value ? "ON " : "off";

    private sealed class DelayedBoolFilter
    {
        private bool _state;
        private bool _candidate;
        private DateTime _candidateSince;

        public DelayedBoolFilter(bool initial)
        {
            _state = initial;
            _candidate = initial;
            _candidateSince = DateTime.UtcNow;
        }

        public bool Update(bool requested, TimeSpan delay)
        {
            if (requested == _state)
            {
                _candidate = requested;
                _candidateSince = DateTime.UtcNow;
                return _state;
            }

            if (requested != _candidate)
            {
                _candidate = requested;
                _candidateSince = DateTime.UtcNow;
                return _state;
            }

            if (DateTime.UtcNow - _candidateSince >= delay)
                _state = requested;

            return _state;
        }
    }

    private static double ApproachSensitivity(
        double current,
        double target,
        TimeSpan rampTime)
    {
        if (Math.Abs(target - current) < 0.001)
            return target;

        // Runtime loop is effectively high-frequency; use a small exponential
        // step so the transition feels smooth but reaches target quickly.
        double alpha = 1.0 - Math.Exp(-0.016 / Math.Max(0.001, rampTime.TotalSeconds));
        return current + (target - current) * alpha;
    }

    private static ushort ConfigKey(
        ControlConfig config,
        string section,
        string key,
        ushort fallback)
    {
        return config.TryGetKeyBinding(section, key, out ushort configured)
            ? configured
            : fallback;
    }

    private static KeyboardMouseOutput.MouseButton ConfigMouse(
        ControlConfig config,
        string section,
        string key,
        KeyboardMouseOutput.MouseButton fallback)
    {
        return config.TryGetMouseBinding(section, key, out var configured)
            ? configured
            : fallback;
    }

    private static bool UpdatePositiveHysteresis(
        bool held,
        double value,
        double engageThreshold,
        double releaseThreshold)
    {
        return held
            ? value >= releaseThreshold
            : value >= engageThreshold;
    }

    private static bool UpdateNegativeHysteresis(
        bool held,
        double value,
        double engageThreshold,
        double releaseThreshold)
    {
        return held
            ? value <= -releaseThreshold
            : value <= -engageThreshold;
    }

    private static void UpdateSightDirections(
        double x,
        double y,
        ref bool left,
        ref bool right,
        ref bool up,
        ref bool down)
    {
        double ax = Math.Abs(x);
        double ay = Math.Abs(y);

        // First decide which axes are intentionally active. If one axis clearly
        // dominates, favor that cardinal direction. Strong movement on both axes
        // still produces a deliberate diagonal.
        bool horizontalDominant =
            ax >= SightThreshold &&
            ay < ax * SightDiagonalRatio;

        bool verticalDominant =
            ay >= SightThreshold &&
            ax < ay * SightDiagonalRatio;

        bool allowHorizontal =
            horizontalDominant ||
            (!verticalDominant && ax >= SightThreshold);

        bool allowVertical =
            verticalDominant ||
            (!horizontalDominant && ay >= SightThreshold);

        left = left
            ? x <= -SightReleaseThreshold && !verticalDominant
            : allowHorizontal && x <= -SightThreshold;

        right = right
            ? x >= SightReleaseThreshold && !verticalDominant
            : allowHorizontal && x >= SightThreshold;

        up = up
            ? y <= -SightReleaseThreshold && !horizontalDominant
            : allowVertical && y <= -SightThreshold;

        down = down
            ? y >= SightReleaseThreshold && !horizontalDominant
            : allowVertical && y >= SightThreshold;

        // Opposing directions can never be active simultaneously.
        if (left) right = false;
        if (right) left = false;
        if (up) down = false;
        if (down) up = false;
    }

    private static async Task SetRebindableConfiguredAsync(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        ControlConfig config,
        MacroEngine macroEngine,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        string section,
        string key,
        bool enabled,
        CancellationToken token)
    {
        bool physicalPressed = enabled && raw.Button(button);
        bool wasPressed = previous[button];

        RuntimeBindings.BindingValue? runtimeBinding = bindings.Get(button);

        // Existing on-controller runtime rebinding remains highest priority.
        if (runtimeBinding is not null)
        {
            if (runtimeBinding.Kind == RuntimeBindings.BindingKind.Mouse)
            {
                output.SetMouseButton(
                    runtimeBinding.MouseButton,
                    physicalPressed);
            }
            else if (physicalPressed && !wasPressed)
            {
                output.PressKeyBriefly(
                    runtimeBinding.VirtualKey,
                    70);
            }

            previous[button] = physicalPressed;
            return;
        }

        string action = config.GetAction(section, key, "None");

        // Plain single-key values keep the low-overhead normal path.
        if (ControlConfig.TryParseKeyName(action, out ushort vk))
        {
            if (physicalPressed && !wasPressed)
                output.PressKeyBriefly(vk, 70);

            previous[button] = physicalPressed;
            return;
        }

        // Plain mouse values follow true physical down/up timing.
        if (ControlConfig.TryParseMouseName(action, out var mouse))
        {
            output.SetMouseButton(mouse, physicalPressed);
            previous[button] = physicalPressed;
            return;
        }

        // Macro/chord/sequence values execute once on the physical press edge.
        if (physicalPressed && !wasPressed)
            await macroEngine.ExecuteActionAsync(action, output, token);

        previous[button] = physicalPressed;
    }

    private static void SetRebindable(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        ushort defaultKey,
        bool enabled = true)
    {
        bool physicalPressed = enabled && raw.Button(button);
        bool wasPressed = previous[button];
        RuntimeBindings.BindingValue? binding = bindings.Get(button);

        if (binding is null)
        {
            // Default panel keyboard behavior: one reliable short key press.
            if (physicalPressed && !wasPressed)
                output.PressKeyBriefly(defaultKey, 70);
        }
        else if (binding.Kind == RuntimeBindings.BindingKind.Mouse)
        {
            // Mouse assignments follow the physical button duration.
            output.SetMouseButton(binding.MouseButton, physicalPressed);
        }
        else
        {
            // Keyboard assignments are one reliable press per physical press.
            if (physicalPressed && !wasPressed)
                output.PressKeyBriefly(binding.VirtualKey, 70);
        }

        previous[button] = physicalPressed;
    }

    private static void ApplyFixedMouseButton(
        KeyboardMouseOutput output,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        bool enabled,
        KeyboardMouseOutput.MouseButton mouseButton)
    {
        bool physicalPressed = enabled && raw.Button(button);

        // Real mouse-button semantics:
        // physical DOWN -> mouse DOWN
        // physical UP   -> mouse UP
        //
        // Keyboard auto-repeat is irrelevant here, and KeyboardMouseOutput
        // internally ignores duplicate SetMouseButton states.
        output.SetMouseButton(mouseButton, physicalPressed);
        previous[button] = physicalPressed;
    }

    private static void ApplySightChangeClick(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        ControlConfig config,
        SteelBattalionState raw,
        bool[] previous,
        ref KeyboardMouseOutput.MouseButton? activeMouse,
        bool enabled)
    {
        const int button = 34;
        ushort defaultKey =
            ConfigKey(config, "SIGHT_CHANGE", "Click", KeyboardMouseOutput.Keys.M);

        bool physicalPressed = enabled && raw.Button(button);
        bool wasPressed = previous[button];
        RuntimeBindings.BindingValue? binding = bindings.Get(button);

        KeyboardMouseOutput.MouseButton? desiredMouse = null;

        if (binding is not null &&
            binding.Kind == RuntimeBindings.BindingKind.Mouse)
        {
            desiredMouse = binding.MouseButton;
        }

        if (activeMouse.HasValue &&
            (!desiredMouse.HasValue || activeMouse.Value != desiredMouse.Value))
        {
            output.SetMouseButton(activeMouse.Value, false);
            activeMouse = null;
        }

        if (desiredMouse.HasValue)
        {
            output.SetMouseButton(desiredMouse.Value, physicalPressed);
            activeMouse = physicalPressed ? desiredMouse : null;
        }
        else
        {
            ushort key =
                binding is null
                    ? defaultKey
                    : binding.VirtualKey;

            if (physicalPressed && !wasPressed)
                output.PressKeyBriefly(key, 70);
        }

        previous[button] = physicalPressed;
    }

    private static void ApplyLockOnButton(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        ControlConfig config,
        SteelBattalionState raw,
        bool[] previous,
        ref KeyboardMouseOutput.MouseButton? activeMouse,
        bool enabled)
    {
        const int button = 3;
        ushort defaultKey =
            ConfigKey(config, "AIMING_LEVER", "LockOn", KeyboardMouseOutput.Keys.Space);

        bool physicalPressed = enabled && raw.Button(button);
        bool wasPressed = previous[button];
        RuntimeBindings.BindingValue? binding = bindings.Get(button);

        KeyboardMouseOutput.MouseButton? desiredMouse = null;

        if (binding is not null &&
            binding.Kind == RuntimeBindings.BindingKind.Mouse)
        {
            desiredMouse = binding.MouseButton;
        }

        // Release an old mouse assignment if Lock On now uses a keyboard key
        // or has been rebound to a different mouse button.
        if (activeMouse.HasValue &&
            (!desiredMouse.HasValue || activeMouse.Value != desiredMouse.Value))
        {
            output.SetMouseButton(activeMouse.Value, false);
            activeMouse = null;
        }

        if (desiredMouse.HasValue)
        {
            output.SetMouseButton(desiredMouse.Value, physicalPressed);
            activeMouse = physicalPressed ? desiredMouse : null;
        }
        else
        {
            ushort key =
                binding is null
                    ? defaultKey
                    : binding.VirtualKey;

            if (physicalPressed && !wasPressed)
                output.PressKeyBriefly(key, 70);
        }

        previous[button] = physicalPressed;
    }

    private static void ApplyRebindableMouseButton(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        bool enabled,
        KeyboardMouseOutput.MouseButton defaultMouse)
    {
        bool physicalPressed = enabled && raw.Button(button);
        bool wasPressed = previous[button];
        RuntimeBindings.BindingValue? binding = bindings.Get(button);

        if (binding is null)
        {
            // Default mouse binding follows the physical button duration.
            output.SetMouseButton(defaultMouse, physicalPressed);
        }
        else if (binding.Kind == RuntimeBindings.BindingKind.Mouse)
        {
            // Remapped mouse binding also follows physical press/release.
            output.SetMouseButton(binding.MouseButton, physicalPressed);
        }
        else
        {
            // Keyboard bindings stay one-shot to avoid key-repeat behavior.
            if (physicalPressed && !wasPressed)
                output.TapKey(binding.VirtualKey);
        }

        previous[button] = physicalPressed;
    }

    private static void SetMomentary(
        KeyboardMouseOutput output,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        ushort key,
        bool enabled = true)
    {
        bool pressed = enabled && raw.Button(button);
        output.SetKey(key, pressed);
        previous[button] = pressed;
    }

    private static void TapOnToggle(
        KeyboardMouseOutput output,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        ushort key)
    {
        bool current = raw.Button(button);

        if (current != previous[button])
            output.TapKey(key);

        previous[button] = current;
    }

    private static async Task<Centers> CaptureCentersAsync(
        SteelBattalionUsb usb, CancellationToken token, int durationMs)
    {
        var ax=new List<int>(); var ay=new List<int>(); var r=new List<int>();
        var sx=new List<int>(); var sy=new List<int>(); var c=new List<int>();
        var b=new List<int>(); var t=new List<int>();

        DateTime end = DateTime.UtcNow.AddMilliseconds(durationMs);

        while (DateTime.UtcNow < end)
        {
            var s = SteelBattalionPacketDecoder.Decode(await usb.ReadPacketAsync(token));
            ax.Add(s.AimX); ay.Add(s.AimY); r.Add(s.Rotation);
            sx.Add(s.SightX); sy.Add(s.SightY);
            c.Add(s.Clutch); b.Add(s.Brake); t.Add(s.Throttle);
        }

        return new Centers(
            Median(ax), Median(ay), Median(r), Median(sx), Median(sy),
            Median(c), Median(b), Median(t));
    }

    private static void UpdateAdaptiveAimCenter(
        SteelBattalionState raw,
        ref double centerX,
        ref double centerY,
        ref double previousRawX,
        ref double previousRawY,
        ref DateTime? restSince)
    {
        // Measure displacement BEFORE the radial deadzone. Adaptive recentering
        // is only allowed very close to the current center, so deliberate slow
        // aiming does not get "eaten" by the correction.
        double nx = NormalizeAsymmetric(raw.AimX, centerX, AimXMin, AimXMax);
        double ny = NormalizeAsymmetric(raw.AimY, centerY, AimYMin, AimYMax);
        double magnitude = Math.Sqrt(nx * nx + ny * ny);

        double rawMotion = Math.Sqrt(
            Math.Pow(raw.AimX - previousRawX, 2) +
            Math.Pow(raw.AimY - previousRawY, 2));

        bool nearRest =
            magnitude <= AimRestCaptureRadius &&
            rawMotion <= AimRestRawMotionThreshold;

        DateTime now = DateTime.UtcNow;

        if (nearRest)
        {
            restSince ??= now;

            if (now - restSince.Value >= AimRestDelay)
            {
                centerX += (raw.AimX - centerX) * AimCenterFollowRate;
                centerY += (raw.AimY - centerY) * AimCenterFollowRate;
            }
        }
        else
        {
            restSince = null;
        }

        previousRawX = raw.AimX;
        previousRawY = raw.AimY;
    }

    private static Output Normalize(
        SteelBattalionState s,
        Centers c,
        double aimCenterX,
        double aimCenterY)
    {
        var aim = NormalizeRadial(
            s.AimX,s.AimY,aimCenterX,aimCenterY,
            AimXMin,AimXMax,AimYMin,AimYMax,AimDeadzone);

        var sight = NormalizeRadial(
            s.SightX,s.SightY,c.SightX,c.SightY,
            SightXMin,SightXMax,SightYMin,SightYMax,SightDeadzone);

        return new Output(
            aim.X, aim.Y,
            NormalizeCenteredAxis(
                s.Rotation,c.Rotation,RotationMin,RotationMax,RotationDeadzoneRaw),
            sight.X, sight.Y,
            NormalizePedal(s.Clutch,c.Clutch,ClutchReleasedCeiling,ClutchPressed),
            NormalizePedal(s.Brake,c.Brake,BrakeReleasedCeiling,BrakePressed),
            NormalizePedal(s.Throttle,c.Throttle,ThrottleReleasedCeiling,ThrottlePressed));
    }

    private readonly record struct Vec2(double X, double Y);

    private static Vec2 NormalizeRadial(
        double rawX,double rawY,double centerX,double centerY,
        double minX,double maxX,double minY,double maxY,double deadzone)
    {
        double x=NormalizeAsymmetric(rawX,centerX,minX,maxX);
        double y=NormalizeAsymmetric(rawY,centerY,minY,maxY);

        double mag=Math.Sqrt(x*x+y*y);
        if (mag<=deadzone) return new Vec2(0,0);

        double capped=Math.Min(mag,1.0);
        double scaled=(capped-deadzone)/(1.0-deadzone);
        double scale=mag>0?scaled/mag:0;

        return new Vec2(
            Math.Clamp(x*scale,-1,1),
            Math.Clamp(y*scale,-1,1));
    }

    private static double NormalizeCenteredAxis(
        double raw,double center,double min,double max,double dz)
    {
        double d=raw-center;
        if (Math.Abs(d)<=dz) return 0;
        if (d<0) return Math.Clamp((d+dz)/Math.Max(1,center-min-dz),-1,0);
        return Math.Clamp((d-dz)/Math.Max(1,max-center-dz),0,1);
    }

    private static double NormalizeAsymmetric(
        double raw,double center,double min,double max)
    {
        if (raw<center)
            return Math.Clamp((raw-center)/Math.Max(1,center-min),-1,0);

        return Math.Clamp((raw-center)/Math.Max(1,max-center),0,1);
    }

    private static double NormalizePedal(
        double raw,double sessionRest,double releasedCeiling,double pressed)
    {
        double zero=Math.Max(Math.Max(sessionRest,0),releasedCeiling);
        if (raw<=zero) return 0;
        return Math.Clamp((raw-zero)/Math.Max(1,pressed-zero),0,1);
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int m=values.Count/2;
        return values.Count%2==1 ? values[m] : (values[m-1]+values[m])/2.0;
    }
}
