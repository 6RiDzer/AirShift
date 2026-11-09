using System.Text.Json.Serialization;

namespace AirShift.Core.Ride;

public enum AirLevel
{
    Ride,
    Low,
    Parked,
}

public enum AxleOrder
{
    Together,
    FrontFirst,
    RearFirst,
}

/// <summary>Everything the user can tune. Values are clamped on load and on every change.</summary>
public sealed class RideSettings
{
    public const double StanceMin = -50;

    /// <summary>Highest stance per axle: above it the car misbehaves in game (first test, 2026-11-09).</summary>
    public const double FrontStanceMax = 15;

    public const double RearStanceMax = 10;

    /// <summary>Camber per axle, in degrees, as the game's tuning menu shows it (negative: tops in).</summary>
    public const double CamberMin = -15;

    public const double CamberMax = 5;

    /// <summary>Track width change per axle, in millimetres (positive: wheels further out).</summary>
    public const double TrackMin = -100;

    public const double TrackMax = 100;

    public static double StanceMax(bool front) => front ? FrontStanceMax : RearStanceMax;

    /// <summary>Highest air out a preset can ask for, as a share of stock clearance (above 100 % the hub goes below the wheel radius).</summary>
    public const double DropMax = 130;

    /// <summary>The lowest the car can go, whatever the preset and stance, as a share of stock clearance aired out.</summary>
    public const double LowestMin = 40;

    public const double LowestMax = 130;

    /// <summary>Whatever the settings, a hub never goes below this share of its wheel radius (the game's car reads wrong under half).</summary>
    public const double HubOverRadiusMin = 0.65;

    /// <summary>Share of the front wheels' stock clearance removed at the Low preset.</summary>
    public double LowFrontPercent { get; set; } = 45;

    /// <summary>Share of the rear wheels' stock clearance removed at the Low preset.</summary>
    public double LowRearPercent { get; set; } = 45;

    /// <summary>Settings from AirShift 0.2.0 and earlier had one Low air out for both axles.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LowDropPercent
    {
        get => null;
        set
        {
            if (value is not { } v) return;
            LowFrontPercent = v;
            LowRearPercent = v;
        }
    }

    /// <summary>Share of each wheel's stock clearance removed at the Parked preset.</summary>
    public double ParkedDropPercent { get; set; } = 80;

    /// <summary>Mini: the lowest any preset and stance can take the car, as a share of stock clearance aired out.</summary>
    public double LowestPercent { get; set; } = 100;

    /// <summary>Maxi: the highest stance, in percent of stock clearance, under each axle's own top.</summary>
    public double HighestPercent { get; set; } = FrontStanceMax;

    /// <summary>Static ride height per axle, as a share of stock clearance (-50 to +15 front, +10 rear).</summary>
    public double FrontStancePercent { get; set; }

    public double RearStancePercent { get; set; }

    /// <summary>Seconds to dump the air across the full stock clearance.</summary>
    public double ExhaustSeconds { get; set; } = 1.4;

    /// <summary>Seconds to fill across the full stock clearance (a compressor is slower than a dump valve).</summary>
    public double FillSeconds { get; set; } = 2.6;

    public AxleOrder Order { get; set; } = AxleOrder.Together;

    /// <summary>Delay between the first and the second axle when the order is not Together.</summary>
    public double AxleDelaySeconds { get; set; } = 0.35;

    /// <summary>A short settle at the end of a drop, like a real bag coming to rest.</summary>
    public bool Settle { get; set; } = true;

    public bool SpeedGuard { get; set; } = true;

    /// <summary>Air down is refused above this speed.</summary>
    public double GuardDownKmh { get; set; } = 30;

    /// <summary>A lowered car rises back to Ride above this speed.</summary>
    public double GuardUpKmh { get; set; } = 60;

    public bool ControllerEnabled { get; set; }

    /// <summary>Game file versions the user agreed to try even though the profile does not list them.</summary>
    public List<string> AllowedUnknownBuilds { get; set; } = [];

    /// <summary>Per-car stance, keyed by <see cref="CarKey"/>.</summary>
    public Dictionary<string, CarMemory> Cars { get; set; } = new();

    public void Clamp()
    {
        LowFrontPercent = Math.Clamp(Finite(LowFrontPercent, 45), 0, DropMax);
        LowRearPercent = Math.Clamp(Finite(LowRearPercent, 45), 0, DropMax);
        ParkedDropPercent = Math.Clamp(Finite(ParkedDropPercent, 80), 0, DropMax);
        LowestPercent = Math.Clamp(Finite(LowestPercent, 100), LowestMin, LowestMax);
        HighestPercent = Math.Clamp(Finite(HighestPercent, FrontStanceMax), 0, FrontStanceMax);
        FrontStancePercent = Math.Clamp(Finite(FrontStancePercent, 0), StanceMin, FrontStanceMax);
        RearStancePercent = Math.Clamp(Finite(RearStancePercent, 0), StanceMin, RearStanceMax);
        ExhaustSeconds = Math.Clamp(Finite(ExhaustSeconds, 1.4), 0.3, 8);
        FillSeconds = Math.Clamp(Finite(FillSeconds, 2.6), 0.5, 12);
        AxleDelaySeconds = Math.Clamp(Finite(AxleDelaySeconds, 0.35), 0, 2);
        GuardDownKmh = Math.Clamp(Finite(GuardDownKmh, 30), 5, 80);
        GuardUpKmh = Math.Clamp(Finite(GuardUpKmh, 60), GuardDownKmh + 5, 200);
        foreach (var car in Cars.Values) car.Clamp();
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    public double DropPercent(AirLevel level, bool front) => level switch
    {
        AirLevel.Low => front ? LowFrontPercent : LowRearPercent,
        AirLevel.Parked => ParkedDropPercent,
        _ => 0,
    };

    /// <summary>The highest stance an axle can take now: Maxi, under the axle's own top.</summary>
    public double StanceTop(bool front) => Math.Min(HighestPercent, StanceMax(front));
}

public sealed class CarMemory
{
    public double Front { get; set; }
    public double Rear { get; set; }

    /// <summary>Stock hub heights, kept so a crash while lowered can be recovered.</summary>
    public double[]? Stock { get; set; }

    /// <summary>The last heights AirShift wrote for this car.</summary>
    public double[]? LastWritten { get; set; }

    /// <summary>Camber per axle in degrees (left wheel's sign, the right wheel mirrors it); null keeps the car's own.</summary>
    public double? CamberFront { get; set; }

    public double? CamberRear { get; set; }

    /// <summary>Track width change per axle, in millimetres.</summary>
    public double TrackFront { get; set; }

    public double TrackRear { get; set; }

    /// <summary>
    /// While AirShift holds this car's wheels: each wheel's own camber before AirShift turned it
    /// and how far it turned and moved each one, so a restart after a crash finds stock again.
    /// </summary>
    public double[]? WheelStockCamber { get; set; }

    public double[]? WheelStockTrack { get; set; }

    public double[]? WheelCamberShift { get; set; }

    public double[]? WheelTrackShift { get; set; }

    [JsonIgnore]
    public bool HasStance => Math.Abs(Front) > 0.01 || Math.Abs(Rear) > 0.01;

    [JsonIgnore]
    public bool HasWheels => CamberFront is not null || CamberRear is not null
                             || Math.Abs(TrackFront) > 0.01 || Math.Abs(TrackRear) > 0.01;

    public void Clamp()
    {
        Front = Math.Clamp(double.IsFinite(Front) ? Front : 0, RideSettings.StanceMin, RideSettings.FrontStanceMax);
        Rear = Math.Clamp(double.IsFinite(Rear) ? Rear : 0, RideSettings.StanceMin, RideSettings.RearStanceMax);
        CamberFront = Camber(CamberFront);
        CamberRear = Camber(CamberRear);
        TrackFront = Math.Clamp(double.IsFinite(TrackFront) ? TrackFront : 0, RideSettings.TrackMin, RideSettings.TrackMax);
        TrackRear = Math.Clamp(double.IsFinite(TrackRear) ? TrackRear : 0, RideSettings.TrackMin, RideSettings.TrackMax);
        if (WheelStockCamber is { Length: not 4 } || WheelStockTrack is { Length: not 4 }
            || WheelCamberShift is { Length: not 4 } || WheelTrackShift is { Length: not 4 })
        {
            WheelStockCamber = WheelStockTrack = WheelCamberShift = WheelTrackShift = null;
        }
    }

    private static double? Camber(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, RideSettings.CamberMin, RideSettings.CamberMax) : null;

    /// <summary>Back to the car's own height, camber and track width.</summary>
    public void Reset()
    {
        Front = Rear = 0;
        CamberFront = CamberRear = null;
        TrackFront = TrackRear = 0;
    }
}

public static class CarKey
{
    /// <summary>The game's car id when the profile can read it, else the wheel radii (stable for a car and tyre fitment).</summary>
    public static string For(int? carId, IReadOnlyList<float> radii)
    {
        if (carId is { } id) return "car:" + id;
        return "wheels:" + string.Join('-', radii.Select(r => ((int)Math.Round(r * 1000)).ToString()));
    }
}
