using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DmaDesktop
{
    public class Program
    {
        private static string serverUrl = "http://localhost:18800";
        private static string deviceId = "";
        private static string deviceName = "";
        private static bool isRunning = true;
        private static string updateStatus = "Checking...";
        private static readonly HttpClient httpClient = new HttpClient();

        // ── DTO Records ─────────────────────────────────────────────────────────────
        public record RegisterDeviceDto(string Id, string Name, string Model, string AndroidVersion);
        public record StorageVolume(string Name, long Used, long Total);
        public record SystemInfo(
            string IpAddress, 
            string ConnectionType, 
            int WifiSignal, 
            long TotalRam, 
            long AvailableRam, 
            string CpuArch, 
            long Uptime, 
            string Resolution, 
            bool KeepScreenAwake,
            string UpdateStatus
        );
        public record ReportStatusDto(
            int Battery, 
            long StorageUsed, 
            long StorageTotal, 
            List<string> AppList, 
            List<StorageVolume> StorageVolumes, 
            SystemInfo SystemInfo
        );
        public record CompleteCommandDto(bool Success, string Result);
        public record PendingCommand(string Id, string CommandType, string Payload);

        // ── Main Entry ──────────────────────────────────────────────────────────────
        public static async Task Main(string[] args)
        {
            Console.Title = "DMA Desktop Monitor Agent";
            Console.WriteLine("=== DMA Desktop Agent Starting ===");

            LoadConfig();
            EnsureDeviceId();

            // Run sync loops
            var registrationTask = Task.Run(SyncLoop);
            var updateTask = Task.Run(UpdateStatusLoop);

            Console.WriteLine("Press Ctrl+C to stop the agent.");
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) => {
                eventArgs.Cancel = true;
                isRunning = false;
                exitEvent.Set();
            };

            exitEvent.WaitOne();
            Console.WriteLine("Stopping Agent...");
            await Task.WhenAny(registrationTask, updateTask, Task.Delay(2000));
        }

        // ── Core Sync Loop ──────────────────────────────────────────────────────────
        private static async Task SyncLoop()
        {
            DateTime lastStatusTime = DateTime.MinValue;

            while (isRunning)
            {
                var now = DateTime.UtcNow;

                // 1. Heartbeat & Telemetry Update (Every 10 seconds)
                if ((now - lastStatusTime).TotalSeconds >= 10)
                {
                    await RegisterDevice();
                    await ReportStatus();
                    lastStatusTime = now;
                }

                // 2. Poll & Process Commands (Every 3 seconds)
                await PollCommands();

                try
                {
                    await Task.Delay(3000);
                }
                catch
                {
                    break;
                }
            }
        }

        private static async Task UpdateStatusLoop()
        {
            while (isRunning)
            {
                updateStatus = PlatformHelper.GetUpdateStatus();

                // Wait 1 hour between checks (check isRunning every 10s to exit quickly)
                for (int i = 0; i < 360 && isRunning; i++)
                {
                    try { await Task.Delay(10000); } catch { break; }
                }
            }
        }

        // ── HTTP API Communications ──────────────────────────────────────────────────
        private static async Task RegisterDevice()
        {
            try
            {
                var dto = new RegisterDeviceDto(
                    deviceId,
                    deviceName,
                    GetMachineModel(),
                    PlatformHelper.GetOSVersion()
                );
                
                var response = await httpClient.PostAsJsonAsync($"{serverUrl}/api/devices/register", dto);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Error] Server registration failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Connection Error] Failed to register: {ex.Message}");
            }
        }

        private static async Task ReportStatus()
        {
            try
            {
                // Fetch storage drives info
                var volumes = new List<StorageVolume>();
                long totalStorage = 0;
                long usedStorage = 0;

                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    try
                    {
                        var name = drive.Name;
                        var total = drive.TotalSize;
                        var used = total - drive.AvailableFreeSpace;
                        volumes.Add(new StorageVolume(name, used, total));

                        if (name.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) || name == "/")
                        {
                            totalStorage = total;
                            usedStorage = used;
                        }
                    }
                    catch { }
                }

                if (totalStorage == 0 && volumes.Count > 0)
                {
                    totalStorage = volumes[0].Total;
                    usedStorage = volumes[0].Used;
                }

                // Fetch process list as running apps
                var processNames = Process.GetProcesses()
                    .Select(p => p.ProcessName)
                    .Distinct()
                    .OrderBy(name => name)
                    .Take(250) // Limit to avoid massive payload size
                    .ToList();

                // System Specs details
                var (ramTotal, ramAvail) = PlatformHelper.GetRamInfo();
                var sysInfo = new SystemInfo(
                    GetLocalIpAddress(),
                    GetConnectionType(),
                    -1,
                    (long)ramTotal,
                    (long)ramAvail,
                    PlatformHelper.GetCpuModel(),
                    PlatformHelper.GetUptime(),
                    GetScreenResolution(),
                    false,
                    updateStatus
                );

                var dto = new ReportStatusDto(
                    GetBatteryPercentage(),
                    usedStorage,
                    totalStorage,
                    processNames,
                    volumes,
                    sysInfo
                );

                var response = await httpClient.PostAsJsonAsync($"{serverUrl}/api/devices/{deviceId}/status", dto);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Heartbeat] Telemetry sent successfully. RAM Used: {((ramTotal - ramAvail) / (1024 * 1024 * 1024.0)):F2} GB / {(ramTotal / (1024 * 1024 * 1024.0)):F2} GB");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to send telemetry status: {ex.Message}");
            }
        }

        private static async Task PollCommands()
        {
            try
            {
                var response = await httpClient.GetAsync($"{serverUrl}/api/devices/{deviceId}/commands/pending");
                if (response.IsSuccessStatusCode)
                {
                    var commands = await response.Content.ReadFromJsonAsync<List<PendingCommand>>();
                    if (commands != null && commands.Count > 0)
                    {
                        foreach (var cmd in commands)
                        {
                            Console.WriteLine($"[Command] Received: {cmd.CommandType} with payload: {cmd.Payload}");
                            var (success, result) = ExecuteCommand(cmd.CommandType, cmd.Payload);
                            await CompleteCommand(cmd.Id, success, result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to poll commands: {ex.Message}");
            }
        }

        private static async Task CompleteCommand(string cmdId, bool success, string result)
        {
            try
            {
                var dto = new CompleteCommandDto(success, result);
                await httpClient.PostAsJsonAsync($"{serverUrl}/api/devices/{deviceId}/commands/{cmdId}/complete", dto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to complete command: {ex.Message}");
            }
        }

        // ── Command Processor ───────────────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern void LockWorkStation();

        private static (bool Success, string Result) ExecuteCommand(string type, string payload)
        {
            try
            {
                switch (type)
                {
                    case "Ping":
                        return (true, "Pong");

                    case "Vibrate":
                        Console.Beep();
                        return (true, "Console beep triggered.");

                    case "LockDevice":
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            LockWorkStation();
                            return (true, "Screen Locked.");
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            RunCommand("xdg-screensaver", "lock");
                            return (true, "Screensaver lock triggered.");
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            RunCommand("pmset", "displaysleepnow");
                            return (true, "Display put to sleep.");
                        }
                        return (false, "Lock screen not supported on this OS.");

                    case "LaunchApp":
                        var psi = new ProcessStartInfo
                        {
                            FileName = payload,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        return (true, $"Application launched: {payload}");

                    case "SpeakText":
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var psArgs = $"-Command \"Add-Type -AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{payload.Replace("'", "''")}')\"";
                            RunCommand("powershell", psArgs);
                            return (true, "Text spoken via PowerShell.");
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            RunCommand("say", $"\"{payload.Replace("\"", "\\\"")}\"");
                            return (true, "Text spoken via say command.");
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            RunCommand("espeak", $"\"{payload.Replace("\"", "\\\"")}\"");
                            return (true, "Text spoken via espeak.");
                        }
                        return (false, "TTS not supported on this OS.");

                    case "SetVolume":
                        // Parse level e.g. "media:5"
                        var volStr = payload.Split(':').LastOrDefault() ?? payload;
                        if (int.TryParse(volStr, out int level))
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                // Simple nircmd fallback or PowerShell Core Audio tool could go here.
                                // We can write a quick PowerShell volume setter command:
                                var psVolume = $"-Command \"(New-Object -ComObject WScript.Shell).SendKeys([char]174 * 50); (New-Object -ComObject WScript.Shell).SendKeys([char]175 * {level * 2})\"";
                                RunCommand("powershell", psVolume);
                                return (true, $"Volume adjusted roughly to {level}.");
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                RunCommand("amixer", $"sset Master {level * 6}%");
                                return (true, $"Volume set to {level * 6}%.");
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                            {
                                RunCommand("osascript", $"-e \"set volume output volume {level * 6}\"");
                                return (true, $"Volume set to {level * 6}%.");
                            }
                        }
                        return (false, "Volume setting not supported/failed.");

                    case "SetBrightness":
                        if (int.TryParse(payload, out int bright))
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                RunCommand("powershell", $"-Command \"(Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods).WmiSetBrightness(1, {bright * 100 / 255})\"");
                                return (true, $"Brightness set to {bright}.");
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                // Write to sys backlight if file is writable or execute xbacklight
                                RunCommand("xbacklight", $"-set {bright * 100 / 255}");
                                return (true, $"Brightness set to {bright}.");
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                            {
                                RunCommand("brightness", $"{bright / 255.0}");
                                return (true, $"Brightness set to {bright}.");
                            }
                        }
                        return (false, "Brightness setting not supported/failed.");

                    default:
                        return (false, $"Command {type} not handled on desktop agent.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error executing command: {ex.Message}");
            }
        }

        // ── Device Specs & Telemetry Gatherers ─────────────────────────────────────

        private static string GetMachineModel()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var model = RunCommand("powershell", "-Command \"(Get-WmiObject -Class Win32_ComputerSystem).Model\"");
                return string.IsNullOrEmpty(model) ? "Windows Desktop" : model;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/sys/class/dmi/id/product_name"))
                {
                    return File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                }
                return "Linux Server";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var model = RunCommand("sysctl", "-n hw.model");
                return string.IsNullOrEmpty(model) ? "Macintosh" : model;
            }
            return "Desktop PC";
        }

        private static int GetBatteryPercentage()
        {
            int val;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Read from PowerShell Utility
                var pct = RunCommand("powershell", "-Command \"(Get-WmiObject -Class Win32_Battery).EstimatedChargeRemaining\"");
                if (int.TryParse(pct, out val)) return val;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/sys/class/power_supply/BAT0/capacity"))
                {
                    if (int.TryParse(File.ReadAllText("/sys/class/power_supply/BAT0/capacity").Trim(), out val)) return val;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var pmset = RunCommand("pmset", "-g batt");
                var match = Regex.Match(pmset, @"(\d+)%");
                if (match.Success && int.TryParse(match.Groups[1].Value, out val)) return val;
            }
            return 100; // Default to plugged-in PC
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up))
                {
                    foreach (var ip in iface.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        // Exclude loopback
                        if (!IPAddress.IsLoopback(ip.Address))
                        {
                            return ip.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return "-";
        }

        private static string GetConnectionType()
        {
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up))
                {
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "WiFi";
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet) return "Ethernet";
                }
            }
            catch { }
            return "Wired";
        }

        private static string GetScreenResolution()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var w = RunCommand("powershell", "-Command \"(Get-WmiObject -Class Win32_VideoController).VideoModeDescription\"");
                if (!string.IsNullOrEmpty(w)) return w.Split('x').Take(2).Aggregate((a, b) => $"{a.Trim()}x{b.Trim()}");
            }
            return "-";
        }

        private static string RunCommand(string filename, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    return proc.StandardOutput.ReadToEnd().Trim();
                }
            }
            catch { }
            return string.Empty;
        }

        // ── Local Configuration Config ──────────────────────────────────────────────
        private class Config
        {
            public string ServerUrl { get; set; } = "http://localhost:18800";
            public string DeviceId { get; set; } = "";
            public string DeviceName { get; set; } = "";
        }

        private static void LoadConfig()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            try
            {
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var cfg = JsonSerializer.Deserialize<Config>(json);
                    if (cfg != null)
                    {
                        serverUrl = cfg.ServerUrl;
                        deviceId = cfg.DeviceId;
                        deviceName = cfg.DeviceName;
                    }
                }
                else
                {
                    // Create default template
                    var cfg = new Config();
                    var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(configPath, json);
                    serverUrl = cfg.ServerUrl;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to load config: {ex.Message}");
            }
        }

        private static void EnsureDeviceId()
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                // Generate a persistent random hardware ID
                deviceId = Guid.NewGuid().ToString("N").Substring(0, 16);
            }
            if (string.IsNullOrEmpty(deviceName))
            {
                deviceName = Environment.MachineName;
            }

            // Save back to config
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            try
            {
                var cfg = new Config
                {
                    ServerUrl = serverUrl,
                    DeviceId = deviceId,
                    DeviceName = deviceName
                };
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch { }

            Console.WriteLine($"Device ID: {deviceId}");
            Console.WriteLine($"Device Name: {deviceName}");
            Console.WriteLine($"Server URL: {serverUrl}");
        }
    }
}
