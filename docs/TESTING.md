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

## 5. Specific things worth checking in the current build

This section is rewritten each time a build goes out, and deliberately names no
version: check it against the top entry of [`CHANGELOG.md`](../CHANGELOG.md),
which is the authority for what actually changed.

**The throughput chart, in both themes.** Both lines should now sit on a soft
flat tint — a faint red wash under download, a faint ink wash under upload — with
**both lines the same weight**. Previously only download had a fill, it was a
gradient starting near 60% opacity, and download was drawn noticeably thicker.

Worth looking at on a busy moment rather than an idle one: the fills only read
properly when there is something to fill under. If either wash looks like a solid
block rather than a tint, the palette token is being applied at full strength and
that is the thing to report.

*Send: Overview in both themes, ideally while traffic is moving.*
