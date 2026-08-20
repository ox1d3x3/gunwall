<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../branding/png/banner-slim-dark.png">
  <img src="../branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

# How GunWall compares

Windows has several good firewalls, and some of them will suit you better than
this one. This page is meant to help you decide, not to sell you anything.

**Verified August 2026.** Version numbers and prices move; check the projects
themselves before relying on any of it. Corrections are welcome as
[issues](https://github.com/ox1d3x3/gunwall/issues) — an inaccurate claim about
someone else's work is a bug here like any other.

---

## The short version

| If you want… | Use |
|---|---|
| The smallest possible footprint | **simplewall** — under 1 MB, does one thing well |
| No popups, ever | **TinyWall** — whitelist-based, deliberately silent |
| Cross-platform, with a company behind it | **Portmaster** — Windows and Linux, Safing/IVPN |
| The most polished monitoring | **GlassWire** — commercial, and it shows |
| Deep visibility with per-application domain blocking, free | **GunWall** |

---

## Side by side

|  | **GunWall** | simplewall | TinyWall | Portmaster | GlassWire |
|---|---|---|---|---|---|
| **Licence** | MIT | GPLv3 | Open source | Open source | Proprietary |
| **Cost** | Free | Free | Free | Free + Pro €80/yr | Freemium, ~$36–39/yr |
| **Platform** | Windows | Windows 7–11, ARM64 | Windows | Windows, Linux | Windows, Android |
| **Approach** | Own WFP layer | Own WFP layer | Wraps Windows Firewall | Own kernel layer | Wraps Windows Firewall |
| **Default posture** | Deny, prompt once | Deny, prompt | Whitelist, no prompts | Deny, prompt | Monitor, block on request |
| **Size** | ~190 MB | < 1 MB | ~2 MB | Moderate | Moderate |
| **Signed binaries** | No (checksum published) | GPG signature | Yes | Yes | Yes |
| **Maturity** | Beta, 2026 | Since 2016 | Since 2011 | Since 2019 | Since 2014 |
| **Country / ASN rules** | Yes | No | No | Yes | No |
| **Built-in DNS resolver** | Yes, with DoH | No | No | Yes | No |
| **Per-app domain blocking** | Yes | No | No | Per-app profiles | No |
| **Signature & hash checks** | Yes | Yes | No | No | Yes |
| **VirusTotal lookups** | Yes | No | No | No | Yes |
| **Traffic map** | Yes | No | No | No | Yes |
| **Uninstall removes filters** | Yes, automatically | **No — manual step** | N/A | Yes | N/A |

---

## What GunWall does that the others do not

**Domain blocking scoped to the application that asked.** When a blocklist matches,
GunWall blocks that address *for the program that requested the name*, not for the
whole machine. Blocking a tracker your browser fetched cannot disconnect your
antivirus, even when both sit behind the same content-delivery network. Tools that
block at the address layer globally cannot make that distinction.

**Country and network-operator rules in a free tool.** Block a continent, a
country, or a specific network operator by ASN. Portmaster offers this; the free
Windows-native tools generally do not.

**Verified teardown.** Every route out — the protection switch, *Remove all
filtering*, and the uninstaller — returns the machine to Windows defaults, and you
can confirm it with `netsh wfp show filters` rather than taking anyone's word.
This matters more than it sounds: simplewall's own documentation notes that *"when
you uninstall simplewall, all previously configured filters stay alive in
system"*, and it is the single most common way people end up with a machine
filtering traffic and no idea why.

**A DNS resolver and an application firewall in one process**, so a blocked name
and a blocked connection are the same decision rather than two tools disagreeing.

**On VirusTotal**, this is not a GunWall exclusive — GlassWire has had it since
version 2.0 in 2017, and it is the more established implementation. Key handling
has changed on their side over the years, so check their current documentation
rather than this page for how it is set up today.

The difference that has stayed constant: GunWall queries by SHA-256 and reports
*"not found"* when the hash is unknown, whereas GlassWire can optionally upload
the file itself. Uploading gets you an answer for files VirusTotal has never seen;
querying by hash alone means nothing but the hash leaves your machine. Neither is
simply better, and which you prefer depends on what you are protecting.

---

## What the others do better

**simplewall is a fraction of the size.** Under 1 MB against GunWall's ~190 MB.
It runs on Windows 7 through 11 including ARM64, it has GPG-signed binaries, and
it has been maintained since 2016. If you want a WFP firewall and nothing else,
it is the more sensible choice and it is not close.

**TinyWall never interrupts you.** It whitelists rather than prompting, which is a
genuinely different philosophy — and for many people the better one. GunWall's
first ten minutes are a stream of prompts; TinyWall's are silent.

**Portmaster is cross-platform and better resourced.** It runs on Linux too, has a
company behind it, a paid tier funding development, and years of hardening.
Reports of higher memory use are common, but so is the view that it is the most
capable free option.

**GlassWire's monitoring is more polished**, and it has been refined commercially
since 2014. If visualising bandwidth is your main goal rather than controlling it,
GlassWire does that better — and it carries security features GunWall has no
answer to, including evil-twin Wi-Fi detection, suspicious-host alerting from a
maintained list, and remote monitoring of other machines.

**All four are signed.** GunWall is not, deliberately — a certificate is a
recurring cost this project will not pass on — but that means SmartScreen warnings
and antivirus false positives that the others do not have.

---

## When not to choose GunWall

Plainly:

- **You need something proven.** GunWall is weeks old with a handful of users.
  simplewall and TinyWall have a decade each.
- **You are on limited disk or an older machine.** ~190 MB self-contained against
  under 1 MB.
- **You want zero prompts.** Use TinyWall.
- **You need Linux or macOS.** Portmaster, or Little Snitch on macOS.
- **Unsigned binaries are unacceptable to you or your organisation.** A reasonable
  position, and GunWall does not meet it.
- **You want a support contract.** There isn't one.

---

## When GunWall is the right choice

- You want to **see** what your machine talks to — which countries, which
  operators, which applications, in what volume — and control it in the same place
- You want **blocklists that do not cause collateral damage**, because blocking is
  scoped to the application
- You want **country or ASN rules** without paying for them
- You want to be able to **verify what a firewall did to your machine**, and undo
  it completely, using Windows' own tools
- You are comfortable running beta software and reporting what breaks

---

## A note on running two firewalls

GunWall, simplewall, Portmaster and Windows Firewall all filter independently.
Running GunWall alongside Windows Firewall is fine and expected — GunWall never
modifies Windows Firewall's rules.

Running **two third-party application firewalls at once is not recommended.** They
do not coordinate, both will prompt, and diagnosing which one blocked something
becomes guesswork. Pick one.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
