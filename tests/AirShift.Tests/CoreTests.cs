using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AirShift.Core.Api;
using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Memory;
using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

public class CoreTests
{
    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    private static (AirShiftEngine engine, FakeGame game) Engine(string? version = "1.440.853.0", RideSettings? settings = null)
    {
        var game = new FakeGame(Profile(), version);
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null));
        if (settings is not null)
        {
            engine.Settings.ExhaustSeconds = settings.ExhaustSeconds;
            engine.Settings.FillSeconds = settings.FillSeconds;
        }
        return (engine, game);
    }

    private static double Run(AirShiftEngine engine, double from, double seconds)
    {
        var t = from;
        for (; t < from + seconds; t += 1.0 / 60) engine.Tick(t);
        return t;
    }

    [Fact]
    public void Signature_matches_with_wildcards()
    {
        var sig = Signature.Parse("48 8B ? 15");
        var hits = sig.FindAll(new byte[] { 0, 0x48, 0x8B, 0x99, 0x15, 0x48, 0x8B, 0, 0x16 });
        Assert.Equal([1], hits);
    }

    [Fact]
    public void Builtin_profile_parses_hex_values()
    {
        var p = Profile();
        Assert.Equal(0x7740, p.EntityToCar.Value);
        Assert.Equal(0xAC0, p.Wheels.Stride.Value);
        Assert.Equal(3, p.Chain.Length);
        Assert.True(p.MatchesBuild("1.440.853.0"));
        Assert.False(p.MatchesBuild("1.441.100.0"));
        Assert.True(p.Wheels.IsFront(0) && p.Wheels.IsFront(1) && !p.Wheels.IsFront(2) && !p.Wheels.IsFront(3));
    }

    [Fact]
    public void Locator_finds_the_car_through_the_signature()
    {
        var game = new FakeGame(Profile());
        var locator = new CarLocator(game, Profile());
        var result = locator.Locate();
        Assert.Equal(LocateStatus.Found, result.Status);
        Assert.Equal("signature", locator.ContextSource);
        Assert.Equal(4242, result.Car!.CarId);
        Assert.Equal(0.40f, result.Car.HubHeights[0], 4);
        var report = locator.Probe();
        Assert.Contains("Nothing was written", report);
        Assert.Equal(0, game.Writes);
    }

    [Fact]
    public void Locator_rejects_values_out_of_range()
    {
        var game = new FakeGame(Profile());
        game.SetStock([5f, 5f, 5f, 5f], [0.3f, 0.3f, 0.3f, 0.3f]);
        var result = new CarLocator(game, Profile()).Locate();
        Assert.Equal(LocateStatus.Unreadable, result.Status);
    }

    [Fact]
    public void Air_down_reaches_low_and_air_up_returns_to_stock()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        Assert.True(engine.State.CarReady);
        var result = engine.SetLevelAsync(AirLevel.Low).Result;
        Assert.True(result.Ok, result.Message);
        t = Run(engine, t, 3);
        var low = game.Heights();
        // Low removes 45 % of the 7 cm front clearance.
        Assert.Equal(0.40 - 0.07 * 0.45, low[0], 3);
        Assert.Equal(0.42 - 0.08 * 0.45, low[2], 3);
        Assert.Equal(AirLevel.Low, engine.State.Level);
        Assert.True(engine.State.FrontHeightPercent < -40);

        engine.SetLevelAsync(AirLevel.Ride).Wait();
        Run(engine, t, 4);
        Assert.Equal(0.40f, game.Heights()[0], 4);
        Assert.Equal(0.42f, game.Heights()[3], 4);
    }

    [Fact]
    public void Floor_is_never_crossed()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.ChangeSettingsAsync(s => { s.ParkedDropPercent = 100; s.FloorPercent = 20; }).Wait();
        engine.SetStanceAsync(-50, -50).Wait();
        engine.SetLevelAsync(AirLevel.Parked).Wait();
        Run(engine, t, 4);
        var h = game.Heights();
        Assert.True(h[0] >= 0.33 + 0.07 * 0.2 - 1e-4, h[0].ToString());
    }

    [Fact]
    public void Rebaked_stock_values_are_reapplied()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        t = Run(engine, t, 3);
        var low = game.Heights();
        game.SetStock([0.40f, 0.40f, 0.42f, 0.42f], [0.33f, 0.33f, 0.34f, 0.34f]);
        Run(engine, t, 0.2);
        Assert.Equal(low[0], game.Heights()[0], 4);
        Assert.True(engine.State.Reapplied >= 1);
    }

    [Fact]
    public void Speed_guard_refuses_and_lifts()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        game.Speed = 45;
        t = Run(engine, t, 0.1);
        var refused = engine.SetLevelAsync(AirLevel.Low).Result;
        Assert.False(refused.Ok);
        game.Speed = 10;
        t = Run(engine, t, 0.1);
        Assert.True(engine.SetLevelAsync(AirLevel.Low).Result.Ok);
        t = Run(engine, t, 3);
        game.Speed = 75;
        t = Run(engine, t, 4);
        Assert.Equal(AirLevel.Ride, engine.State.Level);
        Assert.Equal(0.40f, game.Heights()[0], 4);
    }

    [Fact]
    public void Unknown_build_blocks_writes_until_allowed()
    {
        var (engine, game) = Engine("1.500.000.0");
        var t = Run(engine, 0, 1.2);
        Assert.False(engine.State.WritesAllowed);
        Assert.False(engine.SetLevelAsync(AirLevel.Low).Result.Ok);
        Assert.Equal(0, game.Writes);
        Assert.True(engine.AllowThisBuildAsync().Result.Ok);
        Run(engine, t, 0.1);
        Assert.True(engine.SetLevelAsync(AirLevel.Low).Result.Ok);
    }

    [Fact]
    public void A_different_setup_becomes_the_new_stock()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        t = Run(engine, t, 3);
        game.SetStock([0.50f, 0.50f, 0.52f, 0.52f], [0.33f, 0.33f, 0.34f, 0.34f]);
        Run(engine, t, 0.5);
        Assert.Equal(AirLevel.Ride, engine.State.Level);
        Assert.Equal(0.50f, game.Heights()[0], 4);
    }

    [Fact]
    public void Stance_is_remembered_per_car_and_applied()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.SetStanceAsync(-20, 10).Wait();
        Run(engine, t, 3);
        Assert.Equal(0.40 - 0.07 * 0.2, game.Heights()[0], 3);
        Assert.Equal(0.42 + 0.08 * 0.1, game.Heights()[2], 3);
        Assert.Equal(-20, engine.Settings.Cars["car:4242"].Front);
        engine.SetStanceAsync(99, null).Wait();
        Assert.Equal(20, engine.Settings.Cars["car:4242"].Front);
    }

    [Fact]
    public void Shutdown_restores_stock()
    {
        var (engine, game) = Engine();
        var t = Run(engine, 0, 1.2);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Run(engine, t, 3);
        Assert.NotEqual(0.40f, game.Heights()[0], 3);
        engine.Shutdown();
        Assert.Equal(0.40f, game.Heights()[0], 4);
    }

    [Fact]
    public void Motion_dumps_faster_than_it_fills()
    {
        var s = new RideSettings();
        var stock = new double[] { 0.4, 0.4, 0.4, 0.4 };
        var radii = new double[] { 0.3, 0.3, 0.3, 0.3 };
        var low = new double[] { 0.35, 0.35, 0.35, 0.35 };
        var down = RideMotion.Plan(stock, low, stock, radii, w => w < 2, s, 0.01);
        var up = RideMotion.Plan(low, stock, stock, radii, w => w < 2, s, 0.01);
        Assert.True(down.TotalSeconds - WheelMove.SettleSeconds < up.TotalSeconds);
        s.Order = AxleOrder.FrontFirst;
        var staged = RideMotion.Plan(stock, low, stock, radii, w => w < 2, s, 0.01);
        var mid = staged.At(0.3);
        Assert.True(mid[0] < mid[2]);
    }

    [Fact]
    public async Task Api_needs_the_token_and_runs_commands()
    {
        var (engine, game) = Engine();
        Run(engine, 0, 1.2);
        using var server = new ApiServer(engine, "0.1.0");
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var hello = JsonNode.Parse(await http.GetStringAsync("/airshift/1/hello"))!;
        Assert.Equal("airshift/1", hello["api"]!.GetValue<string>());
        var denied = await http.GetAsync("/airshift/1/state");
        Assert.Equal(401, (int)denied.StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        var state = JsonNode.Parse(await http.GetStringAsync("/airshift/1/state"))!;
        Assert.True(state["car"]!["ready"]!.GetValue<bool>());
        Assert.Equal("ride", state["air"]!["level"]!.GetValue<string>());

        // Commands run on the engine thread; drive it while the request waits.
        var cts = new CancellationTokenSource();
        var ticker = Task.Run(() =>
        {
            var t = 1.3;
            while (!cts.IsCancellationRequested)
            {
                engine.Tick(t += 1.0 / 60);
                Thread.Sleep(2);
            }
        });
        var post = await http.PostAsync("/airshift/1/command",
            new StringContent("{\"command\":\"preset.set\",\"preset\":\"parked\"}", Encoding.UTF8, "application/json"));
        var reply = JsonNode.Parse(await post.Content.ReadAsStringAsync())!;
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal("parked", reply["state"]!["air"]!["level"]!.GetValue<string>());
        var bad = await http.PostAsync("/airshift/1/command", new StringContent("{\"command\":\"nope\"}"));
        Assert.Equal(409, (int)bad.StatusCode);
        cts.Cancel();
        await ticker;
    }
}
