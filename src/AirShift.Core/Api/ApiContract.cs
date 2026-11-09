using System.Text.Json;
using System.Text.Json.Nodes;
using AirShift.Core.Engine;
using AirShift.Core.Ride;

namespace AirShift.Core.Api;

/// <summary>The public AirShift local API, version airshift/1. See docs/API.md.</summary>
public static class ApiContract
{
    public const string Version = "airshift/1";
    public const string Prefix = "/airshift/1";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string LevelName(AirLevel level) => level switch
    {
        AirLevel.Low => "low",
        AirLevel.Parked => "parked",
        _ => "ride",
    };

    public static AirLevel? ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "ride" or "up" => AirLevel.Ride,
        "low" or "down" => AirLevel.Low,
        "parked" or "park" => AirLevel.Parked,
        _ => null,
    };

    public static string GameStatusName(GameStatus status) => status switch
    {
        GameStatus.Attached => "attached",
        GameStatus.CannotOpen => "cannot-open",
        _ => "not-running",
    };

    public static JsonObject Hello(string appVersion) => new()
    {
        ["api"] = Version,
        ["app"] = new JsonObject { ["name"] = "AirShift", ["version"] = appVersion, ["developer"] = "6RiDzer" },
    };

    public static JsonObject State(EngineState s, string appVersion) => new()
    {
        ["api"] = Version,
        ["app"] = new JsonObject { ["name"] = "AirShift", ["version"] = appVersion, ["developer"] = "6RiDzer" },
        ["revision"] = s.Revision,
        ["game"] = new JsonObject
        {
            ["status"] = GameStatusName(s.Game),
            ["title"] = s.GameTitle,
            ["fileVersion"] = s.FileVersion,
            ["buildKnown"] = s.BuildKnown,
            ["writesAllowed"] = s.WritesAllowed,
            ["blockedReason"] = s.WritesBlockedReason,
        },
        ["car"] = new JsonObject
        {
            ["ready"] = s.CarReady,
            ["key"] = s.CarKey,
            ["id"] = s.CarId,
            ["speedKmh"] = s.SpeedKmh,
        },
        ["air"] = new JsonObject
        {
            ["level"] = LevelName(s.Level),
            ["moving"] = s.Moving,
            ["progress"] = s.Progress,
            ["frontHeight"] = s.FrontHeightPercent,
            ["rearHeight"] = s.RearHeightPercent,
        },
        ["stance"] = new JsonObject
        {
            ["front"] = s.FrontStance,
            ["rear"] = s.RearStance,
            ["min"] = RideSettings.StanceMin,
            ["max"] = s.FrontStanceTop,
            ["frontMax"] = s.FrontStanceTop,
            ["rearMax"] = s.RearStanceTop,
        },
        ["wheels"] = new JsonObject
        {
            ["front"] = Axle(s.Wheels.Front, s.CamberFront, s.Wheels.StockCamberFront, s.Wheels.LiveCamberFront, s.TrackFront),
            ["rear"] = Axle(s.Wheels.Rear, s.CamberRear, s.Wheels.StockCamberRear, s.Wheels.LiveCamberRear, s.TrackRear),
            ["camberMin"] = RideSettings.CamberMin,
            ["camberMax"] = RideSettings.CamberMax,
            ["trackMin"] = RideSettings.TrackMin,
            ["trackMax"] = RideSettings.TrackMax,
            ["reason"] = s.Wheels.Reason,
        },
        ["tune"] = new JsonObject
        {
            ["lowFront"] = s.Tune.LowFront,
            ["lowRear"] = s.Tune.LowRear,
            ["parked"] = s.Tune.Parked,
            ["lowest"] = s.Tune.Lowest,
            ["highest"] = s.Tune.Highest,
            ["dumpSeconds"] = Math.Round(s.Tune.DumpSeconds, 1),
            ["fillSeconds"] = Math.Round(s.Tune.FillSeconds, 1),
            ["order"] = OrderName(s.Tune.Order),
            ["settle"] = s.Tune.Settle,
            ["speedGuard"] = s.Tune.SpeedGuard,
            ["ranges"] = new JsonObject
            {
                ["drop"] = Range(0, RideSettings.DropMax, 1),
                ["lowest"] = Range(RideSettings.LowestMin, RideSettings.LowestMax, 1),
                ["highest"] = Range(0, RideSettings.FrontStanceMax, 1),
                ["dumpSeconds"] = Range(RideSettings.DumpMin, RideSettings.DumpMax, 0.1),
                ["fillSeconds"] = Range(RideSettings.FillMin, RideSettings.FillMax, 0.1),
            },
        },
        ["guard"] = new JsonObject
        {
            ["on"] = s.GuardOn,
            ["downBelowKmh"] = s.GuardDownKmh,
            ["upAboveKmh"] = s.GuardUpKmh,
        },
        ["message"] = s.Message,
    };

    private static JsonObject Range(double min, double max, double step) => new() { ["min"] = min, ["max"] = max, ["step"] = step };

    public static string OrderName(AxleOrder order) => order switch
    {
        AxleOrder.FrontFirst => "front-first",
        AxleOrder.RearFirst => "rear-first",
        _ => "together",
    };

    public static AxleOrder? ParseOrder(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "together" => AxleOrder.Together,
        "front-first" or "frontfirst" => AxleOrder.FrontFirst,
        "rear-first" or "rearfirst" => AxleOrder.RearFirst,
        _ => null,
    };

    private static JsonObject Axle(bool available, double? camber, double? stock, double? live, double track) => new()
    {
        ["available"] = available,
        ["camber"] = camber is { } c ? Math.Round(c, 1) : null,
        ["stockCamber"] = stock,
        ["liveCamber"] = live,
        ["track"] = Math.Round(track, 0),
    };

    /// <summary>Runs one command from the API on the engine.</summary>
    public static async Task<CommandResult> Run(AirShiftEngine engine, JsonObject body, Action? show, Action? quit = null)
    {
        var command = body["command"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        switch (command)
        {
            case "air.toggle":
                return await engine.ToggleAsync("api");
            case "air.up":
                return await engine.SetLevelAsync(AirLevel.Ride, "api");
            case "air.down":
                return await engine.SetLevelAsync(AirLevel.Low, "api");
            case "preset.set":
                var level = ParseLevel(body["preset"]?.GetValue<string>());
                return level is null
                    ? CommandResult.Refused("preset must be ride, low or parked.")
                    : await engine.SetLevelAsync(level.Value, "api");
            case "stance.set":
                return await engine.SetStanceAsync(Number(body["front"]), Number(body["rear"]), "api");
            case "wheels.set":
                var change = new WheelChange(
                    CamberFront: Number(body["camberFront"]),
                    CamberRear: Number(body["camberRear"]),
                    TrackFront: Number(body["trackFront"]),
                    TrackRear: Number(body["trackRear"]),
                    StockCamberFront: IsStock(body["camberFront"]),
                    StockCamberRear: IsStock(body["camberRear"]));
                return change == default
                    ? CommandResult.Refused("wheels.set needs camberFront, camberRear, trackFront or trackRear.")
                    : await engine.SetWheelsAsync(change, "api");
            case "car.reset":
                return await engine.ResetCarAsync("api");
            case "tune.set":
                var orderText = Text(body["order"]);
                var order = ParseOrder(orderText);
                if (orderText is not null && order is null) return CommandResult.Refused("order must be together, front-first or rear-first.");
                var tune = new TuneChange(
                    LowFront: Number(body["lowFront"]),
                    LowRear: Number(body["lowRear"]),
                    Parked: Number(body["parked"]),
                    Lowest: Number(body["lowest"]),
                    Highest: Number(body["highest"]),
                    DumpSeconds: Number(body["dumpSeconds"]),
                    FillSeconds: Number(body["fillSeconds"]),
                    Order: order,
                    Settle: Flag(body["settle"]),
                    SpeedGuard: Flag(body["speedGuard"]));
                return tune.Empty
                    ? CommandResult.Refused("tune.set needs at least one setting.")
                    : await engine.ChangeTuneAsync(tune, "api");
            case "restore":
                return await engine.RestoreAsync();
            case "app.show":
                show?.Invoke();
                return CommandResult.Done("Shown.");
            case "app.quit":
                // As if the user closed the window: the car goes back to stock first. Another app uses it to
                // put a new AirShift.exe in place and start it again.
                if (quit is null) return CommandResult.Refused("Unknown command.");
                quit();
                return CommandResult.Done("Closing.");
            default:
                return CommandResult.Refused("Unknown command.");
        }
    }

    private static string? Text(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? Flag(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsStock(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text)
                   && string.Equals(text.Trim(), "stock", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double? Number(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            var value = node.GetValue<double>();
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
