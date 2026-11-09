using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AirShift.Core.Api;
using AirShift.Core.Engine;
using AirShift.Core.Game;

namespace AirShift.App;

internal static class Program
{
    public const string Version = "0.4.4";
    private static Mutex? _single;
    private static AirShiftEngine? _engine;
    private static Action<string>? _log;
    private static string? _apiPath;
    private static int _exiting;

    /// <summary>After a quit request, AirShift ends by itself if its window has not closed within this time.</summary>
    private static readonly TimeSpan QuitDeadline = TimeSpan.FromSeconds(10);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && int.TryParse(args[1], out var owner))
        {
            if (args[0] == "--start-guard") return Guard.Start(owner);
            if (args[0] == "--guard") return Guard.Run(owner);
        }

        // --minimized: started by another app, so stay out of the way.
        var minimized = args.Contains("--minimized");
        _single = new Mutex(true, @"Local\AirShift.SingleInstance", out var first);
        if (!first && !args.Contains("--elevated"))
        {
            if (!minimized) AskRunningCopyToShow();
            return 0;
        }

        var folder = ApiDiscovery.DefaultFolder;
        Directory.CreateDirectory(folder);
        var log = LogFile.Writer(folder);
        // The guard of a session that stopped may still be putting a car back: let it finish.
        Guard.WaitForRescue();
        var journal = new JournalFile(Guard.JournalPath(folder));
        journal.Save(null);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        MainForm? form = null;
        Application.ThreadException += (_, e) =>
        {
            log("Window error: " + e.Exception);
            // A window that fails while closing would otherwise stay open and could not be closed again.
            if (form?.CloseRequested == true) ExitNow($"Closing failed ({e.Exception.GetType().Name}).");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log("AirShift crashed: " + e.ExceptionObject);

        var profiles = GameProfile.Load(Path.Combine(folder, "profiles"));
        var profile = profiles.First(p => p.Id == "forza-horizon-6");
        using var engine = new AirShiftEngine(profile, Win32GameMemory.Open, new SettingsStore(Path.Combine(folder, "settings.json")), journal);
        log($"AirShift {Version} starting, profile {profile.Id} r{profile.Revision}.");
        log("Tune: " + AirShiftEngine.DescribeSettings(engine.Settings) + ".");
        // Straight to the file, so what the engine says while closing is kept too.
        engine.Log += log;
        Guard.Launch(log);
        _engine = engine;
        _log = log;

        using var server = new ApiServer(engine, Version, () => form?.BeginInvoke(() => form.BringToFront2()),
            quit: Quit);
        server.Start();
        var apiPath = ApiDiscovery.DefaultPath;
        _apiPath = apiPath;
        ApiDiscovery.Write(apiPath, server.Port, server.Token, Version, Environment.ProcessPath);

        engine.Start();
        form = new MainForm(engine, log);
        if (minimized) form.WindowState = FormWindowState.Minimized;
        try
        {
            Application.Run(form);
        }
        finally
        {
            engine.Shutdown();
            ApiDiscovery.Remove(apiPath);
            log("AirShift closed.");
        }
        return 0;

        // app.quit: close as if the window were closed, and end anyway if that does not happen in time.
        void Quit()
        {
            form?.BeginInvoke(() => form.Close());
            _ = Task.Run(async () =>
            {
                await Task.Delay(QuitDeadline).ConfigureAwait(false);
                ExitNow($"The window did not close within {QuitDeadline.TotalSeconds:0} s of app.quit.");
            });
        }
    }

    /// <summary>
    /// The way out when closing the window fails: the car goes back to stock (the guard process does it too if this
    /// cannot), the API file goes, and the process ends, so AirShift can never be left open and impossible to close.
    /// </summary>
    public static void ExitNow(string reason)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        try
        {
            _log?.Invoke(reason + " AirShift puts the car back and stops.");
        }
        catch (Exception)
        {
        }
        try
        {
            _engine?.Shutdown();
        }
        catch (Exception)
        {
        }
        try
        {
            if (_apiPath is not null) ApiDiscovery.Remove(_apiPath);
            _log?.Invoke("AirShift closed.");
        }
        catch (Exception)
        {
        }
        Environment.Exit(0);
    }

    public static void ReleaseSingleInstance()
    {
        try
        {
            _single?.ReleaseMutex();
        }
        catch (Exception)
        {
        }
    }

    private static void AskRunningCopyToShow()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(ApiDiscovery.DefaultPath))!;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", node["token"]!.GetValue<string>());
            http.PostAsync($"http://127.0.0.1:{node["port"]!.GetValue<int>()}/airshift/1/command",
                new StringContent("{\"command\":\"app.show\"}")).Wait(2000);
        }
        catch (Exception)
        {
        }
    }
}
