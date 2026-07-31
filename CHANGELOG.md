# Changelog

All notable changes to GunWall are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with a `0.x` pre-1.0 series.

---

## [0.99.0] — 2026-07-30

### Added
- **Tamper detection and self-healing for the firewall's filters.** Every 30 seconds GunWall asks the kernel whether each filter it installed is still there, and re-applies any that are not — raising an alert naming how many were removed. A manual check is available in Settings → Diagnostics, which also proves the recovery path by adding and deleting a throwaway filter, so the escape hatch every other safeguard depends on is demonstrated rather than assumed.
- Filter-integrity figures in the diagnostics export.

### Note on scope
The roadmap proposed protecting these objects with an access-control list. That was examined and deliberately not built: GunWall runs elevated as the user rather than as the system, so any descriptor restrictive enough to stop another administrator process would equally lock GunWall out of its own filters, with no way back. Genuine prevention needs the privilege split tracked for 1.0. What ships here is the part that can be done honestly — removal is no longer silent or lasting. A run of engine errors is deliberately never treated as tampering, so the watch cannot cry wolf and cannot trigger a needless rebuild.

---

## [0.98.0] — 2026-07-30

### Added
- **Per-service rules.** A rule written against an executable applies to everything inside it, and dozens of Windows services share a handful of `svchost.exe` processes — so blocking svchost to stop telemetry also stopped Windows Update, DHCP and time synchronisation. Services can now be blocked individually, by their own identity, from the Services page. Others in the same process are unaffected.
  - Enforcement uses the `ALE_USER_ID` condition against a security descriptor naming the service's own SID, on outbound and inbound layers for both address families.
  - The SID is derived from the service name exactly as Windows derives it — uppercase, UTF-16, SHA-1, read as five little-endian values — so it needs no privileges and no system call. It is checked in tests against Microsoft's published value for `TrustedInstaller`, because a wrong SID yields a filter that silently matches nothing rather than an error.
  - Rules are stored by service name, so they survive the service moving between host processes, and are re-asserted when the engine is rebuilt.
  - A rule that installs no filters is reported rather than recorded, so the interface never claims a block that does not exist.
- The Services page shows which services are blocked, and blocked-service names appear in the diagnostics export.

This completes what per-service attribution began in 0.85.0: that release made it possible to see which service was talking, this one makes it possible to do something about one without affecting the rest.

---

## [0.97.1] — 2026-07-29

### Fixed
- **Blocklist enforcement could install a machine-wide block on a loopback or private address.** Every hosts file opens by mapping `localhost` and friends to 127.0.0.1, and those lines were being accepted as blocklist entries; the enforcement then dutifully blocked the address they resolved to. Only the infrastructure loopback permit, which outranks it, prevented the machine being cut off from itself — that was luck, not design. Two independent guards now each stop this on their own: loopback plumbing (`localhost`, `broadcasthost`, the `ip6-*` names) is rejected as a blocklist entry, and enforcement will only ever target a public unicast address. Multicast and broadcast are excluded too, since a global block on the mDNS group would break local network discovery.

### Changed
- The blocklist description now states the real limitation: connection-layer blocking depends on Windows performing the lookup, so a browser using its own encrypted DNS is not covered until that setting is turned off in the browser.

---

## [0.97.0] — 2026-07-29

### Fixed
- **The DNS blocklist blocked nothing.** It only ever applied to lookups GunWall itself answered, so once the machine stopped sending its DNS here — deliberately, in 0.95.0 — the list became inert while the interface went on implying it worked. Adding a domain appeared to succeed and had no effect.

### Added
- **Blocklist enforcement at the connection layer.** The passive DNS watch knows which addresses each name resolved to, so a connection to an address belonging to a blocked domain is now blocked in the kernel and the existing session torn down. This is stronger than refusing the lookup: it holds for applications that bring their own resolver and never ask Windows at all, and it cannot be evaded by choosing a different DNS server. Filters are stored per domain, so removing a domain from the list removes everything it accumulated.
- A global remote-address block filter in the WFP engine, at the user-block weight, for enforcement that is not tied to a single application.

### Changed
- "Block direct connections" no longer requires GunWall's resolver to be running — it reads the same shared observations as domain rules.
- The blocklist description now states how blocking is actually applied, rather than implying that entries take effect on their own.

---

## [0.96.1] — 2026-07-29

### Fixed
- **The application terminated shortly after launch.** Two mistakes in the new DNS observer's native structure declarations, both from describing a layout by hand instead of reusing the one already proven in the byte meter:
  - `EVENT_TRACE_LOGFILEW` was declared sequentially with a guessed 208-byte gap for its two embedded structures. Those are 88 and 280 bytes, which places `EventRecordCallback` at offset 424; the hand-written version put it near 264. `OpenTrace` therefore read an arbitrary value as the callback address and `ProcessTrace` called into it, ending the process outright — with no managed exception and nothing in the log beyond a second startup line.
  - `EVENT_RECORD` had `ProviderId` and `EventId` transposed, so even once the session ran, no event would ever have matched and nothing would have been recorded.
  Both structures now use the explicit offsets shared with the byte meter, which is hardware-proven at over 110,000 events.
- **A crash-loop guard.** A fault inside an ETW callback ends the process immediately, so an application that dies during startup can otherwise never be recovered from its own interface. A marker is now written before the session is touched and cleared once it has run without incident; a launch that finds the marker still present skips the observer and says so, rather than repeating the crash. Turning the setting off and on again is a genuine retry.

### Added
- Offset assertions for both native structures, derived from the documented field layouts and checked against what the code declares — so this class of mistake fails a test rather than a machine.

---

## [0.96.0] — 2026-07-27

### Added
- **Passive observation of the system's DNS lookups**, restoring what removing DNS redirection took away. Domain rules and the "block direct connections" scope both need to know which name produced an address; until now the only source was GunWall's own resolver, so both were blind unless something was pointed at it. GunWall now subscribes to the events the Windows DNS client already emits and reads the results. It claims nothing, rewrites nothing and answers nothing — so unlike redirection it cannot come into conflict with security software or a VPN over ownership of port 53, and it sees every lookup the machine makes rather than only those routed through GunWall.
- A shared name-to-address memory that both the resolver and the observer write to, so the rule engine reads one source regardless of where a lookup was answered. IPv4-mapped IPv6 answers resolve to the same host, and aliases are deliberately never recorded as destinations.
- A **Names known** counter in the DNS panel and a **DNS watch** line in App Health, both stating plainly when domain rules have no data to match on. Observer event, answer and parse-failure counts appear in the diagnostics export.

### Changed
- Domain rules and direct-connection detection no longer require GunWall's resolver to be running.

---

## [0.95.1] — 2026-07-27

### Fixed
- **The removal migration could itself strand an adapter.** Restoring only the adapters recorded in the saved list left behind any that the old redirect had touched without recording — one that came up after the capture, or the case where a crash-leftover state was deliberately saved as nothing. Those would have kept pointing at the loopback resolver with no interface left to change them. The migration now also sweeps every adapter and returns any still aimed at 127.0.0.1 or ::1 to automatic.
- Corrected the claim that GunWall no longer changes this PC's DNS at all: *Security → Filtering DNS* still assigns a public resolver when asked. It never claims port 53 locally, so it carries none of the conflict that caused the removal.

---

## [0.95.0] — 2026-07-27

### Removed
- **"Route this PC's DNS through GunWall"**, along with the Gaming Session bypass that existed to suspend it. Redirecting the machine's DNS means claiming port 53, which puts GunWall in direct competition with other software that also claims it — security suites' DNS protection and VPN clients' leak protection in particular. Testing on hardware showed the failure precisely: plain UDP and DNS-shaped UDP both crossed loopback normally, while replies from the resolver on port 53 were discarded before delivery, with the resolver reporting every query answered. The result is a machine that appears to lose its internet connection, from a cause outside the firewall's control and impossible to fix in software. A feature that fails that way, on an unpredictable subset of machines, is not worth its cost.
- GunWall no longer points this PC's DNS at itself. The code that redirected adapters to the loopback resolver has been removed outright. *Security → Filtering DNS* is unaffected and still sets a public resolver (Cloudflare, Quad9, AdGuard) on request — that is an explicit choice of upstream provider and never claims port 53 locally, so it carries none of the conflict described above.

### Changed
- **Upgrading is safe even if routing was switched on.** On first launch the saved routing state is detected, the adapters are restored to the servers they used before, and the state is cleared. Restoring the saved list alone is not enough: the old redirect applied to whichever adapters were active when it ran, so one that appeared later could be redirected without ever being recorded, and a crash-leftover state was deliberately saved as nothing at all. The migration therefore also sweeps every adapter and returns any still aimed at the loopback resolver to automatic — so removing the feature cannot strand a machine with DNS pointing somewhere it can no longer manage. The sweep runs only during this one-time migration, never on every launch, because setting DNS to 127.0.0.1 by hand is now a legitimate way to use the resolver and must not be undone. A shutdown-time restore is retained as a further line of defence.
- The resolver keeps everything that did not depend on system routing: blocklists, DNS-over-HTTPS, CNAME-cloaking defence, the query log, and the name-to-address history that domain rules and the "block direct connections" scope rely on. Point an application — or this PC's DNS, deliberately and by hand — at 127.0.0.1 to use it.
- With nothing on the machine depending on GunWall for name resolution, fail-closed now means exactly that: a failed encrypted lookup fails rather than quietly falling back to plaintext.

---

## [0.94.0] — 2026-07-26

### Added
- **DNS-shaped loopback probe**, completing the diagnosis of the DNS-routing outage. Testing on real hardware established that plain UDP crosses loopback normally while the resolver's replies — sent successfully, with no send failures — are destroyed before delivery. Nothing in GunWall treats those two cases differently, so the difference is imposed from outside the application. The path test now also sends a genuine DNS message between two ordinary sockets, so the results triangulate the cause: whether loopback UDP is broken generally, whether DNS messages are dropped regardless of port, or whether port 53 specifically has been taken over. The verdict names the likely causes — security software with DNS protection, or a VPN client's DNS leak protection — and gives an ordered way to confirm which, including re-testing on port 5353.

---

## [0.93.0] — 2026-07-26

### Fixed
- **Blocklist entries that could never match, accepted silently.** A pasted URL such as `https://www.example.com/` was stored verbatim, and since DNS blocking matches host names, it could never match anything — the entry looked applied and did nothing. Lines are now reduced to the host name they mean: schemes, paths, queries, credentials, ports, `*.` wildcards and leading dots are all stripped, and hosts-file lines are understood. Anything genuinely unusable — a bare IP address, invalid characters, several words on one line — is now reported rather than quietly turned into a fragment.
- Applying the blocklist now clears the resolver's cache and flushes the Windows DNS cache. Without that, a name resolved before the rule existed kept being answered from cache, so a correct new block appeared not to work.

### Added
- Applying a blocklist reports what each line became: entries that were read as a domain name, entries that were ignored and why, and a reminder that blocking a name covers its subdomains but not its parent.

### Added
- **Raw loopback probe in the DNS path test.** Before testing the resolver, GunWall now sends a plain UDP datagram between two sockets inside its own process, with the resolver not involved at all. This separates two very different situations that previously looked identical: a fault in the resolver, versus loopback UDP delivery being broken on the machine by another network filter driver — in which case no local DNS server of any kind can work, and it is not something GunWall can fix in software.
- The resolver probe now samples its own receive and reply counters around the query, so a timeout reports whether the query was dropped on the way in or the reply was dropped on the way back.

### Fixed
- `FWP_CONDITION_FLAG_IS_APPCONTAINER_LOOPBACK` was `0x00000400`; the correct value is `0x00400000` (verified against `fwptypes.h` and Microsoft's win32metadata). This affected stealth mode's ICMP suppression, which used the flag to exempt loopback traffic.

---

## [0.92.0] — 2026-07-26

### Added
- **Diagnosis tooling for the DNS routing problem.** Three previous attempts at this fault were based on plausible theories that the logs could neither confirm nor rule out, because the resolver reported success for work whose outcome it never checked. This release adds the missing observability rather than another guess.
  - **Test DNS path** (DNS panel) sends a real DNS query to GunWall's own resolver over loopback — exactly as Windows does — on both IPv4 and IPv6, and reports the response code, answer count, round-trip time, and whether the reply's transaction ID matches. This distinguishes the two possibilities the counters cannot: a resolver that answers correctly but whose replies never reach the client, versus one the client never reaches at all.
  - The resolver now confirms **which loopback endpoints it actually bound**, rather than only logging failures, and counts queries received per address family, replies sent, and reply-send failures. A reply that fails to send was previously invisible: the query was recorded as answered while the client saw nothing and retried.
  - The diagnostics bundle now captures the DNS configuration **Windows is really using** — per-adapter servers for both address families, plus the **Name Resolution Policy Table**. NRPT rules override per-adapter DNS, so a VPN or mesh client with rules there wins over GunWall's redirection; `ipconfig` alone does not show them.

---

## [0.91.0] — 2026-07-26

### Fixed
- **Cached failures breaking name resolution — the real cause of the routing outage.** The response code of an upstream reply was never checked anywhere. Two consequences compounded: a `SERVFAIL` or `REFUSED` arrives as a perfectly valid HTTP 200 over DNS-over-HTTPS, so it was counted as a successful lookup (which is why diagnostics reported zero DoH failures during an outage); and it was then written to the cache, where the TTL fallback for an answer-less response is a full 60 seconds. Every subsequent lookup for that name was served the cached failure, and because clients retry hard on failure, they kept hitting the same poisoned entry — one transient upstream hiccup became a sustained outage for that name, while GunWall reported that it had answered every query. Switching adapters over to the resolver is exactly when such a hiccup occurs, which is why the problem appeared the moment routing was enabled and disappeared when it was turned off.
  - Upstream responses are now validated: only `NOERROR` and `NXDOMAIN` count as answers. Failure codes are treated as failures, so the retry and plaintext-fallback paths engage as intended.
  - Only genuine answers are cached. `NXDOMAIN` is cached briefly (30 s) rather than for the full TTL, since a stale "does not exist" is indistinguishable from a broken connection.
  - The cache is flushed when DNS routing is switched on or off, and on any resolver configuration change, so nothing captured mid-change can be served.
  - Diagnostics report an `upstreamRefused` count, making DNS-level failures visible instead of being hidden inside the success counter.

---

## [0.90.0] — 2026-07-25

### Fixed
- **Internet still dropping when routing this PC's DNS through GunWall — the real cause.** The resolver bound only IPv4 loopback (127.0.0.1) and routing set only the IPv4 DNS server, but Windows resolves over IPv6 as well and prefers it. With a VPN active, the machine kept an IPv6 DNS server (often the tunnel's) and sent lookups there, bypassing GunWall entirely; when that path was slow or unreachable, names "could not be found" even though GunWall's resolver was healthy and caching correctly. The resolver now also listens on IPv6 loopback (::1), routing points IPv6 DNS at ::1 too, and restore returns both families to automatic. This is the fix for the reported outage; the v0.89 connection-pool work stands, but the true problem was the IPv6 bypass.

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
