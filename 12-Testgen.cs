using Iceburg.Database;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Iceburg.Conversion.Iceburg.Testgen;

/// <summary>
/// Magewell decoder client/controller. Preserves the operations from the supplied PHP:
/// login, current source, NDI/saved sources, output modes, HDMI status, and controls.
/// </summary>
public sealed class Testgen
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

    public Testgen(string id)
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

    public async Task<byte[]> GetImage()
    {
        string ip = _device.IpAddress;
        string url = $"http://{ip}/preview.jpg";

        using HttpResponseMessage response = await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync();
    }


    public async Task<string> GetState()
    {
        using var response = await _http.GetAsync(
            $"http://{_device.IpAddress}/state");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task Update(string text1, string text2)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(text1), "text1");
        content.Add(new StringContent(text2), "text2");

        using var response = await _http.PostAsync(
            $"http://{_device.IpAddress}/update", content);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetMode(string mode)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(mode), "mode");

        using var response = await _http.PostAsync(
            $"http://{_device.IpAddress}/mode", content);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetWebUi(bool show)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(show.ToString().ToLowerInvariant()), "show");

        using var response = await _http.PostAsync(
            $"http://{_device.IpAddress}/set_webui", content);

        response.EnsureSuccessStatusCode();
    }

    public async Task ToggleMute()
    {
        using var response = await _http.PostAsync(
            $"http://{_device.IpAddress}/mute", null);

        response.EnsureSuccessStatusCode();
    }





    public async Task<bool> Getdevicestatus(CancellationToken ct = default)
    {
        try
        {
            string ip = _device.IpAddress;
            string url = "http://" + ip + "/state";

            using HttpResponseMessage response =
                await _http.GetAsync(url);

            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Invalid JSON response from testgen.", ex);
            }
            Console.WriteLine(doc.ToString);
            // Check that the response contains a valid "modes" array
            if (Property(doc.RootElement, "text1") is JsonElement arr &&
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
