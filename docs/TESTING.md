# Verifying a build

This exists because of a structural problem in how GunWall is developed: the code
is authored in a Linux container that **cannot compile WPF and cannot render a
single pixel of it**. Everything checked before a release is a proxy. The build on
X1 is the only place the software actually exists.

So the screenshots are not a courtesy. They are the test suite.

The list below is ordered so that the cheapest, highest-yield checks come first.
If time is short, do section 1 and stop — it catches the failure modes that have
actually shipped.

---

## 0. Before anything: does it build?

If the build fails, **send the first error only**, with its file and line. Not the
full log. The first error is almost always the real one and the rest are fallout,
and a wall of cascading errors buries it.

Two error codes have specific meanings here:

| Code | Almost always means |
|---|---|
| `CS0103` in a `.xaml.cs` | An element was removed or renamed in the XAML and the code-behind still refers to it |
| `MC3088` | Property-element ordering inside a `<Style>` — a setter placed after `<Style.Triggers>` |

---

## 1. The five-minute pass

These five catch the classes of bug that have genuinely shipped in this project.
Do them on every build.

### 1.1 Switch themes twice, with the app running

Dark → light → dark, without restarting.

**Watch:** the posture dot, the engine dot in the top bar, the hero dot, and the
chart's two colour ticks above the graph.

**Why:** brushes assigned in code do not follow a theme swap unless something
repaints them. This has shipped broken twice — once as frozen `StaticResource`
references, once as a code assignment that destroyed the markup binding and left
the posture state name invisible against its own card. **Anything that stays the
wrong colour after a switch is this bug.**

*Screenshot: one dark, one light, both of the Overview page.*

### 1.2 Open a table and select a row

Applications or Connections.

**Watch:** hovering gives a barely-there tint. **Selecting** gives a red-tinted row
with a red bar down the left edge. The two must look obviously different, and the
row must not change height when selected.

**Why:** hover says where the pointer is; selection says which row's rule you are
about to change. They were the same value for several releases.

*Screenshot: a table with one row selected and the pointer resting on a different row.*

### 1.3 Confirm the column headers exist

**Watch:** the uppercase header row above the rows — `PROCESS PID PROTO ...`.

**Why:** the empty-state work replaces the `ListView` template, and the header row
is drawn by a presenter that lives inside the inner ScrollViewer's style. Get that
wrong and every table renders its rows perfectly with **no headers at all** —
which looks like a layout fault and gets hunted for in the wrong place.

### 1.4 Trigger a connection prompt

Easiest way: turn the firewall on, then launch something that has never connected
before.

**Watch:** it appears at the corner, roughly 430 wide. The chevron on the left
expands to show port, reverse DNS, time and full path, and the countdown stops
while expanded.

*Screenshot: prompt collapsed, and prompt expanded.*

### 1.5 Press Ctrl+K and type

**Watch:** the search field focuses, typing filters, arrows move, Enter navigates,
Escape clears. Selected result is a red-tinted row with a left bar — not a solid
red block.

---

## 2. Per-screen sweep

Do this when a release touched layout or tables. One screenshot per screen, both
themes if the release touched colour.

For each of the thirteen screens, the questions are the same:

- Does anything read as **cut off** rather than **ended**? Truncated text must end
  in `...`, never mid-character. `AS200107 K` is a bug; `AS200107 K...` is not.
- Is any column **missing off the right edge**? Tables clip silently — there is no
  horizontal scrollbar to tell you.
- Does any **empty table** show its message, rather than a header rule over blank
  space?
- Is any **filter box** showing as a bare rectangle with no placeholder text?

Screens where something specific is worth a look:

| Screen | Look at |
|---|---|
| Overview | Chart height — should be a band, not filling the window |
| Connections | Six columns, and `State` present in the right-hand inspector |
| Applications | Publisher and Path end in `...`; hover shows the full value |
| Packet log | `PROTO` and `DIRECTION` headers not clipped |
| Rules | Empty state message when no custom rules exist |
| Traffic | Metering banner if metering is estimated rather than ETW |
| Network scan | Empty state before a scan is run |
| Alerts | Badge count in the nav matches the list |

---

## 3. What to send, and in what form

**Screenshots.** Full window, not cropped — the sidebar and top bar are part of
what is being checked, and cropping has hidden real problems. If something looks
wrong in a small area, send the full window *and* a crop.

**Diagnostics export** after any release that touched sampling, WFP, DNS or
metering. Its value is the error section: zero sample-loop errors is the signal
that a threading change held.

**Say what you did.** "Switched themes, then opened Connections" is worth more
than the screenshot alone, because it says which state the shot is showing.

**Say what looks wrong in your own words.** Do not translate it into what you
think the cause is. "The firewall says it is off when it is on" was a more useful
report than a correct diagnosis would have been — the actual cause was invisible
text, which no amount of looking at the toggle would have found.

---

## 4. What I cannot check, and therefore what only you can

Stated plainly so the division is clear:

| I can verify | Only the build can |
|---|---|
| XAML is well-formed | That it renders |
| Every `x:Name` the code touches exists | That the layout is not broken |
| Every resource key resolves | That the colours are right |
| No `StaticResource` against a swapped palette | That a theme switch repaints |
| Icon geometry parses | That an icon looks like the thing it depicts |
| Version consistent across four files | That it runs |

Anything in the right column that I state as fact is a claim I cannot support.
When I say "this should work", read it as exactly that.

---

## 5. The standing checklist

Copy this. It does not change between builds. Section 6 is the part that does.

### Every build — 60 seconds

- [ ] It compiles. If not: **first error only**, with file and line.
- [ ] It starts elevated, tray icon appears, window opens.
- [ ] Posture module reads **Protected**, engine dot in the top bar is green.
- [ ] Upload/download counters are moving.

If any of these fail, stop and report. Nothing below is meaningful yet.

### Every build — 5 minutes

These catch the classes of bug that have actually shipped here. Not one of them
is hypothetical.

- [ ] **Themes, twice, without restarting.** Dark → light → dark. Watch every
      status dot, chip and chart line. Anything that goes grey, black or invisible
      is a `DynamicResource` that got frozen.
- [ ] **Open a table, hover a row, then select it.** Hover and selection must look
      different from each other, and text must stay readable in both.
- [ ] **Column headers.** Every table has them, in both themes.
- [ ] **A connection prompt.** Trigger one. Nothing overlapping, nothing clipped.
- [ ] **Ctrl+K.** Palette opens, typing filters, Escape closes.
- [ ] **Resize the window** from wide to near-minimum. Nothing clips off the right
      edge, no column leaves a gap. These tables have **no horizontal scrollbar**,
      so anything lost is lost silently.

### Every build — the two states that are easy to skip

Both were shipped repeatedly without ever being looked at.

- [ ] **Empty.** Rules with no custom rules: frame **dashed**, headers still above it.
- [ ] **No results.** Type nonsense into any filter box. Must look **clearly
      different** from empty. That distinction is why the table lifecycle is a
      state machine and not a trigger.

### When the interface font changes

- [ ] Settings → interface font → **Instrument Sans**. Look at a column header.
      Letter-spacing should **return** and still not clip. The monospace guard is
      meant to be selective, not simply off.

---

## 6. This build — 0.99.99

Both changes are about the DNS blocklist. **Watch system DNS lookups** must be ON
(DNS resolver page) for the first half to do anything.

### A. The allow level (2 minutes)

1. **DNS resolver → blocklist box.** Add two lines:

   ```
   example-tracker.com
   @@ssl.gstatic.com
   ```
2. Press **Apply blocklist**.
3. The status line under the box should now say
   `... domains blocked, 1 explicitly allowed.`
4. Browse normally. Anything relying on `ssl.gstatic.com` must keep working even
   though the ads/trackers preset also lists names under it.
5. Remove the `@@` line, apply again — the count returns to zero allowed.

### B. The address-layer refinement

6. Leave the ads/trackers preset on and browse for ten minutes.
7. **Alerts screen** — look for entries titled *"X was not blocked by address"*.
   Each should name what it would have cut off, e.g. *"also serves github.com,
   which you have not blocked."*
8. **Export diagnostics.** Lines to expect:
   - `Address <ip> serves only blocked names (...) - blocking the address.`
     → a genuine tracker host, blocked again. This is the enforcement that came back.
   - `... also serves <name> - not on your blocklist, so the address is left alone.`
     → collateral avoided.
9. **Confirm Kaspersky and GitHub still work.** They are the two that broke before,
   and the whole point of the veto is that they cannot break this way again.

## 7. What to send

Full-window screenshots, both themes if the change touches colour. For a layout
bug, the whole window — a crop hides what the element is being measured against.

Describe what you **see**, not what you think causes it. "The firewall says it is
off when it is on" led to a bug that turned out to be invisible text; no amount of
looking at the toggle would have found it.

If a check reported success and the build is still wrong, say so plainly. A check
that passes on a broken build is a worse problem than the bug, and it is recorded
in `HANDOVER.md` §2.10 when it happens.
