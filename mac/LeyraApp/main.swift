// Leyra for macOS: the window. Shows the same interface as Windows (client-assets) in WebKit and passes its
// commands to the background service over /var/run/leyra/agent.sock. Security decisions stay in the service.
import AppKit
import WebKit
import UserNotifications

let socketPath = "/var/run/leyra/agent.sock"

// MARK: - Talking to the background service (4-byte little-endian length + JSON, as on Windows)

enum ServiceError: Error { case message(String) }

func serviceSocketTrusted() -> Bool {
    // The folder holding the socket must belong to root and not be writable by others: only the service can create it.
    guard let attrs = try? FileManager.default.attributesOfItem(atPath: "/var/run/leyra"),
          let owner = attrs[.ownerAccountID] as? NSNumber, owner.intValue == 0,
          let perms = attrs[.posixPermissions] as? NSNumber else { return false }
    return perms.intValue & 0o022 == 0
}

func askService(_ request: Any, timeout: TimeInterval = 110) throws -> Any {
    guard serviceSocketTrusted() else { throw ServiceError.message("后台服务没有运行，请重新安装 Leyra") }
    let fd = socket(AF_UNIX, SOCK_STREAM, 0)
    guard fd >= 0 else { throw ServiceError.message("无法连接后台服务") }
    defer { close(fd) }
    var tv = timeval(tv_sec: Int(timeout), tv_usec: 0)
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    var addr = sockaddr_un()
    addr.sun_family = sa_family_t(AF_UNIX)
    let pathBytes = Array(socketPath.utf8)
    withUnsafeMutableBytes(of: &addr.sun_path) { raw in
        for (i, b) in pathBytes.enumerated() where i < raw.count - 1 { raw[i] = b }
    }
    let ok = withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
    }
    guard ok == 0 else { throw ServiceError.message("后台服务没有运行，请重新打开 Leyra") }
    let body = try JSONSerialization.data(withJSONObject: ["version": 1, "request": request])
    var length = UInt32(body.count).littleEndian
    var frame = Data(bytes: &length, count: 4); frame.append(body)
    try frame.withUnsafeBytes { p in
        var sent = 0
        while sent < frame.count {
            let n = send(fd, p.baseAddress! + sent, frame.count - sent, 0)
            if n <= 0 { throw ServiceError.message("与后台服务的连接中断") }
            sent += n
        }
    }
    func readExact(_ count: Int) throws -> Data {
        var out = Data(); var buf = [UInt8](repeating: 0, count: 65536)
        while out.count < count {
            let n = recv(fd, &buf, min(buf.count, count - out.count), 0)
            if n <= 0 { throw ServiceError.message("后台服务没有回应") }
            out.append(buf, count: n)
        }
        return out
    }
    let head = try readExact(4)
    let size = Int(head.withUnsafeBytes { $0.load(as: UInt32.self) }.littleEndian)
    guard size > 0 && size < 16 * 1024 * 1024 else { throw ServiceError.message("后台服务回应无效") }
    return try JSONSerialization.jsonObject(with: try readExact(size))
}

// MARK: - Small helpers

@discardableResult func run(_ path: String, _ args: [String]) -> Int32 {
    let p = Process(); p.executableURL = URL(fileURLWithPath: path); p.arguments = args
    do { try p.run(); p.waitUntilExit(); return p.terminationStatus } catch { return -1 }
}

/// Runs a command as administrator with the standard macOS password prompt.
func runAsAdmin(_ command: String) -> Bool {
    let escaped = command.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    let script = "do shell script \"\(escaped)\" with administrator privileges"
    var error: NSDictionary?
    NSAppleScript(source: script)?.executeAndReturnError(&error)
    return error == nil
}

func openURL(_ s: String?) { if let s = s, let u = URL(string: s), ["http", "https"].contains(u.scheme ?? "") { NSWorkspace.shared.open(u) } }

// MARK: - The window

final class AppDelegate: NSObject, NSApplicationDelegate, WKScriptMessageHandler, NSWindowDelegate {
    var window: NSWindow!
    var web: WKWebView!
    var statusItem: NSStatusItem!
    var trayActions: [String] = []
    let queue = DispatchQueue(label: "leyra.service", attributes: .concurrent)

    func applicationDidFinishLaunching(_ n: Notification) {
        let config = WKWebViewConfiguration()
        let shim = """
        (() => { const L = [];
          window.__leyraDeliver = d => L.forEach(f => f({ data: d }));
          window.chrome = { webview: { addEventListener: (k, f) => L.push(f),
            postMessage: m => window.webkit.messageHandlers.leyra.postMessage(m) } }; })();
        """
        config.userContentController.addUserScript(WKUserScript(source: shim, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        config.userContentController.add(self, name: "leyra")
        web = WKWebView(frame: .zero, configuration: config)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 720),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Leyra"; window.minSize = NSSize(width: 860, height: 560)
        window.contentView = web; window.delegate = self; window.center(); window.isReleasedWhenClosed = false
        let assets = Bundle.main.resourceURL!.appendingPathComponent("client-assets")
        web.loadFileURL(assets.appendingPathComponent("win/index.html"), allowingReadAccessTo: assets)
        window.makeKeyAndOrderFront(nil)
        setupMainMenu()
        setupStatusItem()
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Copy, paste, select all and undo only work in a Mac app when its menu bar has an Edit menu with these
    /// standard actions (the keyboard shortcuts are routed through it).
    func setupMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "关于 Leyra", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏 Leyra", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "退出界面（后台继续运行）", action: #selector(quitApp), keyEquivalent: "q")
        appItem.submenu = appMenu
        let editItem = NSMenuItem(); main.addItem(editItem)
        let edit = NSMenu(title: "编辑")
        edit.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "z"); redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        let windowItem = NSMenuItem(); main.addItem(windowItem)
        let windowMenu = NSMenu(title: "窗口")
        windowMenu.addItem(withTitle: "最小化", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "关闭窗口", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowItem.submenu = windowMenu
        NSApp.mainMenu = main; NSApp.windowsMenu = windowMenu
    }

    func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "Leyra"
        let menu = NSMenu()
        menu.addItem(withTitle: "打开主窗口", action: #selector(showMain), keyEquivalent: "")
        menu.addItem(withTitle: "刷新连接", action: #selector(trayRefresh), keyEquivalent: "")
        menu.addItem(withTitle: "恢复防护", action: #selector(trayResume), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出界面（后台继续运行）", action: #selector(quitApp), keyEquivalent: "q")
        statusItem.menu = menu
    }
    @objc func showMain() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func trayRefresh() { showMain(); trayActions.append("refresh") }
    @objc func trayResume() { showMain(); trayActions.append("resume") }
    @objc func quitApp() { NSApp.terminate(nil) }
    func windowShouldClose(_ sender: NSWindow) -> Bool { sender.orderOut(nil); return false }   // stays in the menu bar
    func applicationShouldHandleReopen(_ s: NSApplication, hasVisibleWindows: Bool) -> Bool { showMain(); return true }

    // MARK: commands from the interface

    func userContentController(_ c: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let m = message.body as? [String: Any], let id = m["id"], let command = m["command"] as? String else { return }
        let args = m["args"] as? [String: Any] ?? [:]
        queue.async {
            var reply: [String: Any]
            do { reply = try self.handle(command, args) } catch ServiceError.message(let s) { reply = ["ok": false, "error": s] } catch { reply = ["ok": false, "error": "\(error.localizedDescription)"] }
            reply["id"] = id
            guard let data = try? JSONSerialization.data(withJSONObject: reply), let json = String(data: data, encoding: .utf8) else { return }
            DispatchQueue.main.async { self.web.evaluateJavaScript("window.__leyraDeliver(\(json))", completionHandler: nil) }
        }
    }

    func ok(_ v: Any) -> [String: Any] { ["ok": true, "value": v] }

    func shellAction(_ command: String, _ args: [String: Any]) throws -> [String: Any] {
        let r = try askService(["type": "shell_action", "command": command, "args": args]) as? [String: Any] ?? [:]
        if r["ok"] as? Bool != true { throw ServiceError.message(r["error"] as? String ?? "操作失败") }
        return r["value"] as? [String: Any] ?? [:]
    }

    func handle(_ command: String, _ args: [String: Any]) throws -> [String: Any] {
        switch command {
        case "daemon":
            return try askService(args["request"] ?? [:]) as? [String: Any] ?? ["ok": false, "error": "后台服务回应无效"]
        case "app_version": return ok(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
        case "shell_info": return ok(["glass": false, "platform": "mac"])
        case "verify_owner", "set_theme": return ok([String: Any]())
        case "show_window": DispatchQueue.main.async { self.showMain() }; return ok([String: Any]())
        case "quit_app": DispatchQueue.main.async { NSApp.terminate(nil) }; return ok([String: Any]())
        case "tray_take":
            var taken: [String] = []
            DispatchQueue.main.sync { taken = self.trayActions; self.trayActions = [] }
            return ok(taken)
        case "tray_update":
            let state = args["state"] as? [String: Any] ?? [:]
            let tip = (state["tooltip"] as? String ?? "Leyra").prefix(63)
            DispatchQueue.main.async { self.statusItem.button?.toolTip = String(tip) }
            return ok([String: Any]())
        case "notify":
            let content = UNMutableNotificationContent()
            content.title = String((args["title"] as? String ?? "Leyra").prefix(60)); content.body = String((args["body"] as? String ?? "").prefix(240))
            UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            return ok([String: Any]())
        case "open_login_items":
            openURL("x-apple.systempreferences:com.apple.LoginItems-Settings.extension"); return ok([String: Any]())
        case "run_rescue":
            let done = runAsAdmin("'/Library/Application Support/Leyra/agent/leyra-agent' --recover")
            return done ? ok([String: Any]()) : ["ok": false, "error": "没有完成：需要输入 Mac 登录密码"]
        case "start_daemon":
            _ = runAsAdmin("/bin/launchctl bootstrap system /Library/LaunchDaemons/xin.leyra.agent.plist; /bin/launchctl kickstart -k system/xin.leyra.agent")
            return ok([String: Any]())
        case "test_browser":
            let probe = try shellAction(command, args)
            let launch = probe["launch"] as? String ?? "", target = probe["url"] as? String ?? ""
            let allowed = ["Google Chrome.app", "Microsoft Edge.app", "Firefox.app", "Brave Browser.app", "Safari.app"]
            guard allowed.contains((launch as NSString).lastPathComponent), FileManager.default.fileExists(atPath: launch),
                  target.hasPrefix("http://127.0.0.1:17892/probe/"), target.count == 61 else { throw ServiceError.message("浏览器检查参数无效") }
            run("/usr/bin/open", ["-a", launch, target]); return ok([String: Any]())
        case "quit_conflict":
            // Polite quit as the desktop user (like closing its window); 帮我退出 ends it from the service.
            let info = try shellAction(command, args)
            let names = Set((info["images"] as? [String]) ?? [])
            var asked = 0
            DispatchQueue.main.sync {
                for app in NSWorkspace.shared.runningApplications where names.contains(app.localizedName ?? "") || names.contains(app.executableURL?.lastPathComponent ?? "") {
                    if app.terminate() { asked += 1 }
                }
            }
            return ok(["asked": asked])
        case "relaunch_new_version":
            run("/usr/bin/open", ["-n", "/Applications/Leyra.app"])
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { NSApp.terminate(nil) }
            return ok(["relaunched": true])
        case "import_server":
            return ["ok": false, "error": "Mac 版请用授权码激活"]
        case "install_webrtc_profile", "open_guide", "open_web_center", "force_quit_conflict", "ai_login", "ai_login_poll", "ai_login_cancel":
            let action = try shellAction(command, args)
            openURL(action["url"] as? String); openURL(action["open_url"] as? String)
            return ok(action)
        default:
            return ["ok": false, "error": "不支持的操作"]
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
