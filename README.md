<div align="center">

# 🛡️ NetGuard Pro

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Status](https://img.shields.io/badge/release-v0.1.0%20(alpha)-orange?style=flat-square)](#roadmap)

*Block apps from the internet, watch your traffic in real time, and lock down your machine with one click — with a clean, dark, native Windows 11 interface.*

</div>

---

## ⚠️ Project status

NetGuard Pro is an **early alpha (v0.1.0)**. The core firewall engine and the live monitoring UI are functional, but this is a foundation to build on — not yet a hardened, production security product. Treat it as a serious starting point for a great firewall, and test in a safe environment first. See the [Roadmap](#roadmap) for what's coming.

---

## ✨ Features in v0.1

- **Real per-app blocking** via the Windows Filtering Platform (WFP) — the same low-level technology used by [simplewall](https://github.com/henrypp/simplewall). Filters are *persistent*: they keep working after you close the app and across reboots.
- **Live throughput graph** — real-time download/upload speeds drawn on a smooth, hand-rendered chart.
- **Connection inspector** — every active TCP connection with its owning process, local/remote endpoints, and state.
- **One-click Lockdown** — instantly block all inbound and outbound traffic, then release it just as fast.
- **Allow-by-default policy** — unlike whitelist firewalls, NetGuard Pro only blocks what *you* choose, so it never silently cuts off your internet.
- **Block any executable** — pick any `.exe` from disk, even one that isn't currently connected.
- **Native Windows 11 look** — dark Fluent styling with a proper dark title bar.
- **Zero telemetry** — see [Privacy & Security](#-privacy--security).

---

## 🖼️ Interface

```
┌──────────────┬─────────────────────────────────────────────┐
│ NetGuard Pro │  Dashboard                                   │
│ WFP Firewall │  ┌─────────┐ ┌─────────┐ ┌──────────────┐    │
│              │  │Download │ │ Upload  │ │ Active conns │    │
│ ● Dashboard  │  │ 1.2 MB/s│ │ 340 KB/s│ │      37      │    │
│ ○ Firewall   │  └─────────┘ └─────────┘ └──────────────┘    │
│ ○ Connections│  ┌─────────────────────────────────────────┐│
│              │  │ Throughput (last 60s)    ● Down  ● Up    ││
│ ┌──────────┐ │  │      ╱╲      ╱╲╱╲                        ││
│ │ Lockdown │ │  │  ╱╲╱  ╲╱╲╱╲╱    ╲╱╲╱╲                    ││
│ │ [Engage] │ │  │ ────────────────────────────────────────││
│ └──────────┘ │  └─────────────────────────────────────────┘│
│ Engine:active│                                              │
└──────────────┴─────────────────────────────────────────────┘
```

---

## 🚀 Building from source

### Prerequisites

- **Windows 10 (2004+) or Windows 11**, 64-bit
- **Visual Studio 2022** (17.8 or newer) with the **.NET desktop development** workload
  *(or the standalone [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + `dotnet build`)*

### Build in Visual Studio (recommended)

1. Open `NetGuardPro.sln` in Visual Studio 2022.
2. Set the configuration to **Release** and platform to **x64**.
3. **Build → Build Solution** (`Ctrl+Shift+B`).
4. The executable appears in `src/NetGuardPro/bin/Release/net8.0-windows/NetGuardPro.exe`.

### Build from the command line

```powershell
dotnet build NetGuardPro.sln -c Release
```

To produce a single self-contained EXE that runs without the .NET runtime installed:

```powershell
dotnet publish src/NetGuardPro/NetGuardPro.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true
```

### Running

NetGuard Pro **requires administrator privileges** — WFP cannot add or remove filters otherwise. The application manifest already requests elevation, so Windows will prompt with a UAC dialog automatically when you launch the EXE. If you run from Visual Studio, start Visual Studio itself as administrator.

---

## 🔒 Privacy & Security

NetGuard Pro is designed so that **nothing happens to your data without your say-so**:

- **No network calls of its own.** The app never phones home, checks for updates silently, or uploads anything. The only network activity on the machine is the traffic you already have — NetGuard Pro just observes it locally.
- **No telemetry, no analytics, no accounts.**
- **Local-only storage.** Rules are saved as a plain JSON file under `%ProgramData%\NetGuardPro\rules.json`. You can read it, back it up, or delete it.
- **Explicit actions only.** The engine never blocks or allows anything on its own initiative. Every filter corresponds to a button you pressed.
- **Allow-by-default.** A fresh install changes nothing about your connectivity until you block something.
- **Clean removal.** "Remove all filtering" tears down every filter NetGuard Pro created. Because WFP filters persist, always run this (or release Lockdown) before uninstalling.

> **Security caveat for v0.1:** this alpha runs as a single elevated process and does not yet implement the hardened service-isolation, code-signing, and tamper-protection described in the roadmap. Don't rely on it as your sole defense on a high-risk machine yet.

---

## 🧭 How it works

NetGuard Pro talks directly to the **Windows Filtering Platform**, a set of OS APIs for building network-filtering software. It is *not* a front-end for Windows Firewall and does not modify Windows Firewall rules — the two operate independently, exactly like simplewall.

```
┌─────────────────────────────────────────┐
│  WPF UI (dashboard, firewall, conns)     │  ← what you see
├─────────────────────────────────────────┤
│  FirewallManager + RuleStore (JSON)      │  ← orchestration + persistence
├─────────────────────────────────────────┤
│  WfpEngine  →  fwpuclnt.dll (P/Invoke)   │  ← real WFP filters
└─────────────────────────────────────────┘
```

When you block an app, NetGuard Pro adds four persistent WFP filters (outbound + inbound, IPv4 + IPv6) keyed to that executable's app ID. Lockdown adds higher-weight, condition-less block filters that override per-app rules until released.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full design.

---

## 🗺️ Roadmap

| Version | Focus |
|---------|-------|
| **v0.1** ✅ | WFP engine, per-app block/allow, live graph, connection inspector, lockdown |
| **v0.2** | GeoIP + country flags, per-app bandwidth attribution, connection event history |
| **v0.3** | Firewall profiles (Home/Work/Public), "Ask to Connect" prompts, rule editor |
| **v0.4** | Split into hardened Windows Service + UI, code signing, tamper protection |
| **v0.5** | DNS query logging, LAN device scanner, world map, mini floating widget |
| **v1.0** | Installer, auto-update, multi-language, polish |

---

## 🤝 Contributing

Contributions are welcome. The most valuable help right now:

- Testing the WFP interop on different Windows builds and reporting marshalling issues.
- Implementing per-app bandwidth via a WFP flow callout.
- Improving the UI and adding the roadmap features.

Please open an issue before large changes so we can align on direction.

---

## 📄 License

Released under the [MIT License](LICENSE). NetGuard Pro is original work; its WFP approach is *inspired by* simplewall but contains no simplewall code.

---

<div align="center">
<sub>Built with care for a free, modern, private Windows. Bismillah.</sub>
</div>
