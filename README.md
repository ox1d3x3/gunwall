<div align="center">

<img src="docs/logo.png" alt="GunWall" width="560"/>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-3FB868?style=flat-square)](LICENSE)
[![Dependencies](https://img.shields.io/badge/NuGet%20dependencies-0-3FB868?style=flat-square)](#-privacy--security)
[![Status](https://img.shields.io/badge/release-v0.84.0%20(beta)-E0A53F?style=flat-square)](#roadmap)

*Deny every app by default. See exactly where your traffic goes, app by app and country by country. Decide who may reach the Internet, the LAN, or nothing at all — in a fast, modern interface with no accounts, no telemetry, and no third-party dependencies.*

**[github.com/ox1d3x3/gunwall](https://github.com/ox1d3x3/gunwall)**

</div>

---

## What GunWall is

GunWall is a **zero-trust application firewall** for Windows. It talks directly to the **Windows Filtering Platform** — the same kernel subsystem Windows Firewall uses — and adds three things Windows does not give you:

1. **Default-deny for applications.** Nothing reaches the network until you approve it, and every decision persists.
2. **Real visibility.** Per-app bandwidth measured from the kernel, live connection inspection, traffic broken down by app, host, protocol and country.
3. **Expressive control.** Ordered per-app rule lists that match on country, network operator (ASN), address range, or network scope — not just "allow" and "block".

It is a **single portable executable** with **zero NuGet dependencies** — the entire supply chain is the .NET base class library and Win32.

---

## Project status

GunWall is in **beta**. The filtering engine, monitoring, metering, DNS and rule subsystems are functional and in daily use, but this is still software under active development rather than a hardened, certified security product. It runs as a single elevated process and does not yet implement service isolation or code signing.

Test it in a safe environment first, and don't rely on it as your only defense on a high-risk machine.

---

## Features

### Firewall core

- **Zero-Trust mode** — every program is denied by default and must be explicitly approved. Each new app raises an Allow / Block prompt with an auto-decision countdown; your choice persists. Loopback and core Windows networking stay allowed so the machine keeps working.
- **Per-app rules** — allow or block any executable, in either direction, with optional **timed** (auto-expiring) and **silent** (muted) variants. Critical system processes are guarded against accidental blocking.
- **Per-app access rules** — an **ordered, first-match-wins** policy per application. Rules target *entities*: country, continent, ASN, IP, address range (CIDR), or network scope, each set to allow or block, with a default action when nothing matches. Presets included (*Allow LAN only*, *Allow one country only*, and more).
- **Network scopes** — per-app force-blocks by destination: **device-local**, **LAN**, **Internet** (LAN-only mode), **incoming**, **server / listening sockets** (denies the app any listening port, TCP or UDP), and **P2P / direct** (connections to addresses the app never resolved through DNS).
- **Country & ASN blocking** — block an app, or every app, from reaching a whole country, continent, or network operator.
- **Custom rules** — block or allow by remote IP / CIDR, port, protocol and direction, independent of any app.
- **Block routed traffic** — stop the machine acting as a router for a bridged VM, mesh-VPN peer, or shared connection. Traffic merely *passing through* never reaches the usual filtering layers, so this closes a gap most desktop firewalls leave open.
- **Lockdown** — cut all traffic instantly from the app or the tray, including routed traffic.
- **Stealth mode** — drop unsolicited inbound connections and ICMP error replies so the machine stops answering probes.

### Monitoring & visibility

- **Precise per-app metering** — an optional ETW kernel session attributes bandwidth to processes from the Windows kernel network provider itself. A tested estimation engine runs as an automatic fallback, so usage data is never lost if metering is unavailable.
- **Apps Usage Timeline** — drag across the timeline to select any period and instantly see which applications were active in it, busiest first.
- **Traffic breakdown** — the current session split four ways: **apps**, **remote hosts** (with reverse-DNS names), **traffic type** (HTTPS, QUIC, DNS, VPN protocols, RDP, BitTorrent and more), and **countries**.
- **World map** — live connection arcs from your location to the busiest destinations.
- **Live throughput graph** — smooth download / upload chart with hover readout and session totals, plus a persistent status bar showing rates, totals, protection state and metering mode.
- **Connection inspector** — every live TCP connection and UDP socket (IPv4 + IPv6) with owning process, endpoints, state, country and ASN, with instant search. Right-click to close a connection, block the app, or terminate the process.
- **Packets Log** — a live, searchable, color-coded log of every connection event, with the **reason** for each verdict, exportable to CSV.
- **Network scanner** — discover devices on your LAN (IP, MAC, host name).
- **Notification center** — session alerts for protection changes, threats and network events, with an unread badge.

### DNS

- **Secure DNS (DoH)** — forward every lookup encrypted over HTTPS, so nobody on the network can read or tamper with what you resolve. Built-in providers are IP-addressed, so enabling encryption needs no plaintext lookup to bootstrap itself, and the default is to **fail closed** rather than silently downgrade.
- **CNAME-cloaking defence** — trackers dodge blocklists by aliasing a clean first-party name to a blocked one. GunWall follows each answer's alias chain and refuses the lookup if any hop is blocked.
- **Built-in resolver** — a from-scratch DNS resolver with caching, blocklist filtering and optional system-wide redirection, VPN-aware so it cooperates with an active tunnel.
- **Domain blocklists** — load a curated list (StevenBlack unified hosts, ~100k domains) or your own, applied at resolution time.
- **Suspicious-domain heuristics** — algorithmically generated domain names (a common malware signal) are scored and flagged using entropy, character-distribution and structural analysis.
- **Filtering DNS** — alternatively point Windows at a public filtering resolver (AdGuard for ads/trackers, Quad9 for malware/phishing).
- **Captive portal helper** — detects hotel/airport login pages and offers a temporary portal mode so you can get online.

### App trust & verification

- **Authenticode signature verification** — GunWall *validates* each program's digital signature with `WinVerifyTrust`, marking apps **Valid signature**, **Unsigned**, or **Invalid signature**, so a tampered or forged binary is flagged rather than trusted.
- **Tamper detection** — each rule stores the executable's SHA-256, so a swapped binary at the same path is detectable.
- **VirusTotal lookup** — check an app's hash against VirusTotal with your own API key; only the hash ever leaves the machine.

### Management

- **Rule profiles** — save and switch named rule sets (e.g. Home / Work / Travel).
- **Versioned backups** — automatic and on-demand snapshots of all rules and settings, restorable in one click.
- **Windows Firewall integration** — read its status, toggle it, and import its block rules.
- **Kernel self-test** — verify which Windows Filtering Platform layers and conditions this build of Windows accepts. A test filter is added and immediately removed on each; nothing is changed or left behind.
- **Health & diagnostics** — an app-health panel, a session error log, and a one-click diagnostics export bundling config, logs and network state.
- **Run at startup** — launch with Windows, elevated, without a UAC prompt, via a scheduled task.
- **Close to tray** — closing minimizes to the tray; a true exit warns if filtering is still active.
- **Themes** — matching dark and light themes with an animated switch.

---

## Installing

### Requirements

- **Windows 10 (2004+) or Windows 11**, 64-bit
- **Administrator privileges** — WFP cannot add or remove filters otherwise. The manifest requests elevation automatically.

### Build from source

Prerequisites: **Visual Studio 2022** (17.8+) with the **.NET desktop development** workload, or the standalone [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

**Visual Studio**

1. Open `GunWall.sln`.
2. Set configuration **Release**, platform **x64**.
3. **Build → Build Solution** (`Ctrl+Shift+B`).
4. The executable appears in `src/GunWall/bin/Release/net8.0-windows/GunWall.exe`.

**Command line**

```powershell
dotnet build GunWall.sln -c Release
```

Single self-contained executable:

```powershell
dotnet publish src/GunWall/GunWall.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true
```

### First run

GunWall starts in **monitoring only** — it observes traffic and changes nothing until you turn protection on. A good first session:

1. Watch the Dashboard and Apps list for a few minutes to see what your machine actually talks to.
2. Enable **Zero-Trust mode** when you're ready to start approving apps.
3. Optionally enable **precise metering** (Settings → experimental) for kernel-measured per-app bandwidth.

> **Antivirus note:** a firewall legitimately performs the same low-level operations malware does — modifying the hosts file, changing DNS, creating packet filters, terminating processes. Some behavioral engines may flag an **unsigned** build with a generic heuristic detection, especially when run from a `Downloads` folder. Build in **Release**, run from a stable folder, and add GunWall to your antivirus exclusions if needed. Code signing is the long-term fix.

---

## Privacy & security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No telemetry, no analytics, no accounts, no phoning home.** The only outbound lookups are ones you ask for: reverse-DNS for host names (the same query your OS already makes), optional VirusTotal hash checks, and blocklist updates.
- **Local-only storage.** Rules and settings live in a portable `GunWallData` folder beside the executable, falling back to `%ProgramData%\GunWall` if that's read-only — plain JSON you can read, back up, or delete.
- **Explicit actions only.** Every filter corresponds to a button you pressed. A fresh install changes nothing until you enable protection.
- **Clean removal.** Tear down every persistent filter from Settings before uninstalling.
- **Zero third-party packages.** The whole supply chain is the .NET base class library plus Win32 — trivially auditable.

---

## How it works

GunWall runs as an independent filtering layer and does **not** modify your existing Windows Firewall rules.

```
┌──────────────────────────────────────────────────────────┐
│  WPF UI — dashboard · apps · traffic · connections ·      │
│  packets · DNS · rules · security · network · settings    │
├──────────────────────────────────────────────────────────┤
│  Event-driven detection loop + background sampler         │
│  FirewallManager · AppRuleEngine · RuleStore (JSON)       │
│  DnsResolver · GeoIP · ETW meter · usage & stats services │
├──────────────────────────────────────────────────────────┤
│  WfpEngine → fwpuclnt.dll   ·   WinVerifyTrust            │
│  ETW (advapi32) · hosts file · DNS · scheduled task       │
└──────────────────────────────────────────────────────────┘
```

Detection is **event-driven** off the WFP kernel event stream, not polling. Blocking an app adds persistent WFP filters (outbound + inbound, IPv4 + IPv6) keyed to the executable; Zero-Trust adds a base block plus per-app permits. Entity rules (country, ASN, scope) are enforced **reactively** — GunWall evaluates a connection when it appears and installs a matching filter. Filter IDs are persisted so every filter can be cleanly removed later, even across restarts.

Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## GeoIP data source (optional, self-hosted)

GunWall labels connections with their **country and network operator (ASN)** and uses that for country/ASN rules. Choose the source under **Settings → Security & Privacy → GeoIP data source**:

- **Local database** *(default)* — GunWall downloads the free, public-domain [iptoasn](https://iptoasn.com) IPv4 table on demand and resolves addresses entirely on your machine. No server, no setup.
- **Self-hosted API server** — GunWall asks a small HTTP service you run yourself. Nothing to download, always fresh, and it resolves **IPv6** too. Lookups are cached, so each address is fetched only once.

> GunWall ships with **no** API URL and never contacts anyone else's server. In API mode you point it at your own endpoint — nothing is hard-coded and there is no shared default host.

### Running your own server

The service is [`jedisct1/iptoasn-webservice`](https://github.com/jedisct1/iptoasn-webservice) (BSD-2-Clause). It refreshes the dataset itself and answers `GET /v1/as/ip/<ip>` with JSON. The stock image targets an older Rust toolchain; this two-stage Dockerfile builds cleanly on current Docker:

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

Build and run it (mapping host port `53662` to the container's `53661`):

```bash
docker build -t iptoasn .
docker run -d --name iptoasn --restart unless-stopped -p 53662:53661 iptoasn
```

Confirm it's up — this should report Google, `AS15169`, `US`:

```bash
curl -H "Accept: application/json" http://YOUR_SERVER:53662/v1/as/ip/8.8.8.8
```

Then in GunWall: **Settings → Security & Privacy → GeoIP data source → Self-hosted API server**, enter `http://YOUR_SERVER:53662`, click **Test**, then **Save**.

---

## Roadmap

### Delivered

| Milestone | Focus |
|---|---|
| **v0.9 – v0.13** | Zero-Trust default-deny with persistent approval; SHA-256 tamper hashing; event-driven kernel detection with crash-loop recovery; Packets Log; custom rules |
| **v0.14 – v0.28** | Stealth mode; directional & timed rules; system-rule library; profiles; versioned backups; Windows Firewall import; light/dark themes; VirusTotal |
| **v0.29 – v0.40** | Blocklists with WFP fallback; filtering DNS; Authenticode signature verification; diagnostics export |
| **v0.41 – v0.60** | Network scopes; GeoIP with country & ASN blocking; built-in DNS resolver; domain heuristics; verdict reasons; captive portal helper; notification center |
| **v0.61 – v0.75** | ETW per-app metering; Apps Usage Timeline; traffic breakdown by app/host/protocol/country; world map; status bar; per-app activity sparklines |
| **v0.76 – v0.77** | Block-Internet and P2P/direct scopes; **ordered per-app entity rule engine** with presets |
| **v0.78 – v0.79** | **Secure DNS (DNS-over-HTTPS)** with fail-closed default; **CNAME-cloaking defence** |
| **v0.80 – v0.84** | Expanded kernel layer coverage (routed traffic, port binding); server-socket scope; kernel layer **self-test**; notification exclusions; error-log viewer; tray and view options |

### Planned

**Near term**

- Domain and filter-list entities for access rules
- Per-service attribution for `svchost`-hosted services
- Anti-hijack protection for DNS settings

**Medium term**

- Per-network trust profiles (home / work / public)
- Connection-redirection layer support
- List view modes and further interface options

**Toward v1.0**

- Hardened service split and privilege separation
- Code signing, installer, and auto-update
- Tamper-resistant filter protection

---

## Project documentation

| Document | What it covers |
|---|---|
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in every release |
| [`ROADMAP.md`](ROADMAP.md) | Planned work, by phase |
| [`ROADMAP_ADVANCED.md`](ROADMAP_ADVANCED.md) | Deeper design notes for the zero-trust features |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How GunWall is built: the WFP engine, filter weights, persistence, DNS, metering |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Build setup, conventions, and the rules for touching kernel interop |
| [`SECURITY.md`](SECURITY.md) | Reporting a vulnerability, and the guarantees GunWall intends to hold |

---

## License

[MIT](LICENSE).

**Credits.** GeoIP data from the public-domain [iptoasn](https://iptoasn.com) dataset, served via [`jedisct1/iptoasn-webservice`](https://github.com/jedisct1/iptoasn-webservice) (BSD-2-Clause). Domain blocklist from [StevenBlack/hosts](https://github.com/StevenBlack/hosts) (MIT). Country flag icons from [FlagKit](https://github.com/madebybowtie/FlagKit) (MIT) — see [`Flags/LICENSE-FlagKit.txt`](src/GunWall/Flags/LICENSE-FlagKit.txt).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>
