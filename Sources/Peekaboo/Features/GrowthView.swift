import SwiftUI
import Charts

/// Sidebar page: how free space changed, and which folders grew.
struct GrowthView: View {
    @MainActor static var badge: (String, Bool)? {
        guard let c = DiskGrowth.shared.freeChange(hours: 24), abs(c) >= 1_073_741_824 else { return nil }
        return ((c < 0 ? "−" : "+") + MenuBarRenderer.diskText(abs(c)), c < -5 * 1_073_741_824)
    }

    @Local private var span = 24.0
    @Local private var timer: Timer?

    var body: some View {
        let g = DiskGrowth.shared
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("空间变化", "Disk growth"), subtitle: subtitle(g)) {
                    Button { g.scan() } label: {
                        if g.scanning { Label(L("扫描中 \(Int(g.scanProgress * 100))%", "Scanning \(Int(g.scanProgress * 100))%"), systemImage: "hourglass") }
                        else { Label(L("立即扫描", "Scan now"), systemImage: "arrow.clockwise") }
                    }
                    .disabled(g.scanning).controlSize(.large)
                }

                HStack(alignment: .top, spacing: 16) {
                    stat(L("24 小时", "24 hours"), g.freeChange(hours: 24))
                    stat(L("7 天", "7 days"), g.freeChange(hours: 24 * 7))
                    stat(L("30 天", "30 days"), g.freeChange(hours: 24 * 30))
                }

                Card(L("可用空间", "Free space"), symbol: "chart.xyaxis.line") { chart(g) }

                if !g.fullDiskAccess && !Demo.active { permissionCard }

                growingCard(g)

                if let snap = g.latest { breakdownCard(snap) }

                Text(L("Peekaboo 每小时记一次可用空间，每天量一次各个文件夹的大小，只记大小，不读文件内容。一天少了 5 GB 以上会提醒你（设置 › 提醒）。",
                       "Peekaboo records free space hourly and folder sizes daily: sizes only, never file contents. You get a notification when the disk loses more than 5 GB in a day (Settings › Alerts)."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(24)
        }
        .onAppear {
            g.refreshPermission()
            timer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { _ in MainActor.assumeIsolated { DiskGrowth.shared.refreshPermission() } }
        }
        .onDisappear { timer?.invalidate(); timer = nil }
    }

    private func subtitle(_ g: DiskGrowth) -> String {
        let free = MenuBarRenderer.diskText(AppState.shared.monitor.disk.available)
        if let first = g.history.first {
            return L("现在可用 \(free) · 从 \(Fmt.relative(first.t)) 开始记录", "\(free) free now · recording since \(Fmt.relative(first.t))")
        }
        return L("现在可用 \(free)", "\(free) free now")
    }

    private func stat(_ title: String, _ change: Int64?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            if let c = change {
                Text((c < 0 ? "−" : "+") + Fmt.bytes(abs(c)))
                    .font(.system(size: 22, weight: .semibold, design: .rounded)).monospacedDigit()
                    .foregroundStyle(c < -1_073_741_824 ? Palette.danger : c > 0 ? Palette.disk : .primary)
                Text(c < 0 ? L("可用空间减少", "less free space") : L("可用空间增加", "more free space")).font(.caption2).foregroundStyle(.secondary)
            } else {
                Text("—").font(.system(size: 22, weight: .semibold, design: .rounded)).foregroundStyle(.tertiary)
                Text(L("记录还不够长", "Not enough history yet")).font(.caption2).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.primary.opacity(0.06)))
    }

    @ViewBuilder
    private func chart(_ g: DiskGrowth) -> some View {
        let pts = g.history.suffix(30 * 24)
        if pts.count < 2 {
            Text(L("刚开始记录，几个小时后这里会出现曲线。", "Just started recording; the chart appears after a few hours."))
                .foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 120)
        } else {
            Chart(Array(pts.enumerated()), id: \.offset) { _, p in
                AreaMark(x: .value("t", p.t), y: .value("GB", Double(p.free) / 1e9))
                    .foregroundStyle(LinearGradient(colors: [Palette.disk.opacity(0.3), Palette.disk.opacity(0.02)], startPoint: .top, endPoint: .bottom))
                LineMark(x: .value("t", p.t), y: .value("GB", Double(p.free) / 1e9))
                    .foregroundStyle(Palette.disk)
            }
            .chartYAxis { AxisMarks { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Double.self) { Text("\(Int(d)) GB") } } } }
            .frame(height: 170)
        }
    }

    private var permissionCard: some View {
        HStack(spacing: 12) {
            Image(systemName: "lock.shield").font(.system(size: 22)).foregroundStyle(Palette.warn)
            VStack(alignment: .leading, spacing: 2) {
                Text(L("开启「完全磁盘访问」才能看全", "Turn on Full Disk Access to see everything")).font(.headline)
                Text(L("微信、QQ、照片、邮件等应用的数据在 macOS 保护的文件夹里，没有这个权限时 Peekaboo 量出来是 0。只读大小，不读内容。",
                       "WeChat, QQ, Photos, Mail and other apps keep their data in folders macOS protects; without this permission they measure as 0. Sizes only, never contents."))
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 6) {
                Button(L("去开启", "Turn on")) { PermissionKind.fullDisk.request() }.buttonStyle(.borderedProminent)
                Button(L("已开启，重启生效", "Done, restart")) { AppRelaunch.now() }.controlSize(.small)
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Palette.warn.opacity(0.1)))
    }

    private func growingCard(_ g: DiskGrowth) -> some View {
        let items = g.growth(hours: span)
        return Card(L("最近在长", "Growing"), symbol: "arrow.up.forward.circle", trailing: {
            Picker("", selection: Binding(get: { span }, set: { span = $0 })) {
                Text(L("24 小时", "24 h")).tag(24.0)
                Text(L("7 天", "7 days")).tag(24.0 * 7)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 160)
        }) {
            if g.latest == nil {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text(g.scanning ? L("正在量各个文件夹的大小…", "Measuring folders…") : L("第一次扫描会在启动一分多钟后自动开始，也可以点「立即扫描」。", "The first scan starts a minute or so after launch, or click Scan now."))
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, minHeight: 80)
            } else if items.isEmpty {
                let base = g.baseline(hours: span) == nil
                Text(base ? L("要对比需要更早的记录：每天会自动量一次，\(span > 24 ? "7 天" : "明天")后这里就能看出谁在长。下面先列出现在最大的。",
                               "Comparing needs older snapshots: one is taken every day, so check back \(span > 24 ? "in a week" : "tomorrow"). Here's what's largest now.")
                          : L("这段时间没有哪个文件夹长了 100 MB 以上。", "No folder grew by more than 100 MB in this period."))
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Divider()
                Text(L("目前最大", "Largest now")).font(.subheadline.weight(.semibold))
                ForEach(g.largest()) { row($0, showDelta: false) }
            } else {
                ForEach(items.prefix(15)) { row($0, showDelta: true) }
            }
        }
    }

    private func row(_ it: GrowthItem, showDelta: Bool) -> some View {
        HStack(spacing: 10) {
            Image(systemName: symbol(it.kind))
                .font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 24, height: 24)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(color(it.kind).gradient))
            VStack(alignment: .leading, spacing: 1) {
                Text(it.name).lineLimit(1)
                Text(it.path.replacingOccurrences(of: Paths.home.path, with: "~")).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            Text(Fmt.bytes(it.size)).monospacedDigit().foregroundStyle(.secondary)
            if showDelta || it.delta >= 100 * 1_048_576 {
                Text("+" + Fmt.bytes(it.delta)).monospacedDigit().fontWeight(.semibold)
                    .foregroundStyle(it.delta > 1_073_741_824 ? Palette.danger : Palette.warn)
                    .frame(minWidth: 80, alignment: .trailing)
            }
            Button { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: it.path)]) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(.borderless).help(L("在访达中显示", "Show in Finder"))
        }
        .padding(.vertical, 2)
    }

    private func breakdownCard(_ s: GrowthSnapshot) -> some View {
        let gb: Int64 = 1_073_741_824
        let sys = s.volumes.filter { $0.key != "Data" }.values.reduce(0, +)
        let unseen = max(0, s.used - s.seen - sys)
        return Card(L("空间都去哪了", "Where the space goes"), symbol: "internaldrive", trailing: {
            Text(L("扫描于 \(Fmt.relative(s.date))", "Scanned \(Fmt.relative(s.date))")).font(.caption).foregroundStyle(.secondary)
        }) {
            let total = max(s.used, s.seen + sys + unseen, 1)
            GeometryReader { geo in
                HStack(spacing: 2) {
                    seg(Double(s.seen) / Double(total), Palette.disk, geo.size.width)
                    seg(Double(sys) / Double(total), Palette.cpu, geo.size.width)
                    seg(Double(unseen) / Double(total), Color.gray.opacity(0.45), geo.size.width)
                }
            }
            .frame(height: 14)
            legend(Palette.disk, L("Peekaboo 量到的文件", "Files Peekaboo measured"), s.seen)
            legend(Palette.cpu, L("macOS 自身（系统、启动和更新文件、恢复、交换）", "macOS itself (system, boot & update files, recovery, swap)"), sys)
            legend(Color.gray.opacity(0.45), L("看不到的空间", "Not accounted for"), unseen)
            if unseen > 5 * gb {
                Text(fullDiskHint)
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if s.updateStaged {
                Divider()
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: "arrow.down.app").font(.system(size: 18)).foregroundStyle(Palette.cpu)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(L("有一个 macOS 更新已下载，等待安装（约 \(Fmt.bytes(max(0, (s.volumes["Preboot"] ?? 0) - 6 * gb))) ）", "A macOS update is downloaded and waiting to install (about \(Fmt.bytes(max(0, (s.volumes["Preboot"] ?? 0) - 6 * gb))))")).font(.headline)
                        Text(L("别手动删：它在系统保护的分区里。装上更新（系统设置 › 通用 › 软件更新）后会自动释放；不想更新可以关掉自动下载，macOS 会在空间紧张时自己清掉。",
                               "Don't delete it by hand: it lives on a protected system volume. Installing the update (System Settings › General › Software Update) frees it; or turn off automatic downloads and macOS removes it when space runs low."))
                            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        Button(L("打开软件更新", "Open Software Update")) {
                            if let u = URL(string: "x-apple.systempreferences:com.apple.Software-Update-Settings.extension") { NSWorkspace.shared.open(u) }
                        }
                        .controlSize(.small).padding(.top, 2)
                    }
                }
            }
        }
    }

    private var fullDiskHint: String {
        DiskGrowth.shared.fullDiskAccess || Demo.active
            ? L("看不到的部分通常是其他用户的文件、本地快照（时间机器 / 系统更新）和系统自己的数据库。",
                "The rest is usually other users' files, local snapshots (Time Machine / updates) and system databases.")
            : L("看不到的部分大多在受保护的应用数据里（微信、QQ、照片、邮件…）。开启「完全磁盘访问」后会按应用列出来。",
                "Most of the rest is in protected app data (WeChat, QQ, Photos, Mail…). Turn on Full Disk Access to list it by app.")
    }

    private func seg(_ f: Double, _ c: Color, _ w: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 3).fill(c).frame(width: max(0, w * CGFloat(min(max(f, 0), 1)) - 2))
    }

    private func legend(_ c: Color, _ t: String, _ v: Int64) -> some View {
        HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 2).fill(c).frame(width: 10, height: 10)
            Text(t)
            Spacer()
            Text(Fmt.bytes(v)).monospacedDigit().foregroundStyle(.secondary)
        }
        .font(.callout)
    }

    private func symbol(_ k: WatchPoints.Kind) -> String {
        switch k {
        case .chat: return "bubble.left.and.bubble.right.fill"
        case .app: return "app.fill"
        case .cache: return "archivebox.fill"
        case .user: return "folder.fill"
        case .system: return "gearshape.fill"
        case .developer: return "hammer.fill"
        }
    }

    private func color(_ k: WatchPoints.Kind) -> Color {
        switch k {
        case .chat: return Color(red: 0.2, green: 0.72, blue: 0.4)
        case .app: return Palette.netIn
        case .cache: return Color(red: 0.85, green: 0.55, blue: 0.2)
        case .user: return Palette.disk
        case .system: return Color(red: 0.55, green: 0.56, blue: 0.6)
        case .developer: return Palette.mem
        }
    }
}
