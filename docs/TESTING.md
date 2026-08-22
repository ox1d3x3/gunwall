<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../branding/png/banner-slim-dark.png">
  <img src="../branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

# Verifying a build

GunWall is a WPF application whose filtering engine runs in the Windows kernel.
Neither can be exercised by static analysis: the check suite in `tools/checks`
reads source and can prove a great deal about it, but **it cannot render a pixel
or install a filter**. Everything verified before a release is a proxy for the
thing itself.

A build actually running on Windows is therefore the only complete test, and
screenshots and diagnostics exports from it are evidence rather than courtesy.

The sections below are ordered so the cheapest, highest-yield checks come first.
If time is short, do section 1 and stop — it catches the failure modes that have
actually reached users.

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

## 3. Reporting results

**Full-window screenshots**, not crops — the sidebar and top bar are part of what
is being checked, and cropping has hidden real problems. If something looks wrong
in a small area, send the full window *and* a crop. Both themes, if the change
touched colour.

**A diagnostics export** after any release that touched sampling, WFP, DNS or
metering. Its value is the error section: zero sample-loop errors is the signal
that a threading change held.

**Say what you did.** "Switched themes, then opened Connections" is worth more
than the screenshot alone, because it establishes which state the shot shows.

**Describe what looks wrong in plain terms**, rather than translating it into a
presumed cause. "The firewall says it is off when it is on" proved more useful
than a correct diagnosis would have been — the actual fault was invisible text,
which no amount of examining the toggle would have found.

**If a check reported success and the build is still wrong, say so.** A check that
passes on a broken build is a worse problem than the bug it missed, and the
pattern is recorded in [`HANDOVER.md`](HANDOVER.md) §2.10 each time it occurs.

---

## 4. What automated checks cannot establish

The check suite reads source. It cannot run the application, so the division is
worth stating plainly:

| The checks establish | Only running the build establishes |
|---|---|
| XAML is well-formed | That it renders |
| Every `x:Name` the code touches exists | That the layout is not broken |
| Every resource key resolves | That the colours are right |
| No `StaticResource` against a swapped palette | That a theme switch repaints |
| Icon geometry parses | That an icon looks like the thing it depicts |
| Version consistent across four files | That it starts at all |

Anything in the right column asserted as fact before a build has run is an
unsupported claim. "This should work" means exactly that and nothing more.

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

## 6. Verifying the current build

Each release adds a short, specific checklist here covering what changed in it —
the standing checks above cover everything else. When a build introduces no
user-visible change, this section says so rather than inventing steps.

### 0.99.121 — the whole add-rule row

1. **Applications → double-click an app → Access rules.**
2. **Type `www.google.com` into the value box.**

   **PASS:** the text is visible as you type it.
   **FAIL:** the field looks empty or the text is cropped.

3. The three dropdowns should show their text with a small, even margin above and
   below — not a band of empty space underneath.
4. **Block / Domain / Add rule** should all be the same height and line up.

If anything is still cropped, say which control and in which direction.

### 0.99.120 — dropdowns, third attempt

1. **Applications → double-click an app → Access rules.**

   **PASS:** *Block*, *Domain* and *Allow (default)* each show in full — nothing
   cut at the right edge, and nothing cropped top or bottom.
   **FAIL:** still clipped. If so, say **which direction** — horizontally at the
   right, or vertically — because those are different causes and the answer
   decides the next step rather than another guess.

2. The three controls will be slightly taller than before. That is the fix: they
   are now the height their own template asks for.

### 0.99.119 — dropdowns sized to their content

1. **Applications → double-click an app → Access rules.**

   **PASS:** *Block*, *Domain* and *Allow (default)* all show in full, with no
   character cut off at the right edge.
   **FAIL:** any of the three is still clipped — say which, and by roughly how
   much.

2. **Settings → Appearance → interface font.** The selected entry should read
   *JetBrainsMono Nerd Font (bundled)* in full, not *(bund*.

3. **Glance at the other dropdowns while you are there** — Settings, Security,
   DNS resolver, Connections. Twelve still specify a fixed width and were not
   touched. If any shows clipped text, name it and it gets the same fix.

### 0.99.118 — the access-rules dialog

1. **Applications → double-click an app → Access rules.**
2. **PASS:** the Block/Allow and type dropdowns show their full text — no clipped
   "Block", no clipped "Continent".
3. In the value box, paste a full URL: `https://www.example.com/`
4. Choose **Block** / **Domain**, press **Add rule**.

   **PASS:** the rule is added as `www.example.com` — scheme and slash stripped.
   **FAIL:** it appears with the scheme still attached; that rule matches nothing.

5. **Add the same domain again**, typed plainly this time.

   **PASS:** "There is already a rule for www.example.com."
   **FAIL:** a second identical row appears.

6. Paste something that is not a domain, such as `1.2.3.4`. It should be refused
   with a reason rather than stored.

### 0.99.117 — exempting an app from blocklists

1. **DNS resolver → blocklist box.** Add one line naming a domain your browser
   reaches often, then **Apply blocklist**. Confirm **Watch system DNS lookups**
   is ticked.
2. Browse to it. It should be blocked — the log shows
   `Blocked domain enforced for one app: <browser> -> <address>`.
3. **Applications → double-click that browser → Blocklists** → tick
   **Do not apply DNS blocklists to this app**.

   **PASS:** a message reports how many existing filters were removed.
   **FAIL:** nothing happens, or the application stays blocked.

4. **Browse to the domain again.** It should connect.
5. **Applications list** — that row reads **Exempt** under BLOCKLISTS.
6. **Untick it.** Browse again; the block returns on the next connection.

### What should not change

7. Other applications must still be blocked from that domain — the exemption is
   per-application, not global.
8. With an adapter's DNS pointed at 127.0.0.1, the exempt application still gets
   NXDOMAIN for that name. That is correct: the resolver cannot tell which
   application asked.

---

<div align="center"><sub>Guard your network. Bismillah.</sub></div>
