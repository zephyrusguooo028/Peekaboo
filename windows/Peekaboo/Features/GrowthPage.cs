using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static Peekaboo.UIX;

namespace Peekaboo;

/// "Disk growth": free space over time, which watch points grew, what Windows itself holds,
/// and how much of the drive Peekaboo can't see. Read-only apart from "Show in Explorer".
public sealed class GrowthPage : PageView
{
    static bool _week;
    TextBlock? _status;

    static readonly Color Grow = Color.FromRgb(240, 128, 40);

    public static string? Badge => DiskWatch.Change24h is { } c && Math.Abs(c) >= 1L << 30 ? (c < 0 ? "−" : "+") + Fmt.DiskShort(Math.Abs(c)) : null;
    public static bool BadgeAlert => DiskWatch.Change24h is { } c && -c >= DiskWatch.DropAlertBytes;

    public override void Shown() { DiskWatch.Changed += Build; DiskWatch.Progress += ShowProgress; Build(); }
    public override void Hidden() { DiskWatch.Changed -= Build; DiskWatch.Progress -= ShowProgress; }

    void ShowProgress() { if (_status is not null) _status.Text = StatusText(); }

    static string StatusText()
    {
        if (DiskWatch.Scanning)
            return DiskWatch.ScanStatus.Length > 0 ? L($"正在测量 {Short(DiskWatch.ScanStatus)} …（后台低优先级运行，不影响使用）", $"Measuring {Short(DiskWatch.ScanStatus)} … (runs in the background at low priority)")
                                                   : L("正在测量…", "Measuring…");
        if (DiskWatch.Latest is { } s)
            return L($"上次测量 {Fmt.Relative(s.Time)}，用时 {Seconds(s.Seconds)} · 每天自动测量一次，每小时记录一次可用空间",
                     $"Last measured {Fmt.Relative(s.Time)}, took {Seconds(s.Seconds)} · measured daily, free space recorded hourly");
        return L("每小时记录一次可用空间，每天测量一次常见的会变大的位置", "Free space is recorded hourly; the places that tend to grow are measured daily");
    }

    static string Seconds(double s) => s >= 90 ? L($"{s / 60:0.#} 分钟", $"{s / 60:0.#} min") : L($"{s:0} 秒", $"{s:0} s");

    void Build()
    {
        var free = DiskWatch.CurrentFree;
        var drive = DiskWatch.Latest?.Drive.TrimEnd('\\') ?? SystemMonitor.Shared.Disk.Name;
        var c1 = DiskWatch.Change(TimeSpan.FromDays(1)); var c7 = DiskWatch.Change(TimeSpan.FromDays(7));
        var subtitle = L($"{drive} 可用 {Fmt.Bytes(free)} · 24 小时 {Signed(c1)} · 7 天 {Signed(c7)}", $"{drive} {Fmt.Bytes(free)} free · 24 h {Signed(c1)} · 7 days {Signed(c7)}");
        var scan = Btn(DiskWatch.Scanning ? L("正在扫描…", "Scanning…") : L("立即扫描", "Scan now"), DiskWatch.Scan, primary: !DiskWatch.Scanning, glyph: "");
        scan.IsEnabled = !DiskWatch.Scanning && !DiskWatch.Demo;
        _status = T(StatusText(), 12, color: Theme.Secondary);

        var parts = new List<UIElement> { PageHeader(L("空间变化", "Disk growth"), subtitle, scan), _status, FreeCard(), GrowingCard(), LargestCard(), SystemCard(), UnseenCard() };
        Content = V(16, parts.ToArray());
    }

    static string Signed(long? c) => c is not { } v ? "—" : (v < 0 ? "−" : "+") + Fmt.Bytes(Math.Abs(v));

    // ---- free space chart

    UIElement FreeCard()
    {
        var now = DateTime.Now;
        var pts = DiskWatch.History.Where(p => (now - p.T).TotalDays <= 30).ToList();
        UIElement body = pts.Count < 2
            ? Note(L("正在记录，每小时记一次，明天这里就有曲线了。", "Recording once an hour; the line appears here after a few readings."))
            : FreeChart(pts, now);
        return Card(L("可用空间 · 最近 30 天", "Free space · last 30 days"), "", body);
    }

    static FrameworkElement FreeChart(List<FreePoint> pts, DateTime now)
    {
        const double height = 190, right = 64, bottom = 20;
        var canvas = new Canvas { Height = height, ClipToBounds = false };
        var start = pts[0].T;
        double lo = pts.Min(p => p.Free), hi = pts.Max(p => p.Free);
        double pad = Math.Max((hi - lo) * 0.15, 1L << 30);
        lo = Math.Max(0, lo - pad); hi += pad;
        var c = Theme.DiskC;
        void Draw(double W)
        {
            canvas.Children.Clear();
            double w = W - right, h = height - bottom;
            if (w <= 20) return;
            double span = Math.Max(1, (now - start).TotalSeconds);
            Point P(FreePoint p) => new(w * (p.T - start).TotalSeconds / span, h * (1 - (p.Free - lo) / (hi - lo)));
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                double y = h * (1 - f);
                canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Theme.Line, StrokeThickness = 1 });
                var lb = T(Fmt.Bytes(lo + (hi - lo) * f), 10, color: Theme.Tertiary);
                Canvas.SetLeft(lb, w + 6); Canvas.SetTop(lb, y - 7);
                canvas.Children.Add(lb);
            }
            // Break the line where the PC was off for a while instead of drawing a fake slope.
            var runs = new List<List<Point>> { new() };
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0 && (pts[i].T - pts[i - 1].T).TotalHours > 6) runs.Add(new());
                runs[^1].Add(P(pts[i]));
            }
            foreach (var run in runs.Where(r => r.Count > 0))
            {
                if (run.Count == 1) { var dot = new Ellipse { Width = 4, Height = 4, Fill = Theme.B(c) }; Canvas.SetLeft(dot, run[0].X - 2); Canvas.SetTop(dot, run[0].Y - 2); canvas.Children.Add(dot); continue; }
                var area = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(4, c.R, c.G, c.B), 90) };
                area.Points.Add(new Point(run[0].X, h)); foreach (var p in run) area.Points.Add(p); area.Points.Add(new Point(run[^1].X, h));
                canvas.Children.Add(area);
                var line = new Polyline { Stroke = Theme.B(c), StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round };
                foreach (var p in run) line.Points.Add(p);
                canvas.Children.Add(line);
            }
            for (int k = 0; k <= 4; k++)
            {
                var t = start + TimeSpan.FromSeconds(span * k / 4);
                var lb = T(t.ToString(span > 2 * 86400 ? "M/d" : "HH:mm"), 10, color: Theme.Tertiary);
                Canvas.SetLeft(lb, Math.Clamp(w * k / 4 - 14, 0, w - 28)); Canvas.SetTop(lb, h + 4);
                canvas.Children.Add(lb);
            }
        }
        canvas.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        return canvas;
    }

    // ---- growing

    UIElement GrowingCard()
    {
        var toggle = H(2);
        foreach (var (label, week) in new[] { (L("24 小时", "24 h"), false), (L("7 天", "7 days"), true) })
        {
            var b = Btn(label, () => { _week = week; Build(); }, primary: _week == week);
            b.Padding = new Thickness(10, 3, 10, 3);
            toggle.Children.Add(b);
        }
        var latest = DiskWatch.Latest;
        UIElement body;
        if (latest is null)
            body = DiskWatch.Scanning ? Empty("", L("正在进行第一次测量", "Taking the first measurement"), L("测量完成后，这里会先列出目前最大的位置；明天开始就能看到谁在长。", "When it finishes, the largest places show below; from tomorrow you’ll see what grew."))
                                      : Empty("", L("还没有测量", "Not measured yet"), L("点右上角「立即扫描」，或等 Peekaboo 每天自动测量。", "Click “Scan now”, or wait for the daily measurement."));
        else if (DiskWatch.Baseline(DiskWatch.Snapshots, latest, TimeSpan.FromDays(_week ? 7 : 1)) is not { } then)
        {
            var since = DiskWatch.RecordingSince ?? latest.Time;
            body = Empty("", L("需要至少一天的记录", "Needs at least a day of history"), L($"{since:M月d日} 开始记录", $"Recording since {since:MMM d}"));
        }
        else
        {
            var deltas = DiskWatch.Diff(latest, then).Take(15).ToList();
            var cmp = Note(L($"和 {then.Time:M月d日 HH:mm} 的测量相比（只统计 50 MB 以上的位置）", $"Compared with the measurement on {then.Time:MMM d, HH:mm} (places of 50 MB or more)"));
            if (deltas.Count == 0)
                body = V(4, cmp, Empty("", L("没有明显变大的地方", "Nothing grew noticeably"), L("这段时间没有哪个位置变大超过 50 MB。", "No place grew by more than 50 MB in this period.")));
            else
            {
                var rows = V(2, cmp);
                foreach (var d in deltas) rows.Children.Add(EntryRow(d, showDelta: true));
                body = rows;
            }
        }
        return Card(L("最近在长", "Growing"), "", body, toggle);
    }

    UIElement LargestCard()
    {
        if (DiskWatch.Latest is not { } latest) return new Border();
        var rows = V(2);
        foreach (var d in DiskWatch.Largest(latest, 15)) rows.Children.Add(EntryRow(d, showDelta: false));
        return Card(L("目前最大", "Largest now"), "", rows);
    }

    static string Short(string path)
    {
        var home = DiskWatch.Here.Home;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    static FrameworkElement EntryRow(GrowthDelta d, bool showDelta)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showDelta ? 92 : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = V(1, T(DiskWatch.FriendlyName(d.Path), 13, FontWeights.Medium), T(Short(d.Path), 11, color: Theme.Tertiary));
        name.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(name);
        var size = T(Fmt.Bytes(d.Size), 12.5, color: Theme.Secondary);
        size.HorizontalAlignment = HorizontalAlignment.Right; size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 1); g.Children.Add(size);
        if (showDelta)
        {
            var delta = T("+" + Fmt.Bytes(d.Delta), 13, FontWeights.SemiBold, Theme.B(d.Delta >= 1L << 30 ? Theme.DangerC : Grow));
            delta.HorizontalAlignment = HorizontalAlignment.Right; delta.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(delta, 2); g.Children.Add(delta);
        }
        var reveal = RevealButton(d.Path);
        if (reveal is not null) { reveal.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(reveal, 3); g.Children.Add(reveal); }
        return g;
    }

    static Button? RevealButton(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        Action? act = name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ? () => Open("shell:RecycleBinFolder")
            : name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) ? null   // page / hibernation files are hidden system files
            : () => { if (!DiskWatch.Demo) Reveal(path); };
        if (act is null) return null;
        var b = Btn(L("在资源管理器中显示", "Show in Explorer"), act, glyph: "");
        b.Padding = new Thickness(8, 3, 8, 3);
        return b;
    }

    static void Open(string target)
    {
        if (DiskWatch.Demo) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---- system

    UIElement SystemCard()
    {
        var s = DiskWatch.Latest;
        long? Spot(string k) => s is not null && s.Spots.TryGetValue(k, out var v) ? v : null;
        bool Has(string k) => s is not null && s.Spots.ContainsKey(k);
        string Size(string k) => s is null ? "—" : !Has(k) ? L("没有", "None") : Spot(k) is { } v ? Fmt.Bytes(v) : L("需要管理员权限", "Needs admin");

        var rows = V(12);
        void Item(string title, string size, string explain, params Button[] actions)
        {
            var right = H(8, T(size, 13, FontWeights.SemiBold));
            foreach (var a in actions) { a.Padding = new Thickness(8, 3, 8, 3); right.Children.Add(a); }
            rows.Children.Add(V(3, Row(T(title, 13, FontWeights.Medium), right), Note(explain)));
        }
        Item(L("Windows 更新下载", "Windows Update downloads"), Size("update"),
             L("Windows 更新下载的安装包。装完以后通常就没用了，可以用「磁盘清理」或「存储感知」安全地清掉。", "Installer packages Windows Update downloaded. Usually not needed once installed; Disk Cleanup or Storage Sense can remove them safely."),
             Btn(L("磁盘清理", "Disk Cleanup"), () => Open("cleanmgr.exe")));
        long pf = Spot("pagefile") ?? 0, hf = Spot("hiberfil") ?? 0, sf = Spot("swapfile") ?? 0;
        Item(L("页面文件 / 休眠文件", "Page file / hibernation file"), s is null ? "—" : Fmt.Bytes(pf + hf + sf),
             L($"虚拟内存（{Fmt.Bytes(pf)}）和休眠用的文件（{Fmt.Bytes(hf)}），由 Windows 自己管理，大小跟内存有关。不用休眠的话，可以在管理员命令行里运行 powercfg /h off 关掉休眠文件。",
               $"Virtual memory ({Fmt.Bytes(pf)}) and the hibernation file ({Fmt.Bytes(hf)}), managed by Windows and sized by your RAM. If you never hibernate, powercfg /h off in an admin prompt removes the hibernation file."));
        Item(L("回收站", "Recycle Bin"), Size("recycle"),
             L("删掉的文件还在回收站里占着空间，清空后才会真正释放。", "Deleted files keep taking space until the Recycle Bin is emptied."),
             Btn(L("打开回收站", "Open Recycle Bin"), () => Open("shell:RecycleBinFolder")));
        Item(L("安装包缓存（Windows\\Installer）", "Installer cache (Windows\\Installer)"), Size("installer"),
             L("已安装程序修复和卸载时要用的安装包。不要手动删，卸载不用的程序会一起清掉。", "Packages installed programs need for repair and uninstall. Don’t delete by hand; uninstalling programs removes theirs."));
        Item(L("Windows 临时文件", "Windows temp files"), Size("wintemp"),
             L("系统和安装程序留下的临时文件。「存储感知」可以定期清理。", "Temporary files left by Windows and installers. Storage Sense can clean them regularly."));
        Item(L("系统还原点和卷影副本", "Restore points and shadow copies"), L("看不到", "Not visible"),
             L("保存在 System Volume Information 里，不用管理员权限看不到大小，通常是「看不到的空间」的大头。可以在「系统保护」里调整它最多占多少。", "Kept in System Volume Information; their size can’t be read without admin and they are often most of the space not accounted for. System Protection sets how much they may use."),
             Btn(L("系统保护", "System Protection"), () => Open("SystemPropertiesProtection.exe")));
        Item(L("组件存储（WinSxS）", "Component store (WinSxS)"), L("未测量", "Not measured"),
             L("和 Windows 系统文件共用大量文件，直接相加会算重，所以不测。「磁盘清理 → 清理系统文件 → Windows 更新清理」可以安全地给它瘦身。", "It shares most files with Windows itself, so adding it up would double-count. Disk Cleanup → Clean up system files → Windows Update Cleanup trims it safely."));
        rows.Children.Add(Note(L("Peekaboo 不会删除任何系统文件，只告诉你用哪个 Windows 自带的工具。", "Peekaboo never deletes system files; it only points you to the right built-in Windows tool.")));
        return Card(L("系统占用", "System"), "", rows, Btn(L("存储感知", "Storage Sense"), () => Open("ms-settings:storagesense"), glyph: ""));
    }

    // ---- unseen

    UIElement UnseenCard()
    {
        if (DiskWatch.Latest is not { } s) return new Border();
        var pl = DiskWatch.Here;
        long R(string rel) => s.Roots.GetValueOrDefault(System.IO.Path.Combine(s.Drive, rel));
        long users = R("Users"), programs = R("Program Files") + R("Program Files (x86)"), windows = R("Windows"), pd = s.Roots.GetValueOrDefault(pl.ProgramData);
        long sysFiles = R("pagefile.sys") + R("hiberfil.sys") + R("swapfile.sys"), recycle = R("$Recycle.Bin");
        long other = Math.Max(0, s.Accounted - users - programs - windows - pd - sysFiles - recycle);
        var segs = new List<(string, double, Color)>
        {
            (L("你的用户目录", "Your profile"), s.Profile, Theme.CpuC),
            (L("其他用户和公共", "Other users & Public"), Math.Max(0, users - s.Profile), Color.FromRgb(115, 140, 166)),
            (L("程序", "Programs"), programs, Theme.MemC),
            ("Windows", windows, Theme.DiskC),
            ("ProgramData", pd, Color.FromRgb(191, 166, 38)),
            (L("页面 / 休眠文件", "Page / hibernation"), sysFiles, Theme.NetInC),
            (L("回收站", "Recycle Bin"), recycle, Color.FromRgb(230, 84, 140)),
            (L("其他", "Other"), other, Color.FromRgb(160, 160, 160)),
            (L("看不到的空间", "Not accounted for"), s.Unseen, Color.FromArgb(110, 128, 128, 128)),
        };
        var head = Row(V(2, T(Fmt.Bytes(s.Unseen), 22, FontWeights.Bold), T(L($"磁盘已用 {Fmt.Bytes(s.Used)}，Peekaboo 数到了 {Fmt.Bytes(s.Accounted)}", $"{Fmt.Bytes(s.Used)} used on the drive, Peekaboo counted {Fmt.Bytes(s.Accounted)}"), 12, color: Theme.Secondary)));
        return Card(L("看不到的空间", "Not accounted for"), "", V(10, head,
            Stacked(segs.Where(x => x.Item2 > 0).ToList(), Math.Max(1, s.Used), 14),
            Note(L("这是 Windows 受保护的系统文件、其他用户的文件、系统还原点和卷影副本等，不用管理员权限读不到，所以算不进来。", "System files Peekaboo can’t read without admin, other users’ files, and restore points / shadow copies, so they can’t be counted."))));
    }

    static TextBlock Note(string t) { var x = T(t, 12, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
}
