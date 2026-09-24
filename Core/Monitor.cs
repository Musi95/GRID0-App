using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GRID0.Core;

// The full connection state, gathered from every layer that can lie to us:
// physical internet, the ZeroTier service, the node's reachability, network
// membership, the OS adapter, and the managed address on that adapter.
internal sealed record FullState(
    bool HasInternet,
    bool CliOk,
    bool NodeOnline,
    string? NetStatus,
    string ManagedIps,
    bool AdapterUp,
    bool AdapterHasIp);

internal static class Monitor
{
    public static async Task<FullState> GetFullStateAsync()
    {
        try
        {
            var internetTask = HasInternetAsync();
            var nodeTask = ZeroTier.GetNodeAsync();
            var netTask = ZeroTier.GetNetworkAsync();
            await Task.WhenAll(internetTask, nodeTask, netTask);

            var (cliOk, online) = nodeTask.Result;
            var (status, ips) = netTask.Result;
            var (adapterUp, adapterHasIp) = CheckAdapter(ips);

            return new FullState(
                internetTask.Result, cliOk, online, status,
                string.Join(", ", ips), adapterUp, adapterHasIp);
        }
        catch
        {
            return new FullState(false, false, false, null, "", false, false);
        }
    }

    // One decision tree for setup and monitoring alike. Every disconnect
    // case lands somewhere explicit: no internet, dead service, node
    // offline, left network, de-authorized, disabled adapter, or adapter
    // up without its GRID0 address. Returns (kind, title, detail, connected).
    public static (string Kind, string Title, string Detail, bool Connected) Decide(FullState s)
    {
        if (!s.HasInternet)
            return ("red", "No internet connection",
                "Turn your WiFi on. The app will continue automatically.", false);

        if (!s.CliOk)
            return ("red", "ZeroTier is not responding",
                "The ZeroTier service may have stopped. Restart ZeroTier, then press Retry.", false);

        if (!s.NodeOnline)
            return ("red", "ZeroTier is offline",
                "The ZeroTier node cannot reach ZeroTier's servers. Check your connection.", false);

        if (s.NetStatus is null)
            return ("gray", "Disconnected from GRID0",
                "You are not on the GRID0 network. Press Reconnect to join again.", false);

        if (s.NetStatus == "ACCESS_DENIED")
            return ("orange", "Waiting for authorization",
                "You joined the network. An admin still needs to approve your device.", false);

        if (!s.AdapterUp)
            return ("red", "ZeroTier adapter is down",
                "The ZeroTier network adapter is disabled or missing. Enable it in your network settings.", false);

        if (!s.AdapterHasIp)
            return ("red", "GRID0 address missing",
                "The adapter is up but has no GRID0 address. Press Reconnect.", false);

        if (s.NetStatus == "OK")
            return ("green", "Connected to GRID0",
                "Network " + ZeroTier.NetworkId +
                (s.ManagedIps.Length > 0 ? "\nYour address: " + s.ManagedIps : ""), true);

        return ("gray", "Joining the GRID0 network...", "Status: " + s.NetStatus, false);
    }

    // The OS-level ground truth. listnetworks can report OK while the
    // adapter is disabled in the OS, so check the adapter itself: it must
    // exist, be up, and hold one of the managed addresses.
    private static (bool Up, bool HasIp) CheckAdapter(string[] managedIps)
    {
        try
        {
            var zt = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                (n.Name ?? "").Contains("zerotier", StringComparison.OrdinalIgnoreCase) ||
                (n.Description ?? "").Contains("zerotier", StringComparison.OrdinalIgnoreCase));
            if (zt is null) return (false, false);
            var up = zt.OperationalStatus == OperationalStatus.Up;
            var hasIp = zt.GetIPProperties().UnicastAddresses
                .Select(a => a.Address.ToString())
                .Any(ip => managedIps.Contains(ip));
            return (up, hasIp);
        }
        catch { return (false, false); }
    }

    // Ground-truth internet check. ZeroTier's own "online" flag and
    // listnetworks both lag behind reality (a cached "OK" can linger
    // after WiFi drops), so reachability is verified directly.
    private static async Task<bool> HasInternetAsync()
    {
        try
        {
            using var client = new TcpClient();
            var connected = client.ConnectAsync("1.1.1.1", 443);
            var winner = await Task.WhenAny(connected, Task.Delay(3000));
            if (winner != connected) return false;
            try { await connected; }
            catch { return false; }
            return client.Connected;
        }
        catch { return false; }
    }
}
