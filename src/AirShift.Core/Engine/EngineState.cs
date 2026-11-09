using AirShift.Core.Ride;

namespace AirShift.Core.Engine;

public enum GameStatus
{
    NotRunning,
    CannotOpen,
    Attached,
}

/// <summary>An immutable picture of the engine, published after every change.</summary>
public sealed record EngineState
{
    public GameStatus Game { get; init; } = GameStatus.NotRunning;
    public string GameTitle { get; init; } = "Forza Horizon 6";
    public string? FileVersion { get; init; }
    public bool BuildKnown { get; init; }
    public bool WritesAllowed { get; init; }
    public string? WritesBlockedReason { get; init; }
    public bool CarReady { get; init; }
    public string? CarKey { get; init; }
    public int? CarId { get; init; }
    public double SpeedKmh { get; init; }
    public AirLevel Level { get; init; } = AirLevel.Ride;
    public bool Moving { get; init; }
    public double Progress { get; init; } = 1;
    public double FrontStance { get; init; }
    public double RearStance { get; init; }

    /// <summary>The highest stance per axle now: Maxi, under the axle's own top.</summary>
    public double FrontStanceTop { get; init; } = RideSettings.FrontStanceMax;

    public double RearStanceTop { get; init; } = RideSettings.RearStanceMax;

    /// <summary>Mini or Maxi showing on the car for a moment after it changed; None otherwise.</summary>
    public HeightPreview Preview { get; init; }

    /// <summary>How far each axle of the current car can move; null without a car.</summary>
    public AxleRoom? Axles { get; init; }

    /// <summary>Camber per axle the user set (left wheel's sign); null is the car's own.</summary>
    public double? CamberFront { get; init; }

    public double? CamberRear { get; init; }

    /// <summary>Track width change per axle, in millimetres.</summary>
    public double TrackFront { get; init; }

    public double TrackRear { get; init; }
    public WheelState Wheels { get; init; } = new() { Reason = "No car yet." };

    /// <summary>The Tune settings, so the API and the window show the same values.</summary>
    public TuneState Tune { get; init; } = TuneState.From(new RideSettings());

    /// <summary>Live height per axle as a share of stock clearance: 0 = stock, -50 = half the clearance gone.</summary>
    public double FrontHeightPercent { get; init; }

    public double RearHeightPercent { get; init; }
    public bool GuardOn { get; init; } = true;
    public double GuardDownKmh { get; init; } = 30;
    public double GuardUpKmh { get; init; } = 60;
    public string Message { get; init; } = "Waiting for Forza Horizon 6.";
    public int Reapplied { get; init; }
    public long Revision { get; init; }
}

/// <summary>A height the car shows for a moment after Mini or Maxi changed.</summary>
public enum HeightPreview
{
    None,
    Lowest,
    Highest,
}

/// <summary>
/// Room per axle in millimetres: stock clearance (stock hub height over the wheel radius, what
/// the percentages are shares of), and the deepest a hub may go under stock at all.
/// </summary>
public sealed record AxleRoom(double FrontClearanceMm, double RearClearanceMm, double FrontDeepestMm, double RearDeepestMm)
{
    /// <summary>Millimetres under stock at this Mini.</summary>
    public double LowestMm(bool front, double lowestPercent) =>
        Math.Min((front ? FrontClearanceMm : RearClearanceMm) * lowestPercent / 100.0, front ? FrontDeepestMm : RearDeepestMm);

    /// <summary>Millimetres over stock at this stance.</summary>
    public double RaiseMm(bool front, double stancePercent) => (front ? FrontClearanceMm : RearClearanceMm) * stancePercent / 100.0;
}

/// <summary>The user's Tune settings as they are now.</summary>
public sealed record TuneState(
    double LowFront,
    double LowRear,
    double Parked,
    double Lowest,
    double Highest,
    double DumpSeconds,
    double FillSeconds,
    AxleOrder Order,
    bool Settle,
    bool SpeedGuard)
{
    public static TuneState From(RideSettings s) => new(
        s.LowFrontPercent, s.LowRearPercent, s.ParkedDropPercent, s.LowestPercent, s.HighestPercent,
        s.ExhaustSeconds, s.FillSeconds, s.Order, s.Settle, s.SpeedGuard);
}

/// <summary>A Tune change from the API; null fields stay as they are.</summary>
public sealed record TuneChange(
    double? LowFront = null,
    double? LowRear = null,
    double? Parked = null,
    double? Lowest = null,
    double? Highest = null,
    double? DumpSeconds = null,
    double? FillSeconds = null,
    AxleOrder? Order = null,
    bool? Settle = null,
    bool? SpeedGuard = null)
{
    public bool Empty => this == new TuneChange();

    public void ApplyTo(RideSettings s)
    {
        if (LowFront is { } lf) s.LowFrontPercent = lf;
        if (LowRear is { } lr) s.LowRearPercent = lr;
        if (Parked is { } p) s.ParkedDropPercent = p;
        if (Lowest is { } lo) s.LowestPercent = lo;
        if (Highest is { } hi) s.HighestPercent = hi;
        if (DumpSeconds is { } d) s.ExhaustSeconds = d;
        if (FillSeconds is { } f) s.FillSeconds = f;
        if (Order is { } o) s.Order = o;
        if (Settle is { } st) s.Settle = st;
        if (SpeedGuard is { } g) s.SpeedGuard = g;
    }
}

/// <summary>A camber and track change from the app or the API; null fields stay as they are.</summary>
public readonly record struct WheelChange(
    double? CamberFront = null,
    double? CamberRear = null,
    double? TrackFront = null,
    double? TrackRear = null,
    bool StockCamberFront = false,
    bool StockCamberRear = false);

public sealed record CommandResult(bool Ok, string Message)
{
    public static CommandResult Done(string message) => new(true, message);
    public static CommandResult Refused(string message) => new(false, message);
}
