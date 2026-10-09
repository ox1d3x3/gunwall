<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../branding/png/banner-slim-dark.png">
  <img src="../branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

<div align="center">

# GunWall — User Guide

**Complete documentation for installing, configuring and running GunWall.**

For an overview of what GunWall is, see the [README](../README.md).

</div>

---

## Contents

**Getting started**
1. [Before you install](#1-before-you-install)
2. [Installing](#2-installing)
3. [Your first ten minutes](#3-your-first-ten-minutes)
4. [How GunWall decides](#4-how-gunwall-decides)

**Daily use**
5. [The connection prompt](#5-the-connection-prompt)
6. [Applications](#6-applications)
7. [Rules](#7-rules)
8. [Security and blocklists](#8-security-and-blocklists)
9. [The DNS resolver](#9-the-dns-resolver)
10. [Watching traffic](#10-watching-traffic)
11. [Windows services](#11-windows-services)
12. [Network scanner](#12-network-scanner)

**Reference**
13. [Settings](#13-settings)
14. [Profiles, lockdown and snooze](#14-profiles-lockdown-and-snooze)
15. [When something is blocked and should not be](#15-when-something-is-blocked-and-should-not-be)
16. [Recovery](#16-recovery)
17. [Uninstalling](#17-uninstalling)
18. [Reporting a problem](#18-reporting-a-problem)
19. [Frequently asked questions](#19-frequently-asked-questions)

---

# Getting started

## 1. Before you install

**What you need**

- Windows 10 (version 2004 or later) or Windows 11, 64-bit
- Administrator rights — GunWall cannot add or remove kernel filters without them

**What to expect**

GunWall is a **default-deny** firewall. Once protection is on, every application
must be approved before it reaches the network. For the first ten minutes you will
be answering prompts. That is the product working, not a fault.

**Two things worth understanding first**

*Filters are persistent.* GunWall's rules live in the Windows kernel, not in the
application. Closing GunWall, a crash, or a reboot does not stop them enforcing.
That is what a firewall must do — but it also means you cannot undo GunWall by
closing it. [Chapter 16](#16-recovery) covers every way to stop it.

*A closed GunWall cannot ask you anything.* Filters keep working, but with the
application shut there is no prompt, so a new program is denied and simply fails
with no explanation. If that matters to you, leave **Run GunWall when Windows
starts** enabled.

---

## 2. Installing

### With the installer (recommended)

1. Download `GunWall-<version>-setup.exe` from
   [Releases](https://github.com/ox1d3x3/gunwall/releases/latest).
2. Windows SmartScreen will warn you, because GunWall is not code-signed. Choose
   **More info → Run anyway**, or verify the checksum first (below).
3. Follow the installer. Leave **Start GunWall when Windows starts** ticked unless
   you have a reason not to.

**First launch.** A fresh install opens with a welcome screen. It offers the two
optional databases (see [Additional data](#additional-data)) - **Download now**
fetches them and waits until both are loaded; **Not now** skips them. **Get
started** then takes you into the app. It is shown once, and not after an upgrade.
GunWall starts in **Monitoring only**: nothing is blocked until you turn
protection on.

**After an update.** The first launch of a newer version shows **Update complete**
with the old and new version numbers. **See what's new** opens the changelog on
GitHub; **Continue** goes into the app. Your rules and settings are kept, and the
previous profile is saved as `rules.pre-<version>.json` (see
[Where your data lives](#where-your-data-lives)).

The installer's real advantage is its **uninstaller**, which removes GunWall's
kernel filters before deleting anything. See [Chapter 17](#17-uninstalling).

### Portable

1. Download `GunWall.exe` from the same release.
2. Right-click → **Run as administrator**.

Nothing is written outside your data folder. **But read
[Chapter 17](#17-uninstalling) before you ever delete the folder** — the filters
outlive it.

### Verifying your download

Each release publishes the SHA-256 of every file:

```
certutil -hashfile GunWall.exe SHA256
```

If it matches, the file is byte-for-byte what was built from the published source.

### Where your data lives

`C:\ProgramData\GunWall` — rules, settings, logs and cached lookup data.
**Settings → Diagnostics → Open data folder** opens it.

| File or folder | What it is |
|---|---|
| `rules.json` | Your rules and settings |
| `rules.pre-<version>.json` | The profile exactly as the previous version left it, saved when a new version first starts. The newest three are kept |
| `backups\` | Backups from **Settings → Backups** |
| `profiles\` | Named rule profiles |
| `updates\` | A downloaded update waiting for **Update now** |

Your VirusTotal key is the only encrypted value in the profile; everything else is
plain JSON.

It is deliberately **outside** the application folder so that updating GunWall
never disturbs it. To keep everything together on a USB stick instead, create an
empty file called `portable.txt` beside `GunWall.exe`.

---

## 3. Your first ten minutes

1. **Start GunWall.** It opens on the **Overview** page with protection off. Nothing is
   being blocked yet.

2. **Watch for a minute or two.** Open **Connections** and **Applications** and
   look at what your machine already talks to. This is worth doing before you turn
   anything on — it is usually more than people expect.

3. **Turn protection on** using the switch in the bottom-left panel. GunWall
   installs its baseline and begins denying anything without a rule.

4. **Answer the prompts.** Approve programs you recognise. The ones you do not
   recognise are the point of the exercise — [Chapter 5](#5-the-connection-prompt)
   explains what the prompt is telling you.

5. **Do not rush to allow everything.** If something you were not using asks for
   the network, blocking it costs you nothing; you can allow it later from
   **Applications**.

> **Tip.** Core Windows networking — DNS, DHCP and the service host that runs them
> — is permitted automatically. You will not be asked whether your machine may
> resolve names.

> **Explanations.** A small **ⓘ** icon beside a page title, a section heading or an
> option means there is more to read. Rest the pointer on it and the explanation
> appears; it stays open while the pointer is there.

---

## 4. How GunWall decides

GunWall's filters are weighted, and for each connection the strongest matching
filter decides:

| Strength | Layer | Example |
|---|---|---|
| 1 | Core networking | Loopback, DHCP, DNS and local network infrastructure - always permitted, so the machine stays reachable |
| 2 | Lockdown | Everything else blocked |
| 3 | Block rules | "Block this app", blocked services, custom block rules |
| 4 | Allow rules | Approved applications, system-rule allows, custom allow rules |
| 5 | Zero-trust baseline | With protection on, anything left over is denied |

Two consequences worth knowing:

- **A block beats an allow.** If an application is allowed but a custom rule blocks
  the address it wants, the connection is blocked.
- **Custom rules have no order of their own.** Among them, too, a block always
  beats an allow - there is nothing to rearrange.

GunWall runs as an independent filtering layer and **does not modify your Windows
Firewall rules**. Both apply; either can block.

---

# Daily use

## 5. The connection prompt

When a program with no rule tries to connect, GunWall shows a small window in the
corner.

**What it tells you**

| Field | Meaning |
|---|---|
| Program name and path | Who is asking. Check the path — a familiar name in an unfamiliar folder is worth a second look. |
| Signature | A **Signed**, **Unsigned** or **Invalid** chip from the code signature; hover it for the publisher. **Unsigned** is not automatically bad, but it is worth noticing. |
| Destination | Address and port, with the country's flag where known (hover it for the name). |

**Your options**

- **Allow** — a permanent rule; the program is never asked about again
- **Block** — a permanent rule denying it
- **Chevron (⌄)** — expands full detail: reverse DNS, exact time, full path

**If you do nothing,** the prompt stays open by default. You can change that under
*Settings → Popup stays open for* — but be careful: with a timeout set, anything
you do not answer in time gets a **permanent** rule.

**Closing the prompt without answering blocks that connection.** No rule is saved,
so you will be asked again next time.

**Several at once.** When three or more programs are waiting — after a reset, a
fresh install or at startup — they are listed in one window instead, each with
**Allow** and **Block**, plus **Allow all remaining** and **Block all remaining**.
Anything left undecided stays blocked. Turn this off with *Settings → Show several
waiting apps in one window*.

**Windows' own core processes** (System, smss, csrss, wininit, winlogon, services,
lsass, lsaiso, spoolsv — from the System32 folder only) are allowed without
asking, because blocking them breaks Windows. `svchost` still asks. Turn this off
with *Settings → Allow core Windows processes without asking* for the strictest
zero trust.

---

## 6. Applications

Every program GunWall has seen, with its status, connection count and live
activity.

**What you can do**

- **Allow** or **Block** any application with the button on its row
- **Filter** by name using the search box
- **Show all running apps** to include programs that have not yet used the network
- **Block an app** to add one by browsing to its executable
- **Purge unused** to clear entries with no rule and no connections
- **Right-click a row → Properties…** for full details: publisher, hash,
  signature status, package type and a notes field of your own. **Open file
  location** and **Copy path** sit under the path; **Allow**, **Block** and
  **Close** along the bottom
- **Allow** and **Block** change the row at once; the rule is applied straight
  after

**The coloured dot** beside each name shows signature status — signed, unsigned,
Windows system, or invalid signature. Colours are configurable under
*Settings → Appearance - Category colors*. Icon size is under *Settings →
Preferences → App icon size*.

> **When a program updates**, it often moves to a new versioned folder and its old
> rule stops matching. For a **Microsoft Store** app (marked with a **Store**
> badge), GunWall finds the updated package and moves the rule to it, so your
> decision is kept. For other programs, the stale entry is removed at startup and
> the log says so; the program asks again the next time it connects.

---

## 7. Rules

Three sections: **Custom Rules**, the **IP blocklist** and **System Rules**.

### Custom rules

Your own rules. Each matches on any combination of:

- **Action** — allow or block
- **Direction** — outbound or inbound
- **Protocol** — TCP, UDP, or any
- **Remote address** — an IPv4 address or subnet (e.g. `192.168.1.0/24`), or blank for all
- **Port** and **local port**

Leave a field blank to match everything. A block always beats an allow (see
[How GunWall decides](#4-how-gunwall-decides)).

### System rules

A curated library of common firewall policies, each applying immediately:

- **Stealth mode** — makes the machine quieter to port scans
- **Block all inbound connections** — outbound still works
- **Block file sharing (SMB)**, **NetBIOS**, **Telnet**, **inbound Remote Desktop**
- **Allow common services** — Windows Update delivery, IPv6 transition protocols
- **Secure baseline** — applies a sensible set in one click

The kernel-coverage line beneath tells you how many filters are actually installed,
which is a useful sanity check.

### IP blocklist

Paste IPv4 addresses or subnets, one per line, to block them outright for every
application.

---

## 8. Security and blocklists

### Threat and telemetry blocklists

Toggle categories on or off:

- **Windows telemetry and tracking** — diagnostic and telemetry domains
- **Windows Update servers** — leave off unless you deliberately want updates stopped

Telemetry and update categories use the Windows hosts file, falling back to
firewall rules automatically if security software locks that file.

**Show domains** beside each category lists every domain in it, with a tick. Ticked
domains are blocked while the category is on; untick one to keep it working —
for example, a domain a program you use needs. Type in the box at the top to
filter the list, then press **Save**. Your choices are kept when the lists are
updated from online, and the category's description shows how many are unticked.

### Custom blocklist file

Point GunWall at your own list of domains. Hosts-style lines (`0.0.0.0 domain`),
plain domains and pasted URLs are all understood.

### How domain blocking actually works

Two mechanisms, and it is worth knowing which is doing what:

1. **Through GunWall's resolver.** A blocked name returns NXDOMAIN to anything
   using GunWall for DNS.
2. **In the kernel**, when *Watch system DNS lookups* is on. GunWall observes the
   answers Windows receives and blocks the resolved address **for the application
   that asked for it**.

The second is scoped to one application deliberately. A tracker your browser
fetched cannot disconnect your antivirus, even when both sit behind the same
content-delivery network.

> **Limitation, stated plainly.** Blocking a domain hosted on a large CDN is
> unreliable. GunWall blocks addresses it has observed; large providers rotate
> faster than that. Domain blocking works well against trackers on stable hosts. It
> will not reliably stop a major website.

### GeoIP data source

Where country and network-owner (ASN) information comes from:

- **Local database (download)** — the IPv4 and IPv6 tables from *Settings →
  Additional data*. Works offline.
- **Self-hosted API server** — your own
  [iptoasn-webservice](https://github.com/jedisct1/iptoasn-webservice) instance;
  nothing to download. Press **Test**, then **Save**.

Lookups are cached, so each address is looked up once.

### Country and ASN blocking

Block one application — **Choose app…** — or **All apps** from reaching a whole
**Country** (`RU`), **Continent** (`EU`) or network operator (**ASN**, `AS13335`),
then **Add rule**. It needs a GeoIP data source (above).

Matching is reactive: GunWall blocks an address the moment it sees a connection to
it, so it is most effective against repeated or sustained traffic. **Clear active
blocks** removes the addresses blocked so far; the rules stay.

### Filtering DNS

Points Windows at a filtering DNS service for a second blocking layer with no lists
to maintain — **AdGuard** blocks ads and trackers, **Quad9** blocks malware and
phishing. **Apply DNS** sets it on all active adapters; every change is logged.

---

## 9. The DNS resolver

GunWall includes a local DNS resolver. It is **optional** — nothing uses it unless
you point something at it.

### Turning it on

1. **DNS resolver → Start resolver.** It listens on `127.0.0.1:53`.
2. A few seconds later it checks itself and reports whether it can actually answer.
3. To use it, set an adapter's DNS server to `127.0.0.1`.

If nothing points at it, the query log stays empty. That is correct, not a fault.

### Encrypted DNS

Choose a provider under **Secure DNS**. Lookups then travel over HTTPS.

- **Fall back to plaintext if encryption fails** — off by default. Left off, a
  failed encrypted lookup **fails**, rather than quietly going out unencrypted.
- **Block CNAME-cloaked trackers** — follows each answer's alias chain and refuses
  the lookup if any hop is on a blocklist.

### The blocklist box

One domain per line. Subdomains are included automatically — `example.com` also
covers `ads.example.com`.

**Prefix a line with `@@` to allow it instead.** `@@ssl.gstatic.com` keeps that name
working even when a downloaded preset blocks it, so one bad entry in a
hundred-thousand-domain list does not mean abandoning the whole category.

**Prefix a line with `!!` to block it everywhere.** Normally, when GunWall cannot tell
which program asked for a blocked domain, it blocks the server's address only if
everything else seen on that address is blocked too — a shared server (a CDN) also
serves sites you have not blocked, and blocking its address would cut those off.
`!!example.com` overrides that: the address is blocked anyway, those other sites stop
working on this PC, and the **Alerts** page names them. Use it only for a domain you
want gone at any cost. An `@@` allow still wins over `!!`.

Press **Apply blocklist** after editing. Note that this clears and rebuilds the
kernel-level domain blocks, so give it a moment to re-establish.

---

## 10. Watching traffic

**Overview** — protection state, uptime, live throughput, a 60-second graph, top
talkers, and recent allow/block decisions. **Apps seen** counts every application
with a live connection plus every application you have a rule for; it, **Blocked**
and **Allowed** are not affected by the search or options on the Applications page.

**Activity** — a running history of what GunWall did and why.

**Packet log** — every connection event from the kernel, with the reason for each
verdict. When something is blocked by software other than GunWall, this says so
explicitly rather than claiming GunWall allowed it.

**Connections** — live sockets with process, protocol, local and remote addresses,
country and network operator. Select a row for a detail panel showing the rule that
applied. Where an address has no country, the Location column says why — *This PC
(loopback)*, *Local network*, *Multicast*, *Not routed*, *Not in GeoIP data*, or *No
GeoIP data* when the database has not been downloaded. A socket waiting for
connections, with no remote yet (TCP or UDP), reads *Listening*.

**Traffic** — a world map of destinations, top countries, most active applications,
per-application data usage over a chosen window, and a breakdown by host, traffic
type and country.

> **Per-application byte counts are estimated by default**, attributed by share of
> active connections. For measured figures, enable *precise per-app data metering*
> in Settings. The banner at the top of Traffic always tells you which is in use.

---

## 11. Windows services

Windows services run inside shared host processes, so blocking `svchost.exe` would
block dozens of unrelated things. This screen lets you allow or block **individual
services** by their service identity instead.

Useful when you want a specific Windows component off the network without
disturbing the rest.

---

## 12. Network scanner

**Network scan → Scan network** sweeps your local subnet and lists every device it
finds.

| Column | Where it comes from |
|---|---|
| IP address | ARP table after a sweep |
| Name | The name the device announces about itself over mDNS (Bonjour) — e.g. *Living Room TV*. Hover it for the model and what the device announces |
| MAC address | ARP table |
| Host | Reverse DNS, falling back to NetBIOS, then the device's own `.local` name (mDNS) |
| Vendor | The manufacturer registered for the MAC address — needs the IEEE database from **Settings → Additional data** |
| Likely OS | Inferred from the ping reply's TTL |
| Flags | Gateway role, or a randomised MAC |
| Your note | A label of your own, kept against the device's MAC address |

**Names need the device to announce one.** Chromecasts, Google and Apple TVs,
AirPlay speakers, HomeKit devices, printers, NAS boxes, Macs and
iPhones usually do. Many Windows PCs, cheap IoT plugs and devices on a guest
network do not, and some routers block mDNS between Wi-Fi and Ethernet — those
rows stay blank, which means nothing was announced rather than that something
failed. GunWall only asks; it never announces anything itself.

**Right-click a device** to copy its name, IP, MAC, vendor or host name, the whole row,
or every device as tab-separated text. **Ctrl+C** copies the selected rows.

**Likely OS is a guess and is labelled as one.** It distinguishes families —
Windows, Linux/macOS/Android, router/embedded — and shows nothing rather than
guessing when it cannot tell. Your gateway is identified from the routing table, so
that one is a fact rather than an inference.

**Randomised MAC** means the device chose its own hardware address, which every
modern phone does by default. It is privacy working, not a fault.

---

# Reference

## 13. Settings

**Settings** is the last item in the left-hand sidebar. It is one long page of
cards, each headed with a name in capitals — scroll to find the card named below.
Hover the **ⓘ** beside a card's heading or an option for what it does. Most
options are staged: press **Apply** to save them.

### Firewall mode

**Alert mode (recommended)** allows new applications and notifies you. **Strict
mode** blocks everything except the applications you allow - the same state as the
protection switch in the bottom-left panel. Core Windows networking and loopback
stay allowed either way. **Alert on new apps** (Connection alerts) controls the
notifications.

### Preferences

| Setting | Notes |
|---|---|
| Start minimized to tray | GunWall starts hidden |
| Watch for tampering with the firewall's filters | Detects and restores filters removed by other software |
| Open GunWall with a single tray click | Double-click always works regardless |
| UI size | 50% to 125%; 90% by default. Also **Ctrl + mouse wheel**, **Ctrl + plus / minus**, and **Ctrl + 0** for the default, anywhere in the app |
| App icon size | Small, Medium (default) or Large icons in the Applications list |
| **Run GunWall when Windows starts** | **Recommended** — without it, nothing can prompt after a reboot |
| Send firewall events to the Windows Event Log | For central log collection |
| Play a sound on notification popups | |
| Show a tray notification when a new app is detected | |
| Always confirm before exiting GunWall | Asks even when protection is off |
| Keep apps with no rule and no connections in the list | Otherwise they are tidied away |
| Keep window always on top | |
| Compute SHA-256 hash of apps | Tamper detection: notices when an allowed program's file changes |
| Experimental options | Precise per-app data metering (ETW), and kernel event detection |

Also in **Preferences**:

- **Alert categories** — which categories reach the Alerts page: security,
  protection changes, network, and rules/profiles
- **Popup stays open for** — *Never* by default, so a prompt waits for you.
  **With a timeout set, anything unanswered gets a permanent rule.** **then** sets
  what happens on timeout: Block or Allow
- **Silence popups while a fullscreen app or game is running** — held back and
  shown afterwards
- **Show several waiting apps in one window** — on by default; three or more
  waiting programs are decided in one list ([Chapter 5](#5-the-connection-prompt))
- **Allow core Windows processes without asking** — on by default; off for the
  strictest zero trust ([Chapter 5](#5-the-connection-prompt))
- **Log packets to a CSV file**, rotated at a size you choose; **Keep at most N
  live rows** in the on-screen logs; **Confirm before clearing** the Activity and
  Packet logs
- **Open log folder**, **View error log**
- **Verify kernel layers** — confirms every WFP layer GunWall uses is available
- **Check filter integrity** — confirms every filter it believes it installed is
  actually present

### Diagnostics

- **Export diagnostics (.zip)** — the bundle to attach to a bug report
- **Open data folder**

**App health** shows GunWall's own counters live, the same ones the export records.
**Connectivity check** detects a hotel or airport login page (captive portal);
**Portal mode** pauses filtering for 5 minutes so you can sign in, then resumes.
**Refresh interval** sets how often connections and the graph update - slower uses
less CPU. **Windows Firewall** shows its state per profile.

### Appearance

Interface font, and the colours used for signature categories. The theme is
switched with the sun/moon button at the top right of the window.

### Updates

- **Check now** — looks for a newer release
- **Check for updates automatically** — Daily, Weekly or Monthly. **Off by default**
- **Download updates when one is found** — skipped on metered connections
- **Update now** — appears only when a release is waiting. The download is
  verified again before it runs, and nothing installs without this press

While protection is on, a waiting update shows as a yellow dot on the tray icon.

### Additional data

Two optional databases, each downloaded only when you press **Download now**: the
GeoIP table (country and network owner per connection) and the IEEE registry
(device manufacturers in Network scan). **Keep these up to date automatically** is
off by default.

### VirusTotal

Paste your API key and press **Save key**. It is stored encrypted with Windows DPAPI
and can be read only on this PC; the box stays empty afterwards, and the line below
it says whether a key is saved. To remove the key, save with the box empty. A
profile copied from another PC shows its key as saved but unreadable — enter it
again.

### Profile, profiles and backups

- **Profile (rules backup)** — **Export profile** and **Import profile** write or
  read every rule and setting as one file
- **Profiles** — save every rule **and setting** under a name (e.g. Home, Work,
  Travel) and switch between them
- **Rule profiles** — save only your per-app allow/block decisions under a name
  (e.g. Gaming, Work); applications not in the profile are left as they are
- **Backups** — **Back up now**, **Restore**, and an option to back up on each launch

### Reset

- **Remove all GunWall filtering** — removes everything and clears saved rules,
  which also turns protection off; your VirusTotal key and custom blocklist path
  are kept
- **Reset settings to defaults** — preferences only; **your rules and blocklists
  are kept**

---

## 14. Profiles, lockdown and snooze

**Profiles** save all your rules and settings under a name and switch between them
— for example a permissive profile at home and a strict one on public networks.
**Rule profiles** do the same for per-app allow/block decisions only.

**Turning protection off and on** (the switch in the bottom-left panel) keeps every
approval and block — they are rules. What is reset is the list of applications
GunWall noticed but you never decided on; they ask again the next time they connect.

**Engage lockdown** (bottom-left) cuts all traffic immediately. Use it if you
suspect something is wrong. Press it again to release. The same action is in the
tray icon's right-click menu: it reads **Engage lockdown**, or **Release lockdown**
(in red, with an open padlock) while lockdown is on, and the tray tooltip says
*lockdown, all traffic blocked*. The top line of the tray menu shows the current
state, the same as the sidebar.

Lockdown and a snooze are separate: a snooze pauses your rules, but it does not
lift lockdown. With both on, everything is still blocked, and GunWall shows
*Locked down*.

**Snooze 15 min** (Overview) pauses enforcement for a set period, then restores it
automatically. Useful for installing something that needs broad access, without
leaving protection off and forgetting.

**Exiting.** Closing the window keeps GunWall running in the tray. To exit, use
**Exit** in the tray menu. If protection or lockdown is on, GunWall asks first:
**Turn off and exit** removes enforcement before closing, **Keep on and exit**
leaves your rules enforcing with no window to change them (they stay until the next
restart), and **Cancel** keeps GunWall open.

---

## 15. When something is blocked and should not be

Work through these in order.

**1. Is it GunWall?**

Open **Packet log**, clear the filter box, and reproduce the problem while
watching. If a row appears marked **Blocked**, the REASON column tells you why.

If the reason says **"Blocked by something else — not GunWall"**, then Windows
Firewall, your antivirus or a VPN client stopped it, and changing GunWall's rules
will not help.

If **nothing appears at all**, GunWall never saw the traffic and is not involved.

**2. Allow the application**

**Applications**, find it, press **Allow**. If it is not listed, tick **Show all
running apps**, or use **Block an app** to browse to the executable and then allow
it.

**3. Check custom rules**

A block rule beats an allow. **Rules** — look for anything matching the address or
port involved.

**4. Check blocklists**

If the program reaches a domain, **Security** and the **DNS resolver** blocklist
may be blocking it. Add `@@thedomain.com` to the blocklist box to allow it
specifically without turning off the whole category.

**5. Test with protection off**

Turn protection off with the switch, and try again. If it works, GunWall was the
cause and the packet log will show where. If it still fails, GunWall is not
involved — there are no filters left to block anything.

---

## 16. Recovery

Three routes, in increasing order of thoroughness. All three are verifiable.

### Turn protection off

The switch in the bottom-left. Removes every filter GunWall installed. **Your rules
are kept** — switching back on restores them.

### Remove all GunWall filtering

*Settings → Reset firewall*. Removes every filter, clears GunWall's entries from
the hosts file, restores any adapter DNS it changed, and clears saved rules.

Use this before uninstalling a portable copy.

### Emergency unblock

If GunWall will not start at all, from an **elevated** command prompt in its
folder:

```
GunWall.exe --unblock
```

It removes all filtering, restores the hosts file and adapter DNS, prints what it
did, and exits without opening a window. It runs before any interface is built, so
a broken window cannot prevent recovery.

### Confirming the machine is clean

Do not take GunWall's word for it:

```
netsh wfp show filters file=%TEMP%\gw.xml
```

Open that file and search for `8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00`, GunWall's
sublayer. **Zero matches means the machine is back to Windows defaults** — reported
by Windows, not by GunWall.

If you check *after* restarting GunWall you will see four matches. Those are the
filters permitting GunWall's own executable, and are expected.

---

## 17. Uninstalling

### If you used the installer

**Start Menu → GunWall → Uninstall GunWall**, or Settings → Apps.

The uninstaller **removes all kernel filtering first**, checks that it succeeded,
and stops and warns you if it did not. It then asks whether to delete your saved
rules — answer **No** to keep them for a future reinstall.

### If you are running portable

**Run *Settings → Remove all GunWall filtering* before deleting anything.**

GunWall's filters live in the Windows kernel and survive deleting the folder.
Deleting it first leaves a machine filtering traffic with nothing installed to
manage or explain it.

If that has already happened, run `GunWall.exe --unblock` from any copy of the
executable.

### Removing your data

`C:\ProgramData\GunWall` holds rules and settings. It is left in place unless you
ask for it to be deleted, so reinstalling restores your configuration.

---

## 18. Reporting a problem

1. **Settings → Diagnostics → Export diagnostics (.zip)**. It contains the session log, your
   settings with secrets removed, active rules and network configuration. **No
   browsing history and no personal data.**
2. **Describe what you saw, not what you think caused it.** A description of the
   symptom is more useful than a diagnosis.
3. **Attach a full-window screenshot** for anything visual. A crop hides what an
   element is being measured against.

[github.com/ox1d3x3/gunwall/issues](https://github.com/ox1d3x3/gunwall/issues)

If something is badly broken and you need the machine working immediately, run
`GunWall.exe --unblock` first — it does not destroy the log.

---

## 19. Frequently asked questions

**Does GunWall replace Windows Firewall?**
No. It runs as an independent layer alongside it and does not modify your Windows
Firewall rules. Both apply, and either can block a connection.

**Why does Windows warn me when I run it?**
GunWall is not code-signed. A certificate is a recurring cost this free project
does not pass on. Verify the published SHA-256 instead — it proves the file matches
source you can read.

**I closed GunWall. Why is the internet still blocked?**
Because the filters are in the kernel, not in the application. That is correct
behaviour for a firewall. Restart GunWall, or see [Chapter 16](#16-recovery).

**I closed GunWall and now a new program cannot connect, with no prompt.**
Also expected: filters keep enforcing, but nothing can ask you. Start GunWall and
you will be prompted. Enable **Run GunWall when Windows starts** to avoid it.

**A new program connected (or failed to) and GunWall never asked me.**
Check **Settings → Preferences → Experimental: kernel event detection** is ticked.
Without it GunWall can only notice programs whose connections appear in Windows'
connection list, and a new program blocked on its first attempt may never appear
there. GunWall switches it off by itself only after crashing twice in a row soon
after starting it, and then says so on the **Alerts** page.

**Why does my VPN's country not show on the map?**
The map plots the destinations traffic is going **to**, not the tunnel it travels
through. Changing your VPN exit does not change where the websites are.

**Why do some connections show no country?**
The lookup table does not cover every address. Download the GeoIP data from the
Connections screen if you have not.

**Can I use GunWall on a 32-bit machine?**
No. GunWall is 64-bit only. Windows 11 has no 32-bit edition.

**Does GunWall send anything anywhere?**
No telemetry, no accounts, no ads. It contacts the network only when you ask it to:
checking for updates, downloading GeoIP data or a blocklist, resolving DNS, or
querying VirusTotal if you supply a key.

**What happens if I start GunWall a second time?**
Only one copy runs. Starting it again brings the running window forward.

**Does upgrading keep my rules?**
Yes. Before a new version first writes to your profile, it saves a copy as
`rules.pre-<old version>.json` in the data folder. To go back, close GunWall from the
tray, copy that file over `rules.json`, and start GunWall again.

**Where is my VirusTotal key kept?**
In the profile, encrypted with Windows DPAPI so that only this computer can read
it. A profile exported or attached to a bug report carries nothing usable. Copied
to another computer, the key reads as saved but unreadable, and must be entered
again there. Settings shows that a key is saved; it never shows the key.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
