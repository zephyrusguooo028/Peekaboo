import Foundation
import AppKit
import SwiftUI

/// `Peekaboo --selftest`: prints every reading and scan result without any UI.
enum SelfTest {
    static func run() {
        let cpu = CPUReader()
        let net = NetworkReader()
        _ = cpu.read(); _ = net.read()
        let procs = ProcessReader()
        _ = procs.read()
        Thread.sleep(forTimeInterval: 1.5)

        let c = cpu.read()
        print("CPU total \(Fmt.pct(c.total, digits: 1)) user \(Fmt.pct(c.user, digits: 1)) sys \(Fmt.pct(c.system, digits: 1)) cores \(c.perCore.count) (\(cpu.summary)) letters \(cpu.coreLetters.joined())")
        let m = MemoryReader.read()
        print("MEM used \(Fmt.bytes(m.used)) / \(Fmt.bytes(m.total)) app \(Fmt.bytes(m.app)) wired \(Fmt.bytes(m.wired)) compressed \(Fmt.bytes(m.compressed)) cached \(Fmt.bytes(m.cached)) swap \(Fmt.bytes(m.swapUsed)) pressure \(m.pressureLevel)")
        let d = DiskReader.read()
        print("DISK total \(Fmt.bytes(d.total)) available \(Fmt.bytes(d.available)) free \(Fmt.bytes(d.free)) purgeable \(Fmt.bytes(d.purgeable))")
        let n = net.read()
        print("NET in \(Fmt.rate(n.inRate)) out \(Fmt.rate(n.outRate)) totals \(Fmt.bytes(n.totalIn)) / \(Fmt.bytes(n.totalOut))")
        print("GPU \(GPUReader.read().map { Fmt.pct($0) } ?? "nil")  TEMP \(TemperatureReader().read().map { String(format: "%.1f°C", $0) } ?? "nil")")
        if let b = BatteryReader.read() { print("BATTERY \(Fmt.pct(b.percent)) charging \(b.charging) ac \(b.onAC)") } else { print("BATTERY none") }
        print("UPTIME \(SystemInfo.uptimeText()) LOAD \(SystemInfo.loadAverage)")
        let snapArea = CGRect(x: 0, y: 25, width: 1920, height: 1050)
        let third = SnapAction.leftThird.target(for: .zero, in: snapArea)!
        let tr = SnapAction.topRight.target(for: .zero, in: snapArea)!
        let ctr = SnapAction.center.target(for: CGRect(x: 0, y: 0, width: 800, height: 600), in: snapArea)!
        let gbT: Int64 = 1_073_741_824
        let base = GrowthSnapshot(date: Date().addingTimeInterval(-86_400), sizes: ["/a": 5 * gbT, "/b": gbT], used: 0, seen: 0)
        let cur = GrowthSnapshot(date: Date(), sizes: ["/a": 7 * gbT, "/b": gbT, "/c": gbT / 2], used: 0, seen: 0)
        let gd = DiskGrowth.diff(cur, base)
        print("GROWTH \(gd.map { "\($0.path)+\($0.delta / 1_048_576)MB" }) ok=\(gd.map(\.path) == ["/a", "/c"])")
        print("SNAP leftThird \(third) topRight \(tr) center \(ctr) ok=\(third.width == 640 && tr == CGRect(x: 960, y: 25, width: 960, height: 525) && ctr.minX == 560)")

        let p = procs.read()
        let groups = procs.group(p).sorted { $0.memory > $1.memory }
        print("PROCS \(p.count), groups \(groups.count). Top by memory:")
        for g in groups.prefix(6) { print("   \(g.name)  \(Fmt.bytes(g.memory))  cpu \(String(format: "%.1f%%", g.cpu * 100))  ×\(g.pids.count)") }

        print("DEV CACHES:")
        for i in CleanupScanner.scanDevCaches() { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.recommended ? "推荐" : "")  \(i.subtitle)") }
        let running = CleanupScanner.runningNames()
        print("APP CACHES:")
        for i in CleanupScanner.scanAppCaches(running: running).sorted(by: { $0.size > $1.size }).prefix(8) { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.recommended ? "推荐" : "")  \(i.subtitle)") }
        print("LOGS: \(Fmt.bytes(CleanupScanner.scanLogs().reduce(0) { $0 + $1.size }))")
        print("DOWNLOADS (old):")
        for i in CleanupScanner.scanDownloads(olderThanDays: 90).sorted(by: { $0.size > $1.size }).prefix(8) { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.subtitle)") }
        let t = CleanupModel.measureTrash()
        print("TRASH \(t.readable ? Fmt.bytes(t.size) : "not readable")")

        let engine = ProjectScanEngine()
        let found = engine.findProjects(roots: AppSettings.shared.projectRootURLs)
        print("PROJECTS \(found.count):")
        for (u, tags, root) in found {
            let pr = engine.measure(u, tags: tags, root: root)
            print("   \(pr.displayName) [\(tags.joined(separator: ","))] total \(Fmt.bytes(pr.totalSize)) src \(Fmt.bytes(pr.sourceSize)) git \(Fmt.bytes(pr.gitSize)) artifacts \(Fmt.bytes(pr.artifactSize)) modified \(Fmt.relative(pr.lastModified))")
            for a in pr.artifacts.prefix(4) { print("      - \(a.url.lastPathComponent) \(Fmt.bytes(a.size)) \(a.safe ? "safe" : "UNSURE")") }
        }

        print("SAFETY home=\(CleanupModel.isSafeTarget(Paths.home)) docs=\(CleanupModel.isSafeTarget(Paths.h("Documents"))) caches=\(CleanupModel.isSafeTarget(Paths.h("Library/Caches"))) etc=\(CleanupModel.isSafeTarget(URL(fileURLWithPath: "/etc"))) npm=\(CleanupModel.isSafeTarget(Paths.h(".npm/_cacache")))")
        let rects = Treemap.layout([6, 6, 4, 3, 2, 2, 1], in: CGRect(x: 0, y: 0, width: 600, height: 400))
        let area = rects.reduce(0) { $0 + $1.width * $1.height }
        print("TREEMAP area \(Int(area)) of 240000, all inside: \(rects.allSatisfy { CGRect(x: -0.01, y: -0.01, width: 600.02, height: 400.02).contains($0) })")
    }
}

/// `Peekaboo --snapshot <dir>`: opens every page with live data and saves PNGs.
/// Used to check the UI without needing screen-recording permission.
@MainActor
enum Snapshot {
    static func run(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        Task { @MainActor in
            let app = AppState.shared
            app.monitor.wantProcesses(true)
            try? await Task.sleep(for: .seconds(5))
            for st in PopoverStyle.allCases {
                savePopover(st, to: dir.appendingPathComponent("0-popover-\(st.rawValue).png"))
            }

            OnboardingWindow.show()
            try? await Task.sleep(for: .seconds(2))
            if let v = NSApp.windows.first(where: { $0.contentViewController is NSHostingController<AnyView> || $0.title.contains("Peekaboo") && $0 !== WindowManager.shared.window })?.contentView {
                saveView(v, to: dir.appendingPathComponent("0-onboarding.png"))
            }
            OnboardingWindow.close()
            if CommandLine.arguments.contains("--popover-only") { NSApp.terminate(nil); return }
            WindowManager.shared.show(.overview)
            try? await Task.sleep(for: .seconds(2))
            saveWindow(to: dir.appendingPathComponent("1-overview.png"))

            app.tab = .disk
            app.disk.scan(Paths.home)
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("2-disk-scanning.png"))
            while app.disk.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("2-disk.png"))
            if let lib = app.disk.root?.children.first(where: { $0.name == "Library" }) {
                app.disk.open(lib)
                try? await Task.sleep(for: .seconds(1))
                saveWindow(to: dir.appendingPathComponent("2-disk-library.png"))
            }

            app.tab = .projects
            try? await Task.sleep(for: .seconds(1))
            while app.projects.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("3-projects.png"))

            app.tab = .processes
            try? await Task.sleep(for: .seconds(4))
            saveWindow(to: dir.appendingPathComponent("4-processes.png"))

            app.tab = .cleanup
            try? await Task.sleep(for: .seconds(1))
            while app.cleanup.scanning || app.projects.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("5-cleanup.png"))

            app.tab = .duplicates
            if Demo.active { DuplicatesModel.shared.scan() }   // a real scan of personal folders could take minutes
            try? await Task.sleep(for: .seconds(1))
            while DuplicatesModel.shared.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("13-duplicates.png"))

            app.tab = .uninstall
            try? await Task.sleep(for: .seconds(1))
            while UninstallModel.shared.loading { try? await Task.sleep(for: .milliseconds(500)) }
            if Demo.active, let first = UninstallModel.shared.apps.max(by: { ($0.size ?? 0) < ($1.size ?? 0) }) { UninstallModel.shared.select(first) }
            try? await Task.sleep(for: .seconds(1.5))
            saveWindow(to: dir.appendingPathComponent("14-uninstall.png"))

            TypingMonitor.shared.loadDemo()
            app.tab = .typing
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("7-typing.png"))

            app.tab = .screenshot
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("8-screenshot.png"))

            app.tab = .clipboard; try? await Task.sleep(for: .seconds(1)); saveWindow(to: dir.appendingPathComponent("10-clipboard.png"))

            app.tab = .windows; try? await Task.sleep(for: .seconds(1)); saveWindow(to: dir.appendingPathComponent("15-windows.png"))

            app.tab = .growth; try? await Task.sleep(for: .seconds(1.5)); saveWindow(to: dir.appendingPathComponent("16-growth.png"))
            do {
                let tall = NSHostingView(rootView: GrowthView().environment(AppState.shared).frame(width: 900, height: 1900))
                tall.frame = NSRect(x: 0, y: 0, width: 900, height: 1900)
                try? await Task.sleep(for: .seconds(1))
                saveView(tall, to: dir.appendingPathComponent("16-growth-full.png"))
            }

            app.tab = .battery
            try? await Task.sleep(for: .seconds(1.5))
            saveWindow(to: dir.appendingPathComponent("11-battery.png"))

            app.tab = .network
            try? await Task.sleep(for: .seconds(7))
            saveWindow(to: dir.appendingPathComponent("12-network.png"))

            app.tab = .settings
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("6-settings.png"))
            NSApp.terminate(nil)
        }
    }

    private static func saveWindow(to url: URL) {
        guard let v = WindowManager.shared.window?.contentView else { return }
        saveView(v, to: url)
    }

    private static func savePopover(_ style: PopoverStyle, to url: URL) {
        let host = NSHostingView(rootView: PopoverView(style: style).environment(AppState.shared))
        host.frame = NSRect(origin: .zero, size: host.fittingSize)
        let w = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        w.contentView = host
        w.backgroundColor = .windowBackgroundColor
        host.layoutSubtreeIfNeeded()
        saveView(host, to: url)
    }

    private static func saveView(_ v: NSView, to url: URL) {
        v.layoutSubtreeIfNeeded()
        guard let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        v.cacheDisplay(in: v.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    private static func savePNG(_ img: NSImage, scale: CGFloat, to url: URL) {
        let size = NSSize(width: img.size.width * scale, height: img.size.height * scale)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height), bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()
        img.draw(in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}

/// `Peekaboo --icon-sheet <file.png>`: every icon × every frame, on light and dark
/// menu-bar backgrounds, plus the load-tinted variant.
enum IconSheet {
    @MainActor
    static func run(to url: URL) {
        let scale: CGFloat = 4
        let icons = MenuIcon.allCases.filter { $0 != .none }
        let maxFrames = icons.map(\.frameCount).max() ?? 1
        let cellW: CGFloat = 26 * scale, cellH: CGFloat = 18 * scale + 12
        let variants: [(bg: NSColor, fg: NSColor?)] = [
            (NSColor(white: 0.94, alpha: 1), .black), (NSColor(white: 0.16, alpha: 1), .white),
            (NSColor(white: 0.16, alpha: 1), IconRenderer.tint(for: 0.7)),
        ]
        let rows = icons.count * variants.count
        let W = Int(CGFloat(maxFrames) * cellW + 110), H = Int(CGFloat(rows) * cellH)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H, bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        var row = 0
        for icon in icons {
            for v in variants {
                let y = CGFloat(H) - CGFloat(row + 1) * cellH
                v.bg.setFill(); NSRect(x: 0, y: y, width: CGFloat(W), height: cellH).fill()
                let label = v.bg.whiteComponent > 0.5 ? NSColor.black : NSColor.white
                (icon.title as NSString).draw(at: NSPoint(x: 8, y: y + cellH / 2 - 9), withAttributes: [.font: NSFont.systemFont(ofSize: 15, weight: .semibold), .foregroundColor: label])
                for f in 0..<icon.frameCount {
                    let img = IconRenderer.bitmap(icon, frame: f, cpu: 0.6, tint: v.fg, scale: scale)
                    img.draw(in: NSRect(x: 110 + CGFloat(f) * cellW, y: y + 6, width: img.size.width * scale, height: img.size.height * scale))
                }
                row += 1
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}

/// `Peekaboo --shot-demo <dir>`: renders the capture overlay and an export from a
/// synthetic screen image, so the editor can be checked without screen recording.
enum ShotDemo {
    @MainActor
    static func run(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard let screen = NSScreen.main else { return }
        let size = screen.frame.size
        let scale = screen.backingScaleFactor
        // Fake desktop: gradient plus two "windows".
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale), bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSGradient(colors: [NSColor(red: 0.3, green: 0.4, blue: 0.8, alpha: 1), NSColor(red: 0.8, green: 0.4, blue: 0.6, alpha: 1)])!.draw(in: NSRect(origin: .zero, size: size), angle: 45)
        let w1 = NSRect(x: 120, y: size.height - 520, width: 640, height: 400)
        NSColor.white.setFill(); NSBezierPath(roundedRect: w1, xRadius: 10, yRadius: 10).fill()
        ("Peekaboo 截图测试窗口\n这里有一些文字，用来测试马赛克和标注。" as NSString).draw(in: w1.insetBy(dx: 30, dy: 60), withAttributes: [.font: NSFont.systemFont(ofSize: 26), .foregroundColor: NSColor.black])
        NSColor(white: 0.15, alpha: 1).setFill(); NSBezierPath(roundedRect: NSRect(x: 820, y: size.height - 420, width: 360, height: 260), xRadius: 10, yRadius: 10).fill()
        NSGraphicsContext.restoreGraphicsState()
        guard let cg = rep.cgImage else { return }

        let s = ShotSession(screen: screen, image: cg, windowRects: [CGRect(x: 120, y: 120, width: 640, height: 400), CGRect(x: 820, y: 160, width: 360, height: 260)])
        s.selection = CGRect(x: 100, y: 100, width: 700, height: 460)
        s.phase = .editing
        s.tool = .mosaic
        s.marks = [
            ShotMark(kind: .rect(CGRect(x: 140, y: 170, width: 300, height: 60)), color: ShotPalette.colors[0], width: 3),
            ShotMark(kind: .arrow(CGPoint(x: 600, y: 480), CGPoint(x: 450, y: 220)), color: ShotPalette.colors[1], width: 4),
            ShotMark(kind: .pen((0..<40).map { CGPoint(x: 160 + Double($0) * 8, y: 420 + sin(Double($0) / 3) * 18) }), color: ShotPalette.colors[2], width: 3),
            ShotMark(kind: .mosaicPath((0..<30).map { CGPoint(x: 150 + Double($0) * 16, y: 225 + sin(Double($0) / 2) * 4) }), color: .clear, width: 26),
            ShotMark(kind: .mosaic(CGRect(x: 600, y: 380, width: 150, height: 120)), color: .clear, width: 0, blur: true),
            ShotMark(kind: .text(CGPoint(x: 470, y: 330), "这里很重要"), color: ShotPalette.colors[3], width: 28),
        ]
        s.mouse = CGPoint(x: 500, y: 300)
        let host = NSHostingView(rootView: CaptureView(session: s).environment(AppState.shared))
        host.frame = NSRect(origin: .zero, size: size)
        let win = OverlayWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        win.contentView = host
        host.layoutSubtreeIfNeeded()
        if let r = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
            host.cacheDisplay(in: host.bounds, to: r)
            try? r.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("overlay.png"))
        }
        // A selected text shows its box, the resize handle and the text options.
        s.tool = nil
        s.selectedText = s.marks.last?.id
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        host.layoutSubtreeIfNeeded()
        if let r = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
            host.cacheDisplay(in: host.bounds, to: r)
            try? r.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("overlay-text.png"))
        }
        s.restyleSelected(size: 40)
        let tiny = ShotMark(kind: .pen([CGPoint(x: 10, y: 10), CGPoint(x: 10, y: 10)]), color: .red, width: 4).isTiny
        print("text restyle size=\(s.marks.last?.width ?? 0) tinyClickDropped=\(tiny)")
        s.selectedText = nil
        // Text tool: place a field and check it receives the keyboard.
        s.tool = .text
        s.textAt = CGPoint(x: 300, y: 380)
        win.makeKeyAndOrderFront(nil)
        host.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
        ScreenshotController.focusTextField()
        RunLoop.main.run(until: Date().addingTimeInterval(0.2))
        let fr = win.firstResponder
        print("text focus: \(fr.map { String(describing: type(of: $0)) } ?? "nil") editing=\(fr is NSTextView)")
        win.orderOut(nil)
        s.textAt = nil
        if let img = s.export(), let tiff = img.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
            try? png.write(to: dir.appendingPathComponent("export.png"))
            print("export \(Int(img.size.width))x\(Int(img.size.height)) pt, \(NSBitmapImageRep(data: tiff)!.pixelsWide) px wide")
        }
    }
}
