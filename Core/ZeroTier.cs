using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GRID0.Core;

// Everything the app knows about ZeroTier: where its CLI lives on each OS,
// whether it is alive, how to install it, and how to read node/network state.
// Joining the network is not enough on its own: each member still needs
// authorizing on the network unless the network auto-authorizes.
internal static class ZeroTier
{
    public const string NetworkId = "8bd5124fd68185ec";
    private const string ZtVersion = "1.16.2";

    private static string WindowsMsiUrl =>
        "https://download.zerotier.com/RELEASES/" + ZtVersion + "/dist/ZeroTier%20One.msi";
    private static string MacPkgUrl =>
        "https://download.zerotier.com/RELEASES/" + ZtVersion + "/dist/ZeroTier%20One.pkg";

    public static string? FindCli()
    {
        if (OperatingSystem.IsWindows())
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var c = Path.Combine(pf86, "ZeroTier", "One", "zerotier-cli.bat");
            if (File.Exists(c)) return c;
            var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            c = Path.Combine(pf64, "ZeroTier", "One", "zerotier-cli.bat");
            if (File.Exists(c)) return c;
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var c in new[] { "/usr/local/bin/zerotier-cli", "/opt/homebrew/bin/zerotier-cli" })
                if (File.Exists(c)) return c;
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var c in new[] { "/usr/sbin/zerotier-cli", "/usr/bin/zerotier-cli", "/sbin/zerotier-cli" })
                if (File.Exists(c)) return c;
        }
        return null;
    }

    // A leftover CLI from a broken install is not enough: it must answer.
    public static async Task<bool> IsHealthyAsync(Action<string>? log = null)
    {
        if (FindCli() is null) return false;
        try
        {
            var (code, _, _) = await CliAsync("info", log);
            return code == 0;
        }
        catch { return false; }
    }

    // Waits up to ~90s for the CLI to answer, with progress in the log, so a
    // slow (but alive) service is never mistaken for a dead one.
    public static async Task<bool> WaitForCliAsync(Action<string>? log = null)
    {
        log?.Invoke("Waiting for ZeroTier to answer...");
        for (var i = 0; i < 30; i++)
        {
            if (await IsHealthyAsync(log))
            {
                if (i > 0) log?.Invoke("ZeroTier answered.");
                return true;
            }
            if (i > 0 && i % 10 == 0)
                log?.Invoke("Still waiting for ZeroTier to answer... (" + (i * 3) + "s)");
            await Task.Delay(3000);
        }
        return false;
    }

    // True when Windows knows the ZeroTier service at all.
    public static async Task<bool> IsServiceRegisteredAsync()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var (code, _, _) = await RunAsync("sc.exe", new[] { "query", "ZeroTierOneService" }, 15000);
        return code == 0;
    }

    // The CLI files exist but the service is not registered: a maintenance
    // reinstall can skip re-registering it, so remove fully and install fresh.
    public static async Task RepairAsync(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
            throw new Exception("Repair is only implemented on Windows.");
        var msi = await DownloadAsync(WindowsMsiUrl, "ZeroTierOne.msi", log);
        log("Removing the broken ZeroTier install...");
        await RunAsync("msiexec.exe", new[] { "/x", msi, "/quiet", "/norestart" }, 300000);
        log("Running the ZeroTier installer silently...");
        var (code, _, err) = await RunAsync("msiexec.exe",
            new[] { "/i", msi, "/quiet", "/norestart" }, 300000);
        if (code != 0)
            throw new Exception("The ZeroTier installer exited with code " + code + "." +
                (string.IsNullOrWhiteSpace(err) ? "" : " " + err.Trim()));
        log("Installer finished.");
        if (!await StartServiceAsync(log))
            throw new Exception("ZeroTier installed, but its service is still not registered. " +
                "Install ZeroTier by hand from zerotier.com/download, then press Retry.");
    }

    // Starts the ZeroTier background service where one exists. Returns true
    // when the CLI answers afterwards. A service that cannot start is not
    // fixed by reinstalling: it needs a reboot (fresh driver) or a manual
    // reinstall, so report that instead of looping.
    public static async Task<bool> StartServiceAsync(Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsMacOS())
            {
                log("Starting the ZeroTier service...");
                await RunAsync("/bin/launchctl", new[] { "load", "/Library/LaunchDaemons/com.zerotier.one.plist" }, 30000);
            }
            else if (OperatingSystem.IsLinux())
            {
                log("Starting the ZeroTier service...");
                _ = File.Exists("/usr/bin/pkexec")
                    ? await RunAsync("/usr/bin/pkexec", new[] { "systemctl", "start", "zerotier-one" }, 30000)
                    : await RunAsync("sudo", new[] { "systemctl", "start", "zerotier-one" }, 30000);
            }
            return await IsHealthyAsync();
        }

        log("Checking the ZeroTier service...");
        var (qCode, qOut, _) = await RunAsync("sc.exe", new[] { "query", "ZeroTierOneService" }, 15000);
        if (qCode != 0)
        {
            log("The ZeroTier service is not registered on this PC.");
            return false;
        }
        var running = qOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        if (running && await IsHealthyAsync())
        {
            log("The ZeroTier service is already running.");
            return true;
        }
        if (running)
        {
            // The service claims to be running but the CLI cannot reach it
            // yet. A cold service can be slow to answer, so give it some time
            // before deciding it is hung and restarting it.
            log("The ZeroTier service is running but not answering yet. Giving it a moment...");
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(5000);
                if (await IsHealthyAsync(log))
                {
                    log("The ZeroTier service answered.");
                    return true;
                }
            }
            // Still silent: it is hung. Restart it once; a reinstall would not fix this.
            log("The ZeroTier service is running but not responding. Restarting it...");
            await RunAsync("sc.exe", new[] { "stop", "ZeroTierOneService" }, 60000);
            await Task.Delay(5000);
            var (q2Code, q2Out, _) = await RunAsync("sc.exe", new[] { "query", "ZeroTierOneService" }, 15000);
            if (q2Code == 0 && q2Out.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                log("The ZeroTier service would not stop. Restart your PC and run GRID0 again.");
                return false;
            }
            qOut = q2Out;
        }
        if (qOut.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            log("The ZeroTier service is stuck starting. Restart your PC and run GRID0 again.");
            return false;
        }
        // Make sure the service is not disabled, then start it.
        await RunAsync("sc.exe", new[] { "config", "ZeroTierOneService", "start=", "auto" }, 15000);
        log("Starting the ZeroTier service...");
        var (sCode, sOut, sErr) = await RunAsync("sc.exe", new[] { "start", "ZeroTierOneService" }, 60000);
        if (sCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(sErr) ? sOut.Trim() : sErr.Trim();
            log("The ZeroTier service would not start." + (detail.Length == 0 ? "" : " " + detail));
            return false;
        }
        return await WaitForCliAsync(log);
    }

    public static async Task<(int ExitCode, string Message)> JoinAsync()
    {
        var (code, stdout, stderr) = await CliAsync("join " + NetworkId);
        var msg = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
        return (code, msg);
    }

    public static async Task<(bool CliOk, bool NodeOnline)> GetNodeAsync()
    {
        try
        {
            var (code, stdout, _) = await CliJsonAsync("info");
            if (code != 0) return (false, false);
            using var doc = JsonDocument.Parse(stdout);
            var online = doc.RootElement.TryGetProperty("online", out var o) && o.GetBoolean();
            return (true, online);
        }
        catch { return (false, false); }
    }

    // The GRID0 network's membership state, or (null, []) when not joined.
    // Note: listnetworks' "OK" is cached membership/config state. It can stay
    // "OK" while the node itself is offline or the OS adapter is disabled,
    // so never trust it alone.
    public static async Task<(string? Status, string[] ManagedIps)> GetNetworkAsync()
    {
        try
        {
            var (code, stdout, _) = await CliJsonAsync("listnetworks");
            if (code != 0) return (null, Array.Empty<string>());
            using var doc = JsonDocument.Parse(stdout);
            foreach (var net in doc.RootElement.EnumerateArray())
            {
                if (net.GetProperty("id").GetString() != NetworkId) continue;
                var status = net.GetProperty("status").GetString();
                var ips = net.TryGetProperty("assignedAddresses", out var addrs)
                    ? addrs.EnumerateArray()
                        .Select(a => a.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => s!.Split('/')[0])
                        .ToArray()
                    : Array.Empty<string>();
                return (status, ips);
            }
            return (null, Array.Empty<string>());
        }
        catch { return (null, Array.Empty<string>()); }
    }

    public static async Task InstallAsync(Action<string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            var msi = await DownloadAsync(WindowsMsiUrl, "ZeroTierOne.msi", log);
            log("Running the ZeroTier installer silently...");
            var (code, _, err) = await RunAsync("msiexec.exe",
                new[] { "/i", msi, "/quiet", "/norestart" }, 300000);
            if (code != 0)
                throw new Exception("The ZeroTier installer exited with code " + code + "." +
                    (string.IsNullOrWhiteSpace(err) ? "" : " " + err.Trim()));
            log("Installer finished.");
            if (!await StartServiceAsync(log))
                throw new Exception("ZeroTier installed, but its service did not start. " +
                    "Restart your PC and run GRID0 again.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var pkg = await DownloadAsync(MacPkgUrl, "ZeroTierOne.pkg", log);
            log("Running the ZeroTier installer (macOS will ask for your password)...");
            var script = "do shell script \"installer -pkg " + pkg + " -target /\" with administrator privileges";
            var (code, _, err) = await RunAsync("/usr/bin/osascript",
                new[] { "-e", script }, 300000);
            if (code != 0)
                throw new Exception("The ZeroTier installer failed." +
                    (string.IsNullOrWhiteSpace(err) ? "" : " " + err.Trim()));
            log("Installer finished.");
        }
        else if (OperatingSystem.IsLinux())
        {
            log("Installing ZeroTier (you will be asked for your password)...");
            var installCmd = "curl -s https://install.zerotier.com | bash";
            (int code, string _, string err) = File.Exists("/usr/bin/pkexec")
                ? await RunAsync("/usr/bin/pkexec", new[] { "bash", "-c", installCmd }, 300000)
                : await RunAsync("sudo", new[] { "bash", "-c", installCmd }, 300000);
            if (code != 0)
                throw new Exception("The ZeroTier install failed." +
                    (string.IsNullOrWhiteSpace(err) ? "" : " " + err.Trim()));
            log("Installer finished. Starting the service...");
            _ = File.Exists("/usr/bin/pkexec")
                ? await RunAsync("/usr/bin/pkexec", new[] { "systemctl", "enable", "--now", "zerotier-one" }, 60000)
                : await RunAsync("sudo", new[] { "systemctl", "enable", "--now", "zerotier-one" }, 60000);
        }
        else
        {
            throw new Exception("This operating system is not supported.");
        }
    }

    private static async Task<string> DownloadAsync(string url, string fileName, Action<string> log)
    {
        var dest = Path.Combine(Path.GetTempPath(), fileName);
        log("Downloading the ZeroTier installer...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var net = await resp.Content.ReadAsStreamAsync();
        await using var fs = File.Create(dest);
        var buffer = new byte[81920];
        int n;
        while ((n = await net.ReadAsync(buffer)) > 0)
            await fs.WriteAsync(buffer.AsMemory(0, n));
        log("Download complete.");
        return dest;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> CliAsync(string args, Action<string>? log = null)
    {
        var cli = FindCli();
        if (cli is null) throw new InvalidOperationException("ZeroTier is not installed.");
        var r = OperatingSystem.IsWindows()
            ? await RunCmdAsync("\"\"" + cli + "\" " + args + "\"")
            : await RunAsync(cli, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (log is not null && r.ExitCode != 0)
            log("zerotier-cli " + args + " -> exit " + r.ExitCode + FirstLine(r.StdErr));
        return r;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> CliJsonAsync(string args, Action<string>? log = null)
    {
        var cli = FindCli();
        if (cli is null) throw new InvalidOperationException("ZeroTier is not installed.");
        var r = OperatingSystem.IsWindows()
            ? await RunCmdAsync("\"\"" + cli + "\" -j " + args + "\"")
            : await RunAsync(cli, new[] { "-j", args });
        if (log is not null && r.ExitCode != 0)
            log("zerotier-cli -j " + args + " -> exit " + r.ExitCode + FirstLine(r.StdErr));
        return r;
    }

    // First stderr line, trimmed short for the log.
    private static string FirstLine(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";
        var line = s.Split('\n')[0].Trim();
        return ": " + (line.Length > 160 ? line.Substring(0, 160) + "..." : line);
    }

    // Runs a .bat through cmd.exe with the command line passed verbatim.
    // This must not go through RunAsync: ProcessStartInfo.ArgumentList adds
    // its own quoting layer, and the doubled quotes break cmd's parsing, so
    // the CLI silently never runs and the app thinks ZeroTier is dead.
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCmdAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) throw new Exception("Could not start cmd.exe.");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => p.WaitForExit(30000));
        if (!exited)
        {
            try { p.Kill(); } catch { }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, await outTask, await errTask);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) throw new Exception("Could not start " + fileName + ".");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => p.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { p.Kill(); } catch { }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, await outTask, await errTask);
    }
}

