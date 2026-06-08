<div align="center">

# 🔴 MicAlert

**Never get caught with a hot mic again.**

A lightweight Windows tray app that shows a bright red dot on your screen whenever your microphone is live — perfect for Zoom calls, recordings, and anytime you need to know if you're broadcasting.

[![Build & Release](https://github.com/MatiasM87/MicAlert/actions/workflows/build.yml/badge.svg)](https://github.com/MatiasM87/MicAlert/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

<br/>

![MicAlert demo — red dot appears when mic is live](https://raw.githubusercontent.com/MatiasM87/MicAlert/main/docs/demo.gif)

</div>

---

## ✨ What it does

MicAlert sits quietly in your system tray and watches your microphone state. The moment your mic goes **live**, a small colored dot appears on screen — always on top, always visible, no matter what window you're using.

| State | Indicator |
|-------|-----------|
| 🔴 Mic **ON** (unmuted) | Colored dot visible on screen |
| ⚫ Mic **OFF** (muted) | Dot disappears |

---

## 🚀 Quick Start

1. **[Download the latest release](https://github.com/MatiasM87/MicAlert/releases/latest)** — grab `MicAlert.exe`
2. Run it — no installation needed, it goes straight to the tray
3. Unmute your mic in Zoom and watch the red dot appear
4. Double-click the tray icon to open settings

> **Requirements:** Windows 10/11 × 64-bit · No .NET install needed (self-contained)

---

## ⚙️ Configuration

Double-click the tray icon or right-click → **Configuración** to open the settings panel.

| Setting | Default | Description |
|---------|---------|-------------|
| `mode` | `zoom` | Detection mode: `zoom` (UI Automation) or `windows` (registry) |
| `size` | `14px` | Size of the indicator dot |
| `position` | `top-right` | Corner: `top-right`, `top-left`, `bottom-right`, `bottom-left` |
| `color` | `red` | Any named color or `#hex` value |
| `opacity` | `1.0` | Transparency: `0.1` – `1.0` |
| `pollMs` | `500` | How often to check mic state (min 200ms) |
| `monitorMode` | `primary` | Show on `primary` monitor or `all` monitors |
| `offsetX/Y` | `12` | Pixel offset from screen edge |
| `fallbackToWindowsMicInUse` | `false` | Fall back to registry check if Zoom state is unclear |
| `debug` | `false` | Write debug log to `micalert-debug.log` |

Settings are saved to `config.json` next to the executable.

---

## 🔍 Detection Modes

### `zoom` (recommended)
Uses **Windows UI Automation** to read the mute/unmute button state from the Zoom window. Works reliably with both English and Spanish Zoom interfaces.

> Works with: Zoom (English & Spanish)

### `windows`
Reads the Windows registry (`CapabilityAccessManager`) to detect if any application is actively using the microphone. More universal but less precise — it activates for any app using the mic.

> Works with: any app that uses the Windows microphone

**Tip:** Enable `fallbackToWindowsMicInUse` to use the Windows method as a safety net when Zoom's state can't be determined.

---

## 🏗️ Building from Source

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) · Windows

```bash
# Clone the repo
git clone https://github.com/MatiasM87/MicAlert.git
cd MicAlert

# Build (debug)
dotnet build

# Build release
dotnet build -c Release

# Publish single-file self-contained .exe
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The self-contained `.exe` will be in `bin/Release/net8.0-windows/win-x64/publish/`.

---

## 📦 Releases

Releases are built automatically via GitHub Actions on every tagged commit. The CI:

1. Builds a self-contained, single-file `MicAlert.exe` for `win-x64`
2. Bundles the default `config.json`
3. Creates a GitHub Release with the `.exe` attached

To trigger a release, push a version tag:

```bash
git tag v1.0.1
git push origin v1.0.1
```

---

## 🗂️ Project Structure

```
MicAlert/
├── Program.cs          # All application logic
│   ├── MicAlertContext     # Tray icon + polling loop
│   ├── IndicatorForm       # On-screen dot overlay
│   ├── SettingsForm        # Configuration dialog
│   ├── AppConfig           # JSON config serialization
│   ├── ZoomMuteDetector    # UI Automation for Zoom
│   └── WindowsMicDetector  # Registry-based detection
├── MicAlert.csproj     # .NET project file
├── MicAlert.ico        # Application icon
├── app.manifest        # Windows app manifest
└── config.json         # Default configuration
```

---

## 🛠️ Tray Menu

| Option | Action |
|--------|--------|
| **Configuración** | Open settings dialog |
| **Recargar config** | Reload `config.json` from disk |
| **Mostrar/Ocultar prueba** | Toggle indicator for testing |
| **Abrir log** | Open debug log file |
| **Salir** | Exit MicAlert |

---

## 📋 Requirements

- Windows 10 or Windows 11 (64-bit)
- Zoom desktop client (for `zoom` mode)
- No additional software required when using the release `.exe`

---

## 🤝 Contributing

Pull requests are welcome! For major changes, please open an issue first to discuss what you'd like to change.

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes (`git commit -m 'Add my feature'`)
4. Push to the branch (`git push origin feature/my-feature`)
5. Open a Pull Request

---

## 📄 License

MIT © [MatiasM87](https://github.com/MatiasM87)

---

<div align="center">
Made with ☕ to survive remote meetings
</div>
