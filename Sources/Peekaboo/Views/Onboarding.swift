import SwiftUI
import AppKit
import ApplicationServices

/// macOS never lets an app grant itself privacy permissions; the user flips each switch in
/// System Settings. This window gets them there in one click per permission, watches for the
/// change, and restarts Peekaboo by itself when a permission only applies after a restart.
enum PermissionKind: String, CaseIterable, Identifiable {
    case screen, input, accessibility, fullDisk
    var id: String { rawValue }

    var title: String {
        switch self {
        case .screen: return L("屏幕录制", "Screen Recording")
        case .input: return L("输入监控", "Input Monitoring")
        case .accessibility: return L("辅助功能", "Accessibility")
        case .fullDisk: return L("完全磁盘访问", "Full Disk Access")
        }
    }

    var purpose: String {
        switch self {
        case .screen: return L("截图要用。只在你按下截图快捷键时读取屏幕。", "Needed for screenshots. The screen is only read when you take one.")
        case .input: return L("打字统计要用。只数按键次数，不记录内容。", "Needed for typing stats. Only key counts, never what you type.")
        case .fullDisk: return L("「空间变化」要用：量出微信、QQ、照片等受保护文件夹各占多少。只读大小，不读内容。", "For Disk growth: measures protected folders such as WeChat, QQ and Photos. Sizes only, never contents.")
        case .accessibility: return L("分屏（移动、缩放其他应用的窗口）和剪贴板「自动粘贴」要用。", "Needed for window layout (moving and resizing other apps' windows) and clipboard auto-paste.")
        }
    }

    var symbol: String {
        switch self {
        case .screen: return "camera.viewfinder"
        case .input: return "keyboard"
        case .accessibility: return "accessibility"
        case .fullDisk: return "internaldrive"
        }
    }

    var granted: Bool {
        switch self {
        case .screen: return CGPreflightScreenCaptureAccess()
        case .input: return CGPreflightListenEventAccess()
        case .accessibility: return AXIsProcessTrusted()
        case .fullDisk: return DiskGrowth.checkFullDiskAccess()
        }
    }

    var settingsURL: URL? {
        let anchor: String
        switch self {
        case .screen: anchor = "Privacy_ScreenCapture"
        case .input: anchor = "Privacy_ListenEvent"
        case .accessibility: anchor = "Privacy_Accessibility"
        case .fullDisk: anchor = "Privacy_AllFiles"
        }
        return URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)")
    }

    /// Shows the system prompt (first time only) and opens the right System Settings pane.
    func request() {
        switch self {
        case .screen: _ = CGRequestScreenCaptureAccess()
        case .input: _ = CGRequestListenEventAccess()
        case .accessibility:
            let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(opts)
        case .fullDisk:
            // There is no prompt API; touching a protected folder makes Peekaboo show up in the list.
            _ = try? FileManager.default.contentsOfDirectory(atPath: Paths.h("Library/Safari").path)
        }
        if let u = settingsURL { NSWorkspace.shared.open(u) }
    }
}

@MainActor
@Observable
final class PermissionWatcher {
    static let shared = PermissionWatcher()
    var granted: [PermissionKind: Bool] = [:]
    /// Permissions the user asked for in this window; used to decide on an automatic restart.
    var requested: Set<PermissionKind> = []
    var restarting = false
    @ObservationIgnored private var timer: Timer?

    func start() {
        refresh()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { _ in
            MainActor.assumeIsolated { PermissionWatcher.shared.tick() }
        }
    }

    func stop() { timer?.invalidate(); timer = nil }

    func refresh() { for k in PermissionKind.allCases { granted[k] = k.granted } }

    private func tick() {
        let before = granted
        refresh()
        // Input Monitoring: once granted, try to start the tap; if macOS still refuses
        // inside this process, a restart makes it take effect.
        if granted[.input] == true, before[.input] != true, AppSettings.shared.typingEnabled || requested.contains(.input) {
            if !AppSettings.shared.typingEnabled { AppSettings.shared.typingEnabled = true }
            TypingMonitor.shared.apply()
            if !TypingMonitor.shared.running { restart() }
        }
    }

    /// Restart and come back to this window.
    func restart() {
        guard !restarting else { return }
        restarting = true
        UserDefaults.standard.set(true, forKey: "reopenOnboarding")
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { AppRelaunch.now() }
    }
}

struct OnboardingView: View {
    @Environment(AppState.self) private var app
    private let w = PermissionWatcher.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                AnimatedIconView(icon: app.settings.menuIcon == .none ? .ghost : app.settings.menuIcon, height: 30)
                    .frame(width: 52, height: 52)
                    .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Palette.theme.opacity(0.14)))
                VStack(alignment: .leading, spacing: 3) {
                    Text(L("欢迎使用 Peekaboo", "Welcome to Peekaboo")).font(.system(size: 22, weight: .bold, design: .rounded))
                    Text(L("监控、清理不需要任何权限。下面几项只在用到对应功能时才需要，可以以后再开。",
                           "Monitoring and cleanup need no permissions. The ones below are only for specific features and can wait."))
                        .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }

            VStack(spacing: 10) {
                ForEach(PermissionKind.allCases) { k in row(k) }
            }

            if w.restarting {
                Label(L("权限已生效，正在重启 Peekaboo…", "Permission granted, restarting Peekaboo…"), systemImage: "arrow.clockwise")
                    .foregroundStyle(Palette.disk)
            } else {
                Text(L("系统设置里打开开关后，这里会自动变绿；需要重启才能生效时 Peekaboo 会自己重启。",
                       "Turn the switch on in System Settings and this turns green by itself; Peekaboo restarts on its own if needed."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            HStack {
                Button(L("我已打开开关，重启生效", "I turned it on, restart now")) { w.restart() }
                    .help(L("如果开关已打开但这里还没变绿，重启一次就好。", "If a switch is on but this stays grey, a restart applies it."))
                Spacer()
                Button(L("完成", "Done")) { OnboardingWindow.close() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 560)
        .tint(Palette.theme)
        .onAppear { w.start() }
        .onDisappear { w.stop() }
    }

    private func row(_ k: PermissionKind) -> some View {
        let ok = w.granted[k] ?? false
        return HStack(spacing: 12) {
            Image(systemName: k.symbol)
                .font(.system(size: 15, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill((ok ? Palette.disk : Palette.theme).gradient))
            VStack(alignment: .leading, spacing: 2) {
                Text(k.title).font(.headline)
                Text(k.purpose).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if ok {
                Label(L("已开启", "Granted"), systemImage: "checkmark.circle.fill").foregroundStyle(Palette.disk).font(.callout.weight(.medium))
            } else {
                Button(L("去授权", "Grant")) { w.requested.insert(k); k.request() }
                    .buttonStyle(.bordered)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.04)))
    }
}

@MainActor
enum OnboardingWindow {
    private static var window: NSWindow?

    static func show() {
        if window == nil {
            let host = NSHostingController(rootView: OnboardingView().environment(AppState.shared))
            let w = NSWindow(contentViewController: host)
            w.title = L("Peekaboo 权限设置", "Peekaboo permissions")
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    static func close() {
        window?.close()
        if WindowManager.shared.window?.isVisible != true { NSApp.setActivationPolicy(.accessory) }
    }

    /// First launch, or coming back from a restart this window triggered.
    static func showIfNeeded() {
        let d = UserDefaults.standard
        if d.bool(forKey: "reopenOnboarding") || !d.bool(forKey: "onboardingShown") {
            d.set(false, forKey: "reopenOnboarding")
            d.set(true, forKey: "onboardingShown")
            show()
        }
    }
}
