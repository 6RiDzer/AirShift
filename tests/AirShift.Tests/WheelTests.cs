using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

public class WheelTests
{
    private const int FL = 0, FR = 1, RR = 2, RL = 3;

    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    private static (AirShiftEngine engine, FakeGame game) Engine(bool solidRear = false, FakeGame? existing = null)
    {
        var game = existing ?? new FakeGame(Profile(), solidRear: solidRear);
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null));
        return (engine, game);
    }

    /// <summary>Ticks the engine, with the game's physics reading the tables into the live camber.</summary>
    private static double Run(AirShiftEngine engine, FakeGame game, double from, double seconds)
    {
        var t = from;
        for (; t < from + seconds; t += 1.0 / 60)
        {
            engine.Tick(t);
            game.SyncLiveCamber();
        }
        return t;
    }

    private static double[,] Snapshot(FakeGame game)
    {
        var values = new double[4, FakeGame.Entries * 3];
        for (var w = 0; w < 4; w++)
        for (var i = 0; i < FakeGame.Entries; i++)
        {
            values[w, i * 3] = game.Camber(w, i);
            values[w, i * 3 + 1] = game.TrackX(w, i);
            values[w, i * 3 + 2] = game.TrackXCopy(w, i);
        }
        return values;
    }

    private static void AssertSame(double[,] expected, double[,] actual)
    {
        for (var w = 0; w < 4; w++)
        for (var i = 0; i < expected.GetLength(1); i++)
            Assert.Equal(expected[w, i], actual[w, i], 4);
    }

    [Fact]
    public void Probe_reads_the_tables_and_writes_nothing()
    {
        var game = new FakeGame(Profile());
        var report = new CarLocator(game, Profile()).Probe();
        Assert.Contains("FL: table 0x", report);
        Assert.Contains("live camber -1.50°", report);
        Assert.Contains("Front axle: independent", report);
        Assert.Contains("Rear axle: independent", report);
        Assert.Contains("Nothing was written", report);
        Assert.Equal(0, game.Writes);

        var solid = new CarLocator(new FakeGame(Profile(), solidRear: true), Profile()).Probe();
        Assert.Contains("Rear axle: solid axle", solid);
    }

    [Fact]
    public void Camber_turns_each_curve_and_mirrors_the_right_wheel()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Assert.True(engine.State.Wheels.Front && engine.State.Wheels.Rear, engine.State.Wheels.Reason);
        Assert.Equal(-1.5, engine.State.Wheels.StockCamberFront);
        var stock = Snapshot(game);

        var result = engine.SetWheelsAsync(new WheelChange(CamberFront: -6)).Result;
        Assert.True(result.Ok, result.Message);
        Run(engine, game, t, 0.5);

        Assert.Equal(-6, game.LiveCamber(FL), 2);
        Assert.Equal(6, game.LiveCamber(FR), 2);
        // The camber's own change over the travel is kept.
        Assert.Equal(stock[FL, 0] - stock[FL, 29 * 3], game.Camber(FL, 0) - game.Camber(FL, 29), 3);
        // The rear and the track are left alone, and so is every other field.
        Assert.Equal(stock[RL, 15 * 3], game.Camber(RL, 15), 4);
        Assert.Equal(stock[FL, 1], game.TrackX(FL, 0), 5);
        Assert.Equal(1234.5f, game.Other(FL, 3));
        Assert.Equal(-6, engine.State.Wheels.LiveCamberFront);
        Assert.Equal(-6, engine.State.CamberFront);
    }

    [Fact]
    public void Track_width_moves_both_wheels_outward()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Snapshot(game);
        Assert.True(engine.SetWheelsAsync(new WheelChange(TrackFront: 30, TrackRear: -20)).Result.Ok);
        Run(engine, game, t, 0.5);
        Assert.Equal(stock[FL, 5 * 3 + 1] - 0.030, game.TrackX(FL, 5), 5);
        Assert.Equal(stock[FR, 5 * 3 + 1] + 0.030, game.TrackX(FR, 5), 5);
        Assert.Equal(stock[FR, 5 * 3 + 2] + 0.030, game.TrackXCopy(FR, 5), 5);
        Assert.Equal(stock[RL, 7 * 3 + 1] + 0.020, game.TrackX(RL, 7), 5);
        Assert.Equal(stock[RR, 7 * 3 + 1] - 0.020, game.TrackX(RR, 7), 5);
        Assert.Equal(stock[FL, 15 * 3], game.Camber(FL, 15), 4);
    }

    [Fact]
    public void Closing_puts_the_stock_curves_back_exactly()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Snapshot(game);
        engine.SetWheelsAsync(new WheelChange(CamberFront: -8, CamberRear: -4, TrackFront: 40, TrackRear: 25)).Wait();
        Run(engine, game, t, 0.5);
        Assert.NotEqual(stock[FL, 0], game.Camber(FL, 0), 2);
        engine.Shutdown();
        AssertSame(stock, Snapshot(game));
    }

    [Fact]
    public void A_rebuilt_table_gets_the_settings_again()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        engine.SetWheelsAsync(new WheelChange(CamberFront: -6, TrackFront: 20)).Wait();
        t = Run(engine, game, t, 0.5);

        // The game applies the same tune again: stock values come back.
        game.BakeTune(-1.5);
        t = Run(engine, game, t, 1.0);
        Assert.Equal(-6, game.LiveCamber(FL), 2);

        // A new tune with more camber of its own: the user's camber still shows.
        game.BakeTune(-2.5, track: 0.80);
        Run(engine, game, t, 1.0);
        Assert.Equal(-6, game.LiveCamber(FL), 2);
        Assert.Equal(6, game.LiveCamber(FR), 2);
        Assert.Equal(-(0.80 + 0.020), game.TrackX(FL, 0), 4);
        Assert.Equal(-2.5, engine.State.Wheels.StockCamberFront);
    }

    [Fact]
    public void A_solid_rear_axle_is_left_alone()
    {
        var (engine, game) = Engine(solidRear: true);
        var t = Run(engine, game, 0, 1.2);
        Assert.True(engine.State.Wheels.Front);
        Assert.False(engine.State.Wheels.Rear);
        Assert.Contains("solid axle", engine.State.Wheels.Reason);
        var before = game.Writes;
        Assert.False(engine.SetWheelsAsync(new WheelChange(CamberRear: -5)).Result.Ok);
        Assert.True(engine.SetWheelsAsync(new WheelChange(CamberFront: -5)).Result.Ok);
        Run(engine, game, t, 0.5);
        Assert.Equal(-5, game.LiveCamber(FL), 2);
        Assert.Equal(1.5, game.LiveCamber(RR), 2);
        Assert.True(game.Writes > before);
    }

    [Fact]
    public void Nothing_is_written_on_an_unknown_game_version()
    {
        var game = new FakeGame(Profile(), "1.500.000.0");
        var (engine, _) = Engine(existing: game);
        Run(engine, game, 0, 1.2);
        Assert.False(engine.SetWheelsAsync(new WheelChange(CamberFront: -6)).Result.Ok);
        Assert.Equal(0, game.Writes);
    }

    [Fact]
    public void Reset_puts_height_stance_camber_and_track_back()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Snapshot(game);
        engine.SetStanceAsync(-30, 10).Wait();
        engine.SetWheelsAsync(new WheelChange(CamberFront: -9, TrackRear: 50)).Wait();
        engine.SetLevelAsync(AirLevel.Low).Wait();
        t = Run(engine, game, t, 3);
        Assert.NotEqual(0.40f, game.Heights()[0], 3);

        var reset = engine.ResetCarAsync().Result;
        Assert.True(reset.Ok, reset.Message);
        Run(engine, game, t, 4);
        Assert.Equal(0.40f, game.Heights()[0], 4);
        Assert.Equal(0.42f, game.Heights()[2], 4);
        AssertSame(stock, Snapshot(game));
        var memory = engine.Settings.Cars["car:4242"];
        Assert.False(memory.HasStance || memory.HasWheels);
        Assert.Null(memory.WheelCamberShift);
        Assert.Equal(AirLevel.Ride, engine.State.Level);
    }

    [Fact]
    public void After_a_crash_stock_is_found_again()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Snapshot(game);
        engine.SetWheelsAsync(new WheelChange(CamberFront: -7, TrackFront: 30)).Wait();
        Run(engine, game, t, 1.5);
        // AirShift stops without putting anything back; its settings were saved.
        var saved = engine.Settings.Cars["car:4242"];
        Assert.NotNull(saved.WheelCamberShift);

        var (again, _) = Engine(existing: game);
        again.Settings.Cars["car:4242"] = saved;
        t = Run(again, game, 0, 1.2);
        Assert.Equal(-1.5, again.State.Wheels.StockCamberFront);
        Assert.Equal(-7, game.LiveCamber(FL), 2);
        again.ResetCarAsync().Wait();
        Run(again, game, t, 3);
        AssertSame(stock, Snapshot(game));
    }

    [Fact]
    public void Stance_stops_at_each_axles_top_and_follows_a_drag_quickly()
    {
        var (engine, game) = Engine();
        var t = Run(engine, game, 0, 1.2);
        engine.SetStanceAsync(99, 99).Wait();
        Assert.Equal(15, engine.Settings.Cars["car:4242"].Front);
        Assert.Equal(10, engine.Settings.Cars["car:4242"].Rear);
        t = Run(engine, game, t, 1);
        Assert.Equal(0.40 + 0.07 * 0.15, game.Heights()[0], 4);

        engine.SetStanceAsync(-20, null).Wait();
        Run(engine, game, t, AirShiftEngine.StanceSeconds + 0.05);
        Assert.Equal(0.40 - 0.07 * 0.20, game.Heights()[0], 4);
        Assert.False(engine.State.Moving);
    }
}
