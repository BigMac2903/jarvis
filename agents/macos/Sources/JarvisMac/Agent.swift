import Foundation
import AppKit
import ApplicationServices
import ScreenCaptureKit
import Security

struct AgentConfig: Codable {
    var server: String
    var applications: [String: String]
    var allowedFolders: [String]
    var screenConsent: Bool
}
enum AgentError: Error { case invalid(String) }
@main struct JarvisMac {
    static let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Jarvis")
    static func main() async {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let path = directory.appendingPathComponent("config.json")
            if !FileManager.default.fileExists(atPath: path.path) {
                print("HTTPS-Serveradresse:")
                let server = readLine() ?? ""
                let initial = AgentConfig(server: server, applications: [:], allowedFolders: [], screenConsent: false)
                try JSONEncoder().encode(initial).write(to: path, options: .atomic)
            }
            let config = try JSONDecoder().decode(AgentConfig.self, from: Data(contentsOf: path))
            guard let base = URL(string: config.server), base.scheme == "https" else { throw AgentError.invalid("HTTPS erforderlich") }
            var token = keychainRead()
            if token == nil {
                print("Pairing-Token:"); let pairing = readLine() ?? ""
                print("Pairing-Code:"); let code = readLine() ?? ""
                let result = try await request(config, nil, "devices/pair", "POST", ["token": pairing, "code": code, "name": Host.current().localizedName ?? "Mac", "platform": "macos"]) as? [String: Any]
                guard let value = result?["token"] as? String else { throw AgentError.invalid("Pairing fehlgeschlagen") }
                try keychainSave(value); token = value
            }
            print("JARVIS verbunden. Lokale Freigaben: " + path.path)
            while !Task.isCancelled {
                do {
                    let app = await MainActor.run { NSWorkspace.shared.frontmostApplication?.localizedName ?? "" }
                    _ = try await request(config, token, "device-agent/status", "POST", ["computerName": Host.current().localizedName ?? "Mac", "activeApplication": config.screenConsent ? app : "nicht freigegeben", "online": true])
                    let commands = try await request(config, token, "device-agent/commands", "GET", nil) as? [[String: Any]] ?? []
                    for command in commands {
                        guard let id = command["id"] as? String, let name = command["command"] as? String, let raw = command["args"] as? String,
                              let args = try JSONSerialization.jsonObject(with: Data(raw.utf8)) as? [String: Any] else { continue }
                        var result: [String: Any]
                        do { result = try await execute(name, args, config); result["ok"] = true }
                        catch { result = ["ok": false, "error": "Aktion nicht erlaubt oder fehlgeschlagen. Lokale Freigaben prüfen."] }
                        _ = try await request(config, token, "device-agent/commands/" + id + "/result", "POST", result)
                    }
                } catch { print("Verbindung/Aktion fehlgeschlagen; nächster Versuch in 3 Sekunden.") }
                try await Task.sleep(nanoseconds: 3_000_000_000)
            }
        } catch { print("Start fehlgeschlagen: \(error)"); exit(1) }
    }
    static func request(_ config: AgentConfig, _ token: String?, _ path: String, _ method: String, _ body: [String: Any]?) async throws -> Any {
        let url = URL(string: config.server.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/api/v1/" + path)!
        var req = URLRequest(url: url); req.httpMethod = method; req.timeoutInterval = 30
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { req.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
        if let body { req.httpBody = try JSONSerialization.data(withJSONObject: body) }
        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else { throw AgentError.invalid("HTTP-Fehler") }
        return data.isEmpty ? [:] : try JSONSerialization.jsonObject(with: data)
    }
    static func keychainRead() -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "JarvisAgent", kSecAttrAccount as String: "device", kSecReturnData as String: true]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    static func keychainSave(_ token: String) throws {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "JarvisAgent", kSecAttrAccount as String: "device"]
        SecItemDelete(query as CFDictionary)
        var item = query; item[kSecValueData as String] = Data(token.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw AgentError.invalid("Keychain") }
    }
    @MainActor static var elements: [String: (AXUIElement, Date)] = [:]
    @MainActor static func execute(_ name: String, _ args: [String: Any], _ config: AgentConfig) async throws -> [String: Any] {
        if name == "GetInfo" { return ["computerName": Host.current().localizedName ?? "Mac", "os": ProcessInfo.processInfo.operatingSystemVersionString, "screenConsent": config.screenConsent] }
        if name == "ListApplications" {
            let apps = (try? FileManager.default.contentsOfDirectory(atPath: "/Applications").filter { $0.hasSuffix(".app") }) ?? []
            return ["allowed": Array(config.applications.keys), "discovered": apps]
        }
        guard config.screenConsent else { throw AgentError.invalid("Lokale Freigabe fehlt") }
        if name == "TakeScreenshot" {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            guard let display = content.displays.first else { throw AgentError.invalid("Kein Display") }
            let filter = SCContentFilter(display: display, excludingWindows: [])
            let capture = SCStreamConfiguration(); capture.width = min(display.width, 1920); capture.height = display.height * capture.width / display.width
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: capture)
            guard let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { throw AgentError.invalid("Screenshot") }
            return ["image": "data:image/png;base64," + png.base64EncodedString()]
        }
        if name == "OpenApplication" || name == "CloseApplication" {
            guard let key = args["application"] as? String, let bundle = config.applications[key],
                  let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundle) else { throw AgentError.invalid("App nicht erlaubt") }
            guard !["com.apple.Terminal", "com.googlecode.iterm2", "com.apple.ScriptEditor2"].contains(bundle) else { throw AgentError.invalid("Shell gesperrt") }
            if name == "OpenApplication" { _ = try await NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration()) }
            else { for app in NSRunningApplication.runningApplications(withBundleIdentifier: bundle) { app.terminate() } }
            return [:]
        }
        if name == "OpenFile" || name == "OpenFolder" {
            guard let raw = args["path"] as? String else { throw AgentError.invalid("Pfad fehlt") }
            let url = URL(fileURLWithPath: raw).standardizedFileURL
            guard url == url.resolvingSymlinksInPath(), config.allowedFolders.contains(where: { url.path.hasPrefix(URL(fileURLWithPath: $0).standardizedFileURL.path + "/") }) else { throw AgentError.invalid("Pfad gesperrt") }
            if name == "OpenFile" && !["pdf", "txt", "md", "png", "jpg", "jpeg", "docx", "xlsx"].contains(url.pathExtension.lowercased()) { throw AgentError.invalid("Dateityp gesperrt") }
            guard NSWorkspace.shared.open(url) else { throw AgentError.invalid("Öffnen fehlgeschlagen") }
            return [:]
        }
        guard AXIsProcessTrusted() else {
            let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(opts); throw AgentError.invalid("Bedienungshilfen freigeben")
        }
        if name == "GetElements" {
            guard let app = NSWorkspace.shared.frontmostApplication else { throw AgentError.invalid("Kein Fenster") }
            let root = AXUIElementCreateApplication(app.processIdentifier)
            var queue = [root]; var result = [[String: Any]](); elements.removeAll()
            while !queue.isEmpty && result.count < 300 {
                let element = queue.removeFirst()
                var title: CFTypeRef?; var role: CFTypeRef?; var subrole: CFTypeRef?; var children: CFTypeRef?
                AXUIElementCopyAttributeValue(element, kAXTitleAttribute as CFString, &title)
                AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
                AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subrole)
                if (subrole as? String) == "AXSecureTextField" { continue }
                let id = UUID().uuidString; elements[id] = (element, Date().addingTimeInterval(45))
                result.append(["id": id, "name": title as? String ?? "", "type": role as? String ?? ""])
                AXUIElementCopyAttributeValue(element, kAXChildrenAttribute as CFString, &children)
                queue.append(contentsOf: (children as? [AXUIElement] ?? []).prefix(300))
            }
            return ["elements": result]
        }
        if name == "ClickElement" || name == "TypeText" {
            guard let id = args["elementId"] as? String, let (element, expiry) = elements[id], expiry > Date() else { throw AgentError.invalid("Element abgelaufen") }
            let result: AXError
            if name == "ClickElement" { result = AXUIElementPerformAction(element, kAXPressAction as CFString) }
            else { result = AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, (args["text"] as? String ?? "") as CFString) }
            guard result == .success else { throw AgentError.invalid("Accessibility-Aktion fehlgeschlagen") }
            return [:]
        }
        if name == "PressKey" {
            let keys: [String: CGKeyCode] = ["ENTER": 36, "ESC": 53, "TAB": 48, "UP": 126, "DOWN": 125, "LEFT": 123, "RIGHT": 124, "CTRL+S": 1]
            guard let key = args["key"] as? String, let code = keys[key] else { throw AgentError.invalid("Taste gesperrt") }
            let down = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)
            let up = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: false)
            if key == "CTRL+S" { down?.flags = .maskCommand; up?.flags = .maskCommand }
            down?.post(tap: .cghidEventTap); up?.post(tap: .cghidEventTap); return [:]
        }
        throw AgentError.invalid("Kommando nicht unterstützt")
    }
}
