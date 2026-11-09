using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

/// <summary>Low per axle, Mini, Maxi, old settings files, and the quiet change log.</summary>
public class TuneTests
{
    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    private static (AirShiftEngine engine, FakeGame game, List<string> log) Engine(string? settingsPath = null)
    {
        var game = new FakeGame(Profile());
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(settingsPath));
        var log = new List<string>();
        engine.Log += log.Add;
        return (engine, game, log);
    }

    private static double Run(AirShiftEngine engine, double from, double seconds)
    {
        var t = from;
        for (; t < from + seconds; t += 1.0 / 60) engine.Tick(t);
        return t;
    }

    [Fact]
    public void Low_airs_out_each_axle_on_its_own_and_past_100()
    {
        var (engine, game, _) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.ChangeSettingsAsync(s => { s.LowFrontPercent = 120; s.LowRearPercent = 60; s.LowestPercent = 130; }).Wait();
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Run(engine, t, 5);
        var h = game.Heights();
        Assert.Equal(0.40 - 0.07 * 1.2, h[0], 4);
        Assert.Equal(0.42 - 0.08 * 0.6, h[2], 4);
        Assert.True(engine.State.FrontHeightPercent <= -119);
    }

    [Fact]
    public void Mini_stops_a_low_that_asks_for_more()
    {
        var (engine, game, _) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.ChangeSettingsAsync(s => { s.LowFrontPercent = 125; s.LowestPercent = 100; }).Wait();
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Run(engine, t, 5);
        Assert.Equal(0.33, game.Heights()[0], 4);
    }

    [Fact]
    public void A_hub_never_goes_under_two_thirds_of_its_wheel()
    {
        var settings = new RideSettings { LowFrontPercent = 130, LowRearPercent = 130, LowestPercent = 130 };
        // A tall car: 30 cm of clearance on a 20 cm wheel.
        var targets = AirRideModel.Targets([0.5, 0.5, 0.5, 0.5], [0.2, 0.2, 0.2, 0.2], w => w < 2, settings, AirLevel.Low, -50, -50);
        Assert.All(targets, h => Assert.Equal(0.2 * RideSettings.HubOverRadiusMin, h, 6));
    }

    [Fact]
    public void Maxi_caps_the_stance()
    {
        var (engine, game, _) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.SetStanceAsync(15, 10).Wait();
        t = Run(engine, t, 1);
        Assert.Equal(0.40 + 0.07 * 0.15, game.Heights()[0], 4);

        // Maxi down to +8: the saved stance comes down with it, and so does the car.
        engine.ChangeSettingsAsync(s => s.HighestPercent = 8).Wait();
        t = Run(engine, t, 2);
        Assert.Equal(8, engine.State.FrontStance);
        Assert.Equal(8, engine.State.RearStance);
        Assert.Equal(8, engine.State.FrontStanceTop);
        Assert.Equal(0.40 + 0.07 * 0.08, game.Heights()[0], 4);

        engine.SetStanceAsync(12, 12).Wait();
        Assert.Equal(8, engine.State.FrontStance);
        _ = t;
    }

    [Fact]
    public void Settings_from_0_2_keep_their_low_air_out()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airshift-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "lowDropPercent": 100, "parkedDropPercent": 90, "floorPercent": 5, "exhaustSeconds": 1.1 }""");
            var store = new SettingsStore(path);
            var settings = store.Load();
            Assert.Equal(100, settings.LowFrontPercent);
            Assert.Equal(100, settings.LowRearPercent);
            Assert.Equal(90, settings.ParkedDropPercent);
            Assert.Equal(100, settings.LowestPercent);
            Assert.Equal(1.1, settings.ExhaustSeconds);
            store.Save(settings);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("lowDropPercent", text);
            Assert.Contains("\"lowFrontPercent\": 100", text);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_drag_is_logged_once_with_where_it_stopped()
    {
        var (engine, _, log) = Engine();
        var t = Run(engine, 0, 1.2);
        log.Clear();
        for (var v = 0; v >= -20; v -= 2)
        {
            engine.SetStanceAsync(v, null).Wait();
            t = Run(engine, t, 0.05);
        }
        engine.SetWheelsAsync(new WheelChange(CamberFront: -6, TrackRear: 15)).Wait();
        engine.ChangeSettingsAsync(s => s.LowFrontPercent = 110).Wait();
        t = Run(engine, t, 0.5);
        Assert.DoesNotContain(log, l => l.StartsWith("Stance:"));
        Run(engine, t, 1);
        Assert.Equal(["Stance: front -20 %, rear 0 % (app)."], log.Where(l => l.StartsWith("Stance:")));
        Assert.Single(log, l => l.StartsWith("Wheels: camber front -6.0°") && l.Contains("track front 0 mm, rear +15 mm"));
        Assert.Single(log, l => l.StartsWith("Tune: Low air out front 110 %, rear 45 %"));
    }
}

public class TuneApiTests
{
    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    [Fact]
    public async Task The_api_shows_and_changes_the_tune()
    {
        var game = new FakeGame(Profile());
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null));
        for (var t = 0.0; t < 1.2; t += 1.0 / 60) engine.Tick(t);

        var body = System.Text.Json.Nodes.JsonNode.Parse(
            """{ "command": "tune.set", "lowFront": 140, "lowRear": 60, "lowest": 118, "highest": 8, "dumpSeconds": 1.8, "order": "front-first", "settle": false }""")!.AsObject();
        var result = await AirShift.Core.Api.ApiContract.Run(engine, body, null);
        Assert.True(result.Ok, result.Message);
        engine.Tick(1.3);
        var tune = AirShift.Core.Api.ApiContract.State(engine.State, "0.4.0")["tune"]!;
        Assert.Equal(130, tune["lowFront"]!.GetValue<double>());
        Assert.Equal(60, tune["lowRear"]!.GetValue<double>());
        Assert.Equal(118, tune["lowest"]!.GetValue<double>());
        Assert.Equal(8, tune["highest"]!.GetValue<double>());
        Assert.Equal(1.8, tune["dumpSeconds"]!.GetValue<double>());
        Assert.Equal("front-first", tune["order"]!.GetValue<string>());
        Assert.False(tune["settle"]!.GetValue<bool>());
        Assert.Equal(130, tune["ranges"]!["drop"]!["max"]!.GetValue<double>());
        var stance = AirShift.Core.Api.ApiContract.State(engine.State, "0.4.0")["stance"]!;
        Assert.Equal(8, stance["frontMax"]!.GetValue<double>());

        var wrong = System.Text.Json.Nodes.JsonNode.Parse("""{ "command": "tune.set", "order": "sideways" }""")!.AsObject();
        Assert.False((await AirShift.Core.Api.ApiContract.Run(engine, wrong, null)).Ok);
        var empty = System.Text.Json.Nodes.JsonNode.Parse("""{ "command": "tune.set" }""")!.AsObject();
        Assert.False((await AirShift.Core.Api.ApiContract.Run(engine, empty, null)).Ok);
    }
}

public class QuitApiTests
{
    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    [Fact]
    public async Task App_quit_closes_the_app_and_is_refused_without_a_window()
    {
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.NotRunning;
            message = "no game";
            return null;
        }, new SettingsStore(null));
        var body = System.Text.Json.Nodes.JsonNode.Parse("""{ "command": "app.quit" }""")!.AsObject();
        var closed = 0;
        var result = await AirShift.Core.Api.ApiContract.Run(engine, body, null, () => closed++);
        Assert.True(result.Ok);
        Assert.Equal(1, closed);
        Assert.False((await AirShift.Core.Api.ApiContract.Run(engine, body, null)).Ok);
    }
}
