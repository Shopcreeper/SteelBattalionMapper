namespace SteelBattalionMapper;

internal static class Program
{
    private const int VendorId = 0x0A7B;
    private const int ProductId = 0xD000;

    private static readonly CancellationTokenSource Cancellation = new();

    public static async Task<int> Main(string[] args)
    {
        Console.Title = "Steel Battalion Mapper";
        Console.WriteLine("Steel Battalion Mapper - Final Keyboard/Mouse Release");
        Console.WriteLine("Original Steel Battalion Controller -> WinUSB -> Keyboard/Mouse");
        Console.WriteLine();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This mapper is intended for Windows 10/11.");
            return 1;
        }

        int endpointNumber = GetIntArg(args, "--endpoint", 2);
        int ledEndpoint = GetIntArg(args, "--led-endpoint", 1);
        int overlayPort = GetIntArg(args, "--overlay-port", 17871);
        bool overlayEnabled = !args.Any(a => string.Equals(a, "--no-overlay", StringComparison.OrdinalIgnoreCase));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cancellation.Cancel();
        };

        try
        {
            using StreamOverlayServer? overlay = overlayEnabled ? new StreamOverlayServer(overlayPort) : null;
            if (overlay is not null)
            {
                overlay.Start();
                Console.WriteLine($"OBS stream overlay: {overlay.Url}");
                Console.WriteLine("Add it to OBS as a 1920x1080 Browser Source.");
                Console.WriteLine($"HUD editor: {overlay.Url}?edit=1");
                Console.WriteLine($"HUD-only source: {overlay.Url}?hudonly=1");
                Console.WriteLine();
            }

            using var usb =
                new SteelBattalionUsb(
                    VendorId,
                    ProductId,
                    endpointNumber);

            usb.Open();

            Console.WriteLine(
                $"Controller connected on input endpoint 0x{0x80 | endpointNumber:X2}.");
            Console.WriteLine("Keyboard/mouse mode active.");
            Console.WriteLine("LED support active.");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();

            await KeyboardMouseRuntime.RunAsync(
                usb,
                enableLeds: true,
                ledEndpoint,
                Cancellation.Token);

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Mapper stopped because of an error:");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int GetIntArg(
        string[] args,
        string name,
        int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(
                args[i],
                name,
                StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int value))
            {
                return value;
            }
        }

        return fallback;
    }
}
