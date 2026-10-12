using Iceburg.Database;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Iceburg.Mixer.Aquastreams.Playback;

/// <summary>
/// Magewell decoder client/controller. Preserves the operations from the supplied PHP:
/// login, current source, NDI/saved sources, output modes, HDMI status, and controls.
/// </summary>
public sealed class AquastreamsPlayback
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

    public AquastreamsPlayback(string id)
    {

        _device = Database.Database.GetDevice(id);

        var handler = new HttpClientHandler
        {

        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public string GetInfo() => JsonSerializer.Serialize(new
    {
        id = _id,
        name = _device.Name,
        ip = _ip
    });





    public async Task<JsonDocument> GetButtons()
    {
        string ip = _device.IpAddress;
        string url = "http://"+ip+"/getbuttons/";

        using HttpResponseMessage response =
            await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json);
    }
    public async Task<JsonDocument> Getstatus()
    {
        string ip = _device.IpAddress;
        string url = "http://" + ip + "/getstatus/";

        using HttpResponseMessage response =
            await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json);
    }

    public async Task toggleplayback()
    {
        string ip = _device.IpAddress;
        string url = "http://" + ip + "/pause/";

        using HttpResponseMessage response =
            await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

    }

    public async Task play(string parameters)
    {
        string ip = _device.IpAddress;
        string url = "http://" + ip + "/play/?"+parameters;
        
        using HttpResponseMessage response =
            await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

    }

    public async Task setvol(string parameters)
    {
        string ip = _device.IpAddress;
        string url = "http://" + ip + "/setvol/?" + parameters;

        using HttpResponseMessage response =
            await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

    }

    public async Task<bool> Getdevicestatus(CancellationToken ct = default)
    {
        try
        {
            string ip = _device.IpAddress;
            string url = "http://" + ip + "/getstatus/";

            using HttpResponseMessage response =
                await _http.GetAsync(url);

            response.EnsureSuccessStatusCode();

            string body= await response.Content.ReadAsStringAsync();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Invalid JSON response from Magewell.", ex);
            }
            Console.WriteLine(doc.ToString);
            // Check that the response contains a valid "modes" array
            if (Property(doc.RootElement, "volume") is JsonElement arr &&
                arr.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(arr.GetString()))
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
    private static JsonElement? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p)
            ? p.Clone() : null;

}
