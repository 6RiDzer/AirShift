using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

/// <summary>The journal the engine keeps on disk, and how it puts a car back when AirShift could not.</summary>
public class RescueTests
{
    private const int FL = 0, FR = 1, RR = 2, RL = 3;

    private sealed class MemoryJournal : IJournalStore
    {
        public SessionJournal? Saved { get; private set; }

        public SessionJournal? Load() => Saved;

        public void Save(SessionJournal? journal) => Saved = journal is { Empty: false } ? journal : null;
    }

    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    private static (AirShiftEngine engine, FakeGame game, MemoryJournal journal) Engine()
    {
        var game = new FakeGame(Profile());
        var journal = new MemoryJournal();
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null), journal);
        return (engine, game, journal);
    }

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

    private static double[,] Tables(FakeGame game)
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

    /// <summary>Lowers the car, turns the front camber and widens the front track, as a user would.</summary>
    private static double Customise(AirShiftEngine engine, FakeGame game, double t)
    {
        engine.SetLevelAsync(AirLevel.Low).Wait();
        engine.SetWheelsAsync(new WheelChange(CamberFront: -7, TrackFront: 30)).Wait();
        return Run(engine, game, t, 3);
    }

    [Fact]
    public void The_journal_follows_what_AirShift_holds()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Assert.Null(journal.Saved);

        engine.SetLevelAsync(AirLevel.Low).Wait();
        t = Run(engine, game, t, 3);
        Assert.Equal([0.40, 0.40, 0.42, 0.42], journal.Saved!.StockHeights!, (a, b) => Math.Abs(a - b) < 1e-5);
        Assert.Empty(journal.Saved.Wheels);
        Assert.Equal(4321, journal.Saved.GameProcess);
        Assert.Equal(4242, journal.Saved.CarId);

        engine.SetWheelsAsync(new WheelChange(CamberFront: -7)).Wait();
        t = Run(engine, game, t, 0.5);
        Assert.Equal([FL, FR], journal.Saved!.Wheels.Select(c => c.Wheel).Order());
        // The stock curve is what it keeps, not the turned one.
        Assert.Equal(-1.5 + 0.04 * (0 - 15), journal.Saved.Wheels.Single(c => c.Wheel == FL).Camber[0], 3);

        engine.ResetCarAsync().Wait();
        Run(engine, game, t, 4);
        Assert.Null(journal.Saved);
    }

    [Fact]
    public void After_a_crash_the_journal_puts_stock_back()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Tables(game);
        Customise(engine, game, t);
        Assert.NotEqual(0.40f, game.Heights()[0], 3);
        Assert.Equal(-7, game.LiveCamber(FL), 1);

        // AirShift stops here without Shutdown: only the journal on disk is left.
        var result = SessionRescue.Run(game, Profile(), journal.Saved!, _ => { });
        Assert.False(result.Retry, result.Message);
        Assert.True(result.Remaining.Empty);
        Assert.Contains("stock heights put back", result.Message);
        Assert.Contains("2 wheels", result.Message);
        Assert.Equal([0.40f, 0.40f, 0.42f, 0.42f], game.Heights());
        AssertSame(stock, Tables(game));
    }

    [Fact]
    public void A_crash_in_the_middle_of_a_drag_is_put_back_too()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Tables(game);
        engine.SetWheelsAsync(new WheelChange(CamberFront: -4)).Wait();
        t = Run(engine, game, t, 0.5);
        // The journal was written for -4; the drag goes on to -9 and AirShift dies.
        engine.SetWheelsAsync(new WheelChange(CamberFront: -9, TrackFront: 12)).Wait();
        Run(engine, game, t, 0.3);
        var result = SessionRescue.Run(game, Profile(), journal.Saved!, _ => { });
        Assert.False(result.Retry, result.Message);
        AssertSame(stock, Tables(game));
    }

    [Fact]
    public void What_the_game_rebuilt_since_is_left_alone()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Customise(engine, game, t);
        // After the crash the player loads a new tune: new stock heights and camber.
        game.SetStock([0.45f, 0.45f, 0.47f, 0.47f], [0.33f, 0.33f, 0.34f, 0.34f]);
        game.BakeTune(-2.5);
        var rebuilt = Tables(game);
        var result = SessionRescue.Run(game, Profile(), journal.Saved!, _ => { });
        Assert.False(result.Retry, result.Message);
        Assert.Contains("left alone", result.Message);
        Assert.Equal([0.45f, 0.45f, 0.47f, 0.47f], game.Heights());
        AssertSame(rebuilt, Tables(game));
    }

    [Fact]
    public void Another_car_in_the_same_place_is_never_written()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Customise(engine, game, t);
        game.SetCarId(777);
        var before = game.Writes;
        var result = SessionRescue.Run(game, Profile(), journal.Saved!, _ => { });
        Assert.False(result.Retry);
        Assert.Contains("Another car", result.Message);
        Assert.Equal(before, game.Writes);
    }

    [Fact]
    public void A_car_out_of_view_is_tried_again_later()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Tables(game);
        Customise(engine, game, t);
        var saved = journal.Saved!;
        game.Speed = 1000; // reads as nonsense, like a loading screen
        var first = SessionRescue.Run(game, Profile(), saved, _ => { });
        Assert.True(first.Retry);
        Assert.NotEqual(0.40f, game.Heights()[0], 3);
        game.Speed = 0;
        var second = SessionRescue.Run(game, Profile(), first.Remaining, _ => { });
        Assert.False(second.Retry, second.Message);
        Assert.Equal(0.40f, game.Heights()[0], 4);
        AssertSame(stock, Tables(game));
    }

    [Fact]
    public void Closing_with_the_car_in_view_leaves_no_journal()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Customise(engine, game, t);
        Assert.NotNull(journal.Saved);
        engine.Shutdown();
        Assert.Null(journal.Saved);
        Assert.Equal(0.40f, game.Heights()[0], 4);
    }

    [Fact]
    public void Closing_while_the_car_is_out_of_view_still_puts_it_back()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        var stock = Tables(game);
        t = Customise(engine, game, t);
        // A menu: the car stops reading and AirShift loses it, but the game keeps its values.
        game.Speed = 1000;
        t = Run(engine, game, t, 1);
        Assert.False(engine.State.CarReady);
        Assert.NotNull(journal.Saved);
        // Back in the world but AirShift has not looked again yet, and the user closes it.
        game.Speed = 0;
        engine.Shutdown();
        Assert.Null(journal.Saved);
        Assert.Equal(0.40f, game.Heights()[0], 4);
        AssertSame(stock, Tables(game));
        _ = t;
    }

    [Fact]
    public void Closing_during_a_menu_leaves_the_journal_for_the_guard()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        t = Customise(engine, game, t);
        game.Speed = 1000;
        Run(engine, game, t, 1);
        engine.Shutdown();
        Assert.NotNull(journal.Saved);
        game.Speed = 0;
        Assert.False(SessionRescue.Run(game, Profile(), journal.Saved!, _ => { }).Retry);
        Assert.Equal(0.40f, game.Heights()[0], 4);
    }

    [Fact]
    public void The_game_closing_clears_the_journal()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        t = Customise(engine, game, t);
        Assert.NotNull(journal.Saved);
        game.HasExited = true;
        Run(engine, game, t, 0.1);
        Assert.Null(journal.Saved);
    }

    [Fact]
    public void The_journal_file_round_trips_and_removes_itself()
    {
        var (engine, game, journal) = Engine();
        var t = Run(engine, game, 0, 1.2);
        Customise(engine, game, t);
        var folder = Path.Combine(Path.GetTempPath(), "airshift-tests-" + Guid.NewGuid().ToString("N"));
        var file = new JournalFile(Path.Combine(folder, "session.json"));
        try
        {
            file.Save(journal.Saved);
            var loaded = file.Load();
            Assert.NotNull(loaded);
            Assert.True(loaded!.SameAs(journal.Saved));
            file.Save(null);
            Assert.Null(file.Load());
            Assert.False(File.Exists(Path.Combine(folder, "session.json")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
