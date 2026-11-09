using AirShift.Core.Game;
using AirShift.Core.Memory;
using AirShift.Core.Ride;

namespace AirShift.Core.Engine;

/// <summary>What the wheels can do on the current car, and what they show.</summary>
public sealed record WheelState
{
    public bool Front { get; init; }
    public bool Rear { get; init; }
    public string? Reason { get; init; }
    public double? StockCamberFront { get; init; }
    public double? StockCamberRear { get; init; }
    public double? LiveCamberFront { get; init; }
    public double? LiveCamberRear { get; init; }
    public bool Held { get; init; }
}

/// <summary>
/// Camber and track width of the current car, per axle. AirShift reads each wheel's stock curve
/// from its axle's kinematics table when it finds the car, then writes the stock curve turned
/// and moved by the user's settings, so the camber's own change over suspension travel is kept
/// and stock comes back exactly. It re-applies when the game bakes the table again (a new tune,
/// a reset) and puts stock back on exit. Engine thread only.
/// </summary>
public sealed class WheelAlignment
{
    private const double CheckSeconds = 0.4;
    private const double WriteSeconds = 0.05;

    private readonly GameProfile _profile;
    private readonly WheelTables? _tables;
    private readonly Action<string> _log;
    private readonly WheelCurve?[] _stock = new WheelCurve?[4];
    private readonly WheelCurve?[] _written = new WheelCurve?[4];
    private readonly double?[] _stockCamber = new double?[4];
    private readonly double?[] _live = new double?[4];
    private readonly double[] _camberShift = new double[4];
    private readonly double[] _trackShift = new double[4];
    private long? _car;
    private bool _pending;
    private bool _front;
    private bool _rear;
    private string? _reason = "No car yet.";
    private bool _dirty;
    private double _nextWrite;
    private double _nextCheck;

    public WheelAlignment(IGameMemory memory, GameProfile profile, Action<string> log)
    {
        _profile = profile;
        _tables = profile.Kinematics is null ? null : new WheelTables(memory, profile);
        _log = log;
        if (_tables is null) _reason = "This game profile has no camber and track data.";
    }

    public bool Available(bool front) => front ? _front : _rear;

    /// <summary>True while the car's curves have not been read yet (the game may still hold AirShift's from before).</summary>
    public bool Pending => _pending;

    /// <summary>Changes whenever a stock curve changes, so the engine knows when to write the journal again.</summary>
    public int StockVersion { get; private set; }

    /// <summary>One bit per wheel AirShift holds away from stock.</summary>
    public int HeldMask
    {
        get
        {
            var mask = 0;
            for (var w = 0; w < 4; w++)
            {
                if (_written[w] is not null && _stock[w] is not null) mask |= 1 << w;
            }
            return mask;
        }
    }

    /// <summary>Changes on every write to the game, so the journal can say how far each wheel was moved once it settles.</summary>
    public int WriteVersion { get; private set; }

    /// <summary>Engine time of the last write to the game.</summary>
    public double LastWrite { get; private set; } = double.NegativeInfinity;

    /// <summary>
    /// The stock curve of every wheel AirShift holds away from stock, for the session journal, with
    /// how far it was turned and moved when <paramref name="settled"/> (null while it still moves).
    /// </summary>
    public JournalCurve[] HeldCurves(bool settled)
    {
        var list = new List<JournalCurve>();
        for (var w = 0; w < 4; w++)
        {
            if (_written[w] is null || _stock[w] is not { } stock) continue;
            list.Add(JournalCurve.From(w, stock) with
            {
                CamberDelta = settled ? _camberShift[w] : null,
                TrackDelta = settled ? stock.Outward * _trackShift[w] : null,
            });
        }
        return list.ToArray();
    }

    public string? Reason => _reason;

    /// <summary>The axle's own camber, in the left wheel's sign.</summary>
    public double? StockCamber(bool front) => _stockCamber[Left(front)];

    public WheelState State => new()
    {
        Front = _front,
        Rear = _rear,
        Reason = _reason,
        StockCamberFront = Round(_stockCamber[Left(true)]),
        StockCamberRear = Round(_stockCamber[Left(false)]),
        LiveCamberFront = Round(_live[Left(true)]),
        LiveCamberRear = Round(_live[Left(false)]),
        Held = _written.Any(w => w is not null),
    };

    public void Clear()
    {
        Array.Clear(_stock);
        Array.Clear(_written);
        Array.Clear(_stockCamber);
        Array.Clear(_live);
        Array.Clear(_camberShift);
        Array.Clear(_trackShift);
        _car = null;
        _pending = false;
        _front = _rear = false;
        _dirty = false;
        StockVersion++;
        if (_tables is not null) _reason = "No car yet.";
    }

    /// <summary>A new car (or the same car set up again): read its curves, and recover them if AirShift left them changed.</summary>
    public void Capture(long car, CarMemory memory)
    {
        Clear();
        if (_tables is null) return;
        _car = car;
        _pending = true;
        TryCapture(memory);
    }

    private void TryCapture(CarMemory memory)
    {
        var car = _car!.Value;
        var current = new WheelCurve[4];
        for (var w = 0; w < 4; w++)
        {
            var curve = _tables!.Read(car, w, out var why);
            var live = _tables.LiveCamber(car, w);
            if (curve is null || live is null)
            {
                var reason = $"Camber and track do not read on this car ({_profile.Wheels.Order[w]}: {why ?? "live camber unreadable"}).";
                if (reason != _reason) _log("Wheels: " + reason);
                _reason = reason;
                return;
            }
            current[w] = curve;
            _live[w] = live;
        }
        _pending = false;

        if (Recovered(current, memory))
        {
            _log("Wheels: recovered this car's stock camber and track width from last time.");
        }
        else
        {
            for (var w = 0; w < 4; w++)
            {
                _stock[w] = current[w];
                _stockCamber[w] = _live[w];
            }
            ForgetShifts(memory);
        }

        StockVersion++;
        _front = WheelTables.Independent(_stock[Index("FL")]!, _stock[Index("FR")]!);
        _rear = WheelTables.Independent(_stock[Index("RL")]!, _stock[Index("RR")]!);
        _reason = (_front, _rear) switch
        {
            (true, true) => null,
            (true, false) => "The rear is a solid axle: its camber and track stay as they are.",
            (false, true) => "The front does not read as independent: its camber and track stay as they are.",
            _ => "Camber and track do not read as independent wheels on this car.",
        };
        _dirty = true;
    }

    public void MarkDirty() => _dirty = true;

    public void Tick(double now, bool writesAllowed, CarMemory memory)
    {
        if (_tables is null || _car is null) return;
        if (now >= _nextCheck)
        {
            _nextCheck = now + CheckSeconds;
            if (_pending) TryCapture(memory);
            else Check(memory);
        }
        if (_dirty && writesAllowed && now >= _nextWrite)
        {
            _nextWrite = now + WriteSeconds;
            var before = WriteVersion;
            Apply(memory);
            if (WriteVersion != before) LastWrite = now;
        }
    }

    /// <summary>Puts every curve AirShift changed back to stock.</summary>
    public void Restore(CarMemory? memory)
    {
        if (_tables is null) return;
        for (var w = 0; w < 4; w++)
        {
            if (_written[w] is null || _stock[w] is null) continue;
            _tables.Write(_stock[w]!);
            _written[w] = null;
            _camberShift[w] = _trackShift[w] = 0;
        }
        if (memory is not null) ForgetShifts(memory);
    }

    private void Check(CarMemory memory)
    {
        for (var w = 0; w < 4; w++)
        {
            var live = _tables!.LiveCamber(_car!.Value, w);
            if (live is not null) _live[w] = live;
            if (_written[w] is not { } written) continue;
            var current = _tables.Read(_car.Value, w, out _);
            if (current is not null && current.SameAs(written)) continue;
            // The game baked this table again (a tune change, a reset): its values are the new stock.
            _log($"Wheels: the game rebuilt {_profile.Wheels.Order[w]}'s table; applying camber and track again.");
            if (current is null)
            {
                // The table moved or is being rebuilt: read the car again on the next check.
                var car = _car.Value;
                Capture(car, memory);
                return;
            }
            Rebase(w, current);
            _written[w] = null;
            _camberShift[w] = _trackShift[w] = 0;
            _dirty = true;
        }
    }

    private void Apply(CarMemory memory)
    {
        _dirty = false;
        var any = false;
        for (var w = 0; w < 4; w++)
        {
            if (_stock[w] is null) continue;
            var front = _profile.Wheels.IsFront(w);
            var on = Available(front);
            var axleCamber = front ? memory.CamberFront : memory.CamberRear;
            var camber = on && axleCamber is { } c && _stockCamber[w] is { } own
                ? (_profile.Wheels.IsLeft(w) ? c : -c) - own
                : 0;
            var track = on ? (front ? memory.TrackFront : memory.TrackRear) / 1000.0 : 0;
            var stock = Math.Abs(camber) < 0.005 && Math.Abs(track) < 0.00005;

            if (_written[w] is null)
            {
                if (stock) continue;
                // Still at stock: the game may have baked a new tune since; start from what it holds now.
                var now = _tables!.Read(_car!.Value, w, out _);
                if (now is null)
                {
                    _dirty = true;
                    continue;
                }
                if (!now.SameAs(_stock[w]!)) Rebase(w, now);
                camber = axleCamber is { } c2 && _stockCamber[w] is { } own2 ? (_profile.Wheels.IsLeft(w) ? c2 : -c2) - own2 : 0;
            }

            var target = stock ? _stock[w]! : _stock[w]!.Shifted(camber, track);
            if (_written[w] is { } written && written.SameAs(target)) continue;
            if (_written[w] is null && stock) continue;
            if (!_tables!.Write(target))
            {
                _log($"Wheels: could not write {_profile.Wheels.Order[w]}.");
                _dirty = true;
                continue;
            }
            _written[w] = stock ? null : target;
            WriteVersion++;
            _camberShift[w] = stock ? 0 : camber;
            _trackShift[w] = stock ? 0 : track;
        }

        for (var w = 0; w < 4; w++) any |= _written[w] is not null;
        if (any)
        {
            memory.WheelStockCamber = _stockCamber.Select(v => v ?? 0).ToArray();
            memory.WheelStockTrack = _stock.Select(c => c?.TrackX[0] ?? 0).ToArray();
            memory.WheelCamberShift = _camberShift.ToArray();
            memory.WheelTrackShift = _trackShift.ToArray();
        }
        else
        {
            ForgetShifts(memory);
        }
    }

    /// <summary>A new stock curve for one wheel; its own camber follows the table's average change.</summary>
    private void Rebase(int w, WheelCurve current)
    {
        if (_stock[w] is { } old && _stockCamber[w] is { } own)
            _stockCamber[w] = own + current.Camber.Average() - old.Camber.Average();
        _stock[w] = current;
        StockVersion++;
    }

    private bool Recovered(WheelCurve[] current, CarMemory memory)
    {
        if (memory.WheelStockCamber is not { Length: 4 } own || memory.WheelCamberShift is not { Length: 4 } camber
            || memory.WheelTrackShift is not { Length: 4 } track || memory.WheelStockTrack is not { Length: 4 } x0)
        {
            return false;
        }
        if (camber.All(c => Math.Abs(c) < 0.005) && track.All(t => Math.Abs(t) < 0.00005)) return false;
        // Ours only when every wheel still shows the camber AirShift gave it.
        for (var w = 0; w < 4; w++)
        {
            if (_live[w] is { } live && Math.Abs(live - (own[w] + camber[w])) > 0.25) return false;
            if (Math.Abs(current[w].TrackX[0] - (x0[w] + current[w].Outward * track[w])) > 0.0005) return false;
        }
        for (var w = 0; w < 4; w++)
        {
            _stock[w] = current[w].Shifted(-camber[w], -track[w]);
            _stockCamber[w] = own[w];
            var changed = Math.Abs(camber[w]) >= 0.005 || Math.Abs(track[w]) >= 0.00005;
            _written[w] = changed ? current[w] : null;
            _camberShift[w] = changed ? camber[w] : 0;
            _trackShift[w] = changed ? track[w] : 0;
        }
        return true;
    }

    private static void ForgetShifts(CarMemory memory)
    {
        memory.WheelStockCamber = null;
        memory.WheelStockTrack = null;
        memory.WheelCamberShift = null;
        memory.WheelTrackShift = null;
    }

    private int Index(string name) => _profile.Wheels.IndexOf(name);

    private int Left(bool front) => Index(front ? "FL" : "RL");

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;
}
