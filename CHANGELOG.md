# Changelog

All notable changes to GunWall are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with a `0.x` pre-1.0 series.

---

## [0.89.0] — 2026-07-25

### Fixed
- **Internet dropping when this PC's DNS is routed through GunWall.** Two problems compounded. First, the DoH client used a single shared connection with a global 5-second timeout, so against a slower endpoint one slow request's timeout could abort the shared connection and break the next several in flight — seen as bursts of failures (one endpoint measured 208 aborted lookups against 82 successes, while a faster endpoint had zero). The client now uses a pooled, kept-warm `SocketsHttpHandler` with no global timeout; each query is bounded by its own token and retried once on the warm connection. Second, and more seriously, when a lookup did fail while the machine's own DNS pointed at GunWall, the OS got no answer and the machine appeared to lose internet. GunWall now keeps a plaintext path of last resort **whenever system DNS is routed through it**, so routed resolution can never black-hole — while still honouring fail-closed when GunWall is only an opt-in resolver for other clients.

---

## [0.88.0] — 2026-07-24

### Added
- **Domain rules in the access-rule engine.** A per-app rule can now target a domain — `block doubleclick.net`, or `allow github.com` above a block-all — completing the entity set alongside country, continent, ASN, IP, range and scope. Matching covers subdomains by default (`example.com` matches `cdn.example.com`) and is label-aware, so `evilexample.com` never matches `example.com`. An explicit `*.` prefix is accepted and behaves identically.
- The resolver now records which name produced each resolved address, which is what allows a rule about a *name* to be enforced against a connection that only carries an *address*. Domain rules therefore require GunWall's resolver to be running, and the block alert names the domain that matched.

---

## [0.87.0] — 2026-07-24

### Fixed
- **GeoIP silence.** With the local database never downloaded, every country and ASN lookup returned nothing — so the Countries breakdown was empty and country, continent and ASN rules could never match — with no indication anywhere that this was the cause. An empty Countries column read as "no foreign traffic" when it actually meant "no data". The state is now reported in three places: the App Health card, the diagnostics export, and an inline note on the Countries column itself.

---

## [0.86.0] — 2026-07-24

### Changed
- **The approval prompt now fails closed.** It no longer auto-decides by default — it waits for the user — and if the prompt is dismissed or closed by accident, the app is **blocked** rather than silently allowed. Previously the default was to auto-*allow* after 20 seconds, and closing the window decided nothing. A firewall that grants access to something you walked away from is not doing its job. The settings still allow a timeout, but the safe options are listed first and a missing setting falls back to block.
- **Service labels adapt to the column width.** Long Windows service display names were truncating mid-word; the label now fits as many names as will show and appends a count, with the full list in the tooltip.

---

## [0.85.0] — 2026-07-24

### Added
- **Per-service attribution.** Connections opened by service hosts now name the actual Windows service — `svchost (Windows Update, BITS)` rather than an indistinguishable row of `svchost` entries — with the full hosted-service list in a tooltip. The Apps list shows a service badge on host executables. Resolved through `EnumServicesStatusEx`, cached for 20 seconds, with the struct layout and constants verified against the Windows SDK headers and Microsoft's win32metadata.
- Service attribution counters in the diagnostics export.

---

## [0.84.0] — 2026-07-24

### Fixed
- **Error-log flooding.** Faults caused by GunWall itself cutting the network — lockdown, a new rule, the resolver stopping, an adapter dropping — abort in-flight sockets and pooled HTTPS connections. These are expected consequences, not defects, and a single lockdown could previously write 118 near-identical exception blocks to the log, crowding real errors out of the 300-entry error buffer. They are now classified and counted rather than logged individually. The classifier inspects every exception in an aggregate and treats anything unexpected as a real error.
- **Duplicate error entries.** The error buffer now deduplicates by context + type + message, collapsing repeats into one entry with a count and last-seen time. The log file records the full stack trace once, then milestone lines only (2nd, 10th, 100th repeat).

### Added
- Error counts (`N distinct, M total`) and benign-fault tallies in both the error-log viewer and the diagnostics export.

---

## [0.83.0] — 2026-07-24

### Fixed
- **Three incorrect Windows Filtering Platform GUIDs**, found by the new self-test and verified against the Windows SDK headers and Microsoft's win32metadata:
  - `FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4` — did not exist, so stealth mode's IPv4 ICMP-error suppression silently never installed.
  - `FWPM_CONDITION_IP_REMOTE_PORT` — invalid, so any filter carrying a remote-port condition failed to install.
  - `FWPM_CONDITION_ICMP_TYPE` — pointed at an unrelated real condition (`ALE_SIO_FIREWALL_SYSTEM_PORT`); the SDK defines it as an alias of `IP_LOCAL_PORT`.

### Added
- Condition-field probing in the kernel self-test, covering the class of bug a layer probe structurally cannot catch.

---

## [0.82.0] — 2026-07-24

### Added
- **Kernel layer self-test** (Settings → Diagnostics → *Verify kernel layers*). Probes every WFP layer GunWall uses and reports which this build of Windows accepts, with the error code for any rejection. The probe is a permit filter at weight 0, non-persistent, deleted immediately, so it cannot block traffic, outrank any existing filter, or survive a crash.
- Lockdown and system-rule application are now logged with filter counts, including a warning when a rule installs zero filters.

---

## [0.81.0] — 2026-07-24

### Added
- **Notification exclusions.** Alerts are categorised (security, protection changes, network, rules and profiles) and each category can be silenced independently.
- **Error-log viewer** (Settings → Diagnostics) showing this session's captured errors, with copy, clear, and refresh.
- **Tray single-click** to restore (opt-in; double-click always works).
- **Fit columns to content** in the Apps and Connections context menus.
- **UI size** setting (90% – 125%).

---

## [0.80.0] — 2026-07-24

### Added
- **Block routed (transit) traffic** system rule, closing a real gap: traffic merely routed through the machine — via a bridged VM, mesh VPN peer, or Internet Connection Sharing — never reaches the ALE layers and was previously unfiltered.
- **Block server / listening sockets** per-app scope, denying an application the server role outright across bind (TCP and UDP), listen, and accept.
- Lockdown now covers the forwarding layers, so "block everything" includes transit traffic.
- New WFP layers wired: `IPFORWARD` and `ALE_RESOURCE_ASSIGNMENT`, v4 and v6.

### Fixed
- Optional filters skipped by the kernel were reported only to the debugger and were therefore invisible in release builds. They now reach the diagnostics log, deduplicated.

---

## [0.79.0] — 2026-07-23

### Added
- **CNAME-cloaking defence.** Trackers evade domain blocklists by having a clean first-party name alias to a blocked one. The resolver now follows each answer's CNAME chain and denies the lookup if any hop is blocked. Cloaked answers are never cached.
- Full DNS name reader with compression-pointer support, loop guards, and bounds checking.
- *Cloaked* counter and log verdict in the DNS panel.

---

## [0.78.0] — 2026-07-23

### Added
- **Secure DNS (DNS-over-HTTPS, RFC 8484).** Queries can be forwarded encrypted over HTTPS. Built-in endpoints are IP-addressed, so enabling DoH needs no plaintext lookup to bootstrap itself.
- Fail-closed by default: if the encrypted resolver is unreachable, lookups fail rather than silently downgrading. Plaintext fallback is opt-in and stays with the same provider.

---

## [0.77.0] — 2026-07-22

### Added
- **Per-app entity rule engine.** Each application can carry an ordered, first-match-wins access policy. Rules match on country, continent, ASN, IP, CIDR range, network scope, or any, each set to allow or block, with a configurable default action. Includes an editor with reordering, enable/disable, and presets.

---

## [0.76.0] — 2026-07-22

### Added
- **Block Internet (allow LAN only)** per-app scope, enforced by 46 IPv4 CIDR filters covering exactly the public address space plus `2000::/3` for IPv6.
- **Block P2P / direct connections** per-app scope: connections to public addresses the application never resolved by name are blocked reactively and the session torn down. Requires GunWall's resolver to be running.

---

## [0.75.0] — 2026-07-22

### Added
- Animated connection arcs on the world map, from the local region to the busiest destinations.
- Per-app activity sparklines in the Apps list.

---

## [0.74.0] — 2026-07-22

### Added
- **Traffic breakdown** card splitting the session four ways: applications, remote hosts (with reverse-DNS names), traffic type, and countries, each with per-row bars.
- Port and protocol classifier covering HTTPS, QUIC, DNS, mail, VPN protocols, RDP, SMB, BitTorrent, discovery protocols, and more.

---

## [0.73.0] — 2026-07-22

### Added
- **Apps Usage Timeline.** Drag across the timeline to select any period and see which applications were active in it, busiest first, updating live during the drag.

---

## [0.72.0] — 2026-07-21

### Added
- Persistent footer status bar with live rates, session totals, protection state, metering mode, and unread alerts.
- Graph hover readout with cursor line and time-axis labels.

---

## [0.70.0] — 2026-07-21

### Added
- **Precise per-app metering** via an ETW kernel-network session, attributing bandwidth to processes from the kernel itself. Off by default; the estimation engine remains an automatic fallback, and a watchdog degrades to it if the session stops producing events.

---

## Earlier releases

Versions 0.9 through 0.69 established the foundations: Zero-Trust default-deny
with persistent approval, event-driven kernel detection with crash-loop
recovery, the packets log, custom rules, stealth mode, directional and timed
rules, the system-rule library, profiles, versioned backups, Windows Firewall
import, themes, VirusTotal lookups, blocklists, filtering DNS, Authenticode
signature verification, diagnostics export, network scopes, GeoIP with country
and ASN awareness, the built-in DNS resolver, domain heuristics, verdict
reasons, the captive-portal helper, and the notification centre.

See [`ROADMAP.md`](ROADMAP.md) for what is planned next.
