import SwiftUI

struct MainView: View {
    @Environment(AppState.self) private var app

    var body: some View {
        @Bindable var app = app
        NavigationSplitView {
            List(selection: Binding(get: { app.tab }, set: { if let t = $0 { app.tab = t } })) {
                Section { SidebarBrand() }
                    .listRowSeparator(.hidden)
                Section(L("监控", "Monitor")) {
                    SidebarRow(tab: .overview)
                    SidebarRow(tab: .processes)
                    SidebarRow(tab: .battery)
                    SidebarRow(tab: .network)
                }
                Section(L("空间", "Storage")) {
                    SidebarRow(tab: .disk)
                    SidebarRow(tab: .growth)
                    SidebarRow(tab: .projects)
                    SidebarRow(tab: .cleanup)
                    SidebarRow(tab: .duplicates)
                    SidebarRow(tab: .uninstall)
                }
                Section(L("工具", "Tools")) {
                    SidebarRow(tab: .screenshot)
                    SidebarRow(tab: .clipboard)
                    SidebarRow(tab: .windows)
                    SidebarRow(tab: .typing)
                }
                Section {
                    SidebarRow(tab: .settings)
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 260)
            .safeAreaInset(edge: .bottom) { SidebarFooter().padding(12) }
        } detail: {
            Group {
                switch app.tab {
                case .overview: OverviewView()
                case .disk: DiskView()
                case .projects: ProjectsView()
                case .processes: ProcessesView()
                case .cleanup: CleanupView()
                case .typing: TypingView()
                case .screenshot: ScreenshotView()
                case .battery: BatteryView()
                case .network: NetworkView()
                case .duplicates: DuplicatesView()
                case .uninstall: UninstallView()
                case .clipboard: ClipboardView()
                case .windows: WindowSnapView()
                case .growth: GrowthView()
                case .settings: SettingsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .tint(Palette.theme)
    }
}

extension MainTab {
    /// Each tab gets its own colour tile, like System Settings.
    var tileColor: Color {
        switch self {
        case .overview: return Palette.cpu
        case .processes: return Color(red: 0.36, green: 0.42, blue: 0.55)
        case .disk: return Palette.disk
        case .projects: return Palette.netOut
        case .cleanup: return Color(red: 0.93, green: 0.36, blue: 0.55)
        case .typing: return Palette.mem
        case .screenshot: return Color(red: 0.25, green: 0.70, blue: 0.85)
        case .battery: return Color(red: 0.30, green: 0.75, blue: 0.35)
        case .network: return Palette.netIn
        case .duplicates: return Color(red: 0.85, green: 0.55, blue: 0.20)
        case .uninstall: return Color(red: 0.80, green: 0.30, blue: 0.35)
        case .clipboard: return Color(red: 0.45, green: 0.50, blue: 0.95)
        case .windows: return Color(red: 0.20, green: 0.62, blue: 0.62)
        case .growth: return Color(red: 0.95, green: 0.45, blue: 0.25)
        case .settings: return Color(red: 0.55, green: 0.56, blue: 0.60)
        }
    }
}

private struct SidebarBrand: View {
    @Environment(AppState.self) private var app
    var body: some View {
        let icon = app.settings.menuIcon == .none ? MenuIcon.ghost : app.settings.menuIcon
        HStack(spacing: 10) {
            AnimatedIconView(icon: icon, height: 20)
                .frame(width: 34, height: 34)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Palette.theme.opacity(0.14)))
            VStack(alignment: .leading, spacing: 1) {
                Text("Peekaboo").font(.system(size: 14, weight: .semibold, design: .rounded))
                Text(statusSentence(app.monitor, app.settings)).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.vertical, 4)
        .selectionDisabled()
        .onAppear { IconAnimator.shared.viewer(true) }
        .onDisappear { IconAnimator.shared.viewer(false) }
    }
}

private struct SidebarRow: View {
    let tab: MainTab
    @Environment(AppState.self) private var app

    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: tab.symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(tab.tileColor.gradient))
            Text(tab.title)
            Spacer(minLength: 4)
            if let v = value {
                Text(v.text)
                    .font(.system(size: 10.5, weight: .medium)).monospacedDigit()
                    .foregroundStyle(v.alert ? .white : .secondary)
                    .padding(.horizontal, 6).padding(.vertical, 1.5)
                    .background(Capsule().fill(v.alert ? Palette.danger : Color.primary.opacity(0.07)))
                    .contentTransition(.numericText())
                    .animation(.easeOut(duration: 0.3), value: v.text)
            }
        }
        .padding(.vertical, 2)
        .tag(tab)
    }

    /// Live number shown on the right of each row.
    private var value: (text: String, alert: Bool)? {
        let m = app.monitor
        switch tab {
        case .overview: return (Fmt.pct(m.cpu.total), m.cpu.total > 0.9)
        case .processes: return (Fmt.pct(m.memory.usedFraction), m.memory.pressureLevel >= 4)
        case .disk:
            let low = m.disk.total > 0 && Double(m.disk.available) < Double(app.settings.alertDiskGB) * 1_073_741_824
            let free = MenuBarRenderer.diskText(m.disk.available)
            return (L("剩 " + free, free + " free"), low)
        case .projects: return app.projects.projects.isEmpty ? nil : ("\(app.projects.projects.count)", false)
        case .cleanup:
            let r = app.cleanup.recommendedSize
            return r > 0 ? (Fmt.bytes(r), false) : nil
        case .typing:
            return TypingMonitor.shared.running ? ("\(TypingMonitor.shared.today.total)", false) : nil
        case .screenshot: return app.settings.shotHotkey.map { ($0.title, false) }
        case .battery: return BatteryView.badge.map { ($0, false) }
        case .network: return NetworkView.badge.map { ($0, false) }
        case .duplicates: return DuplicatesView.badge.map { ($0, false) }
        case .uninstall: return nil
        case .clipboard: return ClipboardView.badge.map { ($0, false) }
        case .growth: return GrowthView.badge
        case .windows: return WindowSnapper.shared.trusted || Demo.active ? nil : (L("未授权", "Off"), false)
        case .settings: return Updater.shared.hasUpdate ? (L("新版本", "Update"), true) : nil
        }
    }
}

private struct SidebarFooter: View {
    @Environment(AppState.self) private var app
    var body: some View {
        let d = app.monitor.disk
        let mem = app.monitor.memory
        VStack(alignment: .leading, spacing: 8) {
            meter("internaldrive", L("磁盘", "Disk"), d.usedFraction, L("\(MenuBarRenderer.diskText(d.available)) 可用", "\(MenuBarRenderer.diskText(d.available)) free"),
                  d.usedFraction > 0.9 ? Palette.danger : Palette.disk)
            meter("memorychip", L("内存", "Memory"), mem.usedFraction, Fmt.pct(mem.usedFraction),
                  mem.pressureLevel >= 4 ? Palette.danger : Palette.mem)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.04)))
    }

    private func meter(_ symbol: String, _ title: String, _ v: Double, _ value: String, _ c: Color) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 4) {
                Image(systemName: symbol).font(.system(size: 10)).foregroundStyle(.secondary)
                Text(title).font(.system(size: 10.5, weight: .medium))
                Spacer()
                Text(value).font(.system(size: 10.5)).foregroundStyle(.secondary).monospacedDigit()
            }
            SizeBar(fraction: v, color: c)
        }
    }
}
