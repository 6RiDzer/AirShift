using System.Text.Json;
using AirShift.Core.Ride;

namespace AirShift.Core.Engine;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string? _path;

    public SettingsStore(string? path) => _path = path;

    public RideSettings Load()
    {
        RideSettings settings;
        try
        {
            settings = _path is not null && File.Exists(_path)
                ? JsonSerializer.Deserialize<RideSettings>(File.ReadAllText(_path), Options) ?? new RideSettings()
                : new RideSettings();
        }
        catch (Exception)
        {
            settings = new RideSettings();
        }
        settings.Clamp();
        return settings;
    }

    public void Save(RideSettings settings)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Settings are a convenience; a failed save must never stop the ride.
        }
    }
}
