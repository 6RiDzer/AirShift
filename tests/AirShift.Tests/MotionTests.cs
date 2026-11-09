using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

/// <summary>The air ride motion: smooth, no jolt, a small settle after a drop, none on the floor.</summary>
public class MotionTests
{
    private const double Frame = 1.0 / 60;

    private static (double[] x, double[] v, double[] a) Trace(Func<double, double> at, double seconds)
    {
        var n = (int)(seconds / Frame) + 2;
        var x = Enumerable.Range(0, n).Select(i => at(i * Frame)).ToArray();
        var v = x.Zip(x.Skip(1), (p, q) => (q - p) / Frame).ToArray();
        var a = v.Zip(v.Skip(1), (p, q) => (q - p) / Frame).ToArray();
        return (x, v, a);
    }

    [Fact]
    public void A_drop_starts_and_ends_without_a_jolt_and_settles_a_little()
    {
        // A test car's front: 13 cm of clearance, dump 1.8 s, Low 118 % with stance +6 %.
        var move = new WheelMove(0.4504 + 0.0077, 0.4504 - 0.1287 * 1.12, 0, 1.24 * 1.8, settle: true, floor: 0.30);
        var (x, v, a) = Trace(move.At, move.TotalSeconds + 0.2);
        Assert.True(Math.Abs(v[0]) < 0.02, $"starts at {v[0]:0.000} m/s");
        // The old curve jumped to its full speed in one frame and kicked again when the settle began.
        Assert.True(a.Max(Math.Abs) < 2.0, $"largest acceleration {a.Max(Math.Abs):0.00} m/s²");
        var lowest = x.Min();
        Assert.InRange(move.To - lowest, 0.001, 0.008);
        Assert.Equal(move.To, x[^1], 4);
    }

    [Fact]
    public void A_car_set_down_on_its_lowest_point_does_not_bounce()
    {
        const double floor = 0.30;
        var move = new WheelMove(0.45, floor, 0, 2.0, settle: true, floor: floor);
        Assert.False(move.Settle);
        var (x, v, _) = Trace(move.At, move.TotalSeconds + 0.2);
        Assert.All(x, h => Assert.True(h >= floor - 1e-9));
        Assert.All(v, s => Assert.True(s <= 1e-6, $"moved up at {s:0.0000} m/s"));
    }

    [Fact]
    public void Filling_eases_in_and_out()
    {
        var move = new WheelMove(0.33, 0.45, 0, 2.6, settle: true, floor: 0.30);
        var (x, v, a) = Trace(move.At, move.TotalSeconds + 0.2);
        Assert.True(v[0] < 0.01);
        Assert.True(a.Max(Math.Abs) < 1.5);
        Assert.True(x.Max() - 0.45 < 0.002, "hardly overshoots when it stops");
        Assert.Equal(0.45, x[^1], 4);
    }

    [Fact]
    public void A_new_move_keeps_the_speed_of_the_one_it_replaces()
    {
        var settings = new RideSettings();
        double[] stock = [0.45, 0.45, 0.45, 0.45], radii = [0.32, 0.32, 0.32, 0.32];
        var down = RideMotion.Plan(stock, [0.36, 0.36, 0.36, 0.36], stock, radii, w => w < 2, settings);
        var at = 0.5;
        var from = down.At(at);
        var speed = down.VelocityAt(at);
        Assert.True(speed[0] < -0.03);
        var up = RideMotion.Plan(from, stock, stock, radii, w => w < 2, settings, speed);
        Assert.Equal(from[0], up.At(0)[0], 6);
        // Still going down for a moment, then up: no instant reversal.
        Assert.True(up.At(Frame)[0] < from[0]);
        Assert.Equal(0.45, up.At(up.TotalSeconds)[0], 4);
    }

    [Fact]
    public void A_drag_follows_quickly_without_overshoot()
    {
        var move = new WheelMove(0.45, 0.43, 0, 0.15, settle: true, floor: 0.30, quick: true);
        var (x, _, _) = Trace(move.At, move.TotalSeconds + 0.1);
        Assert.All(x, h => Assert.True(h >= 0.43 - 1e-6));
        Assert.True(move.TotalSeconds < 0.6, move.TotalSeconds.ToString());
    }
}
