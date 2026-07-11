# LiteRemote — Mobile/Mac viewer (.NET MAUI)

Cross-platform **viewer** for LiteRemote. It reuses `RemoteDesktop.Shared` (the audited, frozen
protocol/crypto contract) so the wire protocol is identical to the Windows client — no reimplementation.

**Status (M-A1):** Android target builds into an installable APK. The connect flow (`Services/HostConnection.cs`)
does TLS + trust-on-first-use pinning + the auth handshake using the shared `MessageChannel`/`AuthProtocol`;
this path is covered end-to-end by `RemoteDesktop.Shared.Tests/HandshakeIntegrationTests`. Video decode
(MediaCodec/VideoToolbox), input, and a persistent pin store come in M-A2+.

> This project is intentionally **not** part of `RemoteDesktop.sln` — the Windows CI restores the solution
> and would need the MAUI/Android workload otherwise. Build it explicitly with the commands below.

## Prerequisites
- .NET 8 SDK
- MAUI Android workload: `dotnet workload install maui-android`
- **JDK 17** (e.g. Microsoft OpenJDK 17)
- **Android SDK** (platform 34 + build-tools). Provision headlessly with:
  ```
  dotnet build src/RemoteDesktop.Maui/RemoteDesktop.Maui.csproj -f net8.0-android \
    -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=True \
    -p:JavaSdkDirectory="<jdk>" -p:AndroidSdkDirectory="<sdk>"
  ```
- Point the toolchain at them (once): set `JAVA_HOME` to the JDK and `ANDROID_HOME` to the SDK, or pass
  `-p:JavaSdkDirectory=… -p:AndroidSdkDirectory=…` on each build.

## Build the APK
```
dotnet build src/RemoteDesktop.Maui/RemoteDesktop.Maui.csproj -f net8.0-android -c Debug
```
Output: `bin/Debug/net8.0-android/com.literemote.viewer-Signed.apk` (debug-signed; sideload directly).

## iOS / Mac Catalyst
Add `net8.0-maccatalyst`/`net8.0-ios` back to `<TargetFrameworks>` and build on **macOS** (or a GitHub
Actions macOS runner) with the matching `maui-*` workloads. Not buildable on Windows.
