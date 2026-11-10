using Iceburg.Database;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iceburg.Mixer.Zowietek;

/// <summary>
/// Zowietek POV camera controller.
/// Implements the camera operations used by the supplied PHP controller:
/// read exposure/white-balance/image settings and update those settings.
/// </summary>
public sealed class ZowietekPov
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Device _device;
    private readonly HttpClient _http;

    public ZowietekPov(string id)
    {
        _device = Database.Database.GetDevice(id);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = _device.Name,
        ip = _device.IpAddress
    });
    public Task<string> getinfo(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            }));
        }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            IP = device.IpAddress,
            Name = device.Name
        }));
    }

    /// <summary>
    /// Reads all settings and option lists from the camera.
    /// </summary>
    public async Task<ZowietekCameraSettings> GetSettings(CancellationToken ct = default)
    {
        var result = new ZowietekCameraSettings();

        using var exposure = await GetCameraInfo(
            new { group = "camera", opt = "get_exposure_info" }, ct);

        var exposureData = GetData(exposure.RootElement);

        if (TryGetProperty(exposureData, "shutter", out var shutter))
        {
            result.ShutterOptions = ReadOptions(shutter, "shutter_list");
            result.Shutter = ReadSelectedId(shutter, result.ShutterOptions);
        }

        result.Gain = ReadInt(exposureData, "gain", result.Gain);

        using var whiteBalance = await GetCameraInfo(
            new { group = "camera", opt = "get_white_balance_info" }, ct);

        var wbData = GetData(whiteBalance.RootElement);

        if (TryGetProperty(wbData, "var", out var wb))
        {
            result.WhiteBalanceOptions = ReadOptions(wb, "var_list");
            result.WhiteBalance = ReadSelectedId(wb, result.WhiteBalanceOptions);
        }

        result.Saturation = ReadInt(wbData, "saturation", result.Saturation);
        result.Hue = ReadInt(wbData, "hue", result.Hue);

        using var image = await GetCameraInfo(
            new { group = "camera", opt = "get_image_info" }, ct);

        var imageData = GetData(image.RootElement);
        result.Brightness = ReadInt(imageData, "brightness", result.Brightness);
        result.Contrast = ReadInt(imageData, "contrast", result.Contrast);
        result.Sharpness = ReadInt(imageData, "sharpness", result.Sharpness);

        return result;
    }

    /// <summary>
    /// Applies all supplied camera settings. The shutter and white-balance values
    /// are option IDs, matching the selected_id values expected by the camera API.
    /// </summary>
    public async Task UpdateSettings(
        ZowietekCameraUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await PostCameraCommand(new
        {
            group = "camera",
            opt = "set_exposure_info",
            data = new
            {
                shutter = new { selected_id = update.Shutter },
                gain = update.Gain
            }
        }, ct);

        await PostCameraCommand(new
        {
            group = "camera",
            opt = "set_white_balance_info",
            data = new
            {
                @var = new { selected_id = update.WhiteBalance }
            }
        }, ct);

        await PostCameraCommand(new
        {
            group = "camera",
            opt = "set_white_balance_info",
            data = new
            {
                saturation = update.Saturation,
                hue = update.Hue
            }
        }, ct);

        await PostCameraCommand(new
        {
            group = "camera",
            opt = "set_image_info",
            data = new
            {
                brightness = update.Brightness,
                contrast = update.Contrast,
                sharpness = update.Sharpness
            }
        }, ct);
    }

    public async Task<bool> GetDeviceStatus(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetCameraInfo(
                new { group = "camera", opt = "get_image_info" }, ct);

            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   TryGetProperty(doc.RootElement, "data", out _);
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonDocument> GetCameraInfo(object payload, CancellationToken ct)
    {
        var body = await SendCameraRequest("getinfo", payload, ct);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The Zowietek camera returned invalid JSON.", ex);
        }
    }

    private async Task PostCameraCommand(object payload, CancellationToken ct)
    {
        // The camera's setinfo response is not used by the supplied PHP program,
        // so accept an empty or non-JSON response while still checking HTTP errors.
        await SendCameraRequest("setinfo", payload, ct);
    }

    private async Task<string> SendCameraRequest(
        string option,
        object payload,
        CancellationToken ct)
    {
        var ip = (_device.IpAddress ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(ip))
            throw new InvalidOperationException("No IP address is configured for this camera.");

        // Device records normally contain an IP/host. Also accept a full HTTP URL.
        var baseAddress = ip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          ip.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? ip.TrimEnd('/')
            : $"http://{ip.TrimEnd('/')}";

        var url = $"{baseAddress}/camera?option={Uri.EscapeDataString(option)}&login_check_flag=1";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        if (responseBody.StartsWith("CURL ERROR:", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException(responseBody);

        return responseBody;
    }

    private static JsonElement GetData(JsonElement root)
    {
        return TryGetProperty(root, "data", out var data) ? data : root;
    }

    private static List<ZowietekOption> ReadOptions(JsonElement parent, string listName)
    {
        var options = new List<ZowietekOption>();

        if (!TryGetProperty(parent, listName, out var list))
            return options;

        if (list.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var id = ReadInt(item, "id", index);
                    var name = ReadString(item, "name", id.ToString(CultureInfo.InvariantCulture));
                    options.Add(new ZowietekOption { Id = id, Name = name });
                }
                else
                {
                    options.Add(new ZowietekOption
                    {
                        Id = index,
                        Name = ElementToString(item)
                    });
                }

                index++;
            }
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in list.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    var fallbackId = ParseInt(property.Name, options.Count);
                    var id = ReadInt(property.Value, "id", fallbackId);
                    var name = ReadString(property.Value, "name", id.ToString(CultureInfo.InvariantCulture));
                    options.Add(new ZowietekOption { Id = id, Name = name });
                }
                else
                {
                    options.Add(new ZowietekOption
                    {
                        Id = ParseInt(property.Name, options.Count),
                        Name = ElementToString(property.Value)
                    });
                }
            }
        }

        return options;
    }

    private static int ReadSelectedId(
        JsonElement parent,
        IReadOnlyList<ZowietekOption> options)
    {
        var selected = ReadInt(parent, "selected_id", options.Count > 0 ? options[0].Id : 0);
        return selected;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value))
            return true;

        value = default;
        return false;
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        return TryGetProperty(element, name, out var value)
            ? ParseInt(ElementToString(value), fallback)
            : fallback;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string ReadString(JsonElement element, string name, string fallback)
    {
        return TryGetProperty(element, name, out var value)
            ? ElementToString(value)
            : fallback;
    }

    private static string ElementToString(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.ToString();
    }
}

public sealed class ZowietekCameraSettings
{
    public int Shutter { get; set; } = 0;
    public List<ZowietekOption> ShutterOptions { get; set; } = new();
    public int WhiteBalance { get; set; } = 0;
    public List<ZowietekOption> WhiteBalanceOptions { get; set; } = new();
    public int Gain { get; set; } = 10;
    public int Saturation { get; set; } = 50;
    public int Hue { get; set; } = 0;
    public int Brightness { get; set; } = 4;
    public int Contrast { get; set; } = 8;
    public int Sharpness { get; set; } = 5;
}

public sealed class ZowietekCameraUpdate
{
    [JsonPropertyName("shutter")]
    public int Shutter { get; set; }

    [JsonPropertyName("wb")]
    public int WhiteBalance { get; set; }

    [JsonPropertyName("gain")]
    public int Gain { get; set; }

    [JsonPropertyName("saturation")]
    public int Saturation { get; set; }

    [JsonPropertyName("hue")]
    public int Hue { get; set; }

    [JsonPropertyName("brightness")]
    public int Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public int Contrast { get; set; }

    [JsonPropertyName("sharpness")]
    public int Sharpness { get; set; }
}

public sealed class ZowietekOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
