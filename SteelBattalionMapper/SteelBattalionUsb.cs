using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace SteelBattalionMapper;

internal sealed class SteelBattalionUsb : IDisposable
{
    private readonly int _vid;
    private readonly int _pid;
    private readonly int _endpoint;
    private UsbContext? _context;
    private IUsbDevice? _device;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;
    private IUsbDevice? _wholeDevice;

    public SteelBattalionUsb(int vid, int pid, int endpoint)
    {
        if (endpoint is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(endpoint));

        _vid = vid;
        _pid = pid;
        _endpoint = endpoint;
    }

    public void Open()
    {
        _context = new UsbContext();

        var finder = new UsbDeviceFinder
        {
            Vid = _vid,
            Pid = _pid
        };

        _device = _context.Find(finder)
                  ?? throw new InvalidOperationException(
                      $"Steel Battalion controller VID {_vid:X4} / PID {_pid:X4} was not found through WinUSB.");

        _device.Open();

        // With WinUSB this cast is normally null; with a whole-device backend
        // it can be non-null and configuration/interface must be claimed.
        _wholeDevice = _device as IUsbDevice;
        if (_wholeDevice is not null)
        {
            _wholeDevice.SetConfiguration(1);
            _wholeDevice.ClaimInterface(0);
        }

        _reader = _device.OpenEndpointReader((ReadEndpointID)(0x80 | _endpoint));
    }

    public async Task<byte[]> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
            throw new InvalidOperationException("USB reader is not open.");

        // Original 0.2 driver configures a continuous reader with 32-byte transfers.
        byte[] buffer = new byte[32];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _reader.ReadAsync(buffer, 0, buffer.Length, 500);

            if (result.error == Error.Success && result.transferLength >= 26)
            {
                if (result.transferLength == buffer.Length)
                    return buffer;

                var packet = new byte[result.transferLength];
                Array.Copy(buffer, packet, result.transferLength);
                return packet;
            }

            // A timeout is normal while waiting for reports; keep reading.
            if (result.error == Error.Timeout)
                continue;

            throw new IOException(
                $"USB read failed: {result.error}, bytes={result.transferLength}");
        }
    }

    public void OpenOutputEndpoint(int endpoint)
    {
        if (_device is null || !_device.IsOpen)
            throw new InvalidOperationException("USB device is not open.");

        if (endpoint is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(endpoint));

        _writer = _device.OpenEndpointWriter((WriteEndpointID)endpoint);
    }

    public void WriteLedPacket(byte[] ledData)
    {
        if (_writer is null)
            throw new InvalidOperationException("USB output endpoint is not open.");

        // Known Steel Battalion LED payload is 22 bytes. The physical interrupt
        // pipe may have a larger max packet size, so send a zero-padded 32-byte
        // report with the LED payload at the front.
        byte[] packet = new byte[32];
        Array.Copy(ledData, packet, Math.Min(22, ledData.Length));

        Error error = _writer.Write(packet, 1000, out int transferred);
        if (error != Error.Success)
            throw new IOException($"USB LED write failed: {error}, bytes={transferred}");
    }

    public void Dispose()
    {
        try
        {
            if (_wholeDevice is not null)
                _wholeDevice.ReleaseInterface(0);
        }
        catch { }

        try
        {
            if (_device?.IsOpen == true)
                _device.Close();
        }
        catch { }

        // LibUsbDotNet 3.x closes the endpoint with the owning USB device/context.
        _reader = null;
        _writer = null;
        _context?.Dispose();
    }
}
