# GunWall — Feature Roadmap

**Goal:** match every feature in the leading open-source WFP firewall, then improve on each in GunWall's own way (modern WPF UI, themes, VirusTotal, LAN scanner, update checks — things the reference doesn't have).

**Delivery rule:** 4–5 big features per release, grouped so each release is fast to test and low-risk to validate. Releases are sequenced **safe (managed code) → moderate (WFP kernel work) → risky (UWP SID interop) → production**.

**Every release is verified before shipping:** Roslyn syntax-check on all sources, then the finished zip is extracted and re-checked byte-for-byte against the working copy (the discipline that closed the truncated-file bug).

---

## Already shipped (through v0.19.0)

Engine & detection: WFP engine (8 layers), event-driven kernel detection with crash-loop self-recovery, transport-layer ICMP coverage, strict/Zero-Trust block-all mode, lockdown.

App control: per-app allow/block, **directional** (in/out only), **temporary/timed** blocks (now persistent across restart), silent/mute, SHA-256 hashing + tamper detection, Authenticode publisher info.

Rules: custom rules (IP / port / protocol / direction / local-port / **CIDR subnet**), IP blocklist (+ CIDR), 6 hardening system-rule toggles, profiles (save/load/switch).

UI & ops: Apps, Connections, Packets Log, Custom Rules, System Rules, Services, **Network scanner (LAN devices)**, Activity, Dashboard (live stats), Settings. Light/dark theme + animated slide toggles, modern connection-alert popup, **VirusTotal scanning**, run-at-startup (scheduled task), close-to-tray + safe exit, **snooze/pause protection**, **update checker**, Windows Event Log.

> GunWall already exceeds the reference on: LAN network scanner, built-in VirusTotal, update checking, and full light/dark theming.

---

## Phase A — v0.20 "Visibility & Control" *(all safe, managed code)*  ✅ SHIPPED

The biggest visible jump toward parity with zero kernel risk.

1. **Curated system-rule library (~37 named presets).** Replace the 6 toggles with the reference's full preset set — DNS, DHCP, mDNS, LLMNR, SSDP, UPnP, NTP, NetBIOS, SMB, RDP, KMS, IGMP, ICMPv4/v6, QUIC, SSH, FTP, IMAP/POP3/SMTP, Windows Update, and more — each individually toggleable.
   *Improvement:* grouped by category, searchable, with plain-English descriptions and a "recommended secure baseline" one-click profile.
2. **Close active connection.** Right-click a live connection → terminate it (TCP RST via `SetTcpEntry`).
   *Improvement:* a combined "Block app + close all its connections" action.
3. **App color-coding / highlighting.** Categorize apps (signed / unsigned / system / invalid-or-missing-file / has-active-connection) with a color legend, like the reference's 7 highlight types.
   *Improvement:* click a category to filter the list to just those apps.
4. **Packets log to file.** Stream the packet log to disk, not just the in-app view.
   *Improvement:* rotating log files + CSV/JSON export for analysis.
5. **Notification upgrades.** Sound on popup, tray balloon notifications, fullscreen-silent mode, and a per-app "remember my choice" default action.

## Phase B — v0.21 "Kernel Hardening" *(moderate WFP work, fault-tolerant)*

Real firewall-grade protection. Each follows filter patterns already proven in GunWall.

1. **Stealth mode.** ✅ SHIPPED (v0.21) — Silent drops + discard-layer filters to defeat port scanning and make the machine invisible to unsolicited inbound traffic.
   *Improvement:* a clear on/off with an explainer of what it hides.
2. **Boot-time filters.** Protection active during boot, before the app starts (BOOTTIME-flagged filters on the sublayer).
   *Improvement:* a dashboard indicator showing boot protection is armed.
3. **Filter tamper protection.** A secure sublayer so other software can't delete GunWall's WFP filters.
   *Improvement:* detect tampering attempts and surface an alert.
4. **WUFix (Windows Update keep-alive).** Preset rules that keep Windows Update working under lockdown.
   *Improvement:* auto-detect when Update is being blocked and offer the fix inline.
5. **Expanded WFP layer coverage.** Add LISTEN, RESOURCE_ASSIGNMENT, and ICMP_ERROR layers toward the reference's 22 (added fault-tolerantly so an unsupported layer can't break the engine).
   *Improvement:* a per-layer health/diagnostics view.

## Phase C — v0.22 "Windows Integration" *(moderate)*

Coexist with and absorb the built-in Windows stack.

1. **Windows Firewall enable/disable + coexistence.** Control Windows Defender Firewall from GunWall.
2. **Import existing Windows Firewall rules.** Read current WF rules so users don't start from scratch.
3. **App process categorization.** Detect system / pico / undeletable processes and protect critical system apps from accidental blocking.
4. **Refresh filters on device connect** + advanced engine options (keep-unused-apps, single-click tray, similar-notification timeout).
5. **Auto-backup + versioned profiles.** Automatic profile backups with restore points.
   *Improvement:* timestamped, browsable backup history.

## Phase D — v0.23 "UWP & Store Apps" *(risky — dedicated, fewer features, more iteration)*

The reference's package-SID filtering. Hand-written SID interop that can't be compile-tested here, so this phase is deliberately smaller and expects several test rounds.

1. **UWP / Store app enumeration** — list installed packages (registry / COM).
2. **Package SID resolution** + app-container handling.
3. **UWP app blocking** via `FWPM_CONDITION_ALE_PACKAGE_ID`.
4. **UWP rules persistence** + a dedicated "Store apps" tab.

## Phase E — v1.0 "Production" *(ship to real users)*

1. **Installer** (Inno Setup) — Start-menu shortcut, uninstall, no more unzip-and-run.
2. **Auto-update** — download and apply updates, not just check.
3. **Code signing** — sign the EXE so SmartScreen/UAC trust it. *(Requires you to obtain a code-signing certificate — the only step that lives outside the code.)*
4. **Localization** — multi-language framework + an initial set of languages.
5. **Final audit** — stability, performance, and security pass + user documentation.

---

## What needs your input

- **Code signing (Phase E):** a paid code-signing certificate. Everything else I can build; signing requires the cert on your side.
- **Testing each release on real Windows 11 hardware** — I can't compile or run WPF here, so your build-and-report loop is what validates every phase. Phases B and D especially will need a few iterations.

## Risk summary

| Phase | Risk | Why |
|-------|------|-----|
| A | Low | Managed code + engines we already have |
| B | Medium | New WFP filters, but proven patterns + fault-tolerant |
| C | Medium | OS integration (netsh/COM), mostly managed |
| D | High | Hand-written package-SID kernel interop, untestable here |
| E | Low–Medium | Tooling + packaging; signing needs your certificate |
