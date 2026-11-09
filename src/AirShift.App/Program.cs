using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AirShift.Core.Api;
using AirShift.Core.Engine;
using AirShift.Core.Game;

namespace AirShift.App;

internal static class Program
{
    public const string Version = "0.1.0";
    private static Mutex? _single;

    [STAThread]
    private static int Main(string[] args)
    {
        _single = new Mutex(true, @"Local\AirShift.SingleInstance", out var first);
        if (!first && !args.Contains("--elevated"))
        {
            AskRunningCopyToShow();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var folder = ApiDiscovery.DefaultFolder;
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, "airshift.log");
        void Log(string line)
        {
            try
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_000_000) File.Delete(logPath);
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }

        var profiles = GameProfile.Load(Path.Combine(folder, "profiles"));
        var profile = profiles.First(p => p.Id == "forza-horizon-6");
        using var engine = new AirShiftEngine(profile, Win32GameMemory.Open, new SettingsStore(Path.Combine(folder, "settings.json")));
        Log($"AirShift {Version} starting, profile {profile.Id} r{profile.Revision}.");

        MainForm? form = null;
        using var server = new ApiServer(engine, Version, () => form?.BeginInvoke(() => form.BringToFront2()));
        server.Start();
        var apiPath = ApiDiscovery.DefaultPath;
        ApiDiscovery.Write(apiPath, server.Port, server.Token, Version, Environment.ProcessPath);

        engine.Start();
        form = new MainForm(engine, Log);
        try
        {
            Application.Run(form);
        }
        finally
        {
            engine.Shutdown();
            ApiDiscovery.Remove(apiPath);
            Log("AirShift closed.");
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
