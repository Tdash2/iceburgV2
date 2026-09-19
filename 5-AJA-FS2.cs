using Iceburg.Database;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

namespace Iceburg.Conversion.AJA.fs2;

/// <summary>
/// Single-file AJA FS4 controller.
/// Based directly on the supplied audio.php, index.php and the existing
/// C# device/API style.
/// </summary>
public sealed class AJAFS2
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly Dictionary<string, FS4ParameterDefinition> Definitions =
        BuildDefinitions();

    private static readonly Dictionary<int, string> VideoGroups = new()
    {
        [1] = "Video 1 Processing",
        [2] = "Video 2 Processing"
    };

    private static readonly Dictionary<int, string> AudioGroups = new()
    {
        [1] = "Audio 1 Processing",
        [2] = "Audio 2 Processing"
    };

    private static readonly Dictionary<int, Dictionary<int, string>> VideoSubgroups =
        BuildSubgroups("Input Settings", "ProcAmp", "Red Channel", "Green Channel", "Blue Channel");

    private static readonly Dictionary<int, Dictionary<int, string>> AudioSubgroups =
        BuildSubgroups("Input Settings", "Channel Map 1-4", "Channel Map 5-8",
                       "Channel Map 9-12", "Channel Map 13-16");

    private readonly string _id;
    private readonly Device _device;

    public AJAFS2(string id)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _device = Database.Database.GetDevice(id)
            ?? throw new InvalidOperationException("No Device Found");
    }

    // ============================================================
    // PARAMETER MODEL
    // ============================================================

    public enum FS4ParameterType
    {
        Dropdown,
        Slider
    }

    public sealed class FS4ParameterDefinition
    {
        public string ParamId { get; init; } = "";
        public FS4ParameterType Type { get; init; }
        public int Group { get; init; }
        public int Subgroup { get; init; }

        // Application-level write permission.
        // The supplied PHP had no explicit writable/read-only field, so
        // the parameters from PHP default to writable.
        public bool Writable { get; init; } = true;
    }

    private sealed class FS2Descriptor
    {
        [JsonPropertyName("param_id")]
        public string ParamId { get; set; } = "";

        [JsonPropertyName("param_name")]
        public string? ParamName { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        [JsonPropertyName("default_value")]
        public JsonElement DefaultValue { get; set; }

        [JsonPropertyName("min_value")]
        public JsonElement MinValue { get; set; }

        [JsonPropertyName("max_value")]
        public JsonElement MaxValue { get; set; }

        [JsonPropertyName("enum_values")]
        public JsonElement EnumValues { get; set; }
    }

   
    // ============================================================
    // PARAMETER DEFINITIONS
    // ============================================================

    private static Dictionary<string, FS4ParameterDefinition> BuildDefinitions()
    {
        var d = new Dictionary<string, FS4ParameterDefinition>(StringComparer.Ordinal);

        void Add(string id, FS4ParameterType type, int group, int subgroup,
                 bool writable = true)
        {
            d[id] = new FS4ParameterDefinition
            {
                ParamId = id,
                Type = type,
                Group = group,
                Subgroup = subgroup,
                Writable = writable
            };
        }

        // AUDIO - copied from audio.php
        Add("eParamID_Audio1Input_Universal", FS4ParameterType.Dropdown, 1, 1);
        Add("eParamID_Audio1MapCh1_Universal", FS4ParameterType.Dropdown, 1, 2);
        Add("eParamID_Audio1MapCh2_Universal", FS4ParameterType.Dropdown, 1, 2);
        Add("eParamID_Audio1MapCh3_Universal", FS4ParameterType.Dropdown, 1, 2);
        Add("eParamID_Audio1MapCh4_Universal", FS4ParameterType.Dropdown, 1, 2);
        Add("eParamID_Audio1MapCh5_Universal", FS4ParameterType.Dropdown, 1, 3);
        Add("eParamID_Audio1MapCh6_Universal", FS4ParameterType.Dropdown, 1, 3);
        Add("eParamID_Audio1MapCh7_Universal", FS4ParameterType.Dropdown, 1, 3);
        Add("eParamID_Audio1MapCh8_Universal", FS4ParameterType.Dropdown, 1, 3);
        Add("eParamID_Audio1MapCh9_Universal", FS4ParameterType.Dropdown, 1, 4);
        Add("eParamID_Audio1MapCh10_Universal", FS4ParameterType.Dropdown, 1, 4);
        Add("eParamID_Audio1MapCh11_Universal", FS4ParameterType.Dropdown, 1, 4);
        Add("eParamID_Audio1MapCh12_Universal", FS4ParameterType.Dropdown, 1, 4);
        Add("eParamID_Audio1MapCh13_Universal", FS4ParameterType.Dropdown, 1, 5);
        Add("eParamID_Audio1MapCh14_Universal", FS4ParameterType.Dropdown, 1, 5);
        Add("eParamID_Audio1MapCh15_Universal", FS4ParameterType.Dropdown, 1, 5);
        Add("eParamID_Audio1MapCh16_Universal", FS4ParameterType.Dropdown, 1, 5);

        Add("eParamID_Audio2Input_Universal", FS4ParameterType.Dropdown, 2, 1);
        Add("eParamID_Audio2MapCh1_Universal", FS4ParameterType.Dropdown, 2, 2);
        Add("eParamID_Audio2MapCh2_Universal", FS4ParameterType.Dropdown, 2, 2);
        Add("eParamID_Audio2MapCh3_Universal", FS4ParameterType.Dropdown, 2, 2);
        Add("eParamID_Audio2MapCh4_Universal", FS4ParameterType.Dropdown, 2, 2);
        Add("eParamID_Audio2MapCh5_Universal", FS4ParameterType.Dropdown, 2, 3);
        Add("eParamID_Audio2MapCh6_Universal", FS4ParameterType.Dropdown, 2, 3);
        Add("eParamID_Audio2MapCh7_Universal", FS4ParameterType.Dropdown, 2, 3);
        Add("eParamID_Audio2MapCh8_Universal", FS4ParameterType.Dropdown, 2, 3);
        Add("eParamID_Audio2MapCh9_Universal", FS4ParameterType.Dropdown, 2, 4);
        Add("eParamID_Audio2MapCh10_Universal", FS4ParameterType.Dropdown, 2, 4);
        Add("eParamID_Audio2MapCh11_Universal", FS4ParameterType.Dropdown, 2, 4);
        Add("eParamID_Audio2MapCh12_Universal", FS4ParameterType.Dropdown, 2, 4);
        Add("eParamID_Audio2MapCh13_Universal", FS4ParameterType.Dropdown, 2, 5);
        Add("eParamID_Audio2MapCh14_Universal", FS4ParameterType.Dropdown, 2, 5);
        Add("eParamID_Audio2MapCh15_Universal", FS4ParameterType.Dropdown, 2, 5);
        Add("eParamID_Audio2MapCh16_Universal", FS4ParameterType.Dropdown, 2, 5);


        // VIDEO - copied from index.php
        Add("eParamID_Vid1VideoInput", FS4ParameterType.Dropdown, 1, 1);
        Add("eParamID_Vid1OutputFormat_5923", FS4ParameterType.Dropdown, 1, 1);
        Add("eParamID_Vid1YUVProcAmpEnable_SDI1", FS4ParameterType.Dropdown, 1, 1);
        Add("eParamID_Vid1RGBProcAmpEnable_SDI1", FS4ParameterType.Dropdown, 1, 1);

        Add("eParamID_Vid1YUVProcAmpGain_SDI1", FS4ParameterType.Slider, 1, 2);
        Add("eParamID_Vid1YUVProcAmpBlack_SDI1", FS4ParameterType.Slider, 1, 2);
        Add("eParamID_Vid1YUVProcAmpHue_SDI1", FS4ParameterType.Slider, 1, 2);
        Add("eParamID_Vid1YUVProcAmpSat_SDI1", FS4ParameterType.Slider, 1, 2);

        Add("eParamID_Vid1RGBProcAmpGainRed_SDI1", FS4ParameterType.Slider, 1, 3);
        Add("eParamID_Vid1RGBProcAmpBlackRed_SDI1", FS4ParameterType.Slider, 1, 3);
        Add("eParamID_Vid1RGBProcAmpGammaRed_SDI1", FS4ParameterType.Slider, 1, 3);

        Add("eParamID_Vid1RGBProcAmpGainGreen_SDI1", FS4ParameterType.Slider, 1, 4);
        Add("eParamID_Vid1RGBProcAmpBlackGreen_SDI1", FS4ParameterType.Slider, 1, 4);
        Add("eParamID_Vid1RGBProcAmpGammaGreen_SDI1", FS4ParameterType.Slider, 1, 4);

        Add("eParamID_Vid1RGBProcAmpGainBlue_SDI1", FS4ParameterType.Slider, 1, 5);
        Add("eParamID_Vid1RGBProcAmpBlackBlue_SDI1", FS4ParameterType.Slider, 1, 5);
        Add("eParamID_Vid1RGBProcAmpGammaBlue_SDI1", FS4ParameterType.Slider, 1, 5);


        Add("eParamID_Vid2VideoInput", FS4ParameterType.Dropdown, 2, 1);
        Add("eParamID_Vid2OutputFormat_5923", FS4ParameterType.Dropdown, 2, 1);
        Add("eParamID_Vid2YUVProcAmpEnable_SDI1", FS4ParameterType.Dropdown, 2, 1);
        Add("eParamID_Vid2RGBProcAmpEnable_SDI1", FS4ParameterType.Dropdown, 2, 1);

        Add("eParamID_Vid2YUVProcAmpGain_SDI1", FS4ParameterType.Slider, 2, 2);
        Add("eParamID_Vid2YUVProcAmpBlack_SDI1", FS4ParameterType.Slider, 2, 2);
        Add("eParamID_Vid2YUVProcAmpHue_SDI1", FS4ParameterType.Slider, 2, 2);
        Add("eParamID_Vid2YUVProcAmpSat_SDI1", FS4ParameterType.Slider, 2, 2);

        Add("eParamID_Vid2RGBProcAmpGainRed_SDI1", FS4ParameterType.Slider, 2, 3);
        Add("eParamID_Vid2RGBProcAmpBlackRed_SDI1", FS4ParameterType.Slider, 2, 3);
        Add("eParamID_Vid2RGBProcAmpGammaRed_SDI1", FS4ParameterType.Slider, 2, 3);

        Add("eParamID_Vid2RGBProcAmpGainGreen_SDI1", FS4ParameterType.Slider, 2, 4);
        Add("eParamID_Vid2RGBProcAmpBlackGreen_SDI1", FS4ParameterType.Slider, 2, 4);
        Add("eParamID_Vid2RGBProcAmpGammaGreen_SDI1", FS4ParameterType.Slider, 2, 4);

        Add("eParamID_Vid2RGBProcAmpGainBlue_SDI1", FS4ParameterType.Slider, 2, 5);
        Add("eParamID_Vid2RGBProcAmpBlackBlue_SDI1", FS4ParameterType.Slider, 2, 5);
        Add("eParamID_Vid2RGBProcAmpGammaBlue_SDI1", FS4ParameterType.Slider, 2, 5);

        return d;
    }

    private static Dictionary<int, Dictionary<int, string>> BuildSubgroups(
        string s1, string s2, string s3, string s4, string s5)
    {
        var result = new Dictionary<int, Dictionary<int, string>>();

        for (int group = 1; group <= 4; group++)
        {
            result[group] = new Dictionary<int, string>
            {
                [1] = s1,
                [2] = s2,
                [3] = s3,
                [4] = s4,
                [5] = s5
            };
        }

        return result;
    }

    // ============================================================
    // DEVICE INFO
    // ============================================================

    public string GetInfo()
    {
        return JsonSerializer.Serialize(new
        {
            IP = _device.IpAddress,
            Name = _device.Name,
            Id = _id
        });
    }

    // ============================================================
    // FS4 COMMUNICATION
    // ============================================================

    private string BaseUrl => $"http://{_device.IpAddress}";

    private async Task<List<FS2Descriptor>> FetchDescriptorAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(
            $"{BaseUrl}/desc.json",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        string json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        return JsonSerializer.Deserialize<List<FS2Descriptor>>(json)
               ?? new List<FS2Descriptor>();
    }

    private async Task<JsonElement?> GetValueAsync(
        string paramId,
        CancellationToken cancellationToken = default)
    {
        string url =
            $"{BaseUrl}/config?alt=json&action=get&paramid=" +
            Uri.EscapeDataString(paramId);

        using var response =
            await Http.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        string json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("value", out var value))
                return null;

            return value.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> SetValueAsync(
        string paramId,
        string value,
        CancellationToken cancellationToken = default)
    {
        string url =
            $"{BaseUrl}/config?action=set&paramid=" +
            Uri.EscapeDataString(paramId) +
            "&value=" +
            Uri.EscapeDataString(value);

        using var response =
            await Http.GetAsync(url, cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"FS2 rejected {paramId}: HTTP {(int)response.StatusCode} {body}");
        }

        return body;
    }

    // ============================================================
    // GET / SET PERMISSION CHECK
    // ============================================================

    /// <summary>
    /// Checks that the eParamID is configured by this application,
    /// is writable, exists on this particular FS4, and has a valid value.
    /// </summary>
    public async Task<(bool Allowed, string? Error)> CanSetParamAsync(
        string paramId,
        object? value = null,
        CancellationToken cancellationToken = default)
    {
        if (!Definitions.TryGetValue(paramId, out var definition))
            return (false, "invalid param");

        if (!definition.Writable)
            return (false, "parameter is read-only");

        var descriptor = await FetchDescriptorAsync(cancellationToken);

        var actual = descriptor.FirstOrDefault(x =>
            string.Equals(x.ParamId, paramId, StringComparison.Ordinal));

        if (actual == null)
            return (false, "parameter is not supported by this FS4");

        if (value == null)
            return (true, null);

        string candidate =
            Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ?? "";

        if (definition.Type == FS4ParameterType.Slider)
        {
            if (!double.TryParse(
                    candidate,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double number))
            {
                return (false, "invalid numeric value");
            }

            if (TryGetDouble(actual.MinValue, out double min) &&
                number < min)
            {
                return (false, $"value below minimum {min}");
            }

            if (TryGetDouble(actual.MaxValue, out double max) &&
                number > max)
            {
                return (false, $"value above maximum {max}");
            }
        }

        if (definition.Type == FS4ParameterType.Dropdown &&
            actual.EnumValues.ValueKind == JsonValueKind.Array)
        {
            bool found = actual.EnumValues.EnumerateArray().Any(option =>
            {
                if (option.ValueKind != JsonValueKind.Object)
                    return false;

                if (!option.TryGetProperty("value", out var optionValue))
                    return false;

                return JsonScalarEquals(optionValue, candidate);
            });

            if (!found)
                return (false, "value is not one of the allowed enum values");
        }

        return (true, null);
    }

    private static bool JsonScalarEquals(
        JsonElement element,
        string value)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return string.Equals(
                element.GetString(),
                value,
                StringComparison.OrdinalIgnoreCase);
        }

        if (element.ValueKind == JsonValueKind.Number)
            return element.ToString() == value;

        if (element.ValueKind == JsonValueKind.True)
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (element.ValueKind == JsonValueKind.False)
            return value.Equals("false", StringComparison.OrdinalIgnoreCase);

        return element.ToString() == value;
    }

    private static bool TryGetDouble(
        JsonElement element,
        out double value)
    {
        value = 0;

        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value);
    }

    // ============================================================
    // GET PARAM
    // ============================================================

    public async Task<object> GetParamAsync(
        string paramId,
        CancellationToken cancellationToken = default)
    {
        if (!Definitions.TryGetValue(paramId, out var definition))
        {
            return new
            {
                ok = false,
                error = "invalid param"
            };
        }

        JsonElement? value =
            await GetValueAsync(paramId, cancellationToken);

        return new
        {
            ok = value.HasValue,
            param = paramId,
            value = value.HasValue
                ? GetScalarValue(value.Value)
                : null,
            writable = definition.Writable
        };
    }

    // ============================================================
    // SET PARAM
    // ============================================================

    public async Task<object> SetParamAsync(
        string paramId,
        object? value,
        CancellationToken cancellationToken = default)
    {
        var check = await CanSetParamAsync(
            paramId,
            value,
            cancellationToken);

        if (!check.Allowed)
        {
            return new
            {
                ok = false,
                error = check.Error
            };
        }

        string stringValue =
            Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ?? "";

        try
        {
            string response =
                await SetValueAsync(
                    paramId,
                    stringValue,
                    cancellationToken);

            return new
            {
                ok = true,
                param = paramId,
                value,
                response
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                error = ex.Message
            };
        }
    }

    // ============================================================
    // CONFIG
    // ============================================================

    public async Task<object> GetConfigurationAsync(
        bool audio,
        CancellationToken cancellationToken = default)
    {
        var groups = audio ? AudioGroups : VideoGroups;
        var subgroups = audio ? AudioSubgroups : VideoSubgroups;

        var descriptor =
            await FetchDescriptorAsync(cancellationToken);

        var descriptorMap = descriptor
            .Where(x => !string.IsNullOrWhiteSpace(x.ParamId))
            .GroupBy(x => x.ParamId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.Ordinal);

        var result = new Dictionary<string, object?>();

        foreach (var definition in Definitions.Values
                     .Where(x => audio
                         ? x.ParamId.StartsWith(
                             "eParamID_Audio",
                             StringComparison.Ordinal)
                         : x.ParamId.StartsWith(
                             "eParamID_Vid",
                             StringComparison.Ordinal))
                     .OrderBy(x => x.Group)
                     .ThenBy(x => x.Subgroup)
                     .ThenBy(x => x.ParamId))
        {
            if (!descriptorMap.TryGetValue(
                    definition.ParamId,
                    out var d))
            {
                // Same behavior as the PHP:
                // if the parameter is not in desc.json, do not expose it.
                continue;
            }

            var item = new Dictionary<string, object?>
            {
                ["type"] =
                    definition.Type == FS4ParameterType.Slider
                        ? "slider"
                        : "dropdown",

                ["group"] = definition.Group,
                ["subgroup"] = definition.Subgroup,

                ["value"] = GetScalarValue(d.Value),
                ["default"] = GetScalarValue(d.DefaultValue),

                ["name"] = d.ParamName ?? definition.ParamId,

                ["writable"] = definition.Writable
            };

            if (definition.Type == FS4ParameterType.Slider)
            {
                item["min"] =
                    GetScalarValue(d.MinValue) ?? 0;

                item["max"] =
                    GetScalarValue(d.MaxValue) ?? 100;
            }
            else
            {
                item["options"] =
                    GetEnumValues(d.EnumValues);
            }

            result[definition.ParamId] = item;
        }

        Dictionary<int, string> madiNames = new();

        if (audio)
        {
            string? audioSource = GetAudioSourceId();

            if (!string.IsNullOrWhiteSpace(audioSource))
            {
                madiNames =
                    await GetMadiNamesAsync(
                        audioSource,
                        cancellationToken);
            }
        }

        return new
        {
            @params = result,
            groups,
            subgroups,
            madiNames
        };
    }

    // ============================================================
    // POLLING VALUES
    // ============================================================

    public async Task<Dictionary<string, object?>> GetValuesAsync(
        bool audio,
        CancellationToken cancellationToken = default)
    {
        var ids = Definitions.Values
            .Where(x => audio
                ? x.ParamId.StartsWith(
                    "eParamID_Audio",
                    StringComparison.Ordinal)
                : x.ParamId.StartsWith(
                    "eParamID_Vid",
                    StringComparison.Ordinal))
            .Select(x => x.ParamId)
            .Distinct()
            .ToArray();

        var output = new Dictionary<string, object?>();

        foreach (string paramId in ids)
        {
            var value =
                await GetValueAsync(
                    paramId,
                    cancellationToken);

            if (value.HasValue)
                output[paramId] =
                    GetScalarValue(value.Value);
        }

        return output;
    }

    // ============================================================
    // MADI
    // ============================================================

    /// <summary>
    /// The supplied PHP uses devices.madisorce as the linked-device ID.
    /// The supplied BMD C# file only showed IpAddress/Name on Device, so
    /// reflection is used here to support the existing AudioSorce field
    /// without inventing a new Database.Device definition.
    /// </summary>
    private string? GetAudioSourceId()
    {
        string[] propertyNames =
        {
            "AudioSorce",
            "AudioSource",
            "madisorce",
            "MadiSorce",
            "MadiSource",
            "Madisorce"
        };

        foreach (string name in propertyNames)
        {
            PropertyInfo? property =
                _device.GetType().GetProperty(
                    name,
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.IgnoreCase);

            if (property == null)
                continue;

            object? value = property.GetValue(_device);

            if (value != null)
                return Convert.ToString(value);
        }

        return null;
    }

    /// <summary>
    /// Preserves the supplied PHP behavior:
    /// /x32/getallnames.php?id=<AudioSorce>
    ///
    /// This means the FS4 page can use the existing X32 name service
    /// instead of duplicating the X32 protocol in this file.
    /// </summary>
    private async Task<Dictionary<int, string>> GetMadiNamesAsync(
        string sourceDeviceId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();

        try
        {
            // This relative URL intentionally matches the supplied PHP.
            using var response =
                await Http.GetAsync(
                    $"/x32/getallnames.php?id=" +
                    Uri.EscapeDataString(sourceDeviceId),
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
                return result;

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using var document =
                JsonDocument.Parse(json);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in
                     document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty(
                        "number",
                        out var number))
                    continue;

                if (!item.TryGetProperty(
                        "name",
                        out var name))
                    continue;

                if (number.TryGetInt32(out int channel))
                    result[channel] = name.ToString();
            }
        }
        catch
        {
            // MADI names are supplemental UI data.
            // Do not prevent the FS4 controls from loading if
            // the linked X32 is unavailable.
        }

        return result;
    }

    // ============================================================
    // JSON HELPERS
    // ============================================================

    private static object? GetScalarValue(
        JsonElement element)
    {
        if (element.ValueKind ==
                JsonValueKind.Undefined ||
            element.ValueKind ==
                JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String =>
                element.GetString(),

            JsonValueKind.Number when
                element.TryGetInt64(out long l) =>
                l,

            JsonValueKind.Number when
                element.TryGetDouble(out double d) =>
                d,

            JsonValueKind.True => true,
            JsonValueKind.False => false,

            _ => element.ToString()
        };
    }

    private static List<object> GetEnumValues(
        JsonElement element)
    {
        var result = new List<object>();

        if (element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                string? value =
                    item.TryGetProperty(
                        "value",
                        out var v)
                        ? v.ToString()
                        : null;

                string? text =
                    item.TryGetProperty(
                        "text",
                        out var t)
                        ? t.ToString()
                        : null;

                result.Add(new
                {
                    value,
                    text
                });
            }
            else
            {
                result.Add(item.ToString());
            }
        }

        return result;
    }



    // ============================================================
    // API ENDPOINTS
    // ============================================================



}
