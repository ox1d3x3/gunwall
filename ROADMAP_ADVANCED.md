# GunWall — Advanced Feature Roadmap

This document is a **research-derived roadmap**. It catalogs the capabilities found in mature,
modern open-source application firewalls and maps **each one** to how GunWall would implement it
**natively** in its own architecture: **WFP (user-mode) · .NET 8 / C# · single elevated portable
EXE · zero NuGet dependencies · JSON storage · MIT**.

> **On independence & licensing.** Feature *ideas* and *capabilities* are not copyrightable — only
> specific code is. Everything below is a description of **what to build and how GunWall would build
> it independently**, never a port of anyone else's source. We do not copy third-party code; GunWall
> stays MIT.

---

## 0. Read this first — GunWall's architectural boundaries

The reference designs lean on three things GunWall deliberately does **not** have. Being honest about
this up front keeps the roadmap realistic:

1. **No kernel driver.** Reference firewalls install a kernel callout/driver and inspect **every raw
   packet**. GunWall is user-mode WFP: it sees and controls **connections** (at the ALE layers) and
   transport-layer flows, but it **cannot inspect packet payloads**. → Pure deep-packet-inspection
   features (TLS SNI sniffing, payload signatures) are **out of scope** unless GunWall ever ships a
   driver (a different product). Most filtering value does **not** need this.
2. **No DNS server (yet).** Domain-based filtering in reference designs works because the firewall
   **is** the system's DNS resolver. GunWall currently only *selects* a system resolver. → A whole
   cluster of high-value features (domain rules, CNAME-cloaking defense, DGA heuristics, "block DNS
   bypass") is unlocked by **one foundational build: a local DNS resolver/proxy** (Phase 3). This is
   the single highest-leverage investment in this document.
3. **Zero external dependencies.** Reference designs embed SQLite, bloom-filter libs, GeoIP libs,
   etc. GunWall keeps a zero-NuGet footprint. → Where those help (history DB, fast list lookup,
   GeoIP), we implement a **small purpose-built equivalent in C#** rather than taking a dependency.
   Each such case is flagged below.

**Explicitly out of scope** (don't fit a free, local, single-EXE firewall):
- A hosted multi-hop **privacy network / onion routing** (requires cloud infrastructure & a business).
- **Split tunneling** (only meaningful alongside a VPN/network GunWall doesn't provide).
- **Driver-level DPI** (see boundary #1).

Everything else is on the table. The rest of this document is the plan.

---

## 1. THE flagship upgrade — an entity-based rule engine

Today GunWall's custom rules match on **IP / CIDR / port / protocol / direction**. The biggest single
leap is to generalize a rule's *target* into a small set of **entity types**, each `+ allow` or
`- block`, evaluated as an ordered list with a final default action. This one change is the backbone
that many later features plug into.

**Entity types to support (in priority order):**

| Entity | Example | How GunWall matches it (user-mode WFP) | Needs |
|---|---|---|---|
| IP | `- 1.2.3.4` | existing WFP condition | — (have it) |
| IP range / CIDR | `- 10.0.0.0/8` | existing WFP condition | — (have it) |
| **Network scope** | `- scope:internet` | classify remote at connect-time (loopback / private RFC1918 / link-local / public) and block via dynamic filter | small classifier (§2) |
| **Country** | `- country:RU` | GeoIP lookup of remote IP → block in the connect-event handler | GeoIP DB (§4) |
| **Continent** | `- continent:AS` | GeoIP region rollup | GeoIP DB (§4) |
| **ASN** | `- asn:AS13335` | GeoIP/ASN DB lookup of remote IP | ASN DB (§4) |
| **Domain** | `- domain:*.ads.example` | matched at the **DNS resolver** when the name is resolved; the resolved IPs are then short-lived allow/block filters | DNS resolver (§3) |
| **Filter-list member** | `- lists:ads,malware` | membership test against loaded lists | list engine (§5) |
| Any | `- any` | final catch-all | — |

**Per-app rule lists with presets.** Each app (and a global default) gets an **ordered incoming list**
and **outgoing list**, ending in a default verdict. Ship the same handy presets a power user expects:
*Allow SSH*, *Allow HTTP/S*, *Allow RDP*, *Allow all from LAN*, *Allow all from Internet*,
*Block everything else*. GunWall already has per-app allow/block and a system-rule catalog — this
extends both into ordered, entity-aware lists.

**Effort:** large (it's the foundation). **Risk:** medium — evaluation happens in GunWall's existing
event-driven connect handler, so a wrong rule blocks/allows a connection but can't crash the engine.
**Payoff:** unlocks §2, §4, §5 and most of the "control" story.

---

## 2. Network scopes (fast win, no new infrastructure)

A per-app set of **force-block toggles** by where the traffic goes — the single most loved everyday
feature, and it maps cleanly onto WFP with just an IP classifier (no GeoIP, no DNS):

- **Block device-local** (loopback / `127.0.0.0/8`, `::1`).
- **Block LAN** (RFC1918 `10/8`, `172.16/12`, `192.168/16`, link-local, multicast).
- **Block Internet** (anything public).
- **Block P2P / direct** (connections to non-resolved IPs — i.e. an app dialing a raw IP it never
  looked up via DNS; a strong signal of P2P/telemetry). *Note: the "was this IP resolved via DNS?"*
  *test needs the resolver from §3; until then, approximate as "direct-to-public-IP".*
- **Block incoming** (per app; complements the existing global block-inbound).

**How:** a tiny `IPScopeClassifier` in C# (pure address-range logic) returns the scope of a remote
endpoint; the per-app scope toggles become block filters / connect-time verdicts. **Effort:** small–
medium. **Risk:** low. **Payoff:** high.

> **Status — complete (v0.41.0 → v0.80.0):** all per-app scopes ship (right-click an app →
> *Block by network scope*), enforced with app-ID + remote-range WFP filters through the
> fault-tolerant path, fully removable:
> - ✅ **Block device-local**, **Block LAN**, **Block incoming** *(v0.41.0)*
> - ✅ **Block Internet (LAN only)** *(v0.76.0)* — 46 IPv4 CIDRs covering exactly the public
>   address space, derived programmatically and verified disjoint from the local/LAN ranges,
>   plus `2000::/3` for all routable IPv6.
> - ✅ **Block P2P / direct** *(v0.76.0)* — reactive, using the §3 resolver's resolved-address
>   memory: a public address the app never looked up by name is a direct connection. Blocked
>   per-address with the session torn down; requires the resolver to be running.
> - ✅ **Block server / listening sockets** *(v0.80.0)* — denies the app any listening port via
>   `ALE_RESOURCE_ASSIGNMENT` (bind, TCP *and* UDP), `ALE_AUTH_LISTEN`, and `ALE_AUTH_RECV_ACCEPT`.
>
> The pure `IpScopeClassifier` described above ships in `Services/AppRuleEngine.cs` and is
> unit-tested offline; it also backs the `scope:` entity in the §1 rule engine.

---

## 3. The DNS cluster — a local Secure-DNS resolver (the keystone)

This is the **foundational build** that unlocks domain-level everything. GunWall adds a small local
DNS resolver listening on `127.0.0.1:53` (and steering system DNS to it via WFP redirect / setting the
adapter resolver), forwarding upstream over **DoH/DoT**.

**3a. Secure DNS (DoH / DoT).**
- Encrypted upstream resolvers with presets (the well-known privacy resolvers) + custom URL, fallback,
  and retry. GunWall already lets users *pick* AdGuard/Quad9 as the system resolver; this makes GunWall
  the resolver so it can **filter** and **see domains**.
- DNS **cache**, ignore mDNS, "secure protocols only", and **block unofficial/unknown TLDs**.
- **Ignore system/network-pushed DNS** (don't honor DHCP-provided resolvers) — anti-hijack.

**3b. Domain rules & filtering** (now possible because GunWall resolves names):
- **Domain entity rules** (`+/- domain:...` with wildcards) from §1.
- **Block domain aliases (CNAME-cloaking defense):** follow the CNAME chain and apply list/rule
  verdicts to every alias, not just the leaf — defeats first-party tracker cloaking.
- **Block subdomains of filter-list entries:** if `example.com` is on a list, block `x.example.com`.

**3c. DNS-based hardening:**
- **Block DNS bypass:** block plaintext `:53` and known DoH endpoints from *other* apps so everything
  goes through GunWall's resolver (WFP block on port 53 + a DoH-IP list).
- **Reject vs. drop:** option to actively reject blocked connections (fast app failure) instead of
  silently dropping (the app hangs/retries). WFP supports a block that returns vs. silently drops.
- **Global/Private split-view enforcement (DNS-rebinding defense):** refuse public names that resolve
  to private IPs.

**Effort:** large (a resolver is real work) but **self-contained and dependency-free** (DNS + TLS are
in .NET). **Risk:** medium — DNS is load-bearing for the whole system, so it needs a hard
**fail-open** path (if GunWall's resolver dies, traffic must fall back to system DNS). **Payoff:** very
high — this is what turns GunWall from an IP firewall into a **privacy** firewall.

---

## 4. GeoIP — country / continent / ASN awareness

A bundled-or-downloaded **GeoIP + ASN database**, parsed in pure C# (no NuGet), giving:
- **Country / continent / ASN rules** (§1 entities) — e.g. "block this app from talking to anything
  outside my continent", "block AS-xxxxx".
- **Connection enrichment:** show the **country flag, ASN/owner** next to every connection and app
  (huge for the inspector and history).

**How:** ship a compact IP→country/ASN table (a free, redistributable dataset with proper
attribution, mirroring how GunWall already credits its blocklist data sources); binary-search the
ranges at connect time. **Effort:** medium. **Risk:** low (read-only lookup; enforcement reuses §1).
**Payoff:** high, and it makes the UI feel "pro".

---

## 5. Threat intelligence & filter lists (extends what GunWall already has)

GunWall already ships curated telemetry/ad blocklists with hosts + WFP fallback. Level it up to a real
**filter-list engine**:

- **Categorized lists** (ads, trackers, malware, etc.) toggled **per app and globally**, auto-updated
  on a schedule (GunWall already auto-updates some lists — generalize it).
- **Fast membership lookup:** for large domain lists, add a **bloom filter** (trivial to implement in
  C#, zero-dep) in front of the exact set, so per-query checks stay cheap.
- **Custom filter-list file:** let the user point at a local `.txt` that GunWall **watches and
  auto-reloads**, where each line is a **domain / IP / country / ASN** to block (and a parallel
  allow-list file). This is a big step up from today's manual IP box.
- Per-app **"which lists apply"** selection (some apps need trackers allowed, etc.).

**Effort:** medium. **Risk:** low. **Payoff:** high. Domain-list entries only bite once §3 lands; IP /
country / ASN list entries work immediately on top of §1/§4.

---

## 6. Domain heuristics (DGA / malware-domain detection)

A **heuristic flag** for algorithmically-generated domains (the kind malware uses for command-and-
control): score each resolved domain with a lightweight **lexical/entropy model** (n-gram likelihood)
and optionally block or warn on high-suspicion names. Implementable as a small pure-C# scorer.
**Effort:** medium. **Risk:** medium (false positives — ship it **off by default**, "warn" before
"block"). **Payoff:** medium-high. **Depends on §3** (needs domain visibility).

---

## 7. Network history & per-app bandwidth (persistent, searchable)

Today GunWall has an in-memory activity feed + a CSV packet log. Reference designs keep a **structured,
searchable connection history** with **per-connection byte counts**. Build GunWall's own:

- A persistent **connection record** with: process/app, **domain, country, ASN, scope**, direction,
  **verdict + reason**, **encrypted?**, **started/ended**, **bytes sent/received**, active flag.
- **Search & filter** across all of it (by app, domain, country, verdict, time range).
- **Per-app bandwidth** rollups and a **bytes-over-time chart** (GunWall already has a throughput
  graph — make it per-app).
- **Retention controls:** per-app "keep history" toggle + global auto-delete after N days / on demand.

**Zero-dep note:** rather than embedding SQLite, implement a compact **append-only record store** with
an in-memory index (or a single-file structured log) queried in C#. If a dependency ever becomes
acceptable, `Microsoft.Data.Sqlite` would be the drop-in. **Effort:** large. **Risk:** low–medium
(disk growth — bound it). **Payoff:** high; pairs with §4 enrichment.

*Byte counts:* WFP/IP-Helper expose per-flow statistics GunWall can sample; this is the source for
bandwidth without a driver.

---

## 8. Verdict model & decision caching

Reference designs attach a **reason to every verdict** and cache **"permanent verdicts"** per
connection so repeated traffic isn't re-evaluated.

- **Reason-for-every-decision:** store *why* each connection was allowed/blocked (which rule, which
  list, which scope) — surfaced in the inspector and history. GunWall has the data; make it
  first-class.
- **Decision caching:** once a connection's verdict is computed, cache it for the flow to keep the
  event handler cheap under load.
- **Default action = Allow / Block / Prompt**, per app *and* global — GunWall has allow/block + an
  approval prompt; formalize the three-way default and a "disable auto-allow" switch.

**Effort:** small–medium. **Risk:** low. **Payoff:** medium (correctness, performance, transparency).

---

## 9. Network environment awareness

A small **netenv** capability:
- **Online / Offline / Captive-portal** detection (probe a known endpoint; detect the portal-
  interception signature) with change events.
- React to network changes (GunWall already refreshes on address changes — add explicit
  online/portal state and let rules/profiles respond, e.g. "lock down on unknown networks").

**Effort:** medium. **Risk:** low. **Payoff:** medium (enables "trusted vs untrusted network"
behavior later).

---

## 10. Profiles & app matching (deepen what exists)

- **Layered settings:** a **global default profile** + **per-app overrides** with clear inheritance
  (GunWall has per-app rules; add the inheritance/override model and a global default tier).
- **Robust app fingerprinting:** match an app not just by path but by **publisher/signature + hash +
  (optionally) command-line**, so a moved or updated binary keeps its profile (GunWall already has
  signature + hash — wire them into identity).
- **Profile export / import / sync** (GunWall has profiles/backups — add granular per-profile
  export).

**Effort:** medium. **Risk:** low. **Payoff:** medium-high (UX + correctness for power users).

---

## 11. Connection inspector richness

With §3/§4 in place, the inspector and app list can show, per connection: **process, domain
(+CNAME chain), scope, country/flag, ASN/owner, encryption status, direction, verdict + reason,
bytes**. This is mostly *surfacing* data the above phases produce — high perceived value, low
incremental effort once the data exists.

---

## 12. Self-diagnostics & compatibility

A **compatibility self-check** that flags likely conflicts (other firewalls/filtering software, a
hijacked hosts file, a non-responsive DNS path) and surfaces fixes. GunWall already exports a
diagnostics bundle and detects some of this — promote it to a guided in-app **"Health check"** panel.
**Effort:** small–medium. **Risk:** low. **Payoff:** medium (support burden ↓).

---

## 13. Notifications (actionable)

Generalize today's single new-app prompt into a small **notification center**: actionable prompts
(allow/block/expand) for new apps *and* for notable blocks (a list hit, a country block), with the
existing controls (timeout, default action, sound, tray, **fullscreen-silent** already shipped). Keep
it from becoming noisy with the per-category silencing already on the roadmap.
**Effort:** medium. **Risk:** low. **Payoff:** medium.

---

## Suggested sequencing

A dependency-aware order that front-loads value and defers the big keystone until its prerequisites pay
for themselves:

1. **Network scopes (§2)** — small, beloved, no new infra. *Do this next.*
2. **Entity rule engine (§1, the IP/range/scope subset)** — the backbone; scopes slot into it.
3. **GeoIP + country/ASN rules & enrichment (§4)** — high "pro" value, low risk.
4. **Filter-list engine + custom list file (§5)** — extends existing blocklists; IP/country entries
   work immediately.
5. **Verdict reasons + decision caching (§8)** and **profile/inheritance + fingerprinting (§10)** —
   correctness & UX.
6. **The DNS resolver cluster (§3)** — the keystone; unlocks domain rules, CNAME defense, subdomain
   blocking, block-DNS-bypass, rebinding defense.
7. **DGA heuristics (§6)** — once domains are visible.
8. **Network history + per-app bandwidth (§7)** and **inspector richness (§11)**.
9. **Network environment (§9)**, **self-diagnostics (§12)**, **notification center (§13)**.

**Out of scope (by design):** hosted privacy network / onion routing, split tunneling, driver-level
DPI — documented here only so the omission is deliberate, not an oversight.

---

## At-a-glance matrix

| # | Feature | Fits user-mode WFP? | New infra needed | Effort | Risk | Value |
|---|---|---|---|---|---|---|
| 1 | Entity rule engine | ✅ | — | L | M | ★★★ |
| 2 | Network scopes | ✅ | IP classifier | S–M | L | ★★★ |
| 3 | Secure-DNS resolver cluster | ✅ (app-level) | **local DNS resolver** | L | M | ★★★ |
| 4 | GeoIP country/continent/ASN | ✅ | GeoIP/ASN DB | M | L | ★★★ |
| 5 | Filter-list engine + custom file | ✅ | bloom filter (in-house) | M | L | ★★★ |
| 6 | Domain/DGA heuristics | ✅ | depends on §3 | M | M | ★★ |
| 7 | History + per-app bandwidth | ✅ | in-house record store | L | L–M | ★★★ |
| 8 | Verdict reasons + caching | ✅ | — | S–M | L | ★★ |
| 9 | Network environment / portal | ✅ | — | M | L | ★★ |
| 10 | Profiles, inheritance, fingerprint | ✅ | — | M | L | ★★ |
| 11 | Inspector richness | ✅ | depends §3/§4 | S | L | ★★ |
| 12 | Self-diagnostics / health check | ✅ | — | S–M | L | ★★ |
| 13 | Notification center | ✅ | — | M | L | ★★ |
| — | Privacy network / onion routing | ❌ | cloud infra | — | — | out |
| — | Split tunneling | ❌ | VPN | — | — | out |
| — | Packet-payload DPI | ❌ | kernel driver | — | — | out |

---

<div align="center"><sub>Build it natively. Keep it MIT. Guard your network. Bismillah.</sub></div>
