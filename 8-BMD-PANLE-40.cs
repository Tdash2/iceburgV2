using Iceburg.Database;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Iceburg.Interfaces.BMD.Panle40;

/// <summary>
/// Blackmagic Design Videohub Smart Control / Panel 40-button plugin.
/// The panel protocol is TCP/9991. The Videohub protocol is TCP/9990.
/// </summary>
public class BMD_Panle40
{
    private const int PanelPort = 9991;
    private const int VideohubPort = 9990;
    private const int TimeoutMs = 5000;

    public async Task<string> getinfo(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new { error = "No Device Found" });
        }

        return JsonSerializer.Serialize(new
        {
            IP = device.IpAddress,
            Name = device.Name
        });
    }

    /// <summary>
    /// Reads the panel's current BUTTON KIND, BUTTON SDI_A and Videohub address.
    /// </summary>
    public async Task<string> GetPanelStatus(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            string response = await ReadPanelInitialState(device.IpAddress);

            Dictionary<int, string> types = new();
            Dictionary<int, int> targets = new();

            ParseButtonKinds(response, types);
            ParseButtonTargets(response, targets);

            string? videohubIp = ParseValue(response, "Videohub address");

            var buttons = Enumerable.Range(0, 40)
                .Select(i => new
                {
                    button = i,
                    type = types.TryGetValue(i, out string? type) ? type : "Source",
                    target = targets.TryGetValue(i, out int target) ? target : 0
                })
                .ToArray();

            return JsonSerializer.Serialize(new
            {
                panel_ip = device.IpAddress,
                panel_name = device.Name,
                videohub_ip = videohubIp,
                buttons
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the Videohub input/output labels for the Videohub currently
    /// assigned to the panel.
    /// </summary>
    public async Task<string> GetNames(string id)
    {
        Device? panel = Database.Database.GetDevice(id);

        if (panel == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        try
        {
            string panelState = await ReadPanelInitialState(panel.IpAddress);
            string? videohubIp = ParseValue(panelState, "Videohub address");

            if (string.IsNullOrWhiteSpace(videohubIp))
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Panel did not report a Videohub address"
                });
            }

            return await GetVideohubNamesByIp(videohubIp);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sets a panel button's type and target.
    /// The PHP implementation sends BUTTON KIND and BUTTON SDI_A as two
    /// separate commands and expects an ACK from the panel for each.
    /// </summary>
    public async Task<string> SetButton(
        string id,
        string button,
        string type,
        string target)
    {
        Device? panel = Database.Database.GetDevice(id);

        if (panel == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (!int.TryParse(button, out int buttonNumber) ||
            buttonNumber < 0 ||
            buttonNumber > 39)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Button must be between 0 and 39"
            });
        }

        if (!int.TryParse(target, out int targetNumber))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Target must be an integer"
            });
        }

        if (!IsValidButtonType(type))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Type must be Source, Destination or Salvo"
            });
        }

        try
        {
            string kindCommand =
                $"BUTTON KIND:\r\n{buttonNumber} {type}\r\n\r\n";

            string kindAck =
                await SendPanelCommandAndWaitAck(
                    panel.IpAddress,
                    kindCommand);

            if (kindAck != "ACK")
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    step = "BUTTON KIND",
                    ack = kindAck
                });
            }

            string targetCommand =
                $"BUTTON SDI_A:\r\n{buttonNumber} {targetNumber}\r\n\r\n";

            string targetAck =
                await SendPanelCommandAndWaitAck(
                    panel.IpAddress,
                    targetCommand);

            return JsonSerializer.Serialize(new
            {
                success = targetAck == "ACK",
                button = buttonNumber,
                type,
                target = targetNumber,
                ack = targetAck
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

    /// <summary>
    /// Changes the Videohub IP stored in the panel.
    /// </summary>
    public async Task<string> SetVideohub(string id, string ip)
    {
        Device? panel = Database.Database.GetDevice(id);

        if (panel == null)
            return JsonSerializer.Serialize(new { error = "No Device Found" });

        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Invalid IP address"
            });
        }

        try
        {
            string command =
                $"NETWORK:\r\n Videohub address: {ip}\r\n\r\n";

            string ack =
                await SendPanelCommandAndWaitAck(
                    panel.IpAddress,
                    command);

            return JsonSerializer.Serialize(new
            {
                success = ack == "ACK",
                ack,
                new_ip = ip
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

    private static bool IsValidButtonType(string type)
    {
        return type.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Destination", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("Salvo", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadPanelInitialState(string host)
    {
        using TcpClient client = new TcpClient();

        await client.ConnectAsync(host, PanelPort);

        using NetworkStream stream = client.GetStream();

        // The PHP page sends CRLF to prompt the panel's current status.
        byte[] request = Encoding.ASCII.GetBytes("\r\n");
        await stream.WriteAsync(request, 0, request.Length);
        await stream.FlushAsync();

        return await ReadUntilQuietAsync(
            stream,
            maxMilliseconds: 2500,
            quietMilliseconds: 300);
    }

    private static async Task<string> SendPanelCommandAndWaitAck(
        string host,
        string command)
    {
        using TcpClient client = new TcpClient();

        await client.ConnectAsync(host, PanelPort);

        using NetworkStream stream = client.GetStream();

        byte[] data = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(data, 0, data.Length);
        await stream.FlushAsync();

        byte[] buffer = new byte[4096];
        StringBuilder response = new StringBuilder();

        using CancellationTokenSource timeout =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs));

        while (true)
        {
            int bytesRead = await stream.ReadAsync(
                buffer,
                0,
                buffer.Length,
                timeout.Token);

            if (bytesRead == 0)
                return "NO_ACK";

            string chunk = Encoding.ASCII.GetString(
                buffer,
                0,
                bytesRead);

            response.Append(chunk);

            string text = response.ToString();

            if (ContainsProtocolLine(text, "ACK"))
                return "ACK";

            if (ContainsProtocolLine(text, "NAK"))
                return "NAK";
        }
    }

    private static async Task<string> ReadUntilQuietAsync(
        NetworkStream stream,
        int maxMilliseconds,
        int quietMilliseconds)
    {
        byte[] buffer = new byte[8192];
        StringBuilder response = new StringBuilder();

        DateTime start = DateTime.UtcNow;
        DateTime lastData = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalMilliseconds < maxMilliseconds)
        {
            if (stream.DataAvailable)
            {
                int bytesRead = await stream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length);

                if (bytesRead == 0)
                    break;

                response.Append(
                    Encoding.ASCII.GetString(
                        buffer,
                        0,
                        bytesRead));

                lastData = DateTime.UtcNow;
            }
            else
            {
                if (response.Length > 0 &&
                    (DateTime.UtcNow - lastData).TotalMilliseconds >
                    quietMilliseconds)
                {
                    break;
                }

                await Task.Delay(20);
            }
        }

        return response.ToString();
    }

    private static void ParseButtonKinds(
        string raw,
        Dictionary<int, string> types)
    {
        string? block = GetBlock(raw, "BUTTON KIND");

        if (block == null)
            return;

        foreach (string line in SplitLines(block))
        {
            string[] parts = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int button) &&
                IsValidButtonType(parts[1]))
            {
                types[button] = parts[1];
            }
        }
    }

    private static void ParseButtonTargets(
        string raw,
        Dictionary<int, int> targets)
    {
        string? block = GetBlock(raw, "BUTTON SDI_A");

        if (block == null)
            return;

        foreach (string line in SplitLines(block))
        {
            string[] parts = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int button) &&
                int.TryParse(parts[1], out int target))
            {
                targets[button] = target;
            }
        }
    }

    private static string? GetBlock(string raw, string blockName)
    {
        string[] lines = raw.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.None);

        bool inBlock = false;
        StringBuilder block = new StringBuilder();

        foreach (string line in lines)
        {
            string text = line.Trim();

            if (text.Equals(
                    blockName + ":",
                    StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (inBlock && text.Length == 0)
                break;

            if (inBlock)
                block.AppendLine(text);
        }

        return inBlock ? block.ToString() : null;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim());
    }

    private static string? ParseValue(string raw, string key)
    {
        foreach (string line in raw.Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string text = line.Trim();

            if (text.StartsWith(
                    key + ":",
                    StringComparison.OrdinalIgnoreCase))
            {
                return text[(key.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool ContainsProtocolLine(
        string response,
        string value)
    {
        return response
            .Split(new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(line =>
                line.Trim().Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> GetVideohubNamesByIp(
        string ip)
    {
        using TcpClient client = new TcpClient();

        await client.ConnectAsync(ip, VideohubPort);

        using NetworkStream stream = client.GetStream();

        string raw = await ReadUntilQuietAsync(
            stream,
            maxMilliseconds: 2500,
            quietMilliseconds: 300);

        Dictionary<int, string> inputs = new();
        Dictionary<int, string> outputs = new();

        string? currentBlock = null;

        foreach (string line in raw.Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.None))
        {
            string text = line.Trim();

            if (text.Length == 0)
            {
                currentBlock = null;
                continue;
            }

            if (text.EndsWith(":"))
            {
                currentBlock = text[..^1].Trim();
                continue;
            }

            if (currentBlock == "INPUT LABELS")
            {
                ParseLabelLine(text, inputs);
            }
            else if (currentBlock == "OUTPUT LABELS")
            {
                ParseLabelLine(text, outputs);
            }
        }

        return JsonSerializer.Serialize(new
        {
            inputs = ToArray(inputs),
            outputs = ToArray(outputs)
        });
    }

    private static void ParseLabelLine(
        string text,
        Dictionary<int, string> labels)
    {
        int space = text.IndexOf(' ');

        if (space <= 0)
            return;

        if (int.TryParse(text[..space], out int number))
        {
            labels[number] = text[(space + 1)..].Trim();
        }
    }

    private static string[] ToArray(
        Dictionary<int, string> dictionary)
    {
        if (dictionary.Count == 0)
            return Array.Empty<string>();

        int max = dictionary.Keys.Max();

        string[] result = new string[max + 1];

        foreach (var pair in dictionary)
            result[pair.Key] = pair.Value;

        return result;
    }
}
