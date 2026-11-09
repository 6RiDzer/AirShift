using System.Collections.Concurrent;
using System.Diagnostics;
using AirShift.Core.Game;
using AirShift.Core.Memory;
using AirShift.Core.Ride;

namespace AirShift.Core.Engine;

/// <summary>Opens the game for one profile. Returns null with a reason when the game is not running or cannot be opened.</summary>
public delegate IGameMemory? GameOpener(GameProfile profile, out GameStatus status, out string message);

/// <summary>
/// The air ride engine. It runs on its own 60 Hz thread, finds the player's car, captures the
/// stock hub heights, moves them with the air ride model, re-applies them when the game puts
/// stock values back, guards against speed, and puts everything back on exit.
/// </summary>
public sealed class AirShiftEngine : IDisposable
{
    private const double Tolerance = 0.0015;

    /// <summary>A stance change while no air move runs reaches the car this fast, so a slider drag looks live.</summary>
    public const double StanceSeconds = 0.15;

    private readonly GameProfile _profile;
    private readonly GameOpener _opener;
    private readonly SettingsStore _store;
    private readonly IJournalStore? _journalStore;
    private readonly Dictionary<string, (string Line, double At)> _quietLog = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _stateLock = new();
    private Thread? _thread;
    private volatile bool _running;

    private IGameMemory? _memory;
    private CarLocator? _locator;
    private WheelAlignment? _wheels;
    private GameStatus _game = GameStatus.NotRunning;
    private string _message = "Waiting for Forza Horizon 6.";
    private double _nextAttach;
    private double _nextLocate;
    private double _attachedAt;
    private string? _searchNote;

    /// <summary>For this long after AirShift finds the game, a game whose data is not there yet is still loading.</summary>
    public const double LoadingSeconds = 120.0;

    private CarReading? _car;
    private string? _carKey;
    private CarMemory? _carMemory;
    private double[]? _stock;
    private double[]? _radii;
    private double[]? _current;
    private double[]? _held;
    private double[]? _foreign;
    private int _foreignTicks;
    private RideMotion? _motion;
    private bool _motionQuick;
    private double _motionStart;
    private AirLevel _level = AirLevel.Ride;
    private double _speedKmh;
    private int _reapplied;
    private double _nextSave = double.MaxValue;
    private long _revision;
    private EngineState _state = new();
    private SessionJournal? _journal;
    private (long Car, bool Heights, double[]? Stock, double[]? Held, int Mask, int Version, bool Pending, int Writes) _journalKey;
    private double _motionEnded = double.NegativeInfinity;
    private HeightPreview _preview;
    private double _previewUntil;

    /// <summary>After the last Mini or Maxi change, the car stays at that height this long before it goes back.</summary>
    public const double PreviewHoldSeconds = 2.0;

    /// <summary>A change counts as settled (and the journal says exactly where it stopped) once nothing moved for this long.</summary>
    public const double SettleSeconds = 0.5;

    /// <summary>Changes a user makes by dragging are written to the log once they stop for this long.</summary>
    public const double QuietLogSeconds = 1.0;

    public AirShiftEngine(GameProfile profile, GameOpener opener, SettingsStore store, IJournalStore? journal = null)
    {
        _profile = profile;
        _opener = opener;
        _store = store;
        _journalStore = journal;
        Settings = store.Load();
    }

    /// <summary>What AirShift may have left changed in the game right now (null when nothing).</summary>
    public SessionJournal? Journal => _journal;

    public RideSettings Settings { get; }

    public GameProfile Profile => _profile;

    public EngineState State
    {
        get
        {
            lock (_stateLock) return _state;
        }
    }

    public event Action<EngineState>? StateChanged;

    public event Action<string>? Log;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AirShift engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Tick(_clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Engine error: " + ex.Message);
                Detach("Lost the game: " + ex.Message);
            }
            Thread.Sleep(15);
        }
    }

    // ---- Commands (thread-safe, run on the engine thread) -------------------------------------

    public Task<CommandResult> SetLevelAsync(AirLevel level, string source = "app") =>
        Enqueue(() => SetLevel(level, source));

    public Task<CommandResult> ToggleAsync(string source = "app") =>
        Enqueue(() => SetLevel(_level == AirLevel.Ride ? AirLevel.Low : AirLevel.Ride, source));

    public Task<CommandResult> SetStanceAsync(double? front, double? rear, string source = "app") =>
        Enqueue(() => SetStance(front, rear, source));

    /// <summary>Camber (degrees, absolute, null leaves it) and track width (mm) per axle; <see cref="WheelChange.StockCamberFront"/> and the rear one give the car's own camber back.</summary>
    public Task<CommandResult> SetWheelsAsync(WheelChange change, string source = "app") =>
        Enqueue(() => SetWheels(change, source));

    /// <summary>The current car back to stock: height, stance, camber and track width.</summary>
    public Task<CommandResult> ResetCarAsync(string source = "app") => Enqueue(() => ResetCar(source));

    public Task<CommandResult> ChangeSettingsAsync(Action<RideSettings> change, string source = "app") =>
        Enqueue(() =>
        {
            var lowest = Settings.LowestPercent;
            var highest = Settings.HighestPercent;
            change(Settings);
            Settings.Clamp();
            ScheduleSave();
            if (_carMemory is not null)
            {
                // Maxi may have come down under the car's stance.
                _carMemory.Front = Math.Min(_carMemory.Front, Settings.StanceTop(true));
                _carMemory.Rear = Math.Min(_carMemory.Rear, Settings.StanceTop(false));
            }
            LogLater("settings", "Tune: " + DescribeSettings(Settings) + $" ({source}).");
            // A new Mini or Maxi shows on the car at once, so the user sees where it sits.
            if (Settings.LowestPercent != lowest) ShowPreview(HeightPreview.Lowest);
            else if (Settings.HighestPercent != highest) ShowPreview(HeightPreview.Highest);
            else if (_preview != HeightPreview.None) FollowPreview();
            else if (_motion is null && _held is not null) Retarget();
            Publish();
            return CommandResult.Done("Settings saved.");
        });

    /// <summary>Tune settings from the API: the same as the window's Tune tab.</summary>
    public Task<CommandResult> ChangeTuneAsync(TuneChange change, string source = "api") =>
        ChangeSettingsAsync(change.ApplyTo, source);

    public static string DescribeSettings(RideSettings s) =>
        $"Low air out front {s.LowFrontPercent:0} %, rear {s.LowRearPercent:0} %, Parked {s.ParkedDropPercent:0} %, "
        + $"Mini {s.LowestPercent:0} %, Maxi +{s.HighestPercent:0} %, dump {s.ExhaustSeconds:0.0} s, fill {s.FillSeconds:0.0} s, "
        + $"{s.Order switch { AxleOrder.FrontFirst => "front first", AxleOrder.RearFirst => "rear first", _ => "together" }}, "
        + $"settle {(s.Settle ? "on" : "off")}, speed guard {(s.SpeedGuard ? "on" : "off")}";

    public Task<CommandResult> AllowThisBuildAsync() =>
        Enqueue(() =>
        {
            var version = _memory?.FileVersion;
            if (version is null) return CommandResult.Refused("The game is not open.");
            if (!Settings.AllowedUnknownBuilds.Contains(version)) Settings.AllowedUnknownBuilds.Add(version);
            ScheduleSave();
            return CommandResult.Done("AirShift will try this game version.");
        });

    public Task<CommandResult> ProbeAsync() =>
        Enqueue(() =>
        {
            if (_memory is null) return CommandResult.Refused(_message);
            var report = new CarLocator(_memory, _profile).Probe();
            Log?.Invoke(report);
            return CommandResult.Done(report);
        });

    public Task<CommandResult> RestoreAsync() => Enqueue(() => RestoreNow("Stock heights restored."));

    private Task<CommandResult> Enqueue(Func<CommandResult> command)
    {
        var source = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try
            {
                source.TrySetResult(command());
            }
            catch (Exception ex)
            {
                source.TrySetResult(CommandResult.Refused(ex.Message));
            }
        });
        if (!_running) DrainCommands();
        return source.Task;
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command)) command();
    }

    // ---- Tick -----------------------------------------------------------------------------------

    /// <summary>One engine step. Public so tests can drive the engine with a fake clock.</summary>
    public void Tick(double now)
    {
        _now = now;
        DrainCommands();

        if (_memory is null || _memory.HasExited)
        {
            if (_memory is not null)
            {
                // The game is gone and everything AirShift changed went with it.
                ClearJournal();
                Detach("Forza Horizon 6 closed.");
            }
            FlushLog(now);
            if (now >= _nextAttach)
            {
                _nextAttach = now + 1.5;
                TryAttach();
            }
            Publish();
            return;
        }

        if (_car is null || now >= _nextLocate)
        {
            _nextLocate = now + (_car is null ? 1.0 : 0.3);
            var result = _locator!.Locate();
            if (result.Car is null)
            {
                if (_car is not null) LoseCar(result.Message);
                _message = result.Status == LocateStatus.NoContext && now - _attachedAt < LoadingSeconds
                    ? $"Waiting for {_profile.Title} to load."
                    : result.Message;
                NoteSearch(result);
                FlushLog(now);
                Publish();
                return;
            }
            if (_car is null || result.Car.Car != _car.Car || result.Car.Entity != _car.Entity
                || !SameRadii(result.Car.Radii, _radii!))
            {
                NewCar(result.Car);
            }
        }

        if (_car is null || _locator is null)
        {
            Publish();
            return;
        }

        var heights = _locator.ReadWheels(_car.Car, _profile.Wheels.HubHeight);
        var speed = _locator.ReadSpeed(_car.Car);
        if (heights is null || speed is null)
        {
            LoseCar("Lost the car.");
            Publish();
            return;
        }
        _speedKmh = Math.Abs(speed.Value) * 3.6;
        _current = heights.Select(h => (double)h).ToArray();

        if (DetectForeignChange()) return;

        Guard();
        EndPreview(now);
        Move(now);
        if (_carMemory is not null) _wheels?.Tick(now, WritesAllowed(out _), _carMemory);

        if (now >= _nextSave)
        {
            _nextSave = double.MaxValue;
            _store.Save(Settings);
        }
        UpdateJournal();
        FlushLog(now);
        Publish();
    }

    // ---- Session journal and quiet log ---------------------------------------------------------

    /// <summary>
    /// Keeps the journal on disk in step with what AirShift holds on the current car. While no car
    /// is in view the game may still hold AirShift's values, so the journal stays as it was.
    /// </summary>
    private void UpdateJournal()
    {
        if (_memory is null || _car is null || _radii is null) return;
        var heights = _stock is not null && (_held is not null || _motion is not null);
        var heightsSettled = heights && _motion is null && _now - _motionEnded >= SettleSeconds;
        var wheelsSettled = _wheels is null || _now - _wheels.LastWrite >= SettleSeconds;
        var key = (_car.Car, heights, heights ? _stock : null, heightsSettled ? _held : null, _wheels?.HeldMask ?? 0,
            _wheels?.StockVersion ?? 0, _wheels?.Pending ?? false, wheelsSettled ? _wheels?.WriteVersion ?? 0 : -1);
        if (key == _journalKey) return;
        _journalKey = key;

        var wheels = _wheels is { Pending: true } && _journal is { } old && old.Car == _car.Car && old.Entity == _car.Entity
            ? old.Wheels
            : _wheels?.HeldCurves(wheelsSettled) ?? [];
        var next = new SessionJournal
        {
            AirShiftProcess = Environment.ProcessId,
            GameProcess = _memory.ProcessId,
            GameVersion = _memory.FileVersion,
            Entity = _car.Entity,
            Car = _car.Car,
            CarId = _car.CarId,
            Radii = _radii.ToArray(),
            StockHeights = heights ? _stock!.ToArray() : null,
            HeldHeights = heightsSettled ? _held!.ToArray() : null,
            Wheels = wheels,
        };
        if (next.SameAs(_journal)) return;
        _journal = next.Empty ? null : next;
        _journalStore?.Save(_journal);
    }

    private void ClearJournal()
    {
        _journalKey = default;
        if (_journal is null) return;
        _journal = null;
        _journalStore?.Save(null);
    }

    private void LogLater(string key, string line) => _quietLog[key] = (line, _now + QuietLogSeconds);

    private void FlushLog(double now)
    {
        if (_quietLog.Count == 0) return;
        foreach (var (key, entry) in _quietLog.ToArray())
        {
            if (entry.At > now) continue;
            _quietLog.Remove(key);
            Log?.Invoke(entry.Line);
        }
    }

    private double _now;

    /// <summary>Says in the log where the car search stands each time that changes, so a car that does not show up can be explained.</summary>
    private void NoteSearch(LocateResult result)
    {
        var note = result.Status == LocateStatus.NoContext
            ? $"{result.Status}, {_locator!.ContextSource}"
            : $"{result.Status}, game data via {_locator!.ContextSource}, {result.Entities} entities, {result.Rejected} rejected";
        if (note == _searchNote) return;
        _searchNote = note;
        Log?.Invoke("Looking for the car: " + note + ".");
    }

    private void TryAttach()
    {
        var memory = _opener(_profile, out var status, out var message);
        _game = status;
        _message = message;
        if (memory is null) return;
        _memory = memory;
        _locator = new CarLocator(memory, _profile);
        _wheels = new WheelAlignment(memory, _profile, line => Log?.Invoke(line));
        _game = GameStatus.Attached;
        _message = "Looking for your car.";
        _attachedAt = _now;
        _searchNote = null;
        _nextLocate = 0;
        Log?.Invoke($"Attached to {_profile.Title} (game version {memory.FileVersion ?? "unknown"}).");
    }

    private void Detach(string message)
    {
        LoseCar(message);
        _memory?.Dispose();
        _memory = null;
        _locator = null;
        _wheels = null;
        _game = GameStatus.NotRunning;
        _message = message;
        Publish();
    }

    private void LoseCar(string message)
    {
        if (_car is not null) Log?.Invoke("Car lost: " + message);
        _car = null;
        _carKey = null;
        _carMemory = null;
        _stock = null;
        _radii = null;
        _current = null;
        _held = null;
        _motion = null;
        _foreign = null;
        _foreignTicks = 0;
        _level = AirLevel.Ride;
        _preview = HeightPreview.None;
        _wheels?.Clear();
        _message = message;
    }

    private void NewCar(CarReading reading)
    {
        var replacing = _car is not null;
        LoseCar("New car.");
        _searchNote = null;
        _car = reading;
        _radii = reading.Radii.Select(r => (double)r).ToArray();
        var heights = reading.HubHeights.Select(h => (double)h).ToArray();
        _carKey = CarKey.For(reading.CarId, reading.Radii);
        if (!Settings.Cars.TryGetValue(_carKey, out var memory))
        {
            memory = new CarMemory();
            Settings.Cars[_carKey] = memory;
        }
        _carMemory = memory;

        // If AirShift stopped while this car was lowered, its heights are still ours: keep the
        // stock we saved, so the car can go back up instead of treating "low" as the new stock.
        var recovered = memory.Stock is { Length: 4 } saved && memory.LastWritten is { Length: 4 } last
            && Near(heights, last) && !Near(heights, saved);
        _stock = recovered ? memory.Stock!.ToArray() : heights;
        memory.Stock = _stock.ToArray();
        _current = heights;
        _message = recovered ? "Car ready. Recovered its stock height from last time." : "Car ready.";
        Log?.Invoke($"{(replacing ? "Car changed" : "Car found")}: {_carKey}, stock {Format(_stock)}.");

        if (recovered || memory.HasStance)
        {
            if (WritesAllowed(out _)) Plan(AirLevel.Ride);
        }
        else
        {
            memory.LastWritten = null;
        }
        _wheels?.Capture(reading.Car, memory);
        ScheduleSave();
    }

    private bool DetectForeignChange()
    {
        // Values that are neither ours nor stock mean the game set up a different car or tune in
        // the same memory. Confirm on a second tick, then start over from the new stock.
        var expected = _held ?? _stock!;
        if (_current is null || Near(_current, expected) || Near(_current, _stock!) || _motion is not null)
        {
            _foreign = null;
            _foreignTicks = 0;
            return false;
        }
        if (_foreign is not null && Near(_current, _foreign))
        {
            if (++_foreignTicks >= 2)
            {
                Log?.Invoke("The game changed this car's setup; taking the new heights as stock.");
                if (_carMemory is not null) _carMemory.LastWritten = null;
                var locate = _locator!.Read(_car!.Entity, _car.Car, out _);
                if (locate is null)
                {
                    LoseCar("Lost the car.");
                }
                else
                {
                    if (_carMemory is not null) _carMemory.Stock = null;
                    NewCar(locate);
                }
                Publish();
                return true;
            }
        }
        else
        {
            _foreign = _current.ToArray();
            _foreignTicks = 1;
        }
        return false;
    }

    private void Guard()
    {
        if (!Settings.SpeedGuard || _level == AirLevel.Ride && _preview != HeightPreview.Lowest) return;
        if (_speedKmh > Settings.GuardUpKmh)
        {
            Log?.Invoke($"Speed guard: {_speedKmh:0} km/h, back to Ride.");
            _message = $"Above {Settings.GuardUpKmh:0} km/h: back to Ride.";
            _preview = HeightPreview.None;
            Plan(AirLevel.Ride);
        }
    }

    /// <summary>Ends a Mini or Maxi preview once it has shown long enough: the car goes back to its level like an air move.</summary>
    private void EndPreview(double now)
    {
        if (_preview == HeightPreview.None || now < _previewUntil) return;
        _preview = HeightPreview.None;
        if (_car is null || !WritesAllowed(out _)) return;
        Plan(_level);
        _message = $"Back to {Name(_level)}.";
    }

    /// <summary>
    /// Shows the car at Mini (every hub at its lowest) or Maxi (each axle at its highest stance).
    /// The first change moves it like the air ride; the next ones, while a drag goes on, follow
    /// closely. It stays there <see cref="PreviewHoldSeconds"/> after the last change.
    /// </summary>
    private void ShowPreview(HeightPreview kind)
    {
        if (_car is null || _stock is null || !WritesAllowed(out _)) return;
        if (kind == HeightPreview.Lowest && Settings.SpeedGuard && _speedKmh > Settings.GuardDownKmh)
        {
            _message = $"Mini saved. Slow down below {Settings.GuardDownKmh:0} km/h to see it on the car.";
            return;
        }
        var following = _preview == kind;
        _preview = kind;
        _previewUntil = _now + PreviewHoldSeconds;
        PlanTo(PreviewTargets(kind), quick: following && (_motion is null || _motionQuick));
        _message = kind == HeightPreview.Lowest
            ? $"Showing Mini: {Settings.LowestPercent:0} % air out."
            : $"Showing Maxi: +{Settings.StanceTop(true):0} % front, +{Settings.StanceTop(false):0} % rear.";
    }

    /// <summary>Another setting changed during a preview (Maxi coming down under the stance, say): follow the new heights.</summary>
    private void FollowPreview()
    {
        if (_car is null || _stock is null) return;
        PlanTo(PreviewTargets(_preview), quick: _motion is null || _motionQuick);
    }

    private double[] PreviewTargets(HeightPreview kind)
    {
        var result = new double[4];
        for (var w = 0; w < 4; w++)
        {
            result[w] = kind == HeightPreview.Lowest
                ? AirRideModel.Floor(_stock![w], _radii![w], Settings.LowestPercent)
                : _stock![w] + AirRideModel.Clearance(_stock[w], _radii![w]) * Settings.StanceTop(_profile.Wheels.IsFront(w)) / 100.0;
        }
        return result;
    }

    /// <summary>Where the car is going now: the preview's heights while one shows, else the level's.</summary>
    private double[] CurrentTargets() => _preview != HeightPreview.None ? PreviewTargets(_preview) : Targets(_level);

    private void Move(double now)
    {
        if (_car is null || _locator is null || _current is null) return;
        if (_motion is not null)
        {
            var elapsed = now - _motionStart;
            var values = _motion.At(elapsed);
            if (Write(values)) _held = values;
            if (_motion.Done(elapsed))
            {
                var target = CurrentTargets();
                if (!Near(values, target, 0.0005))
                {
                    // The goal moved while the car was on its way (a setting changed): carry on
                    // from here instead of jumping to it.
                    PlanTo(target, _motionQuick);
                    return;
                }
                _motion = null;
                _motionQuick = false;
                _motionEnded = now;
                _held = target;
                if (Near(_held, _stock!, 0.0002))
                {
                    // Back to stock: hand the car back to the game and stop holding.
                    Write(_stock!);
                    _held = null;
                    if (_carMemory is not null) _carMemory.LastWritten = null;
                    ScheduleSave();
                }
                else
                {
                    Write(_held);
                    if (_carMemory is not null) _carMemory.LastWritten = _held.ToArray();
                    ScheduleSave();
                }
            }
            return;
        }

        if (_held is not null && !Near(_current, _held) && Near(_current, _stock!))
        {
            // The game rebuilt the car with stock values (a reset, a rewind, a fast travel).
            if (Write(_held)) _reapplied++;
        }
    }

    private CommandResult SetLevel(AirLevel level, string source)
    {
        if (_car is null) return CommandResult.Refused(_memory is null ? "Forza Horizon 6 is not running." : "No car yet. Drive out into the world first.");
        if (!WritesAllowed(out var reason)) return CommandResult.Refused(reason!);
        if (level != AirLevel.Ride && Settings.SpeedGuard && _speedKmh > Settings.GuardDownKmh)
        {
            return CommandResult.Refused($"Slow down below {Settings.GuardDownKmh:0} km/h to air down.");
        }
        _preview = HeightPreview.None;
        Plan(level);
        Log?.Invoke($"{Name(level)} ({source}).");
        _message = level == AirLevel.Ride ? "Airing up." : "Airing out.";
        Publish();
        return CommandResult.Done(Name(level));
    }

    private CommandResult SetStance(double? front, double? rear, string source)
    {
        if (_carMemory is null) return CommandResult.Refused("No car yet. Drive out into the world first.");
        if (!WritesAllowed(out var reason)) return CommandResult.Refused(reason!);
        if (front is { } f) _carMemory.Front = Math.Min(f, Settings.StanceTop(true));
        if (rear is { } r) _carMemory.Rear = Math.Min(r, Settings.StanceTop(false));
        _carMemory.Clamp();
        ScheduleSave();
        // A drag sends many stances: follow it quickly, unless an air move is on its way.
        var previewing = _preview != HeightPreview.None;
        _preview = HeightPreview.None;
        Plan(_level, quick: !previewing && (_motion is null || _motionQuick));
        _message = $"Stance front {_carMemory.Front:+0;-0;0} %, rear {_carMemory.Rear:+0;-0;0} %.";
        LogLater("stance", $"Stance: front {_carMemory.Front:+0;-0;0} %, rear {_carMemory.Rear:+0;-0;0} % ({source}).");
        Publish();
        return CommandResult.Done(_message);
    }

    private CommandResult SetWheels(WheelChange change, string source)
    {
        if (_carMemory is null || _wheels is null) return CommandResult.Refused("No car yet. Drive out into the world first.");
        if (!WritesAllowed(out var reason)) return CommandResult.Refused(reason!);
        var front = change.CamberFront is not null || change.TrackFront is not null || change.StockCamberFront;
        var rear = change.CamberRear is not null || change.TrackRear is not null || change.StockCamberRear;
        if (front && !_wheels.Available(true) || rear && !_wheels.Available(false))
            return CommandResult.Refused(_wheels.Reason ?? "Camber and track are not ready on this car.");
        if (change.StockCamberFront) _carMemory.CamberFront = null;
        if (change.StockCamberRear) _carMemory.CamberRear = null;
        if (change.CamberFront is { } cf) _carMemory.CamberFront = NearStock(cf, _wheels.StockCamber(true));
        if (change.CamberRear is { } cr) _carMemory.CamberRear = NearStock(cr, _wheels.StockCamber(false));
        if (change.TrackFront is { } tf) _carMemory.TrackFront = tf;
        if (change.TrackRear is { } tr) _carMemory.TrackRear = tr;
        _carMemory.Clamp();
        _wheels.MarkDirty();
        ScheduleSave();
        _message = "Camber " + Degrees(_carMemory.CamberFront, _wheels.StockCamber(true)) + " / " + Degrees(_carMemory.CamberRear, _wheels.StockCamber(false))
                   + $", track {_carMemory.TrackFront:+0;-0;0} / {_carMemory.TrackRear:+0;-0;0} mm.";
        LogLater("wheels", $"Wheels: camber front {Degrees(_carMemory.CamberFront, _wheels.StockCamber(true))}, rear {Degrees(_carMemory.CamberRear, _wheels.StockCamber(false))}; "
                           + $"track front {_carMemory.TrackFront:+0;-0;0} mm, rear {_carMemory.TrackRear:+0;-0;0} mm ({source}).");
        Publish();
        return CommandResult.Done(_message);
    }

    private static double? NearStock(double value, double? stock) =>
        stock is { } s && Math.Abs(value - s) < 0.05 ? null : value;

    private static string Degrees(double? value, double? stock) =>
        value is { } v ? $"{v:+0.0;-0.0;0.0}°" : stock is { } s ? $"{s:+0.0;-0.0;0.0}° (stock)" : "stock";

    private CommandResult ResetCar(string source)
    {
        if (_carMemory is null || _car is null) return CommandResult.Refused("No car yet. Drive out into the world first.");
        if (!WritesAllowed(out var reason)) return CommandResult.Refused(reason!);
        _carMemory.Reset();
        _carMemory.Clamp();
        _wheels?.MarkDirty();
        ScheduleSave();
        _preview = HeightPreview.None;
        Plan(AirLevel.Ride);
        _message = "Back to stock: height, stance, camber and track width.";
        Log?.Invoke($"Reset ({source}).");
        Publish();
        return CommandResult.Done(_message);
    }

    private CommandResult RestoreNow(string message)
    {
        if (_car is null || _stock is null) return CommandResult.Refused("No car to restore.");
        _motion = null;
        _preview = HeightPreview.None;
        _level = AirLevel.Ride;
        Write(_stock);
        _wheels?.Restore(_carMemory);
        _held = null;
        if (_carMemory is not null) _carMemory.LastWritten = null;
        ScheduleSave();
        _message = message;
        Publish();
        return CommandResult.Done(message);
    }

    private void Retarget() => Plan(_level);

    private void Plan(AirLevel level, bool quick = false)
    {
        if (_stock is null || _radii is null || _current is null) return;
        _level = level;
        PlanTo(Targets(level), quick);
    }

    private void PlanTo(double[] to, bool quick)
    {
        if (_stock is null || _radii is null || _current is null) return;
        var from = _motion is not null ? _motion.At(_now - _motionStart) : (_held ?? _current);
        // A move that starts while another runs keeps the body's speed, so it never jolts.
        var velocity = _motion?.VelocityAt(_now - _motionStart);
        _motion = quick
            ? RideMotion.Quick(from, to, StanceSeconds, velocity)
            : RideMotion.Plan(from, to, _stock, _radii, _profile.Wheels.IsFront, Settings, velocity);
        _motionQuick = quick;
        _motionStart = _now;
    }

    private double[] Targets(AirLevel level) => AirRideModel.Targets(
        _stock!, _radii!, _profile.Wheels.IsFront, Settings, level,
        _carMemory?.Front ?? 0, _carMemory?.Rear ?? 0);

    private bool Write(IReadOnlyList<double> values)
    {
        if (_car is null || _locator is null || !WritesAllowed(out _)) return false;
        return _locator.WriteHubHeights(_car.Car, values);
    }

    private bool WritesAllowed(out string? reason)
    {
        var version = _memory?.FileVersion;
        if (_memory is null)
        {
            reason = "Forza Horizon 6 is not running.";
            return false;
        }
        if (!_profile.MatchesBuild(version) && (version is null || !Settings.AllowedUnknownBuilds.Contains(version)))
        {
            reason = $"This game version ({version ?? "unknown"}) is not in the AirShift profile yet. Run Probe, then allow it.";
            return false;
        }
        reason = null;
        return true;
    }

    private void ScheduleSave() => _nextSave = Math.Min(_nextSave, _now + 1.0);

    /// <summary>Lifts the car back to stock and stops the engine. Called when the app closes.</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _running = false;
        _thread?.Join(1000);
        DrainCommands();
        FlushLog(double.MaxValue);
        try
        {
            if (_car is not null && _stock is not null && _held is not null && _memory is { HasExited: false })
            {
                // A short fill so the car does not pop up.
                var from = _held;
                var motion = RideMotion.Plan(from, _stock, _stock, _radii!, _profile.Wheels.IsFront,
                    new RideSettings { FillSeconds = 0.8, ExhaustSeconds = 0.8, Settle = false });
                var clock = Stopwatch.StartNew();
                while (!motion.Done(clock.Elapsed.TotalSeconds) && clock.Elapsed.TotalSeconds < 1.5)
                {
                    Write(motion.At(clock.Elapsed.TotalSeconds));
                    Thread.Sleep(15);
                }
                Write(_stock);
                if (_carMemory is not null) _carMemory.LastWritten = null;
                Log?.Invoke("Stock heights restored on exit.");
            }
            _held = null;
            _motion = null;
            _preview = HeightPreview.None;
            _level = AirLevel.Ride;
            if (_memory is { HasExited: false } && _wheels?.State.Held == true)
            {
                _wheels.Restore(_carMemory);
                Log?.Invoke("Stock camber and track width restored on exit.");
            }
            FinishJournal();
        }
        finally
        {
            _store.Save(Settings);
            _memory?.Dispose();
            _memory = null;
        }
    }

    private bool _shutDown;

    /// <summary>
    /// On exit: the journal now says what is still changed in the game. With the car in view that
    /// is nothing; with the car out of view (a menu) AirShift tries the journal's own rescue, and
    /// leaves the rest on disk for the guard process to finish once the car can be read.
    /// </summary>
    private void FinishJournal()
    {
        UpdateJournal();
        if (_journal is null || _memory is null || _memory.HasExited || !WritesAllowed(out _))
        {
            ClearJournal();
            return;
        }
        var result = SessionRescue.Run(_memory, _profile, _journal, Thread.Sleep);
        Log?.Invoke("On exit: " + result.Message);
        if (result.Retry)
        {
            _journal = result.Remaining;
            _journalStore?.Save(_journal);
        }
        else
        {
            ClearJournal();
        }
    }

    public void Dispose() => Shutdown();

    // ---- State ---------------------------------------------------------------------------------

    private void Publish()
    {
        var allowed = WritesAllowed(out var reason);
        var state = new EngineState
        {
            Game = _memory is not null ? GameStatus.Attached : _game,
            GameTitle = _profile.Title,
            FileVersion = _memory?.FileVersion,
            BuildKnown = _profile.MatchesBuild(_memory?.FileVersion),
            WritesAllowed = allowed,
            WritesBlockedReason = reason,
            CarReady = _car is not null,
            CarKey = _carKey,
            CarId = _car?.CarId,
            SpeedKmh = Math.Round(_speedKmh, 1),
            Level = _level,
            Moving = _motion is not null,
            Progress = _motion is null ? 1 : Math.Round(_motion.Progress(_now - _motionStart), 3),
            FrontStance = _carMemory?.Front ?? 0,
            RearStance = _carMemory?.Rear ?? 0,
            Tune = TuneState.From(Settings),
            FrontStanceTop = Settings.StanceTop(true),
            RearStanceTop = Settings.StanceTop(false),
            Preview = _preview,
            Axles = AxleRoom(),
            CamberFront = _carMemory?.CamberFront,
            CamberRear = _carMemory?.CamberRear,
            TrackFront = _carMemory?.TrackFront ?? 0,
            TrackRear = _carMemory?.TrackRear ?? 0,
            Wheels = _car is null ? new WheelState { Reason = _wheels?.Reason ?? "No car yet." } : _wheels?.State ?? new WheelState { Reason = "Forza Horizon 6 is not running." },
            FrontHeightPercent = AxlePercent(front: true),
            RearHeightPercent = AxlePercent(front: false),
            GuardOn = Settings.SpeedGuard,
            GuardDownKmh = Settings.GuardDownKmh,
            GuardUpKmh = Settings.GuardUpKmh,
            Message = _message,
            Reapplied = _reapplied,
        };

        EngineState previous;
        lock (_stateLock)
        {
            previous = _state;
            if (Equivalent(previous, state)) return;
            state = state with { Revision = ++_revision };
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    private static bool Equivalent(EngineState a, EngineState b) => a with { Revision = 0 } == b with { Revision = 0 };

    /// <summary>How far each axle can go, in millimetres, so the window can show Mini and Maxi as heights.</summary>
    private AxleRoom? AxleRoom()
    {
        if (_stock is null || _radii is null) return null;
        double Average(bool front, Func<int, double> value)
        {
            var wheels = Enumerable.Range(0, 4).Where(w => _profile.Wheels.IsFront(w) == front).ToArray();
            return wheels.Length == 0 ? 0 : Math.Round(wheels.Average(value) * 1000, 1);
        }
        return new AxleRoom(
            Average(true, w => AirRideModel.Clearance(_stock[w], _radii[w])),
            Average(false, w => AirRideModel.Clearance(_stock[w], _radii[w])),
            Average(true, w => _stock[w] - _radii[w] * RideSettings.HubOverRadiusMin),
            Average(false, w => _stock[w] - _radii[w] * RideSettings.HubOverRadiusMin));
    }

    private double AxlePercent(bool front)
    {
        if (_stock is null || _radii is null || _current is null) return 0;
        double sum = 0;
        var n = 0;
        for (var w = 0; w < 4; w++)
        {
            if (_profile.Wheels.IsFront(w) != front) continue;
            var clearance = Math.Max(0.001, AirRideModel.Clearance(_stock[w], _radii[w]));
            sum += (_current[w] - _stock[w]) / clearance * 100;
            n++;
        }
        return n == 0 ? 0 : Math.Round(sum / n, 0);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static bool Near(IReadOnlyList<double> a, IReadOnlyList<double> b, double tolerance = Tolerance)
    {
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(a[i] - b[i]) > tolerance) return false;
        }
        return true;
    }

    private static bool SameRadii(IReadOnlyList<float> a, IReadOnlyList<double> b)
    {
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 0.0005) return false;
        }
        return true;
    }

    private static string Format(IEnumerable<double> values) => string.Join(" ", values.Select(v => v.ToString("0.000")));

    public static string Name(AirLevel level) => level switch
    {
        AirLevel.Low => "Low",
        AirLevel.Parked => "Parked",
        _ => "Ride",
    };
}
