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
    
    private readonly SemaphoreSlim _pingSemaphore = new SemaphoreSlim(5, 5); // Allow multiple concurrent pings
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromMilliseconds(3000); // Increased for better reliability
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

    // Ping method with default timeout
    public async Task<PingResult> SendPingAsync(string ipAddress)
    {
        return await SendPingAsync(ipAddress, (int)_defaultTimeout.TotalMilliseconds);
    }

    // Enhanced ping method with multiple fallback strategies for Flatpak compatibility
    public async Task<PingResult> SendPingAsync(string ipAddress, int timeout)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            _logger.Warning("Invalid IP address provided for ping");
            return new PingResult { Success = false, ErrorMessage = "Invalid IP address" };
        }

        // Throttle ping requests to same IP
        if (ShouldThrottlePing(ipAddress))
        {
            _logger.Verbose($"Throttling ping request to {ipAddress}");
            return new PingResult { Success = false, ErrorMessage = "Request throttled" };
        }

        _logger.Verbose($"Attempting to ping IP: {ipAddress} with timeout: {timeout}ms");
        
        if (await _pingSemaphore.WaitAsync(timeout))
        {
            try
            {
                // Update last ping time for throttling
                _lastPingTimes.AddOrUpdate(ipAddress, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);

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
                    IPAddress = ipAddress 
                };
            }
            finally
            {
                _pingSemaphore.Release();
            }
        }
        else
        {
            _logger.Warning("Timeout waiting to acquire ping semaphore for {IpAddress}", ipAddress);
            return new PingResult 
            { 
                Success = false, 
                ErrorMessage = "Ping operation timeout due to concurrent requests",
                IPAddress = ipAddress 
            };
        }
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
        
        foreach (var port in portsToTest)
        {
            try
            {
                using var client = new TcpClient();
                var startTime = DateTime.UtcNow;
                
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(Math.Min(timeout, 2000)); // Max 2 seconds per port
                
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

    // Throttling to prevent spam
    private bool ShouldThrottlePing(string ipAddress)
    {
        if (_lastPingTimes.TryGetValue(ipAddress, out var lastPing))
        {
            return DateTime.UtcNow - lastPing < TimeSpan.FromMilliseconds(100); // 100ms minimum between pings
        }
        return false;
    }

    // Cleanup old throttling entries
    private void CleanupThrottlingCache()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var keysToRemove = new List<string>();
        
        foreach (var kvp in _lastPingTimes)
        {
            if (kvp.Value < cutoff)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            _lastPingTimes.TryRemove(key, out _);
        }
    }

    // Bulk ping operation
    public async Task<Dictionary<string, PingResult>> SendBulkPingAsync(IEnumerable<string> ipAddresses, int timeout)
    {
        var tasks = ipAddresses.Select(async ip => new { IP = ip, Result = await SendPingAsync(ip, timeout) });
        var results = await Task.WhenAll(tasks);
        
        return results.ToDictionary(r => r.IP, r => r.Result);
    }

    // Dispose method to clean up all resources
    public void Dispose()
    {
        _pingSemaphore?.Dispose();
        _lastPingTimes?.Clear();
        GC.SuppressFinalize(this);
    }
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