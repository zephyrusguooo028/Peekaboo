using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Peekaboo;

public enum AppLanguage { System, Zh, En }

public enum ThemeChoice { System, Mono, Load, Pink, Mint, Purple, Orange, Blue }

public enum TrayIcon { None, Slime, Ghost, Chick, Whale, Rocket, Heart, Pulse, Fan, Bars }

/// Metrics the user can tick. Each numeric one gets its own small tray icon with the value.
public enum TrayMetric { Cpu, CpuGraph, Memory, Disk, Network, Gpu, Battery, Typing }

public sealed class Settings
{
    public TrayIcon Icon { get; set; } = TrayIcon.Ghost;
    public List<TrayMetric> Metrics { get; set; } = new() { TrayMetric.Cpu, TrayMetric.Memory };
    public bool IconTint { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// Download updates in the background and swap them in when idle or on quit.
    public bool AutoInstallUpdate { get; set; }
    /// Last version a silent check showed a balloon for, so restarts don't repeat it.
    public string? LastNotifiedUpdate { get; set; }
    /// Set just before the updater swaps in a new exe; the next launch shows "Updated to" once.
    public string? UpdatedTo { get; set; }
    public double SampleInterval { get; set; } = 2;
    public bool TypingEnabled { get; set; }
    public int TypingGoal { get; set; } = 10000;
    /// null = no shortcut.
    public HotkeyData? ShotKey { get; set; } = HotkeyData.Default;
    public string ShotFolder { get; set; } = "~\\Desktop";

    public List<string> ProjectRoots { get; set; } = new() { "~\\Documents", "~\\Desktop", "~\\source\\repos", "~\\Projects", "~\\code", "~\\dev" };
    public List<string> Whitelist { get; set; } = new();
    public int DownloadsOldDays { get; set; } = 90;
    public int LargeFileMB { get; set; } = 500;
    public int StaleProjectDays { get; set; } = 60;

    public bool AlertDisk { get; set; } = true;
    public int AlertDiskGB { get; set; } = 20;
    /// Tray balloon when free space drops more than 5 GB in 24 hours (once a day at most).
    public bool AlertDiskDrop { get; set; } = true;
    public DateTime? LastDiskDropAlert { get; set; }
    public bool AlertMemory { get; set; } = true;
    public bool AlertCpu { get; set; }
    public int AlertCpuPercent { get; set; } = 90;
    public bool CleanupTips { get; set; } = true;
    public int CleanupTipGB { get; set; } = 5;
    public DateTime? LastCleanupTip { get; set; }

    // ---- persistence

    [JsonIgnore] public static Settings Shared { get; private set; } = new();
    public static event Action? Changed;

    static string FilePath => Path.Combine(Paths.Support, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Shared = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { Shared = new Settings(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
        Changed?.Invoke();
    }

    // ---- helpers

    public static string Expand(string p) => p.StartsWith("~") ? Paths.Home + p[1..] : Environment.ExpandEnvironmentVariables(p);

    public IEnumerable<string> ProjectRootPaths => ProjectRoots.Select(Expand).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

    public bool IsWhitelisted(string path)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\');
        return Whitelist.Any(w =>
        {
            var wp = Path.GetFullPath(Expand(w)).TrimEnd('\\');
            return p.Equals(wp, StringComparison.OrdinalIgnoreCase) || p.StartsWith(wp + "\\", StringComparison.OrdinalIgnoreCase);
        });
    }

    public bool Shows(TrayMetric m) => Metrics.Contains(m);

    public void SetShown(TrayMetric m, bool on)
    {
        var set = new HashSet<TrayMetric>(Metrics);
        if (on) set.Add(m); else set.Remove(m);
        Metrics = Enum.GetValues<TrayMetric>().Where(set.Contains).ToList();
        Save();
    }

    // ---- launch at login (HKCU Run key, no admin needed)

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool LaunchAtLogin
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Peekaboo") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Peekaboo", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("Peekaboo", throwOnMissingValue: false);
        }
    }

    /// True when the taskbar / tray uses the light theme.
    public static bool TaskbarIsLight
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
    }

    /// Windows accent colour (DWM AccentColor is stored as ABGR).
    public static System.Windows.Media.Color SystemAccent
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int v)
            {
                uint u = unchecked((uint)v);
                return System.Windows.Media.Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
            return System.Windows.Media.Color.FromRgb(0, 120, 212);
        }
    }

    /// True when apps use the light theme.
    public static bool AppsAreLight
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v == 1;
        }
    }
}
