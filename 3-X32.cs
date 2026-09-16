using Iceburg.Database;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Iceburg.Mixer.X32;

public class X32
{
    private const int OscPort = 10023;
    private const int ChannelCount = 32;
    private const int BusCount = 16;

    private sealed class OscValue
    {
        public char Type { get; init; }
        public object? Value { get; init; }
    }

    // ---------------------------------------------------------------------
    // Device information
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // BUS MIX
    //
    // Ported from bus.php:
    //   - 16 mix buses
    //   - 32 channel sends per selected bus
    //   - send level in dB (-90 .. +10)
    //   - send mute
    //   - channel names
    //   - bus names
    // ---------------------------------------------------------------------

    public async Task<string> getbus(string id, int bus = 1)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (bus < 1 || bus > BusCount)
            return JsonSerializer.Serialize(new
            {
                error = "Bus must be between 1 and 16"
            });

        try
        {
            string busName = await GetStringAsync(
                device.IpAddress,
                $"/bus/{bus:00}/config/name") ?? "";

            if (string.IsNullOrWhiteSpace(busName))
                busName = $"Mix Bus {bus}";

            var channels = new List<object>();

            for (int ch = 1; ch <= ChannelCount; ch++)
            {
                float? fader = await GetFloatAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/mix/{bus:00}/level");

                bool? mute = await GetMuteAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/mix/{bus:00}/on");

                string name = await GetStringAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/config/name") ?? "";

                channels.Add(new
                {
                    channel = ch,
                    name = string.IsNullOrWhiteSpace(name)
                        ? $"CH {ch}"
                        : name,
                    gain = fader.HasValue
                        ? Math.Round(FaderToDb(fader.Value), 1)
                        : (double?)null,
                    min = -90,
                    max = 10,
                    mute
                });
            }

            return JsonSerializer.Serialize(new
            {
                bus,
                bus_name = busName,
                channels
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            });
        }
    }

    public async Task<string> getbusnames(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            var buses = new List<object>();

            for (int bus = 1; bus <= BusCount; bus++)
            {
                string name = await GetStringAsync(
                    device.IpAddress,
                    $"/bus/{bus:00}/config/name") ?? "";

                buses.Add(new
                {
                    bus,
                    name = string.IsNullOrWhiteSpace(name)
                        ? $"Mix Bus {bus}"
                        : name
                });
            }

            return JsonSerializer.Serialize(buses);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            });
        }
    }

    public async Task<string> setbus(
        string id,
        int channel,
        int bus,
        double? gain = null,
        bool? mute = null,
        string? name = null)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (channel < 1 || channel > ChannelCount)
            return JsonSerializer.Serialize(new { error = "Channel must be between 1 and 32" });

        if (bus < 1 || bus > BusCount)
            return JsonSerializer.Serialize(new { error = "Bus must be between 1 and 16" });

        try
        {
            if (gain.HasValue)
            {
                double clamped = Math.Clamp(gain.Value, -90.0, 10.0);
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/mix/{bus:00}/level",
                    'f',
                    DbToFader(clamped));
            }

            if (mute.HasValue)
            {
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/mix/{bus:00}/on",
                    'i',
                    mute.Value ? 0 : 1);
            }

            if (name != null)
            {
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/config/name",
                    's',
                    LimitName(name));
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                channel,
                bus,
                gain,
                mute,
                name
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ---------------------------------------------------------------------
    // MAIN MIX
    //
    // Ported from mainmix.php:
    //   - 32 channel main faders
    //   - -90 .. +10 dB
    //   - mute
    //   - channel names
    // ---------------------------------------------------------------------

    public async Task<string> getmainmix(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            var channels = new List<object>();

            for (int ch = 1; ch <= ChannelCount; ch++)
            {
                float? fader = await GetFloatAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/mix/fader");

                bool? mute = await GetMuteAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/mix/on");

                string name = await GetStringAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/config/name") ?? "";

                channels.Add(new
                {
                    channel = ch,
                    name = string.IsNullOrWhiteSpace(name)
                        ? $"CH {ch}"
                        : name,
                    gain = fader.HasValue
                        ? Math.Round(FaderToDb(fader.Value), 1)
                        : (double?)null,
                    min = -90,
                    max = 10,
                    mute
                });
            }

            return JsonSerializer.Serialize(channels);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            });
        }
    }

    public async Task<string> setmainmix(
        string id,
        int channel,
        double? gain = null,
        bool? mute = null,
        string? name = null)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (channel < 1 || channel > ChannelCount)
            return JsonSerializer.Serialize(new { error = "Channel must be between 1 and 32" });

        try
        {
            if (gain.HasValue)
            {
                double clamped = Math.Clamp(gain.Value, -90.0, 10.0);

                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/mix/fader",
                    'f',
                    DbToFader(clamped));
            }

            if (mute.HasValue)
            {
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/mix/on",
                    'i',
                    mute.Value ? 0 : 1);
            }

            if (name != null)
            {
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/config/name",
                    's',
                    LimitName(name));
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                channel,
                gain,
                mute,
                name
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ---------------------------------------------------------------------
    // INPUT CONFIG
    //
    // Ported from inputs.php:
    //   - source 0..32 uses HA gain
    //   - other sources use channel trim
    //   - HA range -12 .. +60 dB
    //   - trim range -18 .. +18 dB
    //   - channel names
    // ---------------------------------------------------------------------

    public async Task<string> getinputs(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            var channels = new Dictionary<int, object?>();

            for (int ch = 1; ch <= ChannelCount; ch++)
            {
                int? source = await GetIntAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/config/source");

                bool useHa = source.HasValue && source.Value >= 0 && source.Value <= 32;

                double? gain;

                if (useHa)
                {
                    float? normalized = await GetFloatAsync(
                        device.IpAddress,
                        $"/headamp/{ch - 1:000}/gain");

                    gain = normalized.HasValue
                        ? Math.Round(NormalizedToDb(normalized.Value, -12, 60), 1)
                        : null;
                }
                else
                {
                    float? normalized = await GetFloatAsync(
                        device.IpAddress,
                        $"/ch/{ch:00}/preamp/trim");

                    gain = normalized.HasValue
                        ? Math.Round(NormalizedToDb(normalized.Value, -18, 18), 1)
                        : null;
                }

                string name = await GetStringAsync(
                    device.IpAddress,
                    $"/ch/{ch:00}/config/name") ?? "";

                channels[ch] = new
                {
                    name,
                    mode = useHa ? "HA" : "TRIM",
                    gain,
                    min = useHa ? -12 : -18,
                    max = useHa ? 60 : 18,
                    source
                };
            }

            return JsonSerializer.Serialize(new
            {
                channels
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            });
        }
    }

    public async Task<string> setinput(
        string id,
        int channel,
        double gain,
        string? name = null)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (channel < 1 || channel > ChannelCount)
            return JsonSerializer.Serialize(new { error = "Channel must be between 1 and 32" });

        try
        {
            int? source = await GetIntAsync(
                device.IpAddress,
                $"/ch/{channel:00}/config/source");

            bool useHa = source.HasValue && source.Value >= 0 && source.Value <= 32;

            if (useHa)
            {
                double clamped = Math.Clamp(gain, -12.0, 60.0);

                await SendAsync(
                    device.IpAddress,
                    $"/headamp/{channel - 1:000}/gain",
                    'f',
                    DbToNormalized(clamped, -12, 60));
            }
            else
            {
                double clamped = Math.Clamp(gain, -18.0, 18.0);

                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/preamp/trim",
                    'f',
                    DbToNormalized(clamped, -18, 18));
            }

            if (name != null)
            {
                await SendAsync(
                    device.IpAddress,
                    $"/ch/{channel:00}/config/name",
                    's',
                    LimitName(name));
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                channel,
                mode = useHa ? "HA" : "TRIM",
                gain,
                name
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ---------------------------------------------------------------------
    // ALL USER OUTPUT NAMES / ROUTING
    //
    // Ported from getallnames.php:
    //   - user outputs 1..32
    //   - P16 source resolution
    //   - MAIN output source resolution
    //   - channel and bus names
    // ---------------------------------------------------------------------

    public async Task<string> getallnames(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            var userOutputs = await GetUserOutputsAsync(device.IpAddress);
            var currentP16 = await GetOutputSourcesAsync(
                device.IpAddress, "/outputs/p16/{0:00}/src", 16);
            var currentMain = await GetOutputSourcesAsync(
                device.IpAddress, "/outputs/main/{0:00}/src", 16);

            var sources = await GetSourcesAsync(device.IpAddress);

            var enumToLabel = sources.ToDictionary(
                x => x.Enum,
                x => x.Label);

            var outputs = new List<object>();

            foreach (var item in userOutputs.OrderBy(x => x.Key))
            {
                int value = item.Value;
                string name = UserOutputName(value, device.Name);

                // P16 / Ultranet output: resolve its actual source.
                if (value >= 185 && value <= 200)
                {
                    int p16Number = value - 184;

                    if (currentP16.TryGetValue(p16Number, out int sourceEnum) &&
                        enumToLabel.TryGetValue(sourceEnum, out string? sourceName))
                    {
                        name = sourceName;
                    }
                }

                // Main output: resolve its actual source.
                if (value >= 169 && value <= 184)
                {
                    int mainNumber = value - 168;

                    if (currentMain.TryGetValue(mainNumber, out int sourceEnum) &&
                        enumToLabel.TryGetValue(sourceEnum, out string? sourceName))
                    {
                        name = sourceName;
                    }
                }

                outputs.Add(new
                {
                    number = item.Key,
                    name,
                    value
                });
            }

            return JsonSerializer.Serialize(outputs);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = ex.Message
            });
        }
    }

    // ---------------------------------------------------------------------
    // OSC
    // ---------------------------------------------------------------------

    private static async Task SendAsync(
        string host,
        string path,
        char? type = null,
        object? value = null)
    {
        using UdpClient client = CreateClient(host);

        // Same initialization used by the PHP files.
        byte[] remote = BuildOscMessage("/xremote");
        await client.SendAsync(remote, remote.Length);

        byte[] message = BuildOscMessage(path, type, value);
        await client.SendAsync(message, message.Length);
    }

    private static async Task<byte[]?> QueryAsync(
        string host,
        string expectedPath,
        char? expectedType = null,
        int timeoutMs = 80)
    {
        using UdpClient client = CreateClient(host);

        // Match the PHP code's /xremote initialization.
        byte[] remote = BuildOscMessage("/xremote");
        await client.SendAsync(remote, remote.Length);

        byte[] message = BuildOscMessage(expectedPath);
        await client.SendAsync(message, message.Length);

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            int remaining = Math.Max(
                1,
                (int)(deadline - DateTime.UtcNow).TotalMilliseconds);

            Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
            Task completed = await Task.WhenAny(
                receiveTask,
                Task.Delay(remaining));

            if (completed != receiveTask)
                break;

            UdpReceiveResult result = await receiveTask;

            if (!TryParseOsc(result.Buffer, out string? address, out string? types, out int dataOffset))
                continue;

            // Ignore asynchronous packets from /xremote that are not
            // the value we just requested.
            if (!string.Equals(address, expectedPath, StringComparison.Ordinal))
                continue;

            if (expectedType.HasValue &&
                (types == null || !types.Contains(expectedType.Value)))
                continue;

            return result.Buffer;
        }

        return null;
    }

    private static UdpClient CreateClient(string host)
    {
        IPAddress[] addresses = Dns.GetHostAddresses(host);

        IPAddress address = addresses.FirstOrDefault(
            x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException(
                $"Could not resolve X32 address: {host}");

        UdpClient client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(new IPEndPoint(address, OscPort));
        return client;
    }

    private static byte[] BuildOscMessage(
        string path,
        char? type = null,
        object? value = null)
    {
        using MemoryStream ms = new MemoryStream();

        WriteOscString(ms, path);

        if (!type.HasValue)
            return ms.ToArray();

        WriteOscString(ms, "," + type.Value);

        switch (type.Value)
        {
            case 'i':
                WriteInt32BigEndian(ms, Convert.ToInt32(value));
                break;

            case 'f':
                WriteFloatBigEndian(ms, Convert.ToSingle(value));
                break;

            case 's':
                WriteOscString(ms, Convert.ToString(value) ?? "");
                break;

            default:
                throw new NotSupportedException(
                    $"OSC type '{type.Value}' is not supported.");
        }

        return ms.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);

        int padding = (4 - ((bytes.Length + 1) % 4)) % 4;

        for (int i = 0; i < padding; i++)
            stream.WriteByte(0);
    }

    private static void WriteInt32BigEndian(Stream stream, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteFloatBigEndian(Stream stream, float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool TryParseOsc(
        byte[] packet,
        out string? address,
        out string? types,
        out int dataOffset)
    {
        address = null;
        types = null;
        dataOffset = 0;

        if (!TryReadOscString(packet, 0, out address, out int addressEnd))
            return false;

        if (!TryReadOscString(packet, addressEnd, out types, out dataOffset))
        {
            // A message such as /xremote has no typetag.
            dataOffset = addressEnd;
            types = null;
        }

        return true;
    }

    private static bool TryReadOscString(
        byte[] data,
        int offset,
        out string? value,
        out int nextOffset)
    {
        value = null;
        nextOffset = offset;

        if (offset < 0 || offset >= data.Length)
            return false;

        int end = Array.IndexOf(data, (byte)0, offset);

        if (end < 0)
            return false;

        value = Encoding.ASCII.GetString(
            data,
            offset,
            end - offset);

        int lengthIncludingNull = end - offset + 1;
        int paddedLength = (lengthIncludingNull + 3) / 4 * 4;

        nextOffset = offset + paddedLength;

        return nextOffset <= data.Length;
    }

    private static bool TryGetOscValue(
    byte[] packet,
    char requestedType,
    out object? value)
    {
        value = null;

        if (!TryParseOsc(
            packet,
            out _,
            out string? types,
            out int dataOffset))
        {
            return false;
        }

        if (string.IsNullOrEmpty(types))
            return false;

        // OSC type tags begin with ','.
        // For the X32 responses we expect:
        //     ",f" = float
        //     ",i" = integer
        //     ",s" = string
        if (types.Length < 2 || types[0] != ',')
            return false;

        if (types[1] != requestedType)
            return false;

        switch (requestedType)
        {
            case 'i':
                if (dataOffset + 4 > packet.Length)
                    return false;

                value = ReadInt32BigEndian(packet, dataOffset);
                return true;

            case 'f':
                if (dataOffset + 4 > packet.Length)
                    return false;

                value = ReadFloatBigEndian(packet, dataOffset);
                return true;

            case 's':
                if (!TryReadOscString(
                    packet,
                    dataOffset,
                    out string? text,
                    out _))
                {
                    return false;
                }

                value = text;
                return true;

            default:
                return false;
        }
    }

    private static int ReadInt32BigEndian(byte[] data, int offset)
    {
        byte[] bytes = data[offset..(offset + 4)];

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return BitConverter.ToInt32(bytes, 0);
    }

    private static float ReadFloatBigEndian(byte[] data, int offset)
    {
        byte[] bytes = data[offset..(offset + 4)];

        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return BitConverter.ToSingle(bytes, 0);
    }

    public static async Task<int?> GetIntAsync(string host, string path)
    {
        byte[]? response = await QueryAsync(host, path, 'i');

        if (response == null)
            return null;

        return TryGetOscValue(response, 'i', out object? value)
            ? Convert.ToInt32(value)
            : null;
    }

    private static async Task<float?> GetFloatAsync(string host, string path)
    {
        byte[]? response = await QueryAsync(host, path, 'f');

        if (response == null)
            return null;

        return TryGetOscValue(response, 'f', out object? value)
            ? Convert.ToSingle(value)
            : null;
    }

    private static async Task<string?> GetStringAsync(string host, string path)
    {
        byte[]? response = await QueryAsync(host, path, 's');

        if (response == null)
            return null;

        return TryGetOscValue(response, 's', out object? value)
            ? Convert.ToString(value)
            : null;
    }

    private static async Task<bool?> GetMuteAsync(string host, string path)
    {
        int? value = await GetIntAsync(host, path);

        return value.HasValue ? value.Value == 0 : null;
    }

    // ---------------------------------------------------------------------
    // X32 fader conversions
    // ---------------------------------------------------------------------

    private static double FaderToDb(double f)
    {
        if (f <= 0.0)
            return -90.0;

        if (f <= 0.0625)
            return -90.0 + (f / 0.0625) * 30.0;

        if (f <= 0.25)
            return -60.0 + ((f - 0.0625) / (0.25 - 0.0625)) * 30.0;

        if (f <= 0.5)
            return -30.0 + ((f - 0.25) / (0.5 - 0.25)) * 20.0;

        return -10.0 + ((f - 0.5) / (1.0 - 0.5)) * 20.0;
    }

    private static double DbToFader(double db)
    {
        if (db <= -60.0)
            return Math.Max(0.0, (db + 90.0) / 30.0 * 0.0625);

        if (db <= -30.0)
            return 0.0625 + ((db + 60.0) / 30.0) * (0.25 - 0.0625);

        if (db <= -10.0)
            return 0.25 + ((db + 30.0) / 20.0) * (0.5 - 0.25);

        if (db <= 10.0)
            return 0.5 + ((db + 10.0) / 20.0) * (1.0 - 0.5);

        return 1.0;
    }

    private static double NormalizedToDb(
        double normalized,
        double min,
        double max)
    {
        return normalized * (max - min) + min;
    }

    private static double DbToNormalized(
        double db,
        double min,
        double max)
    {
        return (db - min) / (max - min);
    }

    private static string LimitName(string name)
    {
        return name.Length <= 12 ? name : name[..12];
    }

    // ---------------------------------------------------------------------
    // getallnames helpers
    // ---------------------------------------------------------------------

    private static async Task<Dictionary<int, int>> GetUserOutputsAsync(
        string host)
    {
        var outputs = new Dictionary<int, int>();

        for (int i = 1; i <= 32; i++)
        {
            int? value = await GetIntAsync(
                host,
                $"/config/userrout/out/{i:00}");

            outputs[i] = value ?? 0;
        }

        return outputs;
    }

    private static async Task<Dictionary<int, int>> GetOutputSourcesAsync(
        string host,
        string pathFormat,
        int count)
    {
        var result = new Dictionary<int, int>();

        for (int i = 1; i <= count; i++)
        {
            int? value = await GetIntAsync(
                host,
                string.Format(pathFormat, i));

            result[i] = value ?? 0;
        }

        return result;
    }

    private sealed record SourceInfo(string Label, int Enum);

    private static async Task<List<SourceInfo>> GetSourcesAsync(string host)
    {
        var sources = new List<SourceInfo>
        {
            new("Silence", 0),
            new("MAIN L", 1),
            new("MAIN R", 2),
            new("MC", 3)
        };

        for (int i = 1; i <= ChannelCount; i++)
        {
            string path = $"/ch/{i:00}/config/name";
            string? name = await GetStringAsync(host, path);

            string label = !string.IsNullOrWhiteSpace(name)
                ? $"DO: {name.Trim()}"
                : $"DO: CH {i}";

            sources.Add(new SourceInfo(label, 26 + (i - 1)));
        }

        for (int i = 1; i <= BusCount; i++)
        {
            string path = $"/bus/{i:00}/config/name";
            string? name = await GetStringAsync(host, path);

            string label = !string.IsNullOrWhiteSpace(name)
                ? $"DO: {name.Trim()}"
                : $"Bus: Bus {i}";

            sources.Add(new SourceInfo(label, 4 + (i - 1)));
        }

        return sources;
    }

    private static string UserOutputName(int value, string deviceName)
    {
        if (value == 0) return "OFF";
        if (value >= 1 && value <= 32) return $"{deviceName} XLR In {value}";
        if (value >= 33 && value <= 80) return $"AES50-A {value - 32}";
        if (value >= 81 && value <= 128) return $"AES50-B {value - 80}";
        if (value >= 129 && value <= 160) return $"Card In {value - 128}";
        if (value >= 161 && value <= 166) return $"Aux In {value - 160}";
        if (value == 167) return "TB Internal";
        if (value == 168) return "TB External";
        if (value >= 169 && value <= 184) return $"Output {value - 168}";
        if (value >= 185 && value <= 200) return $"P16 {value - 184}";
        if (value >= 201 && value <= 206) return $"AUX {value - 200}";
        if (value == 207) return "Monitor L";
        if (value == 208) return "Monitor R";

        return "Unknown";

    }

}
