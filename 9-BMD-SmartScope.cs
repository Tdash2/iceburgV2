using Iceburg.Database;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Iceburg.Monitor.BMD.smartscope;

public sealed class BMD_SmartScope
{
    private const int Port = 9992;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static Device? FindDevice(string id) => Database.Database.GetDevice(id);

    public Task<string> GetInfo(string id)
    {
        var device = FindDevice(id);
        return Task.FromResult(device is null
            ? Json(new { error = "No Device Found" })
            : Json(new { IP = device.IpAddress, Name = device.Name }));
    }

    public async Task<string> GetState(string id)
    {
        var device = FindDevice(id);

        if (device is null)
            return Json(new { error = "No Device Found" });

        try
        {
            using var client = new TcpClient();

            await client.ConnectAsync(device.IpAddress, Port);

            using NetworkStream stream = client.GetStream();

            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("\r\n"));

            await stream.FlushAsync();

            var result = new StringBuilder();
            var buffer = new byte[4096];

            var start = DateTime.UtcNow;
            var lastData = DateTime.UtcNow;

            while ((DateTime.UtcNow - start).TotalSeconds < 3)
            {
                if (stream.DataAvailable)
                {
                    int count = await stream.ReadAsync(buffer, 0, buffer.Length);

                    if (count == 0)
                        break;

                    result.Append(
                        Encoding.ASCII.GetString(buffer, 0, count));

                    lastData = DateTime.UtcNow;
                }
                else
                {
                    if (result.Length > 0 &&
                        (DateTime.UtcNow - lastData).TotalMilliseconds > 200)
                    {
                        break;
                    }

                    await Task.Delay(20);
                }
            }

            string response = result.ToString();

            return Json(new
            {
                monitor_a = ParseBlock(response, "MONITOR A"),
                monitor_b = ParseBlock(response, "MONITOR B")
            });
        }
        catch (Exception ex)
        {
            return Json(new
            {
                error = ex.Message,
                details = ex.GetType().Name,
                device = device.IpAddress
            });
        }
    }

    public async Task<string> Settallyon(string id, string monitor)
    {
        var device = FindDevice(id);
        if (device is null) return Json(new { ok = false, error = "No Device Found" });


        string command = $"MONITOR {monitor}:\nBorder: red\n\n MONITOR {monitor}:\nBorder: red\n\n";
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(Timeout);
            await client.ConnectAsync(device.IpAddress, Port, cts.Token);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);
            await stream.FlushAsync(cts.Token);
            await Task.Delay(100, cts.Token);
            return Json(new { ok = true, cmd = command });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message, cmd = command });
        }
    }
    public async Task<string> Settallyoff(string id, string monitor)
    {
        var device = FindDevice(id);
        if (device is null) return Json(new { ok = false, error = "No Device Found" });


        string command = $"MONITOR {monitor}:\nBorder: none\n\n";
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(Timeout);
            await client.ConnectAsync(device.IpAddress, Port, cts.Token);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);
            await stream.FlushAsync(cts.Token);
            await Task.Delay(100, cts.Token);
            return Json(new { ok = true, cmd = command });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message, cmd = command });
        }
    }

    public async Task<string> Set(string id, string monitor, string param, string value)
    {
        var device = FindDevice(id);
        if (device is null) return Json(new { ok = false, error = "No Device Found" });

        if (monitor is not ("A" or "B"))
            return Json(new { ok = false, error = "Monitor must be A or B" });

        string[] allowed = { "Brightness", "Contrast", "Saturation", "ScopeMode", "Border" };
        if (!allowed.Contains(param, StringComparer.Ordinal))
            return Json(new { ok = false, error = "Unsupported parameter" });

        if (param == "ScopeMode")
        {
            string[] modes = { "Picture", "WaveformLuma", "Vector100", "ParadeRGB",
                "ParadeYUV", "Histogram", "AudioDbfs", "AudioDbvu" };
            if (!modes.Contains(value, StringComparer.Ordinal))
                return Json(new { ok = false, error = "Unsupported scope mode" });
        }
       // else if (!int.TryParse(value, out int n) || n < 0 || n > 255)
       //     return Json(new { ok = false, error = "Value must be between 0 and 255" });

        string command = $"MONITOR {monitor}:\n{param}: {value}\n\n";
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(Timeout);
            await client.ConnectAsync(device.IpAddress, Port, cts.Token);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);
            await stream.FlushAsync(cts.Token);
            await Task.Delay(100, cts.Token);
            return Json(new { ok = true, cmd = command });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message, cmd = command });
        }
    }

    private static Dictionary<string, string> ParseBlock(string text, string blockName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        bool inBlock = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (inBlock) break;
                continue;
            }

            if (line.EndsWith(':'))
            {
                inBlock = line.Equals(blockName + ":", StringComparison.Ordinal);
                continue;
            }

            if (!inBlock) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return values;
    }
}

// Register these routes where ICEBURG maps its device APIs.