using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DmaDesktop
{
    public static class PlatformHelper
    {
        // ── Windows P/Invoke for Memory ─────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedPhys;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // ── Main Metrics Retrieval ──────────────────────────────────────────────────

        public static string GetOSVersion()
        {
            return $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        }

        public static string GetCpuModel()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown Windows CPU";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/proc/cpuinfo"))
                    {
                        foreach (var line in File.ReadLines("/proc/cpuinfo"))
                        {
                            if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = line.Split(':');
                                if (parts.Length > 1) return parts[1].Trim();
                            }
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return RunCommand("sysctl", "-n machdep.cpu.brand_string");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading CPU Model: {ex.Message}");
            }
            return "Unknown Processor";
        }

        public static (ulong Total, ulong Available) GetRamInfo()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    return (memStatus.ullTotalPhys, memStatus.ullAvailPhys);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    if (File.Exists("/proc/meminfo"))
                    {
                        ulong total = 0;
                        ulong avail = 0;
                        foreach (var line in File.ReadLines("/proc/meminfo"))
                        {
                            if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                            {
                                total = ParseMeminfoLine(line);
                            }
                            else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
                            {
                                avail = ParseMeminfoLine(line);
                            }
                        }
                        return (total, avail);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading Linux memory: {ex.Message}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    var totalStr = RunCommand("sysctl", "-n hw.memsize");
                    if (ulong.TryParse(totalStr.Trim(), out ulong totalBytes))
                    {
                        // macOS free memory estimation via vm_stat
                        var vmStat = RunCommand("vm_stat", "");
                        ulong freePages = 0;
                        ulong inactivePages = 0;
                        ulong pageSize = 4096; // Standard page size fallback

                        var pageMatch = Regex.Match(vmStat, @"page size of (\d+) bytes");
                        if (pageMatch.Success) ulong.TryParse(pageMatch.Groups[1].Value, out pageSize);

                        var freeMatch = Regex.Match(vmStat, @"Pages free:\s+(\d+)");
                        if (freeMatch.Success) ulong.TryParse(freeMatch.Groups[1].Value, out freePages);

                        var inactiveMatch = Regex.Match(vmStat, @"Pages inactive:\s+(\d+)");
                        if (inactiveMatch.Success) ulong.TryParse(inactiveMatch.Groups[1].Value, out inactivePages);

                        ulong availBytes = (freePages + inactivePages) * pageSize;
                        return (totalBytes, availBytes);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading macOS memory: {ex.Message}");
                }
            }

            return (0, 0);
        }
        public static long GetUptime()
        {
            return Environment.TickCount64 / 1000;
        }

        public static string GetUpdateStatus()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var ps = "-Command \"try { $s = New-Object -ComObject Microsoft.Update.Session; $searcher = $s.CreateUpdateSearcher(); $res = $searcher.Search('IsInstalled=0 and Type=''Software'''); echo $res.Updates.Count } catch { echo 0 }\"";
                    var countStr = RunCommand("powershell", ps);
                    if (int.TryParse(countStr, out int count))
                    {
                        return count == 0 ? "Up to date" : $"{count} updates pending";
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/var/lib/update-notifier/updates-available"))
                    {
                        var text = File.ReadAllText("/var/lib/update-notifier/updates-available").Trim();
                        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0) return lines[0].Trim();
                    }
                    var aptCheck = RunCommand("/usr/lib/update-notifier/apt-check", "");
                    if (!string.IsNullOrEmpty(aptCheck) && aptCheck.Contains(";"))
                    {
                        var parts = aptCheck.Split(';');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int total) && int.TryParse(parts[1], out int security))
                        {
                            return total == 0 ? "Up to date" : $"{total} updates pending ({security} security)";
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var updates = RunCommand("softwareupdate", "-l");
                    if (updates.Contains("No new software available")) return "Up to date";
                    var count = updates.Split(new[] { '\n' }).Count(l => l.Contains("*"));
                    return count > 0 ? $"{count} updates pending" : "Up to date";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading update status: {ex.Message}");
            }
            return "Unknown";
        }

        // ── Helper Utilities ────────────────────────────────────────────────────────

        private static ulong ParseMeminfoLine(string line)
        {
            // Parses lines like: "MemTotal:       16345620 kB"
            var match = Regex.Match(line, @"\d+");
            if (match.Success && ulong.TryParse(match.Value, out ulong valueKb))
            {
                return valueKb * 1024; // Convert kB to Bytes
            }
            return 0;
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Command failed: {filename} {arguments}. Error: {ex.Message}");
            }
            return string.Empty;
        }
    }
}
