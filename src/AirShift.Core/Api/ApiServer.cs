using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirShift.Core.Engine;

namespace AirShift.Core.Api;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 for the airshift/1 API. It needs no URL reservation and
/// no administrator rights. Every call except /hello needs the bearer token from api.json.
/// </summary>
public sealed class ApiServer : IDisposable
{
    private const int MaxBody = 16 * 1024;

    private readonly AirShiftEngine _engine;
    private readonly string _appVersion;
    private readonly Action? _show;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApiServer(AirShiftEngine engine, string appVersion, Action? show = null, string? token = null)
    {
        _engine = engine;
        _appVersion = appVersion;
        _show = show;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _engine.StateChanged += _ => Interlocked.Exchange(ref _changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    public string Token { get; }

    public int Port { get; private set; }

    public void Start(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Handle(client);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var request = await ReadRequest(stream);
            if (request is null) return;
            var (status, body) = await Route(request);
            var payload = Encoding.UTF8.GetBytes(body.ToJsonString(ApiContract.Json));
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                       $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
        }
        catch (Exception)
        {
            // A broken client never takes the server down.
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);

    private static async Task<Request?> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[MaxBody + 8192];
        var read = 0;
        var headerEnd = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (headerEnd < 0)
        {
            if (read >= buffer.Length) return null;
            var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            if (n == 0) return null;
            read += n;
            headerEnd = IndexOf(buffer.AsSpan(0, read), "\r\n\r\n"u8);
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var parsed) ? parsed : 0;
        if (length < 0 || length > MaxBody) return null;
        var bodyStart = headerEnd + 4;
        while (read - bodyStart < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            if (n == 0) return null;
            read += n;
        }

        var target = first[1];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return new Request(first[0].ToUpperInvariant(), path, query, headers, Encoding.UTF8.GetString(buffer, bodyStart, length));
    }

    private async Task<(int, JsonObject)> Route(Request request)
    {
        // Browsers send Origin; the API is for local apps only.
        if (request.Headers.ContainsKey("Origin")) return (403, Error("Browsers are not allowed."));
        if (request.Headers.TryGetValue("Host", out var host) && !IsLocalHost(host)) return (403, Error("Bad host."));

        var path = request.Path.TrimEnd('/');
        if (path == ApiContract.Prefix + "/hello" && request.Method == "GET")
            return (200, ApiContract.Hello(_appVersion));

        if (!Authorized(request)) return (401, Error("Missing or wrong token."));

        if (path == ApiContract.Prefix + "/state" && request.Method == "GET")
        {
            if (request.Query.TryGetValue("since", out var sinceText) && long.TryParse(sinceText, out var since)
                && _engine.State.Revision == since)
            {
                var wait = request.Query.TryGetValue("wait", out var w) && double.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    ? Math.Clamp(seconds, 0, 25) : 0;
                if (wait > 0) await Task.WhenAny(_changed.Task, Task.Delay(TimeSpan.FromSeconds(wait)));
            }
            return (200, ApiContract.State(_engine.State, _appVersion));
        }

        if (path == ApiContract.Prefix + "/command" && request.Method == "POST")
        {
            JsonObject? body;
            try
            {
                body = JsonNode.Parse(request.Body) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body is null) return (400, Error("Body must be a JSON object."));
            var result = await ApiContract.Run(_engine, body, _show);
            return (result.Ok ? 200 : 409, new JsonObject
            {
                ["ok"] = result.Ok,
                ["message"] = result.Message,
                ["state"] = ApiContract.State(_engine.State, _appVersion),
            });
        }

        return (404, Error("Not found."));
    }

    private bool Authorized(Request request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var value)) return false;
        const string scheme = "Bearer ";
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(value[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static bool IsLocalHost(string host)
    {
        var name = host.Split(':')[0].Trim('[', ']');
        return name is "127.0.0.1" or "localhost" or "::1";
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["message"] = message };

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        _ => "Error",
    };

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle) => data.IndexOf(needle);

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
        }
    }
}
