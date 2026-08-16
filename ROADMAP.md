# GunWall Roadmap

This document tracks GunWall's path to **full feature parity with mature reference firewalls** on the Windows Filtering Platform, and beyond. It is exhaustive: every capability a complete WFP firewall is expected to have is listed, marked ✅ done, ◐ partial, or ☐ planned.

**Nothing here is tied to a version number.** Work is grouped by what it touches
and how risky it is, not by the release it lands in. Ordering within a group is a
rough sense of value, not a queue — items get picked up when they make sense, and
a version number attached to a plan is a promise about sequencing that this
project has no reason to make. What shipped and when lives in
[`CHANGELOG.md`](CHANGELOG.md); this file is only about what is true now and what
is still open.

GunWall remains **WPF / .NET 8, single elevated portable EXE, zero NuGet dependencies, MIT**. See the "Architecture & language" note at the bottom for why it stays single-language.

---

## ✅ Shipped

**Engine & enforcement** — event-driven WFP detection (kernel net-event stream, not polling) with crash-loop self-recovery · Zero-Trust default-deny with persistent per-app approval · lockdown · stealth mode · per-app allow/block, directional, timed (auto-expiring) and silent (muted) rules · critical-process protection · persistent filters with stored IDs for clean removal.

**Rules** — custom rules by IP / CIDR / port / protocol / direction / local-port · manual IP blocklist · curated system-rule library (~21 presets + secure baseline).

**Threat & privacy blocking** — telemetry & Windows-Update blocklists via hosts file **with automatic WFP firewall-rule fallback** when the hosts file is blocked · ads & trackers via AdGuard DNS · filtering-DNS selection (AdGuard / Quad9) · on-demand online list updates.

**App trust** — **Authenticode signature verification** (valid / unsigned / invalid via WinVerifyTrust) · SHA-256 tamper detection · VirusTotal hash lookup · verified-publisher column and colored signature in alerts.

**Visibility** — connection inspector (TCP+UDP, IPv4+IPv6) with close / block / terminate · live Packets Log (+CSV) · throughput graph · activity feed · LAN network scanner · reverse-DNS host resolution.

**Management & UI** — profiles (import/export) · versioned backups (auto + manual) · Windows Firewall status/on-off/import · diagnostics export · run-at-startup (UAC-skipping scheduled task) · start-minimized · close-to-tray with active-firewall exit warning · configurable alerts (timeout, default action, sound, tray, snooze) · light/dark theme · search bar · always-on-top · update checker.

---

## ☐ Open

### App model & visibility
*Managed C# throughout. No kernel risk.*
- ☑ **UWP / Microsoft Store app support** — Store/UWP apps are detected from their package path, shown with their real display name and a "Store" badge, with package-family identity surfaced in the Properties dialog. They are ruled by executable path (the proven enforcement path), which covers the common case without package-SID interop.
- ✅ **Service & network-app categorization** — connections name the hosted service, and services can be blocked individually by their own identity.
- ◐ **Complete IP and country coverage for every connection** — ✅ **IPv6 GeoIP**, the largest of the three gaps: the table was IPv4-only, so every v6 destination resolved to nothing. Remaining: — some connections show no country, so the map and the country tables under-report. Three causes found so far, in order of size:
  - **The local GeoIP table is IPv4-only** (`geoip-v4.tsv`). Every IPv6 destination resolves to nothing. The self-hosted API mode already answers v6, so the gap is the bundled data rather than the lookup.
  - **The map draws only the top ten countries**, so quiet destinations never appear on it even when the Connections table knows them.
  - **Addresses outside the 532k ranges** return empty and are shown blank rather than as "unknown", which reads as a failure rather than a limit.

  Note on VPNs: switching the VPN's exit country will not change the map, and that is correct — the map plots the destinations traffic is going **to**, not the tunnel it travels through. A dedicated "where am I exiting from" readout would be a separate feature, and is worth considering.

- ◐ **Richer network scan** — ✅ likely OS from ping TTL · ✅ **gateway detection** from the routing table, which is a fact rather than a guess · ✅ **NetBIOS names** for devices with no reverse-DNS record · ✅ **randomised-MAC detection**, so a phone shows why its vendor is unknown. Remaining: **vendor from the MAC OUI**, which needs a downloadable lookup table rather than data written from memory, and **mDNS/Bonjour names** for Apple and IoT devices.

- ☐ **Pico / subsystem process support** — identify WSL and other minimal-process traffic.
- ✅ **App icons in the list** — each executable's icon is shown in the Application column.
- ✅ **App properties dialog** — a per-app detail window (path, publisher, hash, signature, type/package, counts) with **Open file location**, **Copy path** and a notes field.
- ✅ **Purge unused apps** + **keep-unused toggle** + **purge expired timers** — manual purge buttons plus a setting to hide apps with no rule and no live connections.
- ✅ **Protected ("undeletable") rules** — a custom rule can be marked protected; it then refuses deletion until unprotected. (Per-app *disable-notifications* is the existing Mute action.)
- ◐ **Color-highlight customization** — ✅ user-editable colors for signed / unsigned / system / invalid / unknown (Settings → Appearance). Remaining: the **special**, **pico**, **undeletable** and **connection** categories (need the underlying detection).
- ✅ **Per-app notes** — attach a free-text note to any app (in the Properties dialog).

### Notifications, blocklists & logging
*Managed C# throughout. No kernel risk.*
- ✅ **Fullscreen-silent mode** — approval popups are held back while a fullscreen app/game/presentation is foreground (detected via the OS notification-state signal), and appear once it ends.
- ✅ **Confirmation prompts** — confirm-before-clearing the Activity / Packets logs, and an always-confirm-on-exit option (on top of the existing active-firewall exit warning).
- ✅ **Notification exclusions** — alerts are categorised (security, protection changes, network, rules) and each category can be silenced independently. *(GunWall currently raises a single new-app approval prompt, so this waits on having multiple notification categories to exclude.)*
- ◐ **Address-layer enforcement of name-based blocks** — with *Watch system DNS lookups* on, a blocked domain also has the address it resolved to blocked in the kernel. Correct for an address belonging to the blocked site, destructive on a shared one: a CDN edge answers for thousands of names, and blocking one tracker took an antivirus's update service and a git client offline for days, above every application rule, with nothing on screen explaining it.
  - ✅ **Block only when every name on the address is blocked.** 0.99.87 refused outright on any shared address, which also spared hosts serving two tracker names — exactly what a blocklist is for. The test is now "is it shared with anything the user wants to keep": one unblocked name vetoes, and a saturated name set (too many names to be sure) also vetoes, because an incomplete answer must not read as a positive one.
  - ✅ **Show it.** A withheld block now raises an Alert naming the service it would have cut off, instead of only a log line.
  - ☐ **Per-domain override** — let the user force the address block anyway, with the collateral spelled out.
  - ✅ **Prefer the app layer where the app is known** — a connection carries its executable, so a blocked name is now enforced as *this application may not reach that address*. Scoped to one process the filter carries **no collateral at all**, so a shared CDN edge is safe to block: the sharing veto only applies to the global fallback, which is now reached only when the process cannot be identified.
- ◐ **3-level blocklist control** — ✅ the allow level: `@@name` in the blocklist box permits a name even when a category or preset blocks it, using the syntax adblock lists already use, so one bad entry in a hundred-thousand-domain list no longer means turning the category off. Allows are tested before blocks and share the same subdomain matching. Remaining: per-category allow/block/disable in the UI, an **"extra" curated list**, and **exclude-apps-from-blocklist**.
- ✅ **Logging upgrades** *(complete)* — blocked/allowed events to the **Windows Event Log** (toggle), a configurable **log-size limit** (live-row cap + CSV rotation size), and a separate **error log viewer** with deduplicated entries.
- ◐ **View & tray niceties** — ✅ autosize columns, **tray single-click**, and **UI size / zoom**. Remaining: list view modes (details / icon / tile) and icon sizes.

### Kernel hardening
*Touches the WFP filter set. Every item here needs hardware verification and a removal path before it ships.*
- ◐ **Kernel verdict visibility** — ✅ the packet log now records what the kernel actually did, not only what GunWall would have decided, and names a drop caused by anything else on the machine. Achieved by reading the existing net-event verdict rather than adding discard layers, which needed no new GUIDs and no new struct layouts. Remaining: attributing the drop to the specific filter, which does need the event union.
- ◐ **Expanded WFP layers** — **16 layers wired and verified on hardware**: outbound connect, inbound accept, listen, **resource assignment** (bind, TCP *and* UDP), inbound/outbound transport, outbound ICMP error, and **IP forwarding** — each v4 and v6. Shipped as opt-in, removable rules through the fault-tolerant filter path.
  - ✅ **Kernel layer self-test** — probes every layer *and condition* the kernel accepts, using a permit filter at weight 0 that is non-persistent and deleted immediately. This surfaced three incorrect WFP identifiers that had been failing silently, including one that had disabled IPv4 stealth-mode ICMP suppression entirely.
  - Remaining: ALE_CONNECT_REDIRECT and the matching *_DISCARD* layers (v4/v6).
- ✅ **Quick rule toggles** — *Windows Update* (Delivery Optimization 7680 TCP/UDP, WSUS 8530/8531), *Teredo* (UDP 3544) and *6to4 / ISATAP* (IP protocol 41) as one-tap entries in the system-rule library. Adding 6to4 required the engine to express raw IP protocol numbers: it previously mapped only TCP and UDP and dropped anything else, which would have turned protocol 41 into a permit for every protocol.
- ◐ **Filter tamper resistance** — ✅ detection and self-healing have shipped. Remaining: true prevention via an access-control list, which needs the privilege split first (an elevated-user process cannot lock out other administrators without locking out itself). *Carries a lockout risk; needs a guaranteed recovery path before shipping.* The sublayer-delete-by-key removal path is now proven, which is a prerequisite.

### Advanced and dangerous
*Opt-in, heavily warned, and each one needs a guaranteed recovery path. These are last for a reason, not for a release date.*
- ☐ **Boot-time filters** — enforce blocking during boot before GunWall starts. *Can break boot networking; must be reversible from Safe Mode.*
- ☐ **Windows Update repair (WUFix)** — registry repair for a stuck Update service. *Edits HKLM; gated behind explicit confirmation.*
- ☐ **Compressed / encrypted profile formats** — alongside today's plain JSON.

### Localization
- ☐ **Multi-language UI** — externalize strings and ship language packs.

---

## 🧭 Architecture & language note

GunWall stays **single-language C# / .NET 8** on purpose. The work a WFP firewall does is dominated by **kernel transitions (WFP filter add/remove, the kernel event stream) and I/O**, not CPU-bound managed code — so rewriting parts in C, Go, or Rust would add cross-language interop, a heavier build, and a larger footprint **without a measurable performance gain**, while breaking the portable single-EXE / zero-dependency goals. The one place native code would matter — a kernel-mode callout driver — is **not used** by mature user-mode WFP firewalls either, and would require driver signing, kernel debugging, and BSOD risk that contradict GunWall being free, portable, and install-free. The right tool here is exactly what's in use.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
