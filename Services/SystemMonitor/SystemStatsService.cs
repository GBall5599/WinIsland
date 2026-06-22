using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace WinIsland.Services.SystemMonitor
{
    public struct SystemStats
    {
        public bool IsValid;
        public float CpuUsage;      // CPU 使用率 0-100
        public float RamUsage;      // 内存使用率 0-100
        public float? CpuTemperatureC;
        public float? GpuTemperatureC;
        public long NetUploadBps;   // 上行速度 Byte/s
        public long NetDownloadBps; // 下行速度 Byte/s

        public string GetFormattedCpu() => $"{Math.Round(CpuUsage)}%";
        public string GetFormattedRam() => $"{Math.Round(RamUsage)}%";
        public string GetFormattedCpuTemp() => FormatTemperature(CpuTemperatureC);
        public string GetFormattedGpuTemp() => FormatTemperature(GpuTemperatureC);
        public string GetFormattedUpload() => FormatSpeed(NetUploadBps);
        public string GetFormattedDownload() => FormatSpeed(NetDownloadBps);

        private static string FormatTemperature(float? celsius)
        {
            if (celsius == null || celsius <= 0 || celsius > 130) return "N/A";
            return $"{Math.Round(celsius.Value)}C";
        }
#if false
            return $"{Math.Round(celsius.Value)}°";
        }

#endif
        private static string FormatSpeed(long bps)
        {
            if (bps < 1024) return $"{bps} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024} KB/s";
            return $"{bps / 1024 / 1024} MB/s";
        }
    }

    /// <summary>
    /// 系统资源监控服务
    /// 负责获取 CPU、内存 loading 和网络流量
    /// </summary>
    public class SystemStatsService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        public event EventHandler<SystemStats>? OnStatsUpdated;

        private PerformanceCounter? _cpuCounter;
        // 内存可以使用 PerformanceCounter 或 Microsoft.VisualBasic.Devices.ComputerInfo
        // 这里为了兼容性使用 GlobalMemoryStatusEx 的封装或者计算 Available MBytes

        // 网络接口统计
        private NetworkInterface[] _interfaces = Array.Empty<NetworkInterface>();
        private readonly Dictionary<string, NetworkSample> _networkSamples = new();
        private readonly object _hardwareMonitorLock = new();
        private Computer? _hardwareMonitor;
        private bool _hardwareMonitorUnavailable;
        private DateTime _lastHardwareMonitorAttempt = DateTime.MinValue;
        private DateTime _lastTemperatureReadAt = DateTime.MinValue;
        private (float? cpu, float? gpu) _lastTemperatures;

        public bool IsRunning { get; private set; }

        public SystemStatsService()
        {
            InitializeCounters();
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        // P/Invoke for accurate Physical Memory usage matching Task Manager
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private void InitializeCounters()
        {
            try
            {
                // 获取总 CPU 使用率
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                
                // 内存改为使用 GlobalMemoryStatusEx，不再依赖 PerformanceCounter
                // _memCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");

                _interfaces = NetworkInterface.GetAllNetworkInterfaces();
                
                // 预热 Counter
                _cpuCounter.NextValue();
                PrimeNetworkStats();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemStats] Init Failed: {ex.Message}");
            }
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            _timer.Start();
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_cpuCounter == null) return;

            try
            {
                var stats = new SystemStats
                {
                    IsValid = true,
                    CpuUsage = _cpuCounter.NextValue()
                };

                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                stats.RamUsage = GlobalMemoryStatusEx(ref memStatus) ? memStatus.dwMemoryLoad : 0;

                var (cpuTemp, gpuTemp) = ReadCachedTemperatures();
                stats.CpuTemperatureC = cpuTemp;
                stats.GpuTemperatureC = gpuTemp;

                var (up, down) = UpdateNetworkStats();
                stats.NetUploadBps = up;
                stats.NetDownloadBps = down;

                OnStatsUpdated?.Invoke(this, stats);
            }
            catch { }
        }

#if false
        private void Timer_Tick_EncodingDamaged(object? sender, EventArgs e)
        {
            if (_cpuCounter == null) return;

            try
            {
                var stats = new SystemStats();
                stats.IsValid = true;
                stats.CpuUsage = _cpuCounter.NextValue();
                
                // 使用 Kernel32 获取真实的物理内存负载
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    stats.RamUsage = memStatus.dwMemoryLoad; // 0-100
                }
                else
                {
                    stats.RamUsage = 0;
                }

                var (cpuTemp, gpuTemp) = ReadCachedTemperatures();
                stats.CpuTemperatureC = cpuTemp;
                stats.GpuTemperatureC = gpuTemp;

                // 计算网速
                var (up, down) = UpdateNetworkStats();
                stats.NetUploadBps = up;
                stats.NetDownloadBps = down;

                OnStatsUpdated?.Invoke(this, stats);
            }
            catch { }
        }

#endif
        private void PrimeNetworkStats()
        {
            try
            {
                _networkSamples.Clear();
                foreach (var ni in GetActiveNetworkInterfaces())
                {
                    var stats = ni.GetIPv4Statistics();
                    _networkSamples[ni.Id] = new NetworkSample(stats.BytesSent, stats.BytesReceived, DateTime.Now);
                }
            }
            catch { }
        }

        private static IEnumerable<NetworkInterface> GetActiveNetworkInterfaces()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    ni.Supports(NetworkInterfaceComponent.IPv4));
        }

        private (long uploadSpeed, long downloadSpeed) UpdateNetworkStats()
        {
            double totalUpload = 0;
            double totalDownload = 0;

            try
            {
                var now = DateTime.Now;
                var activeIds = new HashSet<string>();

                foreach (var ni in GetActiveNetworkInterfaces())
                {
                    activeIds.Add(ni.Id);
                    var ipStats = ni.GetIPv4Statistics();
                    var sent = ipStats.BytesSent;
                    var received = ipStats.BytesReceived;

                    if (_networkSamples.TryGetValue(ni.Id, out var previous))
                    {
                        var seconds = Math.Max(0.25, (now - previous.Timestamp).TotalSeconds);
                        var upload = (sent - previous.BytesSent) / seconds;
                        var download = (received - previous.BytesReceived) / seconds;

                        if (upload > 0) totalUpload += upload;
                        if (download > 0) totalDownload += download;
                    }

                    _networkSamples[ni.Id] = new NetworkSample(sent, received, now);
                }

                foreach (var id in _networkSamples.Keys.Where(id => !activeIds.Contains(id)).ToList())
                {
                    _networkSamples.Remove(id);
                }

                return ((long)totalUpload, (long)totalDownload);
            }
            catch
            {
                return (0, 0);
            }
        }

        private (float? cpu, float? gpu) ReadCachedTemperatures()
        {
            var now = DateTime.Now;
            if (now - _lastTemperatureReadAt < TimeSpan.FromSeconds(2))
            {
                return _lastTemperatures;
            }

            var temperatures = ReadTemperatures();
            _lastTemperatures = temperatures;
            _lastTemperatureReadAt = now;
            return temperatures;
        }

        private (float? cpu, float? gpu) ReadTemperatures()
        {
            var (cpu, gpu) = TryReadLibreHardwareTemperatures();

            if (cpu == null || gpu == null)
            {
                TryReadHardwareMonitorTemperatures("root\\LibreHardwareMonitor", ref cpu, ref gpu);
                TryReadHardwareMonitorTemperatures("root\\OpenHardwareMonitor", ref cpu, ref gpu);
            }

            cpu ??= TryReadAcpiTemperature();

            return (cpu, gpu);
        }

        private (float? cpu, float? gpu) TryReadLibreHardwareTemperatures()
        {
            var computer = EnsureHardwareMonitor();
            if (computer == null) return (null, null);

            lock (_hardwareMonitorLock)
            {
                float? cpu = null;
                float? gpu = null;

                foreach (var hardware in computer.Hardware)
                {
                    ReadHardwareTemperatures(hardware, ref cpu, ref gpu);
                }

                return (cpu, gpu);
            }
        }

        private Computer? EnsureHardwareMonitor()
        {
            lock (_hardwareMonitorLock)
            {
                if (_hardwareMonitor != null) return _hardwareMonitor;

                if (_hardwareMonitorUnavailable &&
                    DateTime.Now - _lastHardwareMonitorAttempt < TimeSpan.FromMinutes(5))
                {
                    return null;
                }

                _lastHardwareMonitorAttempt = DateTime.Now;

                try
                {
                    var computer = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMotherboardEnabled = true,
                        IsControllerEnabled = true
                    };

                    computer.Open();
                    _hardwareMonitor = computer;
                    _hardwareMonitorUnavailable = false;
                    return _hardwareMonitor;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SystemStats] LibreHardwareMonitor init failed: {ex.Message}");
                    try { _hardwareMonitor?.Close(); } catch { }
                    _hardwareMonitor = null;
                    _hardwareMonitorUnavailable = true;
                    return null;
                }
            }
        }

        private static void ReadHardwareTemperatures(IHardware hardware, ref float? cpu, ref float? gpu)
        {
            try { hardware.Update(); } catch { }

            foreach (var subHardware in hardware.SubHardware)
            {
                ReadHardwareTemperatures(subHardware, ref cpu, ref gpu);
            }

            var hardwareType = hardware.HardwareType.ToString();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                {
                    continue;
                }

                var value = sensor.Value.Value;
                if (!IsValidTemperature(value)) continue;

                var sensorName = $"{hardware.Name} {sensor.Name}".ToLowerInvariant();
                if (IsCpuSensor(hardwareType, sensorName))
                {
                    cpu = PickTemperature(cpu, value);
                }
                else if (IsGpuSensor(hardwareType, sensorName))
                {
                    gpu = PickTemperature(gpu, value);
                }
            }
        }

        private static bool IsCpuSensor(string hardwareType, string sensorName)
        {
            return hardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase) ||
                   sensorName.Contains("cpu") ||
                   sensorName.Contains("processor") ||
                   sensorName.Contains("core") ||
                   sensorName.Contains("package") ||
                   sensorName.Contains("tctl") ||
                   sensorName.Contains("tdie");
        }

        private static bool IsGpuSensor(string hardwareType, string sensorName)
        {
            return hardwareType.Contains("Gpu", StringComparison.OrdinalIgnoreCase) ||
                   sensorName.Contains("gpu") ||
                   sensorName.Contains("graphics") ||
                   sensorName.Contains("video");
        }

        private static float? PickTemperature(float? current, float value)
        {
            return current == null ? value : Math.Max(current.Value, value);
        }

        private static bool IsValidTemperature(float value)
        {
            return value > 0 && value < 130;
        }

        private static void TryReadHardwareMonitorTemperatures(string scope, ref float? cpu, ref float? gpu)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, "SELECT Name, SensorType, Value, HardwareType FROM Sensor WHERE SensorType = 'Temperature'");
                foreach (ManagementObject item in searcher.Get())
                {
                    var value = Convert.ToSingle(item["Value"]);
                    if (value <= 0 || value > 130) continue;

                    var name = (item["Name"]?.ToString() ?? string.Empty).ToLowerInvariant();
                    var hardwareType = (item["HardwareType"]?.ToString() ?? string.Empty).ToLowerInvariant();
                    var combined = $"{hardwareType} {name}";

                    if (cpu == null && (combined.Contains("cpu") || combined.Contains("processor") || combined.Contains("core")))
                    {
                        cpu = value;
                    }
                    else if (gpu == null && combined.Contains("gpu"))
                    {
                        gpu = value;
                    }
                }
            }
            catch { }
        }

        private static float? TryReadAcpiTemperature()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                var values = new List<float>();
                foreach (ManagementObject item in searcher.Get())
                {
                    var raw = Convert.ToDouble(item["CurrentTemperature"]);
                    var celsius = (float)((raw / 10.0) - 273.15);
                    if (celsius > 0 && celsius < 130)
                    {
                        values.Add(celsius);
                    }
                }

                return values.Count == 0 ? null : values.Max();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            Stop();
            try { _timer.Tick -= Timer_Tick; } catch { }
            lock (_hardwareMonitorLock)
            {
                try { _hardwareMonitor?.Close(); } catch { }
                _hardwareMonitor = null;
            }
        }

        private readonly record struct NetworkSample(long BytesSent, long BytesReceived, DateTime Timestamp);
    }
}
