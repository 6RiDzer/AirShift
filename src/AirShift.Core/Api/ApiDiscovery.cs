using System.Text.Json;
using System.Text.Json.Nodes;

namespace AirShift.Core.Api;

/// <summary>
/// Writes %LOCALAPPDATA%\AirShift\api.json so local apps (a stream deck tool, an overlay) can find the
/// API: port, token, process id and where the app is. Removed when AirShift closes.
/// </summary>
public static class ApiDiscovery
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirShift");

    public static string DefaultPath => Path.Combine(DefaultFolder, "api.json");

    public static void Write(string path, int port, string token, string version, string? exe)
    {
        var node = new JsonObject
        {
            ["api"] = ApiContract.Version,
            ["port"] = port,
            ["token"] = token,
            ["pid"] = Environment.ProcessId,
            ["version"] = version,
            ["exe"] = exe,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    public static void Remove(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["pid"]?.GetValue<int>() == Environment.ProcessId) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
