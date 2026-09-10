using Iceburg.Database;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;

namespace Iceburg.Router.BMD;

public class BMD_Router
{
    public async Task<string> getinfo(string id)
    {
        Device? device = Config.GetDevice(id);
        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }
        return JsonSerializer.Serialize(new
        {
            IP = device.IpAddress,
            Name = device.Name
        });
    }
    public async Task<string> getnames(string id)
    {
        Device? device = Config.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }

        try
        {
            using TcpClient client = new TcpClient();

            // Connect to Videohub
            await client.ConnectAsync(device.IpAddress, 9990);

            using NetworkStream stream = client.GetStream();

            StringBuilder response = new StringBuilder();

            byte[] buffer = new byte[8192];

            DateTime start = DateTime.UtcNow;
            DateTime lastData = DateTime.UtcNow;

            // Read for up to 2.5 seconds.
            // The Videohub sends multiple blocks and keeps
            // the TCP connection open.
            while ((DateTime.UtcNow - start).TotalMilliseconds < 2500)
            {
                if (stream.DataAvailable)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                        break;

                    response.Append(
                        Encoding.ASCII.GetString(buffer, 0, bytesRead));

                    lastData = DateTime.UtcNow;
                }
                else
                {
                    // Once we've received data, if nothing has
                    // arrived for 300ms, assume the initial dump
                    // is complete.
                    if (response.Length > 0 &&
                        (DateTime.UtcNow - lastData).TotalMilliseconds > 300)
                    {
                        break;
                    }

                    await Task.Delay(20);
                }
            }

            string raw = response.ToString();

            var inputLabels = new Dictionary<int, string>();
            var outputLabels = new Dictionary<int, string>();

            string? currentBlock = null;

            string[] lines = raw.Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None);

            foreach (string line in lines)
            {
                string text = line.Trim();

                // Blank line ends the current block
                if (text.Length == 0)
                {
                    currentBlock = null;
                    continue;
                }

                // Detect block header
                if (text.EndsWith(":"))
                {
                    currentBlock = text[..^1].Trim();
                    continue;
                }

                // INPUT LABELS
                if (currentBlock == "INPUT LABELS")
                {
                    int space = text.IndexOf(' ');

                    if (space > 0 &&
                        int.TryParse(text[..space], out int number))
                    {
                        string name = text[(space + 1)..].Trim();

                        inputLabels[number] = name;
                    }
                }

                // OUTPUT LABELS
                else if (currentBlock == "OUTPUT LABELS")
                {
                    int space = text.IndexOf(' ');

                    if (space > 0 &&
                        int.TryParse(text[..space], out int number))
                    {
                        string name = text[(space + 1)..].Trim();

                        outputLabels[number] = name;
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                input_labels = inputLabels,
                output_labels = outputLabels
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
    public async Task<string> GetRoutes(string id)
    {
        Device? device = Config.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }

        try
        {
            using TcpClient client = new TcpClient();

            await client.ConnectAsync(device.IpAddress, 9990);

            using NetworkStream stream = client.GetStream();

            StringBuilder response = new StringBuilder();

            byte[] buffer = new byte[8192];

            DateTime start = DateTime.UtcNow;
            DateTime lastData = DateTime.UtcNow;

            // Read the initial Videohub status dump
            while ((DateTime.UtcNow - start).TotalMilliseconds < 2500)
            {
                if (stream.DataAvailable)
                {
                    int bytesRead = await stream.ReadAsync(
                        buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                        break;

                    response.Append(
                        Encoding.ASCII.GetString(
                            buffer, 0, bytesRead));

                    lastData = DateTime.UtcNow;
                }
                else
                {
                    if (response.Length > 0 &&
                        (DateTime.UtcNow - lastData).TotalMilliseconds > 300)
                    {
                        break;
                    }

                    await Task.Delay(20);
                }
            }

            var routes = new Dictionary<int, int>();

            string? currentBlock = null;

            string[] lines = response.ToString().Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None);

            foreach (string line in lines)
            {
                string text = line.Trim();

                // Blank line ends the block
                if (text.Length == 0)
                {
                    currentBlock = null;
                    continue;
                }

                // Block header
                if (text.EndsWith(":"))
                {
                    currentBlock = text[..^1].Trim();
                    continue;
                }

                // VIDEO OUTPUT ROUTING
                if (currentBlock == "VIDEO OUTPUT ROUTING")
                {
                    string[] parts = text.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2 &&
                        int.TryParse(parts[0], out int output) &&
                        int.TryParse(parts[1], out int input))
                    {
                        routes[output] = input;
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                routes
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
    public async Task<string> setinputname(string id,string input, string name)
    {
        Device? device = Config.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }

        try
        {
            using TcpClient client = new TcpClient();

            await client.ConnectAsync(device.IpAddress, 9990);

            using NetworkStream stream = client.GetStream();

            string command =
                $"INPUT LABELS:\n" +
                $"{input} {name}\n" +
                "\n";

            byte[] data = Encoding.ASCII.GetBytes(command);

            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            // Wait for ACK / NAK
            byte[] buffer = new byte[1024];

            int bytesRead = await stream.ReadAsync(
                buffer, 0, buffer.Length);

            string response = Encoding.ASCII
                .GetString(buffer, 0, bytesRead)
                .Trim();

            if (response.StartsWith("ACK"))
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    input = input,
                    name = name
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Videohub rejected the name change",
                response = response
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
    public async Task<string> setoutputname(string id, string input, string name)
    {
        Device? device = Config.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }

        try
        {
            using TcpClient client = new TcpClient();

            await client.ConnectAsync(device.IpAddress, 9990);

            using NetworkStream stream = client.GetStream();

            string command =
                $"OUTPUT LABELS:\n" +
                $"{input} {name}\n" +
                "\n";

            byte[] data = Encoding.ASCII.GetBytes(command);

            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            // Wait for ACK / NAK
            byte[] buffer = new byte[1024];

            int bytesRead = await stream.ReadAsync(
                buffer, 0, buffer.Length);

            string response = Encoding.ASCII
                .GetString(buffer, 0, bytesRead)
                .Trim();

            if (response.StartsWith("ACK"))
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    input = input,
                    name = name
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Videohub rejected the name change",
                response = response
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
    public async Task<string> setroute(string id,string sorce,string destnatnion)
    {
        Device? device = Config.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }

        try
        {
            using TcpClient client = new TcpClient();

            await client.ConnectAsync(device.IpAddress, 9990);

            using NetworkStream stream = client.GetStream();

            // Videohub protocol:
            //
            // VIDEO OUTPUT ROUTING:
            // destination source
            //
            // Example:
            // VIDEO OUTPUT ROUTING:
            // 7 2
            //
            // Means output 7 is routed to input 2.
            string command =
                $"VIDEO OUTPUT ROUTING:\n" +
                $"{destnatnion} {sorce}\n" +
                "\n";

            byte[] data = Encoding.ASCII.GetBytes(command);

            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            // Wait for ACK / NAK
            byte[] buffer = new byte[1024];

            int bytesRead = await stream.ReadAsync(
                buffer, 0, buffer.Length);

            string response = Encoding.ASCII
                .GetString(buffer, 0, bytesRead)
                .Trim();

            if (response.StartsWith("ACK"))
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    source = sorce,
                    destination = destnatnion
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Videohub rejected the route",
                response = response
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

}
