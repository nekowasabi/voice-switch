// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "voice-switch",
    // macOS 26+ for SpeechTranscriber. Windows builds use Swift for Windows defaults
    // (see BUILD.md); Apple frameworks are behind #if os(macOS).
    platforms: [.macOS("26.0")],
    targets: [
        .executableTarget(
            name: "voice-switch"
        ),
    ],
    swiftLanguageModes: [.v5]
)
