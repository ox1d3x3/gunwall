<div align="center">

<img src="docs/logo.png" alt="GunWall" width="560"/>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Status](https://img.shields.io/badge/release-v0.6.0%20(alpha)-orange?style=flat-square)](#roadmap)

*Take full control of your network. Block apps from the internet, watch your traffic in real time, and get a popup the moment a new app reaches out — in a fast, dark, modern interface.*

[github.com/ox1d3x3/gunwall](https://github.com/ox1d3x3/gunwall)

</div>

---

## ⚠️ Project status

GunWall is an **early alpha (v0.6.0)**. The core engine, real-time monitoring, connection alerts and full-control mode are functional and fast, but this is a foundation under active development — not yet a hardened production security product. Test it in a safe environment first.

---

## ✨ Features

- **Enable Firewall (full control)** — one click takes over all network traffic: every app is blocked except the ones you allow, enforced by persistent WFP filters. Loopback and core Windows networking (DNS/DHCP) stay alive automatically so your connection never silently dies.
- **Connection alerts** — a popup the first time any new app reaches the network, showing name, **Authenticode signature**, address, **reverse-DNS host**, port and path, with one-click **Allow / Block**. Detection is fast (sub-second) and robust: apps running as SYSTEM or other users (VPN helpers, security software) are resolved correctly, and outbound-UDP apps (VPN tunnels) are caught too.
- **Live throughput graph** — smooth gradient area chart of download/upload, plus session data totals.
- **Connection inspector** — every active TCP connection and UDP socket (IPv4 + IPv6) with owning process, endpoints and state, with instant search.
- **Activity feed** — new connections logged as they appear. Local only.
- **Lockdown** — block all traffic instantly from the app or the tray.
- **System tray** — minimize to tray; filters keep enforcing because they live in the OS, not the app.
- **Searchable app list** — filter by name or path; optionally show all running apps.
- **Staged settings with Apply** — choose your options, then commit them with one button.
- **Allow-by-default until you say otherwise** — monitoring mode never cuts your internet; full control is opt-in.
- **Zero telemetry, zero dependencies** — see [Privacy & Security](#-privacy--security).

---

## 🚀 Building from source

### Prerequisites

- **Windows 10 (2004+) or Windows 11**, 64-bit
- **Visual Studio 2022** (17.8+) with the **.NET desktop development** workload
  *(or the standalone [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0))*

### Build in Visual Studio (recommended)

1. Open `GunWall.sln`.
2. Set configuration **Release**, platform **x64**.
3. **Build → Build Solution** (`Ctrl+Shift+B`).
4. The EXE appears in `src/GunWall/bin/Release/net8.0-windows/GunWall.exe`.

### Command line

```powershell
dotnet build GunWall.sln -c Release
```

Single self-contained EXE:

```powershell
dotnet publish src/GunWall/GunWall.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true
```

### Running

GunWall **requires administrator privileges** — WFP cannot add or remove filters otherwise. The manifest requests elevation automatically (UAC prompt). To debug from Visual Studio, start Visual Studio as administrator.

---

## 🔒 Privacy & Security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No network calls of its own.** No phoning home, no silent update checks, no uploads. (The only outbound lookup is reverse-DNS for the alert's "Host" field, which is the same query your OS already makes, to your own DNS server.)
- **No telemetry, no analytics, no accounts.**
- **Local-only storage.** Rules live in `%ProgramData%\GunWall\rules.json` — plain JSON you can read, back up, or delete.
- **Explicit actions only.** Every filter corresponds to a button you pressed.
- **Allow-by-default.** A fresh install changes nothing until you enable the firewall or block something.
- **Clean removal.** Settings → "Remove all GunWall filtering" tears down every persistent filter. Always run it before uninstalling.
- **Zero third-party packages.** The whole supply chain is the .NET base class library plus Win32 — trivially auditable.

> **Security caveat:** this alpha runs as a single elevated process and does not yet implement service isolation, code signing, or tamper protection. Don't rely on it as your sole defense on a high-risk machine yet.

---

## 🧭 How it works

GunWall talks directly to the **Windows Filtering Platform**, the OS network-filtering subsystem. It runs as an independent filtering layer and does not modify your existing Windows Firewall rules.

```
┌──────────────────────────────────────────────┐
│  WPF UI — dashboard • firewall • connections  │
│           activity • settings • tray • alerts │
├──────────────────────────────────────────────┤
│  Fast detection loop + background sampler      │
│  FirewallManager + RuleStore (JSON)           │
├──────────────────────────────────────────────┤
│  WfpEngine  →  fwpuclnt.dll (P/Invoke)        │
└──────────────────────────────────────────────┘
```

Blocking an app adds four persistent WFP filters (outbound + inbound, IPv4 + IPv6) keyed to the executable. Enabling full control adds a base block plus per-app permits. Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## 🗺️ Roadmap

| Version | Focus |
|---------|-------|
| **v0.6** ✅ | Enable-Firewall takeover, robust detection (SYSTEM/VPN apps), staged settings + Apply, refined UI |
| **v0.7** | Kernel net-event drop notifications (show address/port of blocked attempts), per-rule scoping (address/port/direction) |
| **v0.8** | GeoIP + per-app traffic attribution, traffic history database, profiles (Home/Work/Public) |
| **v0.9** | DNS-level blocking and blocklists, rule editor |
| **v1.0** | Hardened service split, code signing, tamper protection, installer, auto-update |

---

## 📄 License

[MIT](LICENSE).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>
