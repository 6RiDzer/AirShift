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
            ["max"] = RideSettings.StanceMax,
        },
        ["guard"] = new JsonObject
        {
            ["on"] = s.GuardOn,
            ["downBelowKmh"] = s.GuardDownKmh,
            ["upAboveKmh"] = s.GuardUpKmh,
        },
        ["message"] = s.Message,
    };

    /// <summary>Runs one command from the API on the engine.</summary>
    public static async Task<CommandResult> Run(AirShiftEngine engine, JsonObject body, Action? show)
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
            case "restore":
                return await engine.RestoreAsync();
            case "app.show":
                show?.Invoke();
                return CommandResult.Done("Shown.");
            default:
                return CommandResult.Refused("Unknown command.");
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
