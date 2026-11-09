using System.Buffers.Binary;

namespace AirShift.Core.Memory;

/// <summary>Read and write access to the game's memory. Windows uses ReadProcessMemory; tests use a fake.</summary>
public interface IGameMemory : IDisposable
{
    long ModuleBase { get; }
    long ModuleSize { get; }
    string? FileVersion { get; }
    bool HasExited { get; }

    /// <summary>The game's process id, so a journal is only used on the game process it was written for.</summary>
    int ProcessId => 0;
    bool Read(long address, Span<byte> buffer);
    bool Write(long address, ReadOnlySpan<byte> buffer);
}

public static class GameMemoryExtensions
{
    public static long? Pointer(this IGameMemory memory, long address)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!memory.Read(address, buffer)) return null;
        var value = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        return IsUserPointer(value) ? value : null;
    }

    public static float? Single(this IGameMemory memory, long address)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!memory.Read(address, buffer)) return null;
        var value = BinaryPrimitives.ReadSingleLittleEndian(buffer);
        return float.IsFinite(value) ? value : null;
    }

    public static int? Int32(this IGameMemory memory, long address)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!memory.Read(address, buffer)) return null;
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    public static long? Int64(this IGameMemory memory, long address)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!memory.Read(address, buffer)) return null;
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    public static bool WriteSingle(this IGameMemory memory, long address, float value)
    {
        if (!float.IsFinite(value)) return false;
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        return memory.Write(address, buffer);
    }

    /// <summary>A plausible 64-bit user-mode address (above the null page, below the kernel split).</summary>
    public static bool IsUserPointer(long value) => value > 0x10000 && value < 0x7FFF_FFFF_FFFF;
}
