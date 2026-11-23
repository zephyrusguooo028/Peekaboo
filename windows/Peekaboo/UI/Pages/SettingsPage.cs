using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class SettingsPage : PageView
{
    Settings S => Settings.Shared;
    readonly List<(Image img, TrayIcon icon)> _previews = new();
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _cache = new();

    public override void Shown()
    {
        Updater.Changed += Build;
        TrayController.Shared.Viewer(true);
        TrayController.Shared.FrameChanged += Animate;
        Build();
    }

    public override void Hidden()
    {
        Updater.Changed -= Build;
        TrayController.Shared.Viewer(false);
        TrayController.Shared.FrameChanged -= Animate;
    }

    void Save() { S.Save(); Build(); }

    void Animate()
    {
        int f = TrayController.Shared.Frame;
        foreach (var (img, icon) in _previews)
        {
            var c = S.Icon == icon ? Theme.AccentColor : ((SolidColorBrush)Theme.Text).Color;
            int fr = f % IconArt.Frames(icon);
            var key = $"{icon}{fr}{c}";
            if (!_cache.TryGetValue(key, out var src))
            {
                using var bmp = IconArt.Render(icon, fr, SystemMonitor.Shared.Cpu.Total, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 72);
                src = IconArt.ToWpf(bmp);
                _cache[key] = src;
            }
            img.Source = src;
        }
    }

    void Build()
    {
        _previews.Clear();

        // ---- tray
        var grid = new UniformGrid { Columns = 5 };
        foreach (var icon in Enum.GetValues<TrayIcon>())
        {
            var ic = icon;
            FrameworkElement visual;
            if (icon == TrayIcon.None) visual = Glyph("", 22, Theme.Tertiary);
            else { var img = new Image { Width = 36, Height = 36 }; RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor); _previews.Add((img, icon)); visual = img; }
            visual.HorizontalAlignment = HorizontalAlignment.Center;
            var label = T(IconArt.Title(icon), 12); label.HorizontalAlignment = HorizontalAlignment.Center;
            var tile = new Border
            {
                Margin = new Thickness(4), Padding = new Thickness(6, 10, 6, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = S.Icon == icon ? Theme.AccentSoftBrush : Theme.CardAlt,
                BorderBrush = S.Icon == icon ? Theme.AccentBrush : Brushes.Transparent, BorderThickness = new Thickness(1.5),
                Child = V(6, new Grid { Height = 36, Children = { visual } }, label),
            };
            tile.MouseLeftButtonUp += (_, _) => { S.Icon = ic; Save(); };
            grid.Children.Add(tile);
        }
        Animate();

        var swatches = H(10, T(L("主题色", "Accent color"), 13));
        foreach (var t in Enum.GetValues<ThemeChoice>())
        {
            var tc = t;
            Brush fill = t switch
            {
                ThemeChoice.System => new LinearGradientBrush(new GradientStopCollection { new(Colors.Red, 0), new(Colors.Orange, 0.25), new(Colors.LimeGreen, 0.5), new(Colors.DodgerBlue, 0.75), new(Colors.MediumPurple, 1) }, 0),
                ThemeChoice.Load => new LinearGradientBrush(Theme.DiskC, Theme.DangerC, 0),
                _ => Theme.B(Theme.ChoiceColor(t, 0)),
            };
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = fill, BorderBrush = S.Theme == t ? Theme.Text : Theme.Line, BorderThickness = new Thickness(S.Theme == t ? 2 : 1), HorizontalAlignment = HorizontalAlignment.Center };
            var cap = T(Theme.ChoiceTitle(t), 10.5, color: Theme.Secondary); cap.HorizontalAlignment = HorizontalAlignment.Center;
            var sw = V(4, dot, cap); sw.Width = 54; sw.Cursor = Cursors.Hand; sw.Background = Brushes.Transparent;
            sw.MouseLeftButtonUp += (_, _) => { S.Theme = tc; Save(); MainWindow.Instance?.Navigate(Page.Settings); };
            swatches.Children.Add(sw);
        }

        var metrics = new UniformGrid { Columns = 4 };
        var titles = new Dictionary<TrayMetric, string>
        {
            [TrayMetric.Cpu] = L("CPU 占用", "CPU usage"), [TrayMetric.CpuGraph] = L("CPU 走势图", "CPU graph"), [TrayMetric.Memory] = L("内存占用", "Memory usage"), [TrayMetric.Disk] = L("磁盘剩余（GB）", "Disk free (GB)"),
            [TrayMetric.Network] = L("下载速度", "Download speed"), [TrayMetric.Gpu] = L("GPU 占用", "GPU usage"), [TrayMetric.Battery] = L("电池", "Battery"), [TrayMetric.Typing] = L("今日打字", "Typing today"),
        };
        foreach (var m in Enum.GetValues<TrayMetric>())
        {
            var mm = m;
            var c = Check(titles[m], S.Shows(m), on => S.SetShown(mm, on));
            c.Margin = new Thickness(0, 4, 0, 4);
            metrics.Children.Add(c);
        }
        var interval = new ComboBox { Width = 100 };
        foreach (var v in new[] { 1.0, 2.0, 3.0, 5.0 })
        {
            var item = new ComboBoxItem { Content = L($"{v:0} 秒", $"{v:0} s"), Tag = v };
            interval.Items.Add(item);
            if (Math.Abs(S.SampleInterval - v) < 0.01) interval.SelectedItem = item;
        }
        interval.SelectionChanged += (_, _) => { S.SampleInterval = (double)((ComboBoxItem)interval.SelectedItem).Tag; S.Save(); SystemMonitor.Shared.Reschedule(); };

        var tray = Card(L("任务栏托盘", "Taskbar tray"), "", V(12,
            T(L("动画图标", "Animated icon"), 13, FontWeights.SemiBold),
            Note(L("CPU 越忙，动画越快。动画本身大约占 1–2% CPU，选「不显示」最省电。", "The busier the CPU, the faster it animates. The animation itself uses about 1–2% CPU; choose “None” to save the most power.")),
            grid,
            Check(L("图标按 CPU 负载变色（绿 / 橙 / 红）", "Tint icon by CPU load (green / orange / red)"), S.IconTint, on => { S.IconTint = on; S.Save(); }),
            swatches,
            new Border { Height = 1, Background = Theme.Line },
            T(L("托盘里显示的数值", "Values shown in the tray"), 13, FontWeights.SemiBold),
            Note(L("Windows 托盘只能放图标，所以每个勾选的数值都会单独占一个小图标，鼠标悬停可以看到详情。", "The Windows tray only holds icons, so each value you tick gets its own small icon. Hover over it for details.")),
            metrics,
            H(8, T(L("刷新间隔", "Refresh interval"), 13), interval, Note(L("间隔越长越省电", "Longer intervals save power")))));

        // ---- language
        var lang = new ComboBox { Width = 160 };
        foreach (var (val, text) in new[] { (AppLanguage.System, "跟随系统 / System"), (AppLanguage.Zh, "简体中文"), (AppLanguage.En, "English") })
        {
            var item = new ComboBoxItem { Content = text, Tag = val };
            lang.Items.Add(item);
            if (S.Language == val) lang.SelectedItem = item;
        }
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is not ComboBoxItem { Tag: AppLanguage v } || v == S.Language) return;
            S.Language = v; S.Save();
            TrayController.Shared.RebuildMenus();
            MainWindow.Instance?.RebuildNav();
            Dispatcher.BeginInvoke(() => MainWindow.Instance?.Navigate(Page.Settings));
        };
        var language = Card("语言 / Language", "\uE774", V(10, H(8, T(L("界面语言", "Display language"), 13), lang)));

        // ---- general
        var login = Check(L("开机时自动启动", "Start automatically at sign-in"), Settings.LaunchAtLogin, on => Settings.LaunchAtLogin = on);
        var general = Card(L("通用", "General"), "", V(10, login));

        // ---- alerts
        var alerts = Card(L("提醒", "Alerts"), "", V(10,
            H(8, Check(L("磁盘可用空间低于", "Alert when free disk space is below"), S.AlertDisk, on => { S.AlertDisk = on; S.Save(); }), Stepper(() => S.AlertDiskGB, v => S.AlertDiskGB = v, 5, 500, 5, " GB"), T(L("时提醒", ""), 13)),
            Check(L("可用空间一天内减少超过 5 GB 时提醒", "Alert when free space drops by more than 5 GB in a day"), S.AlertDiskDrop, on => { S.AlertDiskDrop = on; S.Save(); }),
            Check(L("内存压力持续紧张时提醒", "Alert when memory pressure stays critical"), S.AlertMemory, on => { S.AlertMemory = on; S.Save(); }),
            H(8, Check(L("CPU 连续 5 分钟高于", "Alert when CPU stays above"), S.AlertCpu, on => { S.AlertCpu = on; S.Save(); }), Stepper(() => S.AlertCpuPercent, v => S.AlertCpuPercent = v, 50, 100, 5, "%"), T(L("时提醒", " for 5 minutes"), 13)),
            H(8, Check(L("每周检查一次，可清理空间超过", "Check weekly and notify when cleanable space exceeds"), S.CleanupTips, on => { S.CleanupTips = on; S.Save(); }), Stepper(() => S.CleanupTipGB, v => S.CleanupTipGB = v, 1, 100, 1, " GB"), T(L("时提示", ""), 13)),
            Note(L("同一种提醒有冷却时间，不会反复打扰。", "Each alert has a cooldown, so you won’t be nagged repeatedly."))));

        var rules = Card(L("清理规则", "Cleanup rules"), "", V(10,
            H(8, T(L("下载文件夹里超过", "Files in Downloads untouched for more than"), 13), Stepper(() => S.DownloadsOldDays, v => S.DownloadsOldDays = v, 7, 720, 15, L(" 天", " days")), T(L("没动的文件算「旧文件」", "count as “old files”"), 13)),
            H(8, T(L("超过", "Files over"), 13), Stepper(() => S.LargeFileMB, v => S.LargeFileMB = v, 50, 10000, 50, " MB"), T(L("的文件算「大文件」", "count as “large files”"), 13)),
            H(8, T(L("超过", "Projects untouched for over"), 13), Stepper(() => S.StaleProjectDays, v => S.StaleProjectDays = v, 7, 365, 7, L(" 天", " days")), T(L("没改动的项目，默认勾选它的依赖和构建产物", "get their dependencies and build output selected by default"), 13))));

        var about = Card(L("关于", "About"), "", V(6,
            Note(L("Peekaboo 只在本机读取系统信息，不上传任何数据；唯一的联网是检查更新（可关闭）。清理默认移到回收站，并且永远不碰系统目录和用户目录之外的文件。", "Peekaboo reads system information locally and never uploads anything; the only network access is the optional update check. Cleanup moves items to the Recycle Bin by default and never touches system folders or anything outside your user folder.")),
            Note(L("CPU 温度在 Windows 上需要管理员权限才能读取，所以这里不显示。", "Reading CPU temperature on Windows requires administrator rights, so it isn’t shown here.")),
            Note(L("数据保存在 ", "Data is stored in ") + Paths.Support)));

        var page = V(16, PageHeader(L("设置", "Settings"), "Peekaboo " + Updater.Current), language, tray, general, alerts, rules,
            PathList(L("项目目录", "Project folders"), "", L("在这些目录里自动找开发项目（最多往下找 4 层）。", "Peekaboo looks for development projects in these folders (up to 4 levels deep)."), S.ProjectRoots),
            PathList(L("白名单", "Whitelist"), "", L("这些位置永远不会被清理。也可以在任意列表里右键「加入白名单」。", "These locations are never cleaned. You can also right-click any item in a list and choose “Add to whitelist”."), S.Whitelist),
            UpdateCard(), ClipboardSettingsCard.Build(), WidgetSettingsCard.Build(),
            about);
        page.MaxWidth = 820; page.HorizontalAlignment = HorizontalAlignment.Left;
        Content = page;
    }

    static TextBlock Note(string t) { var x = T(t, 12, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }

    FrameworkElement Stepper(Func<int> get, Action<int> set, int min, int max, int step, string unit)
    {
        var value = T($"{get()}{unit}", 13, FontWeights.SemiBold); value.MinWidth = 60; value.TextAlignment = TextAlignment.Center; value.VerticalAlignment = VerticalAlignment.Center;
        Button B(string g, int d)
        {
            var b = Btn("", () => { set(Math.Clamp(get() + d, min, max)); S.Save(); value.Text = $"{get()}{unit}"; }, glyph: g);
            b.Padding = new Thickness(6, 2, 6, 2);
            return b;
        }
        return H(4, B("", -step), value, B("", step));
    }

    Border PathList(string title, string glyph, string note, List<string> paths)
    {
        var s = V(6, Note(note));
        if (paths.Count == 0) s.Children.Add(T(L("（空）", "(empty)"), 12.5, color: Theme.Tertiary));
        foreach (var p in paths.ToList())
        {
            var path = p;
            var exists = Directory.Exists(Settings.Expand(p)) || File.Exists(Settings.Expand(p));
            var remove = Btn("", () => { paths.Remove(path); Save(); }, glyph: ""); remove.Padding = new Thickness(6, 2, 6, 2);
            s.Children.Add(Row(H(8, Glyph("", 13), T(p, 13), T(exists ? "" : L("不存在", "not found"), 11.5, color: Theme.Tertiary)), remove));
        }
        var add = Btn(L("添加…", "Add…"), () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Paths.Home, Multiselect = true };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FolderNames) { var t = Fmt.Tilde(f); if (!paths.Contains(t)) paths.Add(t); }
            Save();
        }, glyph: "");
        return Card(title, glyph, s, add);
    }

    UIElement UpdateCard()
    {
        CheckBox? install = null;
        var auto = Check(L("自动检查更新", "Check automatically"), S.AutoUpdate, on =>
        {
            S.AutoUpdate = on; S.Save();
            if (install is not null) install.IsEnabled = on;
            Updater.SettingsChanged();
        });
        install = Check(L("自动安装更新", "Install updates automatically"), S.AutoInstallUpdate, on => { S.AutoInstallUpdate = on; S.Save(); Updater.SettingsChanged(); });
        install.IsEnabled = S.AutoUpdate;
        var installBlock = V(2, install, Note(L("在后台下载好，等你离开电脑一会儿或退出 Peekaboo 时自动换成新版本。",
                                                "Downloads in the background and switches to the new version when you're away for a while or quit Peekaboo.")));
        installBlock.Margin = new Thickness(24, 0, 0, 0);
        var body = V(10, T(L($"当前版本 {Updater.Current}", $"Version {Updater.Current}"), 13), V(6, auto, installBlock));
        switch (Updater.State)
        {
            case Updater.Status.Checking: body.Children.Add(T(L("正在检查…", "Checking…"), 12.5, color: Theme.Secondary)); break;
            case Updater.Status.Available:
                body.Children.Add(T(L($"有新版本 {Updater.NewVersion}", $"Version {Updater.NewVersion} is available"), 14, FontWeights.SemiBold, Theme.AccentBrush));
                if (Updater.Notes.Length > 0) body.Children.Add(Note(Updater.Notes));
                body.Children.Add(H(8, Btn(L("下载并安装", "Download and install"), () => _ = Updater.Install(), primary: true),
                                     T(L("会自动替换并重新打开，设置会保留。", "Peekaboo replaces itself and reopens; settings are kept."), 12, color: Theme.Secondary)));
                break;
            case Updater.Status.Ready:
                body.Children.Add(T(L($"已下载 {Updater.NewVersion}，空闲时会自动安装", $"{Updater.NewVersion} is downloaded and installs when you're idle"), 14, FontWeights.SemiBold, Theme.AccentBrush));
                if (Updater.Notes.Length > 0) body.Children.Add(Note(Updater.Notes));
                body.Children.Add(H(8, Btn(L("立即安装", "Install now"), () => _ = Updater.Install(), primary: true)));
                break;
            case Updater.Status.Downloading: body.Children.Add(V(4, T(L("正在下载…", "Downloading…"), 12.5), Bar(Updater.Progress, Theme.AccentColor, 6))); break;
            case Updater.Status.Installing: body.Children.Add(T(L("正在安装，马上重新打开…", "Installing, reopening shortly…"), 12.5)); break;
            default:
                var row = H(10, Btn(L("检查更新", "Check for updates"), () => _ = Updater.Check()));
                if (Updater.State == Updater.Status.UpToDate) row.Children.Add(T(L("已经是最新版本", "You're up to date"), 12.5, color: Theme.B(Theme.DiskC)));
                if (Updater.State == Updater.Status.Failed && Updater.Error is { } err) row.Children.Add(T(err, 12, color: Theme.B(Theme.DangerC)));
                body.Children.Add(row);
                break;
        }
        body.Children.Add(Note(L("检查更新时只访问 GitHub 上的公开版本信息，不上传任何数据。关掉自动检查后 Peekaboo 完全不联网。",
                                 "Update checks only read public release info from GitHub and send no data. With automatic checks off, Peekaboo never goes online.")));
        return Card(L("更新", "Updates"), "\uE896", body);
    }
}
