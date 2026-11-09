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
        double rearStance,
        double hardFloor)
    {
        var drop = settings.DropPercent(level);
        var result = new double[4];
        for (var w = 0; w < 4; w++)
        {
            var clearance = Clearance(stock[w], radii[w]);
            var stance = isFront(w) ? frontStance : rearStance;
            var value = stock[w] + clearance * stance / 100.0 - clearance * drop / 100.0;
            result[w] = Math.Max(Floor(stock[w], radii[w], settings.FloorPercent, hardFloor), value);
        }
        return result;
    }

    public static double Floor(double stock, double radius, double floorPercent, double hardFloor) =>
        radius + Math.Max(Clearance(stock, radius) * floorPercent / 100.0, hardFloor);

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

/// <summary>One wheel moving from one height to another.</summary>
public sealed class WheelMove
{
    public WheelMove(double from, double to, double delay, double duration, bool settle, double floor)
    {
        From = from;
        To = to;
        Delay = delay;
        Duration = Math.Max(0.12, duration);
        Settle = settle && to < from - 0.004;
        Floor = floor;
    }

    public double From { get; }
    public double To { get; }
    public double Delay { get; }
    public double Duration { get; }
    public bool Settle { get; }
    public double Floor { get; }

    public const double SettleSeconds = 0.45;

    public double TotalSeconds => Delay + Duration + (Settle ? SettleSeconds : 0);

    public bool Down => To < From;

    public double At(double elapsed)
    {
        var t = elapsed - Delay;
        if (t <= 0) return From;
        if (t < Duration)
        {
            var u = t / Duration;
            // Dump: fast start, slowing as pressure drops. Fill: gentle start and finish.
            var p = Down ? 1 - Math.Pow(1 - u, 2.2) : u * u * (3 - 2 * u);
            return From + (To - From) * p;
        }
        if (Settle)
        {
            var s = t - Duration;
            if (s < SettleSeconds)
            {
                var amplitude = Math.Min(0.006, Math.Abs(From - To) * 0.06);
                var wave = Math.Sin(s / SettleSeconds * Math.PI * 3) * Math.Exp(-s * 6);
                return Math.Max(Floor, To - amplitude * wave);
            }
        }
        return To;
    }
}

public sealed class RideMotion
{
    private readonly WheelMove[] _wheels;

    public RideMotion(WheelMove[] wheels) => _wheels = wheels;

    public double TotalSeconds => _wheels.Max(w => w.TotalSeconds);

    public double[] At(double elapsed) => _wheels.Select(w => w.At(elapsed)).ToArray();

    public bool Done(double elapsed) => elapsed >= TotalSeconds;

    public double Progress(double elapsed) => TotalSeconds <= 0 ? 1 : Math.Clamp(elapsed / TotalSeconds, 0, 1);

    /// <summary>Every wheel straight to its target in <paramref name="seconds"/>, no axle delay and no settle.</summary>
    public static RideMotion Quick(IReadOnlyList<double> from, IReadOnlyList<double> to, double seconds) =>
        new(Enumerable.Range(0, 4).Select(w => new WheelMove(from[w], to[w], 0, seconds, settle: false, floor: 0)).ToArray());

    public static RideMotion Plan(
        IReadOnlyList<double> from,
        IReadOnlyList<double> to,
        IReadOnlyList<double> stock,
        IReadOnlyList<double> radii,
        Func<int, bool> isFront,
        RideSettings settings,
        double hardFloor)
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
            var floor = AirRideModel.Floor(stock[w], radii[w], settings.FloorPercent, hardFloor);
            wheels[w] = new WheelMove(from[w], to[w], delays[w], duration, settings.Settle, floor);
        }
        return new RideMotion(wheels);
    }
}
