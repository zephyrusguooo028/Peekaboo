import SwiftUI
import AppKit

@main
enum Entry {
    static func main() {
        Migration.fromMacPulse()
        UserDefaults.standard.set(false, forKey: "NSQuitAlwaysKeepsWindows")
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--icon-sheet"), i + 1 < args.count {
            MainActor.assumeIsolated { IconSheet.run(to: URL(fileURLWithPath: args[i + 1])) }
            exit(0)
        }
        if let i = args.firstIndex(of: "--shot-demo"), i + 1 < args.count {
            _ = NSApplication.shared
            MainActor.assumeIsolated { ShotDemo.run(to: URL(fileURLWithPath: args[i + 1])) }
            exit(0)
        }
        if let i = args.firstIndex(of: "--perm-check"), i + 1 < args.count {
            // Run through `open` so macOS attributes the request to this app, not a terminal.
            var lines = ["bundle=\(Bundle.main.bundleIdentifier ?? "nil") path=\(Bundle.main.bundlePath)"]
            lines.append("listenPreflight=\(CGPreflightListenEventAccess())")
            lines.append("screenPreflight=\(CGPreflightScreenCaptureAccess())")
            let mask = CGEventMask(1 << CGEventType.keyDown.rawValue)
            let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly, eventsOfInterest: mask,
                                        callback: { _, _, e, _ in Unmanaged.passUnretained(e) }, userInfo: nil)
            lines.append("tapCreated=\(tap != nil)")
            try? lines.joined(separator: "\n").write(toFile: args[i + 1], atomically: true, encoding: .utf8)
            exit(0)
        }
        if let i = args.firstIndex(of: "--snap-test"), i + 1 < args.count {
            _ = NSApplication.shared
            MainActor.assumeIsolated { WindowSnapper.selfTest(to: args[i + 1]) }
            exit(0)
        }
        if args.contains("--growth-scan") {
            _ = NSApplication.shared
            let t0 = Date()
            let snap = WatchPoints.measure { _ in }
            let gb = 1_073_741_824.0
            print(String(format: "scan %.1fs used %.1f GB seen %.1f GB volumes %@ updateStaged %@ fullDisk %@", Date().timeIntervalSince(t0), Double(snap.used) / gb, Double(snap.seen) / gb,
                         snap.volumes.mapValues { String(format: "%.1f", Double($0) / gb) }.description, "\(snap.updateStaged)", "\(DiskGrowth.checkFullDiskAccess())"))
            MainActor.assumeIsolated {
                for (p, v) in snap.sizes.sorted(by: { $0.value > $1.value }).prefix(20) {
                    print(String(format: "%7.2f GB  %@  (%@)", Double(v) / gb, WatchPoints.name(for: p), p))
                }
            }
            exit(0)
        }
        if args.contains("--selftest") {
            SelfTest.run()
            exit(0)
        }
        PeekabooApp.main()
    }
}

enum MainTab: String, CaseIterable, Identifiable {
    case overview, disk, projects, processes, cleanup, typing, screenshot, battery, network, duplicates, uninstall, clipboard, windows, growth, settings
    var id: String { rawValue }
    var title: String {
        switch self {
        case .overview: return L("概览", "Overview")
        case .disk: return L("磁盘", "Disk")
        case .projects: return L("项目", "Projects")
        case .processes: return L("进程", "Processes")
        case .cleanup: return L("清理", "Clean up")
        case .typing: return L("打字", "Typing")
        case .screenshot: return L("截图", "Screenshot")
        case .battery: return L("电池", "Battery")
        case .network: return L("网络", "Network")
        case .duplicates: return L("重复文件", "Duplicates")
        case .uninstall: return L("卸载应用", "Uninstall")
        case .clipboard: return L("剪贴板", "Clipboard")
        case .windows: return L("分屏", "Window layout")
        case .growth: return L("空间变化", "Disk growth")
        case .settings: return L("设置", "Settings")
        }
    }
    var symbol: String {
        switch self {
        case .overview: return "gauge.with.dots.needle.67percent"
        case .disk: return "internaldrive"
        case .projects: return "folder.badge.gearshape"
        case .processes: return "list.bullet.rectangle"
        case .cleanup: return "sparkles"
        case .typing: return "keyboard"
        case .screenshot: return "camera.viewfinder"
        case .battery: return "battery.75percent"
        case .network: return "network"
        case .duplicates: return "doc.on.doc"
        case .uninstall: return "trash.square"
        case .clipboard: return "list.clipboard"
        case .windows: return "rectangle.split.3x1"
        case .growth: return "chart.line.downtrend.xyaxis"
        case .settings: return "gearshape"
        }
    }
}

/// Shared state for every view.
@MainActor
@Observable
final class AppState {
    static let shared = AppState()
    let monitor = SystemMonitor.shared
    let settings = AppSettings.shared
    let disk = DiskScanModel()
    let projects = ProjectModel()
    let cleanup = CleanupModel()
    var tab: MainTab = .overview
}

struct PeekabooApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        // The menu bar item and windows are managed through AppKit (StatusItemController,
        // WindowManager). SwiftUI only needs a scene to exist; a Settings scene would be
        // opened as an empty window at launch, so use a menu bar extra that is never inserted.
        MenuBarExtra("Peekaboo", isInserted: .constant(false)) { EmptyView() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ n: Notification) {
        MainActor.assumeIsolated {
            NSApp.setActivationPolicy(.accessory)
            let m = SystemMonitor.shared
            m.onTick = {
                AlertManager.shared.check($0)
                StatusItemController.shared.refresh()
            }
            m.start()
            IconAnimator.shared.start()
            StatusItemController.shared.install()
            // Users who turned typing on before the menu bar option existed get it once.
            let st = AppSettings.shared
            if st.typingEnabled && !UserDefaults.standard.bool(forKey: "typingMetricAdded") {
                if !st.menuMetrics.contains(.typing) { st.menuMetrics.append(.typing) }
                UserDefaults.standard.set(true, forKey: "typingMetricAdded")
            }
            TypingMonitor.shared.apply()
            ScreenshotController.shared.applyHotkey()
            ClipboardService.startIfEnabled()
            WindowSnapper.shared.start()
            DiskGrowth.shared.start()
            Updater.shared.start()
            DesktopWidget.startIfEnabled()
            AlertManager.shared.setup()
            if let i = CommandLine.arguments.firstIndex(of: "--snapshot"), i + 1 < CommandLine.arguments.count {
                Snapshot.run(to: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
            } else if !Demo.active {
                OnboardingWindow.showIfNeeded()
            }
            if let i = CommandLine.arguments.firstIndex(of: "--shot-focus-test"), i + 1 < CommandLine.arguments.count {
                let out = CommandLine.arguments[i + 1]
                let before = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "nil"
                ScreenshotController.shared.start()
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) {
                    let during = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "nil"
                    ScreenshotController.shared.finish()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                        let after = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "nil"
                        try? "before=\(before) during=\(during) after=\(after)".write(toFile: out, atomically: true, encoding: .utf8)
                        NSApp.terminate(nil)
                    }
                }
            }
            if CommandLine.arguments.contains("--open") {
                WindowManager.shared.show(.overview)
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        MainActor.assumeIsolated { WindowManager.shared.show(nil) }
        return true
    }

    func applicationWillTerminate(_ n: Notification) {
        MainActor.assumeIsolated { SystemMonitor.shared.shutdown(); TypingMonitor.shared.save(); Updater.shared.installOnQuit() }
    }
}

/// The main window is managed through AppKit so it can be opened from anywhere
/// (popover, notifications) and so the Dock icon only shows while it is open.
@MainActor
final class WindowManager: NSObject, NSWindowDelegate {
    static let shared = WindowManager()
    private(set) var window: NSWindow?

    func show(_ tab: MainTab?) {
        StatusItemController.shared.closePopover()
        if let tab { AppState.shared.tab = tab }
        if window == nil {
            let host = NSHostingController(rootView: MainView().environment(AppState.shared))
            let w = NSWindow(contentViewController: host)
            w.title = "Peekaboo"
            w.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            w.titlebarAppearsTransparent = true
            w.toolbarStyle = .unified
            w.setContentSize(NSSize(width: 1120, height: 760))
            w.minSize = NSSize(width: 900, height: 600)
            w.isReleasedWhenClosed = false
            w.delegate = self
            w.center()
            w.setFrameAutosaveName("PeekabooMain")
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}

/// One-time move from the app's old name (MacPulse, local.macpulse.app).
enum Migration {
    static func fromMacPulse() {
        let d = UserDefaults.standard
        guard !d.bool(forKey: "migratedFromMacPulse") else { return }
        if let old = d.persistentDomain(forName: "local.macpulse.app") {
            for (k, v) in old where d.object(forKey: k) == nil { d.set(v, forKey: k) }
        }
        let fm = FileManager.default
        let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let oldDir = base.appendingPathComponent("MacPulse"), newDir = base.appendingPathComponent("Peekaboo")
        if fm.fileExists(atPath: oldDir.path) && !fm.fileExists(atPath: newDir.path) {
            try? fm.moveItem(at: oldDir, to: newDir)
        }
        d.set(true, forKey: "migratedFromMacPulse")
    }
}
