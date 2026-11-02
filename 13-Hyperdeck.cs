// Hyperdeck.cs
// HyperDeck Ethernet/FTP implementation using the existing Iceburg device database.

using Iceburg.Database;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Features;

namespace Iceburg.Router.BMD;

public sealed class Hyperdeck
{
    private readonly string _ip;
    private const int ControlPort = 9993;

    public Hyperdeck(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("HyperDeck IP is required.", nameof(ip));
        _ip = ip;
    }

    public sealed record Clip(string id, string name, string codec, string format, string duration);

    public async Task<string?> SendCommandAsync(string command, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_ip, ControlPort, ct);
            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 500;
            stream.WriteTimeout = 500;

            // Read the HyperDeck connection banner through its blank line.
            string? line;
            while ((line = await ReadLineAsync(stream, ct)) is not null && line.Trim().Length > 0) { }

            byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);

            var response = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                while ((line = await ReadLineAsync(stream, timeout.Token)) is not null)
                {
                    response.AppendLine(line);
                    if (line.Trim().Length == 0) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            
            return response.ToString();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int count = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (count == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public async Task<object> GetClipsAsync(CancellationToken ct = default)
    {
        string? response = await SendCommandAsync("disk list", ct);
        if (response is null) return new { connected = false };

        var clips = new List<Clip>();
        foreach (string rawLine in response.Split('\n'))
        {
            string line = rawLine.Trim();
            Match match = Regex.Match(line, @"^(\d+):\s+(.*)$");
            if (!match.Success) continue;

            string id = match.Groups[1].Value;
            string rest = match.Groups[2].Value;
            Match parts = Regex.Match(rest,
                @"(.*?)\s+(QuickTime.*?|DNx.*?|H\.264.*?|H\.265.*?)\s+([0-9A-Za-z]+)\s+([0-9:;]+)$");
            clips.Add(parts.Success
                ? new Clip(id, parts.Groups[1].Value, parts.Groups[2].Value, parts.Groups[3].Value, parts.Groups[4].Value)
                : new Clip(id, rest, "", "", ""));
        }
        return new { connected = true, clips };
    }

    public async Task<IResult> HandleGetAsync(HttpRequest request, CancellationToken ct = default)
    {
        if (request.Query.ContainsKey("timecode"))
        {
            string? response = await SendCommandAsync("transport info", ct);
            

            if (response is null) return Results.Json(new { connected = false });
            Match tc = Regex.Match(response, @"timecode:\s*([0-9:;]+)", RegexOptions.IgnoreCase);
            return Results.Json(new { connected = true, timecode = tc.Success ? tc.Groups[1].Value : "" });
        }

        if (request.Query.TryGetValue("cue", out var cue))
        {
            if (!int.TryParse(cue.ToString(), out int clipId) || clipId < 0)
                return Results.BadRequest("Invalid clip id");
            string? result = await SendCommandAsync($"goto: clip id:{clipId}", ct);
            return result is null ? Results.Problem("Could not connect to HyperDeck.") : Results.Text("OK");
        }

        if (request.Query.ContainsKey("record"))
            return await CommandResultAsync("record", ct);
        if (request.Query.ContainsKey("stop"))
            return await CommandResultAsync("stop", ct);
        if (request.Query.ContainsKey("play"))
            return await CommandResultAsync("play: single clip: true", ct);

        object clips = await GetClipsAsync(ct);
        return Results.Json(clips, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public async Task<IResult> CommandResultAsync(string command, CancellationToken ct = default)
    {
        string? result = await SendCommandAsync(command, ct);
        return result is null ? Results.Problem("Could not connect to HyperDeck.") : Results.Text("OK");
    }
    public async Task<bool> status()
    {
        string? response = await SendCommandAsync("transport info");
       


        if (response is null) return false;
        Match tc = Regex.Match(response, @"timecode:\s*([0-9:;]+)", RegexOptions.IgnoreCase);
        return true;

    } 

    public async Task<IResult> UploadAsync(IFormFile? file, string? filename, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest("No file uploaded");

        string originalName = Path.GetFileName(file.FileName);
        string extension = Path.GetExtension(originalName);

        string requested = string.IsNullOrWhiteSpace(filename)
            ? originalName
            : filename.Trim();

        if (!string.IsNullOrWhiteSpace(filename) &&
            string.Equals(Path.GetExtension(requested), extension, StringComparison.OrdinalIgnoreCase))
        {
            requested = Path.GetFileNameWithoutExtension(requested);
        }

        string safeName = Path.GetFileName(
            string.IsNullOrWhiteSpace(filename)
                ? originalName
                : requested + extension);

        if (string.IsNullOrWhiteSpace(safeName) ||
            safeName is "." or ".." ||
            safeName.Contains('/') ||
            safeName.Contains('\\'))
        {
            return Results.BadRequest("Invalid filename");
        }

        List<string> errors = new();

        foreach (string folder in new[] { "sd1", "sd2" }) // no leading slash
        {
            try
            {
                await FtpUploadAsync($"{folder}/{safeName}", file, ct);
                return Results.Text($"Uploaded: /{folder}/{safeName}");
            }
            catch (WebException ex)
            {
                if (ex.Response is FtpWebResponse ftp)
                {
                    errors.Add($"{folder}: {(int)ftp.StatusCode} {ftp.StatusDescription.Trim()}");
                    ftp.Dispose();
                }
                else
                {
                    errors.Add($"{folder}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{folder}: {ex.Message}");
            }
        }

        return Results.Problem(
            detail: string.Join(" | ", errors),
            statusCode: 500);
    }

    public async Task<IResult> DeleteAsync(string? filename, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filename)) return Results.BadRequest("No filename supplied");
        string name = Path.GetFileName(Uri.UnescapeDataString(filename));
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            return Results.BadRequest("Invalid filename");

        bool sd1 = await FtpFileExistsAsync("/sd1/" + name, ct);
        bool sd2 = await FtpFileExistsAsync("/sd2/" + name, ct);
        if (sd1 && !sd2)
        {
            await FtpDeleteAsync("/sd1/" + name, ct);
            return Results.Text("Deleted from SD1: " + name);
        }
        if (sd2 && !sd1)
        {
            await FtpDeleteAsync("/sd2/" + name, ct);
            return Results.Text("Deleted from SD2: " + name);
        }
        if (sd1 && sd2) return Results.Text("File exists on both cards. Nothing deleted.");
        return Results.NotFound("File not found.");
    }

    private Uri FtpUri(string path)
    {
        return new Uri($"ftp://{_ip}/{path.TrimStart('/')}");
    }
    private FtpWebRequest CreateFtpRequest(string path, string method)
    {
        var request = (FtpWebRequest)WebRequest.Create(FtpUri(path));
        request.Method = method;
        request.Credentials = new NetworkCredential("anonymous", "");
        request.UsePassive = true;
        request.UseBinary = true;
        request.KeepAlive = false;
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        return request;
    }


    private async Task FtpUploadAsync(
        string path,
        IFormFile file,
        CancellationToken ct)
    {
        var request = (FtpWebRequest)WebRequest.Create(FtpUri(path));
        request.Method = WebRequestMethods.Ftp.UploadFile;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.ContentLength = file.Length;

        await using (Stream requestStream =
            await request.GetRequestStreamAsync())
        await using (Stream fileStream = file.OpenReadStream())
        {
            await fileStream.CopyToAsync(requestStream, 81920, ct);
        }

        using var response =
            (FtpWebResponse)await request.GetResponseAsync();

        if (response.StatusCode != FtpStatusCode.ClosingData &&
            response.StatusCode != FtpStatusCode.FileActionOK)
        {
            throw new IOException(
                $"FTP upload failed: {(int)response.StatusCode} {response.StatusDescription}");
        }
    }


    private async Task<bool> FtpFileExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            FtpWebRequest request = CreateFtpRequest(path, WebRequestMethods.Ftp.GetFileSize);
            using var response = (FtpWebResponse)await request.GetResponseAsync();
            return true;
        }
        catch (WebException ex) when (ex.Response is FtpWebResponse response &&
    response.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable)
        {
            response.Dispose();
            return false;
        }
        catch (WebException ex) when (ex.Status == WebExceptionStatus.ProtocolError)
        {
            return false;
        }
    }

    private async Task FtpDeleteAsync(string path, CancellationToken ct)
    {
        FtpWebRequest request = CreateFtpRequest(path, WebRequestMethods.Ftp.DeleteFile);
        using var response = (FtpWebResponse)await request.GetResponseAsync();
    }
}

// Map these endpoints inside the application's existing authorized API group.
// Device lookup follows the existing Iceburg router pattern: Database.Database.GetDevice(id).
public static class HyperdeckEndpointExtensions
{
    public static IEndpointRouteBuilder MapHyperdeckEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bmd/hyperdeck/{id}");

        group.MapGet("/", async (string id, HttpRequest request, CancellationToken ct) =>
        {
            Device? device = Database.Database.GetDevice(id);
            if (device is null) return Results.NotFound("No Device Found");
            return await new Hyperdeck(device.IpAddress).HandleGetAsync(request, ct);
        });


        group.MapPost("/upload", async (
            string id,
            HttpRequest request,
            CancellationToken ct) =>
        {
            // Allow uploads up to 100 MB for this endpoint only.
            var sizeFeature = request.HttpContext.Features
                .Get<IHttpMaxRequestBodySizeFeature>();

            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = 100_000_000_00;
            }

            Device? device = Database.Database.GetDevice(id);
            if (device is null)
                return Results.NotFound("No Device Found");

            var form = await request.ReadFormAsync(ct);

            return await new Hyperdeck(device.IpAddress).UploadAsync(
                form.Files.GetFile("file"),
                form["filename"].ToString(),
                ct);
        });


        group.MapPost("/delete", async (string id, HttpRequest request, CancellationToken ct) =>
        {
            Device? device = Database.Database.GetDevice(id);
            if (device is null) return Results.NotFound("No Device Found");
            var form = await request.ReadFormAsync(ct);
            return await new Hyperdeck(device.IpAddress).DeleteAsync(
                form["filename"].ToString(), ct);
        });

        return endpoints;
    }
}
