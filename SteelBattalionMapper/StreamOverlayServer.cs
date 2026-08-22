using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SteelBattalionMapper;

internal static class StreamTelemetryHub
{
    internal sealed class Frame
    {
        public long Sequence { get; init; }
        public bool[] Buttons { get; init; } = new bool[40];
        public string[] ButtonOutputs { get; init; } = new string[40];
        public ushort AimXRaw { get; init; }
        public ushort AimYRaw { get; init; }
        public short RotationRaw { get; init; }
        public short SightXRaw { get; init; }
        public short SightYRaw { get; init; }
        public ushort ClutchRaw { get; init; }
        public ushort BrakeRaw { get; init; }
        public ushort ThrottleRaw { get; init; }
        public byte Tuner { get; init; }
        public sbyte GearRaw { get; init; }
        public double AimX { get; init; }
        public double AimY { get; init; }
        public double Rotation { get; init; }
        public double SightX { get; init; }
        public double SightY { get; init; }
        public double Clutch { get; init; }
        public double Brake { get; init; }
        public double Throttle { get; init; }
        public double AimSensitivity { get; init; }
        public int SensitivityGear { get; init; }
        public bool MouseYInverted { get; init; }
        public bool FilterEnabled { get; init; }
        public bool OxygenEnabled { get; init; }
        public bool FuelEnabled { get; init; }
        public bool BufferEnabled { get; init; }
        public bool VtEnabled { get; init; }
    }

    private static long _sequence;
    private static Frame _latest = new();

    public static Frame Latest => Volatile.Read(ref _latest);

    public static void Update(
        SteelBattalionState raw,
        double aimX, double aimY, double rotation,
        double sightX, double sightY,
        double clutch, double brake, double throttle,
        double aimSensitivity, int sensitivityGear, bool mouseYInverted,
        bool filterEnabled, bool oxygenEnabled, bool fuelEnabled,
        bool bufferEnabled, bool vtEnabled, string[] buttonOutputs)
    {
        var buttons = new bool[40];
        for (int i = 1; i <= 39; i++)
            buttons[i] = raw.Button(i);

        var frame = new Frame
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Buttons = buttons,
            ButtonOutputs = buttonOutputs,
            AimXRaw = raw.AimX,
            AimYRaw = raw.AimY,
            RotationRaw = raw.Rotation,
            SightXRaw = raw.SightX,
            SightYRaw = raw.SightY,
            ClutchRaw = raw.Clutch,
            BrakeRaw = raw.Brake,
            ThrottleRaw = raw.Throttle,
            Tuner = raw.Tuner,
            GearRaw = raw.GearRaw,
            AimX = aimX,
            AimY = aimY,
            Rotation = rotation,
            SightX = sightX,
            SightY = sightY,
            Clutch = clutch,
            Brake = brake,
            Throttle = throttle,
            AimSensitivity = aimSensitivity,
            SensitivityGear = sensitivityGear,
            MouseYInverted = mouseYInverted,
            FilterEnabled = filterEnabled,
            OxygenEnabled = oxygenEnabled,
            FuelEnabled = fuelEnabled,
            BufferEnabled = bufferEnabled,
            VtEnabled = vtEnabled
        };

        Volatile.Write(ref _latest, frame);
    }
}

internal static class StreamAlertHub
{
    private static readonly object Sync = new();
    private static readonly Queue<string> Items = new();

    public static void Push(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 80) text = text[..80];
        lock (Sync)
        {
            Items.Enqueue(text);
            while (Items.Count > 4) Items.Dequeue();
        }
    }

    public static string[] Snapshot()
    {
        lock (Sync) return Items.Reverse().ToArray();
    }
}

internal sealed class StreamOverlayServer : IDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly AudioMeters _audio = new();
    private readonly string _assetRoot;
    private Task? _acceptLoop;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StreamOverlayServer(int port = 17871)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _assetRoot = FindAssetRoot();
    }

    public string Url => $"http://127.0.0.1:{_port}/";

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(100); }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true))
        {
            try
            {
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                string[] first = requestLine.Split(' ');
                string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                string target = first.Length > 1 ? first[1] : "/";
                string path = target.Split('?')[0];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                if (path.Equals("/events", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeEventsAsync(stream);
                    return;
                }

                if (path.Equals("/alert", StringComparison.OrdinalIgnoreCase))
                {
                    string text = GetQueryValue(target, "text");
                    StreamAlertHub.Push(text);
                    await SendTextAsync(stream, "200 OK", "OK");
                    return;
                }

                if (path.Equals("/layout", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "POST")
                    {
                        int length = headers.TryGetValue("Content-Length", out string? rawLength) && int.TryParse(rawLength, out int n) ? n : 0;
                        if (length <= 0 || length > 1_000_000)
                        {
                            await SendTextAsync(stream, "400 Bad Request", "Invalid layout payload");
                            return;
                        }
                        char[] chars = new char[length];
                        int read = 0;
                        while (read < length)
                        {
                            int r = await reader.ReadAsync(chars, read, length - read);
                            if (r <= 0) break;
                            read += r;
                        }
                        string json = new(chars, 0, read);
                        try { JsonDocument.Parse(json).Dispose(); }
                        catch { await SendTextAsync(stream, "400 Bad Request", "Invalid JSON"); return; }
                        string layoutPath = Path.Combine(_assetRoot, "overlay-layout.json");
                        string tempPath = layoutPath + ".tmp";
                        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                        File.Move(tempPath, layoutPath, true);
                        await SendTextAsync(stream, "200 OK", "SAVED");
                        return;
                    }
                    await ServeFileAsync(stream, "overlay-layout.json", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"widgets\":{},\"custom\":[]}"));
                    return;
                }

                if (path.Equals("/reset-layout", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    string layoutPath = Path.Combine(_assetRoot, "overlay-layout.json");
                    try { if (File.Exists(layoutPath)) File.Delete(layoutPath); } catch { }
                    await SendTextAsync(stream, "200 OK", "RESET");
                    return;
                }

                if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/overlay.html", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeFileAsync(stream, "overlay.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes("Overlay files missing."));
                    return;
                }

                if (path.Equals("/overlay-base.png", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeFileAsync(stream, "overlay-base.png", "image/png", Encoding.UTF8.GetBytes("overlay image missing"));
                    return;
                }

                await SendTextAsync(stream, "404 Not Found", "Not found");
            }
            catch { }
        }
    }

    private static string FindAssetRoot()
    {
        // dotnet run executes from bin/Release/...; saving there makes edits appear
        // to vanish on the next build. Prefer the project folder containing the
        // editable overlay assets, and fall back to the executable directory.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SteelBattalionMapper.csproj")) &&
                File.Exists(Path.Combine(dir.FullName, "overlay.html")))
                return dir.FullName;
        }

        string cwdProject = Path.Combine(Environment.CurrentDirectory, "SteelBattalionMapper");
        if (File.Exists(Path.Combine(cwdProject, "SteelBattalionMapper.csproj")))
            return cwdProject;

        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "SteelBattalionMapper.csproj")))
            return Environment.CurrentDirectory;

        return AppContext.BaseDirectory;
    }

    private static string GetQueryValue(string target, string name)
    {
        int q = target.IndexOf('?');
        if (q < 0 || q + 1 >= target.Length) return "";
        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }
        return "";
    }

    private async Task ServeFileAsync(NetworkStream stream, string fileName, string contentType, byte[] fallback)
    {
        string filePath = Path.Combine(_assetRoot, fileName);
        if (!File.Exists(filePath))
        {
            await SendBytesAsync(stream, "200 OK", contentType, fallback);
            return;
        }
        await SendBytesAsync(stream, "200 OK", contentType, await File.ReadAllBytesAsync(filePath));
    }

    private async Task ServeEventsAsync(NetworkStream stream)
    {
        string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: keep-alive\r\n" +
            "Access-Control-Allow-Origin: *\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cts.Token);
        await stream.FlushAsync(_cts.Token);

        long lastSequence = -1;
        while (!_cts.IsCancellationRequested && stream.CanWrite)
        {
            StreamTelemetryHub.Frame frame = StreamTelemetryHub.Latest;
            if (frame.Sequence != lastSequence)
            {
                lastSequence = frame.Sequence;
                var payload = new
                {
                    controller = frame,
                    audio = new { mic = _audio.MicPeak, desktop = _audio.DesktopPeak },
                    streamAlerts = StreamAlertHub.Snapshot()
                };
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            await Task.Delay(50, _cts.Token);
        }
    }

    private static async Task SendTextAsync(NetworkStream stream, string status, string text)
        => await SendBytesAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static async Task SendBytesAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        string headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptLoop?.Wait(500); } catch { }
        _audio.Dispose();
        _cts.Dispose();
    }

    private sealed class AudioMeters : IDisposable
    {
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _mic;
        private MMDevice? _desktop;

        public AudioMeters()
        {
            try { _enumerator = new MMDeviceEnumerator(); TryRefresh(); } catch { }
        }

        private void TryRefresh()
        {
            if (_enumerator is null) return;
            try
            {
                _mic?.Dispose(); _desktop?.Dispose();
                _mic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                _desktop = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }
        }

        public float MicPeak => ReadPeak(_mic);
        public float DesktopPeak => ReadPeak(_desktop);

        private float ReadPeak(MMDevice? device)
        {
            try { return Math.Clamp(device?.AudioMeterInformation.MasterPeakValue ?? 0f, 0f, 1f); }
            catch { TryRefresh(); return 0f; }
        }

        public void Dispose()
        {
            try { _mic?.Dispose(); } catch { }
            try { _desktop?.Dispose(); } catch { }
            try { _enumerator?.Dispose(); } catch { }
        }
    }
}
