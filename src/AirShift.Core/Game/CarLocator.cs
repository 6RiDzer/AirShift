using System.Text;
using AirShift.Core.Memory;

namespace AirShift.Core.Game;

public sealed record CarReading(
    long Entity,
    long Car,
    float[] HubHeights,
    float[] Radii,
    float SpeedMs,
    int? CarId)
{
    public double SpeedKmh => Math.Abs(SpeedMs) * 3.6;
}

public enum LocateStatus
{
    Found,
    NoContext,
    NoList,
    NoCar,
    Unreadable,
}

public sealed record LocateResult(LocateStatus Status, CarReading? Car, string Message, int Entities, int Rejected);

/// <summary>
/// Finds the player's car in the game's memory using a <see cref="GameProfile"/>, and reads or
/// writes its four wheel hub heights. Every value is range-checked: a layout that does not fit
/// this build reads as "not found", never as a car.
/// </summary>
public sealed class CarLocator
{
    private const int ScanChunk = 4 * 1024 * 1024;

    private readonly IGameMemory _memory;
    private readonly GameProfile _profile;
    private long? _contextRva;

    public CarLocator(IGameMemory memory, GameProfile profile)
    {
        _memory = memory;
        _profile = profile;
    }

    public GameProfile Profile => _profile;

    /// <summary>How the context global was found: "signature", "fallback" or a failure reason.</summary>
    public string ContextSource { get; private set; } = "not resolved";

    public int SignatureHits { get; private set; } = -1;

    public long? ResolveContext()
    {
        if (_contextRva is { } cached) return cached;

        var ctx = _profile.Context;
        var signature = Signature.Parse(ctx.Signature);
        var hits = ScanModule(signature, 4);
        SignatureHits = hits.Count;
        if (hits.Count == 1)
        {
            var instruction = hits[0] + ctx.InstructionOffset;
            var displacement = _memory.Int32(instruction + ctx.DisplacementOffset);
            if (displacement is { } disp)
            {
                var target = instruction + ctx.InstructionLength + disp;
                var rva = target - _memory.ModuleBase;
                if (rva > 0 && rva < _memory.ModuleSize && _memory.Pointer(target) is not null)
                {
                    ContextSource = "signature";
                    _contextRva = rva;
                    return rva;
                }
            }
        }

        if (ctx.FallbackRva is { } fallback && fallback.Value > 0 && fallback.Value < _memory.ModuleSize
            && _memory.Pointer(_memory.ModuleBase + fallback.Value) is not null)
        {
            ContextSource = hits.Count == 0 ? "fallback (signature not found)" : $"fallback ({hits.Count} signature hits)";
            _contextRva = fallback.Value;
            return fallback.Value;
        }

        ContextSource = hits.Count == 0 ? "signature not found" : $"signature ambiguous ({hits.Count} hits)";
        return null;
    }

    /// <summary>Addresses (absolute) where the signature matches inside the main module.</summary>
    public List<long> ScanModule(Signature signature, int limit)
    {
        var hits = new List<long>();
        var size = _memory.ModuleSize;
        var overlap = signature.Length - 1;
        var buffer = new byte[ScanChunk + overlap];
        for (long offset = 0; offset < size && hits.Count < limit; offset += ScanChunk)
        {
            var length = (int)Math.Min(ScanChunk + overlap, size - offset);
            var span = buffer.AsSpan(0, length);
            if (!_memory.Read(_memory.ModuleBase + offset, span))
            {
                // Part of the image may be unreadable (guard pages). Retry in small pages.
                span.Clear();
                const int page = 0x1000;
                for (var p = 0; p < length; p += page)
                {
                    var n = Math.Min(page, length - p);
                    if (!_memory.Read(_memory.ModuleBase + offset + p, span.Slice(p, n))) span.Slice(p, n).Clear();
                }
            }
            foreach (var hit in signature.FindAll(span, limit - hits.Count))
            {
                if (hit < ScanChunk || offset + ScanChunk >= size) hits.Add(_memory.ModuleBase + offset + hit);
            }
        }
        return hits;
    }

    public LocateResult Locate()
    {
        var rva = ResolveContext();
        if (rva is null) return new(LocateStatus.NoContext, null, "This game build is not supported yet (" + ContextSource + ").", 0, 0);

        long? address = _memory.ModuleBase + rva.Value;
        foreach (var offset in _profile.Chain)
        {
            var next = _memory.Pointer(address!.Value);
            if (next is null) return new(LocateStatus.NoList, null, "No car yet. Drive out into the world first.", 0, 0);
            address = next.Value + offset.Value;
        }

        var list = _memory.Pointer(address!.Value);
        if (list is null) return new(LocateStatus.NoList, null, "No car yet. Drive out into the world first.", 0, 0);

        var begin = _memory.Pointer(list.Value + _profile.EntityList.Begin.Value);
        var end = _memory.Int64(list.Value + _profile.EntityList.End.Value);
        if (begin is null || end is null || end < begin || (end - begin) % 8 != 0
            || (end - begin) / 8 > _profile.EntityList.MaxEntries)
        {
            return new(LocateStatus.NoList, null, "No car yet. Drive out into the world first.", 0, 0);
        }

        var count = (int)((end.Value - begin.Value) / 8);
        var rejected = 0;
        for (var i = 0; i < count; i++)
        {
            var entity = _memory.Pointer(begin.Value + i * 8L);
            if (entity is null) continue;
            var car = _memory.Pointer(entity.Value + _profile.EntityToCar.Value);
            if (car is null) continue;
            var reading = Read(entity.Value, car.Value, out _);
            if (reading is not null) return new(LocateStatus.Found, reading, "Car ready.", count, rejected);
            rejected++;
        }

        return rejected > 0
            ? new(LocateStatus.Unreadable, null, "A car was found but it does not read correctly on this game build.", count, rejected)
            : new(LocateStatus.NoCar, null, "No car yet. Drive out into the world first.", count, 0);
    }

    /// <summary>Reads and checks one car. Returns null with a reason when any value is out of range.</summary>
    public CarReading? Read(long entity, long car, out string? reason)
    {
        var limits = _profile.Limits;
        if (_profile.Car.EngineSpeed is { } engineField)
        {
            var engine = _memory.Single(car + engineField.Value);
            if (engine is null || engine < -10 || engine > limits.EngineSpeedMax)
            {
                reason = "engine speed out of range";
                return null;
            }
        }

        var speed = _memory.Single(car + _profile.Car.Speed.Value);
        if (speed is null || Math.Abs(speed.Value) > limits.MaxSpeedMs)
        {
            reason = "speed out of range";
            return null;
        }

        var heights = ReadWheels(car, _profile.Wheels.HubHeight);
        var radii = ReadWheels(car, _profile.Wheels.Radius);
        if (heights is null || radii is null)
        {
            reason = "wheels unreadable";
            return null;
        }
        for (var w = 0; w < 4; w++)
        {
            if (heights[w] < limits.HubHeightMin || heights[w] > limits.HubHeightMax)
            {
                reason = $"hub height {heights[w]:0.000} out of range";
                return null;
            }
            if (radii[w] < limits.RadiusMin || radii[w] > limits.RadiusMax)
            {
                reason = $"wheel radius {radii[w]:0.000} out of range";
                return null;
            }
            if (heights[w] <= radii[w] * 0.5f)
            {
                reason = "hub height below half the wheel radius";
                return null;
            }
        }

        int? carId = null;
        if (_profile.CarIdentity is { } identity)
        {
            var record = _memory.Pointer(entity + identity.EntityToRecord.Value);
            if (record is not null)
            {
                var id = _memory.Int32(record.Value + identity.CarId.Value);
                if (id is > 0 and < 1_000_000) carId = id;
            }
        }

        reason = null;
        return new CarReading(entity, car, heights, radii, speed.Value, carId);
    }

    public float[]? ReadWheels(long car, HexValue field)
    {
        var values = new float[4];
        for (var w = 0; w < 4; w++)
        {
            var v = _memory.Single(_profile.Wheels.Address(car, w, field));
            if (v is null) return null;
            values[w] = v.Value;
        }
        return values;
    }

    public float? ReadSpeed(long car) => _memory.Single(car + _profile.Car.Speed.Value);

    /// <summary>Writes four hub heights. Refuses any value outside the profile limits.</summary>
    public bool WriteHubHeights(long car, IReadOnlyList<double> values)
    {
        var limits = _profile.Limits;
        if (values.Count != 4) return false;
        foreach (var v in values)
        {
            if (!double.IsFinite(v) || v < limits.HubHeightMin || v > limits.HubHeightMax) return false;
        }
        var ok = true;
        for (var w = 0; w < 4; w++)
        {
            ok &= _memory.WriteSingle(_profile.Wheels.Address(car, w, _profile.Wheels.HubHeight), (float)values[w]);
        }
        return ok;
    }

    /// <summary>A read-only report of every step, for the Probe button and bug reports.</summary>
    public string Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AirShift probe · profile {_profile.Id} r{_profile.Revision}");
        sb.AppendLine($"Game file version: {_memory.FileVersion ?? "unknown"} ({(_profile.MatchesBuild(_memory.FileVersion) ? "known build" : "build not in profile")})");
        sb.AppendLine($"Module base 0x{_memory.ModuleBase:X}, size 0x{_memory.ModuleSize:X}");
        var rva = ResolveContext();
        sb.AppendLine($"Context: {(rva is null ? "none" : "rva 0x" + rva.Value.ToString("X"))} via {ContextSource} (signature hits {SignatureHits})");
        var result = Locate();
        sb.AppendLine($"Entity list: {result.Entities} entries, {result.Rejected} rejected");
        sb.AppendLine($"Result: {result.Status} · {result.Message}");
        if (result.Car is { } car)
        {
            sb.AppendLine($"Car: entity 0x{car.Entity:X}, car 0x{car.Car:X}, car id {car.CarId?.ToString() ?? "unknown"}");
            sb.AppendLine($"Speed: {car.SpeedKmh:0.0} km/h");
            for (var w = 0; w < 4; w++)
            {
                sb.AppendLine($"  {_profile.Wheels.Order[w]}: hub height {car.HubHeights[w]:0.0000} m, radius {car.Radii[w]:0.0000} m, clearance {car.HubHeights[w] - car.Radii[w]:0.0000} m");
            }
            if (_profile.Kinematics is not null)
            {
                sb.AppendLine("Camber and track (kinematics tables):");
                new WheelTables(_memory, _profile).Describe(car.Car, sb);
            }
            else
            {
                sb.AppendLine("Camber and track: not in this profile.");
            }
        }
        sb.AppendLine("Nothing was written.");
        return sb.ToString();
    }
}
