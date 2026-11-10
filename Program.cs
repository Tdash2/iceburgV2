
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Iceburg.Database;
using Iceburg.Devices.Status;
using Iceburg.Router.BMD;
using Iceburg.Mixer.X32;
using Iceburg.Conversion.AJA.fs4;
using Iceburg.Conversion.AJA.fs2;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

using System.Net;
using System.Data;
using Iceburg.Conversion.Cobalt.OG9905;
using System.Threading;
using System.Reflection;
using Iceburg.Interfaces.BMD.Panle40;
using Iceburg.Monitor.BMD.smartscope;
using Iceburg.Conversion.Magewell.Decoder;
using Iceburg.Mixer.Aquastreams.Playback;
using Iceburg.Conversion.Iceburg.Testgen;
using Microsoft.AspNetCore.Http.Features;
using Iceburg.Mixer.Zowietek;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Xml.Linq;
using System.Text;
bool debug = false;
bool debuglogin = false;

var routerPanelsFile = Path.Combine(AppContext.BaseDirectory, "routerpanles.json");
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "DataProtection-Keys"))).SetApplicationName("Iceburg");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter()
    );
});

string ProgramVerson = "2.1.0";

// ============================================================
// DATABASE
// ============================================================
Database.Initialize();

TallyDatabase.Initialize();  // tally database loaded

TallyDatabase.SyncDevices(); // copies Type 1 + Type 2 devices
SyncManager sync = new SyncManager();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(80);

    options.ListenAnyIP(443, listenOptions =>
    {
        listenOptions.UseHttps(
            "iceburg.pfx",
            "admin");
    });
});

builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = 443;
});

const long maxUploadSize = 25L * 1024 * 1024 * 1024; // 25 GiB

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadSize;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadSize;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "IceburgAuthv2";

    // JavaScript cannot read the authentication cookie.
    options.Cookie.HttpOnly = true;

    // Helps protect against CSRF.
    options.Cookie.SameSite = SameSiteMode.Strict;

    // Automatically uses Secure when HTTPS is used.
    options.Cookie.SecurePolicy =
        CookieSecurePolicy.SameAsRequest;

    // Fixed 30 minute session.
    options.ExpireTimeSpan =
        TimeSpan.FromMinutes(120);

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

        // Preserve the exact page the user was trying
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


    // --------------------------------------------------------
    // COOKIE VALIDATION
    // --------------------------------------------------------

    options.Events.OnValidatePrincipal = context =>
    {
        // Get the user ID stored in the cookie.
        string? userId =
            context.Principal?
                .FindFirst(ClaimTypes.NameIdentifier)?
                .Value;

        // No user ID = invalid authentication cookie.
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.RejectPrincipal();

            return Task.CompletedTask;
        }

        // Look up the CURRENT user from config.json.
        User? user = Database.GetUserById(userId);

        // User no longer exists.
        if (user == null)
        {
            context.RejectPrincipal();

            return context.HttpContext.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
        }

        // User still exists.
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization();
var app = builder.Build();


// ============================================================
// HTTPS REDIRECT
// ============================================================
string debugid = ""; // Empty = show all; e.g. "/tally"

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

    if (debug)
    {
        var stopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        await next();

        stopwatch.Stop();

        string url =
            context.Request.Path +
            context.Request.QueryString;

        // Empty ID matches everything; otherwise filter by URL.
        if (string.IsNullOrWhiteSpace(debugid) ||
            url.Contains(debugid, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"{context.Request.Host} {context.Request.Method} " +
                $"{url} - " +
                $"{stopwatch.Elapsed.TotalMilliseconds:F2}ms");

            TerminalOutputHub.Broadcast($"{context.Request.Host} {context.Request.Method} " +
                $"{url} - " +
                $"{stopwatch.Elapsed.TotalMilliseconds:F2}ms");
        }
    }
    else
    {
        await next();
    }
});

app.UseAuthentication();

// ============================================================
// IP AUTO LOGIN
// ============================================================

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        var remoteIp =
            context.Connection.RemoteIpAddress;

        if (debuglogin)
        {
            TerminalOutputHub.Broadcast("Auto Login IP: " + remoteIp);
            Console.WriteLine("Auto Login IP: " + remoteIp);
        }

        if (remoteIp != null)
        {
            var users =
                Database.GetUsers();

            var user =
                users.FirstOrDefault(u =>
                    u.AutoLoginIps.Any(ipString =>
                    {
                        if (IPAddress.TryParse(
                                ipString,
                                out var configuredIp))
                        {
                            return configuredIp.Equals(remoteIp);
                        }

                        return false;
                    }));

            if (user != null)
            {
                var claims = new List<Claim>
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

                await context.SignInAsync(
                    CookieAuthenticationDefaults
                        .AuthenticationScheme,
                    principal);


            }
        }
    }

    await next();
});

app.UseAuthorization();

// ============================================================
// PROTECTED HTML PAGES
// ============================================================
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
// ============================================================
// ADMIN AREA PROTECTION
// ============================================================

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Redirect("/login.html");
            return;
        }

        if (!IsAdmin(context))
        {
            context.Response.Redirect("/");
            return;
        }
    }

    await next();
});

// STATIC FILES
app.UseDefaultFiles();
app.UseStaticFiles();
var loginChallenges = new ConcurrentDictionary<string, LoginChallenge>();

app.MapPost("/api/login/challenge", (LoginChallengeRequest request) =>
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

app.MapPost("/api/login", async (LoginRequest request, HttpContext httpContext) =>
{
    // --------------------------------------------------------
    // Validate request.
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



    var user2 = Database.GetUser(username);

    if (user2 == null)
    {
        return Results.Unauthorized();
    }

    // Accounts with AutoLoginIps are not allowed to use
    // username/password authentication.
    if (user2.AutoLoginIps != null && user2.AutoLoginIps.Count > 0)
    {
        return Results.Unauthorized();
    }

    // Continue with your existing password verification...


    // --------------------------------------------------------
    // Retrieve and REMOVE challenge.
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
    // HMAC-SHA256(
    //     storedVerifier,
    //     challenge
    // )
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


    // --------------------------------------------------------
    // CREATE AUTH COOKIE
    // --------------------------------------------------------

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
            returnUrl ??
            "/dashboard.html";
    }


    // --------------------------------------------------------
    // USER
    // --------------------------------------------------------

    else
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) &&
            !IsAdminPage(returnUrl))
        {
            redirect =
                returnUrl;
        }
        else
        {
            redirect =
                "/dashboard.html";
        }
    }


    return Results.Ok(new
    {
        success = true,
        redirect
    });
});

app.MapGet("/api/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(
        CookieAuthenticationDefaults
            .AuthenticationScheme);

    return Results.Ok(new
    {
        success = true
    });
});

app.MapPost("/api/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(
        CookieAuthenticationDefaults
            .AuthenticationScheme);

    return Results.Ok(new
    {
        success = true
    });
});

//User managment API
static bool IsAdmin(HttpContext context)
{
    if (context.User.Identity?.IsAuthenticated != true)
        return false;

    string? userId =
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return false;

    User? user = Database.GetUserById(userId);

    return user != null &&
           string.Equals(
               user.Role,
               "Admin",
               StringComparison.OrdinalIgnoreCase);
}

static string? GetCurrentUserId(HttpContext context)
{
    return context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}

static bool CanAccessDevice(HttpContext context, string deviceId)
{
    // Admins retain full access.
    if (IsAdmin(context))
        return true;

    var userId = GetCurrentUserId(context);
    return !string.IsNullOrWhiteSpace(userId) &&
           Database.IsDeviceAllowedForUser(userId, deviceId);
}

 static async ValueTask<object?> RequireAllowedDevice(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    if (IsAdmin(context.HttpContext))
        return await next(context);

    // Device-specific groups below all use {id} in their route.
    var deviceId = context.HttpContext.Request.RouteValues["id"]?.ToString();

    if (string.IsNullOrWhiteSpace(deviceId) ||
        !CanAccessDevice(context.HttpContext, deviceId))
    {
        return Results.Forbid();
    }

    return await next(context);
}

app.MapPut("/api/users/{id}", (string id, HttpContext httpContext, UserUpdateRequest request) =>
{
    // --------------------------------------------------------
    // Admin only
    // --------------------------------------------------------

    if (!IsAdmin(httpContext))
    {
        return Results.Forbid();
    }


    // --------------------------------------------------------
    // Make sure the user exists
    // --------------------------------------------------------

    User? existingUser = Database.GetUserById(id);

    if (existingUser == null)
    {
        return Results.NotFound(new
        {
            error = "User not found."
        });
    }


    // --------------------------------------------------------
    // Validate username
    // --------------------------------------------------------

    if (string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.BadRequest(new
        {
            error = "Username is required."
        });
    }


    // --------------------------------------------------------
    // Normalize role
    //
    // Only Admin or User are allowed.
    // --------------------------------------------------------

    string role =
        string.Equals(
            request.Role,
            "Admin",
            StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "User";


    // --------------------------------------------------------
    // Change password only if a new password was supplied
    // --------------------------------------------------------

    bool hasNewPassword =
        !string.IsNullOrWhiteSpace(request.PasswordHash) &&
        !string.IsNullOrWhiteSpace(request.PasswordSalt);

    if (hasNewPassword)
    {
        // Validate the password material first.
        if (!Database.IsValidPasswordMaterial(
                request.PasswordSalt,
                request.PasswordHash))
        {
            return Results.BadRequest(new
            {
                error = "Invalid password."
            });
        }


        // Change the password.
        bool passwordChanged =
            Database.ChangePassword(
                id,
                request.PasswordSalt,
                request.PasswordHash);

        if (!passwordChanged)
        {
            return Results.NotFound(new
            {
                error = "User not found."
            });
        }
    }


    // --------------------------------------------------------
    // Update username, role and Auto Login IPs
    // --------------------------------------------------------

    User? updated = Database.UpdateUser(
        id,
        request.Username,
        role,
        request.AutoLoginIps ?? new List<string>(),
        request.NavOptions,
        request.AllowedDevices);


    // --------------------------------------------------------
    // Handle update failure
    // --------------------------------------------------------

    if (updated == null)
    {
        return Results.Conflict(new
        {
            error =
                "Username already exists or user was not found."
        });
    }


    // --------------------------------------------------------
    // Return updated user
    // --------------------------------------------------------

    return Results.Ok(updated);
})
.RequireAuthorization();

app.MapGet("/api/users", (HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    return Results.Ok(Database.GetUsers());
})
.RequireAuthorization();

app.MapPost("/api/users", (HttpContext httpContext, UserCreateRequest request) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    if (string.IsNullOrWhiteSpace(request.Username))
        return Results.BadRequest(new { error = "Username is required." });

    if (!Database.IsValidPasswordMaterial(request.PasswordSalt, request.PasswordHash))
        return Results.BadRequest(new { error = "Invalid password." });

    string role = string.Equals(request.Role, "Admin", StringComparison.OrdinalIgnoreCase)
        ? "Admin"
        : "User";

    User? user = Database.AddUserWithPasswordHash(
        request.Username,
        request.PasswordSalt,
        request.PasswordHash,
        role,
        request.AutoLoginIps ?? new List<string>());

    if (user == null)
        return Results.Conflict(new { error = "A user with that username already exists." });

    // Persist the optional custom navigation and device permissions after creation.
    if (!Database.UpdateUserNavigationAndDevices(
            user.Id,
            request.NavOptions,
            request.AllowedDevices))
    {
        Database.RemoveUser(user.Id);
        return Results.Problem(
            "The user could not be created because their navigation/device settings could not be saved.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(Database.GetUserById(user.Id));
})
.RequireAuthorization();

app.MapDelete("/api/users/{id}", (string id, HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    string? currentUserId =
        httpContext.User
            .FindFirst(ClaimTypes.NameIdentifier)
            ?.Value;

    if (string.Equals(
            currentUserId,
            id,
            StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            error = "You cannot delete your own account."
        });
    }

    if (!Database.RemoveUser(id))
    {
        return Results.NotFound(new
        {
            error = "User not found."
        });
    }

    return Results.Ok(new
    {
        success = true
    });
})
.RequireAuthorization();


//database


app.MapGet(
    "/api/push/{serverip}/{username}/{password}",
    async (
        string serverip,
        string username,
        string password,
        HttpContext httpContext) =>
    {
        // --------------------------------------------------------
        // CHECK LOCAL ADMIN
        // --------------------------------------------------------

  

        if (string.IsNullOrWhiteSpace(serverip) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            return Results.BadRequest(new
            {
                Success = false,
                Message = "Server IP, username and password are required."
            });
        }

        // --------------------------------------------------------
        // DESTINATION
        // --------------------------------------------------------

        if (!Uri.TryCreate(
            "https://" + serverip,
            UriKind.Absolute,
            out Uri? serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps &&
             serverUri.Scheme != Uri.UriSchemeHttp) ||
            serverUri.UserInfo.Length > 0 ||
            serverUri.Query.Length > 0 ||
            serverUri.Fragment.Length > 0 ||
            serverUri.AbsolutePath != "/")
        {
            return Results.BadRequest(new
            {
                Success = false,
                Message = "Invalid destination server address."
            });
        }

        try
        {
            // ----------------------------------------------------
            // HTTP CLIENT WITH COOKIE SUPPORT
            // ----------------------------------------------------
            string baseUrl = $"https://{serverip}";
            CookieContainer cookies = new();

            using HttpClientHandler handler = new()
            {
                CookieContainer = cookies,
                UseCookies = true,

                // Accept certificates even if they are invalid, expired,
                // self-signed, or untrusted.
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using HttpClient client = new(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            
            var jsonOptions = new JsonSerializerOptions(
                JsonSerializerDefaults.Web);

            // ----------------------------------------------------
            // STEP 1: REQUEST LOGIN CHALLENGE
            // ----------------------------------------------------

            var challengeResponse = await client.PostAsJsonAsync(
               baseUrl + "/api/login/challenge",
                new
                {
                    Username = username
                },
                jsonOptions
            );

            if (!challengeResponse.IsSuccessStatusCode)
            {
                return Results.BadRequest(new
                {
                    Success = false,
                    Message = "Destination server rejected the login challenge.",
                    StatusCode = (int)challengeResponse.StatusCode
                });
            }

            using JsonDocument challengeDocument =
                JsonDocument.Parse(
                    await challengeResponse.Content.ReadAsStringAsync());

            JsonElement challengeRoot = challengeDocument.RootElement;

            string challengeId =
                challengeRoot.GetProperty("challengeId").GetString()!;

            string saltString =
                challengeRoot.GetProperty("salt").GetString()!;

            string challengeString =
                challengeRoot.GetProperty("challenge").GetString()!;

            // ----------------------------------------------------
            // STEP 2: DERIVE PASSWORD VERIFIER
            //
            // Must match your existing login.html:
            // PBKDF2-SHA256, 600000 iterations, 32 bytes.
            // ----------------------------------------------------

            byte[] salt = Convert.FromBase64String(saltString);

            byte[] verifier = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                600000,
                HashAlgorithmName.SHA256,
                32
            );

            // ----------------------------------------------------
            // STEP 3: GENERATE CHALLENGE PROOF
            //
            // HMAC-SHA256(verifier, challenge)
            // ----------------------------------------------------

            byte[] challenge = Convert.FromBase64String(
                challengeString);

            byte[] proof;

            using (HMACSHA256 hmac = new(verifier))
            {
                proof = hmac.ComputeHash(challenge);
            }

            string proofString = Convert.ToBase64String(proof);

            // ----------------------------------------------------
            // STEP 4: AUTHENTICATE WITH DESTINATION
            // ----------------------------------------------------

            var loginResponse = await client.PostAsJsonAsync(
                 baseUrl + "/api/login",
                new
                {
                    Username = username,
                    ChallengeId = challengeId,
                    Proof = proofString
                },
                jsonOptions
            );

            if (!loginResponse.IsSuccessStatusCode)
            {
                return Results.BadRequest(new
                {
                    Success = false,
                    Message = "Destination server authentication failed.",
                    StatusCode = (int)loginResponse.StatusCode
                });
            }

            // The CookieContainer automatically retains the
            // authentication cookie issued by the destination.

            // ----------------------------------------------------
            // STEP 5: EXPORT LOCAL DATABASE
            // ----------------------------------------------------

            string databaseJson = Database.ExportJson();

            // ----------------------------------------------------
            // STEP 6: IMPORT DATABASE ON DESTINATION
            // ----------------------------------------------------

            using StringContent content = new(
                databaseJson,
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var importResponse = await client.PostAsync(
                 baseUrl + "/api/database/import",
                content
            );

            string importResult =
                await importResponse.Content.ReadAsStringAsync();

            if (!importResponse.IsSuccessStatusCode)
            {
                return Results.BadRequest(new
                {
                    Success = false,
                    Message = "Destination authenticated, but database import failed.",
                    StatusCode = (int)importResponse.StatusCode,
                    Response = importResult
                });
            }

            // ========================================================
            // PUSH TALLY DATABASE
            // ========================================================

            string tallyJson = TallyDatabase.ExportJson();

            var tallyImportResponse = await client.PostAsync(
                baseUrl + "/api/tally-database/import",
                new StringContent(
                    tallyJson,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!tallyImportResponse.IsSuccessStatusCode)
            {
                string tallyError =
                    await tallyImportResponse.Content.ReadAsStringAsync();

                return Results.Problem(
                    $"Main database synced, but Tally database sync failed: {tallyError}",
                    statusCode: 502
                );
            }
            // Sync Router Panels using the existing receive endpoint
            string routerPanelsJson = await File.ReadAllTextAsync(routerPanelsFile);

            var routerPanelsResponse = await client.PostAsync(
               baseUrl + "/api/routerpanels/",
                new StringContent(
                    routerPanelsJson,
                    Encoding.UTF8,
                    "application/json"
                )
            );



            if (!routerPanelsResponse.IsSuccessStatusCode)
            {
                string routerPanelsError =
                    await routerPanelsResponse.Content.ReadAsStringAsync();

                return Results.Problem(
                    $"Main database and Tally database synced, but Router Panels sync failed: {routerPanelsError}",
                    statusCode: 502
                );
            }

            // ----------------------------------------------------
            // SUCCESS
            // ----------------------------------------------------

            return Results.Ok(new
            {
                Success = true,
                Message = "Database successfully pushed to destination server.",
                Destination = serverUri.Host
            });
        }
        catch (TaskCanceledException)
        {
            return Results.Problem(
                "Destination server timed out.",
                statusCode: 504
            );
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(
                $"Could not connect to destination server: {ex.Message}",
                statusCode: 502
            );
        }
        catch (Exception ex)
        {
            return Results.Problem(
                $"Push failed: {ex.Message} {ex.StackTrace}",
                statusCode: 500
            );
        }
    });


// ========================================================
// TALLY DATABASE EXPORT / IMPORT
// ========================================================

app.MapGet("/api/tally-database/export",
    (HttpContext context) =>
    {
        if (!IsAdmin(context))
            return Results.Forbid();

        string json = TallyDatabase.ExportJson();

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(json),
            "application/json",
            "iceburg-tally-database.json");
    })
    .RequireAuthorization();

app.MapPost("/api/tally-database/import",async (HttpContext context) =>
    {
        if (sync.IsPrimaryServer)
            return Results.NotFound();
        if (!IsAdmin(context))
            return Results.Forbid();

        using var reader = new StreamReader(context.Request.Body);

        string json = await reader.ReadToEndAsync();

        if (!TallyDatabase.ImportJson(json, out string error))
        {
            return Results.BadRequest(new
            {
                Success = false,
                Message = error
            });
        }

        return Results.Ok(new
        {
            Success = true,
            Message = "Tally database imported successfully."
        });
    })
    .RequireAuthorization();

// ============================================================
// DATABASE - EXPORT
// ============================================================

app.MapGet("/api/database/export", (HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    string json = Database.ExportJson();

    return Results.File(
        Encoding.UTF8.GetBytes(json),
        "application/json",
        "iceburg-database.json"
    );
});

// ============================================================
// DATABASE - IMPORT
// ============================================================

app.MapPost("/api/database/import", async (HttpContext httpContext) =>
{
    if (sync.IsPrimaryServer)
        return Results.NotFound();
    if (!IsAdmin(httpContext))
        return Results.Forbid();



    try
    {
        using StreamReader reader = new(
            httpContext.Request.Body
        );

        string json = await reader.ReadToEndAsync();

        if (!Database.ImportJson(json, out string error))
        {
            return Results.BadRequest(new
            {
                Success = false,
                Message = error
            });
        }

        return Results.Ok(new
        {
            Success = true,
            Message = "Database imported successfully."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            $"Database import failed: {ex.Message}"
        );
    }
});


//Current User
app.MapGet("/api/me", (HttpContext httpContext) =>
{
    if (httpContext.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        id =
            httpContext.User
                .FindFirst(
                    ClaimTypes.NameIdentifier)
                ?.Value,

        username =
            httpContext.User.Identity?.Name,
        verson = "Iceburg V" + ProgramVerson,

        role = httpContext.User.FindFirst(ClaimTypes.Role)?.Value
    });
})
.RequireAuthorization();

//navbar elemetns
app.MapGet("/api/nav", async (HttpContext httpContext) =>
{
    var userId = GetCurrentUserId(httpContext);
    var user = string.IsNullOrWhiteSpace(userId)
        ? null
        : Database.GetUserById(userId);

    if (user == null)
        return Results.Unauthorized();

    var navigation = new List<NavItem>();
    string iconlink = "/dashboard.html";
    // If this user has configured custom navigation, use it.
    // Otherwise retain the existing default Tally and Panels navigation.
    if (user.NavOptions != null && user.NavOptions.Count > 0)
    {
        iconlink = "";
        foreach (var option in user.NavOptions)
        {
            if (option == null ||
                string.IsNullOrWhiteSpace(option.Text) ||
                string.IsNullOrWhiteSpace(option.Location))
                continue;

            var customLocation = option.Location.Trim();
            if (!customLocation.StartsWith("/", StringComparison.Ordinal) ||
                customLocation.StartsWith("//", StringComparison.Ordinal) ||
                customLocation.StartsWith("/\\", StringComparison.Ordinal))
                continue;

            navigation.Add(new NavItem
            {
                Label = option.Text.Trim(),
                Url = customLocation
            });
        }
    }
    else
    {
        iconlink = "/dashboard.html";
        navigation.Add(new NavItem
        {
            Label = "Tally",
            Children = new List<NavItem>
            {
                new NavItem
                {
                    Label = "Tally Grid",
                    Url = "/tallygrid.html"
                },
                new NavItem
                {
                    Label = "Program Tally Hardware",
                    Children = new List<NavItem>
                    {
                        new NavItem
                        {
                            Label = "Iceburg Tally 8x8",
                            Url = "/upload8x8/"
                        },
                        new NavItem
                        {
                            Label = "Iceburg Tally Ross",
                            Url = "/uploadross/"
                        }
                    }
                }
            }
        });

        var panels = await GetRouterPanels();
        var settings = new NavItem
        {
            Label = "Panels",
            Children = new List<NavItem>()
        };

        foreach (var panel in panels)
        {
            settings.Children.Add(new NavItem
            {
                Label = panel.Name ?? "Router Panel",
                Url = $"/panle.html?id={Uri.EscapeDataString(panel.Id ?? "")}"
            });
        }

        navigation.Add(settings);
    }

    // Keep the administration menu exclusive to admins.
    if (IsAdmin(httpContext))
    {
        navigation.Add(new NavItem
        {
            Label = "Settings",
            Children = new List<NavItem>
            {
                new NavItem { Label = "Users", Url = "/admin/users.html" },
                new NavItem { Label = "Devices", Url = "/admin/devices.html" },
                new NavItem { Label = "Panels", Url = "/admin/routerpanels.html" },
                new NavItem { Label = "Server Shell", Url = "/admin/debug.html" }
            }
        });
    }

    

    return Results.Ok(new
    {
        navigation = navigation,
        iconlink = iconlink,
        primaryserver= sync.IsPrimaryServer
    });
})
.RequireAuthorization();

app.MapGet("/access-denied", () =>
{
    return Results.Json(
        new
        {
            error = "Access denied."
        },
        statusCode: 403);
});

var deviceApi = app.MapGroup("/api/device").RequireAuthorization();

deviceApi.MapGet("/getdevices/", (HttpContext context) =>
{
    var devices = Database.GetDevices()
        .Where(device => CanAccessDevice(context, device.Id))
        .Select(device => new
        {
            device.Id,
            device.Name,
            device.Type,
            device.IpAddress,
            device.AudioSorce,
            device.LastSeen
        })
        .ToList();

    return Results.Ok(devices);
});

// Admin-only device selector used by the Managed Users page.
deviceApi.MapGet("/getdevice", (HttpContext context) =>
{
    if (!IsAdmin(context))
        return Results.Forbid();

    return Results.Ok(Database.GetDevices().Select(device => new
    {
        device.Id,
        device.Name,
        device.Type
    }).ToList());
});

app.MapGet("/api/device/getdevicestatus/{id}", async (string id,HttpContext context) =>
{
    if (!CanAccessDevice(context, id))
        return Results.Forbid();

    var devicetest = new DeviceStatus();
    return Results.Ok(await devicetest.Getstatus(id));
})
.RequireAuthorization();

app.MapGet("/api/devices", (HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    return Results.Ok(Database.GetDevices());
})
.RequireAuthorization();

app.MapPost("/api/devices", (Device device, HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    if (string.IsNullOrWhiteSpace(device.Name))
    {
        return Results.BadRequest(new
        {
            error = "Device name is required."
        });
    }

    if (string.IsNullOrWhiteSpace(device.Type))
    {
        return Results.BadRequest(new
        {
            error = "Device type is required."
        });
    }

    if (string.IsNullOrWhiteSpace(device.IpAddress))
    {
        return Results.BadRequest(new
        {
            error = "IP address is required."
        });
    }

    Device created = Database.AddDevice(
        device.Name,
        device.Type,
        device.IpAddress,
        string.IsNullOrWhiteSpace(device.AudioSorce)
            ? null
            : Guid.TryParse(device.AudioSorce, out Guid audioSource)
                ? audioSource
                : null,
        "",
        device.Username ?? "",
        device.Password ?? ""
    );

    return Results.Ok(created);
})
.RequireAuthorization();

app.MapPut("/api/devices/{id}", (string id, Device device, HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    if (!string.Equals(
        id,
        device.Id,
        StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            error = "Device ID does not match the URL."
        });
    }

    if (string.IsNullOrWhiteSpace(device.Name))
    {
        return Results.BadRequest(new
        {
            error = "Device name is required."
        });
    }

    if (string.IsNullOrWhiteSpace(device.Type))
    {
        return Results.BadRequest(new
        {
            error = "Device type is required."
        });
    }

    if (string.IsNullOrWhiteSpace(device.IpAddress))
    {
        return Results.BadRequest(new
        {
            error = "IP address is required."
        });
    }

    bool updated = Database.EditDevice(device);

    if (!updated)
    {
        return Results.NotFound(new
        {
            error = "Device not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        device = Database.GetDevice(id)
    });
})
.RequireAuthorization();

app.MapDelete("/api/devices/{id}", (string id, HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    if (!Database.RemoveDevice(id))
    {
        return Results.NotFound(new
        {
            error = "Device not found."
        });
    }

    return Results.Ok(new
    {
        success = true
    });
})
.RequireAuthorization();
async Task<List<RouterPanelInfo>> GetRouterPanels()
{
    if (!File.Exists(routerPanelsFile))
        return new List<RouterPanelInfo>();

    using var document = JsonDocument.Parse(
        await File.ReadAllTextAsync(routerPanelsFile));

    if (!document.RootElement.TryGetProperty("panels", out var panels) ||
        panels.ValueKind != JsonValueKind.Array)
        return new List<RouterPanelInfo>();

    return panels.EnumerateArray()
        .Select(panel => new RouterPanelInfo
        {
            Id = panel.TryGetProperty("id", out var id)
                ? id.GetString()
                : null,

            Name = panel.TryGetProperty("name", out var name)
                ? name.GetString()
                : null
        })
        .ToList();
}

app.MapGet("/api/devices/type/", (HttpContext httpContext) =>
{
    var devices = new Dictionary<string, object>
    {
        ["1"] = new
        {
            name = "BMD Router",
            group = "Routing",
            page = "BMDRouter.html",
            tallypage = "routertally.html"
        },

        ["2"] = new
        {
            name = "Tally Device",
            group = "Tally",
            page = "tally.html"
        },

        ["3"] = new
        {
            name = "X32 Mixer",
            group = "Audio",
            page = "x32.html",
            isaudiosorce = true
        },

        ["4"] = new
        {
            name = "AJA FS 4",
            group = "Video",
            page = "ajafs4.html"
        },

        ["5"] = new
        {
            name = "AJA FS 2",
            group = "Video",
            page = "ajafs2.html"
        },

        ["6"] = new
        {
            name = "Cobalt 9905-MPX",
            group = "Video",
            page = "og9905.html"
        },
        ["7"] = new
        {
            name = "Server To Server Tally",
            group = "Tally",
            page = "servertoservertally.html"
        },
        ["8"] = new
        {
            name = "BMD 40 Button Router Panle",
            group = "Control",
            page = "bmd40panle.html"
        }
        ,
        ["9"] = new
        {
            name = "BMD Smart Scope",
            group = "Video",
            page = "BMD-SmartScope.html"
        },
        ["10"] = new
        {
            name = "Magewell Proconvert Decoder",
            group = "Video",
            page = "magewell-decoder.html"
        },
        ["11"] = new
        {
            name = "Aquastreams Playback",
            group = "Audio",
            page = "aquastreamsplayback.html"
        },
        ["12"] = new
        {
            name = "Iceburg Test Generator",
            group = "Video",
            page = "IceburgTestGenerator.html"
        },
        ["13"] = new
        {
            name = "Black Magic Hyperdeck",
            group = "Video",
            page = "hyperdeck.html"
        } ,       
        ["14"] = new
        {
            name = "Zowietek POV",
            group = "Video",
            page = "zowietekpov.html"
        }
    
};

    return Results.Ok(devices);
})
.RequireAuthorization();

//BMD Router
// ============================================================
// VIRTUAL ROUTER PANEL LAYOUT STORAGE + ROUTE ACTIONS
// ============================================================
var routerPanelsApi = app.MapGroup("/api/routerpanels").RequireAuthorization();

routerPanelsApi.MapGet("/", async () =>
{
    if (!File.Exists(routerPanelsFile))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(routerPanelsFile)!);
        await File.WriteAllTextAsync(routerPanelsFile,
            """{"version":1,"panels":[{"id":"panel-default","name":"Router Panel 1","width":1000,"height":600,"buttons":[] }]}""");
    }

    return Results.File(routerPanelsFile, "application/json");
});

routerPanelsApi.MapPost("/", async (HttpRequest request) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("panels", out var panels) ||
            panels.ValueKind != JsonValueKind.Array)
            return Results.BadRequest(new { error = "A panels array is required." });

        if (panels.GetArrayLength() > 100)
            return Results.BadRequest(new { error = "A maximum of 100 panels is supported." });

        var normalized = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(routerPanelsFile)!);
        var temporaryFile = routerPanelsFile + ".tmp";
        await File.WriteAllTextAsync(temporaryFile, normalized);
        File.Move(temporaryFile, routerPanelsFile, true);
        return Results.Ok(new { success = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "The panel configuration is not valid JSON." });
    }
});

// Read route indexes from JSON as integers or numeric strings. Videohub indexes
// are zero-based, so input/output 0 are valid values.
static bool TryReadRouteIndex(JsonElement element, string propertyName, out int value)
{
    value = 0;
    if (element.ValueKind != JsonValueKind.Object)
        return false;

    foreach (var property in element.EnumerateObject())
    {
        if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            continue;

        if (property.Value.ValueKind == JsonValueKind.Number)
            return property.Value.TryGetInt32(out value) && value >= 0;

        if (property.Value.ValueKind == JsonValueKind.String &&
            int.TryParse(property.Value.GetString(), out value))
            return value >= 0;

        return false;
    }

    return false;
}

static string? TryReadRouteDeviceId(JsonElement element)
{
    if (element.ValueKind != JsonValueKind.Object)
        return null;

    foreach (var property in element.EnumerateObject())
    {
        if (string.Equals(property.Name, "deviceId", StringComparison.OrdinalIgnoreCase) &&
            property.Value.ValueKind == JsonValueKind.String)
            return property.Value.GetString();
    }

    return null;
}

static (bool Success, string? Error, JsonElement? Payload) ParseRouterCommandResult(string raw)
{
    try
    {
        using var parsed = JsonDocument.Parse(raw);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (false, "Videohub returned an unexpected response.", null);

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            return (true, null, root.Clone());

        var error = root.TryGetProperty("error", out var errorValue)
            ? errorValue.GetString()
            : "Videohub did not confirm the route.";
        return (false, error, root.Clone());
    }
    catch (JsonException)
    {
        return (false, string.IsNullOrWhiteSpace(raw) ? "Videohub returned an empty response." : raw, null);
    }
}

// A virtual-panel button routes one Videohub input to one output.
routerPanelsApi.MapPost("/route", async (HttpRequest request, HttpContext context) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var root = document.RootElement;
        var deviceId = TryReadRouteDeviceId(root);
        if (string.IsNullOrWhiteSpace(deviceId) ||
            !TryReadRouteIndex(root, "input", out var input) ||
            !TryReadRouteIndex(root, "output", out var output))
            return Results.BadRequest(new { success = false, error = "A Videohub deviceId and non-negative integer input/output indexes are required." });

        if (!CanAccessDevice(context, deviceId))
            return Results.Forbid();

        var router = new BMD_Router();
        var raw = await router.setroute(deviceId, input.ToString(), output.ToString());
        var parsed = ParseRouterCommandResult(raw);
        if (!parsed.Success)
            return Results.Json(new { success = false, error = parsed.Error, deviceId, input, output, routerResponse = parsed.Payload }, statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(new { success = true, deviceId, input, output, routerResponse = parsed.Payload });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { success = false, error = "Invalid route request JSON." });
    }
});

// Execute { routes: [{ deviceId, input, output }, ...] } in order.
// Validate the entire batch before sending any commands; indexes are zero-based.
routerPanelsApi.MapPost("/routes", async (HttpRequest request, HttpContext context) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("routes", out var routes) ||
            routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
            return Results.BadRequest(new { success = false, error = "Request body must contain a non-empty routes array." });

        if (routes.GetArrayLength() > 100)
            return Results.BadRequest(new { success = false, error = "A maximum of 100 routes is supported per button." });

        var validatedRoutes = new List<(string DeviceId, int Input, int Output)>();
        var routeNumber = 0;
        foreach (var route in routes.EnumerateArray())
        {
            routeNumber++;
            var deviceId = TryReadRouteDeviceId(route);
            if (string.IsNullOrWhiteSpace(deviceId) ||
                !TryReadRouteIndex(route, "input", out var input) ||
                !TryReadRouteIndex(route, "output", out var output))
                return Results.BadRequest(new { success = false, error = $"Route {routeNumber} requires deviceId and non-negative integer input/output indexes." });

            validatedRoutes.Add((deviceId, input, output));
        }

        // Authorize every target before executing any part of the batch.
        if (validatedRoutes.Any(route => !CanAccessDevice(context, route.DeviceId)))
            return Results.Forbid();

        var router = new BMD_Router();
        var results = new List<object>();
        for (var index = 0; index < validatedRoutes.Count; index++)
        {
            var route = validatedRoutes[index];
            var raw = await router.setroute(route.DeviceId, route.Input.ToString(), route.Output.ToString());
            var parsed = ParseRouterCommandResult(raw);

            if (!parsed.Success)
            {
                return Results.Json(new
                {
                    success = false,
                    completed = results.Count,
                    total = validatedRoutes.Count,
                    failedRoute = index + 1,
                    results,
                    error = $"Route {index + 1} failed on router {route.DeviceId}: {parsed.Error}",
                    routerResponse = parsed.Payload
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            results.Add(new
            {
                deviceId = route.DeviceId,
                input = route.Input,
                output = route.Output,
                success = true,
                routerResponse = parsed.Payload
            });
        }

        return Results.Ok(new { success = true, completed = results.Count, total = validatedRoutes.Count, results });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { success = false, error = "Invalid multi-route request JSON." });
    }
});




var routerApi = app.MapGroup("/api/router").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);

routerApi.MapGet("/bmd/{id}/getinfo", async (string id) =>
{
    var bmdRouter =
        new BMD_Router();

    return await bmdRouter.getinfo(id);
});

routerApi.MapGet("/bmd/{id}/getnames", (string id) =>
{
    var bmdRouter =
        new BMD_Router();

    return bmdRouter.getnames(id);
});

routerApi.MapGet("/bmd/{id}/getroutes", (string id) =>
{
    var bmdRouter =
        new BMD_Router();

    return bmdRouter.GetRoutes(id);
});

routerApi.MapGet("/bmd/{id}/setinputname/{input}/{name}", (string id, string input, string name) =>
{
    var bmdRouter =
        new BMD_Router();

    return bmdRouter.setinputname(
        id,
        input,
        name);
});

routerApi.MapGet("/bmd/{id}/setoutputname/{input}/{name}", (string id, string input, string name) =>
{
    var bmdRouter =
        new BMD_Router();

    return bmdRouter.setoutputname(
        id,
        input,
        name);
});

routerApi.MapGet("/bmd/{id}/setroute/{Input}/{Output}", (string id, string Input, string Output) =>

{

    var bmdRouter = new BMD_Router();

    return bmdRouter.setroute(
        id,
        Input,
        Output);
});


// ============================================================
// TALLY / GPIO API
// ============================================================

TallyEndpoints.Map(
    app,
    RequireAllowedDevice,
    IsAdmin,
    CanAccessDevice);

// X32 HTTP API endpoints

var mixerapi = app.MapGroup("/api/mixer").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);

mixerapi.MapGet("/x32/{id}/getinfo", (string id) =>
{
    var x32 = new X32();
    return x32.getinfo(id);
});

mixerapi.MapGet("/x32/{id}/getmainmix", (string id) =>
{
    var x32 = new X32();
    return x32.getmainmix(id);
});

mixerapi.MapPost("/x32/{id}/setmainmix", async (HttpRequest request, string id) =>
{
    var body = await request.ReadFromJsonAsync<X32MainMixRequest>();
    if (body is null || body.channel < 1 || body.channel > 32)
        return Results.BadRequest();
    var x32 = new X32();
    return Results.Ok(await x32.setmainmix(id, body.channel, body.gain, body.mute, body.name));
});

mixerapi.MapGet("/x32/{id}/getbus/{bus}", (string id, int bus) =>
{
    var x32 = new X32();
    return x32.getbus(id, bus);
});

mixerapi.MapGet("/x32/{id}/getbusnames", (string id) =>
{
    var x32 = new X32();
    return x32.getbusnames(id);
});

mixerapi.MapPost("/x32/{id}/setbus", async (HttpRequest request, string id) =>
{
    var body = await request.ReadFromJsonAsync<X32BusRequest>();

    if (body is null ||
        body.channel < 1 || body.channel > 32 ||
        body.bus < 1 || body.bus > 16)
        return Results.BadRequest();
    var x32 = new X32();
    return Results.Ok(await x32.setbus(id, body.channel, body.bus, body.gain, body.mute, body.name));
});

mixerapi.MapGet("/x32/{id}/getallnames", (string id) =>
{
    var x32 = new X32();
    return x32.getallnames(id);
});

//FS4-fs2 api
var FS4api = app.MapGroup("/api/Conversion").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);
FS4api.MapGet("/ajafs4/{id}/info", (string id) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Content(
            fs4.GetInfo(),
            "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 404);
    }
});

FS4api.MapGet("/ajafs4/{id}/audio", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.GetConfigurationAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/video", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.GetConfigurationAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/audio/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.GetValuesAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/video/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.GetValuesAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/param/{paramId}", async (string id, string paramId, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.GetParamAsync(
                paramId,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

FS4api.MapPost("/ajafs4/{id}/param/{paramId}", async (string id, string paramId, SetRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        object? value =
           JsonElementToObject(
                request.Value);

        return Results.Json(
            await fs4.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/setparam/{paramId}/{value}", async (string id, string paramId, string value, CancellationToken cancellationToken) =>
{
    try
    {
        var fs4 = new AJAFS4(id);

        return Results.Json(
            await fs4.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

FS4api.MapGet("/ajafs4/{id}/GetMadiSorce/", (string id) =>
{
    Device device = Database.GetDevice(id);

    if (device.AudioSorce != null)
    {
        var x32 = new X32();
        return x32.getallnames(device.AudioSorce);
    }
    else
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            AudioSorce = "NULL"



        }));
    }


});

//FS4-fs2 api
var FS2api = app.MapGroup("/api/Conversion").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);
FS2api.MapGet("/ajafs2/{id}/info", (string id) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Content(
            fs2.GetInfo(),
            "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 404);
    }
});

FS2api.MapGet("/ajafs2/{id}/audio", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.GetConfigurationAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS2api.MapGet("/ajafs2/{id}/video", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.GetConfigurationAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS2api.MapGet("/ajafs2/{id}/audio/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.GetValuesAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS2api.MapGet("/ajafs2/{id}/video/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.GetValuesAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

FS2api.MapGet("/ajafs2/{id}/param/{paramId}", async (string id, string paramId, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.GetParamAsync(
                paramId,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

FS2api.MapPost("/ajafs2/{id}/param/{paramId}", async (string id, string paramId, SetRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        object? value =
           JsonElementToObject(
                request.Value);

        return Results.Json(
            await fs2.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

FS2api.MapGet("/ajafs2/{id}/setparam/{paramId}/{value}", async (string id, string paramId, string value, CancellationToken cancellationToken) =>
{
    try
    {
        var fs2 = new AJAFS2(id);

        return Results.Json(
            await fs2.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

//og9905 api
var og9905api = app.MapGroup("/api/Conversion").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);
og9905api.MapGet("/OG9905MPx/{id}/info", (string id) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Content(
            og9905.GetInfo(),
            "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 404);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/audio", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.GetConfigurationAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/video", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.GetConfigurationAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/audio/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.GetValuesAsync(
                true,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/video/values", async (string id, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.GetValuesAsync(
                false,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/param/{paramId}", async (string id, string paramId, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.GetParamAsync(
                paramId,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

og9905api.MapPost("/OG9905MPx/{id}/param/{paramId}", async (string id, string paramId, SetRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        object? value =
           JsonElementToObject(
                request.Value);

        return Results.Json(
            await og9905.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/setparam/{paramId}/{value}", async (string id, string paramId, string value, CancellationToken cancellationToken) =>
{
    try
    {
        var og9905 = new OG9905MPx(id);

        return Results.Json(
            await og9905.SetParamAsync(
                paramId,
                value,
                cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                error = ex.Message
            },
            statusCode: 500);
    }
});

og9905api.MapGet("/OG9905MPx/{id}/GetMadiSorce/", (string id) =>
{
    Device device = Database.GetDevice(id);

    if (device.AudioSorce != null)
    {
        var x32 = new X32();
        return x32.getallnames(device.AudioSorce);
    }
    else
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            AudioSorce = "NULL"



        }));
    }


});


var bndrouterpnale40 = app.MapGroup("/api/control").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);
//BMD 40 Butotn Panle
bndrouterpnale40.MapGet("/bmd/40panle/{id}/getinfo", (string id) =>
{
    var bmdRouter = new BMD_Panle40();
    return bmdRouter.getinfo(id);
});

bndrouterpnale40.MapGet("/bmd/40panle/{id}/getpanelstatus", (string id) =>
{
    var bmdRouter = new BMD_Panle40();
    return bmdRouter.GetPanelStatus(id);
});

bndrouterpnale40.MapGet("/bmd/40panle/{id}/getnames", (string id) =>
{
    var bmdRouter = new BMD_Panle40();
    return bmdRouter.GetNames(id);
});

bndrouterpnale40.MapGet("/bmd/40panle/{id}/setbutton/{button}/{type}/{target}",
    (string id, string button, string type, string target) =>
    {
        var bmdRouter = new BMD_Panle40();
        return bmdRouter.SetButton(id, button, type, target);
    });

bndrouterpnale40.MapGet("/bmd/40panle/{id}/setvideohub/{ip}",
    (string id, string ip) =>
    {
        var bmdRouter = new BMD_Panle40();
        return bmdRouter.SetVideohub(id, ip);
    });
//BMD Smartscope
var smarscopeapi = app.MapGroup("/api/monitors").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);

smarscopeapi.MapGet("/bmd/{id}/getinfo", (string id) => new BMD_SmartScope().GetInfo(id));

smarscopeapi.MapGet("/bmd/{id}/getstate", (string id) => new BMD_SmartScope().GetState(id));

smarscopeapi.MapGet("/bmd/{id}/set/{monitor}/{param}/{value}", (string id, string monitor, string param, string value) => new BMD_SmartScope().Set(id, monitor, param, value));

smarscopeapi.MapPost("/bmd/{id}/set", async (string id, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    string monitor = form["monitor"].ToString();
    string param = form["param"].ToString();
    string value = form["value"].ToString();
    return await new BMD_SmartScope().Set(id, monitor, param, value);
});

// Magewell Decoder
var decoder = app.MapGroup("/api/decoding").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);

decoder.MapGet("/magewell/{id}", async (string id, HttpRequest request) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.GetStateAsync();
});

decoder.MapPost("/magewell/{id}/source", async (string id, HttpRequest request, SwitchSourceRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SwitchSourceAsync(
        body.SourceType,
        body.SourceName);
});

decoder.MapPost("/magewell/{id}/video-mode", async (string id, HttpRequest request, VideoModeRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SetVideoModeAsync(
        body.Width,
        body.Height,
        body.FieldRate,
        body.AspectRatio,
        body.Interlaced);
});

decoder.MapPost("/magewell/{id}/hdmi-output", async (string id, HttpRequest request, HdmiOutputRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SetHdmiOutputAsync(body.Enabled);
});

//Aquastreams Playback

var aquastreamsplayback = app.MapGroup("/api/aquastreams").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);

aquastreamsplayback.MapGet("/playback/{id}", async (string id) =>
{


    var aquastreamsplayback = new AquastreamsPlayback(id);
    return await aquastreamsplayback.GetButtons();
});

aquastreamsplayback.MapGet("/playback/{id}/status", async (string id) =>
{
    var aquastreamsplayback = new AquastreamsPlayback(id);
    return await aquastreamsplayback.Getstatus();
});

aquastreamsplayback.MapGet("/playback/{id}/toggleplayback", async (string id) =>
{
    var aquastreamsplayback = new AquastreamsPlayback(id);
    await aquastreamsplayback.toggleplayback();


});

aquastreamsplayback.MapGet("/playback/{id}/play", async (string id, HttpRequest request) =>
{
    var aquastreamsplayback = new AquastreamsPlayback(id);

    string parameters = request.QueryString.Value?.TrimStart('?') ?? "";

    await aquastreamsplayback.play(parameters);

});

aquastreamsplayback.MapGet("/playback/{id}/setvol", async (string id, HttpRequest request) =>
{
    var aquastreamsplayback = new AquastreamsPlayback(id);

    string parameters = request.QueryString.Value?.TrimStart('?') ?? "";

    await aquastreamsplayback.setvol(parameters);

});


var testgen = app.MapGroup("/api/testgen").RequireAuthorization().AddEndpointFilter(RequireAllowedDevice);
//Icebug Testgen
testgen.MapGet("/iceburg/{id}/image", async (string id) =>
{
    var Iceburg = new Testgen(id);

    byte[] image = await Iceburg.GetImage();

    return Results.File(image, "image/jpeg");
});

testgen.MapGet("/iceburg/{id}/set_webui", async (string id) =>
{
    var Iceburg = new Testgen(id);

    byte[] image = await Iceburg.GetImage();

    return "";
});

testgen.MapGet("/iceburg/{id}/state", async (string id) =>
{
    var device = new Testgen(id);
    return Results.Content(
        await device.GetState(),
        "application/json");
});

testgen.MapPost("/iceburg/{id}/update", async (
    string id, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();

    var device = new Testgen(id);
    await device.Update(
        form["text1"].ToString(),
        form["text2"].ToString(),
        form["timezone"].ToString());

    return Results.Ok();
});

testgen.MapPost("/iceburg/{id}/mode", async (
    string id, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();

    var device = new Testgen(id);
    await device.SetMode(form["mode"].ToString());

    return Results.Ok();
});

testgen.MapPost("/iceburg/{id}/set_webui", async (
    string id, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();

    bool show = bool.TryParse(
        form["show"].ToString(), out var value) && value;

    var device = new Testgen(id);
    await device.SetWebUi(show);

    return Results.Ok();
});

testgen.MapPost("/iceburg/{id}/mute", async (string id) =>
{
    var device = new Testgen(id);
    await device.ToggleMute();

    return Results.Ok();
});


//hyperdeck


var playback = app.MapGroup("/api/playback")
    .RequireAuthorization()
    .AddEndpointFilter(RequireAllowedDevice);

playback.MapHyperdeckEndpoints();

//zowiepov


var zowietek = app.MapGroup("/api/zowietek")
    .RequireAuthorization()
    .AddEndpointFilter(RequireAllowedDevice);

// GET /api/zowietek/camera/{id}
// Returns settings plus the camera's shutter and white-balance option lists.
zowietek.MapGet("/camera/{id}", async (string id, CancellationToken ct) =>
{
    var camera = new ZowietekPov(id);
    return Results.Ok(await camera.GetSettings(ct));
});

// POST /api/zowietek/camera/{id}
// JSON body: { shutter, wb, gain, saturation, hue, brightness, contrast, sharpness }
zowietek.MapPost("/camera/{id}", async (
    string id,
    ZowietekCameraUpdate update,
    CancellationToken ct) =>
{
    var camera = new ZowietekPov(id);
    await camera.UpdateSettings(update, ct);
    return Results.Ok(new { success = true });
});

// GET /api/zowietek/status/{id}
zowietek.MapGet("/status/{id}", async (string id, CancellationToken ct) =>
{
    var camera = new ZowietekPov(id);
    return Results.Ok(new { online = await camera.GetDeviceStatus(ct) });
});

zowietek.MapGet("/getinfo/{id}", (string id) =>
{
    var pov = new ZowietekPov(id);
    return pov.getinfo(id);
});



//server shell

app.MapGet("/api/terminal/stream", async (
    HttpContext context) =>
{
    if (!IsAdmin(context))
    {
        context.Response.StatusCode =
            StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.ContentType =
        "text/event-stream";
    context.Response.Headers.CacheControl =
        "no-cache";
    context.Response.Headers["X-Accel-Buffering"] =
        "no";

    var reader = TerminalOutputHub.Subscribe(out var clientId);

    try
    {
        await foreach (
            var message in reader.ReadAllAsync(
                context.RequestAborted))
        {
            var data = JsonSerializer.Serialize(message);

            await context.Response.WriteAsync(
                $"data: {data}\n\n",
                context.RequestAborted);

            await context.Response.Body.FlushAsync(
                context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Browser disconnected.
    }
    finally
    {
        TerminalOutputHub.Unsubscribe(clientId);
    }
})
.RequireAuthorization();


app.MapPost("/api/terminal/execute", (
    HttpContext context,
    JsonElement body) =>
{
    if (!IsAdmin(context))
        return Results.Forbid();

    if (!body.TryGetProperty("command", out var value) ||
        value.ValueKind != JsonValueKind.String)
    {
        return Results.BadRequest(new
        {
            error = "Command is required."
        });
    }

    var input = value.GetString() ?? "";

    if (input.Length > 300)
        return Results.BadRequest(new
        {
            error = "Command is too long."
        });

    var output = new List<string>();

    try
    {
        RunIceburgCommand(
            input,
            line => output.Add(line),
            fromWeb: true);

        return Results.Ok(new
        {
            output = string.Join("\n", output)
        });
    }
    catch (Exception)
    {
        return Results.Json(
            new { error = "Command failed." },
            statusCode: 500);
    }
})
.RequireAuthorization();


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
Console.WriteLine("For Help Type \"help\" ");

if (Database.GetUser("localadmin") == null)
{
    Database.AddUser(
           "localadmin",
           "localadminpassword",
           "Admin",
           new List<string> { "::1", "127.0.0.1" });
    Console.WriteLine(
       "Created initial Iceburg Local admin account.");
}


void RunIceburgCommand(string input, Action<string> write, bool fromWeb)
{
    var parts = SplitCommandLine(input);

    if (parts.Count == 0)
        return;

    var command = parts[0].ToLowerInvariant();

    switch (command)
    {
        case "help":

            write("Commands:");
            write(
                "    help");
            write(
                "    device list");
            write(
                "    device add (Name) (IP) (Type)");
            write(
                "    device remove (ID)");
            write(
                "    user list");
            write(
                "    user add (Username) (password) (role)");
            write(
                "    user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
            write(
                "    user remove (ID)");
            write(
                "    exit");

            break;


        case "device":

            if (parts.Count > 1)
            {
                if (parts[1] == "add")
                {
                    write(
                        "Device: add");

                    if (parts.Count != 5)
                    {
                        write(
                            "device add (Name) (IP) (Type)");
                    }
                    else
                    {
                        string Name = parts[2];
                        string IP = parts[3];
                        string type = parts[4];

                        Device device =
                            Database.AddDevice(
                                Name,
                                type,
                                IP);

                        write(
                            $"Added New Device. Device ID: {device.Id}");
                    }
                }

                if (parts[1] == "list")
                {
                    write(
                        $"{"Name",-25} {"IP",-15} {"ID",-20}");

                    write(
                        new string('-', 65));

                    foreach (
                        var device
                        in Database.Devices)
                    {
                        write(
                            $"{device.Name,-25} " +
                            $"{device.IpAddress,-15} " +
                            $"{device.Id,-20}");
                    }
                }

                if (parts[1] == "remove")
                {
                    if (parts.Count != 3)
                    {
                        write(
                            "device remove (ID)");
                    }
                    else
                    {
                        string ID = parts[2];

                        Device deviceToRemove =
                            Database.GetDevice(ID);

                        if (deviceToRemove != null)
                        {
                            Database.RemoveDevice(ID);

                            write(
                                $"Removed Device {ID}");
                        }
                        else
                        {
                            write(
                                "Device Not found");
                        }
                    }
                }
            }
            else
            {
                write(
                    "device list");

                write(
                    "device add (Name) (IP) (Type)");

                write(
                    "device remove (ID)");
            }

            break;


        case "user":

            if (parts.Count > 1)
            {
                if (parts[1] == "add")
                {
                    if (parts.Count != 5)
                    {
                        write(
                            "user add (Username) (password) (role \"Admin\" \"User\")");
                    }
                    else
                    {
                        string username = parts[2];
                        string password = parts[3];
                        string role = parts[4];

                        if (!(role == "Admin" ||
                              role == "User"))
                        {
                            write(
                                "Unknown Role");

                            break;
                        }

                        if (Database.GetUser(username) != null)
                        {
                            write(
                                "Username is already used");

                            break;
                        }

                        User user =
                            Database.AddUser(
                                username,
                                password,
                                role);

                        write(
                            $"Added New User. User ID: {user.Id}");
                    }
                }

                if (parts[1] == "edit")
                {
                    if (parts.Count != 6)
                    {
                        write(
                            "user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
                    }
                    else
                    {
                        string userid = parts[2];
                        string username = parts[3];
                        string password = parts[4];
                        string role = parts[5];

                        if (!(role == "Admin" ||
                              role == "User"))
                        {
                            write(
                                "Unknown Role");

                            break;
                        }

                        if (Database.GetUserById(userid) == null)
                        {
                            write(
                                "Username not found");

                            break;
                        }

                        User user =
                            Database.EditUser(
                                userid,
                                username,
                                password,
                                role);

                        write(
                            $"Edited User. Username: {user.Username} User Role: {user.Role}");
                    }
                }

                if (parts[1] == "list")
                {
                    write(
                        $"{"Name",-25} {"Role",-15} {"ID",-20}");

                    write(
                        new string('-', 65));

                    foreach (
                        var device
                        in Database.Users)
                    {
                        write(
                            $"{device.Username,-25} " +
                            $"{device.Role,-15} " +
                            $"{device.Id,-20}");
                    }
                }

                if (parts[1] == "remove")
                {
                    if (parts.Count != 3)
                    {
                        write(
                            "User remove (ID)");
                    }
                    else
                    {
                        string ID = parts[2];

                        User deviceToRemove =
                            Database.GetUserById(ID);

                        if (deviceToRemove != null)
                        {
                            Database.RemoveUser(ID);

                            write(
                                $"Removed User {ID}");
                        }
                        else
                        {
                            write(
                                "User Not found");
                        }
                    }
                }
            }
            else
            {
                write(
                    "user list");

                write(
                    "user add (Username) (password) (role)");

                write(
                    "user remove (ID)");

                write(
                    "user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
            }

            break;


        case "exit":
            if (fromWeb)
            {
                write("The server cannot be stopped from the web terminal.");
            }
            else
            {
                write("Stopping Iceburg...");
                Environment.Exit(0);
            }

            break;


        case "debug":

            if (parts.Count > 1)
            {
                if (parts[1] == "on")
                {
                    debug = true;
                    debugid = "";
                }
                if (parts[1] == "login")
                {
                    debuglogin = true;
                }

                if (parts[1] == "sync")
                {
                    sync.debug = true;
                }


                if (parts[1] == "off")
                {
                    debug = false;
                    debugid = "";
                    debuglogin = false;
                    sync.debug = false;
                }
            }
            if (parts.Count > 2)
            {
                if (parts[1] == "device")
                {
                    debug = true;
                    debugid = parts[2];

                }
            }

            break;


        case "":
            break;


        default:

            write(
                $"Unknown command: {input}");

            break;
    }
}



_ = Task.Run(async () =>
{
    while (true)
    {
        Console.Write("ICEBURG> ");

        var input = await Console.In.ReadLineAsync();

        if (input == null)
            break;

        try
        {
            RunIceburgCommand(
                input,
                Console.WriteLine,
                fromWeb: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Command failed: {ex.Message}");
        }
    }
});


app.Run();

static object? JsonElementToObject(
   JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.String =>
            value.GetString(),

        JsonValueKind.Number when
            value.TryGetInt64(out long l) =>
            l,

        JsonValueKind.Number when
            value.TryGetDouble(out double d) =>
            d,

        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,

        _ => value.ToString()
    };
}

static string? GetSafeReturnUrl(string? returnUrl)
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
    // //evil.example.com
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
public record LoginRequest(string? Username, string? ChallengeId, string? Proof, string? ReturnUrl);
public sealed class LoginChallenge
{
    public string Username { get; set; } = "";

    public byte[] Challenge { get; set; } =
        Array.Empty<byte>();

    public DateTime ExpiresAt { get; set; }
}
public sealed class X32MainMixRequest
{
    public int channel { get; set; }
    public double? gain { get; set; }
    public bool? mute { get; set; }
    public string? name { get; set; }
}
public sealed class X32BusRequest
{
    public int channel { get; set; }
    public int bus { get; set; }
    public double? gain { get; set; }
    public bool? mute { get; set; }
    public string? name { get; set; }
}
public sealed class SetRequest
{
    public JsonElement Value { get; set; }
}
public class NavItem
{
    public string Label { get; set; } = "";
    public string? Url { get; set; }
    public List<NavItem>? Children { get; set; }
}

public class UserCreateRequest
{
    public string Username { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User";
    public List<string>? AutoLoginIps { get; set; }

    public List<Iceburg.Database.NavigationOption>? NavOptions { get; set; }
    public List<string>? AllowedDevices { get; set; }
}
public class UserUpdateRequest
{
    public string Username { get; set; } = "";
    public string? PasswordSalt { get; set; }
    public string? PasswordHash { get; set; }
    public string Role { get; set; } = "User";
    public List<string>? AutoLoginIps { get; set; }

    public List<Iceburg.Database.NavigationOption>? NavOptions { get; set; }
    public List<string>? AllowedDevices { get; set; }
}
public class DeviceTypeGroup
{
    public string Label { get; set; } = string.Empty;
    public List<DeviceType> Children { get; set; } = new();
}

public class DeviceType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Page { get; set; }
    public string? TallyPage { get; set; }
    public bool IsAudioSorce { get; set; }
}

public sealed record SwitchSourceRequest(
    string SourceType,
    string SourceName);

public sealed record VideoModeRequest(
    int Width,
    int Height,
    int FieldRate,
    double AspectRatio,
    bool Interlaced);

public sealed record HdmiOutputRequest(bool Enabled);
public class RouterPanelInfo
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}
