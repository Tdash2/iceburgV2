
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iceburg.Database
{
    public static class Config
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

        private static readonly object _lock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // ============================================================
        // CONFIGURATION DATA
        // ============================================================

        private static AppConfig _data = new();

        public static AppConfig Data
        {
            get
            {
                lock (_lock)
                {
                    return _data;
                }
            }
        }

        public static List<Device> Devices
        {
            get
            {
                lock (_lock)
                {
                    return _data.Devices;
                }
            }
        }

        public static List<User> Users
        {
            get
            {
                lock (_lock)
                {
                    return _data.Users;
                }
            }
        }

        public static AppSettings Settings
        {
            get
            {
                lock (_lock)
                {
                    return _data.Settings;
                }
            }
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        public static void Initialize()
        {
            lock (_lock)
            {
                try
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
                        SaveInternal();
                        return;
                    }

                    _data = JsonSerializer.Deserialize<AppConfig>(
                        json,
                        JsonOptions
                    ) ?? new AppConfig();

                    // Make sure these are never null.
                    _data.Settings ??= new AppSettings();
                    _data.Devices ??= new List<Device>();
                    _data.Users ??= new List<User>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error loading config: {ex.Message}"
                    );

                    _data = new AppConfig();

                    SaveInternal();
                }
            }
        }

        // ============================================================
        // SAVE
        // ============================================================

        public static void Save()
        {
            lock (_lock)
            {
                SaveInternal();
            }
        }

        private static void SaveInternal()
        {
            try
            {
                string json = JsonSerializer.Serialize(
                    _data,
                    JsonOptions
                );

                File.WriteAllText(
                    ConfigFilePath,
                    json
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Error saving config: {ex.Message}"
                );
            }
        }

        // ============================================================
        // USERS
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

        /// <summary>
        /// Gets a user by username.
        /// </summary>
        public static User? GetUser(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                return _data.Users.FirstOrDefault(u =>
                    string.Equals(
                        u.Username,
                        username,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Gets a user by ID.
        /// </summary>
        public static User? GetUserById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            lock (_lock)
            {
                return _data.Users.FirstOrDefault(
                    u => u.Id == id);
            }
        }

        // ============================================================
        // PASSWORD INFORMATION
        // ============================================================

        /// <summary>
        /// Gets the stored password salt for login.
        /// </summary>
        public static string? GetPasswordSalt(
            string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                User? user = _data.Users.FirstOrDefault(u =>
                    string.Equals(
                        u.Username,
                        username,
                        StringComparison.OrdinalIgnoreCase));

                return user?.PasswordSalt;
            }
        }

        /// <summary>
        /// Gets the stored password verifier for login.
        /// </summary>
        public static string? GetPasswordHash(
            string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_lock)
            {
                User? user = _data.Users.FirstOrDefault(u =>
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
                    Convert.FromBase64String(passwordSalt);

                byte[] hash =
                    Convert.FromBase64String(passwordHash);

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
        // USERS - GET
        // ============================================================

        /// <summary>
        /// Gets all users.
        ///
        /// PasswordSalt and PasswordHash are removed from the returned
        /// objects so API callers cannot accidentally expose them.
        /// </summary>
        public static List<User> GetUsers()
        {
            lock (_lock)
            {
                return _data.Users
                    .Select(CreatePublicUser)
                    .ToList();
            }
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

        // ============================================================
        // DEVICE - ADD
        // ============================================================

        public static Device AddDevice(
            string name,
            string type,
            string ipAddress,
            Guid? audioSource = null)
        {
            lock (_lock)
            {
                Device device = new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Type = type,
                    IpAddress = ipAddress,
                    AudioSorce =
                        audioSource?.ToString() ?? ""
                };

                _data.Devices.Add(device);

                SaveInternal();

                return device;
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
                if (string.IsNullOrWhiteSpace(device.Id))
                {
                    device.Id =
                        Guid.NewGuid().ToString();
                }

                _data.Devices.Add(device);

                SaveInternal();

                return device;
            }
        }

        // ============================================================
        // DEVICE - REMOVE
        // ============================================================

        public static bool RemoveDevice(
            string id)
        {
            lock (_lock)
            {
                Device? device = _data.Devices
                    .FirstOrDefault(d => d.Id == id);

                if (device == null)
                    return false;

                _data.Devices.Remove(device);

                SaveInternal();

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

                SaveInternal();

                return true;
            }
        }

        // ============================================================
        // DEVICE - GET
        // ============================================================

        public static Device? GetDevice(
            string id)
        {
            lock (_lock)
            {
                return _data.Devices
                    .FirstOrDefault(d => d.Id == id);
            }
        }

        public static Device? GetDeviceByName(
            string name)
        {
            lock (_lock)
            {
                return _data.Devices
                    .FirstOrDefault(d =>
                        string.Equals(
                            d.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase));
            }
        }

        public static List<Device> GetDevices()
        {
            lock (_lock)
            {
                return _data.Devices.ToList();
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
    }


    // ================================================================
    // APP CONFIGURATION
    // ================================================================

    public class AppConfig
    {
        public string ApplicationName { get; set; }
            = "Iceburg";

        public string Version { get; set; }
            = "2.0.1";

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
    }
}

