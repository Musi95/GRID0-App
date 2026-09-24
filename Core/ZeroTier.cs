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
    public static async Task<bool> IsHealthyAsync()
    {
        if (FindCli() is null) return false;
        try
        {
            var (code, _, _) = await CliAsync("info");
            return code == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> WaitForCliAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            if (await IsHealthyAsync()) return true;
            await Task.Delay(3000);
        }
        return false;
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
        if (qOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            log("The ZeroTier service is already running.");
            return await IsHealthyAsync();
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
        return await WaitForCliAsync();
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

    private static Task<(int ExitCode, string StdOut, string StdErr)> CliAsync(string args)
    {
        var cli = FindCli();
        if (cli is null) throw new InvalidOperationException("ZeroTier is not installed.");
        if (OperatingSystem.IsWindows())
        {
            // Run the .bat through cmd with the whole command quoted.
            return RunAsync("cmd.exe", new[] { "/c", "\"\"" + cli + "\" " + args + "\"" });
        }
        return RunAsync(cli, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> CliJsonAsync(string args)
    {
        var cli = FindCli();
        if (cli is null) throw new InvalidOperationException("ZeroTier is not installed.");
        if (OperatingSystem.IsWindows())
            return await RunAsync("cmd.exe", new[] { "/c", "\"\"" + cli + "\" -j " + args + "\"" });
        return await RunAsync(cli, new[] { "-j", args });
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
