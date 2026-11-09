using AirShift.Core.Game;
using AirShift.Core.Memory;
using AirShift.Core.Ride;

namespace AirShift.Core.Engine;

/// <summary>
/// Puts a car back to the stock values a <see cref="SessionJournal"/> kept, when AirShift could not
/// do it itself (it crashed, was killed, or closed while the car was out of view). It writes only
/// after checking that the same car is still there and that what it holds still looks like what
/// AirShift wrote; anything the game has rebuilt since is left alone.
/// </summary>
public static class SessionRescue
{
    private const double HeightTolerance = 0.0015;
    private const double RampSeconds = 0.6;

    /// <summary><see cref="Retry"/> is true when part of the car could not be read yet (a menu, a loading screen); <see cref="Remaining"/> is what is left to put back.</summary>
    public sealed record Result(bool Retry, SessionJournal Remaining, string Message);

    public static Result Run(IGameMemory memory, GameProfile profile, SessionJournal journal, Action<int> sleep)
    {
        if (journal.Empty) return new(false, journal, "Nothing to put back.");
        var locator = new CarLocator(memory, profile);
        var reading = locator.Read(journal.Entity, journal.Car, out var why);
        if (reading is null) return new(true, journal, $"The car does not read right now ({why}).");
        if (journal.CarId is { } id && reading.CarId is { } now && id != now
            || !SameRadii(reading.Radii, journal.Radii))
        {
            return new(false, journal with { StockHeights = null, Wheels = [] }, "Another car is there now; nothing written.");
        }

        var notes = new List<string>();
        if (journal.StockHeights is { Length: 4 } stock)
        {
            var current = reading.HubHeights.Select(h => (double)h).ToArray();
            if (Near(current, stock))
            {
                notes.Add("heights already stock");
            }
            else if (journal.HeldHeights is { Length: 4 } held ? !Near(current, held) : !Ours(current, stock, journal.Radii))
            {
                notes.Add("heights changed by the game, left alone");
            }
            else
            {
                Ramp(locator, journal.Car, current, stock, sleep);
                notes.Add("stock heights put back");
            }
        }

        var left = new List<JournalCurve>();
        if (journal.Wheels.Length > 0)
        {
            var tables = profile.Kinematics is null ? null : new WheelTables(memory, profile);
            var put = 0;
            foreach (var saved in journal.Wheels)
            {
                var current = tables?.Read(journal.Car, saved.Wheel, out _);
                if (tables is null || current is null)
                {
                    left.Add(saved);
                    continue;
                }
                var stockCurve = saved.ToCurve();
                if (current.Table != saved.Table || current.Start != saved.Start) continue;
                if (current.SameAs(stockCurve, 0.005, 0.00005)) continue;
                if (!Shifted(current, stockCurve, saved.CamberDelta, saved.TrackDelta)) continue;
                if (tables.Write(stockCurve)) put++;
                else left.Add(saved);
            }
            notes.Add(put > 0 ? $"stock camber and track put back on {put} wheel{(put == 1 ? "" : "s")}" : "camber and track already stock");
        }

        var remaining = journal with { StockHeights = null, Wheels = left.ToArray() };
        return new(!remaining.Empty, remaining, string.Join(", ", notes) + ".");
    }

    /// <summary>True when every height is somewhere AirShift could have put it: between Mini at its lowest and the highest stance.</summary>
    private static bool Ours(double[] current, double[] stock, double[] radii)
    {
        for (var w = 0; w < 4; w++)
        {
            var clearance = AirRideModel.Clearance(stock[w], radii[w]);
            var low = AirRideModel.Floor(stock[w], radii[w], RideSettings.LowestMax) - 0.002;
            var high = stock[w] + clearance * RideSettings.FrontStanceMax / 100.0 + 0.002;
            if (current[w] < low || current[w] > high) return false;
        }
        return true;
    }

    /// <summary>
    /// A curve AirShift turned and moved: the same change on every sample and, when the journal
    /// knows where the change settled, exactly that change. A table the game baked again (a new
    /// tune) moves by something else and is left alone.
    /// </summary>
    private static bool Shifted(WheelCurve current, WheelCurve stock, double? camberDelta, double? trackDelta)
    {
        if (current.Camber.Length != stock.Camber.Length) return false;
        var camber = current.Camber.Zip(stock.Camber, (a, b) => a - b).ToArray();
        var track = current.TrackX.Zip(stock.TrackX, (a, b) => a - b).ToArray();
        if (camber.Max() - camber.Min() > 0.05 || track.Max() - track.Min() > 0.0005) return false;
        if (camberDelta is { } c && Math.Abs(camber.Average() - c) > 0.05) return false;
        if (trackDelta is { } t && Math.Abs(track.Average() - t) > 0.0005) return false;
        return Math.Abs(track.Average()) <= Math.Max(-RideSettings.TrackMin, RideSettings.TrackMax) / 1000.0 + 0.0005;
    }

    private static void Ramp(CarLocator locator, long car, double[] from, double[] to, Action<int> sleep)
    {
        const int steps = 40;
        var values = new double[4];
        for (var i = 1; i < steps; i++)
        {
            var u = i / (double)steps;
            var p = u * u * (3 - 2 * u);
            for (var w = 0; w < 4; w++) values[w] = from[w] + (to[w] - from[w]) * p;
            locator.WriteHubHeights(car, values);
            sleep((int)(RampSeconds * 1000 / steps));
        }
        locator.WriteHubHeights(car, to);
    }

    private static bool Near(double[] a, double[] b)
    {
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(a[i] - b[i]) > HeightTolerance) return false;
        }
        return true;
    }

    private static bool SameRadii(float[] a, double[] b)
    {
        if (b.Length != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 0.0005) return false;
        }
        return true;
    }
}
