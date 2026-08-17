using System.Buffers.Binary;

namespace SteelBattalionMapper;

public readonly record struct SteelBattalionState(
    ulong ButtonBits,
    ushort AimX,
    ushort AimY,
    short Rotation,
    short SightX,
    short SightY,
    ushort Clutch,
    ushort Brake,
    ushort Throttle,
    byte Tuner,
    sbyte GearRaw)
{
    public bool Button(int oneBasedIndex)
    {
        if (oneBasedIndex is < 1 or > 39)
            throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));

        return (ButtonBits & (1UL << (oneBasedIndex - 1))) != 0;
    }
}

public static class SteelBattalionPacketDecoder
{
    public const int MinimumPacketLength = 26;

    public static SteelBattalionState Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumPacketLength)
            throw new ArgumentException(
                $"Steel Battalion report must contain at least {MinimumPacketLength} bytes.",
                nameof(packet));

        // Layout copied from the original driver's SBC_INPUT_DATA structure.
        // Bytes 0-1 and 7 are padding/status bytes.
        ulong buttons =
            (ulong)packet[2] |
            ((ulong)packet[3] << 8) |
            ((ulong)packet[4] << 16) |
            ((ulong)packet[5] << 24) |
            ((ulong)(packet[6] & 0x7F) << 32);

        return new SteelBattalionState(
            ButtonBits: buttons,
            AimX: BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2)),
            AimY: BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2)),
            Rotation: BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(12, 2)),
            SightX: BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(14, 2)),
            SightY: BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(16, 2)),
            Clutch: BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(18, 2)),
            Brake: BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(20, 2)),
            Throttle: BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(22, 2)),
            Tuner: packet[24],
            GearRaw: unchecked((sbyte)packet[25]));
    }
}
