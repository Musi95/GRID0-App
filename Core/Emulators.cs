using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GRID0.Core;

// Points Nintendo Switch emulators at the ZeroTier adapter so native
// LAN-mode games work over the GRID0 network. LAN only, no LDN.
// Ryujinx family: enable_internet_access=true, multiplayer_mode=0,
// multiplayer_lan_interface_id=<ZeroTier adapter id>.
// Yuzu family: [Network] network_interface=<ZeroTier adapter name>.
// Astris: detected only, its config layout is unverified.
internal static class Emulators
{
    private enum Family { Ryujinx, Yuzu, Manual }

    private sealed class Emulator
    {
        public string DisplayName { get; init; } = "";
        public string[] ProcessMatches { get; init; } = Array.Empty<string>();
        public Family Family { get; init; }
        public string ConfigFolder { get; init; } = "";
    }

    private sealed class DetectedEmu
    {
        public Emulator Emu { get; }
        public string[] ProcessNames { get; }
        public string? ExeDir { get; }
        public DetectedEmu(Emulator emu, string[] processNames, string? exeDir)
        {
            Emu = emu;
            ProcessNames = processNames;
            ExeDir = exeDir;
        }
    }

    private static readonly Emulator[] AllEmulators = new[]
    {
        new Emulator { DisplayName = "Ryujinx",  ProcessMatches = new[] { "ryujinx" }, Family = Family.Ryujinx, ConfigFolder = "Ryujinx" },
        new Emulator { DisplayName = "Ryubing",  ProcessMatches = new[] { "ryubing" }, Family = Family.Ryujinx, ConfigFolder = "Ryujinx" },
        new Emulator { DisplayName = "Kenji-NX", ProcessMatches = new[] { "kenji" },   Family = Family.Ryujinx, ConfigFolder = "Ryujinx" },
        new Emulator { DisplayName = "Hyjinx",   ProcessMatches = new[] { "hyjinx" },  Family = Family.Ryujinx, ConfigFolder = "Hyjinx" },
        new Emulator { DisplayName = "Eden",       ProcessMatches = new[] { "eden" },    Family = Family.Yuzu, ConfigFolder = "eden" },
        new Emulator { DisplayName = "Sudachi",    ProcessMatches = new[] { "sudachi" }, Family = Family.Yuzu, ConfigFolder = "sudachi" },
        new Emulator { DisplayName = "Citron NEO", ProcessMatches = new[] { "citron" },  Family = Family.Yuzu, ConfigFolder = "citron" },
        new Emulator { DisplayName = "Torzu",      ProcessMatches = new[] { "torzu" },   Family = Family.Yuzu, ConfigFolder = "yuzu" },
        new Emulator { DisplayName = "Suyu",       ProcessMatches = new[] { "suyu" },    Family = Family.Yuzu, ConfigFolder = "suyu" },
        new Emulator { DisplayName = "yuzu",       ProcessMatches = new[] { "yuzu" },    Family = Family.Yuzu, ConfigFolder = "yuzu" },
        new Emulator { DisplayName = "Astris",     ProcessMatches = new[] { "astris" },  Family = Family.Manual, ConfigFolder = "" },
    };

    // promptCloseAsync shows a dialog asking the user to close the named
    // emulator; it returns true when the user confirms it is closed.
    public static async Task<int> ConfigureAsync(Action<string> log, Func<string, Task<bool>> promptCloseAsync)
    {
        NetworkInterface? zt;
        try
        {
            zt = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                (n.Name ?? "").Contains("zerotier", StringComparison.OrdinalIgnoreCase) ||
                (n.Description ?? "").Contains("zerotier", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            throw new Exception("Could not list network adapters: " + ex.Message);
        }
        if (zt is null)
            throw new Exception("No ZeroTier network adapter found. Connect to GRID0 first.");

        log("ZeroTier adapter: " + zt.Name);

        var detected = DetectRunning(log);
        var targets = new List<(Emulator emu, string? exeDir)>();

        if (detected.Count == 0)
        {
            log("No running emulators detected. Scanning known config locations instead.");
            foreach (var emu in AllEmulators)
                targets.Add((emu, null));
        }
        else
        {
            foreach (var d in detected)
            {
                log("Detected running: " + d.Emu.DisplayName +
                    " (process: " + string.Join(", ", d.ProcessNames) + ")");
                if (d.Emu.Family == Family.Manual)
                {
                    log("Astris config layout is unverified, skipping. " +
                        "Set it up manually: point its LAN/network interface at the ZeroTier adapter.");
                    continue;
                }
                log(d.Emu.DisplayName + " must be closed before its config can be edited.");
                if (!await promptCloseAsync(d.Emu.DisplayName))
                {
                    log("Skipped " + d.Emu.DisplayName + ".");
                    continue;
                }
                if (IsStillRunning(d.Emu))
                {
                    log(d.Emu.DisplayName + " is still running. Skipping it.");
                    continue;
                }
                targets.Add((d.Emu, d.ExeDir));
            }
        }

        var donePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int configured = 0;

        foreach (var (emu, exeDir) in targets)
        {
            if (emu.Family == Family.Manual)
            {
                log(emu.DisplayName + ": config layout unverified, skipping (manual setup required).");
                continue;
            }

            var path = ResolveConfigPath(emu, exeDir);
            if (path is null)
            {
                log(emu.DisplayName + ": config not found, skipping.");
                continue;
            }
            if (!donePaths.Add(path))
            {
                log(emu.DisplayName + ": same config file already handled, skipping.");
                continue;
            }

            log(emu.DisplayName + ": " + path);
            try
            {
                bool changed = emu.Family == Family.Ryujinx
                    ? ApplyRyujinxConfig(path, zt, log)
                    : ApplyYuzuConfig(path, zt, log);
                if (changed) configured++;
            }
            catch (Exception ex)
            {
                log("ERROR editing config: " + ex.Message);
            }
        }

        log(configured > 0
            ? "Updated " + configured + " config file(s). Backups saved next to the originals (.bak)."
            : "Nothing was changed.");
        return configured;
    }

    private static List<DetectedEmu> DetectRunning(Action<string> log)
    {
        var result = new List<DetectedEmu>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (Exception ex)
        {
            log("WARNING: could not list processes: " + ex.Message);
            return result;
        }

        foreach (var emu in AllEmulators)
        {
            var matches = procs.Where(p => MatchesEmu(p, emu)).ToList();
            if (matches.Count == 0) continue;

            string? exeDir = null;
            foreach (var p in matches)
            {
                exeDir = TryGetExeDir(p);
                if (exeDir is not null) break;
            }

            var names = matches
                .Select(p => { try { return p.ProcessName; } catch { return "?"; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            result.Add(new DetectedEmu(emu, names, exeDir));
        }
        return result;
    }

    private static bool MatchesEmu(Process p, Emulator emu)
    {
        string name;
        try { name = p.ProcessName ?? ""; }
        catch { return false; }
        return emu.ProcessMatches.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetExeDir(Process p)
    {
        try
        {
            var file = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(file)) return null;
            return Path.GetDirectoryName(file);
        }
        catch { return null; }
    }

    private static bool IsStillRunning(Emulator emu)
    {
        try { return Process.GetProcesses().Any(p => MatchesEmu(p, emu)); }
        catch { return true; } // fail closed: assume still running
    }

    private static string? ResolveConfigPath(Emulator emu, string? exeDir)
    {
        var candidates = new List<string>();

        // Portable installs live next to the emulator executable.
        if (exeDir is not null)
        {
            candidates.Add(emu.Family == Family.Ryujinx
                ? Path.Combine(exeDir, "portable", "Config.json")
                : Path.Combine(exeDir, "user", "config", "qt-config.ini"));
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (emu.Family == Family.Ryujinx)
        {
            string f = emu.ConfigFolder;
            if (OperatingSystem.IsWindows())
                candidates.Add(Path.Combine(appData, f, "Config.json"));
            else if (OperatingSystem.IsLinux())
                candidates.Add(Path.Combine(home, ".config", f, "Config.json"));
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add(Path.Combine(home, "Library", "Application Support", f, "Config.json"));
                candidates.Add(Path.Combine(home, ".config", f, "Config.json"));
            }
        }
        else // yuzu family
        {
            string n = emu.ConfigFolder;
            if (OperatingSystem.IsWindows())
                candidates.Add(Path.Combine(appData, n, "config", "qt-config.ini"));
            else if (OperatingSystem.IsLinux())
            {
                candidates.Add(Path.Combine(home, ".config", n, "qt-config.ini"));
                if (emu.DisplayName == "Suyu")
                    candidates.Add(Path.Combine(home, ".var", "app", "dev.suyu_emu.suyu", "config", "suyu", "qt-config.ini"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add(Path.Combine(home, ".config", n, "qt-config.ini"));
                candidates.Add(Path.Combine(home, "Library", "Application Support", n, "config", "qt-config.ini"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void Backup(string path, Action<string> log)
    {
        string bak = path + ".bak";
        File.Copy(path, bak, overwrite: true);
        log("Backup saved: " + bak);
    }

    private static bool ApplyRyujinxConfig(string path, NetworkInterface zt, Action<string> log)
    {
        Backup(path, log);
        var root = JsonNode.Parse(File.ReadAllText(path));
        if (root is not JsonObject obj)
            throw new InvalidDataException("Config.json root is not a JSON object.");

        var changes = new List<string>();
        SetJson(obj, "enable_internet_access", JsonValue.Create(true), changes);
        SetJson(obj, "multiplayer_mode", JsonValue.Create(0), changes);
        SetJson(obj, "multiplayer_lan_interface_id", JsonValue.Create(zt.Id), changes);

        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        if (changes.Count == 0)
        {
            log("Already configured for GRID0, no changes needed.");
            return false;
        }
        foreach (var c in changes) log(c);
        return true;
    }

    private static void SetJson(JsonObject obj, string key, JsonNode value, List<string> changes)
    {
        string oldText = obj[key]?.ToString() ?? "(missing)";
        string newText = value.ToString();
        if (oldText == newText) return;
        obj[key] = value;
        changes.Add(key + ": " + oldText + " -> " + newText);
    }

    private static bool ApplyYuzuConfig(string path, NetworkInterface zt, Action<string> log)
    {
        Backup(path, log);
        var lines = File.ReadAllLines(path).ToList();
        const string targetSection = "Network";
        const string targetKey = "network_interface";

        int sectionIdx = -1;
        int keyIdx = -1;
        string? currentSection = null;

        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]") && t.Length > 2)
            {
                currentSection = t.Substring(1, t.Length - 2).Trim();
                if (currentSection.Equals(targetSection, StringComparison.OrdinalIgnoreCase) && sectionIdx == -1)
                    sectionIdx = i;
                continue;
            }
            if (currentSection is not null &&
                currentSection.Equals(targetSection, StringComparison.OrdinalIgnoreCase) &&
                keyIdx == -1)
            {
                if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                    keyIdx = i;
            }
        }

        string oldVal = "(missing)";
        if (keyIdx != -1)
        {
            string t = lines[keyIdx].Trim();
            int eq = t.IndexOf('=');
            oldVal = t.Substring(eq + 1).Trim();
            if (oldVal == zt.Name)
            {
                log("Already configured for GRID0, no changes needed.");
                return false;
            }
            lines[keyIdx] = targetKey + "=" + zt.Name;
        }
        else if (sectionIdx != -1)
        {
            lines.Insert(sectionIdx + 1, targetKey + "=" + zt.Name);
        }
        else
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                lines.Add("");
            lines.Add("[" + targetSection + "]");
            lines.Add(targetKey + "=" + zt.Name);
        }

        File.WriteAllLines(path, lines);
        log("[Network] " + targetKey + ": " + oldVal + " -> " + zt.Name);
        return true;
    }
}
