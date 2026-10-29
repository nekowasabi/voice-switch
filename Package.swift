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
        // whisper.cpp b5454 (v1.9.5); a dynamic framework, so `make app` copies it into Contents/Frameworks.
        .binaryTarget(
            name: "whisper",
            url: "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-b5454-xcframework.zip",
            checksum: "e57f8c48933000acabc13bb913fbe82805d483a2deff692cc6738836bd92393b"
        ),
        .executableTarget(
            name: "voice-switch",
            dependencies: [
                .product(name: "onnxruntime", package: "onnxruntime-swift-package-manager", condition: .when(platforms: [.macOS])),
                .target(name: "whisper", condition: .when(platforms: [.macOS])),
            ],
            resources: [.copy("Resources/silero_vad.onnx"), .copy("Resources/SILERO_LICENSE"), .copy("Resources/grok-bot.png")],
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks",
                                           "-Xlinker", "-rpath", "-Xlinker", "@executable_path"])]
        ),
    ],
    swiftLanguageModes: [.v5]
)
