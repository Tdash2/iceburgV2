using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Iceburg.Database;
using Iceburg.Devices.Status;
using Iceburg.Router.BMD;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter( "Microsoft", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter()
    );
});
builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = 443;
});
builder.Services.AddAuthentication( CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
    {
        options.Cookie.Name = "IceburgAuth";

        // JavaScript cannot read the authentication cookie.
        options.Cookie.HttpOnly = true;

        // Helps protect against CSRF.
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Works over HTTP during local deployment and
        // automatically becomes Secure when HTTPS is used.
        options.Cookie.SecurePolicy =
            CookieSecurePolicy.SameAsRequest;

        // Fixed 30 minute session.
        options.ExpireTimeSpan =
            TimeSpan.FromMinutes(30);

        options.SlidingExpiration = false;

        options.LoginPath = "/login.html";

        options.AccessDeniedPath = "/access-denied";


        // --------------------------------------------------------
        // UNAUTHENTICATED REQUEST
        // --------------------------------------------------------

        options.Events.OnRedirectToLogin = context =>
        {
            // API requests get 401 instead of an HTML redirect.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 401;

                return Task.CompletedTask;
            }

            // Preserve the exact page that the user was trying
            // to access.
            var returnUrl =
                context.Request.PathBase +
                context.Request.Path +
                context.Request.QueryString;

            var encodedReturnUrl =
                Uri.EscapeDataString(returnUrl);

            context.Response.Redirect(
                $"/login.html?returnUrl={encodedReturnUrl}");

            return Task.CompletedTask;
        };


        // --------------------------------------------------------
        // FORBIDDEN REQUEST
        // --------------------------------------------------------

        options.Events.OnRedirectToAccessDenied = context =>
        {
            // API requests get 403.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 403;

                return Task.CompletedTask;
            }

            context.Response.Redirect("/access-denied");

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
var app = builder.Build();
Database.Initialize();
app.Use(async (context, next) =>
{
    bool isTallyApi =
        context.Request.Path.StartsWithSegments("/tally");

    // Tally is allowed over HTTP.
    // Everything else gets redirected to HTTPS.
    if (!context.Request.IsHttps && !isTallyApi)
    {
        var httpsUrl =
            $"https://{context.Request.Host.Host}:443" +
            context.Request.PathBase +
            context.Request.Path +
            context.Request.QueryString;

        context.Response.Redirect(httpsUrl);
        return;
    }
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await next();
    stopwatch.Stop();

    Console.WriteLine(
        $"{context.Request.Method} {context.Request.Path} - {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
});






app.UseAuthentication();
app.UseAuthorization();
var protectedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/admin.html",
        "/dashboard.html",
        "/router.html",
        "/devices.html",
        "/settings.html"
    };
app.Use(async (context, next) =>
{
    var path =
        context.Request.Path.Value ?? "";

    if (protectedPages.Contains(path))
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var returnUrl =
                context.Request.PathBase +
                context.Request.Path +
                context.Request.QueryString;

            var encodedReturnUrl =
                Uri.EscapeDataString(returnUrl);

            context.Response.Redirect(
                $"/login.html?returnUrl={encodedReturnUrl}");

            return;
        }
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
var loginChallenges = new ConcurrentDictionary<string, LoginChallenge>();





app.UseAuthentication();
app.UseAuthorization();

app.MapPost( "/api/login/challenge",(LoginChallengeRequest request) =>
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Results.BadRequest(new
            {
                error = "Username is required."
            });
        }

        var username =
            request.Username.Trim();

        var user =
            Database.GetUser(username);

        if (user == null)
        {
            return Results.Unauthorized();
        }

        // --------------------------------------------------------
        // Get stored salt.
        // --------------------------------------------------------

        var passwordSalt =
     user.PasswordSalt;

        if (string.IsNullOrWhiteSpace(passwordSalt))
        {
            return Results.Unauthorized();
        }

        byte[] salt;

        try
        {
            salt =
                Convert.FromBase64String(
                    passwordSalt);
        }
        catch
        {
            return Results.Unauthorized();
        }

        // --------------------------------------------------------
        // Generate random one-time challenge.
        // --------------------------------------------------------

        var challenge =
            RandomNumberGenerator.GetBytes(32);

        var challengeId =
            Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32));

        var storedChallenge =
            new LoginChallenge
            {
                Username = user.Username,

                Challenge = challenge,

                ExpiresAt =
                    DateTime.UtcNow.AddMinutes(2)
            };

        loginChallenges[challengeId] =
            storedChallenge;

        // --------------------------------------------------------
        // Return salt and challenge to browser.
        // --------------------------------------------------------

        return Results.Ok(new
        {
            challengeId,

            salt =
                Convert.ToBase64String(salt),

            challenge =
                Convert.ToBase64String(challenge)
        });
    });
app.MapPost("/api/login", async (LoginRequest request,HttpContext httpContext) =>
    {
        // --------------------------------------------------------
        // Validate request.
        //
        // This is important because ConcurrentDictionary does
        // not accept a null key.
        //
        // It prevents:
        //
        // ArgumentNullException:
        // Value cannot be null. (Parameter 'key')
        // --------------------------------------------------------

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Results.BadRequest(new
            {
                error = "Username is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.ChallengeId))
        {
            return Results.BadRequest(new
            {
                error = "ChallengeId is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Proof))
        {
            return Results.BadRequest(new
            {
                error = "Proof is required."
            });
        }

        var username =
            request.Username.Trim();

        var challengeId =
            request.ChallengeId.Trim();


        // --------------------------------------------------------
        // Retrieve and REMOVE challenge.
        //
        // TryRemove makes the challenge one-time use.
        // --------------------------------------------------------

        if (!loginChallenges.TryRemove(
                challengeId,
                out var loginChallenge))
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Check expiration.
        // --------------------------------------------------------

        if (loginChallenge.ExpiresAt <
            DateTime.UtcNow)
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Make sure challenge belongs to username.
        // --------------------------------------------------------

        if (!string.Equals(
                loginChallenge.Username,
                username,
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Find user.
        // --------------------------------------------------------

        var user =
            Database.GetUser(username);

        if (user == null)
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Get stored password verifier.
        // --------------------------------------------------------

        if (string.IsNullOrWhiteSpace(
                user.PasswordHash))
        {
            return Results.Unauthorized();
        }

        byte[] storedVerifier;

        byte[] suppliedProof;

        try
        {
            storedVerifier =
                Convert.FromBase64String(
                    user.PasswordHash);

            suppliedProof =
                Convert.FromBase64String(
                    request.Proof);
        }
        catch
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Calculate expected proof.
        //
        //     HMAC-SHA256(
        //         storedVerifier,
        //         challenge
        //     )
        // --------------------------------------------------------

        byte[] expectedProof;

        using (var hmac =
            new HMACSHA256(storedVerifier))
        {
            expectedProof =
                hmac.ComputeHash(
                    loginChallenge.Challenge);
        }


        // --------------------------------------------------------
        // Constant-time comparison.
        // --------------------------------------------------------

        if (suppliedProof.Length !=
            expectedProof.Length)
        {
            return Results.Unauthorized();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                suppliedProof,
                expectedProof))
        {
            return Results.Unauthorized();
        }


        // --------------------------------------------------------
        // Authentication succeeded.
        // --------------------------------------------------------

        var claims =
            new List<Claim>
            {
                new Claim(
                    ClaimTypes.NameIdentifier,
                    user.Id),

                new Claim(
                    ClaimTypes.Name,
                    user.Username),

                new Claim(
                    ClaimTypes.Role,
                    user.Role)
            };


        var identity =
            new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults
                    .AuthenticationScheme);


        var principal =
            new ClaimsPrincipal(identity);


        await httpContext.SignInAsync(
            CookieAuthenticationDefaults
                .AuthenticationScheme,
            principal);


        // --------------------------------------------------------
        // Determine redirect destination.
        // --------------------------------------------------------

        var returnUrl =
            GetSafeReturnUrl(
                request.ReturnUrl);

        string redirect;


        // --------------------------------------------------------
        // ADMIN
        // --------------------------------------------------------

        if (user.Role.Equals(
                "Admin",
                StringComparison.OrdinalIgnoreCase))
        {
            redirect =
                returnUrl ?? "/dashboard.html";
        }


        // --------------------------------------------------------
        // USER
        // --------------------------------------------------------

        else
        {
            // Normal users may return to their original page,
            // except administrator-only pages.
            if (!string.IsNullOrWhiteSpace(returnUrl) &&
                !IsAdminPage(returnUrl))
            {
                redirect = returnUrl;
            }
            else
            {
                redirect = "/dashboard.html";
            }
        }


        return Results.Ok(new
        {
            success = true,
            redirect
        });
    });
app.MapGet("/api/logout",async ( HttpContext httpContext) =>
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults .AuthenticationScheme);

        return Results.Ok(new
        {
            success = true,

        });
    });
app.MapGet( "/api/me",(HttpContext httpContext) =>
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new
        {
            id =
                httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            username =
                httpContext.User.Identity?.Name,
            role =
                httpContext.User.FindFirst( ClaimTypes.Role)?.Value
        });
    })
    .RequireAuthorization();

app.MapGet(
    "/access-denied",
    () =>
    {
        return Results.Json(
            new
            {
                error = "Access denied."
            },
            statusCode: 403);
    });

//device api
var deviceApi = app.MapGroup("/api/device").RequireAuthorization();
deviceApi.MapGet("/getdevices/", () =>
{
return Iceburg.Database.Database.Devices;
});
deviceApi.MapGet("/getdevicestatus/{id}", async (string id) =>
{
    var devicetest = new DeviceStatus();

    return await devicetest.Getstatus(id);
});

// Video Hub API
var routerApi = app.MapGroup("/api/router").RequireAuthorization();

routerApi.MapGet( "/bmd/{id}/getinfo", async(string id) =>
    {
        var bmdRouter =
            new BMD_Router();

        return await bmdRouter.getinfo(id);
    });
routerApi.MapGet( "/bmd/{id}/getnames", (string id) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.getnames(id);
    });
routerApi.MapGet("/bmd/{id}/getroutes",(string id) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.GetRoutes(id);
    });
routerApi.MapPost("/bmd/{id}/setinputname",(string id, SetNameRequest request) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.setinputname(
            id,
            request.Input,
            request.Name);
    });
routerApi.MapPost("/bmd/{id}/setoutputname", (string id, SetNameRequest request) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.setoutputname(
            id,
            request.Input,
            request.Name);
    });
routerApi.MapPost("/bmd/{id}/setroute", (string id, SetRouteRequest request) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.setroute(
            id,
            request.Input,
            request.Output);
    });
//Tally Legacy API
app.MapGet("/tally/gettallystatus.php", () =>
{
    return "{\"inputs\":{\"1\":false,\"2\":false,\"3\":false,\"4\":false,\"5\":false,\"6\":false,\"7\":false,\"8\":false},\"outputs\":{\"1\":false,\"2\":false,\"3\":false,\"4\":false,\"5\":false,\"6\":false,\"7\":false,\"8\":false}}";
});
app.MapGet("/tally/getumd.php", () =>
{
    return "{\"inputs\":{\"1\":\"\",\"2\":\"\",\"3\":\"\",\"4\":\"\",\"5\":\"\",\"6\":\"\",\"7\":\"\",\"8\":\"\"},\"outputs\":{\"1\":\"\",\"2\":\"\",\"3\":\"\",\"4\":\"\",\"5\":\"\",\"6\":\"\",\"7\":\"\",\"8\":\"\"}}";
});
app.MapGet("/tally/settallystatus.php", () =>
{
    return "test";
});



//Command Line Setup and command handeling
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(@"
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⣄⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡾⠋⡏⢻⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣰⠟⠁⠀⡇⠀⠹⣆⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣸⠏⠀⠀⢸⠁⠀⠀⠸⣷⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡾⠃⠀⠀⠀⠈⢦⠀⠀⠀⠈⢷⣀⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣠⡶⠟⠁⠀⠀⠀⠀⠀⠈⢳⡀⠀⠀⠀⠉⠛⠳⢶⣦⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣴⠟⠧⣄⡀⠐⠀⠀⠀⡀⠀⠀⠀⢻⡀⠀⠀⠀⠀⢀⡇⠸⣧⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣼⠃⠀⠀⢸⠀⠄⠀⠀⡀⠀⠀⠀⠀⠀⢷⠀⠀⠀⠀⣸⠁⠀⠸⣧⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣾⠃⠀⠀⠀⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠳⣄⠀⠀⠃⠀⠀⠀⠸⣧⡀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣠⡾⠃⠀⠀⠀⢰⠃⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠳⣄⠀⠀⠀⠀⠀⠈⠛⢷⣄⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣾⠏⠀⠀⠀⠀⠀⠈⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢹⡆⠀⠀⠀⠀⠀⠀⠀⠹⣇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡾⠁⠀⠀⠀⠀⠀⠀⠀⠀⢠⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢹⡄⠀⠀⠙⢆⠀⠀⠀⢹⣇⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⡾⠁⠀⠀⠀⠀⠀⠀⠀⠀⠀⡌⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢳⡄⠀⠀⠈⢷⡀⠀⠀⢻⣆⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
                        ICEBURG Server


Web Interfaces Hosted at:");
foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
{
    if (ni.OperationalStatus !=
        OperationalStatus.Up)
    {
        continue;
    }

    foreach (
        var addr
        in ni.GetIPProperties().UnicastAddresses)
    {
        if (addr.Address.AddressFamily ==
            AddressFamily.InterNetwork)
        {
            Console.WriteLine(
                $"https://{addr.Address}");
        }
    }
}

static List<string> SplitCommandLine(string input)
{
    var result =
        new List<string>();

    var current =
        new System.Text.StringBuilder();

    bool inQuotes = false;

    char quoteCharacter = '\0';

    for (int i = 0; i < input.Length; i++)
    {
        var character =
            input[i];

        if (character == '"' ||
            character == '\'')
        {
            if (!inQuotes)
            {
                inQuotes = true;
                quoteCharacter = character;
                continue;
            }

            if (character == quoteCharacter)
            {
                inQuotes = false;
                continue;
            }
        }

        if (char.IsWhiteSpace(character) &&
            !inQuotes)
        {
            if (current.Length > 0)
            {
                result.Add(
                    current.ToString());

                current.Clear();
            }

            continue;
        }

        current.Append(character);
    }

    if (current.Length > 0)
    {
        result.Add(
            current.ToString());
    }

    return result;
}


Console.WriteLine("");
Console.WriteLine("");

if (Database.GetUsers().Count == 0)
{
    Database.AddUser(
        "admin",
        "admin",
        "Admin");

    Console.WriteLine(
        "Created initial Iceburg admin account.");
}
Console.WriteLine($"For Help Type \"help\" ");
_ = Task.Run(async () =>
{

    while (true)
    {

        Console.Write("iceburg> ");

        var input = await Console.In.ReadLineAsync();

        if (input == null)
            break;

        var parts =
            SplitCommandLine(input);

        if (parts.Count == 0)
            continue;

        var command =
            parts[0].ToLowerInvariant();

        switch (command)
        {
            case "help":
                Console.WriteLine("Commands:");
                Console.WriteLine($"    help                                                                                Show this help");
                Console.WriteLine($"    device list                                                                         Shows all devices");
                Console.WriteLine($"    device add (Name) (IP) (Type)                                                       Adds a device");
                Console.WriteLine($"    device remove (ID)                                                                  Removes a device");
                Console.WriteLine($"    user list                                                                           List all users");
                Console.WriteLine($"    user add (Username) (password) (role)                                               Adds a user");
                Console.WriteLine($"    user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")             Edits A user");
                Console.WriteLine($"    user remove (ID)                                                                    Removes A user");              
                Console.WriteLine($"    exit                                                                                Stops Iceburg");
                break;

            case "device":
                if (parts.Count > 1)
                {
                    
                    {
                        Console.WriteLine($"Device: add");
                        if (parts.Count != 5)
                        {
                            Console.WriteLine($"device add (Name) (IP) (Type)");
                        }
                        else
                        {
                            string Name = parts[2];
                            string IP = parts[3];
                            string type = parts[4];

                            Device device = Database.AddDevice(Name, type, IP);
                            Console.WriteLine($"Added New Device. Device ID: {device.Id.ToString()}");
                        }                         
                    }
                    if (parts[1] == "list")
                    {
                        Console.WriteLine($"{"Name",-25} {"IP",-15} {"ID",-20}");
                        Console.WriteLine(new string('-', 65));

                        foreach (var device in Database.Devices)
                        {
                            Console.WriteLine($"{device.Name,-25} {device.IpAddress,-15} {device.Id,-20}");
                        }
                    }
                    if (parts[1] == "remove")
                    {
                        if (parts.Count != 3)
                        {
                            Console.WriteLine($"device remove (ID)");
                        }
                        else
                        {
                            string ID = parts[2];
                            Device deviceToRemove = Database.GetDevice(ID);
                            if (!(deviceToRemove == null))
                            {
                                Database.RemoveDevice(ID);

                                Console.WriteLine($"Removed Device {ID}");
                            }
                            else
                            {
                                Console.WriteLine($"Device Not found");
                            }                                                      
                        }
                    }

                }
                else
                {
                    Console.WriteLine($"device list");
                    Console.WriteLine($"device add (Name) (IP) (Type)");
                    Console.WriteLine($"device remove (ID)");

                }
                break;
            case "user":
                if (parts.Count > 1)
                {
                    if (parts[1] == "add")
                    {
                       
                        if (parts.Count != 5)
                        {
                            Console.WriteLine($"user add (Username) (password) (role \"Admin\" \"User\")");
                        }
                        else
                        {
                            string username = parts[2];
                            string password = parts[3];
                            string role = parts[4];
                            if (!(role=="Admin"|| role == "User"))
                            {
                                Console.WriteLine($"Unknown Role");
                                break;
                            }

                            if (!(Database.GetUser(username) == null))
                            {
                                Console.WriteLine($"Username is already used");
                                break;
                            }

                            User user = Database.AddUser(username, password, role);
                            

                            Console.WriteLine($"Added New User. User ID: {user.Id}");

                        }
                    }
                    if (parts[1] == "edit")
                    {
                        if (parts.Count != 6)
                        {
                            Console.WriteLine($"user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
                        }
                        else
                        {
                            string userid = parts[2];
                            string username = parts[3];
                            string password = parts[4];
                            string role = parts[5];


                            if (!(role == "Admin" || role == "User"))
                            {
                                Console.WriteLine($"Unknown Role");
                                break;
                            }

                            if ((Database.GetUserById(userid) == null))
                            {
                                Console.WriteLine($"Username not found");
                                break;
                            }

                            User user = Database.EditUser(userid, username, password, role);


                            Console.WriteLine($"Edited User. Username: {user.Username} User Role: {user.Username}");

                        }
                    }
                    if (parts[1] == "list")
                    {
                        Console.WriteLine($"{"Name",-25} {"Role",-15} {"ID",-20}");
                        Console.WriteLine(new string('-', 65));

                        foreach (var device in Database.Users)
                        {
                            Console.WriteLine($"{device.Username,-25} {device.Role,-15} {device.Id,-20}");
                        }
                    }
                    if (parts[1] == "remove")
                    {
                        if (parts.Count != 3)
                        {
                            Console.WriteLine($"User remove (ID)");
                        }
                        else
                        {
                            string ID = parts[2];
                            User deviceToRemove = Database.GetUserById(ID);
                            if (!(deviceToRemove == null))
                            {
                                Database.RemoveUser(ID);

                                Console.WriteLine($"Removed User {ID}");
                            }
                            else
                            {
                                Console.WriteLine($"User Not found");
                            }
                        }
                    }

                }
                else
                {
                    Console.WriteLine($"user list");
                    Console.WriteLine($"user add (Username) (password) (role)");
                    Console.WriteLine($"user remove (ID)");
                    Console.WriteLine($"user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");

                }
                break;
            case "exit":
                Console.WriteLine(
                    "Stopping Iceburg...");

                Environment.Exit(0);
                break;
            case "":
                break;
            default:
                Console.WriteLine(
                    $"Unknown command: {input}");
                break;
        }
    }
});
app.Urls.Add("http://0.0.0.0:80");
app.Urls.Add("https://0.0.0.0:443");


app.Run();


// HELPERS
// ============================================================
static string? GetSafeReturnUrl( string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return null;
    }

    returnUrl =
        returnUrl.Trim();


    // Must be a local path.
    if (!returnUrl.StartsWith("/"))
    {
        return null;
    }


    // Prevent URLs such as:
    //
    //     //evil.example.com
    //
    if (returnUrl.StartsWith("//"))
    {
        return null;
    }


    // Never redirect into an API endpoint.
    if (returnUrl.StartsWith(
            "/api/",
            StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }


    // Never redirect back to login.
    if (returnUrl.Equals(
            "/login.html",
            StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }


    return returnUrl;
}
static bool IsAdminPage(string returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return false;
    }

    var path =
        returnUrl.Split(
            '?',
            '#')[0];

    return
        path.Equals(
            "/admin.html",
            StringComparison.OrdinalIgnoreCase)
        ||
        path.StartsWith(
            "/admin/",
            StringComparison.OrdinalIgnoreCase);
}
public record LoginChallengeRequest(string? Username);
public record LoginRequest( string? Username,string? ChallengeId,string? Proof,string? ReturnUrl);
public record LogoutRequest(string? ReturnUrl);
public record SetNameRequest(string Input,string Name);
public record SetRouteRequest(string Input,string Output);
public sealed class LoginChallenge
{
    public string Username { get; set; } = "";

    public byte[] Challenge { get; set; } =
        Array.Empty<byte>();

    public DateTime ExpiresAt { get; set; }
}