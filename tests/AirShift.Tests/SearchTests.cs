using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Memory;
using Xunit;

namespace AirShift.Tests;

/// <summary>
/// AirShift may find the game a second after it starts, long before the game has loaded: it keeps looking
/// until the car shows up, without staying on an answer that only fitted the half-loaded game.
/// </summary>
public class SearchTests
{
    // A global elsewhere in the module that holds a pointer but leads to no car: what the profile's
    // fallback reads on the other edition of the game.
    private const long WrongGlobal = 0x18_0000;

    private static GameProfile Profile(long? fallback = null)
    {
        var profile = GameProfile.BuiltIn().Single(p => p.Id == "forza-horizon-6");
        profile.Context.FallbackRva = fallback is { } rva ? new HexValue(rva) : null;
        return profile;
    }

    private static (AirShiftEngine engine, List<string> log) Engine(FakeGame game, GameProfile profile)
    {
        var engine = new AirShiftEngine(profile, (GameProfile _, out GameStatus status, out string message) =>
        {
            status = GameStatus.Attached;
            message = "ok";
            return game;
        }, new SettingsStore(null));
        var log = new List<string>();
        engine.Log += log.Add;
        return (engine, log);
    }

    private static double Run(AirShiftEngine engine, double t, double seconds)
    {
        var end = t + seconds;
        for (; t < end; t += 1.0 / 60) engine.Tick(t);
        return t;
    }

    [Fact]
    public void A_game_attached_while_it_loads_still_gets_its_car_found()
    {
        // The code is not readable yet and the fallback points at the wrong global for this edition.
        var profile = Profile(WrongGlobal);
        var game = new FakeGame(profile, withSignature: false);
        game.SetPointer(FakeGame.Base + WrongGlobal, FakeGame.Heap + 0x3_8000);
        var (engine, log) = Engine(game, profile);

        var t = Run(engine, 0, 10);
        Assert.False(engine.State.CarReady);

        game.LoadCode();
        t = Run(engine, t, 6.5);

        Assert.True(engine.State.CarReady, engine.State.Message);
        Assert.Contains(log, line => line.StartsWith("Car found", StringComparison.Ordinal));
    }

    [Fact]
    public void While_the_game_loads_the_window_says_so_not_that_the_build_is_unsupported()
    {
        var profile = Profile();
        var game = new FakeGame(profile, withSignature: false);
        var (engine, log) = Engine(game, profile);

        var t = Run(engine, 0, 5);
        Assert.Equal("Waiting for Forza Horizon 6 to load.", engine.State.Message);
        // The search state is written once, not every second.
        Assert.Single(log, line => line.StartsWith("Looking for the car", StringComparison.Ordinal));

        Run(engine, t, AirShiftEngine.LoadingSeconds);
        Assert.StartsWith("This game build is not supported yet", engine.State.Message);
    }

    [Fact]
    public void The_signature_holds_while_the_game_has_not_filled_its_global_yet()
    {
        var profile = Profile();
        var game = new FakeGame(profile);
        var global = FakeGame.Base + FakeGame.ContextRva;
        game.SetPointer(global, 0);
        var locator = new CarLocator(game, profile);

        for (var i = 0; i < 3 * CarLocator.SearchAgainAfter; i++) Assert.Null(locator.Locate().Car);
        Assert.Equal("signature", locator.ContextSource);
        Assert.Equal(1, game.ModuleScans);

        game.SetPointer(global, FakeGame.Heap + 0x100);
        Assert.Equal(LocateStatus.Found, locator.Locate().Status);
        Assert.Equal(1, game.ModuleScans);
    }

    [Fact]
    public void A_search_without_a_context_is_not_repeated_every_second()
    {
        var profile = Profile();
        var game = new FakeGame(profile, withSignature: false);
        var locator = new CarLocator(game, profile);

        for (var i = 0; i < 2 * CarLocator.SearchAgainAfter; i++) Assert.Equal(LocateStatus.NoContext, locator.Locate().Status);
        Assert.Equal(2, game.ModuleScans);
    }

    [Fact]
    public void A_fallback_that_found_the_car_is_kept_while_the_player_is_in_menus()
    {
        var profile = Profile(FakeGame.ContextRva);
        var game = new FakeGame(profile, withSignature: false);
        var locator = new CarLocator(game, profile);
        Assert.Equal(LocateStatus.Found, locator.Locate().Status);
        Assert.StartsWith("fallback", locator.ContextSource);

        // Back in the menus: the entity list is empty.
        game.SetPointer(FakeGame.Heap + 0x1000 + 0x20, FakeGame.Heap + 0x2000);
        for (var i = 0; i < 3 * CarLocator.SearchAgainAfter; i++) Assert.Null(locator.Locate().Car);
        Assert.Equal(1, game.ModuleScans);
    }

    [Fact]
    public void An_unproven_fallback_is_checked_again()
    {
        var profile = Profile(WrongGlobal);
        var game = new FakeGame(profile, withSignature: false);
        game.SetPointer(FakeGame.Base + WrongGlobal, FakeGame.Heap + 0x3_8000);
        var locator = new CarLocator(game, profile);

        for (var i = 0; i < CarLocator.SearchAgainAfter; i++) Assert.Null(locator.Locate().Car);
        Assert.StartsWith("fallback", locator.ContextSource);
        game.LoadCode();
        Assert.Equal(LocateStatus.Found, locator.Locate().Status);
        Assert.Equal("signature", locator.ContextSource);
    }

    [Fact]
    public void The_microsoft_edition_is_named_by_its_package_version()
    {
        Assert.Equal("3.483.970.0", ImageVersion.FromPackagePath("Microsoft.ForteBaseGame_3.483.970.0_x64__8wekyb3d8bbwe"));
        Assert.Null(ImageVersion.FromPackagePath(null));
    }
}
