using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Peekaboo;

/// One hourly reading of the system drive.
public sealed class FreePoint
{
    public DateTime T { get; set; }
    public long Free { get; set; }
    public long Total { get; set; }
}

/// A measured folder or file in a growth snapshot.
public sealed class GrowthEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}

/// One daily measurement of the watch points.
public sealed class GrowthSnapshot
{
    public DateTime Time { get; set; }
    public string Drive { get; set; } = "C:\\";
    public long Total { get; set; }
    /// Free bytes on the drive (not counting per-user quotas), so Total − Free is what the drive really holds.
    public long Free { get; set; }
    /// Sum of everything Peekaboo could see under the drive root.
    public long Accounted { get; set; }
    /// The current user's profile folder.
    public long Profile { get; set; }
    public double Seconds { get; set; }
    public int Denied { get; set; }
    /// Top-level children of the drive (plus the Recycle Bin) and their sizes.
    public Dictionary<string, long> Roots { get; set; } = new();
    /// Known system spots; null = no permission to read it.
    public Dictionary<string, long?> Spots { get; set; } = new();
    /// Watch points of at least 50 MB.
    public List<GrowthEntry> Entries { get; set; } = new();

    [JsonIgnore] public long Used => Math.Max(0, Total - Free);
    [JsonIgnore] public long Unseen => Math.Max(0, Used - Accounted);
}

public sealed record GrowthDelta(string Path, long Size, long Delta);

/// Disk history (hourly free space, 90 days), the "lost a lot in a day" alert, and daily
/// growth snapshots of the places that tend to grow (35 days). Read-only: nothing here deletes.
public static class DiskWatch
{
    public const long MinEntry = 50L << 20;
    public const long DropAlertBytes = 5L << 30;
    const int HistoryDays = 90, SnapshotDays = 35;

    /// `--demo` and the CI `--snapshot` run use made-up data so screenshots never show anyone's folders.
    public static bool Demo { get; } = Environment.GetCommandLineArgs().Any(a => a is "--demo" or "--snapshot");

    public static List<FreePoint> History { get; private set; } = new();
    public static List<GrowthSnapshot> Snapshots { get; private set; } = new();
    public static bool Scanning { get; private set; }
    public static string ScanStatus { get; private set; } = "";
    /// Structure changed (new reading, new snapshot, scan started / finished).
    public static event Action? Changed;
    /// Only the "measuring …" line changed.
    public static event Action? Progress;

    static DispatcherTimer? _timer, _first;
    static readonly JsonSerializerOptions Json = new();
    static string HistoryFile => Path.Combine(Paths.Support, "disk-history.json");
    static string SnapshotFile => Path.Combine(Paths.Support, "disk-growth.json");

    public static GrowthSnapshot? Latest => Snapshots.Count > 0 ? Snapshots[^1] : null;
    public static DateTime? RecordingSince => new[] { History.FirstOrDefault()?.T, Snapshots.FirstOrDefault()?.Time }.Where(t => t is not null).Min();
    public static GrowthScanner.Places Here => Demo ? GrowthDemo.Places : GrowthScanner.Current;

    public static long CurrentFree => Demo ? History.LastOrDefault()?.Free ?? 0
        : SystemMonitor.Shared.Disk.Total > 0 ? SystemMonitor.Shared.Disk.Free : History.LastOrDefault()?.Free ?? 0;
    public static long CurrentTotal => Demo ? History.LastOrDefault()?.Total ?? 0
        : SystemMonitor.Shared.Disk.Total > 0 ? SystemMonitor.Shared.Disk.Total : History.LastOrDefault()?.Total ?? 0;
    public static long? Change(TimeSpan window) => FreeChange(History, CurrentFree, DateTime.Now, window);
    public static long? Change24h => Change(TimeSpan.FromDays(1));

    public static void Start()
    {
        if (Demo) { (History, Snapshots) = GrowthDemo.Make(DateTime.Now); return; }
        Load();
        Record();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        // The first daily scan waits a couple of minutes so it doesn't compete with sign-in.
        _first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _first.Tick += (_, _) => { _first!.Stop(); if (ScanDue) Scan(); };
        _first.Start();
    }

    static bool ScanDue => Latest is not { } l || (DateTime.Now - l.Time).TotalHours >= 23;

    static void Tick()
    {
        if (History.Count == 0 || (DateTime.Now - History[^1].T).TotalMinutes >= 59) Record();
        if (ScanDue) Scan();
    }

    static void Record()
    {
        var now = DateTime.Now;
        if (History.Count > 0 && (now - History[^1].T).TotalMinutes < 10) return;   // quick restarts
        try
        {
            var d = new DriveInfo(Paths.SystemDrive);
            History.Add(new FreePoint { T = now, Free = d.AvailableFreeSpace, Total = d.TotalSize });
        }
        catch { return; }
        History.RemoveAll(p => (now - p.T).TotalDays > HistoryDays);
        Write(HistoryFile, History);
        CheckDrop();
        Changed?.Invoke();
    }

    static void CheckDrop()
    {
        var s = Settings.Shared; var now = DateTime.Now;
        if (!s.AlertDiskDrop || History.Count == 0) return;
        if (s.LastDiskDropAlert is { } last && (now - last).TotalHours < 24) return;
        if (FreeChange(History, History[^1].Free, now, TimeSpan.FromDays(1)) is not { } ch || -ch <= DropAlertBytes) return;
        s.LastDiskDropAlert = now; s.Save();
        TrayController.Shared.Notify(L($"磁盘一天少了 {Fmt.Bytes(-ch)}", $"Disk lost {Fmt.Bytes(-ch)} in a day"),
                                     L("打开「空间变化」看看是什么在长。", "Open Disk growth to see what grew."), Page.Growth);
    }

    /// Measures the watch points on a low-priority background thread and appends a snapshot.
    public static void Scan()
    {
        if (Scanning || Demo) return;
        Scanning = true; ScanStatus = ""; Changed?.Invoke();
        var t = new Thread(() =>
        {
            GrowthSnapshot? snap = null;
            bool bg = Native2.SetThreadPriority(Native2.GetCurrentThread(), Native2.THREAD_MODE_BACKGROUND_BEGIN);
            try { snap = GrowthScanner.Run(GrowthScanner.Current, p => UiThread.Run(() => { ScanStatus = p; Progress?.Invoke(); })); }
            catch (Exception e) { try { File.AppendAllText(Path.Combine(Paths.Support, "errors.log"), $"{DateTime.Now:u} growth scan {e}\n\n"); } catch { } }
            finally { if (bg) Native2.SetThreadPriority(Native2.GetCurrentThread(), Native2.THREAD_MODE_BACKGROUND_END); }
            UiThread.Run(() =>
            {
                Scanning = false; ScanStatus = "";
                if (snap is not null)
                {
                    Snapshots.Add(snap);
                    Snapshots.RemoveAll(x => (snap.Time - x.Time).TotalDays > SnapshotDays);
                    Write(SnapshotFile, Snapshots);
                }
                Changed?.Invoke();
            });
        }, 16 << 20) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Peekaboo growth scan" };
        t.Start();
    }

    static void Load()
    {
        History = Read<List<FreePoint>>(HistoryFile) ?? new();
        Snapshots = Read<List<GrowthSnapshot>>(SnapshotFile) ?? new();
        History.Sort((a, b) => a.T.CompareTo(b.T));
        Snapshots.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    static T? Read<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : null; } catch { return null; }
    }

    static void Write<T>(string file, T value)
    {
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }

    // ---- growth math (pure, covered by --selftest)

    /// The item whose time is closest to `target`, among those no later than `notAfter`.
    public static T? Closest<T>(IEnumerable<T> items, Func<T, DateTime> time, DateTime target, DateTime notAfter) where T : class
        => items.Where(i => time(i) <= notAfter).OrderBy(i => Math.Abs((time(i) - target).Ticks)).FirstOrDefault();

    /// Free space now minus free space about `window` ago (negative = space was lost).
    /// Needs a reading at least half the window old, so "7 days" never quietly means "2 hours".
    public static long? FreeChange(IList<FreePoint> history, long freeNow, DateTime now, TimeSpan window)
        => Closest(history, p => p.T, now - window, now - window / 2) is { } then ? freeNow - then.Free : null;

    /// The snapshot to compare `current` with: the one closest to `window` before it, at least 12 hours older.
    public static GrowthSnapshot? Baseline(IList<GrowthSnapshot> snaps, GrowthSnapshot current, TimeSpan window)
        => Closest(snaps.Where(s => !ReferenceEquals(s, current)), s => s.Time, current.Time - window, current.Time - TimeSpan.FromHours(12));

    /// What grew between two snapshots, biggest first. A path missing from `then` was under 50 MB (or new) and counts from 0.
    public static List<GrowthDelta> Diff(GrowthSnapshot now, GrowthSnapshot then, long minDelta = MinEntry)
    {
        var old = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in then.Entries) old.TryAdd(e.Path, e.Size);
        var list = now.Entries.Select(e => new GrowthDelta(e.Path, e.Size, e.Size - old.GetValueOrDefault(e.Path)))
                              .Where(d => d.Delta >= minDelta).OrderByDescending(d => d.Delta).ToList();
        return Dedupe(list, d => d.Delta);
    }

    public static List<GrowthDelta> Largest(GrowthSnapshot s, int n)
        => Dedupe(s.Entries.Select(e => new GrowthDelta(e.Path, e.Size, 0)).OrderByDescending(d => d.Size).ToList(), d => d.Size).Take(n).ToList();

    /// Drops a folder when one of its listed sub-folders already explains most (70%) of it,
    /// so "Documents +3.2 GB" doesn't sit next to "WeChat Files +3.2 GB".
    public static List<GrowthDelta> Dedupe(List<GrowthDelta> sorted, Func<GrowthDelta, long> value)
        => sorted.Where(a => !sorted.Any(b => !ReferenceEquals(a, b) && IsUnder(b.Path, a.Path) && value(b) >= value(a) * 0.7)).ToList();

    public static bool IsUnder(string child, string parent)
    {
        var p = parent.TrimEnd('\\') + "\\";
        return child.Length > p.Length && child.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string SelfCheck()
    {
        void Expect(string what, bool ok) { if (!ok) throw new Exception("GROWTH " + what); }
        var now = new DateTime(2026, 3, 10, 12, 0, 0);
        const long G = 1L << 30, M = 1L << 20;
        GrowthSnapshot S(double hoursAgo, params (string p, long s)[] e) => new() { Time = now.AddHours(-hoursAgo), Entries = e.Select(x => new GrowthEntry { Path = x.p, Size = x.s }).ToList() };
        var cur = S(0, (@"C:\U\Documents", 23 * G), (@"C:\U\Documents\WeChat Files", 13 * G), (@"C:\U\Downloads", 6 * G), (@"C:\U\New", 300 * M), (@"C:\U\Same", 2 * G));
        var snaps = new List<GrowthSnapshot>
        {
            S(24 * 8, (@"C:\U\Documents", 15 * G), (@"C:\U\Documents\WeChat Files", 5 * G), (@"C:\U\Downloads", 2 * G)),
            S(30, (@"C:\U\Documents", 19 * G)),
            S(23, (@"C:\U\Documents", 20 * G), (@"C:\U\Documents\WeChat Files", 10 * G), (@"C:\U\Downloads", 5 * G), (@"C:\U\Same", 2 * G), (@"C:\U\Gone", G)),
            S(11, (@"C:\U\Documents", 22 * G)),
            cur,
        };
        var day = Baseline(snaps, cur, TimeSpan.FromDays(1));
        Expect("closest to 24 h ago", day == snaps[2]);
        Expect("closest to 7 d ago", Baseline(snaps, cur, TimeSpan.FromDays(7)) == snaps[0]);
        Expect("needs 12 h of history", Baseline(new List<GrowthSnapshot> { snaps[3], cur }, cur, TimeSpan.FromDays(1)) is null);
        var d = Diff(cur, day!);
        Expect("sorted deltas", d.Select(x => x.Path).SequenceEqual(new[] { @"C:\U\Documents\WeChat Files", @"C:\U\Downloads", @"C:\U\New" }));
        Expect("delta values", d[0].Delta == 3 * G && d[1].Delta == G && d[2].Delta == 300 * M);
        Expect("parent folded into child", d.All(x => x.Path != @"C:\U\Documents"));
        var hist = new List<FreePoint> { new() { T = now.AddHours(-26), Free = 60 * G }, new() { T = now.AddHours(-24.5), Free = 58 * G }, new() { T = now.AddHours(-1), Free = 52 * G } };
        Expect("free change 24 h", FreeChange(hist, 51 * G, now, TimeSpan.FromDays(1)) == -7 * G);
        Expect("free change 7 d needs history", FreeChange(hist, 51 * G, now, TimeSpan.FromDays(7)) is null);
        return $"GROWTH math ok: 24 h baseline {day!.Time:MM-dd HH:mm}, top {Path.GetFileName(d[0].Path)} +{Fmt.Bytes(d[0].Delta)}";
    }

    // ---- names

    public static string FriendlyName(string path) => GrowthNames.Name(path, Here);
}

/// Walks the system drive once and records the sizes of the watch points.
public static class GrowthScanner
{
    public sealed record Places(string Drive, string Home, string Local, string Roaming, string Packages, string ProgramData, string Temp, string Windows);

    static Places? _current;
    public static Places Current => _current ??= Find();

    static Places Find()
    {
        static string N(string p) => Native2.LongPath(p).TrimEnd('\\');
        var local = N(Paths.LocalAppData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new Places(Paths.SystemDrive, N(Paths.Home), local, N(Paths.RoamingAppData), Path.Combine(local, "Packages"),
                          N(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)), N(Path.GetTempPath()),
                          N(windows.Length > 0 ? windows : Path.Combine(Paths.SystemDrive, "Windows")));
    }

    /// Specific deep folders worth their own line (chat apps, browser profiles, caches, system spots).
    public static IEnumerable<string> ExtraPoints(Places p)
    {
        foreach (var rel in new[] { @"Documents\WeChat Files", @"Documents\xwechat_files", @"xwechat_files", @"Documents\Tencent Files", @"Documents\WXWork",
                                    @"Downloads\Telegram Desktop", @".nuget\packages" })
            yield return Path.Combine(p.Home, rel);
        foreach (var rel in new[] { @"Tencent\WeChat", @"Tencent\xwechat", @"Tencent\QQ", @"Tencent\QQNT", @"Telegram Desktop\tdata" })
            yield return Path.Combine(p.Roaming, rel);
        foreach (var rel in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"pip\cache", @"Docker\wsl", @"Microsoft\Teams" })
            yield return Path.Combine(p.Local, rel);
        foreach (var rel in new[] { @"SoftwareDistribution\Download", "Temp", "Installer", "Logs" })
            yield return Path.Combine(p.Windows, rel);
    }

    public static GrowthSnapshot Run(Places p, Action<string> progress)
    {
        var sw = Stopwatch.StartNew();
        var drive = p.Drive;
        var parents = new HashSet<string>(new[] { drive, p.Home, p.Local, p.Roaming, p.Packages, p.ProgramData, p.Temp }, StringComparer.OrdinalIgnoreCase);
        var extra = new HashSet<string>(ExtraPoints(p), StringComparer.OrdinalIgnoreCase);
        // WinSxS shares most of its files with System32 through hard links, so adding it up would
        // double-count, and it is slow to walk. The Recycle Bin is measured through the shell API.
        var skip = new HashSet<string>(new[] { Path.Combine(p.Windows, "WinSxS"), Path.Combine(drive, "$Recycle.Bin") }, StringComparer.OrdinalIgnoreCase);
        var w = new Walker(parents, extra, skip, new HashSet<string>(new[] { drive, p.Home }, StringComparer.OrdinalIgnoreCase), progress);
        long total = w.Walk(drive);
        // Profile folders kept on another drive still get their watch points.
        foreach (var root in parents.Where(r => !DiskWatch.IsUnder(r, drive) && !r.Equals(drive, StringComparison.OrdinalIgnoreCase)))
            if (!w.Sizes.ContainsKey(root) && Directory.Exists(root)) w.Sizes[root] = w.Walk(root);

        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        long recycle = Native.SHQueryRecycleBin(drive, ref info) == 0 ? info.i64Size : 0;
        var recyclePath = Path.Combine(drive, "$Recycle.Bin");

        var di = new DriveInfo(drive);
        var snap = new GrowthSnapshot { Time = DateTime.Now, Drive = drive, Total = di.TotalSize, Free = di.TotalFreeSpace, Denied = w.Denied.Count };
        foreach (var (path, size) in w.Sizes)
            if (string.Equals(Path.GetDirectoryName(path), drive, StringComparison.OrdinalIgnoreCase)) snap.Roots[path] = size;
        snap.Roots[recyclePath] = recycle;
        snap.Accounted = total + recycle;
        snap.Profile = w.Sizes.GetValueOrDefault(p.Home);

        long? Spot(string path)
        {
            if (w.Denied.Contains(path) || w.Denied.Any(d => DiskWatch.IsUnder(path, d))) return null;
            return w.Sizes.TryGetValue(path, out var s) ? s : 0;
        }
        snap.Spots["update"] = Spot(Path.Combine(p.Windows, @"SoftwareDistribution\Download"));
        snap.Spots["wintemp"] = Spot(Path.Combine(p.Windows, "Temp"));
        snap.Spots["installer"] = Spot(Path.Combine(p.Windows, "Installer"));
        snap.Spots["recycle"] = recycle;
        foreach (var f in new[] { "pagefile", "hiberfil", "swapfile" })
            if (w.Sizes.TryGetValue(Path.Combine(drive, f + ".sys"), out var s)) snap.Spots[f] = s;

        bool Container(string path) => parents.Any(r => r.Equals(path, StringComparison.OrdinalIgnoreCase) || DiskWatch.IsUnder(r, path));
        snap.Entries = w.Sizes.Where(kv => kv.Value >= DiskWatch.MinEntry && !Container(kv.Key))
                              .Select(kv => new GrowthEntry { Path = kv.Key, Size = kv.Value }).ToList();
        if (recycle >= DiskWatch.MinEntry) snap.Entries.Add(new GrowthEntry { Path = recyclePath, Size = recycle });
        snap.Entries.Sort((a, b) => b.Size.CompareTo(a.Size));
        snap.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        return snap;
    }

    sealed class Walker
    {
        // OneDrive "online-only" files report their full size but take no space here.
        const FileAttributes Cloud = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
        static readonly EnumerationOptions Opts = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        readonly HashSet<string> _parents, _extra, _skip, _report;
        readonly Action<string> _progress;
        public readonly Dictionary<string, long> Sizes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase);

        public Walker(HashSet<string> parents, HashSet<string> extra, HashSet<string> skip, HashSet<string> report, Action<string> progress)
        { _parents = parents; _extra = extra; _skip = skip; _report = report; _progress = progress; }

        public long Walk(string dir)
        {
            long total = 0;
            bool record = _parents.Contains(dir.Length > 3 ? dir.TrimEnd('\\') : dir);
            bool report = _report.Contains(dir);
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Opts))
                {
                    FileAttributes a;
                    try { a = e.Attributes; } catch { continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;   // junctions, symlinks, dedup'd mounts
                    if (e is FileInfo f)
                    {
                        if ((a & Cloud) != 0) continue;
                        long len;
                        try { len = f.Length; } catch { continue; }
                        total += len;
                        if (record || (len >= DiskWatch.MinEntry && (f.Extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase))))
                            Sizes[f.FullName] = len;
                    }
                    else
                    {
                        var path = e.FullName;
                        if (_skip.Contains(path)) continue;
                        if (report) _progress(path);
                        long s = Walk(path);
                        total += s;
                        if (record || _extra.Contains(path)) Sizes[path] = s;
                    }
                }
            }
            catch (UnauthorizedAccessException) { Denied.Add(dir); }
            catch (System.Security.SecurityException) { Denied.Add(dir); }
            catch { }
            return total;
        }
    }
}

/// Plain names for well-known folders; anything else shows its folder name.
static class GrowthNames
{
    public static string Name(string path, GrowthScanner.Places p)
    {
        var full = path.TrimEnd('\\');
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full) ?? "";
        bool Is(string root, string rel) => full.Equals(Path.Combine(root, rel), StringComparison.OrdinalIgnoreCase);
        bool ParentIs(string root) => parent.Equals(root, StringComparison.OrdinalIgnoreCase);
        bool Under(string root) => DiskWatch.IsUnder(full, root);
        bool NameIs(params string[] names) => names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));

        // chat apps
        if (Is(p.Home, @"Documents\WeChat Files") || Is(p.Home, @"Documents\xwechat_files") || Is(p.Home, "xwechat_files")) return L("微信聊天文件", "WeChat files");
        if (Is(p.Roaming, @"Tencent\WeChat") || Is(p.Roaming, @"Tencent\xwechat")) return L("微信程序数据", "WeChat app data");
        if (Is(p.Home, @"Documents\Tencent Files")) return L("QQ 聊天文件", "QQ files");
        if (Is(p.Roaming, @"Tencent\QQ") || Is(p.Roaming, @"Tencent\QQNT")) return L("QQ 程序数据", "QQ app data");
        if (Is(p.Roaming, "Tencent")) return L("腾讯（微信 / QQ）", "Tencent (WeChat / QQ)");
        if (Is(p.Home, @"Documents\WXWork")) return L("企业微信文件", "WeCom files");
        if (Is(p.Home, @"Downloads\Telegram Desktop") || Is(p.Roaming, @"Telegram Desktop\tdata") || (ParentIs(p.Roaming) && NameIs("Telegram Desktop"))) return "Telegram";
        if ((ParentIs(p.Roaming) || ParentIs(p.Local)) && NameIs("DingTalk")) return L("钉钉", "DingTalk");
        if ((ParentIs(p.Roaming) || ParentIs(p.Local)) && NameIs("Feishu", "LarkShell", "Lark")) return L("飞书", "Feishu / Lark");
        if ((ParentIs(p.Roaming) || ParentIs(p.Local)) && NameIs("discord")) return "Discord";
        if ((ParentIs(p.Roaming) || ParentIs(p.Local)) && NameIs("Slack")) return "Slack";
        if (Is(p.Local, @"Microsoft\Teams") || ((ParentIs(p.Roaming) || ParentIs(p.Local)) && NameIs("Teams"))) return "Microsoft Teams";
        // browsers
        if (Is(p.Local, @"Google\Chrome\User Data")) return L("Chrome 浏览器数据", "Chrome profiles");
        if (Is(p.Local, "Google")) return "Google (Chrome)";
        if (Is(p.Local, @"Microsoft\Edge\User Data")) return L("Edge 浏览器数据", "Edge profiles");
        if (ParentIs(p.Local) && NameIs("Mozilla")) return "Firefox";
        // dev tools
        if (name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
        {
            if (Under(Path.Combine(p.Local, "Docker"))) return L("Docker 虚拟磁盘", "Docker virtual disk");
            if (Under(p.Packages) || Under(Path.Combine(p.Local, "wsl"))) return L("WSL Linux 虚拟磁盘", "WSL Linux virtual disk");
            return L($"虚拟磁盘 {name}", $"Virtual disk {name}");
        }
        if (Is(p.Local, "Docker") || Is(p.Local, @"Docker\wsl")) return L("Docker（WSL 磁盘）", "Docker (WSL disk)");
        if (Is(p.Home, ".docker")) return "Docker";
        if (Is(p.Local, "wsl")) return L("WSL Linux 磁盘", "WSL Linux disks");
        if (ParentIs(p.Packages) && name.StartsWith("CanonicalGroupLimited.", StringComparison.OrdinalIgnoreCase)) return "WSL Ubuntu";
        if (ParentIs(p.Packages) && name.StartsWith("MicrosoftCorporationII.WindowsSubsystemForLinux", StringComparison.OrdinalIgnoreCase)) return "WSL";
        if (Is(p.Local, "npm-cache")) return L("npm 缓存", "npm cache");
        if (Is(p.Roaming, "npm")) return L("npm 全局包", "npm global packages");
        if (Is(p.Local, @"pip\cache") || Is(p.Local, "pip")) return L("pip 缓存", "pip cache");
        if (Is(p.Home, @".nuget\packages") || Is(p.Home, ".nuget")) return L("NuGet 包缓存", "NuGet packages");
        if (Is(p.Local, "NuGet")) return L("NuGet 缓存", "NuGet cache");
        if (Is(p.Local, "Yarn")) return L("Yarn 缓存", "Yarn cache");
        if (Is(p.Local, "pnpm") || Is(p.Local, "pnpm-cache")) return L("pnpm 缓存", "pnpm store");
        if (Is(p.Home, ".gradle")) return L("Gradle 缓存", "Gradle cache");
        if (Is(p.Home, ".m2")) return L("Maven 仓库", "Maven repository");
        if (Is(p.Home, ".cargo") || Is(p.Home, ".rustup")) return "Rust (" + name + ")";
        if (Is(p.Home, ".vscode")) return L("VS Code 扩展", "VS Code extensions");
        if (Is(p.Home, "anaconda3") || Is(p.Home, "miniconda3") || Is(p.Home, ".conda")) return "Conda";
        if (Is(p.Local, "JetBrains")) return L("JetBrains 缓存", "JetBrains caches");
        // system spots
        if (Is(p.Windows, @"SoftwareDistribution\Download")) return L("Windows 更新下载", "Windows Update downloads");
        if (Is(p.Windows, "Temp")) return L("Windows 临时文件", "Windows temp files");
        if (Is(p.Windows, "Installer")) return L("已安装程序的安装包缓存", "Windows Installer cache");
        if (Is(p.Windows, "Logs")) return L("Windows 日志", "Windows logs");
        if (Is(p.Drive, "$Recycle.Bin")) return L("回收站", "Recycle Bin");
        if (Is(p.Drive, "pagefile.sys")) return L("页面文件（虚拟内存）", "Page file (virtual memory)");
        if (Is(p.Drive, "hiberfil.sys")) return L("休眠文件", "Hibernation file");
        if (Is(p.Drive, "swapfile.sys")) return L("应用交换文件", "Swap file");
        if (Is(p.Drive, "Windows")) return L("Windows 系统", "Windows");
        if (Is(p.Drive, "Program Files")) return L("已安装的程序", "Program Files");
        if (Is(p.Drive, "Program Files (x86)")) return L("已安装的程序（32 位）", "Program Files (x86)");
        if (full.Equals(p.Temp, StringComparison.OrdinalIgnoreCase) || Is(p.Local, "Temp")) return L("临时文件", "Temp files");
        if (ParentIs(p.Temp)) return L($"临时文件 · {name}", $"Temp · {name}");
        // profile folders
        if (ParentIs(p.Home))
        {
            var known = name.ToLowerInvariant() switch
            {
                "desktop" => L("桌面", "Desktop"), "documents" => L("文档", "Documents"), "downloads" => L("下载", "Downloads"),
                "pictures" => L("图片", "Pictures"), "videos" => L("视频", "Videos"), "music" => L("音乐", "Music"),
                _ => null,
            };
            if (known is not null) return known;
            if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        }
        // Store apps: "Microsoft.WindowsTerminal_8wekyb3d8bbwe" → "Microsoft.WindowsTerminal"
        if (ParentIs(p.Packages) && name.LastIndexOf('_') is var u and > 0) return name[..u];
        return name;
    }
}

/// Made-up history and snapshots for `--demo` / CI screenshots.
static class GrowthDemo
{
    public static readonly GrowthScanner.Places Places = new(@"C:\", @"C:\Users\Alex", @"C:\Users\Alex\AppData\Local", @"C:\Users\Alex\AppData\Roaming",
        @"C:\Users\Alex\AppData\Local\Packages", @"C:\ProgramData", @"C:\Users\Alex\AppData\Local\Temp", @"C:\Windows");

    const long G = 1L << 30, M = 1L << 20;

    public static (List<FreePoint>, List<GrowthSnapshot>) Make(DateTime now)
    {
        long total = 476 * G;
        var rnd = new Random(7);
        // Free space drifts down ~0.9 GB a day, a cleanup 12 days ago won 8 GB back, and the last day lost 4.3 GB more.
        double FreeAt(double d) => 58.0 * G + 0.9 * G * d + (d < 12 ? 8.0 * G : 0) - (d < 1 ? (1 - d) * 4.3 * G : 0);
        var hist = new List<FreePoint>();
        for (int h = 30 * 24; h >= 0; h--)
        {
            double d = h / 24.0;
            if (d is > 19 and < 21) continue;   // laptop was off for two days
            hist.Add(new FreePoint { T = now.AddHours(-h), Free = (long)(FreeAt(d) + (rnd.NextDouble() - 0.5) * 0.3 * G), Total = total });
        }
        hist[^1].Free = (long)FreeAt(0);

        var p = Places;
        string H(string rel) => Path.Combine(p.Home, rel);
        string Lo(string rel) => Path.Combine(p.Local, rel);
        // path, size now, growth over 24 h, growth over 7 days
        var items = new (string path, double now, double day, double week)[]
        {
            (H(@"Documents\WeChat Files"), 38.5, 3.2, 6.9),
            (H("Documents"), 52.0, 3.25, 7.1),
            (H("Downloads"), 21.4, 1.1, 2.6),
            (Path.Combine(p.Windows, @"SoftwareDistribution\Download"), 4.8, 2.4, 4.6),
            (Path.Combine(p.Drive, "Windows"), 27.5, 2.4, 4.7),
            (Lo(@"Docker\wsl\disk\docker_data.vhdx"), 18.2, 0.4, 2.1),
            (Lo("Docker"), 18.6, 0.4, 2.1),
            (p.Temp, 3.6, 0.6, 1.4),
            (Lo(@"Google\Chrome\User Data"), 6.1, 0.15, 0.7),
            (Lo("Google"), 6.3, 0.15, 0.7),
            (Lo("npm-cache"), 2.9, 0, 0.8),
            (Path.Combine(p.Roaming, @"Tencent\WeChat"), 1.2, 0.1, 0.3),
            (Path.Combine(p.Roaming, "Tencent"), 1.4, 0.1, 0.3),
            (H("Pictures"), 14.8, 0, 0.2),
            (H("Videos"), 22.6, 0, 0),
            (H("Desktop"), 3.1, 0.02, 0.1),
            (H(@".nuget\packages"), 4.4, 0, 0.3),
            (H(".nuget"), 4.5, 0, 0.3),
            (Path.Combine(p.Drive, "Program Files"), 31.0, 0, 1.2),
            (Path.Combine(p.Drive, "Program Files (x86)"), 9.4, 0, 0),
            (Path.Combine(p.ProgramData, "Microsoft"), 2.1, 0.05, 0.2),
            (Path.Combine(p.Drive, "pagefile.sys"), 12.0, 0, 0),
            (Path.Combine(p.Drive, "hiberfil.sys"), 6.4, 0, 0),
            (Path.Combine(p.Drive, "$Recycle.Bin"), 2.3, 0.3, 0.9),
            (Path.Combine(p.Windows, "Installer"), 3.9, 0, 0.1),
        };
        long At(double now0, double day, double week, double d) =>
            (long)((d <= 1 ? now0 - day * d : now0 - day - (week - day) * Math.Min(d - 1, 6) / 6) * G);

        var snaps = new List<GrowthSnapshot>();
        foreach (var d in new[] { 8.0, 7.0, 6.0, 5.0, 4.0, 3.0, 2.0, 1.0, 0.0 })
        {
            var t = now.AddDays(-d).AddMinutes(d == 0 ? -20 : 0);
            var s = new GrowthSnapshot { Time = t, Drive = p.Drive, Total = total, Free = (long)FreeAt(d), Seconds = 96, Denied = 41, Profile = (long)(285 * G - (8 - d) * 0.5 * G) };
            foreach (var (path, n, day, week) in items) s.Entries.Add(new GrowthEntry { Path = path, Size = At(n, day, week, d) });
            s.Roots[Path.Combine(p.Drive, "Users")] = s.Profile + 5 * G;
            s.Roots[Path.Combine(p.Drive, "Program Files")] = At(31.0, 0, 1.2, d);
            s.Roots[Path.Combine(p.Drive, "Program Files (x86)")] = (long)(9.4 * G);
            s.Roots[Path.Combine(p.Drive, "Windows")] = At(27.5, 2.4, 4.7, d);
            s.Roots[p.ProgramData] = (long)(6.2 * G);
            s.Roots[Path.Combine(p.Drive, "pagefile.sys")] = 12 * G;
            s.Roots[Path.Combine(p.Drive, "hiberfil.sys")] = (long)(6.4 * G);
            s.Roots[Path.Combine(p.Drive, "swapfile.sys")] = 256 * M;
            s.Roots[Path.Combine(p.Drive, "$Recycle.Bin")] = At(2.3, 0.3, 0.9, d);
            s.Accounted = s.Roots.Values.Sum();
            s.Spots["update"] = At(4.8, 2.4, 4.6, d);
            s.Spots["wintemp"] = null;
            s.Spots["installer"] = At(3.9, 0, 0.1, d);
            s.Spots["recycle"] = s.Roots[Path.Combine(p.Drive, "$Recycle.Bin")];
            s.Spots["pagefile"] = 12 * G;
            s.Spots["hiberfil"] = (long)(6.4 * G);
            s.Spots["swapfile"] = 256 * M;
            s.Entries.Sort((a, b) => b.Size.CompareTo(a.Size));
            snaps.Add(s);
        }
        return (hist, snaps);
    }
}

internal static class Native2
{
    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000, THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    public static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetLongPathName(string shortPath, StringBuilder? longPath, uint size);

    /// %TEMP% is often an 8.3 path ("C:\Users\ALEXAN~1\…"); expand it so it matches the walked paths.
    public static string LongPath(string p)
    {
        if (!p.Contains('~')) return p;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetLongPathName(p, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : p;
        }
        catch { return p; }
    }
}
