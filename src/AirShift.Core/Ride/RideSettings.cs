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
    public const double StanceMax = 20;

    /// <summary>Share of each wheel's stock clearance removed at the Low preset.</summary>
    public double LowDropPercent { get; set; } = 45;

    /// <summary>Share of each wheel's stock clearance removed at the Parked preset.</summary>
    public double ParkedDropPercent { get; set; } = 80;

    /// <summary>Share of stock clearance that is always kept, whatever the preset and stance.</summary>
    public double FloorPercent { get; set; } = 12;

    /// <summary>Static ride height per axle, as a share of stock clearance (-50 to +20).</summary>
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
        LowDropPercent = Math.Clamp(Finite(LowDropPercent, 45), 0, 100);
        ParkedDropPercent = Math.Clamp(Finite(ParkedDropPercent, 80), 0, 100);
        FloorPercent = Math.Clamp(Finite(FloorPercent, 12), 5, 60);
        FrontStancePercent = Math.Clamp(Finite(FrontStancePercent, 0), StanceMin, StanceMax);
        RearStancePercent = Math.Clamp(Finite(RearStancePercent, 0), StanceMin, StanceMax);
        ExhaustSeconds = Math.Clamp(Finite(ExhaustSeconds, 1.4), 0.3, 8);
        FillSeconds = Math.Clamp(Finite(FillSeconds, 2.6), 0.5, 12);
        AxleDelaySeconds = Math.Clamp(Finite(AxleDelaySeconds, 0.35), 0, 2);
        GuardDownKmh = Math.Clamp(Finite(GuardDownKmh, 30), 5, 80);
        GuardUpKmh = Math.Clamp(Finite(GuardUpKmh, 60), GuardDownKmh + 5, 200);
        foreach (var car in Cars.Values) car.Clamp();
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    public double DropPercent(AirLevel level) => level switch
    {
        AirLevel.Low => LowDropPercent,
        AirLevel.Parked => ParkedDropPercent,
        _ => 0,
    };
}

public sealed class CarMemory
{
    public double Front { get; set; }
    public double Rear { get; set; }

    /// <summary>Stock hub heights, kept so a crash while lowered can be recovered.</summary>
    public double[]? Stock { get; set; }

    /// <summary>The last heights AirShift wrote for this car.</summary>
    public double[]? LastWritten { get; set; }

    [JsonIgnore]
    public bool HasStance => Math.Abs(Front) > 0.01 || Math.Abs(Rear) > 0.01;

    public void Clamp()
    {
        Front = Math.Clamp(double.IsFinite(Front) ? Front : 0, RideSettings.StanceMin, RideSettings.StanceMax);
        Rear = Math.Clamp(double.IsFinite(Rear) ? Rear : 0, RideSettings.StanceMin, RideSettings.StanceMax);
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
