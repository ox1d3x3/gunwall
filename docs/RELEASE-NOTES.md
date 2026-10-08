<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../branding/png/banner-slim-dark.png">
  <img src="../branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

# GunWall 0.99.188 — public beta

A zero-trust application firewall for Windows 11, built on the Windows Filtering
Platform. Free, MIT-licensed, no telemetry.

## Downloads

| File | Use |
|---|---|
| `GunWall-0.99.188-setup.exe` | **Recommended.** Installs, upgrades in place keeping your rules, and its uninstaller removes every GunWall filter before deleting anything |
| `GunWall.exe` | Portable. Create `portable.txt` beside it to keep the data next to the executable |

Each release lists the SHA-256 of every file. Check yours with
`certutil -hashfile <file> SHA256` before running it.

## Upgrading

Install over the previous version. Your rules and settings are kept, and before
this version first writes to them it saves the profile as
`rules.pre-<previous version>.json` in `C:\ProgramData\GunWall`. A VirusTotal key
saved by an earlier version is encrypted on first start; an older build started
afterwards cannot read it and needs the key entered again.

## What has changed since the first public beta (0.99.109)

**Protection you can rely on after a restart**
- Kernel filters no longer survive a reboot, so a reboot always gets a machine back
  online. GunWall reinstalls everything at startup — application rules, system
  rules, scope and country blocks, custom rules and blocklists — with protection
  back about seven seconds after launch on a typical boot.
- GunWall acts only on filters proven to be its own, and only one copy runs at a
  time; starting it again shows the running window.
- Approvals and blocks are rules: kept across restarts and when protection is
  switched off and on.
- GunWall finds its own filters by asking the Windows filter engine directly,
  rather than running `netsh` - faster, and complete.
- Microsoft Store apps keep their rule when they update.

**Updates and upkeep**
- Update checking — off by default, Daily, Weekly or Monthly — with optional
  download. The installer is verified again when **Update now** is pressed, and
  nothing installs itself. A yellow tray dot shows a waiting update.
- GeoIP and MAC-vendor databases are downloaded on request, validated before use,
  and can be refreshed automatically (off by default; skipped on metered
  connections).
- The profile is snapshotted before every upgrade; automatic backups are available.

**Privacy and trust**
- The VirusTotal API key is stored encrypted with Windows DPAPI, readable only on
  the PC that saved it, including in backups and upgrade snapshots. Settings shows
  that a key is saved and never shows the key.
- The diagnostics export redacts the key and records performance and filter
  integrity, so a bug report carries evidence rather than guesses.

**Network scan**
- Device manufacturer from the MAC address, your own note per device, and copying
  of any field, row or the whole table.
- Device names announced over mDNS / Bonjour ("Living Room TV", "Kitchen
  Speaker"), with the model on hover, and `.local` host names for phones, Macs and
  smart-home devices that reverse DNS and NetBIOS cannot name.

**Interface**
- A first-run welcome screen offers the optional databases, shows each download
  until it is loaded, and ends with the welcome - instead of a popup over a
  half-ready window. After an update, an **Update complete** screen links to
  what's new.
- Explanations sit behind an **ⓘ** icon beside each heading and option, shown on
  hover, instead of paragraphs under every heading.
- Blocklist categories can be trimmed domain by domain (**Show domains**), and a
  domain can be blocked everywhere with `!!`.
- Application icon size: small, medium or large.
- UI size from 50% to 125%, also with Ctrl + mouse wheel, Ctrl + plus / minus and
  Ctrl + 0.
- The tray menu is drawn in GunWall's own colours, with a status line at the top,
  and offers **Engage lockdown** or **Release lockdown** as it applies; the tray
  tooltip says when lockdown is on.
- Block and Allow change the Applications row the moment they are pressed.
- Every confirmation and error uses GunWall's own themed dialog, with buttons that
  say what they do where it matters (e.g. **Turn off and exit**), and every
  window's title bar follows the light or dark theme.
- The Location column says why an address has no country.

**Performance**
- Idle memory about 150–190 MB (from about 350 MB) and idle CPU about 0.2%.
- No redrawing while the window is hidden or behind other windows.

**Removed**
- The *Ads & trackers* switch, which only pointed Windows DNS at a public filtering
  service and could take the connection down. The DNS resolver's ads and malware
  preset and the Filtering DNS card remain.

## Known limitations

- Filters are not enforced from boot until GunWall starts; enable **Run GunWall
  when Windows starts** (Settings → Preferences).
- GunWall runs as a single elevated process; a privileged service is planned
  before 1.0.
- Releases are not code-signed. The published checksums are the integrity check.

The full detail of every release is in [CHANGELOG.md](../CHANGELOG.md).
