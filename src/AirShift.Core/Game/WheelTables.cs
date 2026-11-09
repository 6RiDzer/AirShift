using System.Buffers.Binary;
using System.Text;
using AirShift.Core.Memory;

namespace AirShift.Core.Game;

/// <summary>
/// One wheel's curve in its axle's kinematics table: camber (degrees) and lateral position
/// (metres) at each sample of the suspension's travel, and where it was read.
/// </summary>
public sealed record WheelCurve(long Table, long Start, double[] Camber, double[] TrackX, double[]? TrackXCopy)
{
    /// <summary>+1 when the wheel sits on the positive side of the centre line, -1 otherwise.</summary>
    public int Outward => TrackX.Average() >= 0 ? 1 : -1;

    /// <summary>This curve with every camber sample turned by <paramref name="camber"/> degrees and the wheel moved outward by <paramref name="track"/> metres.</summary>
    public WheelCurve Shifted(double camber, double track)
    {
        var dx = Outward * track;
        return this with
        {
            Camber = Camber.Select(c => c + camber).ToArray(),
            TrackX = TrackX.Select(x => x + dx).ToArray(),
            TrackXCopy = TrackXCopy?.Select(x => x + dx).ToArray(),
        };
    }

    public bool SameAs(WheelCurve other, double camberTolerance = 0.02, double trackTolerance = 0.0002)
    {
        if (Table != other.Table || Start != other.Start || Camber.Length != other.Camber.Length) return false;
        for (var i = 0; i < Camber.Length; i++)
        {
            if (Math.Abs(Camber[i] - other.Camber[i]) > camberTolerance) return false;
            if (Math.Abs(TrackX[i] - other.TrackX[i]) > trackTolerance) return false;
            if (TrackXCopy is not null && other.TrackXCopy is not null
                && Math.Abs(TrackXCopy[i] - other.TrackXCopy[i]) > trackTolerance) return false;
        }
        return true;
    }
}

/// <summary>
/// Reads, checks and writes the camber and track width curves of a car's wheels, as a
/// <see cref="KinematicsLayout"/> describes them. A curve that does not read as plausible
/// numbers is never returned, so nothing is written to a layout that does not fit.
/// </summary>
public sealed class WheelTables
{
    private readonly IGameMemory _memory;
    private readonly WheelLayout _wheels;
    private readonly KinematicsLayout _layout;

    public WheelTables(IGameMemory memory, GameProfile profile)
    {
        _memory = memory;
        _wheels = profile.Wheels;
        _layout = profile.Kinematics ?? throw new InvalidOperationException("This profile has no kinematics.");
    }

    public KinematicsLayout Layout => _layout;

    public WheelCurve? Read(long car, int wheel, out string? reason)
    {
        var block = _wheels.Address(car, wheel, new HexValue(0));
        var axle = _memory.Pointer(block + _layout.WheelToAxle);
        if (axle is null)
        {
            reason = "no axle pointer";
            return null;
        }
        var table = _memory.Pointer(axle.Value + _layout.AxleToTable);
        if (table is null)
        {
            reason = "no table pointer";
            return null;
        }
        var start = table.Value + (_wheels.IsLeft(wheel) ? _layout.LeftRegion : _layout.RightRegion);
        var bytes = new byte[_layout.EntryCount * _layout.EntrySize];
        if (!_memory.Read(start, bytes))
        {
            reason = "table unreadable";
            return null;
        }

        var n = _layout.EntryCount;
        var camber = new double[n];
        var track = new double[n];
        var copy = _layout.TrackXCopy is null ? null : new double[n];
        for (var i = 0; i < n; i++)
        {
            var entry = bytes.AsSpan(i * _layout.EntrySize, _layout.EntrySize);
            var angle = HalfAngle(entry, (int)_layout.Camber);
            if (angle is null || Math.Abs(angle.Value) > _layout.CamberAbsMax)
            {
                reason = $"camber sample {i} out of range";
                return null;
            }
            camber[i] = angle.Value;
            var x = Float(entry, (int)_layout.TrackX);
            if (x is null || !Plausible(x.Value))
            {
                reason = $"track sample {i} out of range";
                return null;
            }
            track[i] = x.Value;
            if (copy is not null)
            {
                var x2 = Float(entry, (int)_layout.TrackXCopy!.Value);
                if (x2 is null || !Plausible(x2.Value))
                {
                    reason = $"track copy sample {i} out of range";
                    return null;
                }
                copy[i] = x2.Value;
            }
        }
        reason = null;
        return new WheelCurve(table.Value, start, camber, track, copy);
    }

    /// <summary>The camber the physics uses on this wheel now, in degrees.</summary>
    public double? LiveCamber(long car, int wheel)
    {
        if (_wheels.LiveCamber is not { } field) return null;
        Span<byte> pair = stackalloc byte[8];
        if (!_memory.Read(_wheels.Address(car, wheel, field), pair)) return null;
        var angle = HalfAngle(pair, 0);
        return angle is { } a && Math.Abs(a) <= _layout.CamberAbsMax ? a : null;
    }

    /// <summary>Writes a whole curve back: camber, lateral position and its copy, entry by entry. Other fields are left alone.</summary>
    public bool Write(WheelCurve curve)
    {
        var ok = true;
        Span<byte> pair = stackalloc byte[8];
        for (var i = 0; i < curve.Camber.Length; i++)
        {
            if (!double.IsFinite(curve.Camber[i]) || Math.Abs(curve.Camber[i]) > _layout.CamberAbsMax) return false;
            if (!Plausible(curve.TrackX[i]) || (curve.TrackXCopy is not null && !Plausible(curve.TrackXCopy[i]))) return false;
        }
        for (var i = 0; i < curve.Camber.Length; i++)
        {
            var entry = curve.Start + (long)i * _layout.EntrySize;
            var half = curve.Camber[i] * Math.PI / 360.0;
            BinaryPrimitives.WriteSingleLittleEndian(pair, (float)Math.Sin(half));
            BinaryPrimitives.WriteSingleLittleEndian(pair[4..], (float)Math.Cos(half));
            ok &= _memory.Write(entry + _layout.Camber, pair);
            ok &= _memory.WriteSingle(entry + _layout.TrackX, (float)curve.TrackX[i]);
            if (curve.TrackXCopy is not null && _layout.TrackXCopy is { } copy)
                ok &= _memory.WriteSingle(entry + copy.Value, (float)curve.TrackXCopy[i]);
        }
        return ok;
    }

    /// <summary>
    /// True when the two wheels of an axle move on their own (independent suspension): their
    /// curves sit on opposite sides of the centre line. A solid axle reads both on one side.
    /// </summary>
    public static bool Independent(WheelCurve left, WheelCurve right)
    {
        if (left.TrackX.Length != right.TrackX.Length) return false;
        var opposite = left.TrackX.Zip(right.TrackX).Count(p => p.First * p.Second < 0);
        return opposite >= left.TrackX.Length * 0.9;
    }

    /// <summary>Read-only lines for the Probe report.</summary>
    public void Describe(long car, StringBuilder sb)
    {
        var curves = new WheelCurve?[4];
        for (var w = 0; w < 4; w++)
        {
            var name = _wheels.Order[w];
            var curve = curves[w] = Read(car, w, out var reason);
            var live = LiveCamber(car, w);
            var liveText = live is null ? "unreadable" : $"{live:+0.00;-0.00}°";
            if (curve is null)
            {
                sb.AppendLine($"  {name}: kinematics {reason} · live camber {liveText}");
                continue;
            }
            sb.AppendLine($"  {name}: table 0x{curve.Table:X} +0x{curve.Start - curve.Table:X} · live camber {liveText} · "
                + $"camber {curve.Camber.Min():+0.00;-0.00}..{curve.Camber.Max():+0.00;-0.00}° · "
                + $"x {curve.TrackX.Min():+0.000;-0.000}..{curve.TrackX.Max():+0.000;-0.000} m"
                + (curve.TrackXCopy is { } c ? $" (copy {c[0]:+0.000;-0.000})" : ""));
        }
        foreach (var (title, left, right) in new[] { ("Front", "FL", "FR"), ("Rear", "RL", "RR") })
        {
            var l = curves[_wheels.IndexOf(left)];
            var r = curves[_wheels.IndexOf(right)];
            var verdict = l is null || r is null ? "unreadable" : Independent(l, r) ? "independent, camber and track can move" : "solid axle, left alone";
            sb.AppendLine($"  {title} axle: {verdict}");
        }
    }

    private bool Plausible(double x) => double.IsFinite(x) && Math.Abs(x) >= _layout.TrackAbsMin && Math.Abs(x) <= _layout.TrackAbsMax;

    private static float? Float(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length) return null;
        var value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        return float.IsFinite(value) ? value : null;
    }

    /// <summary>An angle stored as the sine and cosine of its half, in degrees; null unless the pair is a unit vector.</summary>
    private static double? HalfAngle(ReadOnlySpan<byte> bytes, int offset)
    {
        var sin = Float(bytes, offset);
        var cos = Float(bytes, offset + 4);
        if (sin is null || cos is null) return null;
        var norm = sin.Value * (double)sin.Value + cos.Value * (double)cos.Value;
        if (Math.Abs(norm - 1) > 0.01 || cos.Value <= 0) return null;
        return 2 * Math.Atan2(sin.Value, cos.Value) * 180 / Math.PI;
    }
}
