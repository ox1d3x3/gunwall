# GunWall — Roadmap

**Goal:** full feature parity with the leading open-source WFP firewall, then improve on each. Ship 4-5 features per release, sequenced **safe → kernel-risky**, validating each on real hardware. GunWall stays **portable and free** — no installer or paid code-signing on the main path.

**Verification (every release):** Roslyn syntax-check all sources, a missing-`using` check for WPF types, then the finished zip is extracted and compared byte-for-byte against the working copy.

---

## ✅ Shipped (v0.1 → v0.23)

Engine: WFP engine (8 layers), event-driven detection with crash-loop recovery, transport-layer ICMP, strict/Zero-Trust block-all, lockdown, stealth mode.
App control: per-app allow/block, directional, temporary/timed (persistent), silent, SHA-256 + tamper detection, publisher info, color-coding, critical-process protection.
Rules: custom rules (IP/port/protocol/direction/local-port/CIDR), manual IP blocklist, curated system-rule library (~21 presets + secure baseline), profiles, versioned backups.
Windows integration: Windows Firewall status / on-off / rule import, auto-refresh on network change.
UI/ops: clickable Dashboard, Apps, Connections (+ close/block-and-close), Packets Log (+ CSV export), Custom/System Rules, Services, LAN scanner, Activity, Settings, light/dark theme, configurable alert popup, VirusTotal, run-at-startup, close-to-tray, snooze, update checker, Event Log, notification sound/tray.

---

## 🔜 Remaining work (re-prioritised after deep source review)

### 1. Curated blocklists — ✅ SHIPPED (v0.24, upgraded v0.25) *(hosts-file domain blocking + online MIT lists + filtering DNS)*
Built-in, toggleable threat/telemetry blocklists like the reference's spy/update/extra sets:
- **Telemetry / tracking blocklist** — block known Windows telemetry & tracking endpoints. The headline missing feature.
- **Windows Update servers blocklist** — optional (off by default; blocking breaks updates).
- **Ads / extra blocklist** — optional.
- Each a simple per-list **Off / Block** toggle, applied via the existing CIDR block engine.

  *GunWall will ship its own curated lists (built from public threat/telemetry sources), not copied from any GPL project. Improvement angle: live count, last-updated date, and a future "update lists" fetch.*

### 2. Process & app control gaps — *safe, managed*
- **Kill / terminate process** — end a running process, not just block it.
- **Certificate verification** — actually validate the Authenticode signature (valid / invalid / unsigned), beyond just reading the publisher name.
- **Per-app & per-rule comments** — annotate entries with notes.
- **Keep-unused-apps** toggle — keep apps listed even when not running.

### 3. Notifications & interface polish — *safe, managed*
- **Fullscreen silent mode** — suppress popups while a fullscreen app/game runs.
- **Highlighting controls** — toggle each category, customize colors, add Pico / Special / Undeletable categories.
- **Confirmation prompts** — confirm-before-allow, confirm-on-exit, confirm-clear-log, etc.
- **Notification exclusions** — don't notify for blocklist / custom-rule / stealth / inbound hits.
- **Smaller display prefs** — tray single-click, configurable log path + size limit, short-path display, hide icons, network-resolution/monitor toggles.

### 4. Kernel hardening — *Phase B leftovers (high risk, careful hardware testing)*
- **Expanded WFP layer coverage** — add LISTEN, RESOURCE_ASSIGNMENT, ICMP_ERROR, IPFORWARD toward the reference's 22 (we use 8). *Safest kernel item, biggest coverage gain.*
- **Filter tamper protection** — secured sublayer so other software can't delete GunWall's filters.
- **Boot-time filters** ⚠️ — protection during boot. Dangerous failure mode (boot networking).
- **WUFix** ⚠️ — keeps Windows Update working under lockdown. Invasive (rewrites WU service registry entries).
- **Allow-loopback toggle**, packet-queuing, app-monitor — small advanced engine options.

### 5. UWP / Microsoft Store apps — *high risk, dedicated effort*
- Enumerate Store packages, resolve package SIDs, block via the package-ID WFP condition, add a "Store apps" tab + rule persistence. Untestable here, so expect several iterate-and-test rounds.

### 6. Localization — *anytime*
- Multi-language support.

---

## Deliberately NOT doing (for now)
- **Installer** — GunWall stays portable (unzip-and-run). Maybe much later.
- **Code signing** — needs a paid certificate; not worth the cost for a free/open-source project. Users can run the portable build.

## Recommended order
1. **Curated telemetry/update/extra blocklists** (the big missed feature; safe).
2. **Kill process + certificate verification + comments** (safe control gaps).
3. **Notification & interface polish** (fullscreen-silent, highlighting, confirmations, exclusions).
4. **Expanded WFP layers**, then tamper protection (kernel).
5. **Boot-time filters + WUFix** (the dangerous pair, slow & careful).
6. **UWP / Store apps**, then localization.

## Needs your input
- **Testing each kernel release** on real Windows 11 — the only way to validate kernel changes.
