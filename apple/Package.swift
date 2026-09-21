// swift-tools-version: 6.0
import PackageDescription
import Foundation

let cltTestingPlugins = "/Library/Developer/CommandLineTools/usr/lib/swift/host/plugins/testing"
let useCLTPlugins = FileManager.default.fileExists(atPath: cltTestingPlugins)
    && ProcessInfo.processInfo.environment["YIKZ_NO_CLT_PLUGIN"] == nil
    && !(ProcessInfo.processInfo.environment["DEVELOPER_DIR"] ?? "").contains("Xcode")
let testSwiftSettings: [SwiftSetting] = useCLTPlugins ? [.unsafeFlags(["-plugin-path", cltTestingPlugins])] : []

let package = Package(
    name: "YikzClipboard",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "YikzClipboard", targets: ["YikzClipboard"]),
        .library(name: "ClipCore", targets: ["ClipCore"])
    ],
    targets: [
        .target(
            name: "ClipCore",
            linkerSettings: [.linkedLibrary("sqlite3")]
        ),
        .executableTarget(
            name: "YikzClipboard",
            dependencies: ["ClipCore"],
            linkerSettings: [
                .linkedFramework("Carbon"),
                .linkedFramework("ServiceManagement"),
                .linkedFramework("UserNotifications")
            ]
        ),
        .testTarget(
            name: "ClipCoreTests",
            dependencies: ["ClipCore"],
            swiftSettings: testSwiftSettings
        )
    ]
)
