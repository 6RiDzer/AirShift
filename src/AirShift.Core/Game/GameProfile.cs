using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirShift.Core.Memory;

namespace AirShift.Core.Game;

/// <summary>
/// Where the data AirShift needs lives in one game build. Profiles are plain data so a new game
/// update only needs a new JSON file, never a new engine. Every value is checked at run time
/// before anything is written.
/// </summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Revision { get; set; }
    public string[] ProcessNames { get; set; } = [];
    public string[] KnownBuilds { get; set; } = [];
    public ContextLocator Context { get; set; } = new();
    public HexValue[] Chain { get; set; } = [];
    public EntityListLayout EntityList { get; set; } = new();
    public HexValue EntityToCar { get; set; }
    public CarLayout Car { get; set; } = new();
    public WheelLayout Wheels { get; set; } = new();
    public CarIdentityLayout? CarIdentity { get; set; }
    public ProfileLimits Limits { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new HexValueConverter() },
    };

    public static GameProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty game profile.");
        profile.Validate();
        return profile;
    }

    /// <summary>The profiles shipped inside AirShift.</summary>
    public static IReadOnlyList<GameProfile> BuiltIn()
    {
        var assembly = typeof(GameProfile).Assembly;
        var list = new List<GameProfile>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("AirShift.Profiles.", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(Parse(reader.ReadToEnd()));
        }
        return list;
    }

    /// <summary>Built-in profiles, replaced by any profile with the same id found in <paramref name="folder"/>.</summary>
    public static IReadOnlyList<GameProfile> Load(string? folder)
    {
        var byId = BuiltIn().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var profile = Parse(File.ReadAllText(file));
                    if (!byId.TryGetValue(profile.Id, out var existing) || profile.Revision >= existing.Revision)
                        byId[profile.Id] = profile;
                }
                catch (Exception)
                {
                    // A broken override never replaces a working built-in profile.
                }
            }
        }
        return byId.Values.ToList();
    }

    public bool MatchesBuild(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return false;
        return KnownBuilds.Any(b => fileVersion.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("Profile id is missing.");
        if (ProcessNames.Length == 0) throw new InvalidDataException("Profile has no process name.");
        if (Wheels.Order.Length != 4) throw new InvalidDataException("Profile must describe four wheels.");
        if (Wheels.Stride.Value <= 0) throw new InvalidDataException("Wheel stride must be positive.");
        if (Chain.Length == 0) throw new InvalidDataException("Pointer chain is empty.");
        _ = Signature.Parse(Context.Signature);
    }
}

public sealed class ContextLocator
{
    public string Signature { get; set; } = "";
    public int InstructionOffset { get; set; }
    public int DisplacementOffset { get; set; } = 3;
    public int InstructionLength { get; set; } = 7;
    public HexValue? FallbackRva { get; set; }
}

public sealed class EntityListLayout
{
    public HexValue Begin { get; set; }
    public HexValue End { get; set; }
    public int MaxEntries { get; set; } = 4096;
}

public sealed class CarLayout
{
    public HexValue Speed { get; set; }
    public HexValue? EngineSpeed { get; set; }
}

public sealed class WheelLayout
{
    public HexValue Base { get; set; }
    public HexValue Stride { get; set; }
    public string[] Order { get; set; } = [];
    public HexValue HubHeight { get; set; }
    public HexValue Radius { get; set; }

    public long Address(long car, int wheel, HexValue field) => car + Base.Value + wheel * Stride.Value + field.Value;

    /// <summary>Index in the game's order of the wheel with this name (FL, FR, RL, RR).</summary>
    public int IndexOf(string name) => Array.FindIndex(Order, o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase));

    public bool IsFront(int wheel) => Order[wheel].StartsWith('F');
}

public sealed class CarIdentityLayout
{
    public HexValue EntityToRecord { get; set; }
    public HexValue CarId { get; set; }
}

public sealed class ProfileLimits
{
    public double HubHeightMin { get; set; } = 0.02;
    public double HubHeightMax { get; set; } = 2.0;
    public double RadiusMin { get; set; } = 0.05;
    public double RadiusMax { get; set; } = 1.0;
    public double HardFloor { get; set; } = 0.01;
    public double MaxSpeedMs { get; set; } = 150;
    public double EngineSpeedMax { get; set; } = 6300;
}

/// <summary>An offset written as "0x1D0" or a plain number in profile JSON.</summary>
public readonly record struct HexValue(long Value)
{
    public static implicit operator long(HexValue h) => h.Value;
    public override string ToString() => "0x" + Value.ToString("X", CultureInfo.InvariantCulture);

    public static HexValue Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return new HexValue(long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return new HexValue(long.Parse(text, CultureInfo.InvariantCulture));
    }
}

public sealed class HexValueConverter : JsonConverter<HexValue>
{
    public override HexValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => new HexValue(reader.GetInt64()),
            JsonTokenType.String => HexValue.Parse(reader.GetString() ?? "0"),
            _ => throw new JsonException("Expected a number or a hex string."),
        };

    public override void Write(Utf8JsonWriter writer, HexValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
