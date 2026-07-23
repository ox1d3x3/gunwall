# GunWall — Architecture

This document describes how GunWall is **actually built** as of v0.84.0. It
reflects the code in this repository, not an aspirational design. Where the
long-term plan differs from what ships today, that is called out explicitly.

---

## 1. Design goals

1. **One portable executable.** Builds cleanly from Visual Studio with no exotic
   SDKs, no installer, and no packaging step.
2. **Never silently touch the network.** Every filter that exists corresponds to
   an action the user took. A fresh install changes nothing until protection is
   turned on.
3. **Fail loudly, not silently.** A filter that cannot be installed must say so.
   Hiding a failure so the interface looks correct is worse than showing it: the
   user believes they are protected when they are not.
4. **Survive restarts.** Rules are persistent at the OS level and on disk, and
   every filter is removable — including after a crash.
5. **No telemetry, no accounts, no cloud.** Everything stays on the machine.
6. **Zero third-party packages.** The dependency surface is the .NET base class
   library and Win32, so the supply chain is trivial to audit.

GunWall uses **WPF on .NET 8** (`net8.0-windows`) rather than WinUI 3, because
WPF compiles to a clean native-host EXE without MSIX packaging or the Windows
App SDK — which is what makes "open the solution, build, run the EXE" reliable.

---

## 2. High-level structure

```
┌───────────────────────────────────────────────────────────────┐
│                       WPF UI (MainWindow)                      │
│  Dashboard · Apps · Traffic · Connections · Packets · DNS      │
│  Rules · Security · Network · Alerts · Settings                │
├───────────────────────────────────────────────────────────────┤
│  Event-driven detection  +  1-second sampling loop             │
│                                                                │
│  FirewallManager   orchestration, policy, persistence          │
│  AppRuleEngine     pure first-match-wins policy evaluation     │
│  DnsResolver       caching resolver, DoH, blocklists, CNAME    │
│  GeoIpService      country / ASN attribution                   │
│  EtwByteMeter      per-process byte metering (kernel events)   │
│  NetworkMonitor    live connections, throughput                │
│  RuleStore         JSON persistence                            │
├───────────────────────────────────────────────────────────────┤
│  WfpEngine → fwpuclnt.dll     WinVerifyTrust     ETW/advapi32  │
│  hosts file · system DNS · scheduled task                      │
└───────────────────────────────────────────────────────────────┘
```

The whole thing runs as **one elevated process**. There is no Windows Service:
the app requires administrator rights (declared in `app.manifest` with
`requireAdministrator`) because opening the WFP engine and adding filters needs
them. Enforcement does not depend on the app staying open — the filters
themselves are persistent at the OS level (see §6). Splitting into a privileged
service is tracked for 1.0.

---

## 3. Projects and folders

| Path | Responsibility |
|------|----------------|
| `App.xaml(.cs)` | Entry point, theme dictionaries, global exception traps and fault classification. |
| `MainWindow.xaml(.cs)` | The shell: navigation rail, all panels, sampling loop, hand-drawn charts, dialogs. |
| `Themes/` | Dark and light palettes and all control styles (no third-party theme library). |
| `Models/` | Data types: connections, apps, rules, access policies, notifications, the system-rule catalog. |
| `Services/Wfp/WfpNative.cs` | Raw P/Invoke surface for WFP: layer and condition GUIDs, structs, entry points. |
| `Services/Wfp/WfpEngine.cs` | Safe managed facade over WFP. Filter construction, weights, removal, self-test. |
| `Services/Wfp/NetEventMonitor.cs` | Kernel net-event subscription for event-driven detection. |
| `Services/FirewallManager.cs` | The one class the UI talks to for policy. Owns the engine and the store. |
| `Services/AppRuleEngine.cs` | Pure, testable first-match-wins evaluator plus the IP scope classifier. |
| `Services/DnsResolver.cs`, `DnsMessage.cs` | Resolver, DoH transport, blocklists, CNAME-chain inspection, wire-format parsing. |
| `Services/EtwByteMeterService.cs` | Real-time ETW session against the kernel network provider. |
| `Services/GeoIpService.cs`, `GeoData.cs` | Country and ASN attribution, local database or self-hosted API. |
| `Services/NetworkMonitor.cs`, `ConnectionService.cs` | Live connection tables and throughput. |
| `Services/AppUsageService.cs`, `NetworkStatsService.cs` | Usage history and traffic attribution. |
| `Services/RuleStore.cs` | JSON persistence of every rule and setting. |
| `Services/DiagnosticLog.cs` | Always-on log with a deduplicating in-memory error buffer. |
| `Services/SignatureService.cs`, `HashService.cs` | Authenticode verification and SHA-256 tamper hashing. |

---

## 4. The WFP engine (`Services/Wfp`)

### Identity

Every object GunWall creates is tagged with its own **provider** and lives in
its own **sublayer**. This is what makes clean removal possible: the sublayer can
be deleted by key, which removes every filter inside it, even if the stored list
of filter identifiers is lost to a crash.

### Policy model — Zero-Trust default-deny

The engine adds **no filters at startup**. When Zero-Trust mode is enabled it
installs a low-weight block-all baseline plus per-app permits above it, so an
application reaches the network only once it has been approved. Loopback and
core Windows networking are permitted by infrastructure filters at the highest
weight so the machine keeps working.

With Zero-Trust off, GunWall observes without enforcing and only the explicit
rules the user created apply.

### Weights

Filter weight decides which rule wins within the sublayer — highest first, first
terminating match ends evaluation. The ordering, from the top:

| Weight | Purpose |
|--------|---------|
| `0x0F` | Infrastructure permits (loopback, core networking, GunWall itself) |
| `0x0E` | Lockdown |
| `0x0C` | Explicit user block |
| `0x0B` | Explicit user allow |
| `0x09` | Per-app rules |
| `0x08` | Zero-Trust block-all baseline |

Lockdown deliberately sits *below* infrastructure permits so that "block
everything" cannot hard-lock the local stack.

### Layer coverage

Sixteen layers are wired: outbound connect, inbound accept, listen, resource
assignment (bind), inbound and outbound transport, outbound ICMP error, and IP
forwarding — each in v4 and v6. Forwarding matters because traffic merely
*routed through* the machine, via a bridged VM or a mesh VPN peer, never reaches
the ALE layers at all.

### Verifying the kernel surface

Layer and condition identifiers cannot be validated by compiling; a wrong one
either fails silently or, worse, resolves to a different real layer and filters
the wrong thing. `WfpEngine.VerifyLayers()` therefore probes every layer and
condition by installing a filter and immediately deleting it. The probe is a
**permit** action at **weight 0** (below every real weight), **non-persistent**,
and removed within microseconds, so it cannot block traffic, weaken a block, or
survive a crash. Conditions are probed separately, because a layer probe
structurally cannot catch a bad condition identifier.

This exists because three incorrect identifiers shipped undetected for months.
Anyone touching this layer should run it (Settings → Diagnostics → *Verify
kernel layers*) and check the result.

### Fault tolerance

Optional filters go through `TryAdd`, which tolerates a layer this Windows build
does not expose. Failures are recorded to the diagnostics log, deduplicated —
previously they went only to the debugger and were invisible in release builds.

### Reactive enforcement

Entity rules (country, ASN, scope, P2P) cannot be expressed as a single static
filter, because the facts are not known until a connection appears. These are
enforced **reactively**: the sampling loop evaluates each new connection, and a
block verdict installs a persistent per-address filter and tears down the
session. Filter identifiers are stored under the owning rule so that changing
the rule removes everything it accumulated.

---

## 5. Detection

Detection is **event-driven** off the WFP kernel event stream rather than
polling, with a 1-second sampling loop for the interface, throughput, and
reactive rule evaluation. Process enumeration is cached with a short TTL, since
the naive approach called `Process.GetProcesses()` several times a second.

Each sampling step is isolated: one failing step degrades its own feature rather
than blanking the interface, and errors are counted and surfaced in the
diagnostics export.

---

## 6. Persistence

Rules and settings live in `GunWallData` beside the executable, falling back to
`%ProgramData%\GunWall` when that location is read-only. The format is plain
JSON that can be read, backed up, or deleted by hand. Versioned backups are
taken automatically and on demand.

Persistence is two-layered, and both layers matter:

- **On disk** — so the interface can show what is configured.
- **In the kernel** — filters are created persistent, so enforcement continues
  when the app is closed and across reboots.

Because the two can drift (a crash between adding a filter and saving its
identifier), removal never relies solely on the stored list: deleting the
sublayer by key clears everything GunWall ever installed.

---

## 7. DNS

The resolver is written from scratch — message parsing, caching, and forwarding
— with no DNS library. It listens on loopback and can be made the system
resolver by rewriting adapter DNS, which is VPN-aware so it cooperates with an
active tunnel.

Forwarding is either plaintext UDP or **DNS-over-HTTPS** (RFC 8484: the wire
query POSTed as `application/dns-message`). Built-in DoH endpoints are addressed
by IP, so enabling encryption needs no plaintext lookup to bootstrap itself.
When DoH fails, the default is to **fail closed** rather than silently
downgrade; plaintext fallback is opt-in and stays with the same provider.

Blocklists are applied at resolution time. The resolver also inspects the
**CNAME chain** of every answer, so a tracker cannot evade a blocklist by
aliasing from a clean first-party name; a cloaked answer is refused and never
cached. Answers additionally feed a resolved-address memory, which is what makes
"block direct connections" meaningful — a public address an application dials
without ever looking it up by name is a direct connection.

---

## 8. Metering

Per-application bandwidth has two engines:

- **Measured** — a real-time ETW session against the Microsoft-Windows-Kernel-
  Network provider, hand-parsed from the raw event records, attributing bytes to
  process identifiers. Opt-in.
- **Estimated** — measured adapter totals apportioned across applications by
  their share of active connections. Always available.

The measured engine is opt-in because it is the most delicate interop in the
project. A watchdog degrades to the estimate if the session stops producing
events while traffic is flowing, so a metering failure can never blank the usage
data, and the interface always states which engine produced the numbers.

---

## 9. Threading and the UI

The interface is a single WPF window with panels rather than pages. Long
operations — reverse DNS, VirusTotal lookups, blocklist downloads, GeoIP —
run off the UI thread and marshal back through the dispatcher. Shared state
crossing threads is lock-guarded; an early bug came from unsynchronised
dictionary writes across sampling loops.

Charts are drawn by hand onto a canvas rather than with a charting library, in
keeping with the zero-dependency rule.

---

## 10. What is intentionally not here yet

- **No service or privilege split.** GunWall is one elevated process. A
  compromise of the UI is a compromise of the firewall. Tracked for 1.0.
- **No code signing or installer.** Builds are unsigned, which is also why
  behavioural antivirus sometimes flags them.
- **No kernel-mode callout driver.** Mature user-mode WFP firewalls do not use
  one either; it would require driver signing and carry BSOD risk that
  contradicts being free, portable, and install-free.
- **No packet payload inspection.** GunWall filters connections and resolves
  names; it does not read traffic contents.
- **No secure (tamper-protected) filters or boot-time filters.** Both carry
  lockout risk and need a guaranteed recovery path first.

These are tracked in [`ROADMAP.md`](../ROADMAP.md). The WFP facade is kept
deliberately small and honest so they can be layered on without rework.

---

## 11. Dependency policy

**Zero third-party NuGet packages.** Everything uses the .NET base class library
and direct Win32 P/Invoke. This keeps the supply chain trivial to audit — a
firewall that pulls in a dozen opaque dependencies undermines its own promise.

This applies to functionality that would normally justify a library: the DNS
resolver, the ETW metering, the GeoIP lookups, the domain heuristics, and the
charts are all hand-written.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
