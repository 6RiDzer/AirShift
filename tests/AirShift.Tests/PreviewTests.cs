using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Ride;
using Xunit;

namespace AirShift.Tests;

/// <summary>Mini and Maxi show on the car while they change, then the car goes back to its level.</summary>
public class PreviewTests
{
    // The fake car: front hubs 0.40 m on 0.33 m wheels (7 cm clearance), rear 0.42 m on 0.34 m (8 cm).
    private const double Front = 0.40;
    private const double Rear = 0.42;

    private static GameProfile Profile() => GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");

    private static (AirShiftEngine engine, FakeGame game) Engine()
    {
        var game = new FakeGame(Profile());
        var engine = new AirShiftEngine(Profile(), (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null));
        return (engine, game);
    }

    /// <summary>Runs the engine at 60 Hz and returns the biggest front height change in one tick.</summary>
    private static double Run(AirShiftEngine engine, FakeGame game, ref double t, double seconds)
    {
        var biggest = 0.0;
        var last = (double)game.Heights()[0];
        var end = t + seconds;
        for (; t < end; t += 1.0 / 60)
        {
            engine.Tick(t);
            var now = (double)game.Heights()[0];
            biggest = Math.Max(biggest, Math.Abs(now - last));
            last = now;
        }
        return biggest;
    }

    [Fact]
    public void A_new_mini_shows_on_the_car_then_it_goes_back_to_ride()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.ChangeSettingsAsync(s => s.LowestPercent = 90).Wait();
        Assert.Equal(HeightPreview.Lowest, engine.State.Preview);
        var step = Run(engine, game, ref t, 1.8);
        Assert.Equal(Front - 0.07 * 0.9, game.Heights()[0], 4);
        Assert.Equal(Rear - 0.08 * 0.9, game.Heights()[2], 4);
        Assert.True(step < 0.004, $"jumped {step * 1000:0.0} mm in one tick");
        Assert.Contains("Showing Mini", engine.State.Message);

        Run(engine, game, ref t, 4);
        Assert.Equal(HeightPreview.None, engine.State.Preview);
        Assert.Equal(Front, game.Heights()[0], 4);
        Assert.Equal(AirLevel.Ride, engine.State.Level);
    }

    [Fact]
    public void A_drag_of_mini_is_followed_closely()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.ChangeSettingsAsync(s => s.LowestPercent = 99).Wait();
        Run(engine, game, ref t, 1.8);
        for (var v = 95.0; v >= 70; v -= 5)
        {
            engine.ChangeSettingsAsync(s => s.LowestPercent = v).Wait();
            Run(engine, game, ref t, 0.12);
        }
        Run(engine, game, ref t, 0.3);
        Assert.Equal(Front - 0.07 * 0.7, game.Heights()[0], 4);
        Assert.Equal(HeightPreview.Lowest, engine.State.Preview);
    }

    [Fact]
    public void A_new_maxi_shows_each_axle_at_its_top()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.ChangeSettingsAsync(s => s.HighestPercent = 12).Wait();
        Assert.Equal(HeightPreview.Highest, engine.State.Preview);
        Run(engine, game, ref t, 1.5);
        Assert.Equal(Front + 0.07 * 0.12, game.Heights()[0], 4);
        Assert.Equal(Rear + 0.08 * 0.10, game.Heights()[2], 4);
        Assert.Equal(8.4, engine.State.Axles!.RaiseMm(true, 12), 1);
        Assert.Equal(63.0, engine.State.Axles!.LowestMm(true, 90), 1);
    }

    [Fact]
    public void Mini_does_not_show_at_speed()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        game.Speed = 72;
        Run(engine, game, ref t, 0.1);
        engine.ChangeSettingsAsync(s => s.LowestPercent = 90).Wait();
        Run(engine, game, ref t, 1.5);
        Assert.Equal(HeightPreview.None, engine.State.Preview);
        Assert.Equal(Front, game.Heights()[0], 4);
        Assert.Contains("Slow down", engine.State.Message);
        Assert.Equal(90, engine.Settings.LowestPercent);
    }

    [Fact]
    public void Choosing_a_level_ends_the_preview()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.ChangeSettingsAsync(s => s.LowestPercent = 90).Wait();
        Run(engine, game, ref t, 0.5);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Assert.Equal(HeightPreview.None, engine.State.Preview);
        Run(engine, game, ref t, 5);
        Assert.Equal(Front - 0.07 * 0.45, game.Heights()[0], 4);
        Assert.Equal(AirLevel.Low, engine.State.Level);
    }

    [Fact]
    public void Raising_mini_over_a_low_car_lifts_it_smoothly()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.ChangeSettingsAsync(s => { s.LowFrontPercent = 130; s.LowRearPercent = 130; s.LowestPercent = 118; }).Wait();
        Run(engine, game, ref t, 3);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Run(engine, game, ref t, 5);
        Assert.Equal(Front - 0.07 * 1.18, game.Heights()[0], 4);

        engine.ChangeSettingsAsync(s => s.LowestPercent = 100).Wait();
        var step = Run(engine, game, ref t, 1.8);
        Assert.True(step < 0.004, $"jumped {step * 1000:0.0} mm in one tick");
        Assert.Equal(Front - 0.07, game.Heights()[0], 4);
        // After the preview the car stays low: Mini 100 % is its Low now.
        Run(engine, game, ref t, 4);
        Assert.Equal(Front - 0.07, game.Heights()[0], 4);
        Assert.Equal(AirLevel.Low, engine.State.Level);
    }

    [Fact]
    public void A_setting_changed_during_a_move_does_not_make_the_car_jump()
    {
        var (engine, game) = Engine();
        var t = 0.0;
        Run(engine, game, ref t, 1.2);
        engine.SetLevelAsync(AirLevel.Low).Wait();
        Run(engine, game, ref t, 0.2);
        engine.ChangeSettingsAsync(s => s.LowFrontPercent = 80).Wait();
        var step = Run(engine, game, ref t, 5);
        Assert.True(step < 0.004, $"jumped {step * 1000:0.0} mm in one tick");
        Assert.Equal(Front - 0.07 * 0.8, game.Heights()[0], 4);
    }
}
