<div align="center">

<img src="docs/logo.png" alt="GunWall" width="560"/>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-3FB868?style=flat-square)](LICENSE)
[![Dependencies](https://img.shields.io/badge/NuGet%20dependencies-0-3FB868?style=flat-square)](#-privacy--security)
[![Status](https://img.shields.io/badge/release-v0.32.0%20(alpha)-E0A53F?style=flat-square)](#-roadmap)

*Take full control of your network. Deny every app by default, watch your traffic in real time, verify who signed the programs reaching out, and block telemetry, ads and trackers — in a fast, dark, modern interface.*

**[github.com/ox1d3x3/gunwall](https://github.com/ox1d3x3/gunwall)**

</div>

---

## ⚠️ Project status

GunWall is an **early alpha**. The WFP engine, real-time monitoring, Zero-Trust enforcement, connection alerts, signature verification and the blocking subsystems are functional and fast — but this is a foundation under active development, **not yet a hardened production security product**. It runs as a single elevated process and does not yet implement service isolation or code signing. Test it in a safe environment first, and don't rely on it as your sole defense on a high-risk machine.

---

## ✨ Features

### 🛡️ Firewall core
- **Zero-Trust mode** — one click takes complete control: **every program is denied by default** and must be explicitly approved before it can reach the network. Each undecided app raises an Allow / Block prompt; your decision persists until you change it. Loopback and core Windows networking (DNS / DHCP) stay allowed so your connection keeps working.
- **Per-app rules** — allow or block any executable, in either direction, with optional **timed** (auto-expiring) and **silent** (muted) variants. Critical system processes are guarded against accidental blocking.
- **Lockdown** — kill all traffic instantly from the app or the tray.
- **Stealth mode** — drop unsolicited inbound connections and ICMP error replies so the machine stops answering probes.
- **Allow-by-default until you say otherwise** — plain monitoring never cuts your internet; full control is opt-in.

### 🔍 Monitoring & visibility
- **Connection alerts** — a popup the first time any new app reaches the network, showing name, **verified Authenticode signature**, remote address, **reverse-DNS host**, port and path, with one-click **Allow / Block** and an auto-decision countdown. Detection is sub-second and resolves apps running as SYSTEM or other users (VPN helpers, security software) and outbound-UDP tunnels.
- **Connection inspector** — every live TCP connection and UDP socket (IPv4 + IPv6) with owning process, endpoints and state, with instant search. Right-click to **close a connection**, **block the app**, or **terminate the process**.
- **Packets Log** — a live, searchable, color-coded log of every connection event (allowed and blocked, system services included), exportable to CSV.
- **Live throughput graph** — smooth download / upload area chart plus session totals.
- **Activity feed** — new connections logged as they appear, locally.
- **Network scanner** — discover devices on your LAN (IP, MAC, host name).

### ✅ App trust & verification
- **Authenticode signature verification** — GunWall *validates* each program's digital signature with `WinVerifyTrust` (the same check Windows uses), not just reading a name. Apps are marked **Valid signature**, **Unsigned**, or **Invalid signature** — so a file that was tampered with after signing, or carries a forged / untrusted certificate, is flagged in red instead of trusted.
- **Tamper detection** — each rule stores the executable's SHA-256, so a swapped binary at the same path is detectable.
- **VirusTotal lookup** — right-click an app to check its hash against VirusTotal with your own API key (only the hash leaves the machine).

### 🚫 Threat & privacy blocking
- **Telemetry & Windows Update blocklists** — category toggles that block known Windows telemetry and update-delivery domains via the hosts file, with an **automatic WFP firewall-rule fallback** if security software blocks the hosts file, so the lists still take effect.
- **Ads & trackers** — blocked at the DNS layer via **AdGuard DNS** — fast, no list to maintain, and unaffected by hosts-file protection.
- **Filtering DNS** — point Windows at a filtering resolver (AdGuard for ads/trackers, Quad9 for malware/phishing) as a second blocking layer that needs no upkeep.
- **Custom rules** — block or allow by remote IP / CIDR, port, protocol and direction, independent of any app.
- **Manual IP blocklist** — paste IPv4 addresses to block outright.
- **System-rule library** — one-tap toggles for common hardening rules and a secure baseline.
- **Update lists from online** — pull the latest community blocklists on demand (telemetry from WindowsSpyBlocker, ads/trackers from StevenBlack — both MIT).

### ⚙️ Management
- **Profiles** — save and switch named rule-set profiles (e.g. Home / Work / Travel).
- **Versioned backups** — automatic and on-demand snapshots of all rules and settings, restorable in a click.
- **Windows Firewall integration** — read its status, turn it on/off, and import its block rules.
- **Diagnostics export** — bundle config, logs and network state into a single file for troubleshooting.
- **Run at startup** — launch with Windows, elevated and without a UAC prompt, via a scheduled task.
- **Close to tray** — closing the window minimizes to the tray so the firewall stays manageable; a true Exit warns if filtering is still active and offers to turn it off on the way out.
- **Configurable alerts** — set the popup timeout and default action, mute apps, or snooze prompts.

### 🎨 Interface
- A deep, blue-tinted **dark theme** and a matching **light theme**, with an animated switch in the header and high-contrast text in both.
- Icon tab navigation, soft status pills, elevation and hover states for a clean, modern feel.

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
GunWall **requires administrator privileges** — WFP cannot add or remove filters otherwise. The manifest requests elevation automatically (UAC prompt). To debug from Visual Studio, start it as administrator.

> **Antivirus note:** because a firewall legitimately does the same low-level things malware does — modifying the hosts file, changing DNS, creating packet filters, terminating processes — some behavioral antivirus engines may flag an **unsigned** build with a *heuristic* (generic) detection, especially when run from a `Downloads` folder. This is a false positive. Build in **Release**, run from a stable folder, and if needed add GunWall to your antivirus's trusted-application / exclusion list. Proper code signing is the long-term fix.

---

## 🔒 Privacy & Security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No telemetry, no analytics, no accounts, no phoning home.** The only outbound lookups are the ones you ask for: reverse-DNS for an alert's "Host" field (the same query your OS already makes), optional VirusTotal hash checks, and "Update lists from online".
- **Local-only storage.** Your profile (allow/block choices + settings) lives in a portable `GunWallData` folder next to the executable, falling back to `%ProgramData%\GunWall` if that's read-only — plain JSON you can read, back up, or delete.
- **Explicit actions only.** Every filter corresponds to a button you pressed. A fresh install changes nothing until you enable the firewall or block something.
- **Clean removal.** Tear down every persistent filter from Settings before uninstalling.
- **Zero third-party packages.** The whole supply chain is the .NET base class library plus Win32 — trivially auditable.

---

## 🧭 How it works

GunWall talks directly to the **Windows Filtering Platform**, the OS network-filtering subsystem. It runs as an independent filtering layer and does **not** modify your existing Windows Firewall rules.

```
┌────────────────────────────────────────────────────┐
│  WPF UI — dashboard · apps · connections · packets  │
│  security · services · network · activity · alerts  │
├────────────────────────────────────────────────────┤
│  Sub-second detection loop + background sampler      │
│  FirewallManager · RuleStore (JSON) · services       │
├────────────────────────────────────────────────────┤
│  WfpEngine → fwpuclnt.dll  ·  WinVerifyTrust          │
│  hosts file · DNS · scheduled task (P/Invoke + Win32)│
└────────────────────────────────────────────────────┘
```

Detection is **event-driven** off the WFP kernel event stream (not polling). Blocking an app adds persistent WFP filters (outbound + inbound, IPv4 + IPv6) keyed to the executable; Zero-Trust adds a base block plus per-app permits. Filter IDs are persisted so every filter can be cleanly removed later, even across restarts. Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## 🌍 GeoIP data source (optional, self-hosted)

GunWall can label each connection with its **country and network operator (ASN)** and use that for country/ASN blocking. Pick the source under **Settings → Security & Privacy → GeoIP data source**:

- **Local database** *(default)* — GunWall downloads the free, public-domain [iptoasn](https://iptoasn.com) IPv4 table on demand and resolves addresses entirely on your machine. No server, no setup. This is all most people need.
- **Self-hosted API server** — GunWall instead asks a small HTTP service you run yourself. Nothing to download, always fresh, and it resolves **IPv6** too. Lookups are cached, so each address is fetched only once.

> GunWall ships with **no** API URL and never contacts anyone else's server. If you choose API mode you point it at your own endpoint — nothing is hard-coded and there is no shared or default host to overload.

### Run your own server

The service is [`jedisct1/iptoasn-webservice`](https://github.com/jedisct1/iptoasn-webservice) (BSD-2-Clause). It fetches and refreshes the dataset itself and answers `GET /v1/as/ip/<ip>` with JSON. The project's stock image targets an older Rust toolchain; this two-stage Dockerfile builds cleanly on current Docker:

```dockerfile
FROM rust:bookworm AS builder
RUN git clone --depth 1 https://github.com/jedisct1/iptoasn-webservice.git /build
WORKDIR /build
RUN cargo build --release

FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=builder /build/target/release/iptoasn-webservice /usr/local/bin/iptoasn-webservice
EXPOSE 53661
ENTRYPOINT ["iptoasn-webservice", "--listen", "0.0.0.0:53661"]
```

Build and run it (mapping host port `53662` to the container's `53661` — change to taste):

```bash
docker build -t iptoasn .
docker run -d --name iptoasn --restart unless-stopped -p 53662:53661 iptoasn
```

Confirm it's up — this should report Google, `AS15169`, `US`:

```bash
curl -H "Accept: application/json" http://YOUR_SERVER:53662/v1/as/ip/8.8.8.8
```

Then in GunWall: **Settings → Security & Privacy → GeoIP data source → Self-hosted API server**, enter `http://YOUR_SERVER:53662`, click **Test**, then **Save**. To reach it from outside your LAN, put it behind a reverse proxy or tunnel (e.g. Cloudflare Tunnel) and use that `https://…` hostname instead.

---

## 🗺️ Roadmap

| Version | Focus |
|---------|-------|
| **v0.9** ✅ | Zero Trust: default-deny, persistent per-app approval prompts, deny-on-timeout |
| **v0.10** ✅ | SHA-256 tamper hashing, silent apps, profile export/import |
| **v0.11–0.13** ✅ | Event-driven kernel detection with crash-loop recovery; Packets Log; custom rules; IP blocklist; run-at-startup |
| **v0.14–0.23** ✅ | Stealth mode, directional/timed rules, system-rule library, profiles, versioned backups, Windows Firewall import, full tabbed UI, light/dark theme, VirusTotal |
| **v0.24–0.26** ✅ | Curated telemetry / update / ads blocklists; filtering-DNS selection; diagnostics export |
| **v0.27–0.28** ✅ | Dedicated Security tab; connection-table reliability; terminate process |
| **v0.29–0.31** ✅ | Honest blocklist state; **WFP fallback** when the hosts file is blocked; **ads via AdGuard DNS** |
| **v0.32** ✅ | **Authenticode signature verification** (valid / unsigned / invalid) across the app list and alerts |
| **Next** | Per-app & per-rule comments, notification polish, expanded WFP layer coverage |
| **v1.0** | Hardened service split, code signing, installer, auto-update |

---

## 📄 License

[MIT](LICENSE).

**Credits.** GeoIP data from the public-domain [iptoasn](https://iptoasn.com) dataset, served via [`jedisct1/iptoasn-webservice`](https://github.com/jedisct1/iptoasn-webservice) (BSD-2-Clause). Country flag icons from [FlagKit](https://github.com/madebybowtie/FlagKit) (MIT, public-domain artwork) — see [`Flags/LICENSE-FlagKit.txt`](src/GunWall/Flags/LICENSE-FlagKit.txt).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>
