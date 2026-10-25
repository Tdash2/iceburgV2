
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

bool debug = false;



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

string ProgramVerson = "2.0.2";

// ============================================================
// DATABASE
// ============================================================
Database.Initialize();

TallyDatabase.Initialize();  // tally database loaded

TallyDatabase.SyncDevices(); // copies Type 1 + Type 2 devices


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

        Console.WriteLine(
        
            $"{context.Request.Method} " +
            $"{context.Request.Path}" +
            $"{context.Request.QueryString} - " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F2}ms");
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

        if (debug)
        {
            Console.WriteLine("Auto Login IP: "+remoteIp);
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
var protectedPages =new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

app.MapPost("/api/login/challenge",(LoginChallengeRequest request) =>
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

app.MapPost("/api/login",async (LoginRequest request,HttpContext httpContext) =>
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

app.MapGet("/api/logout",async (HttpContext httpContext) =>
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

app.MapPut("/api/users/{id}",(string id,HttpContext httpContext,UserUpdateRequest request) =>
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
            request.AutoLoginIps ?? new List<string>());


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

app.MapPost("/api/users", (HttpContext httpContext,UserCreateRequest request) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    if (string.IsNullOrWhiteSpace(request.Username))
        return Results.BadRequest(new
        {
            error = "Username is required."
        });

    if (!Database.IsValidPasswordMaterial(
            request.PasswordSalt,
            request.PasswordHash))
    {
        return Results.BadRequest(new
        {
            error = "Invalid password."
        });
    }

    string role =
        string.Equals(
            request.Role,
            "Admin",
            StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "User";

    User? user = Database.AddUserWithPasswordHash(
        request.Username,
        request.PasswordSalt,
        request.PasswordHash,
        role,
        request.AutoLoginIps ?? new List<string>());

    if (user == null)
    {
        return Results.Conflict(new
        {
            error = "A user with that username already exists."
        });
    }

    return Results.Ok(user);
})
.RequireAuthorization();

app.MapDelete("/api/users/{id}", (string id,HttpContext httpContext) =>
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

//Current User
app.MapGet("/api/me",(HttpContext httpContext) =>
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
app.MapGet("/api/nav", (HttpContext httpContext) =>
{
    bool isadminuser = IsAdmin(httpContext);

    var navigation = new List<NavItem>
    {
        new NavItem
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
        }
    };


    // Only show Settings to Admin users
    if (isadminuser)
    {
        navigation.Add(
            new NavItem
            {
                Label = "Settings",
                Children = new List<NavItem>
                {
                    new NavItem
                    {
                        Label = "Users",
                        Url = "/admin/users.html"
                    },

                    new NavItem
                    {
                        Label = "Devices",
                        Url = "/admin/devices.html"
                    }
                }
            }
        );
    }


    return Results.Ok(navigation);
})
.RequireAuthorization();

app.MapGet("/access-denied",() =>
    {
        return Results.Json(
            new
            {
                error = "Access denied."
            },
            statusCode: 403);
    });

var deviceApi =app.MapGroup("/api/device").RequireAuthorization();

deviceApi.MapGet("/getdevices/",() =>
    {
        return Iceburg.Database.Database.Devices;
    });

deviceApi.MapGet("/getdevicestatus/{id}",async (string id) =>
    {
        var devicetest =
            new DeviceStatus();

        return await devicetest.Getstatus(id);
    });

app.MapGet("/api/devices", (HttpContext httpContext) =>
{
    if (!IsAdmin(httpContext))
        return Results.Forbid();

    return Results.Ok(Database.GetDevices());
})
.RequireAuthorization();

app.MapPost("/api/devices", (Device device,HttpContext httpContext) =>
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

app.MapDelete("/api/devices/{id}", (string id,HttpContext httpContext) =>
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
        }
    };

    return Results.Ok(devices);
})
.RequireAuthorization();

//BMD Router
var routerApi = app.MapGroup("/api/router").RequireAuthorization();

routerApi.MapGet("/bmd/{id}/getinfo",async (string id) =>
    {
        var bmdRouter =
            new BMD_Router();

        return await bmdRouter.getinfo(id);
    });

routerApi.MapGet("/bmd/{id}/getnames",(string id) =>
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

routerApi.MapGet("/bmd/{id}/setinputname/{input}/{name}",(string id, string input, string name) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.setinputname(
            id,
            input,
            name);
    });

routerApi.MapGet("/bmd/{id}/setoutputname/{input}/{name}",(string id, string input,string name) =>
    {
        var bmdRouter =
            new BMD_Router();

        return bmdRouter.setoutputname(
            id,
            input,
            name);
    });

routerApi.MapGet("/bmd/{id}/setroute/{Input}/{Output}",(string id, string Input, string Output) =>

    {
        
        var bmdRouter =new BMD_Router();
   
        return bmdRouter.setroute(
            id,
            Input,
            Output);
    });


// ============================================================
// TALLY / GPIO API
// ============================================================

TallyEndpoints.Map(app);

// X32 HTTP API endpoints

var mixerapi =app.MapGroup("/api/mixer").RequireAuthorization();

mixerapi.MapGet("/x32/{id}/getinfo",(string id) =>
{
    var x32 = new X32();
    return x32.getinfo(id);
});

mixerapi.MapGet("/x32/{id}/getmainmix",(string id) =>
{
    var x32 = new X32();
    return x32.getmainmix(id);
});

mixerapi.MapPost("/x32/{id}/setmainmix",async (HttpRequest request, string id) =>
{
    var body = await request.ReadFromJsonAsync<X32MainMixRequest>();
        if (body is null || body.channel < 1 || body.channel > 32)
            return Results.BadRequest();
            var x32 = new X32();
            return Results.Ok(await x32.setmainmix(id,body.channel,body.gain,body.mute,body.name));
});

mixerapi.MapGet("/x32/{id}/getbus/{bus}",(string id, int bus) =>
{
    var x32 = new X32();
    return x32.getbus(id, bus);
});

mixerapi.MapGet("/x32/{id}/getbusnames",(string id) =>
{
    var x32 = new X32();
    return x32.getbusnames(id);
});

mixerapi.MapPost("/x32/{id}/setbus",async (HttpRequest request, string id) =>
{
    var body = await request.ReadFromJsonAsync<X32BusRequest>();

    if (body is null ||
        body.channel < 1 || body.channel > 32 ||
        body.bus < 1 || body.bus > 16)
    return Results.BadRequest();
    var x32 = new X32();
    return Results.Ok(await x32.setbus(id,body.channel,body.bus,body.gain,body.mute,body.name));
});

mixerapi.MapGet("/x32/{id}/getallnames",(string id) =>
    {
        var x32 = new X32();
        return x32.getallnames(id);
    });

//FS4-fs2 api
var FS4api = app.MapGroup("/api/Conversion").RequireAuthorization();
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
var FS2api = app.MapGroup("/api/Conversion").RequireAuthorization();
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
        var fs2= new AJAFS2(id);

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
var og9905api = app.MapGroup("/api/Conversion").RequireAuthorization();
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


var bndrouterpnale40 = app.MapGroup("/api/control").RequireAuthorization();
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
var smarscopeapi = app.MapGroup("/api/monitors").RequireAuthorization();

smarscopeapi.MapGet("/bmd/{id}/getinfo", (string id) => new BMD_SmartScope().GetInfo(id));

smarscopeapi.MapGet("/bmd/{id}/getstate", (string id) => new BMD_SmartScope().GetState(id));

smarscopeapi.MapGet("/bmd/{id}/set/{monitor}/{param}/{value}",(string id, string monitor, string param, string value) => new BMD_SmartScope().Set(id, monitor, param, value));

smarscopeapi.MapPost("/bmd/{id}/set",async (string id, HttpRequest request) =>
    {
        var form = await request.ReadFormAsync();
        string monitor = form["monitor"].ToString();
        string param = form["param"].ToString();
        string value = form["value"].ToString();
        return await new BMD_SmartScope().Set(id, monitor, param, value);
    });

// Magewell Decoder
var decoder = app.MapGroup("/api/decoding").RequireAuthorization();

decoder.MapGet("/magewell/{id}", async (string id, HttpRequest request) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.GetStateAsync();
});

decoder.MapPost("/magewell/{id}/source", async (string id,HttpRequest request,SwitchSourceRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SwitchSourceAsync(
        body.SourceType,
        body.SourceName);
});

decoder.MapPost("/magewell/{id}/video-mode", async (string id,HttpRequest request,VideoModeRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SetVideoModeAsync(
        body.Width,
        body.Height,
        body.FieldRate,
        body.AspectRatio,
        body.Interlaced);
});

decoder.MapPost("/magewell/{id}/hdmi-output", async (string id,HttpRequest request,HdmiOutputRequest body) =>
{
    var magewell = new MagewellDecoder(id);
    return await magewell.SetHdmiOutputAsync(body.Enabled);
});

//Aquastreams Playback

var aquastreamsplayback = app.MapGroup("/api/aquastreams").RequireAuthorization();

aquastreamsplayback.MapGet("/playback/{id}", async (string id) =>
{
    Console.WriteLine("Playback endpoint reached!");

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

aquastreamsplayback.MapGet("/playback/{id}/setvol",async (string id, HttpRequest request) =>
    {
        var aquastreamsplayback = new AquastreamsPlayback(id);

        string parameters = request.QueryString.Value?.TrimStart('?') ?? "";

        await aquastreamsplayback.setvol(parameters);

    });


var testgen = app.MapGroup("/api/testgen").RequireAuthorization();
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
        form["text2"].ToString());

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

_ = Task.Run(async () =>
{
    while (true)
    {
        Console.Write("ICEBURG> ");

        var input =
            await Console.In.ReadLineAsync();

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
                Console.WriteLine(
                    "    help");
                Console.WriteLine(
                    "    device list");
                Console.WriteLine(
                    "    device add (Name) (IP) (Type)");
                Console.WriteLine(
                    "    device remove (ID)");
                Console.WriteLine(
                    "    user list");
                Console.WriteLine(
                    "    user add (Username) (password) (role)");
                Console.WriteLine(
                    "    user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
                Console.WriteLine(
                    "    user remove (ID)");
                Console.WriteLine(
                    "    exit");

                break;


            case "device":

                if (parts.Count > 1)
                {
                    if (parts[1] == "add")
                    {
                        Console.WriteLine(
                            "Device: add");

                        if (parts.Count != 5)
                        {
                            Console.WriteLine(
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

                            Console.WriteLine(
                                $"Added New Device. Device ID: {device.Id}");
                        }
                    }

                    if (parts[1] == "list")
                    {
                        Console.WriteLine(
                            $"{"Name",-25} {"IP",-15} {"ID",-20}");

                        Console.WriteLine(
                            new string('-', 65));

                        foreach (
                            var device
                            in Database.Devices)
                        {
                            Console.WriteLine(
                                $"{device.Name,-25} " +
                                $"{device.IpAddress,-15} " +
                                $"{device.Id,-20}");
                        }
                    }

                    if (parts[1] == "remove")
                    {
                        if (parts.Count != 3)
                        {
                            Console.WriteLine(
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

                                Console.WriteLine(
                                    $"Removed Device {ID}");
                            }
                            else
                            {
                                Console.WriteLine(
                                    "Device Not found");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine(
                        "device list");

                    Console.WriteLine(
                        "device add (Name) (IP) (Type)");

                    Console.WriteLine(
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
                            Console.WriteLine(
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
                                Console.WriteLine(
                                    "Unknown Role");

                                break;
                            }

                            if (Database.GetUser(username) != null)
                            {
                                Console.WriteLine(
                                    "Username is already used");

                                break;
                            }

                            User user =
                                Database.AddUser(
                                    username,
                                    password,
                                    role);

                            Console.WriteLine(
                                $"Added New User. User ID: {user.Id}");
                        }
                    }

                    if (parts[1] == "edit")
                    {
                        if (parts.Count != 6)
                        {
                            Console.WriteLine(
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
                                Console.WriteLine(
                                    "Unknown Role");

                                break;
                            }

                            if (Database.GetUserById(userid) == null)
                            {
                                Console.WriteLine(
                                    "Username not found");

                                break;
                            }

                            User user =
                                Database.EditUser(
                                    userid,
                                    username,
                                    password,
                                    role);

                            Console.WriteLine(
                                $"Edited User. Username: {user.Username} User Role: {user.Role}");
                        }
                    }

                    if (parts[1] == "list")
                    {
                        Console.WriteLine(
                            $"{"Name",-25} {"Role",-15} {"ID",-20}");

                        Console.WriteLine(
                            new string('-', 65));

                        foreach (
                            var device
                            in Database.Users)
                        {
                            Console.WriteLine(
                                $"{device.Username,-25} " +
                                $"{device.Role,-15} " +
                                $"{device.Id,-20}");
                        }
                    }

                    if (parts[1] == "remove")
                    {
                        if (parts.Count != 3)
                        {
                            Console.WriteLine(
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

                                Console.WriteLine(
                                    $"Removed User {ID}");
                            }
                            else
                            {
                                Console.WriteLine(
                                    "User Not found");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine(
                        "user list");

                    Console.WriteLine(
                        "user add (Username) (password) (role)");

                    Console.WriteLine(
                        "user remove (ID)");

                    Console.WriteLine(
                        "user edit (User ID) (New Username) (New password) (role \"Admin\" \"User\")");
                }

                break;


            case "exit":

                Console.WriteLine(
                    "Stopping Iceburg...");

                Environment.Exit(0);

                break;


            case "debug":

                if (parts.Count > 1)
                {
                    if (parts[1] == "on")
                    {
                        debug = true;
                    }

                    if (parts[1] == "off")
                    {
                        debug = false;
                    }
                }

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
public record LoginRequest(string? Username,string? ChallengeId,string? Proof,string? ReturnUrl);
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
}


public class UserUpdateRequest
{
    public string Username { get; set; } = "";

    public string? PasswordSalt { get; set; }

    public string? PasswordHash { get; set; }

    public string Role { get; set; } = "User";

    public List<string>? AutoLoginIps { get; set; }
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