# GunWall Roadmap

This document tracks GunWall's path to **full feature parity with mature reference firewalls** on the Windows Filtering Platform, and beyond. It is exhaustive: every capability a complete WFP firewall is expected to have is listed, marked ✅ done, ◐ partial, or ☐ planned.

GunWall remains **WPF / .NET 8, single elevated portable EXE, zero NuGet dependencies, MIT**. See the "Architecture & language" note at the bottom for why it stays single-language.

---

## ✅ Shipped (through v0.32)

**Engine & enforcement** — event-driven WFP detection (kernel net-event stream, not polling) with crash-loop self-recovery · Zero-Trust default-deny with persistent per-app approval · lockdown · stealth mode · per-app allow/block, directional, timed (auto-expiring) and silent (muted) rules · critical-process protection · persistent filters with stored IDs for clean removal.

**Rules** — custom rules by IP / CIDR / port / protocol / direction / local-port · manual IP blocklist · curated system-rule library (~21 presets + secure baseline).

**Threat & privacy blocking** — telemetry & Windows-Update blocklists via hosts file **with automatic WFP firewall-rule fallback** when the hosts file is blocked · ads & trackers via AdGuard DNS · filtering-DNS selection (AdGuard / Quad9) · on-demand online list updates.

**App trust** — **Authenticode signature verification** (valid / unsigned / invalid via WinVerifyTrust) · SHA-256 tamper detection · VirusTotal hash lookup · verified-publisher column and colored signature in alerts.

**Visibility** — connection inspector (TCP+UDP, IPv4+IPv6) with close / block / terminate · live Packets Log (+CSV) · throughput graph · activity feed · LAN network scanner · reverse-DNS host resolution.

**Management & UI** — profiles (import/export) · versioned backups (auto + manual) · Windows Firewall status/on-off/import · diagnostics export · run-at-startup (UAC-skipping scheduled task) · start-minimized · close-to-tray with active-firewall exit warning · configurable alerts (timeout, default action, sound, tray, snooze) · light/dark theme · search bar · always-on-top · update checker.

---

## ☐ Remaining for parity

### Phase 1 — App model & visibility (safe, managed C#)
- ☑ **UWP / Microsoft Store app support** — Store/UWP apps are detected from their package path, shown with their real display name and a "Store" badge, with package-family identity surfaced in the Properties dialog. They are ruled by executable path (the proven enforcement path), which covers the common case without package-SID interop.
- ☐ **Service & network-app categorization** — distinguish svchost-hosted network services and show the owning service per rule (beyond the current Services tab).
- ☐ **Pico / subsystem process support** — identify WSL and other minimal-process traffic.
- ✅ **App icons in the list** — each executable's icon is shown in the Application column.
- ✅ **App properties dialog** — a per-app detail window (path, publisher, hash, signature, type/package, counts) with **Open file location**, **Copy path** and a notes field.
- ✅ **Purge unused apps** + **keep-unused toggle** + **purge expired timers** — manual purge buttons plus a setting to hide apps with no rule and no live connections.
- ✅ **Protected ("undeletable") rules** — a custom rule can be marked protected; it then refuses deletion until unprotected. (Per-app *disable-notifications* is the existing Mute action.)
- ◐ **Color-highlight customization** — ✅ user-editable colors for signed / unsigned / system / invalid / unknown (Settings → Appearance). Remaining: the **special**, **pico**, **undeletable** and **connection** categories (need the underlying detection).
- ✅ **Per-app notes** — attach a free-text note to any app (in the Properties dialog).

### Phase 2 — Notifications, blocklists & logging (safe)
- ✅ **Fullscreen-silent mode** — approval popups are held back while a fullscreen app/game/presentation is foreground (detected via the OS notification-state signal), and appear once it ends.
- ✅ **Confirmation prompts** — confirm-before-clearing the Activity / Packets logs, and an always-confirm-on-exit option (on top of the existing active-firewall exit warning).
- ☐ **Notification exclusions** — independently silence notifications for classify-allow, custom-rule, stealth, and blocklist hits. *(GunWall currently raises a single new-app approval prompt, so this waits on having multiple notification categories to exclude.)*
- ☐ **3-level blocklist control** — allow / block / disable per category (adds an explicit allow/whitelist level over today's on/off), plus an **"extra" curated list** and **exclude-apps-from-blocklist**.
- ◐ **Logging upgrades** — ✅ blocked/allowed events to the **Windows Event Log** (toggle) and ✅ a configurable **log-size limit** (live-row cap + CSV rotation size). Remaining: a separate **error log** view.
- ☐ **View & tray niceties** — list view modes (details / icon / tile) and icon sizes, autosize-columns, **tray single-click**, font / zoom options.

### Phase 3 — Kernel hardening (higher risk, large coverage)
- ◐ **Expanded WFP layers** — extend from today's ~10 toward the full ~22. ✅ **ALE_AUTH_LISTEN** (v4/v6) now ships as an opt-in, removable "Block listening sockets" System Rule, applied through the fault-tolerant filter path, with a live kernel-coverage readout on the System Rules tab. Remaining (each needs careful **on-hardware testing** before shipping — a bad filter on these can break connectivity in ways that can't be validated off-device): ALE_RESOURCE_ASSIGNMENT, ALE_CONNECT_REDIRECT, INBOUND_ICMP_ERROR, IPFORWARD, and the matching *_DISCARD* layers (v4/v6).
- ☐ **Quick rule toggles** — one-tap *Allow Windows Update* and *Allow 6to4 / IPv6 transition*.
- ☐ **Secure filters** — protect GunWall's sublayer/filters from tampering via a DACL. *Carries a lockout risk; needs a guaranteed recovery path before shipping.*

### Phase 4 — Advanced / dangerous (opt-in, heavily warned, last)
- ☐ **Boot-time filters** — enforce blocking during boot before GunWall starts. *Can break boot networking; must be reversible from Safe Mode.*
- ☐ **Windows Update repair (WUFix)** — registry repair for a stuck Update service. *Edits HKLM; gated behind explicit confirmation.*
- ☐ **Compressed / encrypted profile formats** — alongside today's plain JSON.

### Phase 5 — Localization
- ☐ **Multi-language UI** — externalize strings and ship language packs.

---

## 🧭 Architecture & language note

GunWall stays **single-language C# / .NET 8** on purpose. The work a WFP firewall does is dominated by **kernel transitions (WFP filter add/remove, the kernel event stream) and I/O**, not CPU-bound managed code — so rewriting parts in C, Go, or Rust would add cross-language interop, a heavier build, and a larger footprint **without a measurable performance gain**, while breaking the portable single-EXE / zero-dependency goals. The one place native code would matter — a kernel-mode callout driver — is **not used** by mature user-mode WFP firewalls either, and would require driver signing, kernel debugging, and BSOD risk that contradict GunWall being free, portable, and install-free. The right tool here is exactly what's in use.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
