using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Peekaboo;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Settings.Load();
        Theme.Load();
        if (Arg(args, "--selftest") is { } report) { SelfTest.Run(report); return 0; }
        if (Arg(args, "--icon-sheet") is { } sheet) { SelfTest.IconSheet(sheet); return 0; }
        if (Arg(args, "--shot-demo") is { } shotDir) { ShotDemo.Run(shotDir); return 0; }

        using var mutex = new Mutex(true, "Peekaboo.SingleInstance", out bool first);
        // After an update the old process starts the new exe with --background and then quits;
        // give it a moment to let go of the mutex instead of exiting straight away.
        if (!first && args.Contains("--background"))
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { first = true; }
        if (!first && Arg(args, "--snapshot") is null) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            ThemeStyles.Install(app);
            ThemeStyles.ThemeChanged += () => { MainWindow.ThemeChanged(); TrayController.Shared.RebuildMenus(); };
            Settings.Changed += () => ThemeStyles.Apply(app);   // accent colour choice
            var m = SystemMonitor.Shared;
            m.Ticked += Alerts.Check;
            m.Start();
            TrayController.Shared.Install();
            DiskWatch.Start();   // after the tray icon exists, so a startup drop alert can show
            TypingMonitor.Shared.Apply();
            Screenshot.ApplyHotkey();
            ClipboardService.Start();
            WindowSnap.Start();
            DesktopWidget.Start();
            Updater.Start();
            if (Arg(args, "--snapshot") is { } dir) _ = Snapshot.Run(dir);
            else if (!args.Contains("--background")) MainWindow.Show(Page.Overview);
        };
        app.SessionEnding += (_, _) => Updater.InstallOnExit();
        app.Exit += (_, _) => { Updater.InstallOnExit(); SystemMonitor.Shared.Shutdown(); TypingMonitor.Shared.Stop(); TrayController.Shared.Dispose(); };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(Paths.Support, "errors.log"), $"{DateTime.Now:u} {e.Exception}\n\n"); } catch { }
            e.Handled = true;
        };
        return app.Run();
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// Threshold alerts with cooldowns, shown as tray balloon notifications.
static class Alerts
{
    static readonly Dictionary<string, DateTime> Last = new();
    static DateTime? _pressureSince, _cpuSince;
    static DateTime _lastTipCheck = DateTime.MinValue;

    public static void Check()
    {
        var m = SystemMonitor.Shared; var s = Settings.Shared; var now = DateTime.Now;
        if (s.AlertDisk && m.Disk.Total > 0 && m.Disk.Free < s.AlertDiskGB * 1073741824L)
            Fire("disk", TimeSpan.FromHours(6), L("磁盘空间不足", "Low disk space"), L($"{m.Disk.Name} 只剩 {Fmt.Bytes(m.Disk.Free)}，打开 Peekaboo 看看哪些可以清理。", $"Only {Fmt.Bytes(m.Disk.Free)} left on {m.Disk.Name}. Open Peekaboo to see what you can clean up."), Page.Cleanup);
        if (s.AlertMemory)
        {
            if (m.Memory.PressureLevel >= 4) { _pressureSince ??= now; if ((now - _pressureSince.Value).TotalSeconds > 30) Fire("mem", TimeSpan.FromHours(1), L("内存压力很高", "High memory pressure"), L($"已用 {Fmt.Bytes(m.Memory.Used)} / {Fmt.Bytes(m.Memory.Total)}，可以关掉一些占内存的应用。", $"{Fmt.Bytes(m.Memory.Used)} of {Fmt.Bytes(m.Memory.Total)} in use. Try closing some memory-heavy apps."), Page.Processes); }
            else _pressureSince = null;
        }
        if (s.AlertCpu)
        {
            if (m.Cpu.Total * 100 >= s.AlertCpuPercent) { _cpuSince ??= now; if ((now - _cpuSince.Value).TotalMinutes > 5) Fire("cpu", TimeSpan.FromHours(1), L("CPU 持续高负载", "Sustained high CPU"), L($"过去 5 分钟 CPU 一直在 {s.AlertCpuPercent}% 以上。", $"CPU has been above {s.AlertCpuPercent}% for the past 5 minutes."), Page.Processes); }
            else _cpuSince = null;
        }
        if (s.CleanupTips && (now - _lastTipCheck).TotalHours > 6)
        {
            _lastTipCheck = now;
            if (s.LastCleanupTip is null || (now - s.LastCleanupTip.Value).TotalDays > 7)
            {
                long threshold = s.CleanupTipGB * 1073741824L;
                Task.Run(() => CleanupScanner.DevCacheItems().Concat(CleanupScanner.TempAndLogItems()).Where(i => i.Recommended).Sum(i => i.Size))
                    .ContinueWith(t => UiThread.Run(() =>
                    {
                        s.LastCleanupTip = DateTime.Now; s.Save();
                        if (t.Result >= threshold) Fire("tip", TimeSpan.Zero, L($"可以清理 {Fmt.Bytes(t.Result)}", $"{Fmt.Bytes(t.Result)} can be cleaned up"), L($"开发缓存和临时文件占了 {Fmt.Bytes(t.Result)}，点这里查看并清理。", $"Developer caches and temp files take up {Fmt.Bytes(t.Result)}. Click to review and clean."), Page.Cleanup);
                    }));
            }
        }
    }

    static void Fire(string key, TimeSpan cooldown, string title, string body, Page tab)
    {
        if (Last.TryGetValue(key, out var l) && DateTime.Now - l < cooldown) return;
        Last[key] = DateTime.Now;
        TrayController.Shared.Notify(title, body, tab);
    }
}

/// `--snapshot <dir>`: opens every page with live data and saves PNGs (used by CI).
static class Snapshot
{
    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        SystemMonitor.Shared.WantProcesses(true);
        await Task.Delay(5000);

        PopupWindow.Toggle();
        await Task.Delay(1500);
        foreach (Window w in Application.Current.Windows) if (w is PopupWindow p) { Save(p, Path.Combine(dir, "0-popup.png")); p.Close(); }

        MainWindow.Show(Page.Overview);
        await Task.Delay(2500);
        Save(MainWindow.Instance!, Path.Combine(dir, "1-overview.png"));

        MainWindow.Instance!.Navigate(Page.Disk);
        DiskModel.Shared.Scan(Paths.Home);
        while (DiskModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "2-disk.png"));

        // Disk growth uses made-up history in snapshot runs (see DiskWatch.Demo).
        MainWindow.Instance!.Navigate(Page.Growth);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "16-growth.png"));

        MainWindow.Instance!.Navigate(Page.Projects);
        await Task.Delay(1000);
        while (ProjectModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1000);
        Save(MainWindow.Instance!, Path.Combine(dir, "3-projects.png"));

        MainWindow.Instance!.Navigate(Page.Processes);
        await Task.Delay(4000);
        Save(MainWindow.Instance!, Path.Combine(dir, "4-processes.png"));

        MainWindow.Instance!.Navigate(Page.Cleanup);
        await Task.Delay(1000);
        while (CleanupModel.Shared.Scanning || ProjectModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1000);
        Save(MainWindow.Instance!, Path.Combine(dir, "5-cleanup.png"));

        // Duplicates is captured before a scan: scanning personal folders could take minutes.
        MainWindow.Instance!.Navigate(Page.Duplicates);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "13-duplicates.png"));

        MainWindow.Instance!.Navigate(Page.Uninstall);
        await Task.Delay(1000);
        while (UninstallModel.Shared.Loading) await Task.Delay(500);
        await Task.Delay(1000);
        Save(MainWindow.Instance!, Path.Combine(dir, "14-uninstall.png"));

        // Exercise the keyboard hook: simulate keys and check they were counted.
        Settings.Shared.TypingEnabled = true;
        TypingMonitor.Shared.Apply();
        MainWindow.Instance!.Navigate(Page.Typing);
        int before = TypingMonitor.Shared.Today.Total;
        foreach (var ch in "peekaboo is counting keys only 1234567890")
        {
            byte vk = ch == ' ' ? (byte)0x20 : (byte)char.ToUpperInvariant(ch);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            await Task.Delay(15);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
            await Task.Delay(15);
        }
        await Task.Delay(1500);
        File.WriteAllText(Path.Combine(dir, "typing.txt"), $"hook running={TypingMonitor.Shared.Running} counted={TypingMonitor.Shared.Today.Total - before} of 41");
        Save(MainWindow.Instance!, Path.Combine(dir, "7-typing.png"));

        MainWindow.Instance!.Navigate(Page.Clipboard); await Task.Delay(1500); Save(MainWindow.Instance!, Path.Combine(dir, "10-clipboard.png"));

        MainWindow.Instance!.Navigate(Page.WindowSnap); await Task.Delay(1500); Save(MainWindow.Instance!, Path.Combine(dir, "15-windows.png"));

        MainWindow.Instance!.Navigate(Page.Battery);
        await Task.Delay(2500);
        Save(MainWindow.Instance!, Path.Combine(dir, "11-battery.png"));

        MainWindow.Instance!.Navigate(Page.Network);
        await Task.Delay(4000);
        Save(MainWindow.Instance!, Path.Combine(dir, "12-network.png"));

        MainWindow.Instance!.Navigate(Page.Settings);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "6-settings.png"));
        Application.Current.Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static void Save(Window w, string file)
    {
        try
        {
            var el = (FrameworkElement)w.Content;
            el.UpdateLayout();
            double dpi = VisualTreeHelper.GetDpi(el).PixelsPerInchX;
            int width = (int)Math.Max(1, el.ActualWidth * dpi / 96), height = (int)Math.Max(1, el.ActualHeight * dpi / 96);
            var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var ctx = bg.RenderOpen()) ctx.DrawRectangle(Theme.Window, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch (Exception e) { File.WriteAllText(file + ".error.txt", e.ToString()); }
    }
}

/// `--shot-demo <dir>`: renders the screenshot editor on a real capture of the screen
/// with sample annotations, plus the exported crop (used by CI).
static class ShotDemo
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            ThemeStyles.Install(app);
            try
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), System.Drawing.Color.SteelBlue, System.Drawing.Color.Orchid, 45f);
                    g.FillRectangle(bg, 0, 0, b.Width, b.Height);
                    g.FillRectangle(System.Drawing.Brushes.White, 120, 120, 640, 400);
                    using var f = new System.Drawing.Font("Microsoft YaHei UI", 20);
                    g.DrawString("Peekaboo 截图测试窗口\n这里有一些文字，用来测试马赛克和标注。", f, System.Drawing.Brushes.Black, 150, 180);
                }
                var o = new ShotOverlay(Screenshot.ToSource(bmp), new List<Rect> { new(120, 120, 640, 400) }, b);
                o.Show();
                await Task.Delay(800);
                var red = Screenshot.Colors[0];
                o.Demo(new Rect(100, 100, 700, 460), new[]
                {
                    new ShotMark { Kind = ShotTool.Rect, A = new Point(140, 170), B = new Point(440, 225), Color = Screenshot.Colors[0], Width = 3 },
                    new ShotMark { Kind = ShotTool.Arrow, A = new Point(600, 480), B = new Point(450, 260), Color = Screenshot.Colors[1], Width = 4 },
                    new ShotMark { Kind = ShotTool.Pen, Points = Enumerable.Range(0, 40).Select(i => new Point(160 + i * 8, 420 + Math.Sin(i / 3.0) * 18)).ToList(), Color = Screenshot.Colors[2], Width = 3 },
                    new ShotMark { Kind = ShotTool.Mosaic, Brush = true, Width = 26, Points = Enumerable.Range(0, 30).Select(i => new Point(150 + i * 16, 225 + Math.Sin(i / 2.0) * 4)).ToList(), Color = red },
                    new ShotMark { Kind = ShotTool.Mosaic, Blur = true, A = new Point(600, 380), B = new Point(750, 500), Color = red },
                    new ShotMark { Kind = ShotTool.Text, A = new Point(470, 330), Text = "这里很重要", Color = Screenshot.Colors[3], Width = 28 },
                });
                await Task.Delay(800);
                SaveVisual((FrameworkElement)o.Content, Path.Combine(dir, "overlay.png"));
                var img = o.ExportForTest();
                if (img is not null)
                {
                    using var fs = File.Create(Path.Combine(dir, "export.png"));
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); enc.Save(fs);
                    File.WriteAllText(Path.Combine(dir, "shot.txt"), $"export {img.PixelWidth}x{img.PixelHeight}");
                }
                // A selected text shows its box, the resize handle and the text options.
                o.DemoSelectText();
                await Task.Delay(400);
                SaveVisual((FrameworkElement)o.Content, Path.Combine(dir, "overlay-text.png"));
                File.AppendAllText(Path.Combine(dir, "shot.txt"), Environment.NewLine + o.DemoChecks());
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "shot-error.txt"), e.ToString()); }
            app.Shutdown();
        };
        app.Run();
    }

    static void SaveVisual(FrameworkElement el, string file)
    {
        el.UpdateLayout();
        double dpi = VisualTreeHelper.GetDpi(el).PixelsPerInchX;
        var rtb = new RenderTargetBitmap((int)(el.ActualWidth * dpi / 96), (int)(el.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(el);
        using var fs = File.Create(file);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb)); enc.Save(fs);
    }
}
