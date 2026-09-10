using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Iceburg.Router.BMD;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter()
    );
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

Iceburg.Database.Config.Initialize();
//Iceburg.Database.Config.AddDevice("Test Router","1","192.168.127.9");

//BMD Router
app.MapGet("/api/router/bmd/{id}/getinfo", (string id) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.getinfo(id);
});
app.MapGet("/api/router/bmd/{id}/getnames", (string id) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.getnames(id);
});
app.MapGet("/api/router/bmd/{id}/getroutes", (string id) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.GetRoutes(id);
});
app.MapGet("/api/router/bmd/{id}/setinputname/{input}/{name}", (string id, string input, string name) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.setinputname(id, input,name);
});
app.MapGet("/api/router/bmd/{id}/setoutputname/{input}/{name}", (string id, string input, string name) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.setoutputname(id, input, name);
});
app.MapGet("/api/router/bmd/{id}/setroute/{input}/{output}", (string id,string input,string output) =>
{
    var bmdRouter = new BMD_Router();

    return bmdRouter.setroute(id, input,output);
});





















foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
{
    if (ni.OperationalStatus != OperationalStatus.Up)
        continue;

    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
    {
        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            Console.WriteLine($"Starting Web Interface at: http://{addr.Address}");
        }
    }
}
app.Run("http://0.0.0.0:80");