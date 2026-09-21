
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iceburg.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using static System.Net.Mime.MediaTypeNames;
using Iceburg.Router.BMD;

namespace Iceburg.Database
{

    using System.Text.Json;
    using System.Text.Json.Serialization;

    public static class TallyDatabase
    {


        private static readonly object _lock = new();

        private static readonly string FilePath =
            Path.Combine(
                AppContext.BaseDirectory,
                "gpio.json"
            );

        private static GpioConfig _config =
            new();

        private static FileSystemWatcher? _watcher;

        // Live Blackmagic router routes. These are intentionally kept
        // in memory because they are runtime state returned by the router.
        // Key: Iceburg router device ID. Value: router input (0-based) ->
        // router output (0-based).
        private static readonly Dictionary<string, Dictionary<int, int>>
            _routerRoutes =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, DateTime>
            _routerLastSuccessfulPoll =
                new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, bool>
            _routerConnected =
                new(StringComparer.OrdinalIgnoreCase);

        private static CancellationTokenSource? _routerPollingCts;
        private static Task? _routerPollingTask;

        private static readonly JsonSerializerOptions JsonOptions =
            new()
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };


        // ============================================================
        // INITIALIZATION
        // ============================================================

        public static void Initialize()
        {
            lock (_lock)
            {
                LoadInternal();

                StartWatcherInternal();

                CleanupRoutesInternal();

                SaveInternal();

                RecalculateAllOutputsInternal();

                StartRouterPollingInternal();
            }
        }
        private static bool IsRouterDeviceInternal(string deviceId)
        {
            return Database.Devices.Any(d =>
                string.Equals(
                    d.Id,
                    deviceId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    d.Type,
                    "1",
                    StringComparison.OrdinalIgnoreCase));
        }
        private static async Task PollRouterInternal(string routerId)
        {
            try
            {
                if (!IsRouterDeviceInternal(routerId))
                    return;

                var router = new BMD_Router();

                string response = await router.GetRoutes(routerId);

                if (string.IsNullOrWhiteSpace(response))
                    return;

                using JsonDocument document = JsonDocument.Parse(response);

                JsonElement root = document.RootElement;

                // Do not replace the cached routes if the router
                // returned an error.
                if (root.ValueKind != JsonValueKind.Object)
                    return;

                if (root.TryGetProperty("error", out _))
                {
                    lock (_lock)
                    {
                        _routerConnected[routerId] = false;
                    }

                    return;
                }

                if (!root.TryGetProperty("routes", out JsonElement routesElement))
                    return;

                if (routesElement.ValueKind != JsonValueKind.Object)
                    return;

                var newRoutes = new Dictionary<int, int>();

                foreach (JsonProperty property in routesElement.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, out int routerInput))
                        continue;

                    int routerOutput;

                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (!property.Value.TryGetInt32(out routerOutput))
                            continue;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (!int.TryParse(
                                property.Value.GetString(),
                                out routerOutput))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    newRoutes[routerInput] = routerOutput;
                }

                // A response without usable routes is not considered
                // a successful poll, so keep the previous cache.
                if (newRoutes.Count == 0)
                    return;

                lock (_lock)
                {
                    // Replace the cache ONLY after the entire response
                    // has been successfully parsed.
                    _routerRoutes[routerId] = newRoutes;

                    _routerLastSuccessfulPoll[routerId] = DateTime.UtcNow;

                    _routerConnected[routerId] = true;

                    // Recalculate using:
                    //
                    // 1. Normal Iceburg routes
                    // 2. Router output -> router input propagation
                    // 3. Recalculate normal outputs using router inputs
                    //
                    // This prevents router-input propagation from becoming
                    // part of the router-output calculation itself.
                    RecalculateAllOutputsInternal();
                }
            }
            catch
            {
                // Poll failure must NOT destroy the last known-good routes.
                //
                // The cached routes remain available and will be used by
                // ApplyRouterInputTalliesInternal() on the next calculation.

                lock (_lock)
                {
                    _routerConnected[routerId] = false;
                }
            }
        }

        private static void StartRouterPollingInternal()
        {
            if (_routerPollingTask != null &&
                !_routerPollingTask.IsCompleted)
            {
                return;
            }

            _routerPollingCts?.Dispose();
            _routerPollingCts = new CancellationTokenSource();

            var token = _routerPollingCts.Token;

            _routerPollingTask = Task.Run(
                async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            List<string> routerIds;

                            lock (_lock)
                            {
                                routerIds = Database.Devices
                                    .Where(d =>
                                        !string.IsNullOrWhiteSpace(d.Id) &&
                                        string.Equals(
                                            d.Type,
                                            "1",
                                            StringComparison.OrdinalIgnoreCase))
                                    .Select(d => d.Id)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                            }

                            foreach (var routerId in routerIds)
                            {
                                if (token.IsCancellationRequested)
                                    break;

                                PollRouterInternal(routerId);
                            }
                        }
                        catch
                        {
                            // Keep polling even if one poll fails.
                        }

                        try
                        {
                            await Task.Delay(500, token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                },
                token);
        }
        private static void ApplyRouterInputTalliesInternal()
        {
            foreach (var router in _config.Devices)
            {
                if (!IsRouterDeviceInternal(router.DeviceId))
                    continue;

                // Start with all router inputs OFF.
                foreach (var input in router.Inputs)
                {
                    input.Status = false;
                }

                if (!_routerRoutes.TryGetValue(
                        router.DeviceId,
                        out var routes))
                {
                    continue;
                }

                // Blackmagic router inputs are 0-based.
                // Iceburg router input ports are 1-based.
                //
                // Example:
                // Blackmagic input 0 -> Iceburg input port 1
                // Blackmagic input 1 -> Iceburg input port 2
                // etc.
                foreach (var route in routes)
                {
                    int routerInput = route.Key;
                    int routerOutput = route.Value;

                    int iceburgInputPort = routerInput + 1;  //Router output to tally input mapping
                 

                    var input = router.Inputs.FirstOrDefault(x =>
                        x.Port == iceburgInputPort);

                    if (input == null)
                        continue;

                    // Router output tally comes from normal Iceburg
                    // routes feeding that router output.
                    var output = router.Outputs.FirstOrDefault(x =>x.Port == routerOutput+1);

                    if (output == null)
                        continue;

                    if (output.Status)
                    {
                        input.Status = true;
                    }
                }
            }
        }
        // ============================================================
        // BLACKMAGIC ROUTER POLLING
        // ============================================================
        // ============================================================

        private static void StartWatcherInternal()
        {
            if (_watcher != null)
                return;

            var directory =
                Path.GetDirectoryName(FilePath);

            if (string.IsNullOrWhiteSpace(directory))
                directory =
                    AppContext.BaseDirectory;

            Directory.CreateDirectory(directory);

            _watcher =
                new FileSystemWatcher(
                    directory,
                    Path.GetFileName(FilePath)
                )
                {
                    NotifyFilter =
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.FileName
                };

            _watcher.Changed +=
                (_, _) => ReloadFromDisk();

            _watcher.Created +=
                (_, _) => ReloadFromDisk();

            _watcher.EnableRaisingEvents =
                true;
        }


        private static void ReloadFromDisk()
        {
            try
            {
                lock (_lock)
                {
                    if (!File.Exists(FilePath))
                        return;

                    LoadInternal();

                    CleanupRoutesInternal();

                    RecalculateAllOutputsInternal();
                }
            }
            catch
            {
                // Ignore transient file watcher errors.
                // The next file change will retry.
            }
        }


        // ============================================================
        // FILE STORAGE
        // ============================================================

        private static void LoadInternal()
        {
            if (!File.Exists(FilePath))
            {
                _config =
                    new GpioConfig();

                return;
            }

            var json =
                File.ReadAllText(FilePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                _config =
                    new GpioConfig();

                return;
            }

            _config =
                JsonSerializer.Deserialize<GpioConfig>(
                    json,
                    JsonOptions
                )
                ?? new GpioConfig();

            _config.Devices ??=
                new List<GpioDevice>();

            foreach (var device in _config.Devices)
            {
                device.Inputs ??=
                    new List<GpioInput>();

                device.Outputs ??=
                    new List<GpioOutput>();

                device.Routes ??=
                    new List<GpioRoute>();
            }
        }


        private static void SaveInternal()
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_config, JsonOptions);

            const int maxAttempts = 5;
            const int delayMs = 100;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.WriteAllText(FilePath, json);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(delayMs);
                }
            }

            // At this point the file is still locked/unavailable.
            // Don't let this bring down the application.
        }


        private static void SaveAndRecalculateInternal()
        {
            CleanupRoutesInternal();

            RecalculateAllOutputsInternal();

            SaveInternal();
        }


        // ============================================================
        // DEVICES
        // ============================================================

        // ============================================================
        // DEVICE SYNCHRONIZATION
        // ============================================================

        /// <summary>
        /// Synchronizes the GPIO database with the application's
        /// main device database.
        ///
        /// Devices with Type == "2" are GPIO devices.
        /// Devices with Type == "1" are Blackmagic router devices.
        ///
        /// IMPORTANT:
        /// Existing GPIO inputs, outputs, and cross-device routes are
        /// preserved when a device already exists.
        ///
        /// Devices are matched by DeviceId / Database.Device.Id.
        /// </summary>
        public static void SyncDevices()
        {
            lock (_lock)
            {
                // Get the latest devices from the main database.
                var sourceDevices =
                    Database.Devices
                        .Where(d =>
                            string.Equals(
                                d.Type,
                                "1",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                d.Type,
                                "2",
                                StringComparison.OrdinalIgnoreCase))
                        .ToList();

                var sourceIds =
                    new HashSet<string>(
                        sourceDevices
                            .Where(d =>
                                !string.IsNullOrWhiteSpace(d.Id))
                            .Select(d => d.Id),
                        StringComparer.OrdinalIgnoreCase);

                // --------------------------------------------------------
                // ADD OR UPDATE GPIO DEVICES
                // --------------------------------------------------------

                foreach (var sourceDevice in sourceDevices)
                {
                    if (string.IsNullOrWhiteSpace(sourceDevice.Id))
                        continue;

                    var gpioDevice =
                        FindDeviceInternal(sourceDevice.Id);

                    if (gpioDevice == null)
                    {
                        // New GPIO/router device.
                        //
                        // Start it with empty GPIO configuration.
                        // Inputs and outputs can then be configured from
                        // the tally page.
                        gpioDevice =
                            new GpioDevice
                            {
                                DeviceId =
                                    sourceDevice.Id,

                                Name =
                                    sourceDevice.Name ?? "",

                                Nickname =
                                    sourceDevice.Name ?? "",

                                IpAddress =
                                    sourceDevice.IpAddress ?? "",

                                Inputs =
                                    new List<GpioInput>(),

                                Outputs =
                                    new List<GpioOutput>(),

                                Routes =
                                    new List<GpioRoute>()
                            };

                        _config.Devices.Add(
                            gpioDevice);
                    }
                    else
                    {
                        // Existing device.
                        //
                        // IMPORTANT:
                        // Do NOT replace Inputs, Outputs, or Routes.
                        // Those contain the user's GPIO configuration.
                        gpioDevice.Name =
                            sourceDevice.Name ?? "";

                        gpioDevice.IpAddress =
                            sourceDevice.IpAddress ?? "";

                        // Keep existing nickname if one has been
                        // configured. If it is empty, use the device name.
                        if (string.IsNullOrWhiteSpace(
                                gpioDevice.Nickname))
                        {
                            gpioDevice.Nickname =
                                sourceDevice.Name ?? "";
                        }

                        gpioDevice.Inputs ??=
                            new List<GpioInput>();

                        gpioDevice.Outputs ??=
                            new List<GpioOutput>();

                        gpioDevice.Routes ??=
                            new List<GpioRoute>();
                    }
                }

                // --------------------------------------------------------
                // REMOVE DEVICES THAT ARE NO LONGER TYPE 1 OR TYPE 2
                // --------------------------------------------------------

                _config.Devices.RemoveAll(
                    gpioDevice =>
                        !sourceIds.Contains(
                            gpioDevice.DeviceId));

                // --------------------------------------------------------
                // CLEAN UP ROUTES
                // --------------------------------------------------------

                CleanupRoutesInternal();

                // --------------------------------------------------------
                // RECALCULATE OUTPUT STATES
                // --------------------------------------------------------

                RecalculateAllOutputsInternal();

                // --------------------------------------------------------
                // SAVE
                // --------------------------------------------------------

                SaveInternal();
            }
        }


        public static List<GpioDevice> GetDevices()
        {
            lock (_lock)
            {
                return _config.Devices
                    .Select(CloneDevice)
                    .ToList();
            }
        }


        public static GpioDevice? GetDevice(
            string deviceId
        )
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                return device == null
                    ? null
                    : CloneDevice(device);
            }
        }


        private static GpioDevice? FindDeviceInternal(
            string deviceId
        )
        {
            return _config.Devices.FirstOrDefault(
                x =>
                    string.Equals(
                        x.DeviceId,
                        deviceId,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
        }


        // ============================================================
        // INPUTS
        // ============================================================

        public static List<GpioInput> GetInputs(
            string deviceId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return new List<GpioInput>();

                return device.Inputs
                    .Select(CloneInput)
                    .ToList();
            }
        }


        public static GpioInput? AddInput(
            string deviceId,
            int port,
            string? name,
            string? nickname
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return null;

                var input =
                    new GpioInput
                    {
                        InputId =
                            Guid.NewGuid().ToString("N"),

                        Port = port,

                        Name =
                            name ?? "",

                        Nickname =
                            nickname ?? "",

                        Status = false
                    };

                device.Inputs.Add(input);

                SaveAndRecalculateInternal();

                return CloneInput(input);
            }
        }


        public static GpioInput? EditInput(
            string deviceId,
            string inputId,
            int port,
            string? name,
            string? nickname
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return null;

                var input =
                    device.Inputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.InputId,
                                inputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (input == null)
                    return null;

                input.Port = port;
                input.Name = name ?? "";
                input.Nickname = nickname ?? "";

                SaveAndRecalculateInternal();

                return CloneInput(input);
            }
        }


        public static bool RemoveInput(
            string deviceId,
            string inputId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return false;

                var input =
                    device.Inputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.InputId,
                                inputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (input == null)
                    return false;

                device.Inputs.Remove(input);

                /*
                 * Remove this input from every route,
                 * including routes on other devices.
                 */
                foreach (var destinationDevice in _config.Devices)
                {
                    foreach (var route in destinationDevice.Routes)
                    {
                        if (!string.Equals(
                            route.SourceDeviceId,
                            deviceId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        route.InputIds.RemoveAll(
                            x =>
                                string.Equals(
                                    x,
                                    inputId,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        );
                    }
                }

                CleanupRoutesInternal();

                RecalculateAllOutputsInternal();

                SaveInternal();

                return true;
            }
        }


        public static GpioInput? SetInputStatus(
            string deviceId,
            string inputId,
            bool status
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return null;

                var input =
                    device.Inputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.InputId,
                                inputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (input == null)
                    return null;

                input.Status = status;

                /*
                 * IMPORTANT:
                 *
                 * Do not only recalculate this device.
                 *
                 * This input may be routed to outputs on
                 * completely different GPIO devices.
                 */
                RecalculateAllOutputsInternal();

                SaveInternal();

                return CloneInput(input);
            }
        }


        // ============================================================
        // OUTPUTS
        // ============================================================

        public static List<GpioOutput> GetOutputs(
            string deviceId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return new List<GpioOutput>();

                return device.Outputs
                    .Select(CloneOutput)
                    .ToList();
            }
        }


        public static GpioOutput? AddOutput(
            string deviceId,
            int port,
            string? name,
            string? nickname
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return null;

                var output =
                    new GpioOutput
                    {
                        OutputId =
                            Guid.NewGuid().ToString("N"),

                        Port = port,

                        Name =
                            name ?? "",

                        Nickname =
                            nickname ?? "",

                        Status = false
                    };

                device.Outputs.Add(output);

                SaveAndRecalculateInternal();

                return CloneOutput(output);
            }
        }


        public static GpioOutput? EditOutput(
            string deviceId,
            string outputId,
            int port,
            string? name,
            string? nickname
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return null;

                var output =
                    device.Outputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (output == null)
                    return null;

                output.Port = port;
                output.Name = name ?? "";
                output.Nickname = nickname ?? "";

                SaveAndRecalculateInternal();

                return CloneOutput(output);
            }
        }


        public static bool RemoveOutput(
            string deviceId,
            string outputId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(deviceId);

                if (device == null)
                    return false;

                var output =
                    device.Outputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (output == null)
                    return false;

                device.Outputs.Remove(output);

                /*
                 * Remove routes terminating at this output.
                 */
                device.Routes.RemoveAll(
                    x =>
                        string.Equals(
                            x.OutputId,
                            outputId,
                            StringComparison.OrdinalIgnoreCase
                        )
                );

                SaveAndRecalculateInternal();

                return true;
            }
        }


        // ============================================================
        // ROUTES
        // ============================================================

        /*
         * A route is stored on the DESTINATION device.
         *
         * Example:
         *
         * Device A:
         *     Input ABC
         *
         * Device B:
         *     Output XYZ
         *
         * Device B.Routes:
         *
         * {
         *     SourceDeviceId = "Device A",
         *     OutputId = "XYZ",
         *     InputIds = ["ABC"]
         * }
         *
         * Therefore:
         *
         * Device A / Input ABC
         *          |
         *          v
         * Device B / Output XYZ
         */


        public static List<GpioRoute> GetRoutes(
            string destinationDeviceId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(
                        destinationDeviceId
                    );

                if (device == null)
                    return new List<GpioRoute>();

                return device.Routes
                    .Select(CloneRoute)
                    .ToList();
            }
        }


        public static List<GpioRouteInfo> GetAllRoutes()
        {
            lock (_lock)
            {
                var result =
                    new List<GpioRouteInfo>();

                foreach (var destinationDevice in _config.Devices)
                {
                    foreach (var route in destinationDevice.Routes)
                    {
                        var sourceDevice =
                            FindDeviceInternal(
                                route.SourceDeviceId
                            );

                        var output =
                            destinationDevice.Outputs.FirstOrDefault(
                                x =>
                                    string.Equals(
                                        x.OutputId,
                                        route.OutputId,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                            );

                        if (sourceDevice == null ||
                            output == null)
                        {
                            continue;
                        }

                        foreach (var inputId in route.InputIds)
                        {
                            var input =
                                sourceDevice.Inputs.FirstOrDefault(
                                    x =>
                                        string.Equals(
                                            x.InputId,
                                            inputId,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                );

                            if (input == null)
                                continue;

                            result.Add(
                                new GpioRouteInfo
                                {
                                    RouteId =
                                        BuildRouteId(
                                            destinationDevice.DeviceId,
                                            route
                                        ),

                                    SourceDeviceId =
                                        sourceDevice.DeviceId,

                                    SourceDeviceName =
                                        GetDeviceName(sourceDevice),

                                    SourceInputId =
                                        input.InputId,

                                    SourceInputName =
                                        input.Name,

                                    SourceInputNickname =
                                        input.Nickname,

                                    SourceInputPort =
                                        input.Port,

                                    SourceInputStatus =
                                        input.Status,

                                    DestinationDeviceId =
                                        destinationDevice.DeviceId,

                                    DestinationDeviceName =
                                        GetDeviceName(
                                            destinationDevice
                                        ),

                                    DestinationOutputId =
                                        output.OutputId,

                                    DestinationOutputName =
                                        output.Name,

                                    DestinationOutputNickname =
                                        output.Nickname,

                                    DestinationOutputPort =
                                        output.Port,

                                    DestinationOutputStatus =
                                        output.Status
                                }
                            );
                        }
                    }
                }

                return result;
            }
        }


        /*
         * Create or replace a route for one destination output.
         *
         * SourceDeviceId:
         *     Device containing the inputs.
         *
         * OutputId:
         *     Output on this destination device.
         *
         * InputIds:
         *     Inputs on SourceDeviceId.
         */
        public static GpioRoute? SetRoute(
            string destinationDeviceId,
            string outputId,
            string sourceDeviceId,
            List<string>? inputIds
        )
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(
                    destinationDeviceId))
                {
                    throw new ArgumentException(
                        "Destination device ID is required."
                    );
                }

                if (string.IsNullOrWhiteSpace(
                    outputId))
                {
                    throw new ArgumentException(
                        "Output ID is required."
                    );
                }

                if (string.IsNullOrWhiteSpace(
                    sourceDeviceId))
                {
                    throw new ArgumentException(
                        "Source device ID is required."
                    );
                }

                var destinationDevice =
                    FindDeviceInternal(
                        destinationDeviceId
                    );

                if (destinationDevice == null)
                    return null;

                var sourceDevice =
                    FindDeviceInternal(
                        sourceDeviceId
                    );

                if (sourceDevice == null)
                    throw new ArgumentException(
                        "Source GPIO device was not found."
                    );

                var output =
                    destinationDevice.Outputs.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (output == null)
                    return null;

                var validInputIds =
                    (inputIds ?? new List<string>())
                        .Where(
                            x => !string.IsNullOrWhiteSpace(x)
                        )
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase
                        )
                        .ToList();

                /*
                 * Make sure every input belongs to the
                 * selected source device.
                 */
                foreach (var inputId in validInputIds)
                {
                    var inputExists =
                        sourceDevice.Inputs.Any(
                            x =>
                                string.Equals(
                                    x.InputId,
                                    inputId,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        );

                    if (!inputExists)
                    {
                        throw new ArgumentException(
                            $"Input '{inputId}' does not exist on source device '{sourceDeviceId}'."
                        );
                    }
                }

                /*
                 * Find the existing route for this output
                 * from this source device.
                 */
                var route =
                    destinationDevice.Routes.FirstOrDefault(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            ) &&
                            string.Equals(
                                x.SourceDeviceId,
                                sourceDeviceId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                /*
                 * Empty input list means remove this particular
                 * source -> output relationship.
                 */
                if (validInputIds.Count == 0)
                {
                    if (route != null)
                    {
                        destinationDevice.Routes.Remove(
                            route
                        );

                        RecalculateAllOutputsInternal();

                        SaveInternal();
                    }

                    return null;
                }

                if (route == null)
                {
                    route =
                        new GpioRoute
                        {
                            SourceDeviceId =
                                sourceDeviceId,

                            OutputId =
                                outputId,

                            InputIds =
                                validInputIds
                        };

                    destinationDevice.Routes.Add(
                        route
                    );
                }
                else
                {
                    route.InputIds =
                        validInputIds;
                }

                RecalculateAllOutputsInternal();

                SaveInternal();

                return CloneRoute(route);
            }
        }


        /*
         * Remove ALL source routes going into one destination
         * output.
         */
        public static bool RemoveRoute(
            string destinationDeviceId,
            string outputId
        )
        {
            lock (_lock)
            {
                var device =
                    FindDeviceInternal(
                        destinationDeviceId
                    );

                if (device == null)
                    return false;

                var removed =
                    device.Routes.RemoveAll(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (removed == 0)
                    return false;

                RecalculateAllOutputsInternal();

                SaveInternal();

                return true;
            }
        }

        /*
 * Remove one specific source-device route from one destination output.
 *
 * Example:
 *
 * Destination Device B / Output 1
 *
 *     Device A Input 1  ---> Output 1
 *     Device C Input 1  ---> Output 1
 *
 * Removing Device A must NOT remove Device C.
 */
        public static bool RemoveSourceRoute(
            string destinationDeviceId,
            string outputId,
            string sourceDeviceId
        )
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(destinationDeviceId))
                    return false;

                if (string.IsNullOrWhiteSpace(outputId))
                    return false;

                if (string.IsNullOrWhiteSpace(sourceDeviceId))
                    return false;

                var destinationDevice =
                    FindDeviceInternal(destinationDeviceId);

                if (destinationDevice == null)
                    return false;

                var removed =
                    destinationDevice.Routes.RemoveAll(
                        route =>
                            string.Equals(
                                route.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            ) &&
                            string.Equals(
                                route.SourceDeviceId,
                                sourceDeviceId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    );

                if (removed == 0)
                    return false;

                /*
                 * Recalculate all outputs because removing a source
                 * may change an output on this or another device.
                 */
                RecalculateAllOutputsInternal();

                SaveInternal();

                return true;
            }
        }
        // ============================================================
        // ROUTE CLEANUP
        // ============================================================

        private static void CleanupRoutesInternal()
        {
            foreach (var destinationDevice in _config.Devices)
            {
                destinationDevice.Routes ??=
                    new List<GpioRoute>();

                destinationDevice.Routes.RemoveAll(
                    route =>
                    {
                        if (string.IsNullOrWhiteSpace(
                            route.SourceDeviceId))
                        {
                            return true;
                        }

                        var sourceDevice =
                            FindDeviceInternal(
                                route.SourceDeviceId
                            );

                        if (sourceDevice == null)
                            return true;

                        var outputExists =
                            destinationDevice.Outputs.Any(
                                x =>
                                    string.Equals(
                                        x.OutputId,
                                        route.OutputId,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                            );

                        if (!outputExists)
                            return true;

                        route.InputIds ??=
                            new List<string>();

                        route.InputIds =
                            route.InputIds
                                .Where(
                                    inputId =>
                                        sourceDevice.Inputs.Any(
                                            input =>
                                                string.Equals(
                                                    input.InputId,
                                                    inputId,
                                                    StringComparison.OrdinalIgnoreCase
                                                )
                                        )
                                )
                                .Distinct(
                                    StringComparer.OrdinalIgnoreCase
                                )
                                .ToList();

                        return route.InputIds.Count == 0;
                    }
                );
            }
        }


        // ============================================================
        // GLOBAL OUTPUT EVALUATION
        // ============================================================

        /*
         * Recalculate EVERY output on EVERY GPIO device.
         *
         * This is what allows:
         *
         * Device A Input 1
         *        |
         *        v
         * Device B Output 2
         *
         * to work.
         *
         * We intentionally calculate from source inputs rather
         * than copying state around. That keeps the route model
         * deterministic.
         */
        private static void RecalculateAllOutputsInternal()
        {
            // Pass 1: calculate every output from normal Iceburg routes.
            // Router inputs are NOT used here; they are derived from the
            // already-calculated router outputs below.
            foreach (var destinationDevice in _config.Devices)
            {
                foreach (var output in destinationDevice.Outputs)
                {
                    output.Status =
                        EvaluateOutputInternal(
                            destinationDevice,
                            output.OutputId
                        );
                }
            }

            // Pass 2: walk the Blackmagic router backwards. If router
            // input N is routed to router output X, input N gets the
            // current tally state of output X.
            ApplyRouterInputTalliesInternal();

            // Pass 3: router inputs are now valid source inputs, so any
            // other Iceburg devices routed from those router inputs need
            // their outputs recalculated as well. Router outputs remain
            // based on normal Iceburg routes, preventing a feedback loop.
            foreach (var destinationDevice in _config.Devices)
            {
                foreach (var output in destinationDevice.Outputs)
                {
                    output.Status =
                        EvaluateOutputInternal(
                            destinationDevice,
                            output.OutputId
                        );
                }
            }
        }


        /*
         * An output is ON when ANY input connected to it is ON.
         *
         * Example:
         *
         * Device A Input 1 = OFF
         * Device A Input 2 = ON
         *
         * Both route to Device B Output 1.
         *
         * Result:
         *
         * Device B Output 1 = ON
         */
        private static bool EvaluateOutputInternal(
            GpioDevice destinationDevice,
            string outputId
        )
        {
            var routes =
                destinationDevice.Routes
                    .Where(
                        x =>
                            string.Equals(
                                x.OutputId,
                                outputId,
                                StringComparison.OrdinalIgnoreCase
                            )
                    )
                    .ToList();

            foreach (var route in routes)
            {
                var sourceDevice =
                    FindDeviceInternal(
                        route.SourceDeviceId
                    );

                if (sourceDevice == null)
                    continue;

                foreach (var inputId in route.InputIds)
                {
                    var input =
                        sourceDevice.Inputs.FirstOrDefault(
                            x =>
                                string.Equals(
                                    x.InputId,
                                    inputId,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        );

                    if (input?.Status == true)
                        return true;
                }
            }

            return false;
        }


        // ============================================================
        // RESOLVED ROUTE ID
        // ============================================================

        /*
         * Routes are stored by destination device + source device
         * + output. This gives the UI a stable identifier without
         * requiring another database field.
         */
        private static string BuildRouteId(
            string destinationDeviceId,
            GpioRoute route
        )
        {
            return
                $"{destinationDeviceId}:{route.SourceDeviceId}:{route.OutputId}";
        }


        // ============================================================
        // HELPERS
        // ============================================================

        private static string GetDeviceName(
            GpioDevice device
        )
        {
            return
                !string.IsNullOrWhiteSpace(device.Name)
                    ? device.Name
                    : !string.IsNullOrWhiteSpace(device.Nickname)
                        ? device.Nickname
                        : device.DeviceId;
        }


        private static GpioDevice CloneDevice(
            GpioDevice device
        )
        {
            return new GpioDevice
            {
                DeviceId =
                    device.DeviceId,

                Name =
                    device.Name,

                Nickname =
                    device.Nickname,

                IpAddress =
                    device.IpAddress,

                Inputs =
                    device.Inputs
                        .Select(CloneInput)
                        .ToList(),

                Outputs =
                    device.Outputs
                        .Select(CloneOutput)
                        .ToList(),

                Routes =
                    device.Routes
                        .Select(CloneRoute)
                        .ToList()
            };
        }


        private static GpioInput CloneInput(
            GpioInput input
        )
        {
            return new GpioInput
            {
                InputId =
                    input.InputId,

                Port =
                    input.Port,

                Name =
                    input.Name,

                Nickname =
                    input.Nickname,

                Status =
                    input.Status
            };
        }


        private static GpioOutput CloneOutput(
            GpioOutput output
        )
        {
            return new GpioOutput
            {
                OutputId =
                    output.OutputId,

                Port =
                    output.Port,

                Name =
                    output.Name,

                Nickname =
                    output.Nickname,

                Status =
                    output.Status
            };
        }


        private static GpioRoute CloneRoute(
            GpioRoute route
        )
        {
            return new GpioRoute
            {
                SourceDeviceId =
                    route.SourceDeviceId,

                OutputId =
                    route.OutputId,

                InputIds =
                    route.InputIds
                        .ToList()
            };
        }
    }



    // ==================================================================
    // CONFIGURATION
    // ==================================================================

    public class GpioConfig
    {
        public List<GpioDevice> Devices { get; set; } =
            new();
    }


    // ==================================================================
    // GPIO DEVICE
    // ==================================================================

    public class GpioDevice
    {
        public string DeviceId { get; set; } =
            "";

        public string Name { get; set; } =
            "";

        public string Nickname { get; set; } =
            "";

        public string IpAddress { get; set; } =
            "";

        public List<GpioInput> Inputs { get; set; } =
            new();

        public List<GpioOutput> Outputs { get; set; } =
            new();

        public List<GpioRoute> Routes { get; set; } =
            new();
    }


    // ==================================================================
    // GPIO INPUT
    // ==================================================================

    public class GpioInput
    {
        public string InputId { get; set; } =
            "";

        public int Port { get; set; }

        public string Name { get; set; } =
            "";

        public string Nickname { get; set; } =
            "";

        public bool Status { get; set; }
    }


    // ==================================================================
    // GPIO OUTPUT
    // ==================================================================

    public class GpioOutput
    {
        public string OutputId { get; set; } =
            "";

        public int Port { get; set; }

        public string Name { get; set; } =
            "";

        public string Nickname { get; set; } =
            "";

        public bool Status { get; set; }
    }


    // ==================================================================
    // CROSS-DEVICE ROUTE
    // ==================================================================

    public class GpioRoute
    {
        /*
         * Device containing the source inputs.
         */
        public string SourceDeviceId { get; set; } =
            "";

        /*
         * Output on the destination device.
         */
        public string OutputId { get; set; } =
            "";

        /*
         * One or more inputs on SourceDeviceId.
         *
         * Multiple inputs are OR'd together.
         */
        public List<string> InputIds { get; set; } =
            new();
    }


    // ==================================================================
    // RESOLVED ROUTE FOR THE UI
    // ==================================================================

    public class GpioRouteInfo
    {
        public string RouteId { get; set; } =
            "";

        public string SourceDeviceId { get; set; } =
            "";

        public string SourceDeviceName { get; set; } =
            "";

        public string SourceInputId { get; set; } =
            "";

        public string SourceInputName { get; set; } =
            "";

        public string SourceInputNickname { get; set; } =
            "";

        public int SourceInputPort { get; set; }

        public bool SourceInputStatus { get; set; }

        public string DestinationDeviceId { get; set; } =
            "";

        public string DestinationDeviceName { get; set; } =
            "";

        public string DestinationOutputId { get; set; } =
            "";

        public string DestinationOutputName { get; set; } =
            "";

        public string DestinationOutputNickname { get; set; } =
            "";

        public int DestinationOutputPort { get; set; }

        public bool DestinationOutputStatus { get; set; }
    }
}

// ====================================================================
// ENDPOINTS
// ====================================================================

namespace Iceburg.Database
{
    public static class TallyEndpoints
    {
        public static void Map(
            WebApplication app)
        {
            var gpioApi =
                app.MapGroup(
                    "/api/gpio")
                   .RequireAuthorization();


            // ========================================================
            // DEVICES
            // ========================================================

            gpioApi.MapGet(
                "/devices",
                () =>
                    Results.Ok(
                        TallyDatabase.GetDevices()));


            gpioApi.MapGet(
                "/{deviceId}",
                (string deviceId) =>
                {
                    var d =
                        TallyDatabase.GetDevice(
                            deviceId);

                    return d == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO device not found or device Type is not \"2\"."
                            })
                        : Results.Ok(d);
                });


            // ========================================================
            // INPUTS
            // ========================================================

            gpioApi.MapGet(
                "/{deviceId}/inputs",
                (string deviceId) =>
                {
                    var d =
                        TallyDatabase.GetDevice(
                            deviceId);

                    return d == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO device not found or device Type is not \"2\"."
                            })
                        : Results.Ok(d.Inputs);
                });


            gpioApi.MapPost(
                "/{deviceId}/inputs",
                (
                    string deviceId,
                    GpioInputRequest r) =>
                {
                    if (r.Port < 1)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "Port must be greater than zero."
                            });
                    }

                    var x =
                        TallyDatabase.AddInput(
                            deviceId,
                            r.Port,
                            r.Name,
                            r.Nickname);

                    return x == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO device not found or device Type is not \"2\"."
                            })
                        : Results.Ok(x);
                });


            gpioApi.MapPut(
                "/{deviceId}/inputs/{inputId}",
                (
                    string deviceId,
                    string inputId,
                    GpioInputRequest r) =>
                {
                    if (r.Port < 1)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "Port must be greater than zero."
                            });
                    }

                    var x =
                        TallyDatabase.EditInput(
                            deviceId,
                            inputId,
                            r.Port,
                            r.Name,
                            r.Nickname);

                    return x == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO input or device not found."
                            })
                        : Results.Ok(x);
                });


            gpioApi.MapDelete(
                "/{deviceId}/inputs/{inputId}",
                (
                    string deviceId,
                    string inputId) =>
                {
                    return
                        TallyDatabase.RemoveInput(
                            deviceId,
                            inputId)
                        ? Results.Ok(
                            new
                            {
                                success = true
                            })
                        : Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO input or device not found."
                            });
                });


            gpioApi.MapPut(
                "/{deviceId}/inputs/{inputId}/status",
                (
                    string deviceId,
                    string inputId,
                    GpioStatusRequest r) =>
                {
                    var changed =
                        TallyDatabase.SetInputStatus(
                            deviceId,
                            inputId,
                            r.Status);

                    return changed == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO input or device not found."
                            })
                        : Results.Ok(changed);
                });


            // ========================================================
            // OUTPUTS
            // ========================================================

            gpioApi.MapGet(
                "/{deviceId}/outputs",
                (string deviceId) =>
                {
                    var d =
                        TallyDatabase.GetDevice(
                            deviceId);

                    return d == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO device not found or device Type is not \"2\"."
                            })
                        : Results.Ok(d.Outputs);
                });


            gpioApi.MapPost(
                "/{deviceId}/outputs",
                (
                    string deviceId,
                    GpioOutputRequest r) =>
                {
                    if (r.Port < 1)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "Port must be greater than zero."
                            });
                    }

                    var x =
                        TallyDatabase.AddOutput(
                            deviceId,
                            r.Port,
                            r.Name,
                            r.Nickname);

                    return x == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO device not found or device Type is not \"2\"."
                            })
                        : Results.Ok(x);
                });
            // Delete one specific source-device route from an output.
            //
            // Example:
            //
            // DELETE /api/gpio/3/routes/output123/source/6470251a-fbab-4e58-881f-b38b8938508f
            //
            // This removes:
            //
            //     source device -> destination output
            //
            // without affecting any other source devices using
            // the same destination output.
            gpioApi.MapDelete(
                "/{deviceId}/routes/{outputId}/source/{sourceDeviceId}",
                (
                    string deviceId,
                    string outputId,
                    string sourceDeviceId) =>
                {
                    return
                        TallyDatabase.RemoveSourceRoute(
                            deviceId,
                            outputId,
                            sourceDeviceId)
                        ? Results.Ok(
                            new
                            {
                                success = true
                            })
                        : Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO source route, output, or device not found."
                            });
                });

            gpioApi.MapPut(
                "/{deviceId}/outputs/{outputId}",
                (
                    string deviceId,
                    string outputId,
                    GpioOutputRequest r) =>
                {
                    if (r.Port < 1)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "Port must be greater than zero."
                            });
                    }

                    var x =
                        TallyDatabase.EditOutput(
                            deviceId,
                            outputId,
                            r.Port,
                            r.Name,
                            r.Nickname);

                    return x == null
                        ? Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO output or device not found."
                            })
                        : Results.Ok(x);
                });


            gpioApi.MapDelete(
                "/{deviceId}/outputs/{outputId}",
                (
                    string deviceId,
                    string outputId) =>
                {
                    return
                        TallyDatabase.RemoveOutput(
                            deviceId,
                            outputId)
                        ? Results.Ok(
                            new
                            {
                                success = true
                            })
                        : Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO output or device not found."
                            });
                });


            // ========================================================
            // ROUTES
            // ========================================================

            // Get routes for one destination device.
            gpioApi.MapGet(
                "/{deviceId}/routes",
                (string deviceId) =>
                {
                    var r =
                        TallyDatabase.GetRoutes(
                            deviceId);

                    return Results.Ok(r);
                });


            // Get every route in the GPIO system.
            gpioApi.MapGet(
                "/routes/all",
                () =>
                    Results.Ok(
                        TallyDatabase.GetAllRoutes()));


            // Create/update a route.
            //
            // Example:
            //
            // PUT /api/gpio/DEVICE-B/routes/OUTPUT-1
            //
            // {
            //     "sourceDeviceId": "DEVICE-A",
            //     "inputIds": ["INPUT-1"]
            // }
            //
            gpioApi.MapPut(
                "/{deviceId}/routes/{outputId}",
                (
                    string deviceId,
                    string outputId,
                    GpioRouteRequest r) =>
                {
                    if (r == null)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "Route request is required."
                            });
                    }

                    if (string.IsNullOrWhiteSpace(
                            r.SourceDeviceId))
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "SourceDeviceId is required."
                            });
                    }

                    if (r.InputIds == null ||
                        r.InputIds.Count == 0)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    "At least one input is required."
                            });
                    }

                    try
                    {
                        var x =
                            TallyDatabase.SetRoute(
                                deviceId,
                                outputId,
                                r.SourceDeviceId,
                                r.InputIds);

                        return x == null
                            ? Results.NotFound(
                                new
                                {
                                    error =
                                        "GPIO output, source device, or input not found."
                                })
                            : Results.Ok(x);
                    }
                    catch (ArgumentException ex)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error =
                                    ex.Message
                            });
                    }
                });


            // Delete all routes from an output.
            gpioApi.MapDelete(
                "/{deviceId}/routes/{outputId}",
                (
                    string deviceId,
                    string outputId) =>
                {
                    return
                        TallyDatabase.RemoveRoute(
                            deviceId,
                            outputId)
                        ? Results.Ok(
                            new
                            {
                                success = true
                            })
                        : Results.NotFound(
                            new
                            {
                                error =
                                    "GPIO route, output, or device not found."
                            });
                });


            // ========================================================
            // LEGACY TALLY ENDPOINTS
            // ========================================================

            app.MapGet(
                "/tally/gettallystatus.php",
                (string? id, string? ip) =>
                    TallyLegacy.GetTallyStatus(id, ip));


            app.MapGet(
            "/tally/getumd.php",
            (string? id) =>
                TallyLegacy.GetUmd(id));


            app.MapGet(
                "/tally/settallystatus.php",
                (
                    string? id,
                    HttpRequest request) =>
                    TallyLegacy.SetTallyStatus(
                        id,
                        request.Query));

        }
    }


    // =================================================================
    // LEGACY API
    // =================================================================



    public static class TallyLegacy
    {
        /*
         * Legacy API channel mapping
         *
         * ch1 -> first input
         * ch2 -> second input
         * ...
         * ch8 -> eighth input
         *
         * The same ordering is used by gettallystatus.php and getumd.php.
         */

        public static IResult GetTallyStatus(
       string? id,
       string? ip)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                Database.UpdateDevicePresence(id, ip);
            }

            var d = FindDevice(id);

            if (d == null)
            {
                return Results.Json(new
                {
                    inputs = EmptyBoolChannels(),
                    outputs = EmptyBoolChannels()
                });
            }

            var inputs = OrderedInputs(d);
            var outputs = OrderedOutputs(d);

            return Results.Json(new
            {
                inputs = Enumerable.Range(1, 8)
                    .ToDictionary(
                        ch => ch.ToString(),
                        ch => ch <= inputs.Count
                            ? inputs[ch - 1].Status
                            : false),

                outputs = Enumerable.Range(1, 8)
                    .ToDictionary(
                        ch => ch.ToString(),
                        ch => ch <= outputs.Count
                            ? outputs[ch - 1].Status
                            : false)
            });
        }


        public static IResult GetUmd(string? id)
        {
            var d = FindDevice(id);

            if (d == null)
            {
                return Results.Json(new
                {
                    inputs = EmptyStringChannels(),
                    outputs = EmptyStringChannels()
                });
            }

            var inputs = OrderedInputs(d);
            var outputs = OrderedOutputs(d);

            return Results.Json(new
            {
                inputs = Enumerable.Range(1, 8)
                    .ToDictionary(
                        ch => ch.ToString(),
                        ch => ch <= inputs.Count
                            ? inputs[ch - 1].Nickname ?? ""
                            : ""),

                outputs = Enumerable.Range(1, 8)
                    .ToDictionary(
                        ch => ch.ToString(),
                        ch => ch <= outputs.Count
                            ? outputs[ch - 1].Nickname ?? ""
                            : "")
            });
        }


        public static IResult SetTallyStatus(
            string? id,
            IQueryCollection query)
        {
            var d = FindDevice(id);

            if (d == null)
            {
                return Results.NotFound(new
                {
                    error = "GPIO device not found."
                });
            }

            var inputs = OrderedInputs(d);

            /*
             * Look for:
             *
             *   ch1=1
             *   ch2=0
             *   ch3=1
             *
             * Only channels included in the request are changed.
             */

            for (int channel = 1; channel <= 8; channel++)
            {
                var key = $"ch{channel}";

                if (!query.TryGetValue(key, out var value))
                    continue;

                if (channel > inputs.Count)
                    continue;

                if (!TryParseStatus(value.ToString(), out var status))
                    continue;

                var input = inputs[channel - 1];

                if (string.IsNullOrWhiteSpace(input.InputId))
                    continue;

                TallyDatabase.SetInputStatus(
                    d.DeviceId,
                    input.InputId,
                    status);
            }

            return Results.Ok(new
            {
                success = true
            });
        }


        private static GpioDevice? FindDevice(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return TallyDatabase
                .GetDevices()
                .FirstOrDefault(d =>
                    string.Equals(
                        d.DeviceId,
                        id,
                        StringComparison.OrdinalIgnoreCase));
        }


        /*
         * Sort by Port so the legacy API has a deterministic
         * channel order.
         *
         * For example:
         *
         * Port 21 -> channel 1
         * Port 22 -> channel 2
         * ...
         */
        private static List<GpioInput> OrderedInputs(
            GpioDevice device)
        {
            return device.Inputs
                .OrderBy(x => x.Port)
                .Take(8)
                .ToList();
        }


        private static List<GpioOutput> OrderedOutputs(
            GpioDevice device)
        {
            return device.Outputs
                .OrderBy(x => x.Port)
                .Take(8)
                .ToList();
        }


        private static bool TryParseStatus(
            string value,
            out bool status)
        {
            value = value.Trim();

            if (value == "1" ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                status = true;
                return true;
            }

            if (value == "0" ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                status = false;
                return true;
            }

            status = false;
            return false;
        }

      
        private static Dictionary<string, bool> EmptyBoolChannels()
        {
            return Enumerable.Range(1, 8)
                .ToDictionary(
                    x => x.ToString(),
                    _ => false);
        }


        private static Dictionary<string, string> EmptyStringChannels()
        {
            return Enumerable.Range(1, 8)
                .ToDictionary(
            x => x.ToString(),
                    _ => "");
        }
    }



    // =================================================================
    // REQUEST MODELS
    // =================================================================

    public record GpioInputRequest(
        int Port,
        string Name,
        string Nickname);


    public record GpioOutputRequest(
        int Port,
        string Name,
        string Nickname);


    public record GpioStatusRequest(
        bool Status);


    /// <summary>
    /// Cross-device routing request.
    ///
    /// SourceDeviceId:
    ///     Device containing the source input(s).
    ///
    /// InputIds:
    ///     One or more inputs belonging to SourceDeviceId.
    ///
    /// The destination device and output are supplied
    /// in the URL.
    /// </summary>
    public record GpioRouteRequest(
        string SourceDeviceId,
        List<string> InputIds);

}

