using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Iceburg.Conversion.AJA.fs2;
using System.Threading;
using Iceburg.Conversion.AJA.fs4;
using Iceburg.Database;
using Iceburg.Mixer.X32;

namespace Iceburg.Devices.Status;
public class DeviceStatus
{
    public async Task<string> Getstatus(string id)
    {
        Device? device = Database.Database.GetDevice(id);

        if (device == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No Device Found"
            });
        }
        if (device.Type == "1")
        {
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

                return "connected";
            }
            catch (Exception ex)
            {
                return "error";
            }
        }
        if (device.Type == "2")
        {
            string timeString = device.LastSeen;

            // Later...
            if (DateTime.TryParse(timeString, out DateTime savedTime))
            {
                if (Math.Abs((DateTime.Now - savedTime).TotalSeconds) <= 5)
                {
                    return "connected";
                }
                else
                {
                    return "error";
                }
            }
            else
            {
                return "error";
            }
        }
        if (device.Type == "3")
        {
            int? ch = await X32.GetIntAsync(
     device.IpAddress,
     "/ch/01/config/source");

           

            if (ch.HasValue)
            {
                return "connected";
            }
            else
            {
                return "error";
            }


        }
        if (device.Type == "4")
        {
            try
            {
                var fs2 = new AJAFS4(id);

                var result = await fs2.GetParamAsync("eParamID_AudioOutputSelect_Vid1Embed");

                string value = result?.ToString() ?? "";
                if (value != "")
                {

                    return "connected";
                }
                else
                {
                    return "error";
                }
            }
            catch
            {
                return "error";
            }
        }
        if (device.Type == "5")
        {
            try
            {
                var fs2 = new AJAFS2(id);

                var result = await fs2.GetParamAsync("eParamID_Audio1Input_Universal");

                string value = result?.ToString() ?? "";
                if (value != "")
                {

                    return "connected";
                }
                else
                {
                    return "error";
                }
            }
            catch
            {
                return "error";
            }
        }
        else
        {
            return "error";
        }
       
    }
}