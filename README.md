<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/png/lockup-horizontal-reversed.png">
  <img src="branding/png/lockup-horizontal.png" alt="GunWall" width="520"/>
</picture>

### A modern, open-source firewall for Windows 11, built on the Windows Filtering Platform

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-3FB868?style=flat-square)](LICENSE)
[![Dependencies](https://img.shields.io/badge/filtering%20path-no%20dependencies-3DA9FC?style=flat-square)](#privacy-security)
[![Status](https://img.shields.io/badge/release-beta-E0A53F?style=flat-square)](#project-status)
[![Latest release](https://img.shields.io/github/v/release/ox1d3x3/gunwall?style=flat-square&color=3FB868&include_prereleases&label=latest)](https://github.com/ox1d3x3/gunwall/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/ox1d3x3/gunwall/total?style=flat-square&color=3DA9FC&label=downloads)](https://github.com/ox1d3x3/gunwall/releases)
[![Issues](https://img.shields.io/github/issues/ox1d3x3/gunwall?style=flat-square&color=E0A53F)](https://github.com/ox1d3x3/gunwall/issues)

*Deny every app by default. See exactly where your traffic goes, app by app and country by country. Decide who may reach the Internet, the LAN, or nothing at all — in a fast, modern interface with no accounts, no telemetry, and nothing between you and the kernel.*

### [⬇ Download the latest beta](https://github.com/ox1d3x3/gunwall/releases/latest)

*One portable `.exe`. No installer, no account, no telemetry. Run it as administrator and it works.*

</div>

---

## What GunWall is

GunWall is a **zero-trust application firewall** for Windows. It talks directly to the **Windows Filtering Platform** — the same kernel subsystem Windows Firewall uses — and adds three things Windows does not give you:

1. **Default-deny for applications.** Nothing reaches the network until you approve it, and every decision persists.
2. **Real visibility.** Per-app bandwidth measured from the kernel, live connection inspection, traffic broken down by app, host, protocol and country.
3. **Expressive control.** Ordered per-app rule lists that match on country, network operator (ASN), address range, or network scope — not just "allow" and "block".

It is a **single portable executable**. The filtering path — the WFP engine, the rule evaluator, the DNS resolver, the rule store — is the .NET base class library and Win32 and nothing else; the interface uses one MIT-licensed control library ([WPF UI](https://github.com/lepoco/wpfui)) for its Fluent controls.

---

## Project status

**GunWall is in public beta and is doing its job.** The filtering engine, rule
evaluation, monitoring, metering and DNS subsystems are complete and in daily use
on real hardware. Enforcement is verified against the kernel rather than against
GunWall's own reporting — you can confirm it independently at any time with
`netsh wfp show filters`, and the README shows you how.

**What that means in practice**

- Every filter GunWall installs is **persistent**: closing the app, a crash, or a
  reboot does not stop enforcement. That is what a firewall must do.
- Because of that, there are exactly two ways to stop filtering, and **both are
  verified to return the machine to Windows defaults**: the protection switch, and
  *Remove all GunWall filtering*.
- If GunWall will not open at all, `GunWall.exe --unblock` restores the machine
  from a command prompt without the interface. See
  [If the machine is locked](#if-the-machine-is-locked-and-gunwall-will-not-open).

**What beta still means**

It runs as a single elevated process, with no service isolation yet — that is the
last architectural item before 1.0. GunWall is **deliberately not code-signed and
has no installer**: a certificate is a recurring cost this project will not pass
on to anyone, and a portable executable is a design choice rather than a gap. It
has been
soak-tested for 11 hours at a time with zero errors, but on a small number of
machines. Your Windows build, your VPN and your antivirus are combinations nobody
has tried yet.

Use it. Report what breaks. Don't make it the only thing standing between a
high-risk machine and the Internet just yet.

---

## Features

### Firewall core

- **Zero-Trust mode** — every program is denied by default and must be explicitly approved. Each new app raises an Allow / Block prompt that waits for you, and **dismissing it blocks** — a prompt closed by accident never grants access. Your choice persists. Loopback and core Windows networking stay allowed so the machine keeps working.
- **Per-app rules** — allow or block any executable, in either direction, with optional **timed** (auto-expiring) and **silent** (muted) variants. Critical system processes are guarded against accidental blocking.
- **Per-app access rules** — an **ordered, first-match-wins** policy per application. Rules target *entities*: **domain**, country, continent, ASN, IP, address range (CIDR), or network scope, each set to allow or block, with a default action when nothing matches. Presets included (*Allow LAN only*, *Allow one country only*, and more).
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
- **Per-service attribution** — service hosts are named, so a connection reads `svchost (Windows Update)` instead of one of a dozen identical `svchost` rows. A rule you can't explain isn't really control.
- **Per-service rules** — block one Windows service without touching the others sharing its process, so stopping telemetry doesn't also stop Windows Update.
- **Packets Log** — a live, searchable, color-coded log of every connection event, with the **reason** for each verdict, exportable to CSV.
- **Network scanner** — discover devices on your LAN (IP, MAC, host name).
- **Notification center** — session alerts for protection changes, threats and network events, with an unread badge.

### DNS

- **Secure DNS (DoH)** — forward every lookup encrypted over HTTPS, so nobody on the network can read or tamper with what you resolve. Built-in providers are IP-addressed, so enabling encryption needs no plaintext lookup to bootstrap itself, and the default is to **fail closed** rather than silently downgrade.
- **CNAME-cloaking defence** — trackers dodge blocklists by aliasing a clean first-party name to a blocked one. GunWall follows each answer's alias chain and refuses the lookup if any hop is blocked.
- **Built-in resolver** — a from-scratch DNS resolver with caching and blocklist filtering, bound to loopback only. GunWall never changes this PC's DNS settings; point something at it deliberately to use it.
- **Passive DNS watch** — GunWall reads the lookup events Windows already emits, so domain rules and "block direct connections" know which name produced an address. Nothing is intercepted, redirected or answered.
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
- **Tamper detection** — GunWall checks that its own filters are still installed and puts them back if something removes them, so interference is visible and short-lived rather than silent.
- **Health & diagnostics** — an app-health panel, a session error log, and a one-click diagnostics export bundling config, logs and network state.
- **Run at startup** — launch with Windows, elevated, without a UAC prompt, via a scheduled task.
- **Close to tray** — closing minimizes to the tray; a true exit warns if filtering is still active.
- **Themes** — matching dark and light themes with an animated switch.

---

## Installing

### Requirements

- **Windows 10 (2004+) or Windows 11**, 64-bit
- **Administrator privileges** — WFP cannot add or remove filters otherwise. The manifest requests elevation automatically.

### Download (recommended)

1. Go to **[Releases](https://github.com/ox1d3x3/gunwall/releases/latest)** and
   download `GunWall.exe`.
2. Right-click → **Run as administrator**. There is no installer and nothing is
   written outside the folder you run it from.
3. **Windows SmartScreen will warn you.** GunWall is not code-signed — a
   certificate costs money every year, and this is a free MIT project that would
   rather not charge for one or beg for it. Choose **More info → Run anyway**, or
   verify the SHA-256 checksum published with each release first, which proves the
   file is the one that was built from this source.

**Verifying what you downloaded.** Each release lists the SHA-256 of the
executable. Check it before running:

```
certutil -hashfile GunWall.exe SHA256
```

If that matches the published value, the file is byte-for-byte the one built from
this source. That is a stronger guarantee than a code-signing certificate gives
you — a signature says *someone paid for a certificate*, a checksum against public
source says *this is that source, compiled*.

**Removing it.** GunWall is portable, but its filters live in the Windows kernel
and persist by design. **Use *Settings → Remove all GunWall filtering* before
deleting the folder** — otherwise the machine keeps enforcing rules with nothing
installed to manage them. If that already happened, see
[If the machine is locked](#if-the-machine-is-locked-and-gunwall-will-not-open).

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

**Expect prompts, and expect to be busy for the first ten minutes.** Default-deny
means every program asks once. Approve the ones you recognise; the ones you do not
are the point of the exercise.

**Two settings worth knowing about before you turn protection on:**

| Setting | Why it matters |
|---|---|
| *Settings → Popup stays open for* | Defaults to **Never**, so a prompt waits for you. If you set a timeout, anything you do not answer in that window gets a **permanent** rule — including programs you needed. |
| *Settings → Run GunWall when Windows starts* | Off by default. With it off, filters still enforce after a reboot but nothing can prompt you, so new programs fail silently until you open GunWall. |

> **Antivirus note:** a firewall legitimately performs the same low-level operations malware does — modifying the hosts file, changing DNS, creating packet filters, terminating processes. Some behavioral engines may flag an **unsigned** build with a generic heuristic detection, especially when run from a `Downloads` folder. Build in **Release**, run from a stable folder, and add GunWall to your antivirus exclusions if needed. This is the trade that comes with an unsigned open-source binary, and it is a deliberate one — the source is here to read and build yourself.

---

## Privacy & security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No telemetry, no analytics, no accounts, no phoning home.** The only outbound lookups are ones you ask for: reverse-DNS for host names (the same query your OS already makes), optional VirusTotal hash checks, and blocklist updates.
- **Local-only storage.** Rules and settings live in a portable `GunWallData` folder beside the executable, falling back to `%ProgramData%\GunWall` if that's read-only — plain JSON you can read, back up, or delete.
- **Explicit actions only.** Every filter corresponds to a button you pressed. A fresh install changes nothing until you enable protection.
- **Clean removal.** Tear down every persistent filter from Settings before uninstalling.
- **Nothing third-party decides anything.** One MIT control library draws the interface; every component that inspects or blocks traffic is the .NET base class library plus Win32, and readable end to end.

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

## If the machine is locked and GunWall will not open

GunWall's filters are persistent by design — they keep enforcing after a crash, a
close, or a reboot. If GunWall is not running, nothing can prompt, so a program
without a rule fails silently.

From an **elevated** command prompt, in GunWall's folder:

```
GunWall.exe --unblock
```

This removes every filter, restores the hosts file and any adapter DNS GunWall
changed, prints what it did, and exits without opening a window. It runs before
any interface is built, so a broken window cannot stop it.

Verify it worked:

```
netsh wfp show filters file=%TEMP%\gw.xml
```

then search that file for `8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00`. **Zero matches
means the machine is back to Windows defaults** — and that is Windows reporting
it, not GunWall reporting on itself.

Check *before* reopening GunWall. Starting it again immediately installs four
filters that permit GunWall's own executable, so **four matches after a restart is
also correct** and does not mean anything else is being filtered.

## Roadmap

GunWall is not planned by version number. Work is grouped by what it touches and
how risky it is; things land when they are ready and verified on hardware, not on
a schedule. [`CHANGELOG.md`](CHANGELOG.md) records what shipped and when —
this section is about what exists and what is still open.

### What works today

| Area | Capability |
|---|---|
| **Enforcement** | Zero-trust default-deny with persistent per-app approval · lockdown · stealth mode · directional, timed and silent rules · critical-process protection |
| **Rules** | Custom rules by address, CIDR, port, protocol and direction · manual IP blocklist · curated system-rule library · ordered per-app entity rules with presets |
| **Scopes** | Block device-local, LAN, incoming, Internet, P2P/direct, and listening sockets — per application |
| **Kernel coverage** | 16 WFP layers wired and verified on hardware, v4 and v6 · kernel layer self-test · filter tamper detection with self-healing |
| **Visibility** | Connection inspector · live packet log · throughput graph · activity feed · per-app metering from ETW · traffic by app, host, protocol and country · LAN scanner |
| **App trust** | Authenticode signature verification · SHA-256 tamper detection · VirusTotal hash lookup · per-app properties and notes |
| **DNS** | Built-in resolver · DNS-over-HTTPS with a fail-closed default · CNAME-cloaking defence · domain heuristics · filtering-DNS selection · resolver self-check on start |
| **Blocking** | Telemetry and update blocklists with WFP fallback when the hosts file is unavailable · ads and trackers via filtering DNS · explicit `@@` allow entries that override any list · domain blocks enforced **per application**, so blocking a tracker cannot cut off anything else sharing its address |
| **Recovery** | Reset returns the machine to Windows defaults, including the hosts file and adapter DNS · orphaned filters reconciled at every start · `--unblock` command-line recovery when the interface cannot be opened |
| **Management** | Profiles · versioned backups · Windows Firewall import · diagnostics export · run at startup · close to tray · notification centre |

### What is open

Grouped by risk rather than order. The full list, with detail, is in
[`ROADMAP.md`](ROADMAP.md).

**No kernel risk** — per-category blocklist controls in the interface · an extra
curated list · excluding chosen apps from blocklists · per-network trust profiles ·
list view modes and further interface options · remaining colour-category
customisation · pico and subsystem process identification.

**Touches the filter set** — connect-redirection and discard layers · true filter
tamper *prevention*, which needs a privilege split first because an elevated user
process cannot lock out other administrators without locking out itself.

**Needs a guaranteed recovery path before it can ship** — boot-time filters ·
Windows Update service repair · compressed and encrypted profile formats.

**Before 1.0** — service split and privilege separation · multi-language
interface.

**Deliberately not planned** — code signing and an installer. A certificate is a
recurring cost, and GunWall stays portable by design: one executable, no registry
entries, no install state, updated by replacing the file. If someone donates an
open-source signing certificate it will be used gladly, but nothing here is
waiting on it.

### Known limitations

Stated plainly, because you will meet them:

- **Blocking a domain hosted on a large CDN is unreliable.** GunWall blocks the
  addresses it has observed a name resolve to; Cloudflare and similar services
  rotate faster than that. Domain blocking works well against trackers on stable
  hosts. It will not reliably stop a major website.
- **A closed GunWall cannot prompt.** Filters persist, so an unapproved program is
  correctly denied — but with the app shut, there is no prompt and no way to grant
  access, and the program simply fails. Enable *Run at startup* if that matters.
- **Not code-signed**, by choice, so SmartScreen and some antivirus heuristics
  will complain about an unsigned binary performing firewall operations. Verify
  the published SHA-256 instead.
- **Single elevated process.** Service isolation is a pre-1.0 item.

---

## Project documentation

| Document | What it covers |
|---|---|
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in every release |
| [`ROADMAP.md`](ROADMAP.md) | What is open, grouped by area and risk |
| [`ROADMAP_ADVANCED.md`](ROADMAP_ADVANCED.md) | Deeper design notes for the zero-trust features |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How GunWall is built: the WFP engine, filter weights, persistence, DNS, metering |
| [`docs/TESTING.md`](docs/TESTING.md) | How to verify a build, and what to capture when something looks wrong |
| [`docs/HANDOVER.md`](docs/HANDOVER.md) | What keeps going wrong, what stops it now, and the deviations that are deliberate |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Build setup, conventions, and the rules for touching kernel interop |
| [`SECURITY.md`](SECURITY.md) | Reporting a vulnerability, and the guarantees GunWall intends to hold |

---

## Reporting a problem

Beta feedback is the most useful thing you can send. What makes a report
actionable:

1. **Settings → Export diagnostics (.zip)** — it contains the session log,
   your settings with secrets removed, the active rules and your network
   configuration. **No browsing history and no personal data.**
2. **Describe what you saw, not what you think caused it.** "The firewall says it
   is off when it is on" once led straight to a bug that turned out to be invisible
   text — a diagnosis would have sent us the wrong way.
3. **A full-window screenshot** if it is visual. A crop hides what an element is
   being measured against.

Open an issue at
**[github.com/ox1d3x3/gunwall/issues](https://github.com/ox1d3x3/gunwall/issues)**.

If something is badly broken and you need the machine working *now*, run
`GunWall.exe --unblock` first, then report — recovery does not destroy the log.

---

## License

[MIT](LICENSE).

**Credits.** GeoIP data from the public-domain [iptoasn](https://iptoasn.com) dataset, served via [`jedisct1/iptoasn-webservice`](https://github.com/jedisct1/iptoasn-webservice) (BSD-2-Clause). Domain blocklist from [StevenBlack/hosts](https://github.com/StevenBlack/hosts) (MIT). Country flag icons from [FlagKit](https://github.com/madebybowtie/FlagKit) (MIT) — see [`Flags/LICENSE-FlagKit.txt`](src/GunWall/Flags/LICENSE-FlagKit.txt).

---

<div align="center">
<sub>Guard your network. Bismillah.</sub>
</div>


## Brand

The mark is a wall of sixteen stones with three that are not ordinary: one
missing, one stopped in red, and one drawn as an outline — a barrier, a block,
and something under watch. Source artwork and every derived size live in
[`branding/`](branding/) (SVG, PNG, lockups and app icons).

The red in the mark is deliberately *not* the interface accent. In GunWall red
means blocked, so the logo says the same thing the application does.

