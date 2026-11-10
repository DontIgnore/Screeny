// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Screeny",
    defaultLocalization: "en",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "Screeny", targets: ["Screeny"])],
    targets: [
        .target(
            name: "ScreenyKit",
            resources: [.copy("Resources/Strings")],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(name: "Screeny", dependencies: ["ScreenyKit"], swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "ScreenyKitTests", dependencies: ["ScreenyKit"], swiftSettings: [.swiftLanguageMode(.v5)]),
    ]
)
