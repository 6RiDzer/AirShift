using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AirShift.Core.Api;
using AirShift.Core.Engine;
using AirShift.Core.Game;

namespace AirShift.App;

internal static class Program
{
    public const string Version = "0.3.0";
    private static Mutex? _single;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && int.TryParse(args[1], out var owner))
        {
            if (args[0] == "--start-guard") return Guard.Start(owner);
            if (args[0] == "--guard") return Guard.Run(owner);
        }

        _single = new Mutex(true, @"Local\AirShift.SingleInstance", out var first);
        if (!first && !args.Contains("--elevated"))
        {
            AskRunningCopyToShow();
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
        Application.ThreadException += (_, e) => log("Window error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log("AirShift crashed: " + e.ExceptionObject);

        var profiles = GameProfile.Load(Path.Combine(folder, "profiles"));
        var profile = profiles.First(p => p.Id == "forza-horizon-6");
        using var engine = new AirShiftEngine(profile, Win32GameMemory.Open, new SettingsStore(Path.Combine(folder, "settings.json")), journal);
        log($"AirShift {Version} starting, profile {profile.Id} r{profile.Revision}.");
        log("Tune: " + AirShiftEngine.DescribeSettings(engine.Settings) + ".");
        // Straight to the file, so what the engine says while closing is kept too.
        engine.Log += log;
        Guard.Launch(log);

        MainForm? form = null;
        using var server = new ApiServer(engine, Version, () => form?.BeginInvoke(() => form.BringToFront2()));
        server.Start();
        var apiPath = ApiDiscovery.DefaultPath;
        ApiDiscovery.Write(apiPath, server.Port, server.Token, Version, Environment.ProcessPath);

        engine.Start();
        form = new MainForm(engine, log);
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
