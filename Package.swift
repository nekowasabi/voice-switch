// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "voice-switch",
    platforms: [.macOS("26.0")],
    targets: [
        .executableTarget(
            name: "voice-switch"
        ),
    ],
    swiftLanguageModes: [.v5]
)
