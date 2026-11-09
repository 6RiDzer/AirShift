using System.Diagnostics;
using AirShift.Core.Api;
using AirShift.Core.Engine;
using AirShift.Core.Game;

namespace AirShift.App;

/// <summary>
/// A small second AirShift process that waits for the main one. If the main one stops without
/// putting the car back (a crash, End task), the session journal is still on disk: the guard
/// reads it and puts the stock height, camber and track width back, after checking the car.
/// </summary>
internal static class Guard
{
    private const string RescueMutex = @"Local\AirShift.Rescue";
    private static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(30);

    public static string JournalPath(string folder) => Path.Combine(folder, "session.json");

    /// <summary>Starts the guard through a short-lived middle process, so the guard is not in AirShift's process tree and an End task on AirShift leaves it running.</summary>
    public static void Launch(Action<string> log)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            using var middle = Process.Start(new ProcessStartInfo(exe, $"--start-guard {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            log("Guard could not start: " + ex.Message);
        }
    }

    public static int Start(int owner)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return 1;
            using var guard = Process.Start(new ProcessStartInfo(exe, $"--guard {owner}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    public static int Run(int owner)
    {
        try
        {
            using (var parent = Process.GetProcessById(owner))
            {
                parent.WaitForExit();
            }
        }
        catch (Exception)
        {
            // Already gone: look at the journal straight away.
        }

        Thread.Sleep(300);
        var folder = ApiDiscovery.DefaultFolder;
        var log = LogFile.Writer(folder);
        var store = new JournalFile(JournalPath(folder));
        var journal = store.Load();
        if (journal is null || journal.Empty || journal.AirShiftProcess != owner) return 0;

        using var rescue = new Mutex(false, RescueMutex);
        var owned = Take(rescue, TimeSpan.FromSeconds(10));
        try
        {
            log("Guard: AirShift stopped without putting the car back. Putting stock back.");
            var profile = GameProfile.Load(Path.Combine(folder, "profiles")).First(p => p.Id == "forza-horizon-6");
            var until = DateTime.UtcNow + RetryFor;
            while (true)
            {
                using var memory = Win32GameMemory.Open(profile, out _, out var message);
                if (memory is null)
                {
                    log("Guard: the game is closed, so it is stock again (" + message + ").");
                    break;
                }
                if (journal.GameProcess != 0 && memory.ProcessId != journal.GameProcess)
                {
                    log("Guard: the game was restarted since; nothing to put back.");
                    break;
                }
                var result = SessionRescue.Run(memory, profile, journal, Thread.Sleep);
                if (!result.Retry)
                {
                    log("Guard: " + result.Message);
                    break;
                }
                if (DateTime.UtcNow > until)
                {
                    log("Guard: gave up after 30 s: " + result.Message + " AirShift finds the car again next time it runs.");
                    break;
                }
                journal = result.Remaining;
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            log("Guard error: " + ex.Message);
        }
        finally
        {
            if (store.Load() is { } still && still.AirShiftProcess == owner) store.Save(null);
            if (owned) rescue.ReleaseMutex();
        }
        return 0;
    }

    /// <summary>Called by a new AirShift: waits (a few seconds at most) for a guard that is still putting a car back.</summary>
    public static void WaitForRescue()
    {
        try
        {
            using var rescue = new Mutex(false, RescueMutex);
            if (Take(rescue, TimeSpan.FromSeconds(8))) rescue.ReleaseMutex();
        }
        catch (Exception)
        {
        }
    }

    private static bool Take(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}

/// <summary>%LOCALAPPDATA%\AirShift\airshift.log, shared by the app and its guard.</summary>
internal static class LogFile
{
    public static Action<string> Writer(string folder)
    {
        var path = Path.Combine(folder, "airshift.log");
        var gate = new object();
        return line =>
        {
            lock (gate)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(20);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
        };
    }
}
