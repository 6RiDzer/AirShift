namespace AirShift.Core.Ride;

/// <summary>
/// Turns a preset and stance into four target hub heights, and moves the car there like an air
/// ride: the dump valve is fast at first and slows as the bag empties, the compressor fills at a
/// steadier pace, axles can move one after the other, and a drop ends with a short settle.
/// </summary>
public static class AirRideModel
{
    public static double Clearance(double stock, double radius) => Math.Max(0, stock - radius);

    public static double[] Targets(
        IReadOnlyList<double> stock,
        IReadOnlyList<double> radii,
        Func<int, bool> isFront,
        RideSettings settings,
        AirLevel level,
        double frontStance,
        double rearStance)
    {
        var result = new double[4];
        for (var w = 0; w < 4; w++)
        {
            var front = isFront(w);
            var clearance = Clearance(stock[w], radii[w]);
            var stance = Math.Min(front ? frontStance : rearStance, settings.StanceTop(front));
            var drop = settings.DropPercent(level, front);
            var value = stock[w] + clearance * stance / 100.0 - clearance * drop / 100.0;
            result[w] = Math.Max(Floor(stock[w], radii[w], settings.LowestPercent), value);
        }
        return result;
    }

    /// <summary>
    /// The lowest a hub may go: Mini (a share of stock clearance aired out, past 100 % the hub
    /// sits under the wheel radius), and never under <see cref="RideSettings.HubOverRadiusMin"/> of the radius.
    /// </summary>
    public static double Floor(double stock, double radius, double lowestPercent) =>
        Math.Max(radius * RideSettings.HubOverRadiusMin, stock - Clearance(stock, radius) * lowestPercent / 100.0);

    public static double[] Delays(Func<int, bool> isFront, RideSettings settings, bool goingDown)
    {
        var delays = new double[4];
        if (settings.Order == AxleOrder.Together) return delays;
        // Front first means the front moves first in both directions.
        var frontFirst = settings.Order == AxleOrder.FrontFirst;
        for (var w = 0; w < 4; w++)
        {
            var late = isFront(w) ? !frontFirst : frontFirst;
            delays[w] = late ? settings.AxleDelaySeconds : 0;
        }
        _ = goingDown;
        return delays;
    }
}

/// <summary>
/// One wheel moving from one height to another, like a car body on an air bag. The bag's own
/// height follows the valves (the dump is quick at first and slows as the bag empties, the
/// compressor fills at a steadier pace), and the body follows the bag as a mass on a damped
/// spring: it starts and stops smoothly, never jumps, and after a drop it sinks a few millimetres
/// past the target and comes back up. A body set down on its lowest point (Mini) does not bounce.
/// The motion is worked out once, at 240 steps a second, when the move starts.
/// </summary>
public sealed class WheelMove
{
    private const double Step = 1.0 / 240;
    private const double MaxTailSeconds = 1.5;

    /// <summary>Natural frequency of a car body on its bags (rad/s, about 1.5 Hz).</summary>
    private const double BagOmega = 2 * Math.PI * 1.5;

    /// <summary>Quick moves (a slider drag) follow tightly with no overshoot.</summary>
    private const double QuickOmega = 30;

    private readonly double[] _position;
    private readonly double[] _velocity;

    public WheelMove(double from, double to, double delay, double duration, bool settle, double floor,
        double startVelocity = 0, bool quick = false)
    {
        From = from;
        To = to;
        Delay = Math.Max(0, delay);
        Duration = Math.Max(0.12, duration);
        Floor = floor;
        var down = to < from;
        var travel = Math.Abs(to - from);
        var atFloor = to <= floor + 0.0005;
        Settle = settle && down && !atFloor && !quick && travel > 0.004;

        // A settling drop: the dump valve aims a quarter past the target and closes on it, so the
        // body still moves when it closes and sinks a little past the target before coming back.
        var aim = Settle ? 0.25 : 0.0;
        var close = Settle ? 1 - Math.Sqrt(aim / (1 + aim)) : 1.0;
        var omega = quick ? QuickOmega : BagOmega;
        var zeta = quick || atFloor ? 1.0 : down ? (Settle ? 0.45 : 0.9) : 0.65;

        double Bag(double t)
        {
            var u = (t - Delay) / Duration;
            if (u <= 0) return from;
            if (u >= 1) return to;
            if (quick) return from + (to - from) * u;
            if (down)
            {
                // Valve flow falls with the pressure left in the bag: fast first, slower at the end.
                var v = u * close;
                var drop = (1 + aim) * (1 - (1 - v) * (1 - v));
                return from + (to - from) * Math.Min(1, drop);
            }
            // The compressor needs a moment to build pressure, then lifts steadily and eases in.
            return from + (to - from) * u * u * (3 - 2 * u);
        }

        var positions = new List<double>(256);
        var velocities = new List<double>(256);
        double x = from, vel = startVelocity;
        var end = Delay + Duration;
        for (var t = 0.0; ; t += Step)
        {
            positions.Add(x);
            velocities.Add(vel);
            if (t >= end && Math.Abs(x - to) < 0.0002 && Math.Abs(vel) < 0.002) break;
            if (t >= end + MaxTailSeconds) break;
            var accel = omega * omega * (Bag(t) - x) - 2 * zeta * omega * vel;
            vel += accel * Step;
            x += vel * Step;
            if (x < floor)
            {
                // The body sits down on its lowest point: no bounce below it.
                x = floor;
                if (vel < 0) vel = 0;
            }
        }
        positions[^1] = to;
        velocities[^1] = 0;
        _position = positions.ToArray();
        _velocity = velocities.ToArray();
    }

    public double From { get; }
    public double To { get; }
    public double Delay { get; }
    public double Duration { get; }

    /// <summary>True when the drop ends with a small sink past the target and back.</summary>
    public bool Settle { get; }

    public double Floor { get; }

    public double TotalSeconds => (_position.Length - 1) * Step;

    public bool Down => To < From;

    public double At(double elapsed) => Sample(_position, elapsed, To);

    /// <summary>The body's vertical speed (m/s), so a new move can start from it without a jolt.</summary>
    public double VelocityAt(double elapsed) => Sample(_velocity, elapsed, 0);

    private static double Sample(double[] values, double elapsed, double after)
    {
        if (elapsed <= 0) return values[0];
        var index = elapsed / Step;
        var i = (int)index;
        if (i >= values.Length - 1) return after;
        var f = index - i;
        return values[i] + (values[i + 1] - values[i]) * f;
    }
}

public sealed class RideMotion
{
    private readonly WheelMove[] _wheels;

    public RideMotion(WheelMove[] wheels) => _wheels = wheels;

    public double TotalSeconds => _wheels.Max(w => w.TotalSeconds);

    public double[] At(double elapsed) => _wheels.Select(w => w.At(elapsed)).ToArray();

    public double[] VelocityAt(double elapsed) => _wheels.Select(w => w.VelocityAt(elapsed)).ToArray();

    public bool Done(double elapsed) => elapsed >= TotalSeconds;

    public double Progress(double elapsed) => TotalSeconds <= 0 ? 1 : Math.Clamp(elapsed / TotalSeconds, 0, 1);

    /// <summary>Every wheel straight to its target in about <paramref name="seconds"/>, no axle delay and no settle.</summary>
    public static RideMotion Quick(IReadOnlyList<double> from, IReadOnlyList<double> to, double seconds, IReadOnlyList<double>? velocity = null) =>
        new(Enumerable.Range(0, 4).Select(w => new WheelMove(from[w], to[w], 0, seconds, settle: false, floor: 0,
            startVelocity: velocity?[w] ?? 0, quick: true)).ToArray());

    public static RideMotion Plan(
        IReadOnlyList<double> from,
        IReadOnlyList<double> to,
        IReadOnlyList<double> stock,
        IReadOnlyList<double> radii,
        Func<int, bool> isFront,
        RideSettings settings,
        IReadOnlyList<double>? velocity = null)
    {
        var goingDown = Enumerable.Range(0, 4).Sum(w => to[w] - from[w]) < 0;
        var delays = AirRideModel.Delays(isFront, settings, goingDown);
        var wheels = new WheelMove[4];
        for (var w = 0; w < 4; w++)
        {
            var clearance = Math.Max(0.02, AirRideModel.Clearance(stock[w], radii[w]));
            var share = Math.Abs(to[w] - from[w]) / clearance;
            var down = to[w] < from[w];
            var duration = share * (down ? settings.ExhaustSeconds : settings.FillSeconds);
            var floor = AirRideModel.Floor(stock[w], radii[w], settings.LowestPercent);
            wheels[w] = new WheelMove(from[w], to[w], delays[w], duration, settings.Settle, floor, velocity?[w] ?? 0);
        }
        return new RideMotion(wheels);
    }
}
