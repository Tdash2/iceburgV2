
using Iceburg.Database;
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class SyncManager
{
    private readonly string configFile =
        Path.Combine(AppContext.BaseDirectory, "sync.json"); private readonly TimeSpan syncInterval = TimeSpan.FromSeconds(5);

    private SyncConfig config;

   private string routerPanelsFile = Path.Combine(AppContext.BaseDirectory, "routerpanles.json");

    // Publicly accessible primary server property
    public bool IsPrimaryServer => config.PrimaryServer;

    public string SecondaryServerIP => config.SecondaryServerIP;

    public string SecondaryServerUser => config.SecondaryServerUser;
    public bool debug =false;

    public SyncManager()
    {
        LoadConfig();
        CancellationTokenSource syncCancellation = new();

        // Start syncing in the background without blocking the application.
        _ = Task.Run(() =>
            StartSyncLoopAsync(syncCancellation.Token)
        );
    }

    // Configuration model
    public class SyncConfig
    {
        public bool PrimaryServer { get; set; } = true;
        public string SecondaryServerIP { get; set; } = "";
        public string SecondaryServerUser { get; set; } = "";
        public string SecondaryServerPass { get; set; } = "";
    }

    // Create or load sync.json
    private void LoadConfig()
    {
        if (!File.Exists(configFile))
        {
            config = new SyncConfig();
            SaveConfig();
            return;
        }

        string json = File.ReadAllText(configFile);

        config = JsonSerializer.Deserialize<SyncConfig>(json)
                 ?? new SyncConfig();
    }

    // Save configuration
    public void SaveConfig()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        string json = JsonSerializer.Serialize(config, options);

        File.WriteAllText(configFile, json);
    }

    // Update configuration
    public void UpdateConfig(
        bool primaryServer,
        string ip,
        string username,
        string password)
    {
        config.PrimaryServer = primaryServer;
        config.SecondaryServerIP = ip;
        config.SecondaryServerUser = username;
        config.SecondaryServerPass = password;

        SaveConfig();
    }

    // Background synchronization loop
    public async Task StartSyncLoopAsync(
      CancellationToken cancellationToken)
    {
       

        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsPrimaryServer &&
                !string.IsNullOrWhiteSpace(config.SecondaryServerIP) &&
                !string.IsNullOrWhiteSpace(config.SecondaryServerUser) &&
                !string.IsNullOrWhiteSpace(config.SecondaryServerPass))
            {
                try
                {
                    if (debug)
                    {
                        Console.WriteLine("Starting server synchronization...");
                        TerminalOutputHub.Broadcast("Starting server synchronization...");
                    }
                   

                    string result = await syncServer(
                        config.SecondaryServerIP,
                        config.SecondaryServerUser,
                        config.SecondaryServerPass
                    );
                    if (debug)
                    {
                        Console.WriteLine("Sync result: " + result);
                        TerminalOutputHub.Broadcast("Sync result: " + result);
                    }
                }
                catch (Exception ex)
                {
                    if (debug)
                    {
                        Console.WriteLine("Sync error: " + ex.Message);
                        TerminalOutputHub.Broadcast("Sync error: " + ex.Message);
                    }
                 }
            }

            await Task.Delay(syncInterval, cancellationToken);
        }
    }

    // Your synchronization method
    private async Task<string> syncServer(string serverip, string username,string password)
       
    {
        // --------------------------------------------------------
        // CHECK LOCAL ADMIN
        // --------------------------------------------------------

  

        if (string.IsNullOrWhiteSpace(serverip) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            return"Server IP, username and password are required.";
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
            return "Invalid destination server address.";
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
                return "Destination server rejected the login challenge.";
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
                return "Destination server authentication failed.";
         
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
                return
                   "Destination authenticated, but database import failed.";
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

                return $"Main database synced, but Tally database sync failed: {tallyError}";
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

                return $"Main database and Tally database synced, but Router Panels sync failed: {routerPanelsError}";
        
    }

            // ----------------------------------------------------
            // SUCCESS
            // ----------------------------------------------------

            return "Database successfully pushed to destination server.";
}
catch (TaskCanceledException)
{
            return "Destination server timed out.";
}
catch (HttpRequestException ex)
{
            return
                $"Could not connect to destination server: {ex.Message}";
    
}
catch (Exception ex)
{
            return $"Push failed: {ex.Message} {ex.StackTrace}";
    
}
    }
}
