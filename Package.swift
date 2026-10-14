// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "voice-switch",
    // macOS 26+ for SpeechTranscriber. Windows builds use Swift for Windows defaults
    // (see BUILD.md); Apple frameworks are behind #if os(macOS).
    platforms: [.macOS("26.0")],
    dependencies: [
        // Same ONNX Runtime version as Microsoft.ML.OnnxRuntime in the Windows build, so both run Silero alike.
        .package(url: "https://github.com/microsoft/onnxruntime-swift-package-manager", exact: "1.24.2"),
    ],
    targets: [
        .executableTarget(
            name: "voice-switch",
            dependencies: [.product(name: "onnxruntime", package: "onnxruntime-swift-package-manager", condition: .when(platforms: [.macOS]))],
            resources: [.copy("Resources/silero_vad.onnx"), .copy("Resources/SILERO_LICENSE")]
        ),
    ],
    swiftLanguageModes: [.v5]
)
