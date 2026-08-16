# GunWall 0.99.109 — first public beta

A zero-trust application firewall for Windows 11, built directly on the Windows
Filtering Platform. Free, MIT-licensed, no account, no telemetry, no ads.

**[Download](https://github.com/ox1d3x3/gunwall/releases/latest)** ·
`GunWall-0.99.109.0-setup.exe` for the installer, or `GunWall.exe` to run portable.

---

## What it does

Every application must be approved before it reaches the network. Anything without
a rule is denied by the kernel and raises a prompt showing who is asking, where
they are going, and what is known about them — publisher, signature status,
destination country and network operator, and a VirusTotal verdict if you supply a
key.

Beyond that:

- **Ordered custom rules** matching on address, port, protocol, direction, domain,
  country or ASN
- **A curated system-rule library** — stealth mode, block inbound, block SMB,
  NetBIOS, Telnet and RDP, allow common services, with kernel coverage shown
- **A local DNS resolver** with DNS-over-HTTPS, a fail-closed default,
  CNAME-cloaking defence, and blocklists with an explicit allow level
- **Per-application domain blocking**, so blocking a tracker cannot disconnect
  anything else that shares its address
- **Live visibility** — connections, packet log, per-application bandwidth, a
  connection map, and a network scanner that identifies devices by name, likely
  operating system and gateway role
- **Lockdown** to cut all traffic instantly, and **snooze** to pause enforcement
  for a set period
- **Rule profiles** for switching whole rulesets

## Getting the machine back

A firewall you cannot turn off is a trap. GunWall's filters are persistent by
design — they survive closing the app, a crash and a reboot — so every exit is
verified against the kernel rather than against GunWall's own reporting:

| Route | What it does |
|---|---|
| **Protection switch** | Removes every filter. Rules are kept for when you switch back on. |
| **Remove all GunWall filtering** | Removes filters, clears the hosts file, restores adapter DNS, and clears saved rules. |
| **Uninstaller** | Runs the above automatically, checks it succeeded, and stops and warns if it did not. |
| **`GunWall.exe --unblock`** | Restores the machine from a command prompt when the interface will not open. |

You can confirm any of these independently:

```
netsh wfp show filters file=%TEMP%\gw.xml
```

Search that file for `8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00`. Zero matches means the
machine is at Windows defaults, reported by Windows rather than by GunWall.

## Before you install

- **Windows 10 (2004+) or Windows 11**, 64-bit, administrator rights.
- **Expect prompts for the first ten minutes.** Default-deny means every program
  asks once.
- **SmartScreen will warn you.** GunWall is deliberately not code-signed — a
  certificate is a recurring cost this free project will not pass on. Verify the
  SHA-256 published with the release instead:
  `certutil -hashfile GunWall.exe SHA256`
- **If you run portable, use *Remove all GunWall filtering* before deleting the
  folder.** Filters live in the kernel and outlive the folder. The installer's
  uninstaller does this for you.

## Known limitations

- **Blocking a domain hosted on a large CDN is unreliable.** GunWall blocks the
  addresses it has observed a name resolve to; large providers rotate faster than
  that. Domain blocking works well against trackers on stable hosts.
- **A closed GunWall cannot prompt.** Filters keep enforcing, so an unapproved
  program is correctly denied and simply fails. Enable *Run at startup* if that
  matters to you.
- **Single elevated process.** Service isolation is the last architectural item
  before 1.0.
- Tested on a small number of machines. Your Windows build, VPN and security
  software are combinations nobody has tried.

## Reporting a problem

**Settings → Export diagnostics (.zip)** and open an issue. The bundle contains the
session log, your settings with secrets removed, active rules and network
configuration — no browsing history and no personal data.

Describe what you saw rather than what you think caused it. A full-window
screenshot helps for anything visual.

**[github.com/ox1d3x3/gunwall/issues](https://github.com/ox1d3x3/gunwall/issues)**

---

The complete history of every change is in [`CHANGELOG.md`](../CHANGELOG.md).
