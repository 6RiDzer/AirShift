using System.Diagnostics;
using System.Runtime.InteropServices;
using AirShift.Core.Engine;
using AirShift.Core.Ride;

namespace AirShift.App;

internal sealed class MainForm : Form
{
    private const int HotkeyToggle = 1;
    private const int HotkeyParked = 2;
    private const int WmHotkey = 0x0312;
    private const int PageHeight = 592;

    private readonly AirShiftEngine _engine;
    private readonly Action<string> _log;
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 50 };
    private readonly Controller _controller = new();
    private readonly List<string> _lines = new();

    private readonly Label _status = new();
    private readonly Panel _statusDot = new();
    private readonly FlatButton _tabRide = new() { Text = "Ride" };
    private readonly FlatButton _tabTune = new() { Text = "Tune" };
    private readonly FlatButton _tabWheels = new() { Text = "Wheels" };
    private readonly Panel _ridePage = new();
    private readonly Panel _tunePage = new();
    private readonly Panel _wheelsPage = new();
    private readonly RideView _view = new();
    private readonly FlatButton _air = new() { Primary = true, Height = 60 };
    private readonly FlatButton _ride = new() { Text = "Ride" };
    private readonly FlatButton _low = new() { Text = "Low" };
    private readonly FlatButton _parked = new() { Text = "Parked" };
    private readonly FlatButton _reset = new() { Text = "Reset" };
    private readonly Label _message = new();
    private readonly Slider _front = new() { Label = "Front stance", Minimum = RideSettings.StanceMin, Maximum = RideSettings.FrontStanceMax, Mark = 0 };
    private readonly Slider _rear = new() { Label = "Rear stance", Minimum = RideSettings.StanceMin, Maximum = RideSettings.RearStanceMax, Mark = 0 };

    private readonly Label _wheelsStatus = new();
    private readonly Slider _camberFront = new() { Label = "Front camber", Minimum = RideSettings.CamberMin, Maximum = RideSettings.CamberMax, Step = 0.1 };
    private readonly Slider _camberRear = new() { Label = "Rear camber", Minimum = RideSettings.CamberMin, Maximum = RideSettings.CamberMax, Step = 0.1 };
    private readonly Slider _trackFront = new() { Label = "Front track width", Minimum = RideSettings.TrackMin, Maximum = RideSettings.TrackMax, Mark = 0 };
    private readonly Slider _trackRear = new() { Label = "Rear track width", Minimum = RideSettings.TrackMin, Maximum = RideSettings.TrackMax, Mark = 0 };
    private readonly Label _wheelsLive = new();
    private readonly FlatButton _wheelsStock = new() { Text = "Stock camber and track", Height = 34 };
    private readonly Dictionary<Slider, DateTime> _sent = new();
    private readonly FlatButton _allow = new() { Text = "Try AirShift on this game version", Visible = false };
    private readonly FlatButton _admin = new() { Text = "Restart as administrator", Visible = false };

    private readonly Slider _lowFront = new() { Label = "Low: air out front", Minimum = 0, Maximum = RideSettings.DropMax, Mark = 100, ValueWidth = 150 };
    private readonly Slider _lowRear = new() { Label = "Low: air out rear", Minimum = 0, Maximum = RideSettings.DropMax, Mark = 100, ValueWidth = 150 };
    private readonly Slider _parkedDrop = new() { Label = "Parked: air out", Minimum = 0, Maximum = RideSettings.DropMax, Mark = 100, ValueWidth = 150 };
    private readonly Slider _lowest = new() { Label = "Mini: lowest the car goes (air out)", Minimum = RideSettings.LowestMin, Maximum = RideSettings.LowestMax, Mark = 100 };
    private readonly Slider _highest = new() { Label = "Maxi: highest stance (front / rear)", Minimum = 0, Maximum = RideSettings.FrontStanceMax, ValueWidth = 130 };
    private readonly Label _stanceHint = new();
    private readonly Slider _exhaust = new() { Label = "Dump time", Minimum = RideSettings.DumpMin, Maximum = RideSettings.DumpMax, Step = 0.1 };
    private readonly Slider _fill = new() { Label = "Fill time", Minimum = RideSettings.FillMin, Maximum = RideSettings.FillMax, Step = 0.1 };
    private readonly FlatButton _together = new() { Text = "Together", Height = 32 };
    private readonly FlatButton _frontFirst = new() { Text = "Front first", Height = 32 };
    private readonly FlatButton _rearFirst = new() { Text = "Rear first", Height = 32 };
    private readonly CheckBox _settle = new() { Text = "Settle at the end of a drop" };
    private readonly CheckBox _guard = new();
    private readonly CheckBox _pad = new() { Text = "Controller: hold View, D-pad up / down / left = Ride / Low / Parked" };
    private readonly FlatButton _probe = new() { Text = "Probe (read only)", Height = 34 };
    private readonly FlatButton _copy = new() { Text = "Copy report", Height = 34 };
    private readonly Label _log2 = new();

    private EngineState? _shown;
    private TuneState? _shownTune;
    private string _report = "";
    private bool _hotkeysOk;
    private DateTime _pinnedUntil;

    public MainForm(AirShiftEngine engine, Action<string> log)
    {
        _engine = engine;
        _log = log;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "AirShift";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 108 + PageHeight);
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AirShift.Icon.ico"))
        {
            if (stream is not null) Icon = new Icon(stream);
        }

        BuildHeader();
        BuildRidePage();
        BuildTunePage();
        BuildWheelsPage();
        ShowPage(_ridePage);
        LoadSettingsIntoControls();
        ResumeLayout(false);

        // The engine's lines already go to the log file; here they only show.
        _engine.Log += line => BeginInvokeSafe(() => AddLine(line, write: false));
        _ui.Tick += (_, _) => Refresh(_engine.State);
        _ui.Start();
    }

    // ---- Layout --------------------------------------------------------------------------------

    private void BuildHeader()
    {
        var logo = new PictureBox { Location = new Point(20, 16), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Back };
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AirShift.Logo.png"))
        {
            if (stream is not null) logo.Image = Image.FromStream(stream);
        }
        var title = new Label { Text = "AirShift", Font = Theme.Ui(15f, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(64, 12), AutoSize = true };
        var by = new Label { Text = "by 6RiDzer", Font = Theme.Ui(8.5f), ForeColor = Theme.Muted, Location = new Point(66, 38), AutoSize = true };
        _statusDot.Size = new Size(8, 8);
        _statusDot.Location = new Point(250, 30);
        _status.Location = new Point(264, 22);
        _status.Size = new Size(200, 40);
        _status.ForeColor = Theme.Muted;
        _status.Font = Theme.Ui(8.5f);
        _tabRide.Location = new Point(20, 68);
        _tabRide.Size = new Size(70, 30);
        _tabTune.Location = new Point(96, 68);
        _tabTune.Size = new Size(70, 30);
        _tabWheels.Location = new Point(172, 68);
        _tabWheels.Size = new Size(80, 30);
        _tabRide.Click += (_, _) => ShowPage(_ridePage);
        _tabTune.Click += (_, _) => ShowPage(_tunePage);
        _tabWheels.Click += (_, _) => ShowPage(_wheelsPage);
        Controls.AddRange([logo, title, by, _statusDot, _status, _tabRide, _tabTune, _tabWheels]);

        foreach (var page in new[] { _ridePage, _tunePage, _wheelsPage })
        {
            page.Location = new Point(0, 108);
            page.Size = new Size(480, PageHeight);
            page.BackColor = Theme.Back;
            Controls.Add(page);
        }
    }

    private void BuildRidePage()
    {
        _view.Location = new Point(20, 0);
        _view.Size = new Size(440, 150);
        _air.Location = new Point(20, 162);
        _air.Size = new Size(440, 60);
        _air.Click += async (_, _) => Show(await _engine.ToggleAsync());

        const int resetWidth = 86;
        var w = (440 - resetWidth - 24) / 3;
        _ride.Location = new Point(20, 232);
        _low.Location = new Point(20 + w + 8, 232);
        _parked.Location = new Point(20 + 2 * (w + 8), 232);
        foreach (var b in new[] { _ride, _low, _parked }) b.Size = new Size(w, 40);
        _reset.Location = new Point(460 - resetWidth, 232);
        _reset.Size = new Size(resetWidth, 40);
        _ride.Click += async (_, _) => Show(await _engine.SetLevelAsync(AirLevel.Ride));
        _low.Click += async (_, _) => Show(await _engine.SetLevelAsync(AirLevel.Low));
        _parked.Click += async (_, _) => Show(await _engine.SetLevelAsync(AirLevel.Parked));
        _reset.Click += async (_, _) => Show(await _engine.ResetCarAsync());

        _message.Location = new Point(20, 280);
        _message.Size = new Size(440, 40);
        _message.ForeColor = Theme.Muted;

        _front.Location = new Point(20, 322);
        _front.Size = new Size(440, 44);
        _rear.Location = new Point(20, 368);
        _rear.Size = new Size(440, 44);
        _front.Format = _rear.Format = v => Math.Abs(v) < 0.5 ? "stock" : $"{v:+0;-0} %";
        // The car follows the slider while it moves; the release always sends the last value.
        Live(_front, v => _engine.SetStanceAsync(v, null));
        Live(_rear, v => _engine.SetStanceAsync(null, v));

        _stanceHint.Location = new Point(24, 414);
        _stanceHint.Size = new Size(436, 18);
        _stanceHint.ForeColor = Theme.Muted;
        _stanceHint.Font = Theme.Ui(8f);

        _allow.Location = new Point(20, 440);
        _allow.Size = new Size(440, 36);
        _allow.Click += async (_, _) => Show(await _engine.AllowThisBuildAsync());
        _admin.Location = new Point(20, 440);
        _admin.Size = new Size(440, 36);
        _admin.Click += (_, _) => RestartElevated();

        var keys = new Label
        {
            Text = "F9  Air up / down     Shift+F9  Parked",
            Location = new Point(24, PageHeight - 30),
            Size = new Size(436, 18),
            ForeColor = Theme.Muted,
            Font = Theme.Ui(8f),
        };
        _ridePage.Controls.AddRange([_view, _air, _ride, _low, _parked, _reset, _message, _front, _rear, _stanceHint, _allow, _admin, keys]);
    }

    private void BuildWheelsPage()
    {
        var title = new Label { Text = "Camber and track width", Font = Theme.Ui(11f, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(22, 0), AutoSize = true };
        _wheelsStatus.Location = new Point(22, 26);
        _wheelsStatus.Size = new Size(438, 36);
        _wheelsStatus.ForeColor = Theme.Muted;
        _wheelsStatus.Font = Theme.Ui(8.5f);

        var y = 70;
        foreach (var slider in new[] { _camberFront, _camberRear, _trackFront, _trackRear })
        {
            slider.Location = new Point(20, y);
            slider.Size = new Size(440, 44);
            y += slider == _camberRear ? 58 : 48;
        }
        _camberFront.Format = v => CamberText(v, _shown?.Wheels.StockCamberFront);
        _camberRear.Format = v => CamberText(v, _shown?.Wheels.StockCamberRear);
        _trackFront.Format = _trackRear.Format = v => Math.Abs(v) < 0.5 ? "stock" : $"{v:+0;-0} mm";
        Live(_camberFront, v => _engine.SetWheelsAsync(new WheelChange(CamberFront: v)));
        Live(_camberRear, v => _engine.SetWheelsAsync(new WheelChange(CamberRear: v)));
        Live(_trackFront, v => _engine.SetWheelsAsync(new WheelChange(TrackFront: v)));
        Live(_trackRear, v => _engine.SetWheelsAsync(new WheelChange(TrackRear: v)));

        _wheelsLive.Location = new Point(22, y);
        _wheelsLive.Size = new Size(438, 18);
        _wheelsLive.ForeColor = Theme.Muted;
        _wheelsLive.Font = Theme.Ui(8.5f);
        y += 28;
        _wheelsStock.Location = new Point(20, y);
        _wheelsStock.Size = new Size(440, 34);
        _wheelsStock.Click += async (_, _) => Show(await _engine.SetWheelsAsync(
            new WheelChange(TrackFront: 0, TrackRear: 0, StockCamberFront: true, StockCamberRear: true)));
        y += 46;
        var hint = new Label
        {
            Text = "Negative camber tilts the tops of the wheels in. Track width moves both wheels of an axle out (+) or in (-). "
                   + "Saved for this car, put back to stock when AirShift closes. Run Probe (Tune) first on a new game version.",
            Location = new Point(22, y),
            Size = new Size(438, 54),
            ForeColor = Theme.Muted,
            Font = Theme.Ui(8f),
        };
        _wheelsPage.Controls.AddRange([title, _wheelsStatus, _camberFront, _camberRear, _trackFront, _trackRear, _wheelsLive, _wheelsStock, hint]);
    }

    private static string CamberText(double value, double? stock) =>
        stock is { } s && Math.Abs(value - s) < 0.05 ? $"{value:0.0}° stock" : $"{value:+0.0;-0.0;0.0}°";

    /// <summary>Sends a slider's value while it drags (at most every 60 ms) and always on release.</summary>
    private void Live(Slider slider, Func<double, Task<CommandResult>> send)
    {
        slider.Changed += v =>
        {
            var now = DateTime.UtcNow;
            if (_sent.TryGetValue(slider, out var last) && (now - last).TotalMilliseconds < 60) return;
            _sent[slider] = now;
            _ = send(v);
        };
        slider.Committed += async v =>
        {
            _sent.Remove(slider);
            Show(await send(v), quiet: true);
        };
    }

    private void BuildTunePage()
    {
        var y = 0;
        Slider Add(Slider s, Func<double, string> format, Action<RideSettings, double> apply)
        {
            s.Location = new Point(20, y);
            s.Size = new Size(440, 44);
            s.Format = format;
            s.Committed += async v =>
            {
                Show(await _engine.ChangeSettingsAsync(st => apply(st, v)), quiet: true);
                LoadSettingsIntoControls();
            };
            _tunePage.Controls.Add(s);
            y += 46;
            return s;
        }

        Add(_lowFront, AirOut, (s, v) => s.LowFrontPercent = v);
        Add(_lowRear, AirOut, (s, v) => s.LowRearPercent = v);
        Add(_parkedDrop, AirOut, (s, v) => s.ParkedDropPercent = v);
        Add(_lowest, v => $"{v:0} %", (s, v) => s.LowestPercent = v);
        Add(_highest, v => $"+{Math.Min(v, RideSettings.FrontStanceMax):0} / +{Math.Min(v, RideSettings.RearStanceMax):0} %", (s, v) => s.HighestPercent = v);
        Add(_exhaust, v => $"{v:0.0} s", (s, v) => s.ExhaustSeconds = v);
        Add(_fill, v => $"{v:0.0} s", (s, v) => s.FillSeconds = v);

        var orderLabel = new Label { Text = "Axle order", Location = new Point(24, y + 6), AutoSize = true, ForeColor = Theme.Text };
        var bw = 100;
        _together.Location = new Point(460 - 3 * bw - 16, y);
        _frontFirst.Location = new Point(460 - 2 * bw - 8, y);
        _rearFirst.Location = new Point(460 - bw, y);
        foreach (var b in new[] { _together, _frontFirst, _rearFirst }) b.Size = new Size(bw, 32);
        _together.Click += async (_, _) => { await _engine.ChangeSettingsAsync(s => s.Order = AxleOrder.Together); LoadSettingsIntoControls(); };
        _frontFirst.Click += async (_, _) => { await _engine.ChangeSettingsAsync(s => s.Order = AxleOrder.FrontFirst); LoadSettingsIntoControls(); };
        _rearFirst.Click += async (_, _) => { await _engine.ChangeSettingsAsync(s => s.Order = AxleOrder.RearFirst); LoadSettingsIntoControls(); };
        _tunePage.Controls.AddRange([orderLabel, _together, _frontFirst, _rearFirst]);
        y += 42;

        foreach (var box in new[] { _settle, _guard, _pad })
        {
            box.Location = new Point(22, y);
            box.Size = new Size(440, 24);
            box.ForeColor = Theme.Text;
            box.FlatStyle = FlatStyle.Flat;
            box.FlatAppearance.BorderColor = Theme.Line;
            box.FlatAppearance.CheckedBackColor = Theme.Accent;
            _tunePage.Controls.Add(box);
            y += 28;
        }
        // Loading values set from elsewhere (the API) must not send them back as a change.
        _settle.CheckedChanged += async (_, _) => { if (!_loading) await _engine.ChangeSettingsAsync(s => s.Settle = _settle.Checked); };
        _guard.CheckedChanged += async (_, _) => { if (!_loading) await _engine.ChangeSettingsAsync(s => s.SpeedGuard = _guard.Checked); };
        _pad.CheckedChanged += async (_, _) => { if (!_loading) await _engine.ChangeSettingsAsync(s => s.ControllerEnabled = _pad.Checked); };

        y += 6;
        _probe.Location = new Point(20, y);
        _probe.Size = new Size(216, 34);
        _copy.Location = new Point(244, y);
        _copy.Size = new Size(216, 34);
        _probe.Click += async (_, _) =>
        {
            var result = await _engine.ProbeAsync();
            _report = result.Message;
            _log2.Text = result.Message;
        };
        _copy.Click += (_, _) =>
        {
            var text = _report.Length > 0 ? _report : string.Join(Environment.NewLine, _lines);
            if (text.Length > 0) Clipboard.SetText(text);
        };
        y += 42;
        _log2.Location = new Point(20, y);
        _log2.Size = new Size(440, PageHeight - y - 24);
        _log2.ForeColor = Theme.Muted;
        _log2.Font = new Font("Consolas", 8f);
        _log2.BackColor = Theme.Panel;
        _log2.Padding = new Padding(6);

        var copyright = new Label
        {
            Text = $"AirShift {Program.Version} · © 2026 6RiDzer. All rights reserved.",
            Location = new Point(20, PageHeight - 20),
            Size = new Size(440, 18),
            ForeColor = Theme.Muted,
            Font = Theme.Ui(7.5f),
        };
        _tunePage.Controls.AddRange([_probe, _copy, _log2, copyright]);
    }

    /// <summary>An air out value, and where Mini stops it when it asks for more.</summary>
    private string AirOut(double value)
    {
        var lowest = _engine.Settings.LowestPercent;
        return value > lowest + 0.5 ? $"{value:0} % (Mini stops at {lowest:0})" : $"{value:0} %";
    }

    private void ShowPage(Panel page)
    {
        _ridePage.Visible = page == _ridePage;
        _tunePage.Visible = page == _tunePage;
        _wheelsPage.Visible = page == _wheelsPage;
        _tabRide.Selected = page == _ridePage;
        _tabTune.Selected = page == _tunePage;
        _tabWheels.Selected = page == _wheelsPage;
    }

    private void LoadSettingsIntoControls()
    {
        var s = _engine.Settings;
        _lowFront.Value = s.LowFrontPercent;
        _lowRear.Value = s.LowRearPercent;
        _parkedDrop.Value = s.ParkedDropPercent;
        _lowest.Value = s.LowestPercent;
        _highest.Value = s.HighestPercent;
        foreach (var slider in new[] { _lowFront, _lowRear, _parkedDrop }) slider.Invalidate();
        _front.Maximum = s.StanceTop(true);
        _rear.Maximum = s.StanceTop(false);
        _front.Invalidate();
        _rear.Invalidate();
        _stanceHint.Text = $"Stance is saved for this car: -50 % to +{s.StanceTop(true):0} % front, +{s.StanceTop(false):0} % rear (Maxi, in Tune).";
        _exhaust.Value = s.ExhaustSeconds;
        _fill.Value = s.FillSeconds;
        _together.Selected = s.Order == AxleOrder.Together;
        _frontFirst.Selected = s.Order == AxleOrder.FrontFirst;
        _rearFirst.Selected = s.Order == AxleOrder.RearFirst;
        _loading = true;
        _settle.Checked = s.Settle;
        _guard.Checked = s.SpeedGuard;
        _guard.Text = $"Speed guard: air down below {s.GuardDownKmh:0} km/h, back up above {s.GuardUpKmh:0} km/h";
        _pad.Checked = s.ControllerEnabled;
        _loading = false;
    }

    private bool _loading;

    // ---- State -> UI -----------------------------------------------------------------------------

    private void Refresh(EngineState s)
    {
        if (_engine.Settings.ControllerEnabled) PollController();
        if (DateTime.UtcNow > _pinnedUntil)
        {
            var text = s.WritesBlockedReason is not null && s.CarReady ? s.WritesBlockedReason : s.Message;
            if (_message.Text != text) _message.Text = text;
            _message.ForeColor = Theme.Muted;
        }
        if (ReferenceEquals(s, _shown)) return;
        _shown = s;
        if (_shownTune is not null && s.Tune != _shownTune) LoadSettingsIntoControls(); // changed through the API
        _shownTune = s.Tune;

        string status;
        Color dot;
        if (s.Game == GameStatus.NotRunning) { status = "Waiting for " + s.GameTitle; dot = Theme.Muted; }
        else if (s.Game == GameStatus.CannotOpen) { status = "Cannot open the game"; dot = Theme.Bad; }
        else if (!s.CarReady) { status = s.GameTitle + " · looking for your car"; dot = Theme.Warn; }
        else if (!s.WritesAllowed) { status = "Car found · this game version is not in the profile"; dot = Theme.Warn; }
        else { status = $"Car ready · {s.SpeedKmh:0} km/h"; dot = Theme.Accent; }
        _status.Text = status;
        _statusDot.BackColor = dot;

        var ready = s.CarReady && s.WritesAllowed;
        _air.Enabled = _ride.Enabled = _low.Enabled = _parked.Enabled = _reset.Enabled = _front.Enabled = _rear.Enabled = ready;
        RefreshWheels(s, ready);
        _air.Text = s.Level == AirLevel.Ride ? "Air down" : "Air up";
        _air.Hint = s.Moving ? (s.Level == AirLevel.Ride ? "Filling…" : "Dumping…") : "F9";
        _ride.Selected = s.Level == AirLevel.Ride;
        _low.Selected = s.Level == AirLevel.Low;
        _parked.Selected = s.Level == AirLevel.Parked;
        _front.Value = s.FrontStance;
        _rear.Value = s.RearStance;
        _allow.Visible = s.CarReady && !s.WritesAllowed && !s.BuildKnown && s.FileVersion is not null;
        _admin.Visible = s.Game == GameStatus.CannotOpen && !Win32GameMemory.IsElevated();
        var caption = !s.CarReady ? "No car" : s.Moving ? (s.Level == AirLevel.Ride ? "Filling" : "Dumping") : EngineName(s.Level);
        _view.Show(s.FrontHeightPercent, s.RearHeightPercent, s.CarReady && s.Level != AirLevel.Ride, caption);
    }

    private void RefreshWheels(EngineState s, bool ready)
    {
        var w = s.Wheels;
        _camberFront.Enabled = _trackFront.Enabled = ready && w.Front;
        _camberRear.Enabled = _trackRear.Enabled = ready && w.Rear;
        _wheelsStock.Enabled = ready && (w.Front || w.Rear);
        _camberFront.Mark = w.StockCamberFront;
        _camberRear.Mark = w.StockCamberRear;
        _camberFront.Value = s.CamberFront ?? w.StockCamberFront ?? 0;
        _camberRear.Value = s.CamberRear ?? w.StockCamberRear ?? 0;
        _trackFront.Value = s.TrackFront;
        _trackRear.Value = s.TrackRear;
        _wheelsStatus.Text = !s.CarReady ? "Get in a car first."
            : !s.WritesAllowed ? s.WritesBlockedReason ?? "AirShift is read-only on this game version."
            : w.Reason ?? "Ready. Experimental: check the car in game after each change.";
        _wheelsStatus.ForeColor = s.CarReady && s.WritesAllowed && w.Reason is null ? Theme.Muted : Theme.Warn;
        _wheelsLive.Text = w.LiveCamberFront is null && w.LiveCamberRear is null ? ""
            : $"In game now: front {Signed(w.LiveCamberFront)}, rear {Signed(w.LiveCamberRear)}";
        foreach (var slider in new[] { _camberFront, _camberRear }) slider.Invalidate();
    }

    private static string Signed(double? degrees) => degrees is { } d ? $"{d:+0.0;-0.0;0.0}°" : "—";

    private static string EngineName(AirLevel level) => AirShiftEngine.Name(level);

    private void Show(CommandResult result, bool quiet = false)
    {
        if (quiet && result.Ok) return;
        _message.Text = result.Message;
        _message.ForeColor = result.Ok ? Theme.Muted : Theme.Warn;
        _pinnedUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void AddLine(string line, bool write = true)
    {
        _lines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_lines.Count > 200) _lines.RemoveAt(0);
        if (_report.Length == 0) _log2.Text = string.Join(Environment.NewLine, _lines.TakeLast(14));
        if (write) _log(line);
    }

    private void PollController()
    {
        switch (_controller.Poll())
        {
            case Controller.Action.Up:
                _ = _engine.SetLevelAsync(AirLevel.Ride, "controller");
                break;
            case Controller.Action.Down:
                _ = _engine.SetLevelAsync(AirLevel.Low, "controller");
                break;
            case Controller.Action.Parked:
                _ = _engine.SetLevelAsync(AirLevel.Parked, "controller");
                break;
        }
    }

    // ---- Window plumbing ------------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        _hotkeysOk = RegisterHotKey(Handle, HotkeyToggle, 0x4000, (uint)Keys.F9)
                     & RegisterHotKey(Handle, HotkeyParked, 0x4000 | 0x0004, (uint)Keys.F9);
        if (!_hotkeysOk) AddLine("F9 is already used by another app; use the buttons or the controller.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            var id = m.WParam.ToInt32();
            if (id == HotkeyToggle) _ = _engine.ToggleAsync("hotkey");
            if (id == HotkeyParked) _ = _engine.SetLevelAsync(AirLevel.Parked, "hotkey");
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyToggle);
        UnregisterHotKey(Handle, HotkeyParked);
        _ui.Stop();
        base.OnFormClosing(e);
    }

    public void BringToFront2()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
    }

    private void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            Program.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = "--elevated" });
            Close();
        }
        catch (Exception)
        {
            AddLine("Restart as administrator was cancelled.");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
