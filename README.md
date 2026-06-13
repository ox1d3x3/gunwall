<div align="center">

<img src="docs/logo.png" alt="GunWall" width="560"/>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Status](https://img.shields.io/badge/release-v0.11.0%20(alpha)-orange?style=flat-square)](#roadmap)

*Take full control of your network. Block apps from the internet, watch your traffic in real time, and get a popup the moment a new app reaches out — in a fast, dark, modern interface.*

[github.com/ox1d3x3/gunwall](https://github.com/ox1d3x3/gunwall)

</div>

---

## ⚠️ Project status

GunWall is an **early alpha (v0.11.0)**. The core engine, real-time monitoring, connection alerts and full-control mode are functional and fast, but this is a foundation under active development — not yet a hardened production security product. Test it in a safe environment first.

---

## ✨ Features

- **Enable Firewall (Zero Trust)** — one click takes complete control of network traffic: **every program is denied by default** and must be explicitly approved before it can reach the network. Each undecided app raises an Allow / Block prompt; your decision is remembered permanently (approved apps stay allowed, denied apps stay blocked) until you change it. Loopback and core Windows networking (DNS/DHCP) are auto-allowed so your connection keeps working.
- **Refined interface** — a deeper, blue-tinted dark theme with elevation, status pills, hover rows, and a cyan/violet accent system for a cleaner, more modern feel.
- **Alert auto-decision countdown** — the connection popup counts down and auto-allows if you step away, so it never blocks your workflow.
- **Connection alerts** — a popup the first time any new app reaches the network, showing name, **Authenticode signature**, address, **reverse-DNS host**, port and path, with one-click **Allow / Block**. Detection is fast (sub-second) and robust: apps running as SYSTEM or other users (VPN helpers, security software) are resolved correctly, and outbound-UDP apps (VPN tunnels) are caught too.
- **Live throughput graph** — smooth gradient area chart of download/upload, plus session data totals.
- **Connection inspector** — every active TCP connection and UDP socket (IPv4 + IPv6) with owning process, endpoints and state, with instant search.
- **Activity feed** — new connections logged as they appear. Local only.
- **Lockdown** — block all traffic instantly from the app or the tray.
- **System tray** — minimize to tray; filters keep enforcing because they live in the OS, not the app.
- **Searchable app list** — filter by name or path; optionally show all running apps.
- **Staged settings with Apply** — choose your options, then commit them with one button.
- **Allow-by-default until you say otherwise** — monitoring mode never cuts your internet; full control is opt-in.
- **Tamper detection** — each rule stores the app's SHA-256 hash, so a swapped binary at the same path is detectable.
- **Silent apps** — right-click an app to mute it: stays allowed but never raises a popup again.
- **Profile export / import** — back up all rules and settings to a file and restore them on any machine.
- **Window preferences** — start minimized to tray, always-on-top, toggle hashing.
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
| **v0.6** ✅ | Enable-Firewall takeover, robust detection (SYSTEM/VPN apps), staged settings + Apply |
| **v0.7** ✅ | Refined blue-tinted theme, status pills, alert countdown, new logo |
| **v0.8** ✅ | Corrected full-control engine, transaction-based, reliable blocks |
| **v0.9** ✅ | Zero Trust: default-deny, per-app approval prompts that persist, deny-on-timeout |
| **v0.10** ✅ | SHA-256 tamper hashing, silent (muted) apps, profile export/import, window preferences |
| **v0.11** ✅ | Event-driven detection engine (kernel net events) — catches every app/service instantly, replaces polling |
| **v0.12** | Packets Log tab; alerts show true blocked-destination address |
| **v0.13** | Services tab (per-service rules) + UWP app-container support |
| **v0.14** | Custom rules editor (allow/block by address, port, protocol, direction) |
| **v0.15** | Curated blocklists + system-rules toggles |
| **v1.0** | Hardened service split, code signing, installer, auto-update |

---

## 📄 License

[MIT](LICENSE).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>
