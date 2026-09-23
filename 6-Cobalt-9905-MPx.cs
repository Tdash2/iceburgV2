using Iceburg.Database;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iceburg.Database;
using System.Diagnostics;
using System;

namespace Iceburg.Conversion.Cobalt.OG9905;

/// <summary>
/// Single-file AJA/Cobalt OG9905 controller.
///
/// IMPORTANT:
/// The OG9905 does NOT use desc.json.
/// All parameter definitions, dropdown options and slider ranges
/// are defined locally in this class.
///
/// Communication:
///     GET http://<IP>:9002/getOid?oid=<PARAM>
///     GET http://<IP>:9002/setOid?oid=<PARAM>&value=<VALUE>
/// </summary>
public sealed class OG9905MPx
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly Dictionary<string, OG9905ParameterDefinition>
        Definitions = BuildDefinitions();

    private static readonly Dictionary<int, string> VideoGroups = new()
    {
        [1] = "Video 1 Processing",
        [2] = "Video 2 Processing",
        [3] = "Video 3 Processing",
        [4] = "Video 4 Processing"
    };

    private static readonly Dictionary<int, string> AudioGroups = new()
    {
        [1] = "Audio 1 Processing",
        [2] = "Audio 2 Processing",
        [3] = "Audio 3 Processing",
        [4] = "Audio 4 Processing"
    };

    private static readonly Dictionary<int, Dictionary<int, string>>
        VideoSubgroups =
            BuildSubgroups(
                "Input Settings",
                "ProcAmp",
                "Red Channel",
                "Green Channel",
                "Blue Channel");

    private static readonly Dictionary<int, Dictionary<int, string>>
        AudioSubgroups =
            BuildSubgroups(
                "Input Settings",
                "Channel Map 1-4",
                "Channel Map 5-8",
                "Channel Map 9-12",
                "Channel Map 13-16");

    private readonly string _id;
    private readonly Device _device;

    // ============================================================
    // PARAMETER MODEL
    // ============================================================

    public enum OG9905ParameterType
    {
        Dropdown,
        Slider
    }

    public sealed class OG9905ParameterOption
    {
        public string Value { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public sealed class OG9905ParameterDefinition
    {
        public string ParamId { get; init; } = "";

        public OG9905ParameterType Type { get; init; }

        public int Group { get; init; }

        public int Subgroup { get; init; }

        public bool Writable { get; init; } = true;

        public double? Min { get; init; }

        public double? Max { get; init; }

        public List<OG9905ParameterOption> Options { get; init; } = new();
    }

    // ============================================================
    // CONSTRUCTOR
    // ============================================================


    public OG9905MPx(string id)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _device = Database.Database.GetDevice(id)
            ?? throw new InvalidOperationException(
                $"Device '{id}' could not be found in the database.");
    }
    // ============================================================
    // PARAMETER DEFINITIONS
    // ============================================================

    private static Dictionary<string, OG9905ParameterDefinition>
        BuildDefinitions()
    {
        var d =
            new Dictionary<string, OG9905ParameterDefinition>(
                StringComparer.Ordinal);

        void Add(
            string id,
            OG9905ParameterType type,
            int group,
            int subgroup,
            bool writable = true,
            IEnumerable<(int Value, string Text)>? options = null,
            double? min = null,
            double? max = null)
        {
            var definition =
                new OG9905ParameterDefinition
                {
                    ParamId = id,
                    Type = type,
                    Group = group,
                    Subgroup = subgroup,
                    Writable = writable,
                    Min = min,
                    Max = max
                };

            if (options != null)
            {
                definition.Options.AddRange(
                    options.Select(x =>
                        new OG9905ParameterOption
                        {
                            Value = x.Value.ToString(
                                CultureInfo.InvariantCulture),
                            Text = x.Text
                        }));
            }

            d[id] = definition;
        }

        // ========================================================
        // AUDIO DROPDOWN OPTIONS
        //
        // These are the values accepted by the OG9905 audio
        // routing parameters.
        // ========================================================

        var audioRoutingOptions =
            BuildAudioRoutingOptions();

        // ========================================================
        // AUDIO PATH 1
        //
        // 6832 = Ch 1
        // 6833 = Ch 2
        // ...
        // 6839 = Ch 8
        // ========================================================

        Add(
            "6832",
            OG9905ParameterType.Dropdown,
            1,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6833",
            OG9905ParameterType.Dropdown,
            1,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6834",
            OG9905ParameterType.Dropdown,
            1,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6835",
            OG9905ParameterType.Dropdown,
            1,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6836",
            OG9905ParameterType.Dropdown,
            1,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6837",
            OG9905ParameterType.Dropdown,
            1,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6838",
            OG9905ParameterType.Dropdown,
            1,
            3,
            true,
            audioRoutingOptions);

        Add("6839",OG9905ParameterType.Dropdown,1,3,true,audioRoutingOptions);

        Add("6840", OG9905ParameterType.Dropdown, 1, 4, true, audioRoutingOptions);
        Add("6841", OG9905ParameterType.Dropdown, 1, 4, true, audioRoutingOptions);
        Add("6842", OG9905ParameterType.Dropdown, 1, 4, true, audioRoutingOptions);
        Add("6843", OG9905ParameterType.Dropdown, 1, 4, true, audioRoutingOptions);

        Add("6844", OG9905ParameterType.Dropdown, 1, 5, true, audioRoutingOptions);
        Add("6845", OG9905ParameterType.Dropdown, 1, 5, true, audioRoutingOptions);
        Add("6846", OG9905ParameterType.Dropdown, 1, 5, true, audioRoutingOptions);
        Add("6847", OG9905ParameterType.Dropdown, 1, 5, true, audioRoutingOptions);

        // ========================================================
        // AUDIO PATH 2
        //
        // 6848 - 6855
        // ========================================================

        Add(
            "6848",
            OG9905ParameterType.Dropdown,
            2,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6849",
            OG9905ParameterType.Dropdown,
            2,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6850",
            OG9905ParameterType.Dropdown,
            2,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6851",
            OG9905ParameterType.Dropdown,
            2,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6852",
            OG9905ParameterType.Dropdown,
            2,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6853",
            OG9905ParameterType.Dropdown,
            2,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6854",
            OG9905ParameterType.Dropdown,
            2,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6855",
            OG9905ParameterType.Dropdown,
            2,
            3,
            true,
            audioRoutingOptions);


        Add("6856", OG9905ParameterType.Dropdown, 2, 4, true, audioRoutingOptions);
        Add("6857", OG9905ParameterType.Dropdown, 2, 4, true, audioRoutingOptions);
        Add("6858", OG9905ParameterType.Dropdown, 2, 4, true, audioRoutingOptions);
        Add("6859", OG9905ParameterType.Dropdown, 2, 4, true, audioRoutingOptions);

        Add("6860", OG9905ParameterType.Dropdown, 2, 5, true, audioRoutingOptions);
        Add("6861", OG9905ParameterType.Dropdown, 2, 5, true, audioRoutingOptions);
        Add("6862", OG9905ParameterType.Dropdown, 2, 5, true, audioRoutingOptions);
        Add("6863", OG9905ParameterType.Dropdown, 2, 5, true, audioRoutingOptions);

        // ========================================================
        // AUDIO PATH 3
        //
        // 6864 - 6871
        // ========================================================

        Add(
            "6864",
            OG9905ParameterType.Dropdown,
            3,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6865",
            OG9905ParameterType.Dropdown,
            3,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6866",
            OG9905ParameterType.Dropdown,
            3,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6867",
            OG9905ParameterType.Dropdown,
            3,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6868",
            OG9905ParameterType.Dropdown,
            3,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6869",
            OG9905ParameterType.Dropdown,
            3,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6870",
            OG9905ParameterType.Dropdown,
            3,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6871",
            OG9905ParameterType.Dropdown,
            3,
            3,
            true,
            audioRoutingOptions);

        Add("6872", OG9905ParameterType.Dropdown, 3, 4, true, audioRoutingOptions);
        Add("6873", OG9905ParameterType.Dropdown, 3, 4, true, audioRoutingOptions);
        Add("6874", OG9905ParameterType.Dropdown, 3, 4, true, audioRoutingOptions);
        Add("6875", OG9905ParameterType.Dropdown, 3, 4, true, audioRoutingOptions);

        Add("6876", OG9905ParameterType.Dropdown, 3, 5, true, audioRoutingOptions);
        Add("6877", OG9905ParameterType.Dropdown, 3, 5, true, audioRoutingOptions);
        Add("6878", OG9905ParameterType.Dropdown, 3, 5, true, audioRoutingOptions);
        Add("6879", OG9905ParameterType.Dropdown, 3, 5, true, audioRoutingOptions);
        // ========================================================
        // AUDIO PATH 4
        //
        // 6880 - 6887
        // ========================================================

        Add(
            "6880",
            OG9905ParameterType.Dropdown,
            4,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6881",
            OG9905ParameterType.Dropdown,
            4,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6882",
            OG9905ParameterType.Dropdown,
            4,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6883",
            OG9905ParameterType.Dropdown,
            4,
            2,
            true,
            audioRoutingOptions);

        Add(
            "6884",
            OG9905ParameterType.Dropdown,
            4,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6885",
            OG9905ParameterType.Dropdown,
            4,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6886",
            OG9905ParameterType.Dropdown,
            4,
            3,
            true,
            audioRoutingOptions);

        Add(
            "6887",
            OG9905ParameterType.Dropdown,
            4,
            3,
            true,
            audioRoutingOptions);


        Add("6888", OG9905ParameterType.Dropdown, 4, 4, true, audioRoutingOptions);
        Add("6889", OG9905ParameterType.Dropdown, 4, 4, true, audioRoutingOptions);
        Add("6890", OG9905ParameterType.Dropdown, 4, 4, true, audioRoutingOptions);
        Add("6891", OG9905ParameterType.Dropdown, 4, 4, true, audioRoutingOptions);

        Add("6892", OG9905ParameterType.Dropdown, 4, 5, true, audioRoutingOptions);
        Add("6893", OG9905ParameterType.Dropdown, 4, 5, true, audioRoutingOptions);
        Add("6894", OG9905ParameterType.Dropdown, 4, 5, true, audioRoutingOptions);
        Add("6895", OG9905ParameterType.Dropdown, 4, 5, true, audioRoutingOptions);

        // ========================================================
        // VIDEO
        //
        // Video parameter IDs can remain as their eParamID names
        // because these are the IDs used by the OG9905 API.
        //
        // Their options can also be added here once the actual
        // OG9905 video enum values are supplied.
        // ========================================================



        return d;
    }

    // ============================================================
    // RGB VIDEO HELPERS
    // ============================================================

   

    // ============================================================
    // AUDIO OPTION MAP
    // ============================================================

    private static List<(int Value, string Text)>
        BuildAudioRoutingOptions()
    {
        var options =
            new List<(int Value, string Text)>();

        // --------------------------------------------------------
        // Path 1 EB
        // --------------------------------------------------------

        for (int i = 0; i < 16; i++)
        {
            options.Add(
                (
                    i,
                    $"Path 1 EB Ch {i + 1}"
                ));
        }

        // --------------------------------------------------------
        // Path 2 EB
        // --------------------------------------------------------

        for (int i = 0; i < 16; i++)
        {
            options.Add(
                (
                    16 + i,
                    $"Path 2 EB Ch {i + 1}"
                ));
        }

        // --------------------------------------------------------
        // Path 3 EB
        // --------------------------------------------------------

        for (int i = 0; i < 16; i++)
        {
            options.Add(
                (
                    32 + i,
                    $"Path 3 EB Ch {i + 1}"
                ));
        }

        // --------------------------------------------------------
        // Path 4 EB
        // --------------------------------------------------------

        for (int i = 0; i < 16; i++)
        {
            options.Add(
                (
                    48 + i,
                    $"Path 4 EB Ch {i + 1}"
                ));
        }

        // --------------------------------------------------------
        // MADI RX 1-32
        // --------------------------------------------------------

        for (int i = 0; i < 32; i++)
        {
            options.Add(
                (
                    128 + i,
                    $"MADI RX {i + 1}"
                ));
        }

        // --------------------------------------------------------
        // Silence
        // --------------------------------------------------------

        options.Add(
            (
                4080,
                "Silence"
            ));

        return options;
    }

    // ============================================================
    // SUBGROUPS
    // ============================================================

    private static Dictionary<int, Dictionary<int, string>>
        BuildSubgroups(
            string s1,
            string s2,
            string s3,
            string s4,
            string s5)
    {
        var result =
            new Dictionary<int, Dictionary<int, string>>();

        for (int group = 1; group <= 4; group++)
        {
            result[group] =
                new Dictionary<int, string>
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
        return JsonSerializer.Serialize(
            new
            {
                IP = _device.IpAddress,
                Name = _device.Name,
                Id = _id
            });
    }

    // ============================================================
    // BASE URL
    // ============================================================

    private string BaseUrl =>
        $"http://{_device.IpAddress}";


    // ============================================================
    // GET VALUE
    // ============================================================

  
private async Task<JsonElement?> GetValueAsync(
    string paramId,
    CancellationToken cancellationToken)
    {
        if (_device == null)
        {
            

            throw new InvalidOperationException(
                "OG9905 device is not initialized.");
        }

        if (!int.TryParse(paramId, out var oid))
        {
          

            throw new ArgumentException(
                $"OG9905 parameter ID '{paramId}' is not numeric.",
                nameof(paramId));
        }

        var url =
            $"{BaseUrl}:9002/getOid?oid={Uri.EscapeDataString(paramId)}";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        

        try
        {
            using var request = new HttpRequestMessage(
     HttpMethod.Get,
     url);

            request.Headers.ConnectionClose = true;

            using var response =
                await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);

            stopwatch.Stop();

       



            var body =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);


            if (!response.IsSuccessStatusCode)
            {
               

                throw new HttpRequestException(
                    $"OG9905 returned HTTP {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase} for OID {oid}. " +
                    $"Response: {body}");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
               

                return null;
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                

                Console.WriteLine(
                    $"[OG9905]     Exception: {ex}");

                throw;
            }

            using (document)
            {
                var json = document.RootElement;

      

                if (!json.TryGetProperty(
                        "data_updates",
                        out var dataUpdates))
                {
                   

                    throw new InvalidOperationException(
                        $"OG9905 response for OID {oid} " +
                        $"did not contain 'data_updates'. " +
                        $"Response: {body}");
                }

                if (!dataUpdates.TryGetProperty(
                        "oids",
                        out var oids))
                {
                  

                    throw new InvalidOperationException(
                        $"OG9905 response for OID {oid} " +
                        $"did not contain 'oids'. " +
                        $"Response: {body}");
                }

                foreach (var item in oids.EnumerateArray())
                {
                    if (!item.TryGetProperty(
                            "oid",
                            out var returnedOid))
                    {
                        
                        continue;
                    }

                    var returnedOidValue =
                        returnedOid.GetInt32();

                 

                    if (returnedOidValue != oid)
                        continue;

                    if (!item.TryGetProperty(
                            "data_value",
                            out var dataValue))
                    {
                      

                        throw new InvalidOperationException(
                            $"OG9905 response for OID {oid} " +
                            $"did not contain 'data_value'. " +
                            $"Response: {body}");
                    }

                 
                        

                    return dataValue.Clone();
                }

                Console.WriteLine(
                    $"[OG9905] WARNING: Requested OID {oid} " +
                    $"was not found in the response.");

                return null;
            }
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();

           

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            

            if (ex.InnerException != null)
            {
                
            }

            


           

            throw;
        }
    }




    // ============================================================
    // SET VALUE
    // ============================================================

    private async Task<string>
        SetValueAsync(
            string paramId,
            string value,
            CancellationToken cancellationToken = default)
    {
        string url =
            $"{BaseUrl}:9002/setOid?oid=" +
            Uri.EscapeDataString(paramId) +
            "&value=" +
            Uri.EscapeDataString(value);

        using var response =
            await Http.GetAsync(
                url,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OG9905 rejected {paramId}: " +
                $"HTTP {(int)response.StatusCode} {body}");
        }

        return body;
    }

    // ============================================================
    // PARAMETER VALIDATION
    // ============================================================

    public async Task<(bool Allowed, string? Error)>
        CanSetParamAsync(
            string paramId,
            object? value = null,
            CancellationToken cancellationToken = default)
    {
        if (!Definitions.TryGetValue(
                paramId,
                out var definition))
        {
            return (
                false,
                "invalid param");
        }

        if (!definition.Writable)
        {
            return (
                false,
                "parameter is read-only");
        }

        // --------------------------------------------------------
        // No value means caller only wants to know whether the
        // parameter exists and is writable.
        // --------------------------------------------------------

        if (value == null)
        {
            return (
                true,
                null);
        }

        string candidate =
            Convert.ToString(
                value,
                CultureInfo.InvariantCulture) ?? "";

        // --------------------------------------------------------
        // SLIDER
        // --------------------------------------------------------

        if (definition.Type ==
            OG9905ParameterType.Slider)
        {
            if (!double.TryParse(
                    candidate,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number))
            {
                return (
                    false,
                    "invalid numeric value");
            }

            if (definition.Min.HasValue &&
                number < definition.Min.Value)
            {
                return (
                    false,
                    $"value below minimum " +
                    $"{definition.Min.Value}");
            }

            if (definition.Max.HasValue &&
                number > definition.Max.Value)
            {
                return (
                    false,
                    $"value above maximum " +
                    $"{definition.Max.Value}");
            }
        }

        // --------------------------------------------------------
        // DROPDOWN
        // --------------------------------------------------------

        if (definition.Type ==
            OG9905ParameterType.Dropdown)
        {
            // If options were explicitly supplied, validate them.
            //
            // Parameters such as the video format controls may
            // temporarily have an empty option list until their
            // actual OG9905 enum values are added.
            if (definition.Options.Count > 0)
            {
                bool found =
                    definition.Options.Any(
                        option =>
                            string.Equals(
                                option.Value,
                                candidate,
                                StringComparison.OrdinalIgnoreCase));

                if (!found)
                {
                    return (
                        false,
                        "value is not one of the " +
                        "allowed enum values");
                }
            }
        }

        return (
            true,
            null);
    }

    // ============================================================
    // GET PARAM
    // ============================================================

    public async Task<object>
        GetParamAsync(
            string paramId,
            CancellationToken cancellationToken = default)
    {
        if (!Definitions.TryGetValue(
                paramId,
                out var definition))
        {
            return new
            {
                ok = false,
                error = "invalid param"
            };
        }

        JsonElement? value =
            await GetValueAsync(
                paramId,
                cancellationToken);

        return new
        {
            ok = value.HasValue,
            param = paramId,

            value =
                value.HasValue
                    ? GetScalarValue(value.Value)
                    : null,

            writable =
                definition.Writable
        };
    }

    // ============================================================
    // SET PARAM
    // ============================================================

    public async Task<object>
        SetParamAsync(
            string paramId,
            object? value,
            CancellationToken cancellationToken = default)
    {
        var check =
            await CanSetParamAsync(
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
                CultureInfo.InvariantCulture) ?? "";

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
    // CONFIGURATION
    // ============================================================

  
public async Task<object> GetConfigurationAsync(
    bool audio,
    CancellationToken cancellationToken = default)
    {
        var groups =
            audio
                ? AudioGroups
                : VideoGroups;

        var subgroups =
            audio
                ? AudioSubgroups
                : VideoSubgroups;

        var result =
            new Dictionary<string, object?>();

        IEnumerable<OG9905ParameterDefinition> definitions =
            Definitions.Values
                .Where(
                    x =>
                        audio
                            ? IsAudioParameter(x.ParamId)
                            : IsVideoParameter(x.ParamId))
                .OrderBy(x => x.Group)
                .ThenBy(x => x.Subgroup)
                .ThenBy(x => x.ParamId);

        foreach (var definition in definitions)
        {
            var item =
                new Dictionary<string, object?>
                {
                    ["type"] =
                        definition.Type ==
                        OG9905ParameterType.Slider
                            ? "slider"
                            : "dropdown",

                    ["group"] =
                        definition.Group,

                    ["subgroup"] =
                        definition.Subgroup,

                    ["name"] =
                        GetDisplayName(
                            definition.ParamId),

                    ["writable"] =
                        definition.Writable
                };

            // ----------------------------------------------------
            // Read current value from the OG9905
            // ----------------------------------------------------

            await Task.Delay(8, cancellationToken);

            try
            {
                JsonElement? value =
                    await GetValueAsync(
                        definition.ParamId,
                        cancellationToken);

                item["value"] =
                    value.HasValue
                        ? GetScalarValue(value.Value)
                        : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[OG9905 CONFIG] Failed to read " +
                    $"OID {definition.ParamId}: {ex}");

                // Keep configuration alive even if
                // one OID fails.
                item["value"] = null;
            }

            // ----------------------------------------------------
            // SLIDER
            // ----------------------------------------------------

            if (definition.Type ==
                OG9905ParameterType.Slider)
            {
                item["min"] =
                    definition.Min ?? 0;

                item["max"] =
                    definition.Max ?? 100;
            }

            // ----------------------------------------------------
            // DROPDOWN
            // ----------------------------------------------------

            else
            {
                item["options"] =
                    definition.Options
                        .Select(
                            x => new
                            {
                                value = x.Value,
                                text = x.Text
                            })
                        .ToList();
            }

            result[definition.ParamId] =
                item;
        }

        return new
        {
            @params = result,
            groups,
            subgroups
        };
    }



    // ============================================================
    // POLLING VALUES
    // ============================================================

    public async Task<Dictionary<string, object?>>
        GetValuesAsync(
            bool audio,
            CancellationToken cancellationToken = default)
    {
        var ids =
            Definitions.Values
                .Where(
                    x =>
                        audio
                            ? IsAudioParameter(x.ParamId)
                            : IsVideoParameter(x.ParamId))
                .Select(x => x.ParamId)
                .Distinct()
                .ToArray();

        var output =
            new Dictionary<string, object?>();

        foreach (string paramId in ids)
        {
            var value =
                await GetValueAsync(
                    paramId,
                    cancellationToken);

            if (value.HasValue)
            {
                output[paramId] =
                    GetScalarValue(value.Value);
            }
            await Task.Delay(100, cancellationToken);
        }

        return output;
    }

    // ============================================================
    // PARAMETER CATEGORY HELPERS
    // ============================================================

    private static bool
        IsAudioParameter(
            string paramId)
    {
        // The OG9905 audio routing parameters are numeric OIDs.
        return int.TryParse(
                   paramId,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int oid)
               &&
               (
                   oid >= 6832 &&
                   oid <= 6895
               );
    }

    private static bool
        IsVideoParameter(
            string paramId)
    {
        return paramId.StartsWith(
                   "eParamID_Vid",
                   StringComparison.Ordinal);
    }

    // ============================================================
    // DISPLAY NAMES
    // ============================================================

    private static string
        GetDisplayName(
            string paramId)
    {
        if (int.TryParse(
                paramId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int oid))
        {
            if (TryGetAudioChannelName(
                    oid,
                    out string? name))
            {
                return name;
            }
        }

        return paramId;
    }

    private static bool
        TryGetAudioChannelName(
            int oid,
            out string? name)
    {
        name = null;

        if (oid >= 6832 && oid <= 6847)
        {
            name =
                $"Audio Path 1 - Ch {oid - 6832 + 1}";

            return true;
        }

        if (oid >= 6848 && oid <= 6863)
        {
            name =
                $"Audio Path 2 - Ch {oid - 6848 + 1}";

            return true;
        }

        if (oid >= 6864 && oid <= 6871)
        {
            name =
                $"Audio Path 3 - Ch {oid - 6864 + 1}";

            return true;
        }

        if (oid >= 6880 && oid <= 6887)
        {
            name =
                $"Audio Path 4 - Ch {oid - 6880 + 1}";

            return true;
        }

        return false;
    }

    // ============================================================
    // JSON HELPERS
    // ============================================================

    private static object?
        GetScalarValue(
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
                element.TryGetInt64(
                    out long l) =>
                l,

            JsonValueKind.Number when
                element.TryGetDouble(
                    out double d) =>
                d,

            JsonValueKind.True =>
                true,

            JsonValueKind.False =>
                false,

            _ =>
                element.ToString()
        };
    }
}

