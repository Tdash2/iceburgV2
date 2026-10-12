using Iceburg.Database;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Iceburg.Conversion.Magewell.Decoder;

/// <summary>
/// Magewell decoder client/controller. Preserves the operations from the supplied PHP:
/// login, current source, NDI/saved sources, output modes, HDMI status, and controls.
/// </summary>
public sealed class MagewellDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Device _device;
    private readonly string _id;
    private readonly string _ip;
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _http;

    public MagewellDecoder(string id)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _device = Database.Database.GetDevice(id)
            ?? throw new InvalidOperationException("No Device Found");

        _ip = ReadDeviceString("IpAddress", "IP", "ip", "Host", "Address") ?? "";
        _username = ReadDeviceString("Username", "username", "UserName", "user") ?? "";
        _password = ReadDeviceString("Password", "password", "Pass") ?? "";

        if (string.IsNullOrWhiteSpace(_ip))
            throw new InvalidOperationException("Device IP/host is not configured.");
        if (string.IsNullOrWhiteSpace(_username))
            throw new InvalidOperationException("Magewell username is not configured.");
        if (string.IsNullOrWhiteSpace(_password))
            throw new InvalidOperationException("Magewell password is not configured.");

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public string GetInfo() => JsonSerializer.Serialize(new
    {
        id = _id,
        name = _device.Name,
        ip = _ip
    });

    private string BaseUrl => "http://" + _ip.Trim().TrimEnd('/');

    private string? ReadDeviceString(params string[] names)
    {
        foreach (var name in names)
        {
            var property = _device.GetType().GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = property?.GetValue(_device);
            if (value != null && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
                return Convert.ToString(value);
        }
        return null;
    }

    private async Task<JsonDocument> RequestAsync(
        string method,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "method=" + Uri.EscapeDataString(method) };
        if (parameters != null)
        {
            foreach (var pair in parameters)
            {
                if (pair.Value != null)
                    query.Add(Uri.EscapeDataString(pair.Key) + "=" +
                              Uri.EscapeDataString(pair.Value));
            }
        }

        using var response = await _http.GetAsync(
            $"{BaseUrl}/mwapi?{string.Join("&", query)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid JSON response from Magewell.", ex);
        }
        return doc;
    }

    private async Task EnsureLoginAsync(CancellationToken cancellationToken)
    {
        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(_password)));
        using var result = await RequestAsync("login", new Dictionary<string, string?>
        {
            ["id"] = _username,
            ["pass"] = hash
        }, cancellationToken);

        if (!HasStatusZero(result.RootElement))
            throw new InvalidOperationException("Failed to login to Magewell decoder.");
    }

    private static bool HasStatusZero(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.Number &&
        status.TryGetInt32(out int code) && code == 0;

    private async Task<JsonDocument> AuthenticatedRequestAsync(
        string method,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoginAsync(cancellationToken);
        return await RequestAsync(method, parameters, cancellationToken);
    }

    private static JsonElement? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p)
            ? p.Clone() : null;

    private static object? Value(JsonElement? element)
    {
        if (element == null) return null;
        var e = element.Value;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number when e.TryGetInt64(out var l) => l,
            JsonValueKind.Number when e.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => e.Clone()
        };
    }
    public async Task<bool> Getdevicestatus(CancellationToken ct = default)
    {
        try
        {
            using var d = await AuthenticatedRequestAsync(
                "get-supported-video-modes",
                cancellationToken: ct);

            if (d == null)
                return false;

            // Check that the response contains a valid "modes" array
            if (Property(d.RootElement, "modes") is JsonElement arr &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0)
            {
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<object> GetStateAsync(CancellationToken ct = default)
    {
        string currentName = "";
        string currentType = "";
        var ndiSources = new List<object>();
        var savedSources = new List<object>();
        var videoModes = new List<object>();
        bool hdmiEnabled = true;
        var errors = new Dictionary<string, string>();

        async Task<JsonDocument?> Try(string method)
        {
            try { return await AuthenticatedRequestAsync(method, cancellationToken: ct); }
            catch (Exception ex) { errors[method] = ex.Message; return null; }
        }

        using (var d = await Try("get-channel"))
        {
            if (d != null)
            {
                currentName = Property(d.RootElement, "name")?.GetString() ?? "";
                currentType = Property(d.RootElement, "ndi-name") is JsonElement ndi &&
                              ndi.ValueKind == JsonValueKind.True ? "NDI" : "Saved";
            }
        }
        using (var d = await Try("get-ndi-sources"))
        {
            if (d != null && Property(d.RootElement, "sources") is JsonElement arr &&
                arr.ValueKind == JsonValueKind.Array)
                ndiSources = arr.EnumerateArray().Select(x => (object)x.Clone()).ToList();
        }
        using (var d = await Try("list-channels"))
        {
            if (d != null && Property(d.RootElement, "channels") is JsonElement arr &&
                arr.ValueKind == JsonValueKind.Array)
                savedSources = arr.EnumerateArray().Select(x => (object)x.Clone()).ToList();
        }
        using (var d = await Try("get-supported-video-modes"))
        {
            if (d != null && Property(d.RootElement, "modes") is JsonElement arr &&
                arr.ValueKind == JsonValueKind.Array)
                videoModes = arr.EnumerateArray().Select(x => (object)x.Clone()).ToList();
        }
        using (var d = await Try("get-hdmi-output"))
        {
            if (d != null && Property(d.RootElement, "enabled") is JsonElement enabled)
                hdmiEnabled = enabled.ValueKind == JsonValueKind.True ||
                              (enabled.ValueKind == JsonValueKind.String &&
                               bool.TryParse(enabled.GetString(), out var b) && b);
        }

        return new
        {
            device = new { id = _id, name = _device.Name, ip = _ip },
            currentSource = new { name = currentName, type = currentType },
            ndiSources,
            savedSources,
            videoModes,
            hdmiOutputEnabled = hdmiEnabled,
            errors
        };
    }

    public async Task<object> SwitchSourceAsync(string sourceType, string sourceName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return new { ok = false, message = "A source name is required." };
        if (sourceType is not ("ndi" or "saved"))
            return new { ok = false, message = "Invalid source type." };

        using var result = await AuthenticatedRequestAsync("set-channel",
            new Dictionary<string, string?>
            {
                ["ndi-name"] = sourceType == "ndi" ? "true" : "false",
                ["name"] = sourceName
            }, ct);

        return HasStatusZero(result.RootElement)
            ? new { ok = true, message = "Switched to source: " + sourceName }
            : new { ok = false, message = "Failed to switch source." };
    }

    public async Task<object> SetVideoModeAsync(int width, int height, int fieldRate,
        double aspectRatio, bool interlaced, CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0 || fieldRate <= 0 ||
            !double.IsFinite(aspectRatio) || aspectRatio <= 0)
            return new { ok = false, message = "Invalid output format." };

        using var result = await AuthenticatedRequestAsync("set-video-mode",
            new Dictionary<string, string?>
            {
                ["width"] = width.ToString(CultureInfo.InvariantCulture),
                ["height"] = height.ToString(CultureInfo.InvariantCulture),
                ["field-rate"] = fieldRate.ToString(CultureInfo.InvariantCulture),
                ["aspect-ratio"] = aspectRatio.ToString(CultureInfo.InvariantCulture),
                ["interlaced"] = interlaced ? "true" : "false"
            }, ct);

        return HasStatusZero(result.RootElement)
            ? new { ok = true, message = "Output format updated." }
            : new { ok = false, message = "Failed to update output format." };
    }

    public async Task<object> SetHdmiOutputAsync(bool enabled, CancellationToken ct = default)
    {
        using var result = await AuthenticatedRequestAsync("set-hdmi-output",
            new Dictionary<string, string?> { ["enabled"] = enabled ? "true" : "false" }, ct);

        return HasStatusZero(result.RootElement)
            ? new { ok = true, message = "HDMI output updated." }
            : new { ok = false, message = "Failed to update HDMI output." };
    }
}
