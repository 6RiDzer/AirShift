using System.Buffers.Binary;
using AirShift.Core.Game;
using AirShift.Core.Memory;

namespace AirShift.Tests;

/// <summary>A fake game process laid out like the FH6 profile describes.</summary>
public sealed class FakeGame : IGameMemory
{
    public const long Base = 0x1_4000_0000;
    public const long Heap = 0x2_0000_0000;
    public const long ContextRva = 0x10_0000;
    public const long SignatureOffset = 0x1000;
    public const long Car = Heap + 0x1_0000;
    public const long Entity = Heap + 0x8000;

    private readonly byte[] _module = new byte[0x20_0000];
    private readonly byte[] _heap = new byte[0x4_0000];
    private readonly GameProfile _profile;

    public FakeGame(GameProfile profile, string? version = "6.483.970.0", bool withSignature = true)
    {
        _profile = profile;
        FileVersion = version;
        if (withSignature) PlaceSignature();
        // Context global -> A -> B -> C -> list -> vector -> entity -> car.
        PutPtr(Base + ContextRva, Heap + 0x100);
        PutPtr(Heap + 0x100 + 0x1D0, Heap + 0x400);
        PutPtr(Heap + 0x400 + 0x128, Heap + 0x800);
        PutPtr(Heap + 0x800 + 0x370, Heap + 0x1000);
        PutPtr(Heap + 0x1000 + 0x18, Heap + 0x2000);
        PutPtr(Heap + 0x1000 + 0x20, Heap + 0x2000 + 8 * 2);
        PutPtr(Heap + 0x2000, Entity);
        PutPtr(Heap + 0x2008, Heap + 0x9000); // a second entity with no car
        PutPtr(Entity + 0x7740, Car);
        PutPtr(Entity + 0x2F0, Heap + 0x3000);
        PutI32(Heap + 0x3000 + 0x8C, 4242);
        PutF32(Car + 0x1B0, 90f);
        Speed = 0;
        SetStock([0.40f, 0.40f, 0.42f, 0.42f], [0.33f, 0.33f, 0.34f, 0.34f]);
    }

    public long ModuleBase => Base;
    public long ModuleSize => _module.Length;
    public string? FileVersion { get; }
    public bool HasExited { get; set; }
    public int Writes { get; private set; }

    public float Speed
    {
        set => PutF32(Car + 0x14EC, value / 3.6f);
    }

    public void SetStock(float[] heights, float[] radii)
    {
        for (var w = 0; w < 4; w++)
        {
            PutF32(_profile.Wheels.Address(Car, w, _profile.Wheels.HubHeight), heights[w]);
            PutF32(_profile.Wheels.Address(Car, w, _profile.Wheels.Radius), radii[w]);
        }
    }

    public float[] Heights()
    {
        var values = new float[4];
        for (var w = 0; w < 4; w++)
            values[w] = BinaryPrimitives.ReadSingleLittleEndian(Slice(_profile.Wheels.Address(Car, w, _profile.Wheels.HubHeight), 4));
        return values;
    }

    private void PlaceSignature()
    {
        var at = Base + SignatureOffset;
        byte[] pattern =
        [
            0x06, 0xE8, 1, 2, 3, 4, 0x90, 0x48, 0x8D, 0x4D, 0xC0, 0xE8, 5, 6, 7, 8,
            0x48, 0x8B, 0x15, 0, 0, 0, 0, 0x48, 0x8B, 0x82, 0xA8, 0x07, 0x00, 0x00,
        ];
        pattern.CopyTo(Slice(at, pattern.Length));
        var instruction = at + 16;
        var displacement = (int)(Base + ContextRva - (instruction + 7));
        BinaryPrimitives.WriteInt32LittleEndian(Slice(instruction + 3, 4), displacement);
    }

    public bool Read(long address, Span<byte> buffer)
    {
        var source = TrySlice(address, buffer.Length);
        if (source is null) return false;
        source.Value.Span.CopyTo(buffer);
        return true;
    }

    public bool Write(long address, ReadOnlySpan<byte> buffer)
    {
        var target = TrySlice(address, buffer.Length);
        if (target is null) return false;
        buffer.CopyTo(target.Value.Span);
        Writes++;
        return true;
    }

    private Memory<byte>? TrySlice(long address, int length)
    {
        if (address >= Base && address + length <= Base + _module.Length) return _module.AsMemory((int)(address - Base), length);
        if (address >= Heap && address + length <= Heap + _heap.Length) return _heap.AsMemory((int)(address - Heap), length);
        return null;
    }

    private Span<byte> Slice(long address, int length) => TrySlice(address, length)!.Value.Span;

    private void PutPtr(long address, long value) => BinaryPrimitives.WriteInt64LittleEndian(Slice(address, 8), value);
    private void PutI32(long address, int value) => BinaryPrimitives.WriteInt32LittleEndian(Slice(address, 4), value);
    private void PutF32(long address, float value) => BinaryPrimitives.WriteSingleLittleEndian(Slice(address, 4), value);

    public void Dispose()
    {
    }
}
