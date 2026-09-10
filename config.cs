using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Iceburg.Database
{

    public static class Config
    {
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

        /// <summary>
        /// Gets the application configuration.
        /// </summary>
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

        /// <summary>
        /// Gets the list of devices.
        /// </summary>
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

        /// <summary>
        /// Gets the application settings.
        /// </summary>
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

        /// <summary>
        /// Loads the configuration file.
        ///
        /// If the file doesn't exist, a new configuration
        /// file is automatically created.
        /// </summary>
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

                    // Make sure these are never null
                    _data.Settings ??= new AppSettings();
                    _data.Devices ??= new List<Device>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error loading config: {ex.Message}"
                    );

                    // Start with a fresh configuration
                    _data = new AppConfig();

                    SaveInternal();
                }
            }
        }

        // ============================================================
        // SAVE
        // ============================================================

        /// <summary>
        /// Saves the current configuration to config.json.
        /// </summary>
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
        // DEVICE - ADD
        // ============================================================

        /// <summary>
        /// Adds a new device to the configuration.
        /// </summary>
        public static Device AddDevice(string name, string type, string ipAddress, Guid? audioSource = null)
        {
            lock (_lock)
            {
                Device device = new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Type = type,
                    IpAddress = ipAddress,
                    AudioSorce = audioSource.ToString()
                };

                _data.Devices.Add(device);

                SaveInternal();

                return device;
            }
        }

        /// <summary>
        /// Adds an existing Device object.
        /// </summary>
        public static Device AddDevice(Device device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(device.Id))
                {
                    device.Id = Guid.NewGuid().ToString();
                }

                _data.Devices.Add(device);

                SaveInternal();

                return device;
            }
        }

        // ============================================================
        // DEVICE - REMOVE
        // ============================================================

        /// <summary>
        /// Removes a device using its ID.
        ///
        /// Returns true if the device was removed.
        /// </summary>
        public static bool RemoveDevice(string id)
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

        /// <summary>
        /// Updates an existing device using a Device object.
        /// </summary>
        public static bool EditDevice(Device updatedDevice)
        {
            if (updatedDevice == null)
                throw new ArgumentNullException(nameof(updatedDevice));

            lock (_lock)
            {
                Device? device = _data.Devices
                    .FirstOrDefault(d => d.Id == updatedDevice.Id);

                if (device == null)
                    return false;

                device.Name = updatedDevice.Name;
                device.Type = updatedDevice.Type;
                device.IpAddress = updatedDevice.IpAddress;
                device.AudioSorce = updatedDevice.AudioSorce;

                SaveInternal();

                return true;
            }
        }

        // ============================================================
        // DEVICE - GET
        // ============================================================

        /// <summary>
        /// Gets a device by its ID.
        ///
        /// Returns null if it doesn't exist.
        /// </summary>
        public static Device? GetDevice(string id)
        {
            lock (_lock)
            {
                return _data.Devices
                    .FirstOrDefault(d => d.Id == id);
            }
        }

        /// <summary>
        /// Gets a device by its name.
        ///
        /// Returns null if it doesn't exist.
        /// </summary>
        public static Device? GetDeviceByName(string name)
        {
            lock (_lock)
            {
                return _data.Devices
                    .FirstOrDefault(d =>
                        string.Equals(
                            d.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase
                        ));
            }
        }

        /// <summary>
        /// Gets a copy of all devices.
        /// </summary>
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

        /// <summary>
        /// Gets the full path to config.json.
        /// </summary>
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
        /// <summary>
        /// Name of your application.
        /// </summary>
        public string ApplicationName { get; set; }
            = "Iceburg";

        /// <summary>
        /// Configuration version.
        /// </summary>
        public string Version { get; set; }
            = "2.0.1";

        /// <summary>
        /// General application settings.
        /// </summary>
        public AppSettings Settings { get; set; }
            = new();

        /// <summary>
        /// List of devices.
        /// </summary>
        public List<Device> Devices { get; set; }
            = new();
    }


    // ================================================================
    // APPLICATION SETTINGS
    // ================================================================

    public class AppSettings
    {
        /// <summary>
        /// Should the application automatically start?
        /// </summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>
        /// How often the application should refresh, in seconds.
        /// </summary>
        public int RefreshIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Optional debug mode.
        /// </summary>
        public bool DebugMode { get; set; } = false;
    }


    // ================================================================
    // DEVICE
    // ================================================================

    public class Device
    {
        /// <summary>
        /// Unique ID for the device.
        /// </summary>
        public string Id { get; set; }
            = Guid.NewGuid().ToString();

        /// <summary>
        /// Friendly device name.
        /// </summary>
        public string Name { get; set; }
            = "";

        /// <summary>
        /// Device type.
        /// Example: Computer, Sensor, Printer, Camera, etc.
        /// </summary>
        public string Type { get; set; }
            = "";

        /// <summary>
        /// IP address of the device.
        /// </summary>
        public string IpAddress { get; set; }
            = "";

        /// <summary>
        /// Network port.
        /// </summary>


        /// <summary>
        /// Whether the device is enabled.
        /// </summary>
        public string AudioSorce { get; set; }
    }
}