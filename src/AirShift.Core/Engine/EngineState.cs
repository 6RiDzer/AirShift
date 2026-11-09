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

    /// <summary>Camber per axle the user set (left wheel's sign); null is the car's own.</summary>
    public double? CamberFront { get; init; }

    public double? CamberRear { get; init; }

    /// <summary>Track width change per axle, in millimetres.</summary>
    public double TrackFront { get; init; }

    public double TrackRear { get; init; }
    public WheelState Wheels { get; init; } = new() { Reason = "No car yet." };

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
