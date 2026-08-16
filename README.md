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

*Installer or portable executable. No account, no telemetry, no ads.*

</div>

---

## What GunWall is

GunWall is a **zero-trust application firewall** for Windows. It talks directly to the **Windows Filtering Platform** — the same kernel subsystem Windows Firewall uses — and adds three things Windows does not give you:

1. **Default-deny for applications.** Nothing reaches the network until you approve it, and every decision persists.
2. **Real visibility.** Per-app bandwidth measured from the kernel, live connection inspection, traffic broken down by app, host, protocol and country.
3. **Expressive control.** Ordered per-app rule lists that match on country, network operator (ASN), address range, or network scope — not just "allow" and "block".

It is a **single portable executable**. The filtering path — the WFP engine, the rule evaluator, the DNS resolver, the rule store — is the .NET base class library and Win32 and nothing else; the interface uses one MIT-licensed control library ([WPF UI](https://github.com/lepoco/wpfui)) for its Fluent controls.

---

## Project documentation

| Document | What it covers |
|---|---|
| [`docs/DOCUMENTATION.md`](docs/DOCUMENTATION.md) | **The user guide** — installing, configuring, every screen, troubleshooting |
| [`docs/RELEASE-NOTES.md`](docs/RELEASE-NOTES.md) | What is in the current release, in plain terms |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in every release |
| [`ROADMAP.md`](ROADMAP.md) | What is open, grouped by area and risk |
| [`ROADMAP_ADVANCED.md`](ROADMAP_ADVANCED.md) | Deeper design notes for the zero-trust features |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How GunWall is built: the WFP engine, filter weights, persistence, DNS, metering |
| [`docs/TESTING.md`](docs/TESTING.md) | How to verify a build, and what to capture when something looks wrong |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Building from source, conventions, and the rules for touching kernel interop |
| [`docs/HANDOVER.md`](docs/HANDOVER.md) | Maintainer notes: known failure patterns and the checks that catch them |
| [`SECURITY.md`](SECURITY.md) | Reporting a vulnerability, and the guarantees GunWall intends to hold |


---

## Installing

**[Download the latest release](https://github.com/ox1d3x3/gunwall/releases/latest)**

- **`GunWall-<version>-setup.exe`** — installer. Recommended, because its
  uninstaller removes GunWall's kernel filters before deleting anything.
- **`GunWall.exe`** — portable. Run as administrator.

Requires Windows 10 (2004+) or Windows 11, 64-bit, with administrator rights.
Windows SmartScreen will warn you: GunWall is not code-signed, deliberately —
verify the published SHA-256 instead.

> **Before deleting a portable copy**, run *Settings → Remove all GunWall
> filtering*. The filters live in the Windows kernel and outlive the folder.

**Full instructions, first-run walkthrough and every screen explained:**
**[User Guide](docs/DOCUMENTATION.md)**

---

## Project status

**GunWall is in public beta and is in daily use as a primary firewall.**

The filtering engine, rule evaluation, monitoring, metering, DNS and blocklist
subsystems are complete. Enforcement is verified against the Windows kernel rather
than against GunWall's own reporting — you can confirm it yourself at any time
with `netsh wfp show filters`, and this README shows you how.

**What you should know before installing**

- Filters are **persistent**. Closing GunWall, a crash, or a reboot does not stop
  enforcement. That is what a firewall must do.
- There are exactly two ways to stop filtering, and both return the machine to
  Windows defaults: the **protection switch**, and **Remove all GunWall
  filtering**. The uninstaller runs the second one for you.
- If GunWall will not start at all, `GunWall.exe --unblock` restores the machine
  from a command prompt without the interface.

**What beta still means**

GunWall runs as a single elevated process. Service isolation is the last
architectural item before 1.0, and it is what separates tamper *detection* from
tamper *prevention*.

It is **deliberately not code-signed**: a certificate is a recurring cost this
free MIT project will not pass on or ask for. Each release publishes a SHA-256
instead, which you can check against source you can read.

It has been soak-tested for eleven hours at a stretch with no errors, but on a
small number of machines. Your Windows build, your VPN and your security software
are combinations nobody has tried.

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

## Privacy & security

GunWall is designed so that **nothing happens to your data without your say-so**:

- **No telemetry, no analytics, no accounts, no phoning home.** The only outbound lookups are ones you ask for: reverse-DNS for host names (the same query your OS already makes), optional VirusTotal hash checks, and blocklist updates.
- **Local-only storage.** Rules and settings live in a portable `GunWallData` folder beside the executable, falling back to `%ProgramData%\GunWall` if that's read-only — plain JSON you can read, back up, or delete.
- **Explicit actions only.** Every filter corresponds to a button you pressed. A fresh install changes nothing until you enable protection.
- **Clean removal.** Tear down every persistent filter from Settings before uninstalling.
- **Nothing third-party decides anything.** One MIT control library draws the interface; every component that inspects or blocks traffic is the .NET base class library plus Win32, and readable end to end.

---

## How it works

GunWall runs as an independent filtering layer on the Windows Filtering Platform
and **does not modify your existing Windows Firewall rules**. Both apply; either
can block a connection.

Every outbound connection is evaluated in a fixed order — lockdown, explicit
blocks, explicit allows, your custom rules in your order, then a default-deny
baseline. The first match wins.

See [How GunWall decides](docs/DOCUMENTATION.md#4-how-gunwall-decides) for the
full model, and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the engine
internals.

---

## GeoIP data (optional)

Country and network-operator lookups use the free CC0 dataset from
[iptoasn.com](https://iptoasn.com), downloaded on request from
**Connections → Download GeoIP data**. Nothing is sent anywhere: the tables are
stored locally and every lookup is answered on your machine.

If you would rather run your own lookup service, GunWall can query a self-hosted
[iptoasn-webservice](https://github.com/jedisct1/iptoasn-webservice) instead —
set the address under **Settings → GeoIP data source**.

---

## If the machine is locked and GunWall will not open

GunWall's filters are persistent by design, so a machine can be left filtering by
an application that will not start. From an **elevated** command prompt in
GunWall's folder:

```
GunWall.exe --unblock
```

This removes every filter, restores the hosts file and any adapter DNS GunWall
changed, and exits without opening a window.

Confirm it worked — with Windows, not with GunWall:

```
netsh wfp show filters file=%TEMP%\gw.xml
```

Search that file for `8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00`. **Zero matches means
the machine is back to Windows defaults.** Four matches after restarting GunWall
is also correct — those permit GunWall's own executable.

Every recovery route is covered in
[Recovery](docs/DOCUMENTATION.md#16-recovery).

---

## Roadmap

### Working today

Zero-trust default-deny with first-connection prompts · ordered custom rules
matching on address, port, protocol, domain, country and ASN · per-application
domain blocking · a curated system-rule library · lockdown and snooze · live
connections, packet log and per-application metering · a connection map · a local
DNS resolver with DNS-over-HTTPS and blocklists · Authenticode and hash
verification · VirusTotal lookups · network scanning with device identification ·
rule profiles · full diagnostics export · verified recovery to Windows defaults.

### In progress

Per-category blocklist controls · IPv6 country coverage for the map · MAC vendor
identification · mDNS device names · attributing a kernel drop to the specific
filter that caused it · list view modes.

### Planned

Pico and WSL process identification · connect-redirection and discard layers ·
tamper *prevention*, which requires the service split · per-network trust
profiles · boot-time filters · Windows Update repair · encrypted profiles ·
one-click update from inside the application · multi-language interface.

### Deliberately not planned

**Code signing.** A certificate is a recurring cost this project will not pass on
or ask for; the published SHA-256 proves more, against source you can read. A
donated open-source certificate would be used gladly, but nothing waits on it.

Full detail, grouped by area and risk, is in [`ROADMAP.md`](ROADMAP.md).

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

