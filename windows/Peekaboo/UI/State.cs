using System.Windows;

namespace Peekaboo;

public enum Page { Overview, Disk, Projects, Processes, Cleanup, Typing, Screenshot, Battery, Network, Duplicates, Uninstall, Clipboard, WindowSnap, Growth, Settings }

static class UiThread
{
    public static void Run(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);
}

public sealed class DiskModel
{
    public static DiskModel Shared { get; } = new();
    public DiskNode? Root, Current;
    public bool Scanning;
    public int Files; public long Bytes;
    public List<LargeFile> LargeFiles = new();
    public string ScanRoot = Paths.Home;
    public DateTime? LastScan;
    public event Action? Changed;
    DiskScanEngine? _engine;

    public void Scan(string? root = null)
    {
        if (root is not null) ScanRoot = root;
        _engine?.Cancel();
        var engine = new DiskScanEngine((long)Settings.Shared.LargeFileMB << 20);
        _engine = engine;
        Scanning = true; Files = 0; Bytes = 0; Changed?.Invoke();
        var target = ScanRoot;
        Task.Run(() =>
        {
            var node = engine.Scan(target);
            UiThread.Run(() =>
            {
                if (_engine != engine) return;
                Root = Current = node; LargeFiles = engine.LargeFiles; Scanning = false; LastScan = DateTime.Now;
                Changed?.Invoke();
            });
        });
        Task.Run(async () =>
        {
            while (Scanning && _engine == engine)
            {
                await Task.Delay(300);
                var (f, b) = engine.Progress;
                UiThread.Run(() => { Files = f; Bytes = b; Changed?.Invoke(); });
            }
        });
    }

    public void Cancel() { _engine?.Cancel(); Scanning = false; Changed?.Invoke(); }
    public void Open(DiskNode n) { if (n.IsDirectory && n.Children.Count > 0) { Current = n; Changed?.Invoke(); } }
    public void Up() { if (Current?.Parent is { } p) { Current = p; Changed?.Invoke(); } }
    public void Removed(DiskNode n) { n.Parent?.Remove(n); LargeFiles.RemoveAll(f => f.Path.StartsWith(n.Path, StringComparison.OrdinalIgnoreCase)); Changed?.Invoke(); }
}

public sealed class ProjectModel
{
    public static ProjectModel Shared { get; } = new();
    public List<Project> Projects = new();
    public bool Scanning;
    public string Status = "";
    public DateTime? LastScan;
    public event Action? Changed;
    ProjectScanEngine? _engine;

    public long TotalArtifacts => Projects.Sum(p => p.ArtifactSize);

    public void Scan()
    {
        _engine?.Cancel();
        var engine = new ProjectScanEngine();
        _engine = engine;
        Scanning = true; Status = L("查找项目…", "Finding projects…"); Changed?.Invoke();
        var roots = Settings.Shared.ProjectRootPaths.ToList();
        Task.Run(() =>
        {
            var found = engine.Find(roots);
            UiThread.Run(() => { Status = L($"找到 {found.Count} 个项目，正在计算大小…", $"Found {found.Count} projects, calculating sizes…"); Changed?.Invoke(); });
            var results = new Project[found.Count];
            Parallel.For(0, found.Count, i => results[i] = engine.Measure(found[i].path, found[i].tags, found[i].root));
            UiThread.Run(() =>
            {
                if (_engine != engine) return;
                Projects = results.OrderByDescending(p => p.TotalSize).ToList();
                Scanning = false; Status = ""; LastScan = DateTime.Now;
                Changed?.Invoke();
            });
        });
    }

    public void Removed(HashSet<string> paths)
    {
        foreach (var p in Projects) p.Artifacts.RemoveAll(a => paths.Contains(a.Path));
        Changed?.Invoke();
    }
}

public sealed class CleanupModel
{
    public static CleanupModel Shared { get; } = new();
    public Dictionary<CleanupCategory, List<CleanupItem>> Items = new();
    public HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    public bool Scanning, Working;
    public string? LastResult;
    public long RecycleSize;
    public List<CleanupRecord> Records = CleanupExecutor.Records();
    public DateTime? LastScan;
    public event Action? Changed;

    public IEnumerable<CleanupItem> All => Enum.GetValues<CleanupCategory>().SelectMany(c => Items.TryGetValue(c, out var l) ? l : new());
    public IEnumerable<CleanupItem> SelectedItems => All.Where(i => Selected.Contains(i.Path));
    public long SelectedSize => SelectedItems.Sum(i => i.Size);
    public long Total(CleanupCategory c) => Items.TryGetValue(c, out var l) ? l.Sum(i => i.Size) : 0;

    public void Scan()
    {
        if (Scanning) return;
        Scanning = true; Changed?.Invoke();
        int days = Settings.Shared.DownloadsOldDays;
        Task.Run(() =>
        {
            var running = CleanupScanner.RunningNames();
            var dev = Task.Run(CleanupScanner.DevCacheItems);
            var app = Task.Run(() => CleanupScanner.AppCacheItems(running));
            var temp = Task.Run(CleanupScanner.TempAndLogItems);
            var dl = Task.Run(() => CleanupScanner.DownloadItems(days));
            Task.WaitAll(dev, app, temp, dl);
            var (rb, _) = CleanupScanner.RecycleBin();
            UiThread.Run(() =>
            {
                Items[CleanupCategory.DevCache] = dev.Result.OrderByDescending(i => i.Size).ToList();
                Items[CleanupCategory.AppCache] = app.Result.OrderByDescending(i => i.Size).ToList();
                Items[CleanupCategory.TempAndLogs] = temp.Result.OrderByDescending(i => i.Size).ToList();
                Items[CleanupCategory.Downloads] = dl.Result.OrderByDescending(i => i.Size).ToList();
                RecycleSize = rb;
                SyncFromOthers();
                Selected = new HashSet<string>(All.Where(i => i.Recommended).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                Scanning = false; LastScan = DateTime.Now;
                Changed?.Invoke();
            });
        });
        if (ProjectModel.Shared.Projects.Count == 0 && !ProjectModel.Shared.Scanning) ProjectModel.Shared.Scan();
    }

    public void SyncFromOthers()
    {
        int stale = Settings.Shared.StaleProjectDays;
        var arts = new List<CleanupItem>();
        foreach (var p in ProjectModel.Shared.Projects)
        {
            bool isStale = Fmt.DaysAgo(p.LastModified) >= stale;
            foreach (var a in p.Artifacts.Where(a => a.Size > 1 << 20))
            {
                var note = a.Safe ? a.KindText : a.KindText + L("（未在 .gitignore 中，请确认）", " (not in .gitignore, please check)");
                arts.Add(new CleanupItem(a.Path, $"{p.DisplayName} / {Path.GetFileName(a.Path)}", L($"{note} · 项目 {Fmt.Relative(p.LastModified)}修改", $"{note} · project modified {Fmt.Relative(p.LastModified)}"),
                                         a.Size, CleanupCategory.ProjectArtifacts, a.Safe && isStale));
            }
        }
        Items[CleanupCategory.ProjectArtifacts] = arts.OrderByDescending(i => i.Size).ToList();
        Items[CleanupCategory.LargeFiles] = DiskModel.Shared.LargeFiles.Take(100)
            .Select(f => new CleanupItem(f.Path, Path.GetFileName(f.Path), L($"{Fmt.Tilde(Path.GetDirectoryName(f.Path) ?? "")} · {Fmt.Relative(f.Modified)}修改", $"{Fmt.Tilde(Path.GetDirectoryName(f.Path) ?? "")} · modified {Fmt.Relative(f.Modified)}"),
                                         f.Size, CleanupCategory.LargeFiles, false)).ToList();
        Changed?.Invoke();
    }

    public void Perform(List<CleanupItem> list, bool permanent, Action<CleanupExecutor.Outcome> done)
    {
        Working = true; Changed?.Invoke();
        Task.Run(() =>
        {
            var o = CleanupExecutor.Run(list, permanent);
            var (rb, _) = CleanupScanner.RecycleBin();
            UiThread.Run(() =>
            {
                var failed = new HashSet<string>(o.Failures.Select(f => f.path).Concat(o.Skipped), StringComparer.OrdinalIgnoreCase);
                var ok = list.Where(i => !failed.Contains(i.Path)).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var l in Items.Values) l.RemoveAll(i => ok.Contains(i.Path));
                Selected.ExceptWith(ok);
                RecycleSize = rb; Working = false;
                Records = CleanupExecutor.Records();
                LastResult = permanent ? L($"已删除 {o.Count} 项，释放 {Fmt.Bytes(o.Freed)}", $"Deleted {o.Count} items, freed {Fmt.Bytes(o.Freed)}")
                                       : L($"已把 {o.Count} 项（{Fmt.Bytes(o.Freed)}）移到回收站，清空回收站后才会真正释放空间", $"Moved {o.Count} items ({Fmt.Bytes(o.Freed)}) to the Recycle Bin. Empty it to actually free the space");
                ProjectModel.Shared.Removed(ok);
                Changed?.Invoke();
                done(o);
            });
        });
    }

    public void EmptyRecycleBin()
    {
        Working = true; Changed?.Invoke();
        Task.Run(() =>
        {
            long freed = CleanupExecutor.EmptyRecycleBin();
            var (rb, _) = CleanupScanner.RecycleBin();
            UiThread.Run(() =>
            {
                RecycleSize = rb; Working = false; Records = CleanupExecutor.Records();
                LastResult = L($"回收站已清空，释放 {Fmt.Bytes(freed)}", $"Recycle Bin emptied, freed {Fmt.Bytes(freed)}");
                Changed?.Invoke();
            });
        });
    }
}
