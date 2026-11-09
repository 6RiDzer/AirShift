using System.Text.Json;
using System.Text.Json.Serialization;
using AirShift.Core.Game;

namespace AirShift.Core.Engine;

/// <summary>
/// One wheel's stock curve, as AirShift read it before changing it, and how far AirShift turned
/// (degrees) and moved (metres along x) it once the change settled; null while it was still moving.
/// </summary>
public sealed record JournalCurve(int Wheel, long Table, long Start, double[] Camber, double[] TrackX, double[]? TrackXCopy)
{
    public double? CamberDelta { get; init; }

    public double? TrackDelta { get; init; }

    public static JournalCurve From(int wheel, WheelCurve curve) =>
        new(wheel, curve.Table, curve.Start, curve.Camber.ToArray(), curve.TrackX.ToArray(), curve.TrackXCopy?.ToArray());

    public WheelCurve ToCurve() => new(Table, Start, Camber.ToArray(), TrackX.ToArray(), TrackXCopy?.ToArray());
}

/// <summary>
/// What AirShift may have left changed in the game right now, and the stock values to put back:
/// the car's stock hub heights while AirShift holds them, and each wheel's stock curve while its
/// camber or track width is changed. AirShift keeps it on disk while anything is changed, so the
/// guard process can put the car back to stock if AirShift stops without doing it itself.
/// </summary>
public sealed record SessionJournal
{
    public int AirShiftProcess { get; init; }
    public int GameProcess { get; init; }
    public string? GameVersion { get; init; }
    public long Entity { get; init; }
    public long Car { get; init; }
    public int? CarId { get; init; }
    public double[] Radii { get; init; } = [];

    /// <summary>Stock hub heights, while AirShift holds the car away from them.</summary>
    public double[]? StockHeights { get; init; }

    /// <summary>The heights AirShift holds once they settle; null while the car is still moving.</summary>
    public double[]? HeldHeights { get; init; }

    /// <summary>Stock curves of the wheels whose camber or track width AirShift changed.</summary>
    public JournalCurve[] Wheels { get; init; } = [];

    [JsonIgnore]
    public bool Empty => StockHeights is null && Wheels.Length == 0;

    /// <summary>The same car and the same changes (stock values compared by content).</summary>
    public bool SameAs(SessionJournal? other)
    {
        if (other is null) return Empty;
        if (Empty && other.Empty) return true;
        if (Car != other.Car || Entity != other.Entity || CarId != other.CarId || GameProcess != other.GameProcess) return false;
        if (!Same(StockHeights, other.StockHeights) || !Same(HeldHeights, other.HeldHeights) || Wheels.Length != other.Wheels.Length) return false;
        for (var i = 0; i < Wheels.Length; i++)
        {
            var a = Wheels[i];
            var b = other.Wheels[i];
            if (a.Wheel != b.Wheel || a.Table != b.Table || a.Start != b.Start
                || a.CamberDelta != b.CamberDelta || a.TrackDelta != b.TrackDelta
                || !Same(a.Camber, b.Camber) || !Same(a.TrackX, b.TrackX) || !Same(a.TrackXCopy, b.TrackXCopy)) return false;
        }
        return true;
    }

    private static bool Same(double[]? a, double[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}

/// <summary>Where the journal lives. Saving an empty journal removes it.</summary>
public interface IJournalStore
{
    SessionJournal? Load();

    void Save(SessionJournal? journal);
}

/// <summary>%LOCALAPPDATA%\AirShift\session.json, written atomically.</summary>
public sealed class JournalFile : IJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;

    public JournalFile(string path) => _path = path;

    public SessionJournal? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<SessionJournal>(File.ReadAllText(_path), Options) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(SessionJournal? journal)
    {
        try
        {
            if (journal is null || journal.Empty)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // The journal is a safety net; failing to write it must never stop the ride.
        }
    }
}
