using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Iceburg.Database;
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
Config.Initialize();
if (Config.GetUsers().Count == 0)
{
    Config.AddUser(
        "admin",
        "admin",
        "Admin");

    Console.WriteLine(
        "Created initial Iceburg admin account.");
}
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
            Config.GetUser(username);

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
            Config.GetUser(username);

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


    return Iceburg.Database.Config.Devices;
});

// Video Hub API
var routerApi = app.MapGroup("/api/router").RequireAuthorization();

routerApi.MapGet( "/bmd/{id}/getinfo", (string id) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.getinfo(id);
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
                $"Starting Web Interface at: http://{addr.Address}");
        }
    }
}

//Iceburg.Database.Config.AddDevice("route","1","10.176.72.40");

app.Run("http://0.0.0.0:80");


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