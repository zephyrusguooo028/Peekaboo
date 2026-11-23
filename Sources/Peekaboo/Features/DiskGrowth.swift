import AppKit
import Observation

/// Free space over time, plus daily size snapshots of the places that tend to grow,
/// so Peekaboo can say what is eating the disk instead of just that it is shrinking.
struct DiskPoint: Codable { var t: Date; var free: Int64 }

struct GrowthSnapshot: Codable {
    var date: Date
    /// Path → allocated bytes. Entries under 50 MB are left out to keep the file small.
    var sizes: [String: Int64]
    /// Space in use on the disk when the snapshot was taken, and how much of it the
    /// snapshot could account for.
    var used: Int64
    var seen: Int64
    /// macOS's own volumes (system, update staging, recovery, swap): role → bytes.
    var volumes: [String: Int64] = [:]
    var updateStaged = false
}

struct GrowthItem: Identifiable {
    var id: String { path }
    let path: String
    let size: Int64
    let delta: Int64
    var name: String { WatchPoints.name(for: path) }
    var kind: WatchPoints.Kind { WatchPoints.kind(for: path) }
}

@MainActor
@Observable
final class DiskGrowth {
    static let shared = DiskGrowth()

    private(set) var history: [DiskPoint] = []
    private(set) var snapshots: [GrowthSnapshot] = []
    private(set) var scanning = false
    private(set) var scanProgress: Double = 0
    private(set) var fullDiskAccess = DiskGrowth.checkFullDiskAccess()
    var alertEnabled: Bool { didSet { UserDefaults.standard.set(alertEnabled, forKey: "alert.diskDrop") } }

    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private let historyURL = Paths.support.appendingPathComponent("disk-history.json")
    @ObservationIgnored private let snapshotURL = Paths.support.appendingPathComponent("growth.json")

    private init() {
        alertEnabled = UserDefaults.standard.object(forKey: "alert.diskDrop") as? Bool ?? true
        if Demo.active { loadDemo(); return }
        let dec = JSONDecoder()
        if let d = try? Data(contentsOf: historyURL), let h = try? dec.decode([DiskPoint].self, from: d) { history = h }
        if let d = try? Data(contentsOf: snapshotURL), let s = try? dec.decode([GrowthSnapshot].self, from: d) { snapshots = s }
    }

    func start() {
        guard !Demo.active else { return }
        record()
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in
            MainActor.assumeIsolated { DiskGrowth.shared.tick() }
        }
        // First snapshot a little after launch, then once a day.
        DispatchQueue.main.asyncAfter(deadline: .now() + 90) { DiskGrowth.shared.scanIfDue() }
    }

    private func tick() {
        record()
        scanIfDue()
        fullDiskAccess = Self.checkFullDiskAccess()
    }

    // MARK: Free space history

    private func record() {
        let d = DiskReader.read()
        guard d.total > 0 else { return }
        history.append(DiskPoint(t: Date(), free: d.available))
        let cutoff = Date().addingTimeInterval(-90 * 86_400)
        history.removeAll { $0.t < cutoff }
        if let data = try? JSONEncoder().encode(history) { try? data.write(to: historyURL, options: .atomic) }
        checkDrop()
    }

    /// Change in free space over the last `hours` (negative = disk filled up), once there
    /// is a reading at least 70 % that old.
    func freeChange(hours: Double) -> Int64? {
        guard let last = history.last else { return nil }
        let target = last.t.addingTimeInterval(-hours * 3600)
        let old = history.filter { last.t.timeIntervalSince($0.t) >= hours * 3600 * 0.7 }
            .min { abs($0.t.timeIntervalSince(target)) < abs($1.t.timeIntervalSince(target)) }
        return old.map { last.free - $0.free }
    }

    private func checkDrop() {
        guard alertEnabled, let change = freeChange(hours: 24), change < -5 * 1_073_741_824 else { return }
        let last = UserDefaults.standard.object(forKey: "alert.diskDrop.last") as? Date ?? .distantPast
        guard Date().timeIntervalSince(last) > 86_400 else { return }
        UserDefaults.standard.set(Date(), forKey: "alert.diskDrop.last")
        AlertManager.shared.notify(id: "diskdrop", title: L("磁盘一天少了 \(Fmt.bytes(-change))", "Disk lost \(Fmt.bytes(-change)) in a day"),
                                   body: L("打开「空间变化」看看是什么在长。", "Open Disk growth to see what grew."), tab: .growth)
    }

    // MARK: Snapshots

    var latest: GrowthSnapshot? { snapshots.last }

    func scanIfDue() {
        if let last = snapshots.last, Date().timeIntervalSince(last.date) < 20 * 3600 { return }
        scan()
    }

    func scan() {
        guard !scanning, !Demo.active else { return }
        scanning = true
        scanProgress = 0
        fullDiskAccess = Self.checkFullDiskAccess()
        Task.detached(priority: .utility) {
            let snap = WatchPoints.measure { p in Task { @MainActor in DiskGrowth.shared.scanProgress = p } }
            await MainActor.run { DiskGrowth.shared.finish(snap) }
        }
    }

    private func finish(_ snap: GrowthSnapshot) {
        // One snapshot per half day is plenty: a rescan replaces a recent one.
        if let last = snapshots.last, snap.date.timeIntervalSince(last.date) < 12 * 3600 { snapshots.removeLast() }
        snapshots.append(snap)
        let cutoff = Date().addingTimeInterval(-35 * 86_400)
        snapshots.removeAll { $0.date < cutoff }
        if let data = try? JSONEncoder().encode(snapshots) { try? data.write(to: snapshotURL, options: .atomic) }
        scanning = false
        scanProgress = 1
    }

    /// The snapshot to compare against for "last `hours`": the one closest to that age,
    /// as long as it is at least 70 % that old.
    func baseline(hours: Double) -> GrowthSnapshot? {
        guard let cur = snapshots.last else { return nil }
        let target = cur.date.addingTimeInterval(-hours * 3600)
        return snapshots.dropLast().filter { cur.date.timeIntervalSince($0.date) >= hours * 3600 * 0.7 }
            .min { abs($0.date.timeIntervalSince(target)) < abs($1.date.timeIntervalSince(target)) }
    }

    func growth(hours: Double) -> [GrowthItem] {
        guard let cur = snapshots.last, let base = baseline(hours: hours) else { return [] }
        return Self.diff(cur, base)
    }

    nonisolated static func diff(_ cur: GrowthSnapshot, _ base: GrowthSnapshot, min: Int64 = 100 * 1_048_576) -> [GrowthItem] {
        cur.sizes.compactMap { path, size in
            let d = size - (base.sizes[path] ?? 0)
            return d >= min ? GrowthItem(path: path, size: size, delta: d) : nil
        }
        .sorted { $0.delta > $1.delta }
    }

    func largest(_ n: Int = 15) -> [GrowthItem] {
        guard let cur = snapshots.last else { return [] }
        let base = baseline(hours: 24)
        return cur.sizes.sorted { $0.value > $1.value }.prefix(n).map { e in
            GrowthItem(path: e.key, size: e.value, delta: base.map { b in e.value - (b.sizes[e.key] ?? 0) } ?? 0)
        }
    }

    // MARK: Permission

    /// Full Disk Access can't be queried directly; reading a folder it protects tells.
    nonisolated static func checkFullDiskAccess() -> Bool {
        for p in ["Library/Safari", "Library/Mail", "Library/Messages"] {
            let u = Paths.h(p)
            if FileManager.default.fileExists(atPath: u.path) { return (try? FileManager.default.contentsOfDirectory(atPath: u.path)) != nil }
        }
        return (try? FileManager.default.contentsOfDirectory(atPath: Paths.h("Library/Containers/com.apple.stocks").path)) != nil
    }

    func refreshPermission() {
        let was = fullDiskAccess
        fullDiskAccess = Self.checkFullDiskAccess()
        if fullDiskAccess && !was { scan() }
    }

    // MARK: Demo

    private func loadDemo() {
        let gb: Int64 = 1_073_741_824
        let now = Date()
        var free = 93 * gb
        history = (0..<(30 * 24)).map { i in
            let t = now.addingTimeInterval(Double(i - 30 * 24) * 3600)
            free -= i > 25 * 24 ? gb / 3 : gb / 30
            return DiskPoint(t: t, free: free + Int64.random(in: -gb / 8...gb / 8))
        }
        let h = Paths.home.path
        func snap(_ daysAgo: Double, _ f: Double) -> GrowthSnapshot {
            let s: [String: Int64] = [
                "\(h)/Library/Containers/com.tencent.xinWeChat": Int64((18 + 9 * f) * Double(gb)),
                "\(h)/Library/Containers/com.tencent.qq": Int64((7 + 4 * f) * Double(gb)),
                "\(h)/Downloads": Int64((15 + 2.4 * f) * Double(gb)),
                "\(h)/Library/Caches/com.google.Chrome": Int64((1.2 + 1.1 * f) * Double(gb)),
                "\(h)/Library/Developer": Int64((6 + 1.8 * f) * Double(gb)),
                "\(h)/Documents": 12 * gb, "/Applications/Xcode.app": 11 * gb, "\(h)/Movies": 9 * gb,
                "\(h)/Library/Application Support/Claude": Int64((2 + 0.7 * f) * Double(gb)),
                "\(h)/.npm": Int64(0.7 * Double(gb)),
            ]
            return GrowthSnapshot(date: now.addingTimeInterval(-daysAgo * 86_400), sizes: s, used: 186 * gb, seen: 104 * gb,
                                  volumes: ["System": 19 * gb, "Preboot": 21 * gb, "Recovery": 4 * gb, "VM": 4 * gb], updateStaged: true)
        }
        snapshots = [snap(7, 0), snap(3, 0.5), snap(1, 0.8), snap(0, 1)]
    }
}

/// The folders measured for each snapshot, and readable names for them.
enum WatchPoints {
    enum Kind { case chat, app, cache, user, system, developer }

    static let gb: Int64 = 1_073_741_824

    /// Disjoint set of folders whose sizes add up to (most of) what the user owns.
    static func roots() -> [URL] {
        let fm = FileManager.default
        let home = Paths.home
        func children(_ u: URL, except: Set<String> = []) -> [URL] {
            ((try? fm.contentsOfDirectory(at: u, includingPropertiesForKeys: nil, options: [])) ?? [])
                .filter { !except.contains($0.lastPathComponent) }
        }
        let lib = home.appendingPathComponent("Library")
        let expanded: Set<String> = ["Application Support", "Containers", "Group Containers", "Caches", "Developer"]
        var r = children(home, except: ["Library", ".Trash"])
        r += children(lib, except: expanded)
        for e in expanded { r += children(lib.appendingPathComponent(e)) }
        r.append(home.appendingPathComponent(".Trash"))
        r += children(URL(fileURLWithPath: "/Applications"))
        // Per-user system caches and temporary files. The X folder is left out: it holds
        // APFS clones of apps (e.g. 280 copies of Chrome) that share their data, so adding
        // up their sizes reports hundreds of GB that don't really exist.
        let tmpParent = fm.temporaryDirectory.deletingLastPathComponent()
        r += ["T", "C"].map { tmpParent.appendingPathComponent($0) }
        r += ["/Library/Caches", "/Library/Application Support", "/System/Volumes/Data/System/Library/AssetsV2", "/Users/Shared"]
            .map { URL(fileURLWithPath: $0) }
        return r.filter { fm.fileExists(atPath: $0.path) }
    }

    /// Sizes every root. `progress` is called with 0…1.
    static func measure(progress: @escaping @Sendable (Double) -> Void) -> GrowthSnapshot {
        let list = roots()
        let sizes = UnsafeMutableBufferPointer<Int64>.allocate(capacity: list.count)
        sizes.initialize(repeating: 0)
        let done = Counter()
        let ptr = SendablePtr(sizes)
        DispatchQueue.concurrentPerform(iterations: list.count) { i in
            ptr.p[i] = FileSizer.size(of: list[i])
            let n = done.increment()
            if n % 20 == 0 { progress(Double(n) / Double(list.count)) }
        }
        var map: [String: Int64] = [:]
        var seen: Int64 = 0
        for (i, u) in list.enumerated() {
            seen += sizes[i]
            if sizes[i] >= 50 * 1_048_576 { map[u.path] = sizes[i] }
        }
        sizes.deallocate()
        let d = DiskReader.read()
        let vols = volumes()
        return GrowthSnapshot(date: Date(), sizes: map, used: d.total - d.free, seen: seen, volumes: vols, updateStaged: updateStaged(preboot: vols["Preboot"]))
    }

    /// Space each of macOS's own volumes uses, from `diskutil apfs list -plist`.
    static func volumes() -> [String: Int64] {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/diskutil")
        p.arguments = ["apfs", "list", "-plist"]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [:] }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let containers = plist["Containers"] as? [[String: Any]] else { return [:] }
        // The container holding the Data volume is the main disk.
        for c in containers {
            let vols = c["Volumes"] as? [[String: Any]] ?? []
            guard vols.contains(where: { ($0["Roles"] as? [String])?.contains("Data") == true }) else { continue }
            var out: [String: Int64] = [:]
            for v in vols {
                let role = (v["Roles"] as? [String])?.first ?? "System"
                let used = (v["CapacityInUse"] as? NSNumber)?.int64Value ?? 0
                out[role.isEmpty ? "System" : role, default: 0] += used
            }
            return out
        }
        return [:]
    }

    /// A downloaded macOS update waiting to install: a big Preboot volume, or the update snapshot.
    static func updateStaged(preboot: Int64?) -> Bool {
        if (preboot ?? 0) > 12 * gb { return true }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/tmutil")
        p.arguments = ["listlocalsnapshots", "/"]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return false }
        let out = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        p.waitUntilExit()
        return out.contains("MSUPrepareUpdate")
    }

    // MARK: Names

    private static let known: [(String, String, String, Kind)] = [
        ("xinwechat", "微信", "WeChat", .chat), ("com.tencent.qq", "QQ", "QQ", .chat), ("com.tencent", "腾讯应用", "Tencent apps", .chat),
        ("dingtalk", "钉钉", "DingTalk", .chat), ("lark", "飞书", "Lark", .chat), ("feishu", "飞书", "Feishu", .chat),
        ("telegram", "Telegram", "Telegram", .chat), ("whatsapp", "WhatsApp", "WhatsApp", .chat), ("slack", "Slack", "Slack", .chat),
        ("discord", "Discord", "Discord", .chat), ("com.apple.mail", "邮件", "Mail", .chat), ("/library/messages", "信息附件", "Messages", .chat),
        ("mobilesync", "iPhone / iPad 备份", "iPhone / iPad backups", .user),
        ("com.docker", "Docker", "Docker", .developer), ("/library/developer", "Xcode 与模拟器", "Xcode and simulators", .developer),
        ("google.chrome.code_sign_clone", "Chrome 临时副本", "Chrome temporary copies", .system),
        ("com.google.chrome", "Chrome", "Chrome", .app), ("/.npm", "npm 缓存", "npm cache", .developer), ("/.cache", "开发工具缓存", "Tool caches", .developer),
        ("/.gradle", "Gradle 缓存", "Gradle cache", .developer), ("/.cargo", "Rust 工具链", "Rust toolchain", .developer),
        ("/.rustup", "Rust 工具链", "Rust toolchain", .developer), ("/.nuget", "NuGet 缓存", "NuGet cache", .developer),
        ("/.trash", "废纸篓", "Trash", .user), ("photos library", "照片图库", "Photos library", .user),
        ("assetsv2", "系统资源（语音、字体、AI 模型）", "System assets (voices, fonts, AI models)", .system),
    ]

    static func kind(for path: String) -> Kind {
        let p = path.lowercased()
        if let k = known.first(where: { p.contains($0.0) }) { return k.3 }
        if p.contains("/library/caches") || p.hasSuffix("/c") || p.hasSuffix("/t") { return .cache }
        if p.hasPrefix("/library") || p.hasPrefix("/system") || p.hasPrefix("/private") || p.hasSuffix("/x") { return .system }
        if p.contains("/library/") || p.hasPrefix("/applications") { return .app }
        return .user
    }

    static func name(for path: String) -> String {
        let p = path.lowercased()
        let u = URL(fileURLWithPath: path)
        let suffix: String = {
            if p.contains("/library/caches/") { return L("缓存", " cache") }
            if p.contains("/containers/") || p.contains("/application support/") { return L("数据", " data") }
            return ""
        }()
        if let k = known.first(where: { p.contains($0.0) }) { return L(k.1, k.2) + suffix }
        let tmp = FileManager.default.temporaryDirectory.deletingLastPathComponent().path.lowercased()
        if p.hasPrefix(tmp) {
            switch u.lastPathComponent {
            case "T": return L("临时文件", "Temporary files")
            case "C": return L("系统缓存（当前用户）", "System caches (this user)")
            default: return L("应用临时副本（代码签名）", "App temporary copies (code signing)")
            }
        }
        if p == "/library/caches" { return L("系统缓存（所有用户）", "System caches (all users)") }
        if p == "/library/application support" { return L("系统级应用数据（所有用户）", "App data (all users)") }
        // Bundle-id folders: use the app's name if it is installed.
        let last = u.lastPathComponent
        if last.contains("."), let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: last) ?? groupApp(last) {
            return FileManager.default.displayName(atPath: app.path).replacingOccurrences(of: ".app", with: "") + suffix
        }
        return FileManager.default.displayName(atPath: path) + suffix
    }

    /// Group containers are named "TEAMID.bundle.id".
    private static func groupApp(_ folder: String) -> URL? {
        let parts = folder.split(separator: ".", maxSplits: 1)
        guard parts.count == 2, parts[0].count == 10 else { return nil }
        return NSWorkspace.shared.urlForApplication(withBundleIdentifier: String(parts[1]))
    }
}

private final class Counter: @unchecked Sendable {
    private var n = 0
    private let lock = NSLock()
    func increment() -> Int { lock.lock(); defer { lock.unlock() }; n += 1; return n }
}

private struct SendablePtr: @unchecked Sendable { let p: UnsafeMutableBufferPointer<Int64>; init(_ p: UnsafeMutableBufferPointer<Int64>) { self.p = p } }
