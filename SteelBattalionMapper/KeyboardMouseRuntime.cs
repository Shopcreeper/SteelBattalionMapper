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
    private const double RotationThreshold = 0.22;
    private const double SightThreshold = 0.32;
    private const double FineSensitivityStep = 0.05;

    // Profile 5: ARMORED CORE VI special controls.
    private const double Ac6QuickTurnThreshold = 0.88;
    private const double Ac6QuickTurnRearm = 0.70;
    // Starting calibration for roughly a 90-degree (25% of 360) camera snap.
    // If AC6 mouse sensitivity is changed in-game, this is the one value to retune.
    private const int Ac6QuickTurnMousePixels = 560;
    private const int Ac6QuickTurnLeadMs = 25;
    private const int Ac6QuickTurnHoldMs = 85;

    // Profile 5 Sight Change mouse shaping. The physical Sight Change lever is
    // much shorter/twitchier than the main Aiming Lever, so AC6 gets its own
    // slower response plus a low-pass filter before the normal mouse curve.
    private const double Ac6SightMouseSensitivityScale = 0.42;
    private const double Ac6SightMouseSmoothing = 0.18;

    // Alternate movement mode: the Aiming Lever becomes a digital 8-way WASD stick.
    // The center deadzone is intentionally larger than normal mouse aiming.
    // Cardinal directions also get a broad angular cone: a diagonal is only entered
    // once the secondary axis reaches this fraction of the dominant axis.
    // 0.62 gives roughly +/-32 degrees of forgiveness around W/S/A/D.
    private const double AltDriveDeadzone = 0.10;
    private const double AltDriveDiagonalEntryRatio = 0.62;
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

        int activeProfile = 1;
        RuntimeBindings runtimeBindings = new RuntimeBindings(activeProfile);
        ProfileIni profileIni = new ProfileIni(activeProfile);
        var preferences = new ControllerPreferences();

        double altDriveDeadzone = profileIni.GetDouble("Movement", "AltDriveDeadzone", AltDriveDeadzone);
        double altDriveDiagonalEntryRatio = profileIni.GetDouble("Movement", "AltDriveDiagonalEntryRatio", AltDriveDiagonalEntryRatio);
        double ac6QuickTurnThreshold = profileIni.GetDouble("AC6", "QuickTurnThreshold", Ac6QuickTurnThreshold);
        double ac6QuickTurnRearm = profileIni.GetDouble("AC6", "QuickTurnRearm", Ac6QuickTurnRearm);
        int ac6QuickTurnMousePixels = profileIni.GetInt("AC6", "QuickTurnMousePixels", Ac6QuickTurnMousePixels);
        int ac6QuickTurnLeadMs = profileIni.GetInt("AC6", "QuickTurnLeadMs", Ac6QuickTurnLeadMs);
        int ac6QuickTurnHoldMs = profileIni.GetInt("AC6", "QuickTurnHoldMs", Ac6QuickTurnHoldMs);
        double ac6SightMouseSensitivityScale = profileIni.GetDouble("AC6", "SightMouseSensitivityScale", Ac6SightMouseSensitivityScale);
        double ac6SightMouseSmoothing = profileIni.GetDouble("AC6", "SightMouseSmoothing", Ac6SightMouseSmoothing);

        // Hold OVERRIDE + press COMM 1..5 to switch profiles.
        // Each profile owns an independent RuntimeBindings file so future
        // per-game layouts/macros can be added without overwriting another game.
        bool[] previousProfileChord = new bool[5];
        bool[] suppressCommUntilRelease = new bool[5];

        int? bindingTarget = null;
        HashSet<ushort>? keyboardBaseline = null;
        HashSet<KeyboardMouseOutput.MouseButton>? mouseBaseline = null;

        bool previousStartTriggerSwapChord = false;

        // OVERRIDE + TRIGGER toggles the alternate locomotion/look mode.
        // Normal:   Aiming Lever = mouse, Sight Change = arrow keys.
        // Alternate:Aiming Lever = digital 8-way WASD, Sight Change = mouse.
        bool alternateControlMode = false;
        bool previousAlternateModeChord = false;
        bool suppressOverrideUntilRelease = false;
        bool suppressTriggerUntilRelease = false;

        // AC6 Profile 5: edge-triggered Quick Turn latches.
        bool ac6QuickTurnLeftLatched = false;
        bool ac6QuickTurnRightLatched = false;
        double ac6SmoothedSightX = 0.0;
        double ac6SmoothedSightY = 0.0;

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
                    RuntimeBindings.ResetAllProfiles();
                    runtimeBindings = new RuntimeBindings(activeProfile);
                    profileIni = new ProfileIni(activeProfile);
                    altDriveDeadzone = profileIni.GetDouble("Movement", "AltDriveDeadzone", AltDriveDeadzone);
                    altDriveDiagonalEntryRatio = profileIni.GetDouble("Movement", "AltDriveDiagonalEntryRatio", AltDriveDiagonalEntryRatio);
                    preferences.ResetToDefaults();

                    activeLockOnMouse = null;
                    activeSightClickMouse = null;
                    alternateControlMode = false;
                    previousAlternateModeChord = false;
                    suppressOverrideUntilRelease = false;
                    suppressTriggerUntilRelease = false;
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
                    activeProfile != 5 && raw.Button(7) && raw.Button(2);

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

                // GAME PROFILE SWITCHING:
                // Hold OVERRIDE (21), then press COMM 1..5 (29..33).
                // The switching chord is consumed so it does not send the
                // Override key or the selected COMM binding to the game.
                bool profileOverrideHeld = raw.Button(21);
                bool anyProfileChord = false;

                for (int i = 0; i < 5; i++)
                {
                    int commButton = 29 + i;
                    bool chord = profileOverrideHeld && raw.Button(commButton);
                    anyProfileChord |= chord;

                    if (chord && !previousProfileChord[i])
                    {
                        int newProfile = i + 1;

                        suppressOverrideUntilRelease = true;
                        suppressCommUntilRelease[i] = true;

                        if (newProfile != activeProfile)
                        {
                            // Release outputs that can otherwise remain held across
                            // a profile boundary. Individual future profile macros
                            // can safely start from a clean state.
                            output.SetKey(KeyboardMouseOutput.Keys.W, false);
                            output.SetKey(KeyboardMouseOutput.Keys.S, false);
                            output.SetKey(KeyboardMouseOutput.Keys.A, false);
                            output.SetKey(KeyboardMouseOutput.Keys.D, false);
                            output.SetKey(KeyboardMouseOutput.Keys.Space, false);
                            output.SetKey(KeyboardMouseOutput.Keys.Tab, false);
                            output.SetKey(KeyboardMouseOutput.Keys.Shift, false);
                            output.SetKey(KeyboardMouseOutput.Keys.Control, false);
                            output.SetKey(KeyboardMouseOutput.Keys.R, false);
                            output.SetKey(KeyboardMouseOutput.Keys.P, false);
                            output.SetKey(KeyboardMouseOutput.Keys.Q, false);
                            output.SetKey(KeyboardMouseOutput.Keys.E, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Left, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Right, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.Middle, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.X1, false);
                            output.SetMouseButton(KeyboardMouseOutput.MouseButton.X2, false);
                            output.ResetMouseMotion();

                            activeLockOnMouse = null;
                            activeSightClickMouse = null;
                            alternateControlMode = false;
                            previousAlternateModeChord = false;
                            ac6QuickTurnLeftLatched = false;
                            ac6QuickTurnRightLatched = false;
                            ac6SmoothedSightX = 0.0;
                            ac6SmoothedSightY = 0.0;
                            bindingTarget = null;
                            keyboardBaseline = null;
                            mouseBaseline = null;

                            activeProfile = newProfile;
                            runtimeBindings = new RuntimeBindings(activeProfile);
                            profileIni = new ProfileIni(activeProfile);
                            altDriveDeadzone = profileIni.GetDouble("Movement", "AltDriveDeadzone", AltDriveDeadzone);
                            altDriveDiagonalEntryRatio = profileIni.GetDouble("Movement", "AltDriveDiagonalEntryRatio", AltDriveDiagonalEntryRatio);
                            ac6QuickTurnThreshold = profileIni.GetDouble("AC6", "QuickTurnThreshold", Ac6QuickTurnThreshold);
                            ac6QuickTurnRearm = profileIni.GetDouble("AC6", "QuickTurnRearm", Ac6QuickTurnRearm);
                            ac6QuickTurnMousePixels = profileIni.GetInt("AC6", "QuickTurnMousePixels", Ac6QuickTurnMousePixels);
                            ac6QuickTurnLeadMs = profileIni.GetInt("AC6", "QuickTurnLeadMs", Ac6QuickTurnLeadMs);
                            ac6QuickTurnHoldMs = profileIni.GetInt("AC6", "QuickTurnHoldMs", Ac6QuickTurnHoldMs);
                            ac6SightMouseSensitivityScale = profileIni.GetDouble("AC6", "SightMouseSensitivityScale", Ac6SightMouseSensitivityScale);
                            ac6SightMouseSmoothing = profileIni.GetDouble("AC6", "SightMouseSmoothing", Ac6SightMouseSmoothing);

                            // Resynchronize edge tracking so buttons physically held
                            // during the switch are not treated as fresh presses in
                            // the newly selected profile.
                            for (int b = 1; b <= 39; b++)
                                previous[b] = raw.Button(b);

                            Console.WriteLine();
                            Console.WriteLine();
                            Console.WriteLine(activeProfile == 5 ? "PROFILE 5 SELECTED - ARMORED CORE VI" : $"PROFILE {activeProfile} SELECTED");
                        }
                        else
                        {
                            Console.WriteLine();
                            Console.WriteLine();
                            Console.WriteLine($"PROFILE {activeProfile} ALREADY ACTIVE");
                        }
                    }

                    previousProfileChord[i] = chord;

                    if (!raw.Button(commButton))
                        suppressCommUntilRelease[i] = false;
                }

                if (!profileOverrideHeld)
                    suppressOverrideUntilRelease = false;

                // ALTERNATE CONTROL MODE:
                // Hold OVERRIDE (21) and pull the Aiming Lever TRIGGER (2).
                // The chord toggles between the normal mapping and:
                //   Aiming Lever -> digital 8-way WASD
                //   Sight Change -> mouse movement
                bool alternateModeChord =
                    activeProfile != 5 && raw.Button(21) && raw.Button(2);

                if (alternateModeChord && !previousAlternateModeChord)
                {
                    alternateControlMode = !alternateControlMode;
                    suppressOverrideUntilRelease = true;
                    suppressTriggerUntilRelease = true;

                    // Do not carry held movement or sub-pixel mouse remainder
                    // across the mode transition.
                    output.SetKey(KeyboardMouseOutput.Keys.W, false);
                    output.SetKey(KeyboardMouseOutput.Keys.S, false);
                    output.SetKey(KeyboardMouseOutput.Keys.A, false);
                    output.SetKey(KeyboardMouseOutput.Keys.D, false);
                    output.ResetMouseMotion();

                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine(
                        alternateControlMode
                            ? "ALT CONTROL MODE: Aiming Lever = digital WASD, Sight Change = mouse"
                            : "NORMAL CONTROL MODE: Aiming Lever = mouse, Sight Change = arrow keys");
                }

                previousAlternateModeChord = alternateModeChord;

                if (!raw.Button(21))
                    suppressOverrideUntilRelease = false;
                if (!raw.Button(2))
                    suppressTriggerUntilRelease = false;

                bool ac6Profile = activeProfile == 5;

                // Five physical subsystem switches.
                // OFF = subsystem disabled, ON = subsystem enabled.
                bool filterEnabled = raw.Button(35); // Gear Lever
                bool oxygenEnabled = raw.Button(36); // Rotation Lever
                bool fuelEnabled   = raw.Button(37); // Center/Middle Block
                bool bufferEnabled = raw.Button(38); // Right Block + Aiming Lever
                bool vtEnabled     = raw.Button(39); // Pedals

                // BUFFER MATERIAL powers the right-side aiming controls.
                // Profile 5 (AC6) permanently uses the cockpit-style drive/look split:
                //   Aiming Lever = WASD
                //   Sight Change = mouse camera
                //
                // Quick Turn is now an intentional chord rather than an automatic
                // edge action: HOLD the Sight Change stick click (button 34), then
                // push Sight Change hard left/right. Normal Sight Change movement
                // remains mouse-look at all times.
                bool ac6QuickTurnLeftRequested = false;
                bool ac6QuickTurnRightRequested = false;

                if (ac6Profile)
                {
                    bool quickTurnModifier = raw.Button(34);

                    if (quickTurnModifier && n.SightX <= -ac6QuickTurnThreshold)
                    {
                        if (!ac6QuickTurnLeftLatched)
                            ac6QuickTurnLeftRequested = true;
                        ac6QuickTurnLeftLatched = true;
                    }
                    else if (!quickTurnModifier || n.SightX >= -ac6QuickTurnRearm)
                    {
                        ac6QuickTurnLeftLatched = false;
                    }

                    if (quickTurnModifier && n.SightX >= ac6QuickTurnThreshold)
                    {
                        if (!ac6QuickTurnRightLatched)
                            ac6QuickTurnRightRequested = true;
                        ac6QuickTurnRightLatched = true;
                    }
                    else if (!quickTurnModifier || n.SightX <= ac6QuickTurnRearm)
                    {
                        ac6QuickTurnRightLatched = false;
                    }

                    double targetSightX = n.SightX;
                    double targetSightY = mouseYInverted ? n.SightY : -n.SightY;

                    // Smooth the short Sight Change lever before feeding it into
                    // the existing curved mouse routine. This removes the sharp,
                    // twitchy jumps without changing the physical calibration.
                    ac6SmoothedSightX +=
                        (targetSightX - ac6SmoothedSightX) * ac6SightMouseSmoothing;
                    ac6SmoothedSightY +=
                        (targetSightY - ac6SmoothedSightY) * ac6SightMouseSmoothing;

                    output.MoveMouse(
                        ac6SmoothedSightX,
                        ac6SmoothedSightY,
                        aimSensitivity * ac6SightMouseSensitivityScale);
                }
                else if (alternateControlMode)
                {
                    double sightMouseY = mouseYInverted ? n.SightY : -n.SightY;
                    output.MoveMouse(n.SightX, sightMouseY, aimSensitivity);
                }
                else if (bufferEnabled)
                {
                    double mouseY = mouseYInverted ? n.AimY : -n.AimY;
                    output.MoveMouse(n.AimX, mouseY, aimSensitivity);
                }
                else
                {
                    output.ResetMouseMotion();
                }

                // Existing pedal and Rotation Lever movement stays available.
                // In alternate mode the Aiming Lever is OR'd into those same
                // WASD keys, so the alternate mode does not break the original
                // pedals or Rotation Lever.
                //
                // ALT DRIVE uses solid keyboard holds and broad 8-way sectors.
                // This deliberately favors a cardinal direction when the stick is
                // mostly forward/back/left/right. A diagonal is only selected when
                // the secondary axis becomes substantial, so a slightly crooked
                // forward push still produces W instead of immediately producing W+A/W+D.
                bool aimForward = false;
                bool aimBackward = false;
                bool aimLeft = false;
                bool aimRight = false;

                if ((alternateControlMode || ac6Profile) && bufferEnabled)
                {
                    double driveX = n.AimX;
                    double driveY = n.AimY;
                    double absX = Math.Abs(driveX);
                    double absY = Math.Abs(driveY);
                    double dominant = Math.Max(absX, absY);

                    if (dominant >= altDriveDeadzone)
                    {
                        if (absY >= absX)
                        {
                            // Forward/back is dominant. Stay cardinal unless the
                            // sideways component is large enough to enter a diagonal.
                            aimForward = driveY < 0;
                            aimBackward = driveY > 0;

                            if (absX >= absY * altDriveDiagonalEntryRatio)
                            {
                                aimLeft = driveX < 0;
                                aimRight = driveX > 0;
                            }
                        }
                        else
                        {
                            // Left/right is dominant. Stay cardinal unless the
                            // vertical component is large enough to enter a diagonal.
                            aimLeft = driveX < 0;
                            aimRight = driveX > 0;

                            if (absY >= absX * altDriveDiagonalEntryRatio)
                            {
                                aimForward = driveY < 0;
                                aimBackward = driveY > 0;
                            }
                        }
                    }
                }

                bool pedalForward = !ac6Profile && vtEnabled && n.Throttle >= DriveThreshold;
                bool pedalBackward = !ac6Profile && vtEnabled && n.Brake >= DriveThreshold;
                bool clutchDown = vtEnabled && n.Clutch >= DriveThreshold;
                bool rotationLeft = oxygenEnabled && n.Rotation <= -RotationThreshold;
                bool rotationRight = oxygenEnabled && n.Rotation >= RotationThreshold;

                output.SetKey(KeyboardMouseOutput.Keys.W, pedalForward || aimForward);
                output.SetKey(KeyboardMouseOutput.Keys.S, pedalBackward || aimBackward);
                output.SetKey(KeyboardMouseOutput.Keys.Space, clutchDown);
                output.SetKey(KeyboardMouseOutput.Keys.A, rotationLeft || aimLeft);
                output.SetKey(KeyboardMouseOutput.Keys.D, rotationRight || aimRight);

                // AC6 pedal semantics:
                // Throttle = Boost (Tab), Brake = Quick Boost (Left Shift).
                bool ac6BoostHeld = ac6Profile && vtEnabled && n.Throttle >= DriveThreshold;
                bool ac6QuickBoostHeld = ac6Profile && vtEnabled && n.Brake >= DriveThreshold;
                output.SetKey(KeyboardMouseOutput.Keys.Tab, ac6BoostHeld);
                output.SetKey(KeyboardMouseOutput.Keys.Shift, ac6QuickBoostHeld);

                // Profile 5 Quick Turn: hold Sight Change click, then push the
                // Sight Change lever hard left/right. AC6 expects Boost first,
                // THEN direction. We also inject an approximately 90-degree
                // camera burst so the view follows the chassis.
                if (ac6QuickTurnLeftRequested)
                {
                    ExecuteAc6QuickTurn(
                        output,
                        left: true,
                        leaveBoostDown: ac6BoostHeld,
                        leaveDirectionDown: rotationLeft || aimLeft,
                        mousePixels: ac6QuickTurnMousePixels,
                        leadMs: ac6QuickTurnLeadMs,
                        holdMs: ac6QuickTurnHoldMs);
                }
                else if (ac6QuickTurnRightRequested)
                {
                    ExecuteAc6QuickTurn(
                        output,
                        left: false,
                        leaveBoostDown: ac6BoostHeld,
                        leaveDirectionDown: rotationRight || aimRight,
                        mousePixels: ac6QuickTurnMousePixels,
                        leadMs: ac6QuickTurnLeadMs,
                        holdMs: ac6QuickTurnHoldMs);
                }

                // Sight Change is arrow keys only in the generic normal profile.
                output.SetKey(KeyboardMouseOutput.Keys.Left,
                    !ac6Profile && !alternateControlMode && n.SightX <= -SightThreshold);
                output.SetKey(KeyboardMouseOutput.Keys.Right,
                    !ac6Profile && !alternateControlMode && n.SightX >= SightThreshold);
                output.SetKey(KeyboardMouseOutput.Keys.Up,
                    !ac6Profile && !alternateControlMode && n.SightY <= -SightThreshold);
                output.SetKey(KeyboardMouseOutput.Keys.Down,
                    !ac6Profile && !alternateControlMode && n.SightY >= SightThreshold);

                if (ac6Profile)
                {
                    // PROFILE 5 - ARMORED CORE VI
                    // Profile5.ini is authoritative for these physical button bindings.
                    // Held semantics preserve charged weapons and modifier combinations.
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 1, bufferEnabled,
                        defaultMouse: KeyboardMouseOutput.MouseButton.Left);   // Left Hand Weapon
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 2,
                        bufferEnabled && !suppressTriggerUntilRelease,
                        defaultMouse: KeyboardMouseOutput.MouseButton.Right); // Right Hand Weapon
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 3, bufferEnabled,
                        defaultMouse: KeyboardMouseOutput.MouseButton.Middle); // Target Assist

                    // EJECT remains a special Core Expansion macro.
                    ApplyAc6CoreExpansion(output, raw, previous, enabled: bufferEnabled && !resetArmed);

                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 5,
                        bufferEnabled && !resetArmChord, defaultKey: KeyboardMouseOutput.Keys.F);
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 6,
                        bufferEnabled && !resetArmChord, defaultKey: KeyboardMouseOutput.Keys.I);
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 7, bufferEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.Control); // Assault Boost

                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 10, bufferEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.Escape);
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 18, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.C); // Repair Kit
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 19, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.V); // Scan
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 20, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.P); // Purge modifier
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 26, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.Q); // Left Shoulder
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 27, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.E); // Right Shoulder
                    ApplyProfileHeldBinding(output, runtimeBindings, raw, previous, 28, fuelEnabled,
                        defaultKey: KeyboardMouseOutput.Keys.R); // Reload / Shift Control

                    // Monitor zoom buttons remain convenient left/right utility keys.
                    SetMomentary(output, raw, previous, 12, KeyboardMouseOutput.Keys.Left, bufferEnabled);
                    SetMomentary(output, raw, previous, 13, KeyboardMouseOutput.Keys.Right, bufferEnabled);

                    // Explicitly consume otherwise unused generic panel controls so
                    // old defaults cannot leak into Profile 5.
                    int[] unusedAc6Buttons = { 8, 9, 11, 14, 15, 16, 17, 21, 22, 23, 24, 25 };
                    foreach (int b in unusedAc6Buttons)
                        previous[b] = raw.Button(b);
                }
                else
                {
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
                bool suppressTriggerForSwap =
                    raw.Button(7) || suppressTriggerUntilRelease;

                ApplyFixedMouseButton(
                    output,
                    raw,
                    previous,
                    button: 1,
                    enabled: bufferEnabled,
                    mouseButton:
                        preferences.SwapMainWeaponAndTrigger
                            ? KeyboardMouseOutput.MouseButton.Right
                            : KeyboardMouseOutput.MouseButton.Left);

                ApplyFixedMouseButton(
                    output,
                    raw,
                    previous,
                    button: 2,
                    enabled: bufferEnabled && !suppressTriggerForSwap,
                    mouseButton:
                        preferences.SwapMainWeaponAndTrigger
                            ? KeyboardMouseOutput.MouseButton.Left
                            : KeyboardMouseOutput.MouseButton.Right);

                // Lock On remains rebindable through START + Lock On.
                // Default action stays Right Mouse unless the user changes it.
                ApplyLockOnButton(
                    output,
                    runtimeBindings,
                    raw,
                    previous,
                    ref activeLockOnMouse,
                    bufferEnabled);

                // Common keyboard-oriented panel layout.
                SetMomentary(
                    output,
                    raw,
                    previous,
                    4,
                    KeyboardMouseOutput.Keys.Escape,
                    bufferEnabled && !resetArmed); // Eject
                SetMomentary(output, raw, previous, 5, KeyboardMouseOutput.Keys.H, bufferEnabled && !resetArmChord);      // Cockpit Hatch
                SetMomentary(
                    output, raw, previous, 6, KeyboardMouseOutput.Keys.I,
                    bufferEnabled && !resetArmChord); // Ignition
                SetMomentary(output, raw, previous, 7, KeyboardMouseOutput.Keys.Enter, bufferEnabled);  // Start
                SetRebindable(output, runtimeBindings, raw, previous, 8, KeyboardMouseOutput.Keys.E, bufferEnabled);      // Open/Close
                SetRebindable(output, runtimeBindings, raw, previous, 9, KeyboardMouseOutput.Keys.Z, bufferEnabled);      // Map Zoom
                SetRebindable(output, runtimeBindings, raw, previous, 10, KeyboardMouseOutput.Keys.Tab, bufferEnabled);   // Mode Select
                SetRebindable(output, runtimeBindings, raw, previous, 11, KeyboardMouseOutput.Keys.C, bufferEnabled);     // Sub Monitor
                SetRebindable(output, runtimeBindings, raw, previous, 12, KeyboardMouseOutput.Keys.OemPlus, bufferEnabled);  // Monitor Zoom In
                SetRebindable(output, runtimeBindings, raw, previous, 13, KeyboardMouseOutput.Keys.OemMinus, bufferEnabled); // Monitor Zoom Out
                SetRebindable(output, runtimeBindings, raw, previous, 14, KeyboardMouseOutput.Keys.F, fuelEnabled);     // F.S.S.
                SetRebindable(output, runtimeBindings, raw, previous, 15, KeyboardMouseOutput.Keys.G, fuelEnabled);     // Manipulator
                SetRebindable(output, runtimeBindings, raw, previous, 16, KeyboardMouseOutput.Keys.L, fuelEnabled);     // Line Color
                SetRebindable(output, runtimeBindings, raw, previous, 17, KeyboardMouseOutput.Keys.X, fuelEnabled);     // Washing
                SetRebindable(output, runtimeBindings, raw, previous, 18, KeyboardMouseOutput.Keys.C, fuelEnabled);     // Extinguisher
                SetRebindable(output, runtimeBindings, raw, previous, 19, KeyboardMouseOutput.Keys.V, fuelEnabled);     // Chaff
                SetRebindable(output, runtimeBindings, raw, previous, 20, KeyboardMouseOutput.Keys.T, fuelEnabled);     // Tank Detach
                SetRebindable(
                    output, runtimeBindings, raw, previous, 21,
                    KeyboardMouseOutput.Keys.O,
                    fuelEnabled && !suppressOverrideUntilRelease); // Override
                SetRebindable(output, runtimeBindings, raw, previous, 22, KeyboardMouseOutput.Keys.N, fuelEnabled);     // Night Scope
                SetRebindable(output, runtimeBindings, raw, previous, 23, KeyboardMouseOutput.Keys.F1, fuelEnabled);
                SetRebindable(output, runtimeBindings, raw, previous, 24, KeyboardMouseOutput.Keys.F2, fuelEnabled);
                SetRebindable(output, runtimeBindings, raw, previous, 25, KeyboardMouseOutput.Keys.F3, fuelEnabled);
                SetRebindable(output, runtimeBindings, raw, previous, 26, KeyboardMouseOutput.Keys.Q, fuelEnabled);     // Main Weapon Control
                SetRebindable(output, runtimeBindings, raw, previous, 27, KeyboardMouseOutput.Keys.G, fuelEnabled);     // Sub Weapon Control
                SetRebindable(output, runtimeBindings, raw, previous, 28, KeyboardMouseOutput.Keys.R, fuelEnabled);     // Magazine Change

                }

                // Communications 1..5. When OVERRIDE is held these become
                // profile selectors and their normal game bindings are suppressed.
                SetRebindable(output, runtimeBindings, raw, previous, 29, KeyboardMouseOutput.Keys.D1,
                    fuelEnabled && !suppressCommUntilRelease[0] && !anyProfileChord);
                SetRebindable(output, runtimeBindings, raw, previous, 30, KeyboardMouseOutput.Keys.D2,
                    fuelEnabled && !suppressCommUntilRelease[1] && !anyProfileChord);
                SetRebindable(output, runtimeBindings, raw, previous, 31, KeyboardMouseOutput.Keys.D3,
                    fuelEnabled && !suppressCommUntilRelease[2] && !anyProfileChord);
                SetRebindable(output, runtimeBindings, raw, previous, 32, KeyboardMouseOutput.Keys.D4,
                    fuelEnabled && !suppressCommUntilRelease[3] && !anyProfileChord);
                SetRebindable(output, runtimeBindings, raw, previous, 33, KeyboardMouseOutput.Keys.D5,
                    fuelEnabled && !suppressCommUntilRelease[4] && !anyProfileChord);

                // Sight Change stick click.
                ApplySightChangeClick(
                    output,
                    runtimeBindings,
                    raw,
                    previous,
                    ref activeSightClickMouse,
                    enabled: !ac6Profile);

                // The five physical toggle switches are subsystem power controls.
                // They no longer emit F6-F10 keyboard events.

                // FILTER CONTROL enables the Gear Lever as an Aiming Lever
                // sensitivity/mode selector. Gear changes no longer emit
                // keyboard Numpad keys in Keyboard + Mouse mode.
                //
                // Gear 1 = 65% precision
                // Gear 2 = 100% normal/default
                // Gear 3 = 130%
                // Gear 4 = 165%
                // Gear 5 = 200%
                // Neutral = preserve current sensitivity
                // Reverse = toggle vertical mouse inversion each time entered
                if (!filterEnabled)
                {
                    // Track the physical shifter while locked so turning Filter
                    // back ON does not replay a gear transition.
                    previousGear = raw.GearRaw;
                }
                else if (raw.GearRaw != previousGear)
                {
                    switch (raw.GearRaw)
                    {
                        case 1:
                            aimSensitivity = 0.65;
                            sensitivityGear = 1;
                            break;

                        case 2:
                            aimSensitivity = 1.00;
                            sensitivityGear = 2;
                            break;

                        case 3:
                            aimSensitivity = 1.30;
                            sensitivityGear = 3;
                            break;

                        case 4:
                            aimSensitivity = 1.65;
                            sensitivityGear = 4;
                            break;

                        case 5:
                            aimSensitivity = 2.00;
                            sensitivityGear = 5;
                            break;

                        case -2:
                            mouseYInverted = !mouseYInverted;
                            output.ResetMouseMotion();
                            break;

                        case -1:
                            // Neutral preserves the current aiming settings.
                            break;
                    }

                    previousGear = raw.GearRaw;
                }

                // Tuner dial:
                //   Normal turn       -> mouse wheel
                //   START + turn      -> fine Aiming Lever sensitivity trim
                //                        clockwise +5%, counter-clockwise -5%
                //
                // Any actual Gear Lever change resets aimSensitivity to that
                // gear's exact preset above, discarding the fine trim.
                if (previousTuner.HasValue && raw.Tuner != previousTuner.Value)
                {
                    int delta = raw.Tuner - previousTuner.Value;
                    if (delta > 127) delta -= 256;
                    if (delta < -127) delta += 256;

                    if (delta != 0)
                    {
                        int direction = Math.Sign(delta);

                        if (raw.Button(7))
                        {
                            // On this controller's tuner report, increasing raw
                            // positions are treated as clockwise, matching the
                            // existing tuner direction convention.
                            aimSensitivity += direction * FineSensitivityStep;

                            // Keep the setting usable without allowing zero or
                            // absurd runaway values.
                            aimSensitivity = Math.Clamp(
                                aimSensitivity,
                                0.25,
                                2.50);

                            Console.WriteLine();
                            Console.WriteLine();
                            Console.WriteLine(
                                $"AIM FINE TRIM: {aimSensitivity * 100:0}%");
                        }
                        else
                        {
                            output.MouseWheel(direction);
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

            }
        }
        finally
        {
            try { leds?.AllOff(); } catch { }
        }
    }

    private static void ExecuteAc6QuickTurn(
        KeyboardMouseOutput output,
        bool left,
        bool leaveBoostDown,
        bool leaveDirectionDown,
        int mousePixels,
        int leadMs,
        int holdMs)
    {
        ushort direction = left
            ? KeyboardMouseOutput.Keys.A
            : KeyboardMouseOutput.Keys.D;

        // AC6 Quick Turn wants Boost held BEFORE a fresh direction input.
        // Force the direction up first so this works even while the Aiming
        // Lever was already holding A/D for normal movement.
        output.SetKey(direction, false);
        Thread.Sleep(15);
        output.SetKey(KeyboardMouseOutput.Keys.Tab, true);
        Thread.Sleep(leadMs);
        output.SetKey(direction, true);

        // Keep the camera visually attached to the AC during the snap.
        output.MoveMousePixels(left ? -mousePixels : mousePixels, 0);

        Thread.Sleep(holdMs);
        output.SetKey(direction, leaveDirectionDown);
        output.SetKey(KeyboardMouseOutput.Keys.Tab, leaveBoostDown);
    }

    private static void ApplyDirectMouseButton(
        KeyboardMouseOutput output,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        bool enabled,
        KeyboardMouseOutput.MouseButton mouseButton)
    {
        bool pressed = enabled && raw.Button(button);
        output.SetMouseButton(mouseButton, pressed);
        previous[button] = pressed;
    }

    private static void ApplyAc6CoreExpansion(
        KeyboardMouseOutput output,
        SteelBattalionState raw,
        bool[] previous,
        bool enabled)
    {
        const int button = 4; // Eject
        bool pressed = enabled && raw.Button(button);

        if (pressed && !previous[button])
        {
            bool leaveCtrlDown = raw.Button(7); // START may already be holding Assault Boost.
            output.SetKey(KeyboardMouseOutput.Keys.Control, true);
            output.SetKey(KeyboardMouseOutput.Keys.R, true);
            Thread.Sleep(80);
            output.SetKey(KeyboardMouseOutput.Keys.R, false);
            output.SetKey(KeyboardMouseOutput.Keys.Control, leaveCtrlDown);
        }

        previous[button] = pressed;
    }

    private static void ApplyProfileHeldBinding(
        KeyboardMouseOutput output,
        RuntimeBindings bindings,
        SteelBattalionState raw,
        bool[] previous,
        int button,
        bool enabled,
        ushort? defaultKey = null,
        KeyboardMouseOutput.MouseButton? defaultMouse = null)
    {
        bool pressed = enabled && raw.Button(button);
        RuntimeBindings.BindingValue? binding = bindings.Get(button);

        if (binding is not null)
        {
            if (binding.Kind == RuntimeBindings.BindingKind.Mouse)
                output.SetMouseButton(binding.MouseButton, pressed);
            else
                output.SetKey(binding.VirtualKey, pressed);
        }
        else if (defaultMouse.HasValue)
        {
            output.SetMouseButton(defaultMouse.Value, pressed);
        }
        else if (defaultKey.HasValue)
        {
            output.SetKey(defaultKey.Value, pressed);
        }

        previous[button] = pressed;
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
        SteelBattalionState raw,
        bool[] previous,
        ref KeyboardMouseOutput.MouseButton? activeMouse,
        bool enabled)
    {
        const int button = 34;
        const ushort defaultKey = KeyboardMouseOutput.Keys.M;

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
        SteelBattalionState raw,
        bool[] previous,
        ref KeyboardMouseOutput.MouseButton? activeMouse,
        bool enabled)
    {
        const int button = 3;
        const ushort defaultKey = KeyboardMouseOutput.Keys.Space;

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
