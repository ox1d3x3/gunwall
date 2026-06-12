<div align="center">

<img src="docs/logo.png" alt="GunWall" width="560"/>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Status](https://img.shields.io/badge/release-v0.5.0%20(alpha)-orange?style=flat-square)](#roadmap)

*Block apps from the internet, watch your traffic in real time, and lock down your machine with one click — wrapped in a dark, GlassWire-inspired interface.*

[github.com/ox1d3x3/gunwall](https://github.com/ox1d3x3/gunwall)

</div>

---

## ⚠️ Project status

GunWall is an **early alpha (v0.5.0)**. The core firewall engine and live monitoring are functional and fast, but this is a foundation under active development — not yet a hardened production security product. Test it in a safe environment first.

---

## ✨ Features in v0.5

- **Strict mode (full control)** — simplewall-style whitelist: when engaged, **everything is blocked except apps you explicitly allow** via persistent WFP PERMIT filters. Loopback and core Windows networking (svchost/DNS/DHCP) are auto-allowed so your connection never silently dies.
- **Status banner** — Portmaster-style at-a-glance state: Protected / Strict / Lockdown.
- **Connection alerts** — a simplewall-style popup the first time a new app connects, showing name, **Authenticode signature**, address, **reverse-DNS host**, port and path, with one-click **Allow / Block**. (GunWall is allow-by-default, so the alert is shown on first observed connection; ask-before-connect requires whitelist mode and is on the roadmap.)
- **Session data totals** — bytes downloaded/uploaded this session on the dashboard, GlassWire-style.
- **Real per-app blocking** via the Windows Filtering Platform (WFP) — the same low-level technology used by [simplewall](https://github.com/henrypp/simplewall). Filters are *persistent*: they keep enforcing after you close the app and across reboots.
- **Fast & responsive** — all heavy work (process snapshots, TCP/UDP tables, interface stats) runs on a background thread with PID caching; the UI never stutters.
- **Live throughput graph** — GlassWire-style gradient area chart of download/upload.
- **Connection inspector** — every active **TCP connection and UDP listener** (IPv4 + IPv6) with its owning process, endpoints, and state, with instant search.
- **Activity feed** — new connections logged as they appear, GlassWire-timeline style. Local only.
- **One-click Lockdown** — block all traffic instantly from the app or the tray icon.
- **System tray** — minimize to tray; filters keep enforcing because they live in the OS, not the app.
- **Searchable app list** — filter by name or path; optionally show *all* running apps, not just networked ones.
- **Settings** — refresh interval control and a one-click "remove all GunWall filtering" reset.
- **Allow-by-default policy** — GunWall only blocks what *you* choose, so it never silently cuts your internet.
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

Single self-contained EXE (no .NET runtime needed on the target machine):

```powershell
dotnet publish src/GunWall/GunWall.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true
```

### Running

GunWall **requires administrator privileges** — WFP cannot add or remove filters otherwise. The manifest requests elevation automatically (UAC prompt). To debug from Visual Studio, start Visual Studio as administrator.

---

## 🔒 Privacy & Security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No network calls of its own.** No phoning home, no silent update checks, no uploads.
- **No telemetry, no analytics, no accounts.**
- **Local-only storage.** Rules live in `%ProgramData%\GunWall\rules.json` — plain JSON you can read, back up, or delete. (Rules from earlier NetGuard Pro alphas are migrated automatically.)
- **Explicit actions only.** Every filter corresponds to a button you pressed.
- **Allow-by-default.** A fresh install changes nothing until you block something.
- **Clean removal.** Settings → "Remove all GunWall filtering" tears down every persistent filter. Always run it before uninstalling.
- **Zero third-party packages.** The whole supply chain is the .NET BCL plus Win32 — trivially auditable.

> **Security caveat:** this alpha runs as a single elevated process and does not yet implement service isolation, code signing, or tamper protection. Don't rely on it as your sole defense on a high-risk machine yet.

---

## 🧭 How it works

GunWall talks directly to the **Windows Filtering Platform**. It is *not* a front-end for Windows Firewall — the two operate independently, exactly like simplewall.

```
┌──────────────────────────────────────────────┐
│  WPF UI — dashboard • firewall • connections  │
│           activity • settings • tray          │
├──────────────────────────────────────────────┤
│  Background sampler (TCP/UDP tables, procs)   │
│  FirewallManager + RuleStore (JSON)           │
├──────────────────────────────────────────────┤
│  WfpEngine  →  fwpuclnt.dll (P/Invoke)        │
└──────────────────────────────────────────────┘
```

Blocking an app adds four persistent WFP filters (outbound + inbound, IPv4 + IPv6) keyed to the executable's app ID. Lockdown adds higher-weight condition-less block filters that override everything until released. Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## 🗺️ Roadmap

Informed by studying [simplewall](https://github.com/henrypp/simplewall), [Fort Firewall](https://github.com/tnodir/fort), [Portmaster](https://github.com/safing/portmaster), and [GlassWire](https://www.glasswire.com/):

| Version | Focus |
|---------|-------|
| **v0.3** ✅ | Fast async engine, UDP, activity feed, search, tray, settings, GunWall identity |
| **v0.4** ✅ | Connection alert popups (signature + host), session totals, alert settings |
| **v0.5** ✅ | Strict whitelist mode (full traffic control), loopback keep-alive, status banner, Portmaster-style UI |
| **v0.6** | WFP net-event drop notifications (alert with address/port for blocked attempts), per-rule scoping, rule editor |
| **v0.7** | GeoIP + per-app traffic attribution (ETW), traffic history database |
| **v0.8** | DNS-level blocking and blocklists (Portmaster-style), profiles |
| **v0.9** | Hardened Windows Service split, code signing, tamper protection |
| **v1.0** | Installer, auto-update, multi-language, kernel-driver evaluation (Fort-style) for speed limits |

---

## 🤝 Acknowledgments

GunWall contains no code from other projects, but stands on the shoulders of the open-source firewall community: **simplewall** (WFP approach), **Fort Firewall** (driver-based filtering ideas), and **Portmaster** (DNS-level privacy concepts). GlassWire's visual design language is an inspiration for the UI direction.

## 📄 License

[MIT](LICENSE).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>
