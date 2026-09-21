using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Iceburg.Database
{
    public static class Database
    {
        // ============================================================
        // PASSWORD SETTINGS
        // ============================================================

        // Must match the JavaScript login.html.
        private const int PasswordIterations = 600000;
        private const int PasswordSaltSize = 32;
        private const int PasswordHashSize = 32;

        // ============================================================
        // CONFIGURATION FILE
        // ============================================================

        private static readonly string ConfigFilePath =
            Path.Combine(AppContext.BaseDirectory, "config.json");

        private static readonly string ConfigDirectory =
            Path.GetDirectoryName(ConfigFilePath)
            ?? AppContext.BaseDirectory;

        private static readonly string ConfigFileName =
            Path.GetFileName(ConfigFilePath);

        // All database operations use this lock.
        private static readonly object _lock = new();

        // Used to prevent the FileSystemWatcher from causing
        // unnecessary reloads while this application is writing.
        private static bool _isWriting;

        // Used to prevent multiple watcher callbacks from
        // reloading the file simultaneously.
        private static int _reloadPending;

        private static FileSystemWatcher? _fileWatcher;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // ============================================================
        // CONFIGURATION DATA
        // ============================================================

        private static AppConfig _data = new();

        // ============================================================

        // DATA
        // ============================================================

        /// <summary>
        /// Gets the latest application configuration from config.json.
        ///
        /// The file is checked/reloaded every time this property is
        /// accessed so callers always receive the latest information.
        /// </summary>
        public static AppConfig Data
        {
            get
            {
                lock (_lock)
                {
                    ReloadInternal();
                    return _data;
                }
            }
        }

        /// <summary>
        /// Gets a snapshot of the latest devices.
        /// </summary>
        public static List<Device> Devices
        {
            get
            {
                lock (_lock)
                {
                    ReloadInternal();

                    return _data.Devices
                        .Select(CloneDevice)
                        .ToList();
                }
            }
        }
        // ============================================================
        // DEVICE - PRESENCE
        // ============================================================

        private static readonly Dictionary<string, DateTime>
            _lastSeenSaved = new(
                StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Updates a device's IP address and LastSeen time without
        /// calling EditDevice() or TallyDatabase.SyncDevices().
        ///
        /// LastSeen is updated in memory on every call, but the
        /// configuration file is only rewritten periodically.
        /// </summary>
        public static bool UpdateDevicePresence(
            string id,
            string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            lock (_lock)
            {
                ReloadInternal();

                Device? device = _data.Devices
                    .FirstOrDefault(d =>
                        string.Equals(
                            d.Id,
                            id,
                            StringComparison.OrdinalIgnoreCase));

                if (device == null)
                    return false;

                bool ipChanged =
                    !string.IsNullOrWhiteSpace(ipAddress) &&
                    !string.Equals(
                        device.IpAddress,
                        ipAddress,
                        StringComparison.OrdinalIgnoreCase);

                if (ipChanged)
                {
                    device.IpAddress = ipAddress!;
                }

                device.LastSeen = DateTime.Now.ToString();

                // Always save immediately when the IP changes.
                if (ipChanged)
                {
                    SaveInternal();
                    _lastSeenSaved[id] = DateTime.Now;
                    return true;
                }

                // Otherwise only persist LastSeen periodically.
                DateTime now = DateTime.Now;

                if (!_lastSeenSaved.TryGetValue(
                        id,
                        out DateTime lastSaved) ||
                    (now - lastSaved).TotalSeconds >= 5)
                {
                    SaveInternal();
                    _lastSeenSaved[id] = now;
                }

                return true;
            }
        }
        /// <summary>
        /// Gets a snapshot of the latest users.
        ///
        /// Password material is removed from the returned objects.
        /// </summary>
        public static List<User> Users
        {
            get
            {
                lock (_lock)
                {
                    ReloadInternal();

                    return _data.Users
                        .Select(CreatePublicUser)
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Gets the latest application settings.
        /// </summary>
        public static AppSettings Settings
        {
            get
            {
                lock (_lock)
                {
                    ReloadInternal();

                    return CloneSettings(_data.Settings);
                }
            }
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        /// <summary>
        /// Initializes the database and starts monitoring config.json.
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                try
                {
                    EnsureConfigFileExists();
                    ReloadInternal();
                    StartFileWatcher();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error initializing database: {ex.Message}"
                    );

                    _data = new AppConfig();

                    try
                    {
                        SaveInternal();
                        StartFileWatcher();
                    }
                    catch (Exception saveEx)
                    {
                        Console.WriteLine(
                            $"Error creating config.json: {saveEx.Message}"
                        );
                    }
                }
            }

            TallyDatabase.Initialize();
        }

        // ============================================================
        // FILE WATCHER
        // ============================================================

        /// <summary>
        /// Starts monitoring config.json for changes made by
        /// this application or another process.
        /// </summary>
        private static void StartFileWatcher()
        {
            if (_fileWatcher != null)
                return;

            try
            {
                _fileWatcher = new FileSystemWatcher(
                    ConfigDirectory,
                    ConfigFileName
                )
                {
                    NotifyFilter =
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.FileName |
                        NotifyFilters.CreationTime,

                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnConfigFileChanged;
                _fileWatcher.Created += OnConfigFileChanged;
                _fileWatcher.Renamed += OnConfigFileRenamed;
                _fileWatcher.Deleted += OnConfigFileDeleted;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Error starting config watcher: {ex.Message}"
                );
            }
        }

        private static void OnConfigFileChanged(
            object sender,
            FileSystemEventArgs e)
        {
            QueueReload();
        }

        private static void OnConfigFileRenamed(
            object sender,
            RenamedEventArgs e)
        {
            QueueReload();
        }

        private static void OnConfigFileDeleted(
            object sender,
            FileSystemEventArgs e)
        {
            QueueReload();
        }

        /// <summary>
        /// Queues a delayed reload.
        ///
        /// FileSystemWatcher can fire several events while a file
        /// is being written. The delay gives the writer time to
        /// finish before we attempt to deserialize the file.
        /// </summary>
        private static void QueueReload()
        {
            if (Interlocked.Exchange(
                    ref _reloadPending,
                    1) == 1)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Give the file writer time to finish.
                    Thread.Sleep(100);

                    lock (_lock)
                    {
                        if (_isWriting)
                            return;

                        ReloadInternalWithRetry();
                    }
                }
                finally
                {
                    Interlocked.Exchange(
                        ref _reloadPending,
                        0);
                }
            });
        }

        /// <summary>
        /// Attempts to reload config.json several times.
        ///
        /// This helps when another application is in the middle
        /// of writing the file.
        /// </summary>
        private static void ReloadInternalWithRetry()
        {
            const int maxAttempts = 5;

            for (int attempt = 1;
                 attempt <= maxAttempts;
                 attempt++)
            {
                try
                {
                    ReloadInternal();
                    return;
                }
                catch (JsonException ex)
                {
                    if (attempt == maxAttempts)
                    {
                        Console.WriteLine(
                            $"Error reloading config.json: {ex.Message}"
                        );

                        return;
                    }

                    Thread.Sleep(100);
                }
                catch (IOException ex)
                {
                    if (attempt == maxAttempts)
                    {
                        Console.WriteLine(
                            $"Error reading config.json: {ex.Message}"
                        );

                        return;
                    }

                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (attempt == maxAttempts)
                    {
                        Console.WriteLine(
                            $"Access denied reading config.json: {ex.Message}"
                        );

                        return;
                    }

                    Thread.Sleep(100);
                }
            }
        }

        // ============================================================
        // LOAD
        // ============================================================

        /// <summary>
        /// Loads the current contents of config.json.
        ///
        /// IMPORTANT:
        /// Every public lookup calls this method first.
        /// </summary>
        private static void ReloadInternal()
        {
            if (!File.Exists(ConfigFilePath))
            {
                _data = new AppConfig();

                SaveInternal();
                return;
            }

            string json = File.ReadAllText(ConfigFilePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                _data = new AppConfig();
                return;
            }

            AppConfig? loadedData =
                JsonSerializer.Deserialize<AppConfig>(
                    json,
                    JsonOptions
                );

            _data = loadedData ?? new AppConfig();

            EnsureCollections();
        }

        /// <summary>
        /// Makes sure required collections/settings are never null.
        /// </summary>
        private static void EnsureCollections()
        {
            _data.Settings ??= new AppSettings();
            _data.Devices ??= new List<Device>();
            _data.Users ??= new List<User>();
        }

        /// <summary>
        /// Creates config.json if it doesn't exist.
        /// </summary>
        private static void EnsureConfigFileExists()
        {
            if (File.Exists(ConfigFilePath))
                return;

            _data = new AppConfig();
            SaveInternal();
        }

        // ============================================================
        // SAVE
        // ============================================================

        /// <summary>
        /// Saves the current database to config.json.
        /// </summary>
        public static void Save()
        {
            lock (_lock)
            {
                SaveInternal();
            }
        }

        /// <summary>
        /// Writes the in-memory database to config.json.
        ///
        /// This method assumes _lock is already held.
        /// </summary>
        private static void SaveInternal()
        {
            try
            {
                EnsureCollections();

                string json =
                    JsonSerializer.Serialize(
                        _data,
                        JsonOptions
                    );

                _isWriting = true;

                // Write to a temporary file first.
                //
                // This prevents another process from seeing a
                // partially-written JSON document.
                string tempFilePath =
                    ConfigFilePath + ".tmp";

                File.WriteAllText(
                    tempFilePath,
                    json
                );

                // Replace the original file with the completed file.
                //
                // Delete + Move is used instead of File.Replace because
                // File.Replace is not supported on every filesystem.
                if (File.Exists(ConfigFilePath))
                {
                    File.Delete(ConfigFilePath);
                }

                File.Move(
                    tempFilePath,
                    ConfigFilePath
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Error saving config: {ex.Message}"
                );
            }
            finally
            {
                _isWriting = false;
            }
        }

        // ============================================================
        // USERS - ADD
        // ============================================================

        /// <summary>
        /// Adds a new user.
        ///
        /// The supplied plaintext password is immediately converted
        /// to a salted PBKDF2 verifier. Only the salt and verifier are
        /// stored in config.json.
        ///
        /// IMPORTANT:
        /// This method is intended for trusted server-side operations
        /// such as initial setup. Browser-created users should use the
        /// AddUserWithPasswordHash method below so plaintext passwords
        /// never need to be sent to the server.
        /// </summary>
        public static User? AddUser(
            string username,
            string password,
            string role = "User")
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException(
                    "Username cannot be empty.",
                    nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException(
                    "Password cannot be empty.",
                    nameof(password));

            role = NormalizeRole(role);

            lock (_lock)
            {
                // Always get the newest version before modifying it.
                ReloadInternal();

                // Don't allow duplicate usernames.
                if (_data.Users.Any(u =>
                    string.Equals(
                        u.Username,
                        username,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                // Generate a unique random salt.
                byte[] salt =
                    RandomNumberGenerator.GetBytes(
                        PasswordSaltSize);

                // Derive the password verifier.
                byte[] hash =
                    Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        salt,
                        PasswordIterations,
                        HashAlgorithmName.SHA256,
                        PasswordHashSize);

                User user = new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Username = username.Trim(),
                    PasswordSalt =
                        Convert.ToBase64String(salt),
                    PasswordHash =
                        Convert.ToBase64String(hash),
                    Role = role
                };

                _data.Users.Add(user);

                SaveInternal();

                return CreatePublicUser(user);
            }
        }


        // ============================================================
        // USERS - EDIT
        // ============================================================

        /// <summary>
        /// Fully edits an existing user.
        ///
        /// Updates:
        /// - Username
        /// - Role
        /// - Password
        ///
        /// The plaintext password is converted into a salted PBKDF2
        /// verifier and is never stored directly.
        ///
        /// Returns the updated User, or null if the edit failed.
        /// </summary>
        public static User? EditUser(
            string id,
            string username,
            string password,
            string role)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            if (string.IsNullOrWhiteSpace(username))
                return null;

            if (string.IsNullOrWhiteSpace(password))
                return null;

            if (string.IsNullOrWhiteSpace(role))
                return null;

            role = NormalizeRole(role);

            // Generate a new random salt.
            byte[] salt =
                RandomNumberGenerator.GetBytes(
                    PasswordSaltSize);

            // Generate the password hash.
            byte[] hash =
                Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    PasswordIterations,
                    HashAlgorithmName.SHA256,
                    PasswordHashSize);

            string passwordSalt =
                Convert.ToBase64String(salt);

            string passwordHash =
                Convert.ToBase64String(hash);

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                User? user = _data.Users
                    .FirstOrDefault(u => u.Id == id);

                if (user == null)
                    return null;

                // Don't allow the username to conflict
                // with another existing user.
                bool usernameExists = _data.Users.Any(u =>
                    u.Id != id &&
                    string.Equals(
                        u.Username,
                        username.Trim(),
                        StringComparison.OrdinalIgnoreCase));

                if (usernameExists)
                    return null;

                // Update username.
                user.Username = username.Trim();

                // Update role.
                user.Role = role;

                // Update password.
                user.PasswordSalt = passwordSalt;
                user.PasswordHash = passwordHash;

                // Save changes.
                SaveInternal();

                return user;
            }
        }


        // ============================================================
        // USERS - ADD WITH HASH
        // ============================================================

        /// <summary>
        /// Adds a user using password material already generated
        /// by the browser.
        ///
        /// No plaintext password is accepted by this method.
        /// </summary>
        public static User? AddUserWithPasswordHash(
            string username,
            string passwordSalt,
            string passwordHash,
            string role = "User")
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException(
                    "Username cannot be empty.",
                    nameof(username));

            if (!IsValidPasswordMaterial(
                    passwordSalt,
                    passwordHash))
            {
                throw new ArgumentException(
                    "Invalid password material.",
                    nameof(passwordHash));
            }

            role = NormalizeRole(role);

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                // Don't allow duplicate usernames.
                if (_data.Users.Any(u =>
                    string.Equals(
                        u.Username,
                        username,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                User user = new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Username = username.Trim(),
                    PasswordSalt = passwordSalt,
                    PasswordHash = passwordHash,
                    Role = role
                };

                _data.Users.Add(user);

                SaveInternal();

                return CreatePublicUser(user);
            }
        }

        // ============================================================
        // USERS - GET
        // ============================================================

        /// <summary>
        /// Gets a user by username.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static User? GetUser(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                User? user =
                    _data.Users.FirstOrDefault(u =>
                        string.Equals(
                            u.Username,
                            username,
                            StringComparison.OrdinalIgnoreCase));

                return user == null
                    ? null
                    : CloneUser(user);
            }
        }

        /// <summary>
        /// Gets a user by ID.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static User? GetUserById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                User? user =
                    _data.Users.FirstOrDefault(
                        u => u.Id == id);

                return user == null
                    ? null
                    : CloneUser(user);
            }
        }

        // ============================================================
        // PASSWORD INFORMATION
        // ============================================================

        /// <summary>
        /// Gets the stored password salt for login.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static string? GetPasswordSalt(
            string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                User? user =
                    _data.Users.FirstOrDefault(u =>
                        string.Equals(
                            u.Username,
                            username,
                            StringComparison.OrdinalIgnoreCase));

                return user?.PasswordSalt;
            }
        }

        /// <summary>
        /// Gets the stored password verifier for login.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static string? GetPasswordHash(
            string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                User? user =
                    _data.Users.FirstOrDefault(u =>
                        string.Equals(
                            u.Username,
                            username,
                            StringComparison.OrdinalIgnoreCase));

                return user?.PasswordHash;
            }
        }

        // ============================================================
        // PASSWORD RESET
        // ============================================================

        /// <summary>
        /// Resets a user's password using a plaintext password.
        ///
        /// The password is immediately converted into a salted PBKDF2
        /// verifier and is never written to config.json.
        ///
        /// For browser-based password changes, prefer the overload
        /// accepting passwordSalt and passwordHash.
        /// </summary>
        public static bool ResetPassword(
            string id,
            string newPassword)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (string.IsNullOrWhiteSpace(newPassword))
                return false;

            byte[] salt =
                RandomNumberGenerator.GetBytes(
                    PasswordSaltSize);

            byte[] hash =
                Rfc2898DeriveBytes.Pbkdf2(
                    newPassword,
                    salt,
                    PasswordIterations,
                    HashAlgorithmName.SHA256,
                    PasswordHashSize);

            string passwordSalt =
                Convert.ToBase64String(salt);

            string passwordHash =
                Convert.ToBase64String(hash);

            return ResetPassword(
                id,
                passwordSalt,
                passwordHash);
        }

        /// <summary>
        /// Resets a user's password using browser-generated
        /// password material.
        ///
        /// No plaintext password is accepted.
        /// </summary>
        public static bool ResetPassword(
            string id,
            string passwordSalt,
            string passwordHash)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (!IsValidPasswordMaterial(
                    passwordSalt,
                    passwordHash))
            {
                return false;
            }

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                User? user = _data.Users
                    .FirstOrDefault(u => u.Id == id);

                if (user == null)
                    return false;

                user.PasswordSalt = passwordSalt;
                user.PasswordHash = passwordHash;

                SaveInternal();

                return true;
            }
        }

        // ============================================================
        // PASSWORD CHANGE
        // ============================================================

        /// <summary>
        /// Browser-compatible password change.
        /// </summary>
        public static bool ChangePassword(
            string id,
            string passwordSalt,
            string passwordHash)
        {
            return ResetPassword(
                id,
                passwordSalt,
                passwordHash);
        }

        // ============================================================
        // PASSWORD MATERIAL VALIDATION
        // ============================================================

        /// <summary>
        /// Validates browser-generated password material.
        ///
        /// Salt: 32 bytes
        /// Verifier: 32 bytes
        /// Both are Base64 encoded.
        /// </summary>
        public static bool IsValidPasswordMaterial(
            string passwordSalt,
            string passwordHash)
        {
            if (string.IsNullOrWhiteSpace(passwordSalt) ||
                string.IsNullOrWhiteSpace(passwordHash))
            {
                return false;
            }

            try
            {
                byte[] salt =
                    Convert.FromBase64String(
                        passwordSalt);

                byte[] hash =
                    Convert.FromBase64String(
                        passwordHash);

                return salt.Length == PasswordSaltSize &&
                       hash.Length == PasswordHashSize;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ============================================================
        // USERS - ROLE
        // ============================================================

        /// <summary>
        /// Changes a user's role.
        /// </summary>
        public static bool SetUserRole(
            string id,
            string role)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (string.IsNullOrWhiteSpace(role))
                return false;

            role = NormalizeRole(role);

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                User? user = _data.Users
                    .FirstOrDefault(u => u.Id == id);

                if (user == null)
                    return false;

                user.Role = role;

                SaveInternal();

                return true;
            }
        }

        // ============================================================
        // USERS - REMOVE
        // ============================================================

        public static bool RemoveUser(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                User? user = _data.Users
                    .FirstOrDefault(u => u.Id == id);

                if (user == null)
                    return false;

                _data.Users.Remove(user);

                SaveInternal();

                return true;
            }
        }

        // ============================================================
        // USERS - GET ALL
        // ============================================================

        /// <summary>
        /// Gets all users.
        ///
        /// PasswordSalt and PasswordHash are removed from the returned
        /// objects so API callers cannot accidentally expose them.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static List<User> GetUsers()
        {
            lock (_lock)
            {
                ReloadInternal();

                return _data.Users
                    .Select(CreatePublicUser)
                    .ToList();
            }
        }

        // ============================================================
        // DEVICE - ADD
        // ============================================================

        public static Device AddDevice(
     string name,
     string type,
     string ipAddress,
     Guid? audioSource = null,
     string lastSeen = "",
     string username = "",
     string password = "")
        {
            lock (_lock)
            {
                ReloadInternal();

                Device device = new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Type = type,
                    IpAddress = ipAddress,
                    AudioSorce =
                        audioSource?.ToString() ?? "",
                    LastSeen = lastSeen,
                    Username = username,
                    Password = password
                };

                _data.Devices.Add(device);

                SaveInternal();

                TallyDatabase.SyncDevices();

                return CloneDevice(device);
            }
        }

        public static Device AddDevice(
            Device device)
        {
            if (device == null)
                throw new ArgumentNullException(
                    nameof(device));

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                Device newDevice = CloneDevice(device);

                if (string.IsNullOrWhiteSpace(newDevice.Id))
                {
                    newDevice.Id =
                        Guid.NewGuid().ToString();
                }

                _data.Devices.Add(newDevice);

                SaveInternal();

                TallyDatabase.SyncDevices();

                return CloneDevice(newDevice);
            }
        }

        // ============================================================
        // DEVICE - REMOVE
        // ============================================================

        public static bool RemoveDevice(
            string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                Device? device = _data.Devices
                    .FirstOrDefault(d => d.Id == id);

                if (device == null)
                    return false;

                _data.Devices.Remove(device);

                SaveInternal();

                TallyDatabase.SyncDevices();

                return true;
            }
        }

        // ============================================================
        // DEVICE - EDIT
        // ============================================================

        public static bool EditDevice(
            Device updatedDevice)
        {
            if (updatedDevice == null)
                throw new ArgumentNullException(
                    nameof(updatedDevice));

            lock (_lock)
            {
                // Always reload before modifying.
                ReloadInternal();

                Device? device = _data.Devices
                    .FirstOrDefault(d =>
                        d.Id == updatedDevice.Id);

                if (device == null)
                    return false;

                device.Name =
                    updatedDevice.Name;

                device.Type =
                    updatedDevice.Type;

                device.IpAddress =
                    updatedDevice.IpAddress;

                device.AudioSorce =
                    updatedDevice.AudioSorce;

                device.AudioSorce =
     updatedDevice.AudioSorce;

                device.LastSeen =
                    updatedDevice.LastSeen;

                device.Username =
                    updatedDevice.Username;

                device.Password =
                    updatedDevice.Password;

                SaveInternal();

                TallyDatabase.SyncDevices();

                return true;
            }
        }

        // ============================================================
        // DEVICE - GET
        // ============================================================

        /// <summary>
        /// Gets a device by ID.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static Device? GetDevice(
            string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                Device? device =
                    _data.Devices
                        .FirstOrDefault(d => d.Id == id);

                return device == null
                    ? null
                    : CloneDevice(device);
            }
        }

        /// <summary>
        /// Gets a device by name.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static Device? GetDeviceByName(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            lock (_lock)
            {
                ReloadInternal();

                Device? device =
                    _data.Devices
                        .FirstOrDefault(d =>
                            string.Equals(
                                d.Name,
                                name,
                                StringComparison.OrdinalIgnoreCase));

                return device == null
                    ? null
                    : CloneDevice(device);
            }
        }

        /// <summary>
        /// Gets all devices.
        ///
        /// config.json is reloaded immediately before the lookup.
        /// </summary>
        public static List<Device> GetDevices()
        {
            lock (_lock)
            {
                ReloadInternal();

                return _data.Devices
                    .Select(CloneDevice)
                    .ToList();
            }
        }

        // ============================================================

        // CONFIG FILE INFORMATION
        // ============================================================

        public static string FilePath
        {
            get
            {
                return ConfigFilePath;
            }
        }

        // ============================================================
        // CLONING / SNAPSHOTS
        // ============================================================

        /// <summary>
        /// Creates a safe copy of a user object.
        /// </summary>
        private static User CloneUser(
            User user)
        {
            return new User
            {
                Id = user.Id,
                Username = user.Username,
                PasswordSalt = user.PasswordSalt,
                PasswordHash = user.PasswordHash,
                Role = user.Role
            };
        }

        /// <summary>
        /// Creates a sanitized user object.
        /// </summary>
        private static User CreatePublicUser(
            User user)
        {
            return new User
            {
                Id = user.Id,
                Username = user.Username,
                PasswordSalt = "",
                PasswordHash = "",
                Role = user.Role
            };
        }

        /// <summary>
        /// Creates a safe copy of a device.
        /// </summary>
        private static Device CloneDevice(
            Device device)
        {
            return new Device
            {
                Id = device.Id,
                Name = device.Name,
                Type = device.Type,
                IpAddress = device.IpAddress,
                AudioSorce = device.AudioSorce,
                LastSeen = device.LastSeen,
                Username = device.Username,
                Password = device.Password
            };
        }

        /// <summary>
        /// Creates a safe copy of application settings.
        /// </summary>
        private static AppSettings CloneSettings(
            AppSettings settings)
        {
            return new AppSettings
            {
                AutoStart = settings.AutoStart,
                RefreshIntervalSeconds =
                    settings.RefreshIntervalSeconds,
                DebugMode = settings.DebugMode
            };
        }

        // ============================================================
        // USERS - ROLE NORMALIZATION
        // ============================================================

        /// <summary>
        /// Keeps roles restricted to roles supported by Iceburg.
        /// </summary>
        private static string NormalizeRole(
            string role)
        {
            if (string.Equals(
                role,
                "Admin",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Admin";
            }

            return "User";
        }
    }

    // ================================================================

    // APP CONFIGURATION
    // ================================================================

    public class AppConfig
    {
        public string ApplicationName { get; set; }
            = "Iceburg";

   

        public AppSettings Settings { get; set; }
            = new();

        public List<User> Users { get; set; }
            = new();

        public List<Device> Devices { get; set; }
            = new();
    }

    // ================================================================
    // APPLICATION SETTINGS
    // ================================================================

    public class AppSettings
    {
        public bool AutoStart { get; set; } = true;

        public int RefreshIntervalSeconds { get; set; } = 30;

        public bool DebugMode { get; set; } = false;
    }

    // ================================================================
    // USER
    // ================================================================

    public class User
    {
        public string Id { get; set; }
            = Guid.NewGuid().ToString();

        public string Username { get; set; }
            = "";

        /// <summary>
        /// Base64 encoded 32-byte random PBKDF2 salt.
        /// </summary>
        public string PasswordSalt { get; set; }
            = "";

        /// <summary>
        /// Base64 encoded 32-byte PBKDF2 password verifier.
        /// </summary>
        public string PasswordHash { get; set; }
            = "";

        public string Role { get; set; }
            = "User";
    }

    // ================================================================
    // DEVICE
    // ================================================================

    public class Device
    {
        public string Id { get; set; }
            = Guid.NewGuid().ToString();

        public string Name { get; set; }
            = "";

        public string Type { get; set; }
            = "";

        public string IpAddress { get; set; }
            = "";

        public string AudioSorce { get; set; }
            = "";

        /// <summary>
        /// Last time the device was seen/reachable.
        /// Stored as a string.
        /// </summary>
        public string LastSeen { get; set; }
            = "";

        /// <summary>
        /// Username used to connect to the device.
        /// </summary>
        public string Username { get; set; }
            = "";

        /// <summary>
        /// Plaintext password used to connect to the device.
        ///
        /// This is intentionally NOT hashed because the plaintext
        /// value is required when connecting to the device.
        /// </summary>
        public string Password { get; set; }
            = "";
    }
}