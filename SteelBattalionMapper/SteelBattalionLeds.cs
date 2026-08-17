namespace SteelBattalionMapper;

internal sealed class SteelBattalionLeds
{
    private readonly SteelBattalionUsb _usb;
    private readonly byte[] _data = new byte[22];
    private readonly byte[] _lastSent = new byte[22];

    public int Intensity { get; set; } = 10;

    private bool _prevFilter;
    private bool _prevFuel;
    private bool _prevBuffer;
    private bool _subsystemStateInitialized;

    private Animation? _filterAnimation;
    private Animation? _fuelAnimation;
    private Animation? _bufferAnimation;

    private sealed class Animation
    {
        public required int[] LedIds { get; init; }
        public required DateTime Started { get; init; }
        public required TimeSpan Step { get; init; }
        public required bool AllAtOnce { get; init; }
        public required TimeSpan Total { get; init; }
        public int BlinkCount { get; init; } = 1;
    }

    public SteelBattalionLeds(SteelBattalionUsb usb, int outputEndpoint)
    {
        _usb = usb;
        _usb.OpenOutputEndpoint(outputEndpoint);
    }

    public void UpdateSubsystems(
        SteelBattalionState state,
        bool filterEnabled,
        bool oxygenEnabled,
        bool fuelEnabled,
        bool bufferEnabled,
        bool vtEnabled)
    {
        DateTime now = DateTime.UtcNow;

        // The first controller report establishes the physical switch baseline.
        // Do NOT treat switches that were already ON before mapper startup as
        // fresh power-on transitions.
        if (!_subsystemStateInitialized)
        {
            _prevFilter = filterEnabled;
            _prevFuel = fuelEnabled;
            _prevBuffer = bufferEnabled;
            _subsystemStateInitialized = true;
        }

        // Only physical transitions AFTER initialization start animations.
        if (filterEnabled != _prevFilter)
        {
            _filterAnimation = filterEnabled
                ? StartChase(new[] { 35,36,37,38,39,40,41 }, now)
                : StartFlash(new[] { 35,36,37,38,39,40,41 }, now);

            _prevFilter = filterEnabled;
        }

        if (fuelEnabled != _prevFuel)
        {
            _fuelAnimation = fuelEnabled
                ? StartChase(Enumerable.Range(14, 20).ToArray(), now) // 14..33
                : StartFlash(Enumerable.Range(14, 20).ToArray(), now);

            _prevFuel = fuelEnabled;
        }

        if (bufferEnabled != _prevBuffer)
        {
            _bufferAnimation = bufferEnabled
                ? StartChase(Enumerable.Range(4, 10).ToArray(), now) // 4..13
                : StartFlash(Enumerable.Range(4, 10).ToArray(), now);

            _prevBuffer = bufferEnabled;
        }

        Array.Clear(_data);

        // Animations take visual precedence over normal button flashes.
        bool filterAnimating = ApplyAnimation(_filterAnimation, now);
        if (filterAnimating && IsFinished(_filterAnimation!, now))
            _filterAnimation = null;

        bool fuelAnimating = ApplyAnimation(_fuelAnimation, now);
        if (fuelAnimating && IsFinished(_fuelAnimation!, now))
            _fuelAnimation = null;

        bool bufferAnimating = ApplyAnimation(_bufferAnimation, now);
        if (bufferAnimating && IsFinished(_bufferAnimation!, now))
            _bufferAnimation = null;

        // When active and not animating, LEDs remain OFF unless their physical
        // button is being pressed. Pressed buttons light at full intensity.
        if (bufferEnabled && _bufferAnimation is null)
        {
            for (int button = 4; button <= 13; button++)
            {
                if (state.Button(button))
                    SetLed(button, 15);
            }
        }

        if (fuelEnabled && _fuelAnimation is null)
        {
            for (int button = 14; button <= 33; button++)
            {
                if (state.Button(button))
                    SetLed(button, 15);
            }
        }

        // Gear subsystem remains dark after the startup chase except that the
        // current gear flashes/illuminates on actual gear movement.
        if (filterEnabled && _filterAnimation is null)
        {
            int gearLed = state.GearRaw switch
            {
                -2 => 35,
                -1 => 36,
                 1 => 37,
                 2 => 38,
                 3 => 39,
                 4 => 40,
                 5 => 41,
                 _ => -1
            };

            if (gearLed >= 0)
                SetLed(gearLed, 15);
        }

        // Oxygen and VT control input-only hardware with no dedicated lamps.
        SendIfChanged();
    }

    private static Animation StartChase(int[] ids, DateTime now)
        => new()
        {
            LedIds = ids,
            Started = now,
            Step = TimeSpan.FromMilliseconds(180),
            AllAtOnce = false,
            Total = TimeSpan.FromMilliseconds(Math.Max(1, ids.Length) * 180 + 250)
        };

    private static Animation StartFlash(int[] ids, DateTime now)
        => new()
        {
            LedIds = ids,
            Started = now,
            // Two slow blinks:
            // ON 550 ms -> OFF 350 ms -> ON 550 ms -> OFF.
            Step = TimeSpan.FromMilliseconds(900),
            AllAtOnce = true,
            BlinkCount = 2,
            Total = TimeSpan.FromMilliseconds(1800)
        };

    private bool ApplyAnimation(Animation? animation, DateTime now)
    {
        if (animation is null)
            return false;

        TimeSpan elapsed = now - animation.Started;
        if (elapsed >= animation.Total)
            return true;

        if (animation.AllAtOnce)
        {
            // Slow double-blink waveform. Each 900 ms cycle is ON for 550 ms
            // and OFF for 350 ms. Two cycles = 1.8 seconds total.
            double cycleMs = animation.Step.TotalMilliseconds;
            double phaseMs = elapsed.TotalMilliseconds % cycleMs;
            int cycleIndex = (int)(elapsed.TotalMilliseconds / cycleMs);

            bool on =
                cycleIndex < animation.BlinkCount &&
                phaseMs < 550.0;

            if (on)
            {
                foreach (int id in animation.LedIds)
                    SetLed(id, 15);
            }

            return true;
        }

        int index = (int)(elapsed.TotalMilliseconds / animation.Step.TotalMilliseconds);

        if (index >= 0 && index < animation.LedIds.Length)
            SetLed(animation.LedIds[index], 15);

        return true;
    }

    private static bool IsFinished(Animation animation, DateTime now)
        => now - animation.Started >= animation.Total;

    public bool CanLightButton(int button)
        => button is >= 4 and <= 33;

    public void ShowBindingTarget(int button)
    {
        if (!CanLightButton(button))
            return;
        Array.Clear(_data);
        SetLed(button, 15);
        SendIfChanged(force: true);
    }

    public async Task FlashResetArmedAsync(CancellationToken token)
    {
        // Eject lamp = LED/button 4.
        for (int i = 0; i < 2; i++)
        {
            token.ThrowIfCancellationRequested();

            Array.Clear(_data);
            SetLed(4, 15);
            SendIfChanged(force: true);
            await Task.Delay(350, token);

            Array.Clear(_data);
            SendIfChanged(force: true);
            await Task.Delay(250, token);
        }
    }

    public async Task ConfirmBindingAsync(int button, CancellationToken token)
    {
        if (!CanLightButton(button))
            return;

        for (int i = 0; i < 2; i++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(_data);
            SetLed(button, 15);
            SendIfChanged(force: true);
            await Task.Delay(240, token);

            Array.Clear(_data);
            SendIfChanged(force: true);
            await Task.Delay(180, token);
        }
    }

    public void ShowOnly(int ledId, int intensity = 15)
    {
        Array.Clear(_data);
        SetLed(ledId, intensity);
        SendIfChanged(force: true);
    }

    public void AllOff()
    {
        Array.Clear(_data);
        _usb.WriteLedPacket(_data);
        Array.Copy(_data, _lastSent, _data.Length);
    }

    private void SendIfChanged(bool force = false)
    {
        if (force || !_data.SequenceEqual(_lastSent))
        {
            _usb.WriteLedPacket(_data);
            Array.Copy(_data, _lastSent, _data.Length);
        }
    }

    private void SetLed(int ledId, int intensity)
    {
        intensity = Math.Clamp(intensity, 0, 15);

        int nibble = ledId % 2;
        int bytePos = (ledId - nibble) / 2;

        if (bytePos < 0 || bytePos >= _data.Length)
            return;

        if (nibble == 1)
            _data[bytePos] = (byte)((_data[bytePos] & 0x0F) | (intensity << 4));
        else
            _data[bytePos] = (byte)((_data[bytePos] & 0xF0) | intensity);
    }
}
