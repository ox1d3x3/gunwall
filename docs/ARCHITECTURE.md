# NetGuard Pro — Architecture

This document describes how the **v0.1** release is actually built. It reflects
the code in this repository, not an aspirational design. Where the long-term
plan differs from what ships today, that is called out explicitly.

---

## 1. Design goals

1. **Build cleanly into a single EXE** from Visual Studio with no exotic SDKs.
2. **Never silently touch the network.** Every filter that exists corresponds
   to an action the user took. Default policy is *allow*, not *block*.
3. **Survive restarts.** Rules are persistent at the OS level and on disk.
4. **No telemetry, no accounts, no cloud.** Everything stays on the machine.

To meet goal #1 the v0.1 release uses **WPF on .NET 8** (`net8.0-windows`)
rather than WinUI 3. WPF compiles to a clean native-host EXE without MSIX
packaging or the Windows App SDK, which makes "open solution → Build → run the
EXE" reliable. The richer WinUI 3 shell remains a future option (see Roadmap in
the README).

---

## 2. High-level structure

```
┌──────────────────────────────────────────────────────────┐
│                     WPF UI (MainWindow)                    │
│   Dashboard  •  Firewall (per-app)  •  Connections (live)  │
└───────────────┬───────────────────────────┬──────────────┘
                │                           │
        FirewallManager              NetworkMonitor / ProcessService
        (orchestration)              (read-only telemetry)
                │
     ┌──────────┴──────────┐
     │                     │
  RuleStore            WfpEngine
  (JSON on disk)       (P/Invoke → fwpuclnt.dll)
                            │
                   Windows Filtering Platform
```

The whole thing runs as **one elevated process**. There is no Windows Service
in v0.1 — the app requires administrator rights (declared in `app.manifest`
with `requireAdministrator`) because opening the WFP engine and adding filters
needs them. A background service that keeps enforcing rules while the GUI is
closed is planned but not required, because the filters themselves are
persistent (see §5).

---

## 3. Projects and folders

| Path | Responsibility |
|------|----------------|
| `App.xaml` / `App.xaml.cs` | App entry, merges the theme dictionary, global unhandled-exception trap. |
| `MainWindow.xaml(.cs)` | The shell: navigation rail, three panels, 1-second refresh timer, hand-drawn bandwidth graph, dark title bar. |
| `Themes/Controls.xaml` | Dark palette and all control styles (no third-party theme library). |
| `Models/Models.cs` | `ConnectionInfo`, `AppInfo`, `AppStatus`, `FirewallRule`. |
| `Converters/Converters.cs` | Status → brush / text converters for binding. |
| `Services/FirewallManager.cs` | The one class the UI talks to for blocking. |
| `Services/NetworkMonitor.cs` | Live connections + cumulative throughput. |
| `Services/ProcessService.cs` | PID → name/path resolution. |
| `Services/RuleStore.cs` | Persists rules to `%ProgramData%`. |
| `Services/Wfp/WfpNative.cs` | Raw P/Invoke surface for WFP. |
| `Services/Wfp/WfpEngine.cs` | Safe managed facade over WFP. |

---

## 4. The WFP engine (`Services/Wfp`)

This is the security core. Everything else is presentation.

### Identity
NetGuard Pro owns exactly **one sublayer**, keyed by a fixed GUID
(`8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00`). All of our filters live in it, which
means we can enumerate, reason about, and tear down *only* our own rules
without disturbing Windows Firewall or any other product.

### Policy model — allow-by-default (blacklist)
The engine adds **no filters at startup**. An app reaches the network freely
until the user blocks it. This is the opposite of simplewall's whitelist
default, and is a deliberate safety choice: a fresh install can never lock you
out of your own connection.

### Blocking an app
`BlockApplication(exePath)` resolves the executable to a WFP "app ID" blob via
`FwpmGetAppIdFromFileName0`, then adds **four** persistent BLOCK filters — one
on each of:

- `FWPM_LAYER_ALE_AUTH_CONNECT_V4` (outbound IPv4)
- `FWPM_LAYER_ALE_AUTH_CONNECT_V6` (outbound IPv6)
- `FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4` (inbound IPv4)
- `FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6` (inbound IPv6)

Each filter has a single condition: `FWPM_CONDITION_ALE_APP_ID == <app id>`,
action `FWP_ACTION_BLOCK`, weight `10`. The four filter IDs are returned to the
caller and stored so the block can be reversed precisely.

### Lockdown
`EngageLockdown()` adds four **condition-less** BLOCK filters (one per layer) at
weight `15`. With no condition they match everything, and the higher weight
means they win over any per-app rule — an instant "block all network" panic
switch. Disabling lockdown removes exactly those filter IDs.

### Removal
- `RemoveFilters(ids)` deletes specific filters by ID (used to unblock an app or
  lift lockdown). `FWP_E_FILTER_NOT_FOUND` is treated as success.
- `RemoveAllFiltering()` deletes the whole sublayer (cascading every filter we
  own) and recreates an empty one so the engine stays usable. This is the
  "remove all NetGuard rules" reset.

### Persistence
The sublayer and every filter are created with the **persistent** flag, so they
keep enforcing after the app closes and across reboots — the same behaviour
simplewall relies on. This is why a service is not strictly required for v0.1.

### Error handling
`WfpEngine` never returns silent failure: any non-zero WFP status raises a
`WfpException` carrying the hex code, *except* for the benign idempotent cases
that are explicitly tolerated (`FWP_E_ALREADY_EXISTS`,
`FWP_E_FILTER_NOT_FOUND`, `FWP_E_SUBLAYER_NOT_FOUND`).

> **Interop caveat.** The P/Invoke structs in `WfpNative.cs` collapse several
> WFP unions to their largest member. This is the single most likely place to
> need a small adjustment when you first compile and run on real hardware. If a
> filter add fails with an unexpected code, that struct layout is where to look.

---

## 5. Persistence on disk (`RuleStore`)

Independently of the OS-level persistent filters, NetGuard Pro records its own
intent as JSON at:

```
%ProgramData%\NetGuardPro\rules.json
```

`StoreData` holds the list of `FirewallRule`s, whether lockdown is engaged, and
the lockdown filter IDs. Writes are **atomic** (write to a temp file, then
replace) so a crash mid-save cannot corrupt the rule set. On startup the
manager reads this file to rebuild the UI's view of what is blocked. The OS
filters are the source of truth for *enforcement*; this file is the source of
truth for *display and reconciliation*.

Nothing else is written anywhere. No logs leave the machine.

---

## 6. Telemetry, read-only (`NetworkMonitor`, `ProcessService`)

These services only ever **read**:

- `NetworkMonitor` calls `GetExtendedTcpTable` (`TCP_TABLE_OWNER_PID_ALL`) for
  IPv4 and IPv6 to list active connections with their owning PID, and uses the
  managed `NetworkInterface` API for cumulative bytes sent/received (the source
  for the dashboard graph).
- `ProcessService` maps PIDs to process names and image paths so the
  Connections and Firewall views can show friendly names.

Ports from the TCP table are byte-swapped (`NetworkToHostOrder`) because the API
returns them in network order.

---

## 7. Threading & UI

The shell runs a single `DispatcherTimer` at 1-second cadence to refresh
connections and throughput. All WFP and table calls are quick, synchronous
P/Invoke and run on the UI dispatcher in v0.1; moving the polling onto a
background task is a natural early improvement. The bandwidth graph is drawn by
hand onto a WPF `Canvas` (no charting dependency), and the title bar is darkened
via `DwmSetWindowAttribute`.

---

## 8. What is intentionally *not* here yet

- No Windows Service / driver of our own (we use the OS WFP, persistently).
- No outbound-connection *prompts* ("App X wants to connect — Allow/Block?").
  v0.1 is block-on-demand, not prompt-on-first-connect.
- No per-rule scoping (remote address, port, direction-only). Today a block is
  all-or-nothing per executable.
- No packet inspection or DNS filtering.

These are tracked in the roadmap. The architecture deliberately keeps the WFP
facade small and honest so these can be layered on without rework.

---

## 9. Dependency policy

**Zero third-party NuGet packages.** Everything uses the .NET base class library
and direct Win32 P/Invoke. This keeps the supply chain trivial to audit — a
firewall that pulls in a dozen opaque dependencies undermines its own promise.
