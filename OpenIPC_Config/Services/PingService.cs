using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace OpenIPC_Config.Services;

public class PingService : IDisposable
{
    private static PingService _instance;
    private static readonly object _lock = new object();
    
    // Separate semaphores for different types of operations
    private readonly SemaphoreSlim _regularPingSemaphore = new SemaphoreSlim(20, 20); // Higher limit for regular pings
    private readonly SemaphoreSlim _scanPingSemaphore = new SemaphoreSlim(10, 10); // Separate pool for scans
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromMilliseconds(3000);
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastPingTimes = new ConcurrentDictionary<string, DateTime>();

    // Private constructor for singleton pattern
    private PingService(ILogger logger)
    {
        _logger = logger;
    }

    // Singleton instance getter
    public static PingService Instance(ILogger logger)
    {
        if (_instance == null)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new PingService(logger);
                }
            }
        }
        return _instance;
    }

    // Regular ping method (for device monitoring)
    public async Task<PingResult> SendPingAsync(string ipAddress)
    {
        return await SendPingAsync(ipAddress, (int)_defaultTimeout.TotalMilliseconds, PingType.Regular);
    }

    // Overload for scan operations
    public async Task<PingResult> SendScanPingAsync(string ipAddress, int timeout = 2000)
    {
        return await SendPingAsync(ipAddress, timeout, PingType.Scan);
    }

    // Enhanced ping method with operation type
    private async Task<PingResult> SendPingAsync(string ipAddress, int timeout, PingType pingType)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            _logger.Warning("Invalid IP address provided for ping");
            return new PingResult { Success = false, ErrorMessage = "Invalid IP address" };
        }

        // Choose appropriate semaphore based on operation type
        var semaphore = pingType == PingType.Scan ? _scanPingSemaphore : _regularPingSemaphore;
        var semaphoreTimeout = pingType == PingType.Scan ? timeout + 1000 : timeout + 5000; // More time for regular pings

        // Throttle ping requests to same IP for regular pings only
        if (pingType == PingType.Regular && ShouldThrottlePing(ipAddress))
        {
            _logger.Verbose($"Throttling ping request to {ipAddress}");
            return new PingResult { Success = false, ErrorMessage = "Request throttled" };
        }

        _logger.Verbose($"Attempting {pingType} ping to {ipAddress} with timeout: {timeout}ms");
        
        if (await semaphore.WaitAsync(semaphoreTimeout))
        {
            try
            {
                // Update last ping time for throttling (regular pings only)
                if (pingType == PingType.Regular)
                {
                    _lastPingTimes.AddOrUpdate(ipAddress, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
                }

                // For scan operations, use faster methods first
                if (pingType == PingType.Scan)
                {
                    return await ExecuteScanPing(ipAddress, timeout);
                }
                else
                {
                    return await ExecuteRegularPing(ipAddress, timeout);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
        else
        {
            _logger.Warning("Timeout waiting to acquire ping semaphore for {IpAddress} ({PingType})", ipAddress, pingType);
            return new PingResult 
            { 
                Success = false, 
                ErrorMessage = $"Ping operation timeout due to concurrent requests ({pingType})",
                IPAddress = ipAddress 
            };
        }
    }

    // Fast ping for scan operations
    private async Task<PingResult> ExecuteScanPing(string ipAddress, int timeout)
    {
        // For scans, prioritize speed - try TCP first, then .NET ping if needed
        var tcpResult = await TryTcpConnectivity(ipAddress, Math.Min(timeout, 1500));
        if (tcpResult.Success)
        {
            return tcpResult;
        }

        // Only try .NET ping if TCP failed and we have time left
        if (timeout > 1500)
        {
            var pingResult = await TryNetworkPing(ipAddress, timeout - 1500);
            if (pingResult.Success)
            {
                return pingResult;
            }
        }

        return new PingResult 
        { 
            Success = false, 
            ErrorMessage = "All scan methods failed",
            IPAddress = ipAddress,
            Method = "SCAN_FAILED" 
        };
    }

    // Comprehensive ping for regular monitoring
    private async Task<PingResult> ExecuteRegularPing(string ipAddress, int timeout)
    {
        // Strategy 1: Try .NET Ping (works with --allow=devel in Flatpak)
        var pingResult = await TryNetworkPing(ipAddress, timeout);
        if (pingResult.Success)
        {
            _logger.Verbose($"Ping successful via .NET: {ipAddress}, RTT: {pingResult.RoundtripTime}ms");
            return pingResult;
        }

        // Strategy 2: Try flatpak-spawn to escape sandbox
        var flatpakResult = await TryFlatpakSpawnPing(ipAddress, timeout);
        if (flatpakResult.Success)
        {
            _logger.Verbose($"Ping successful via flatpak-spawn: {ipAddress}");
            return flatpakResult;
        }

        // Strategy 3: TCP connectivity test (most reliable for network devices)
        var tcpResult = await TryTcpConnectivity(ipAddress, timeout);
        if (tcpResult.Success)
        {
            _logger.Verbose($"Connectivity confirmed via TCP: {ipAddress}");
            return tcpResult;
        }

        // All strategies failed
        _logger.Verbose($"All ping strategies failed for {ipAddress}");
        return new PingResult 
        { 
            Success = false, 
            ErrorMessage = "All ping methods failed",
            IPAddress = ipAddress,
            Method = "ALL_FAILED" 
        };
    }

    // Strategy 1: Traditional .NET Ping
    private async Task<PingResult> TryNetworkPing(string ipAddress, int timeout)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, timeout);
            
            return new PingResult
            {
                Success = reply.Status == IPStatus.Success,
                RoundtripTime = reply.Status == IPStatus.Success ? reply.RoundtripTime : 0,
                ErrorMessage = reply.Status != IPStatus.Success ? reply.Status.ToString() : null,
                IPAddress = ipAddress,
                Method = "NET_PING"
            };
        }
        catch (Exception ex)
        {
            _logger.Verbose($"NET Ping failed for {ipAddress}: {ex.Message}");
            return new PingResult 
            { 
                Success = false, 
                ErrorMessage = ex.Message,
                IPAddress = ipAddress,
                Method = "NET_PING" 
            };
        }
    }

    // Strategy 2: Use flatpak-spawn to escape sandbox
    private async Task<PingResult> TryFlatpakSpawnPing(string ipAddress, int timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "flatpak-spawn",
                Arguments = $"--host ping -c 1 -W {Math.Max(1, timeout / 1000)} {ipAddress}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var startTime = DateTime.UtcNow;
            
            process.Start();
            
            var completed = await WaitForExitAsync(process, TimeSpan.FromMilliseconds(timeout + 1000));
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            
            if (completed && process.ExitCode == 0)
            {
                return new PingResult
                {
                    Success = true,
                    RoundtripTime = (long)elapsed,
                    IPAddress = ipAddress,
                    Method = "FLATPAK_SPAWN"
                };
            }

            if (!process.HasExited)
            {
                process.Kill();
            }

            return new PingResult 
            { 
                Success = false, 
                ErrorMessage = $"flatpak-spawn ping failed with exit code: {process.ExitCode}",
                IPAddress = ipAddress,
                Method = "FLATPAK_SPAWN" 
            };
        }
        catch (Exception ex)
        {
            _logger.Verbose($"flatpak-spawn ping failed for {ipAddress}: {ex.Message}");
            return new PingResult 
            { 
                Success = false, 
                ErrorMessage = ex.Message,
                IPAddress = ipAddress,
                Method = "FLATPAK_SPAWN" 
            };
        }
    }

    // Strategy 3: TCP connectivity test (most reliable for network devices)
    private async Task<PingResult> TryTcpConnectivity(string ipAddress, int timeout)
    {
        // Common ports for network devices and cameras
        var portsToTest = new[] { 80, 443, 22, 23, 554, 8080, 8554 };
        var portTimeout = Math.Min(timeout / portsToTest.Length, 500); // Max 500ms per port
        
        foreach (var port in portsToTest)
        {
            try
            {
                using var client = new TcpClient();
                var startTime = DateTime.UtcNow;
                
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(portTimeout);
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                if (completedTask == connectTask && client.Connected)
                {
                    _logger.Verbose($"TCP connection successful to {ipAddress}:{port}");
                    return new PingResult
                    {
                        Success = true,
                        RoundtripTime = (long)elapsed,
                        IPAddress = ipAddress,
                        Method = $"TCP_{port}"
                    };
                }
                
                if (client.Connected)
                {
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.Verbose($"TCP connection to {ipAddress}:{port} failed: {ex.Message}");
            }
        }
        
        return new PingResult 
        { 
            Success = false, 
            ErrorMessage = "No TCP ports accessible",
            IPAddress = ipAddress,
            Method = "TCP" 
        };
    }

    // Helper method to wait for process completion with timeout
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
        }
        catch
        {
            return false;
        }
    }

    // Throttling to prevent spam (only for regular pings)
    private bool ShouldThrottlePing(string ipAddress)
    {
        if (_lastPingTimes.TryGetValue(ipAddress, out var lastPing))
        {
            return DateTime.UtcNow - lastPing < TimeSpan.FromMilliseconds(500); // 500ms minimum between regular pings
        }
        return false;
    }

    // Bulk ping operation for scans
    public async Task<Dictionary<string, PingResult>> SendBulkScanAsync(IEnumerable<string> ipAddresses, int timeout = 2000)
    {
        var tasks = ipAddresses.Select(async ip => new { IP = ip, Result = await SendScanPingAsync(ip, timeout) });
        var results = await Task.WhenAll(tasks);
        
        return results.ToDictionary(r => r.IP, r => r.Result);
    }

    // Dispose method to clean up all resources
    public void Dispose()
    {
        _regularPingSemaphore?.Dispose();
        _scanPingSemaphore?.Dispose();
        _lastPingTimes?.Clear();
        GC.SuppressFinalize(this);
    }
}

// Enum to distinguish ping operation types
public enum PingType
{
    Regular,  // For device monitoring
    Scan      // For network scanning
}

// Enhanced result class with more information
public class PingResult
{
    public bool Success { get; set; }
    public long RoundtripTime { get; set; }
    public string ErrorMessage { get; set; }
    public string IPAddress { get; set; }
    public string Method { get; set; } // Which method was successful
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}