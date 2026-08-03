# GunWall Console — implementation spec

Design: **GunWall Console v2** (dark + light). Every number here is the literal value in the
source (`source/GunWall Console v2.dc.html`), not a re-measurement. Where a screenshot and this
file disagree, this file wins — the screenshots are 1:1 renders but PNG can't carry a token name.

Screens render at **1440 × 1000** in `screenshots/dark/` and `screenshots/light/`.

---

## 1. Window and layout

| Region | Metric |
| --- | --- |
| Export canvas | 1440 × 1000 (design is fluid; 1440 is the reference width) |
| In-app window | fluid to `max-width: 1520px`, height 960px, radius 12px, 1px `--line2` border |
| Title bar | height 38px · brand mark 13px · caption 11.5px `--t3` · window buttons 42px wide, full height |
| Close button hover | background `#d92c11`, glyph `#fff` (only place a hover changes hue) |
| Sidebar | width 238px, `border-right: 1px --line` |
| Sidebar header | padding 20px 20px 18px · logo 24px · wordmark 14.5px/600 · kicker 10.5px/0.06em uppercase |
| Nav scroll region | padding 0 12px 12px, row gap 2px |
| Nav group heading | 10px/600, letter-spacing 0.12em, uppercase, `--t3`, padding 12px 8px 7px (first) / 16px 8px 7px |
| Posture module | margin 0 12px 14px, padding 14px, radius 10px, `--panel` on 1px `--line2` |
| Top bar | height 54px, padding 0 24px 0 28px, `border-bottom: 1px --line` |
| Content padding | 30px top · 36px sides · 44px bottom |
| Content section gap | 22px (table pages) / 26–30px (dashboard, rules, security) |
| Scroll | only the nav region and the content region scroll; chrome never moves |

Grid: no column grid. Alignment comes from **flush-left text** and shared `grid-template-columns`
per table. Cards are used only where a boundary is load-bearing (chart card, posture module,
inspector, add-rule form, overlays). Everything else sits directly on `--bg`, separated by hairlines.

---

## 2. Color tokens

Two themes, same token names. Nothing outside this table appears in chrome.

| Token | Dark | Light | Role |
| --- | --- | --- | --- |
| `--desk` | `radial-gradient(120% 110% at 10% 0%, #1c1f24, #0b0c0e 60%, #060708)` | `radial-gradient(120% 110% at 10% 0%, #e6e8ec, #d4d7dc 60%, #c7cad0)` | desktop behind the window (not part of the app) |
| `--bg` | `#0a0b0d` | `#ffffff` | window ground |
| `--panel` | `#101215` | `#fafbfc` | raised surface: chart card, inspector, posture, inputs |
| `--panel2` | `#171a1e` | `#f4f5f7` | second level: overlays, form fields inside a panel |
| `--line` | `#17191d` | `#ebedf0` | hairline: row rules, chrome dividers |
| `--line2` | `#26292f` | `#dcdfe4` | stronger hairline: control borders, table header rule |
| `--hover` | `rgba(255,255,255,0.05)` | `rgba(0,0,0,0.035)` | hover / selected tint for controls and nav |
| `--row-hover` | `rgba(255,255,255,0.03)` | `rgba(0,0,0,0.025)` | table row hover (half strength of `--hover`) |
| `--t1` | `#f5f6f7` | `#0d0f12` | primary text, numbers, active nav |
| `--t2` | `#9aa1ab` | `#5a626d` | secondary text, table cells |
| `--t3` | `#646b75` | `#858d99` | metadata, labels, placeholders, axis ticks |
| `--brand` | `#ff3b21` | `#d92c11` | the accent — primary action, blocked, destructive |
| `--brand-hi` | `#ff6a55` | `#b32410` | link hover only |
| `--on-brand` | `#ffffff` | `#ffffff` | text on a brand fill |
| `--brand-bg` | `rgba(255,59,33,0.14)` | `rgba(217,44,17,0.10)` | blocked pill, selected row, error panel |
| `--ok` | `#35d07f` | `#0f8a4d` | allowed / running / live |
| `--ok-bg` | `rgba(53,208,127,0.13)` | `rgba(15,138,77,0.11)` | allowed pill |
| `--warn` | `#ffb020` | `#9a6100` | unsigned, cloaked, first-connection prompt |
| `--warn-bg` | `rgba(255,176,32,0.13)` | `rgba(154,97,0,0.11)` | warning pill and banner |
| `--neutral-bg` | `rgba(255,255,255,0.06)` | `rgba(0,0,0,0.05)` | neutral pill, switch track off |
| `--fill-up` | `rgba(245,246,247,0.07)` | `rgba(13,15,18,0.06)` | upload area under the line |
| `--fill-down` | `rgba(255,59,33,0.10)` | `rgba(217,44,17,0.09)` | download area under the line |
| `--skeleton` | `rgba(255,255,255,0.06)` | `rgba(0,0,0,0.055)` | loading bars |
| switch knob (on) | `#08130d` | `#08130d` | dark knob on the green track |

Rules that matter more than the values:

- **One accent.** Red is the only hue used decoratively. Green and amber are *state*, never
  decoration — if nothing is allowed/blocked/warning on screen, the screen is red-and-ink only.
- `--ok` / `--warn` never fill a large area. Pills, dots and 1px borders only.
- `--t3` is metadata. Nothing a user needs in order to make a decision may be `--t3`.
- Light mode is not an inversion: the accent darkens to `#d92c11`, and `--ok` / `--warn` darken
  hard (`#0f8a4d`, `#9a6100`) because the saturated dark-mode values fail on white.
- The five **category colours** on Settings (`#30d158` valid, `#ff9f0a` unsigned, `#0a84ff`
  system, `#ff453a` invalid, `#7a828c` unknown) are *user data*, not chrome. They appear only as
  the 12px swatch in that list and the 7px dot in the Applications table. Don't reuse them in UI.

---

## 3. Type

Two families, loaded as app resources — no system-font fallback in shipping builds.

- **Instrument Sans** 400 / 500 / 600 / 700 — everything.
- **JetBrains Mono** 400 / 500 — every machine value: IP, port, PID, MAC, hash, path, domain,
  byte count, timestamp, keyboard shortcut, error code.

| Role | Size | Weight | Tracking / leading |
| --- | --- | --- | --- |
| Posture statement (dashboard) | 46px | 600 | −0.035em · 1.02 |
| Page title | 34px | 600 | −0.03em · 1.0 |
| Primary stat | 30px | 600 | −0.03em · 1.0 |
| Chart stat | 26px | 600 | −0.03em · 1.0 |
| Compact stat (DNS) | 25px | 600 | −0.03em |
| Dialog title | 22px | 600 | −0.025em |
| Section subhead | 19px | 600 | −0.02em |
| Inspector title | 18px | 600 | −0.02em |
| Empty-state title | 15–17px | 600 | −0.015em |
| Lead paragraph | 13.5–14.5px | 400 | 1.55–1.6 |
| Body / nav row | 13px | 450 (nav) / 400 | 1.55 |
| Table cell, secondary body | 12.5px | 400 | 1.5 |
| Row button, small button | 12–12.5px | 500–600 | — |
| Meta, hint, mono value | 11.5px | 400 | — |
| Field label, pill | 11px | 600 | 0.09em uppercase (label) |
| Column header, mono meta | 10.5px | 600 | 0.10em uppercase |
| Nav group heading | 10px | 600 | 0.12em uppercase |
| Posture ON/OFF pill | 10px | 700 | 0.09em uppercase |

Uppercase tracking by scale: 0.09em at 11px → 0.10em at 10.5px → 0.11em section labels →
0.12em nav groups → 0.13em the brand kicker. Never uppercase anything above 12px.

**Tabular figures are mandatory** anywhere numbers stack: `font-variant-numeric: tabular-nums`
(WPF: `<Typography.NumeralAlignment>Tabular`). Stats, table columns, byte counts, ports.

---

## 4. Spacing

Not a strict 8pt grid — a 4px base with named steps. Allowed values and where they're used:

| Step | Used for |
| --- | --- |
| 2px | nav row gap; radio dot inset |
| 4px | tight label→value; badge inset |
| 6–7px | label → its field (7px); icon → tick |
| 8px | between sibling buttons; between pill and its caption |
| 9–11px | inside a control: icon → label (9–11px), checkbox → label (9px) |
| 12px | grid gutter in dense tables; sidebar side padding; card→card in a pair |
| 14px | label block → content; inside posture module; between related blocks |
| 16–18px | inside overlays; between a heading and its table |
| 20–22px | panel padding (20px); section gap on table pages (22px) |
| 24–30px | column gap between two data regions (28–34px); dashboard section gap (30px) |
| 34–44px | page padding (30/36/44); wide two-column gap (34–40px) |

Row vertical padding: **10px** (dense logs), **11px** (standard), **12px** (Applications,
Connections), **14–16px** (rows with a description). Column header padding-bottom 10–11px.

### Radii

| Radius | Applied to |
| --- | --- |
| 5px | pills |
| 6px | row-level buttons (h27–28) |
| 7px | buttons, inputs, dropdowns, nav rows, search, segmented options |
| 8px | palette items, inline banners, switch track (11px = h/2) |
| 9px | multi-line text area |
| 10px | posture module, mode-selection cards |
| 12px | panels, chart card, inspector, empty-state frame |
| 14px | overlays (palette, connection prompt) |
| 50% | status dots, radio, switch knob |

### Border weights

1px is the only hairline. **2px is reserved**: nav selection marker (2 × 16px), radio ring,
focus ring, and the `inset 2px 0 0 --brand` on a selected table row. Nothing else is 2px.

---

## 5. Components

| Component | Height | Padding | Radius | Type | Fill / border |
| --- | --- | --- | --- | --- | --- |
| Button · primary | 36 | 0 18 | 7 | 13/600 | `--brand` fill, `--on-brand` text |
| Button · secondary | 36 | 0 15 | 7 | 13/500 | transparent, 1px `--line2`, `--t1` |
| Button · toolbar | 34 | 0 14 | 7 | 12.5/500 | transparent, 1px `--line2` |
| Button · with leading icon | 34–36 | 0 15 | 7 | 12.5–13/600 | icon 14px, gap 7px |
| Button · row action | 27 | 0 13 | 6 | 12/600 | transparent, 1px `--line2` |
| Button · row action engaged | 27 | 0 13 | 6 | 12/600 | `--brand` fill (means "click to undo") |
| Button · small utility | 28–30 | 0 12–13 | 6 | 12/500 | transparent, 1px `--line2` |
| Firewall control (sidebar) | 38 | 0 8 0 12 | 8 | label 12.5/600 | `--panel2`, 1px `--line2`, switch right |
| Input / dropdown | 36 | 0 12 | 7 | 13 (12.5 mono) | `--panel` (or `--panel2` in a panel), 1px `--line2` |
| Field label | — | gap 7 to field | — | 11/600 0.09em upper | `--t3` |
| Text area | 160 | 13 15 | 9 | 11.5 mono, lh 1.8 | `--panel`, 1px `--line2` |
| Search (top bar) | 32 | 0 11 | 7 | 12.5 | `--panel`, 1px `--line2`, ⌘K chip 10.5 mono on `--panel2` |
| Switch | 21 (track 38 wide) | 2 | 11 | — | off `--neutral-bg` + 1px `--line2`; on `--ok`; knob 15px, travel 17px |
| Checkbox | 17 | — | 5 | tick stroke 3.6 | off transparent + 1px `--line2`; on `--brand` |
| Radio | 17 | — | 50% | dot 8px | 2px ring `--line2` → `--brand` when on |
| Pill / badge | — | 2.5 8 | 5 | 11/600 | see status pills below |
| Posture ON/OFF pill | — | 2 7 | 5 | 10/700 0.09em | `--ok-bg` / `--neutral-bg` |
| Nav row | 34 | 0 10 0 12 | 7 | 13, 450 → 600 active | active: `--hover` + 2 × 16 `--brand` marker at x −12 |
| Nav count / badge | 17 min-w | — | 9 | 10.5 mono / 10.5/600 | count `--t3`; alert badge `--brand` fill |
| Table column header | — | pb 10–11 | — | 10.5/600 0.10em upper | `--t3`, `border-bottom: 1px --line2` |
| Table row | — | 10–12 vertical | — | 12.5–13 | `border-bottom: 1px --line` |
| Panel / card | — | 20–24 | 12 | — | `--panel`, 1px `--line` (or `--line2` when interactive) |
| Segmented control | 28 opt (34 shell) | 0 13 | 6 (shell 8) | 12.5, 600 when on | shell 1px `--line2` + 3px pad; selected `--hover` |
| Overlay · palette | — | 16 18 / 10 8 12 | 14 | search 15 | `--panel2`, 1px `--line2`, shadow `0 40px 80px rgba(0,0,0,.55)`, backdrop `rgba(4,5,6,.62)` |
| Overlay · prompt | 580 wide | 15 20 / 22 20 20 | 14 | title 22 | as above, shadow `0 40px 90px rgba(0,0,0,.6)` |
| Empty state | — | 34–90 vertical | 12 | title 15–17/600 | 1px **dashed** `--line2` |
| Inline banner | — | 11 14 | 8 | 12.5 | `--warn-bg` + 1px `--warn` (or brand pair for error) |

### Status pills

| Label | Foreground | Background |
| --- | --- | --- |
| Allowed · Running · Cached · Established · Allow | `--ok` | `--ok-bg` |
| Blocked · Deny · Block · Invalid sig · High | `--brand` | `--brand-bg` |
| Cloaked · Medium · `2 / 71` (VT detections) | `--warn` | `--warn-bg` |
| Stopped · Clean · Info · anything else | `--t3` (weight 500) | `--neutral-bg` |

---

## 6. Interaction states

Applies to every control unless the table above says otherwise. See
`screenshots/*/16-state-gallery.png` for all of it rendered side by side.

| State | Filled (brand) | Outlined / ghost | Nav row | Table row |
| --- | --- | --- | --- | --- |
| Rest | `--brand` | transparent + 1px `--line2` | `--t2`, weight 450 | `--t2` on `--bg` |
| Hover | `filter: brightness(1.12)` (1.18 on row buttons) | background `--hover`, border unchanged | background `--hover` | background `--row-hover` |
| Pressed | `brightness(0.92)` + `translateY(0.5px)` | `--hover` + `brightness(0.92)` + `translateY(0.5px)` | same as hover | — |
| Selected | — | — | `--hover` + 2 × 16 `--brand` marker, `--t1`/600 | `--brand-bg` + `inset 2px 0 0 --brand` |
| Focus (keyboard) | `outline: 2px --brand; outline-offset: 2px` | same | same | `outline: 2px --brand; outline-offset: -2px` |
| Disabled | `opacity: 0.4`, no pointer | `opacity: 0.4`, no pointer | — | — |
| Invalid (input) | — | `border-color: --brand` + hint text in `--brand` | — | — |

Extra notes:

- Inputs get `border-color: --t3` on hover and `border-color: --brand` plus an **inner** ring
  (`outline: 2px --brand; outline-offset: -3px`) on focus, so the ring never collides with a
  neighbouring field in a dense form row.
- Focus is never suppressed. Mouse users don't see it (`:focus-visible` only).
- Hover on a whole row does **not** change any text colour — only the background.
- No hover state changes hue except the window close button.
- Hit targets: 34px minimum for anything in chrome; 27px row buttons are acceptable because the
  whole row is also clickable.

---

## 7. Motion

Nothing animates longer than 200ms. No bounce, no spring, no scale-up-from-0.
Under `prefers-reduced-motion: reduce`, all durations drop to 0ms.

| Transition | Duration | Easing | Property |
| --- | --- | --- | --- |
| Hover tint (rows, nav, ghost buttons) | 120ms | `ease` | `background-color` |
| Filled button hover / press | 120ms | `ease` | `filter`, `transform` |
| Switch knob travel | 160ms | `cubic-bezier(0.2, 0, 0, 1)` | `transform` |
| Switch track colour | 160ms | `ease` | `background-color` |
| Checkbox fill + tick | 120ms | `ease` | `background-color`, `opacity` |
| Nav selection marker grow | 150ms | `ease` | `height` 0 → 16px |
| Input border on focus | 140ms | `ease` | `border-color` |
| Page / section change | 140ms | `cubic-bezier(0.2, 0, 0, 1)` | `opacity` 0→1 + `translateY(4px→0)` |
| Overlay in (palette, prompt) | 180ms | `cubic-bezier(0.2, 0, 0, 1)` | `opacity` + `scale(0.98→1)`; backdrop fades 120ms |
| Overlay out | 90ms | `cubic-bezier(0.4, 0, 1, 1)` | `opacity` only, no scale |
| Skeleton shimmer | 1100ms | `linear`, infinite | `transform: translateX(-100% → 100%)` |
| Live chart advance | 1000ms | `linear` | one sample per tick — shift the series, never tween the path |

The live chart and counters update on a **1s tick**. Numbers snap; they don't count up.

---

## 8. Icons

24 × 24 line icons (Lucide geometry), `stroke-linecap: round`, `stroke-linejoin: round`,
no fills. Stroke weight scales with size:

| Context | Size | Stroke |
| --- | --- | --- |
| Nav row | 16 | 1.7 |
| Top bar, palette item | 15 | 1.8–1.9 |
| Inside a button | 13–14 | 2.0–2.4 |
| Chevron (dropdown) | 13 | 2.2 |
| Up/down traffic arrows | 12 | 2.6 |
| Checkbox tick | 10 | 3.6 |
| Window buttons | 10 | 1.0 |

The brand mark is the Null Cell grid: a 4 × 4 grid on a 96 × 96 box, 21px cells, 4px gutters,
two cells removed, top-right cell in `--brand`. At 24px in the sidebar, 13px in the title bar.
The dashboard watermark is the same mark at 220px, `opacity: 0.05`, bleeding off the top-right.
(It is hidden in the exported PNGs — the HTML-to-image renderer overdraws low-opacity SVG.)

---

## 9. Data visualisation

- **Traffic chart**: `viewBox="0 0 600 150"`, `preserveAspectRatio="none"`, rendered 210px tall
  (160px on Traffic). Upload = `--t1` stroke 1.3 over `--fill-up` area. Download = `--brand`
  stroke 1.3 over `--fill-down` area. Download draws under upload. No gridlines, no y-axis; the
  numbers above the chart carry the values. X labels: 10.5px mono `--t3`, `-60s … now`.
- **Sparkline** (Applications): 92 × 22 render, `viewBox="0 0 100 26"`, stroke 1.3 `--t3`, no fill.
- **Share bar** (top talkers, geography, usage): 3px tall track `--line2`, fill `--brand` when the
  value is >60% of the row maximum, else `--t3`. No radius, no gradient.
- Charts never carry a legend box — the label sits above the number with a 8 × 2px colour tick.

---

## 10. Table lifecycle states

The column header and its rule **always render**. The table never collapses to nothing, and the
content region keeps a 260px minimum height so the page doesn't jump.

| State | Treatment |
| --- | --- |
| Loading | 8 skeleton rows at normal row height. Bars 9px tall, radius 4px, `--skeleton`, widths varied 48–88% per column so it doesn't read as a grid. Below the table: 13px spinner (2px `--line2` ring, `--brand` top, 700ms linear) + `Reading kernel event buffer…` in 12px `--t3`. |
| Empty (nothing yet) | Dashed 1px `--line2`, radius 12px, 34–90px vertical padding, centred: 30–42px brand mark at 35% opacity, 15–17px/600 title, 12.5px `--t3` body ≤ 400px wide, optional 34px primary action. |
| No results (filter) | No frame. 26px vertical padding, flush left: 14px/600 title quoting the query, 12.5px `--t3` line stating what clearing restores, then a 30px `Clear filters` button. |
| Error | Solid 1px `--brand`, radius 12px, `--brand-bg`, padding 20–22px: 16px alert icon + 14px/600 title, 12.5px body naming the consequence, 11px mono error code + timestamp + retry countdown, then `Retry now` (brand fill, h30) and a secondary action. |
| Degraded / stale | 8px-radius `--warn-bg` banner with 1px `--warn`, 6px dot, 12.5px text, right-aligned 12px/600 `--warn` action. Sits inside the content column above the header rule. One banner maximum; higher severity replaces lower (error > warning > info). |

Error copy always states **what is still true** ("Filters stay loaded in the kernel — you are
still protected") before what is broken. Never show a raw exception; the mono line carries the
code for a support paste.

---

## 11. Copy rules

- Sentence case for everything except the uppercase micro-labels. No Title Case headings.
- Say the action, not the abstraction: **Turn firewall on / Turn firewall off**, not
  Enable/Disable/Toggle. The label states the action; the switch states the state; the pill
  states ON/OFF. All three agree at all times.
- Plain words over jargon where a plain word exists ("Watch system DNS lookups", not "Enable DNS
  interception hooks"). Keep the precise term when it *is* the term (WFP filter, CNAME, ASN).
- Explain scope in the body, not a tooltip: "It never changes this PC's DNS settings by itself."
- Thousands separators on counts (`48,902`), one decimal on rates (`133.9 KB/s`), space before
  the unit, `·` as the separator in meta lines.
- No exclamation marks. No "Oops". No emoji.

---

## 12. Notes for the WPF build

- Ship both palettes as two `ResourceDictionary` files keyed by the token names above and swap
  at runtime. Don't derive light from dark programmatically — the accent and status hues differ.
- Embed **Instrument Sans** and **JetBrains Mono** as application resources. Segoe UI is not a
  substitute; the whole design leans on Instrument Sans' tight −0.03em display sizes.
- Turn on tabular figures for every numeric column
  (`Typography.NumeralAlignment="Tabular"`), otherwise the ledgers wobble.
- Corner radius is 7px on controls — not the Fluent default 4px, and not 0.
- Hairlines are 1px at 100% scale. At 125%/150% DPI, snap to device pixels
  (`UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`) or the row rules will shimmer.
- The nav selection marker is a 2 × 16px rounded rect at the row's left edge, outside the row's
  own padding box — not a left border on the row.
- Scrollbars: thin, thumb only (`--line2`, `--t3` on hover), no arrow buttons, no track fill.
- Icons are Lucide geometry. Convert to `Path` data or ship the font; do not substitute Segoe
  Fluent Icons — the stroke weights won't match.
- Every table is virtualised in practice (Packet log and Services run to thousands of rows);
  keep row height fixed per table so virtualisation is cheap.

---

## 13. Screen inventory

| # | Screen | Nav group | Notes on the captured state |
| --- | --- | --- | --- |
| 01 | Overview (dashboard) | Monitor | firewall on → `Protected` posture |
| 02 | Applications | Enforce | 12 apps, all four signature colours present |
| 03 | Connections | Monitor | row 2 selected → inspector populated |
| 04 | Traffic | Monitor | range = Hour |
| 05 | DNS resolver | Enforce | resolver stopped (button reads `Start resolver`) |
| 06 | Windows services | System | — |
| 07 | Network scan | System | after a sweep; pre-scan empty state is in the gallery |
| 08 | Packet log | Monitor | streaming |
| 09 | Rules | Enforce | add-rule form + custom rules + system rules |
| 10 | Security & privacy | Enforce | — |
| 11 | Activity | Monitor | — |
| 12 | Alerts | Monitor | badge cleared on entry |
| 13 | Settings | System | Alert mode selected |
| 14 | Connection prompt | overlay | unsigned binary + 2/71 VT — the worst realistic case |
| 15 | Command palette | overlay | ⌘K, actions + go-to |
| 16 | State gallery | reference | every control × every state, 1440 × 2861 |

Missing from the screenshots on purpose: hover and pressed states (they're in the gallery, where
they can be seen side by side rather than guessed from a static frame).
