// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "JarvisMac", platforms: [.macOS(.v14)], products: [.executable(name: "jarvis-mac", targets: ["JarvisMac"])], targets: [.executableTarget(name: "JarvisMac")])
