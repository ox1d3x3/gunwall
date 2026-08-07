# Changelog

All notable changes to GunWall are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with a `0.x` pre-1.0 series.

---

## [0.99.52] — 2026-08-06

### Changed
- **Page padding is the design's 30 top / 36 sides / 44 bottom**, replacing a uniform 24 on every screen. The asymmetry is the point: the extra at the bottom stops the last row of a long table sitting flush against the window edge.
- **The dashboard chart no longer grows to fill the window.** It sat in a star-sized row, so on a tall display it stretched to around 700px — and a throughput trace does not get more readable by getting taller, it just flattens against a larger empty field. Pinned to a 230px plot area, roughly the 310px card the design draws, and the remaining height goes to Top talkers and Recent decisions, which *do* get more useful with more room.

### Documentation
- **Version numbers now mark releases, not changes.** The roadmaps were annotated version by version — "*(v0.85.0, v0.98.0)*", "Shipped through v0.32", "Phase 1 … Phase 5" — which turned a list of open work into a promise about sequencing. This project reorders freely, so that promise was never true. Both roadmaps are now grouped by **what the work touches and how risky it is**, with no version numbers in either.

  The README badge states the stage rather than the number, so a bump no longer edits that file. Four files carry the version and must agree; `CHANGELOG.md` is the only other place a version belongs. The rule is written down in `CONTRIBUTING.md` rather than left as a habit.

  **This entry did not bump the version.** It is the first application of the rule: the change is documentation only, the binary is byte-identical to the one already built for testing, and inventing a number for it would have been exactly the noise the rule exists to stop.

### Added
- **`docs/TESTING.md`** — what to check on each build and what to send back.

  This is written down for the same reason the checks moved into `tools/checks/`: the code is authored somewhere that cannot compile WPF or render a pixel of it, so every pre-release check is a proxy and the build on X1 is the only place the software actually exists. The screenshots are not a courtesy, they are the test suite — and asking for them ad hoc, differently each time, has meant the wrong thing got checked more than once.

  It includes a table of what can be verified here versus what only the build can show, stated plainly so that "this should work" is read as exactly that.

---

## [0.99.51] — 2026-08-06

### Fixed
- **Text cells trim with an ellipsis in all twelve tables.** 0.99.50 fixed this on two columns — Publisher and Path — and moved on. Fourteen more could clip, and Connections' `LOCATION` was still cutting mid-word: `AS200107 K` reads as an operator name rather than a truncated one. That is the failure this project keeps naming: the fix was correct and its scope was decided by which two examples happened to be in the screenshot.

  Declared once per table rather than per column, because most of these are `DisplayMemberBinding` — which produces a bare `TextBlock` with no template to hang a setter on. `ListView.Resources` is the correct scope specifically because resource lookup walks the **logical** tree, and a generated row's logical parent is the `ListView`; the same style inside the `ControlTemplate` would never be found.

  It reaches column headers too, which is wanted: a header too narrow for its own word now shows `PRO...` rather than silently reading as `PROT`.

### Note — the HITS column is not here, deliberately
The design's Rules table has five columns and this build has four; `HITS` is the missing one. It is not a layout gap. `FirewallRule` has no hit counter, and nothing in the WFP layer counts rule matches today — so the column would need hit counting built behind it first. Adding an empty column to match a screenshot would look like conformance and mean nothing. It belongs on the roadmap as a feature, not in a theme release.

---

## [0.99.50] — 2026-08-06

Column sizing. Three defects, one of them introduced by the release before it.

### Fixed
- **`PROTO` was rendering as `PROT`.** Uppercasing the headers in 0.99.49 made every one of them wider, and this was the column with no slack: 20px of header padding left 36px for a word that needs about 40. `DIRECTION` in the packet log was in the same position. Both widened, and a check now measures every header against its own column so this cannot recur silently. Making text wider is an obvious consequence of uppercasing it, and I did not think about it at all.
- **Long values were cut mid-character with no ellipsis.** Publisher read "Kaspersky Labs GmbH — inval" and paths stopped mid-folder. A hard cut reads as the value *ending* there; an ellipsis says it continues. Both columns now trim properly and carry the full value as a tooltip.
- **The Connections table was losing its last column entirely at ordinary window widths.** Seven fixed-width columns plus a 340px inspector overflowed anything under about 1730px, and `HorizontalScrollBarVisibility="Disabled"` means overflow is silent clipping rather than a scrollbar.

### Changed
- **`STATE` is gone from the Connections table.** The design gives that table six columns and carries state in the inspector — which this build already did, so the column was repeating what the panel beside it says, and it was the column being pushed off the edge. Redundant *and* the thing actually breaking. The remaining six were rebalanced from 1094px to 898px so the table fits alongside the inspector at normal widths.

### Note
The first attempt at this asserted and wrote nothing, because `PROTO` also exists in the packet log and the "exactly one occurrence" guard caught an edit that would have hit the wrong table. That guard has now paid for itself twice.

The version check also had a bug of its own: `"0.99.50.0".rstrip(".0")` strips a *set* of characters rather than a suffix, yielding `0.99.5`, so it reported the four files disagreeing when they did not. Latent since the check was written and invisible until the first version ending in zero — which is exactly the kind of defect a check is meant to catch rather than be.

---

## [0.99.49] — 2026-08-06

### Added
- **Empty states on all twelve tables.** A table with nothing in it rendered as a header rule over blank space, which reads as "still loading" or "something broke" — and on several of these screens an empty table is the *normal, good* condition. No rules is what a fresh install looks like. No alerts is what a quiet day looks like. The message comes from each table's `Tag`, so one template serves all twelve, and it is driven by `HasItems` rather than a count kept in code, which cannot drift out of step with what is on screen.
- **Placeholders on the four filter fields.** They were bare rectangles: a border, no label, no placeholder, no clue what typing into one would do. WPF has no placeholder property, so the template carries one, hidden by a trigger on the first keystroke. One of the four was not even using the shared style.

### Changed
- **Column headers are uppercase**, as the design has them. The source uses CSS `text-transform`, which WPF has no equivalent for, so the fifty-one strings themselves changed. They still lack the design's 0.10em tracking — WPF has no character spacing, and that remains the open typography question.

### Note — two mistakes worth recording
Both were caught before packaging, but only because of what they would have done:

- The empty-state template replaced the `ListView` template without the `GridViewScrollViewerStyleKey` style. When a `ListView` has a `GridView`, the column header row is drawn by a `GridViewHeaderRowPresenter` that lives inside *that* ScrollViewer template. Without it, every table would have rendered its rows perfectly and had **no headers at all** — which looks like a layout bug, and would have been hunted for in the layout.
- The script that applied the empty messages used `if not m: continue`, so three tables whose names I had guessed wrong were skipped in silence and reported success for the nine that matched. Rewritten to assert. A helper that quietly does less than asked is the same defect this project keeps finding in the product.

The placeholder also shipped, briefly, defaulting to visible with only a "show when empty" trigger — so it would have sat under the typed text forever, since nothing would ever have hidden it again.

### Still outstanding
Skeleton and error states are not in. They need to distinguish loading, failed and genuinely empty, which is a state machine rather than a trigger — and guessing wrong shows "nothing here" over a table that simply has not finished loading.

---

## [0.99.48] — 2026-08-06

The table system. Ten screens share three styles, so this is one change with a
wide blast radius rather than ten changes.

### Changed
- **Table rows are hairline-separated, square, and quiet.** They were rounded cards with a pixel of air between them, hover from a control-library brush, and a selection tint that was *the same value as nav hover*.

  That last part is the one that mattered. Hover is where the pointer happens to be; selection is a decision. When both are the same value you cannot tell — in a screenshot or at a glance — which row you chose, and on a firewall the selected row is the one whose rule you are about to change. Hover is now `row-hover`, deliberately half the strength of `hover`, and selection is `brand-bg` with a 2px `brand` bar on the leading edge. Two pixels is a reserved measure in this design — focus ring, nav marker, radio ring, selected row — which is what makes it read as *this one* rather than as decoration.

  The separator and the selection bar both live in `BorderThickness`, so a selected row does not change height. A row that grows on selection shifts every row beneath it, and on a live table that is also inserting rows that reads as the list jumping.
- **Column headers are 10.5/600 in `t3` on a `line2` rule, and no longer light up on hover.** The hover state promised sorting, and most of these columns do not sort — an affordance that does nothing is worse than none. `t3` is right here precisely *because* the design forbids `t3` for anything needed to make a decision: a header names the data, it is not the data.
- **Eleven tables came out of their card containers.** Tables sit on the background, separated by hairlines; cards are reserved for boundaries that are load-bearing — the chart, the posture module, the prompt. Thirty legitimate cards remain.
- Table cells take the design's 12.5px size, which the token for it had been declared for and never applied to anything.
- The posture module's state names are sentence case, matching the hero. The dashboard was showing "Monitoring only" and "Monitoring Only" simultaneously, six inches apart, for the same state.

### Removed
- `SurfaceSelected` and `SurfacePressed` from both palettes. Neither is a design token — there is no "selected surface" in section 2, selection is `brand-bg` — and both were invented aliases that the old row style was the only consumer of.

---

## [0.99.47] — 2026-08-06

### Added
- **The Lucide icon system.** Twenty geometries in `Themes/Icons.xaml`, and all thirteen sidebar icons now draw from it instead of Segoe Fluent Icons. Section 12 asks for this specifically and says not to substitute — the two families have different optical weight and a different corner language, and side by side the difference is immediate.

  Every path was taken from the rendered design source rather than recalled, for the same reason a WFP GUID is never recalled from memory: geometry that is almost right produces a picture that looks almost right, and nothing fails loudly enough to notice.

  The conversion was not one mechanical substitution. The set uses `circle`, `ellipse`, `rect` with a corner radius, `line`, `polyline` and `path`, and each maps differently: a circle needs two 180° arcs because a single arc cannot close one (start and end coincide and the sweep is undefined), and a polyline must stay **open** — closing the tick geometry would draw a triangle. All of them are stroked with `Fill="{x:Null}"`; an unfilled Lucide glyph left to fill renders as a solid blob.

  On sizing: the icons are authored on a 24-unit canvas and drawn at 17, and the `Viewbox` scales the stroke down with the shape. That is correct and worth stating because it reads as a bug — SVG does exactly the same thing, since `stroke-width` is in user units. Scaling the stroke is what matches the design; holding it constant would not.

- Six geometries that existed as inline strings — the throughput arrows, the posture padlock, the prompt chevrons — now reference the dictionary. The same shape written in two places is the same shape until someone edits one of them.

### Fixed
- **The red line under the search field.** Setting `BorderThickness="0"` did not remove it because that line is not the TextBox's border: the control library's template draws its own focus underline as a separate element in the accent colour, and `BorderThickness` does not reach it. So the field showed a brand-red bar across the bottom, inside a box that already had a border of its own — and unlike a focus ring, it was there whether focused or not.

  Replaced rather than fought: a text field is a host for text, and everything else is drawn by the `Border` wrapping it. `PART_ContentHost` is the one name the `TextBox` contract requires; omit it and editing silently stops working while the control still renders. Scoped to this one field, because focus treatment across the app is a later decision and one search box is not the place to make it.
- The search glyph was a hand-written approximation of the design's. It is the design's now.

---

## [0.99.46] — 2026-08-06

Duplication removal, mostly. Three controls were offering the same decision in
three places, and a screen where you have to check the same state twice is a
screen you stop trusting.

### Changed
- **The posture module is one row shorter and says the same thing.** It carried a role dot with the state name, an ON/OFF pill, and a separate field below holding the word "Firewall" and the switch. All three answered one question. The switch position *is* the pill, and the state name is what the pill was abbreviating — so state and control now share a line, and the sentence explaining the state sits under them.
- **The dashboard hero no longer turns the firewall on and off.** It was a second control for something the posture module already owns, three feet away, with its own label — and 0.99.45's label change made the two disagree in wording while agreeing in function, which is worse than either. What survives is Resume, shown only while snoozed, because a snooze is the one state with no other way out: the switch reads as already on and lockdown is not engaged. It is hidden in every other state rather than relabelled, so the button means one thing.
- **The theme control is the 30×30 icon button the design asked for.** The 52×28 sliding pill read as a setting with two equal states, which is what a switch means — but a theme is not on-or-off, and the pill was the widest object in a bar built around quiet readouts. The moon is drawn as a disc with a second disc punched out of it, so the crescent is geometry and takes the theme ink like everything else.

### Fixed
- **The search results were a solid brand-red block.** `ListBoxItem` had no style, so it took the control library's selection brush, which derives from our accent — the loudest thing the palette can produce, spent on showing which row the keyboard is on. Section 6 gives the answer: `brand-bg` fill with a 2px `brand` inset on the leading edge. The tint carries the state, the bar carries the position.
- **A red underline across the search field.** The library's TextBox template paints its own focus line in the accent, under a field that already has a border. A second border in the loudest available colour is not a focus ring.

### Note
`UpdateHero` built a four-way string for the button that is now Resume-only. I left the variable in place with a comment claiming the tray menu read it — it does not, and I had not checked before writing that. Assigned in four branches and read nowhere is `CS0219`, so it is gone. A variable kept "in case" is how dead state survives a refactor.

---

## [0.99.45] — 2026-08-06

### Changed
- **The firewall switch label states the status rather than the action**: "Firewall on" / "Firewall off". Section 11 asks for the action, and that is right for a standalone button — but this label sits beside a switch, and a switch already shows the action by which way it is thrown. "Turn firewall off" beside a switch in the on position gives two readings of one control that appear to contradict, and the reading people take is the label. Deliberate deviation, recorded.
- **The connection prompt is compact again**, 430 wide: the question, the app with its signature pill, where it is going, and two buttons. Port, reverse DNS, time and full path are behind a chevron.

  0.99.44 went the other way — the design's 580px dialog with every fact laid out — reasoning that a security question answered from a summary is answered badly. The reasoning was sound and the result was still wrong. A modal that large, arriving unannounced over whatever you were doing, does not get read more carefully; it gets dismissed faster. The detail is one press away rather than absent, which is the balance the original had.
- The chevron is a real 30×30 button whose padding is stated on its own style. The version that clipped to a dot was 34 wide against a shared style carrying thirty pixels of padding — the same trap, avoided this time by not inheriting the padding at all. It drives `Path` geometry rather than a glyph, so the flip swaps `Data`; setting `Text` on either a `Path` or a `SymbolIcon` compiles and quietly does nothing.

### Added
- **The top bar search works.** Typing filters the thirteen destinations, arrow keys move, Enter navigates, Escape clears. **Ctrl+K** focuses it, which is what the chip beside it has been advertising.

  What shipped in 0.99.43 was a styled `Border` with a tooltip explaining that search was not ready — dead chrome shaped like a control, with a keyboard shortcut printed on it that did nothing. That is the failure this project keeps naming in other people's code: a thing that looks like it works and silently does not. Actions and app/address search are still 0.99.48; navigation is what exists now, and it exists rather than being promised.

---

## [0.99.44] — 2026-08-06

### Fixed
- **The posture state name was invisible in the dark theme, and 0.99.43 caused it.** `PostureName` carried `Foreground="{DynamicResource TextPrimary}"` in markup, and `UpdateStatusBanner` *also* assigned it from `FindResource`. A locally set value does not merely bypass a DynamicResource for that assignment — it destroys the binding permanently. On a machine whose saved theme was light, the assignment resolved to near-black ink and stayed near-black forever, so switching to dark left the state name unreadable against its own card. What remained legible in that module was "Turn firewall off", which reads exactly like a firewall that *is* off. The assignment is gone; the markup binding was always sufficient.

  This deserves naming plainly: 0.99.42 added a check for precisely this freeze, and 0.99.43 then shipped it in code-behind form. The check reads XAML. This was a line of C#, so it passed.
- **Four raw colour literals in the connection prompt.** The signature verdict built brushes with `new SolidColorBrush` from `#3FB868`, `#E0A53F`, `#E25C5C` and `#7A828C` — none of them a palette value in either theme, and none following a theme change. Same family as the footer dot in 0.99.42, found because the rewrite went through the file.

### Added
- **A `binding-override` check**: code must not assign a brush property that markup already bound. It found seven, two of them in the hero and predating this migration entirely. Properties that vary with *state* rather than theme are allow-listed, and the allow-list is backed by a second assertion — that `ApplyTheme` calls the painters — because without that repaint every allow-listed element freezes at whatever theme was in force when it last ran.
- `ApplyTheme` now re-runs `UpdateStatusBanner` and `SyncLockdownButton` after swapping the palette. Role colours have to be assigned in code because they depend on state, so they are re-resolved on every swap instead of being made static.

### Changed
- **The connection prompt is rebuilt to the design's dialog.** 580 wide, radius 14, `panel2` on a `line2` hairline, a state strip carrying the kind of moment and the countdown in mono, the question at 22px, and the facts in a two-column ledger of hairline-separated rows. Machine values are JetBrains Mono and prose is Instrument Sans throughout, which is what stops a full executable path reading as a wall of text.

  It stays a separate always-on-top window rather than the design's in-window modal. That is a functional decision: GunWall lives in the tray, so a prompt has to be answerable when the main window is hidden. An overlay inside an invisible window is a prompt nobody can answer, and the fail-closed timeout would then block traffic the person was never asked about.
- **The Details toggle is gone, along with everything it hid.** It existed because a 386px window could not show address, port, path and time at once. At 580 they fit, so they are simply shown — a security question answered from a summary is a question answered badly. The button it was wired to had been clipping to a bare dot since it was written.
- Prompt actions are `PromptSecondary` and `PromptPrimary`: 36 high, radius 7, differing by weight only. Neither is green or red. A green Allow beside a red Block turns a security decision into a reflex, and the reflex people learn is to press the coloured one.

### Removed
- The `PromptButton` style. Its `Padding="15,0"` was the reason a 34px button had four pixels for a twelve-pixel glyph.

---

## [0.99.43] — 2026-08-06

Second of eight design-migration releases. The chrome: sidebar posture module,
rebuilt top bar, and the footer retired.

### Changed
- **The sidebar's protection card is now the posture module the design specifies**, docked to the bottom of the rail: role dot, state, ON/OFF pill, the sentence explaining what the state means, the firewall control, and lockdown. What it replaced was a shield glyph with a single word under it and the real sentence hidden in a tooltip — a shape dictated by a 92-pixel rail. The rail has been 238 pixels since 0.99.33. The card outlived the constraint that shaped it by ten releases, and its own comment was still explaining the reasoning: *"the rail is 92 pixels wide, so a card with an icon and two lines of text beside it could only ever clip."* Nothing had clipped in a long time; nobody had reread the comment.
- **The top bar is rebuilt to section 1**: 54 high, search box with its shortcut chip on the left, then engine state with a role dot, a hairline divider, and the two throughput readouts. Its bottom rule is `line` rather than `line2` — chrome dividers are the quieter of the two hairlines.
- **The firewall switch and lockdown button moved out of the top bar into the posture module.** They are controls that change the posture, so they belong with the posture they change; the top bar now answers what the machine is doing, which is a different question.
- The firewall control's label states what pressing it *does* rather than what state the machine is in, per section 11. The state is already on the line above it.
- The engine line and its dot are now set together. They were independent and could disagree, and a dot saying one thing beside text saying another is worse than either alone.
- The theme control stays as the animated switch rather than the design's 30×30 icon button, by explicit decision. Recorded as a deliberate deviation rather than an oversight.

### Removed
- **The persistent footer.** Once the posture module and top bar landed, three of its five readouts were duplicates: protection state appeared in both of those, the rates in the top bar, and the alert count was already the nav badge. Its two genuinely unique readouts were rehomed rather than dropped — session totals are tooltips on the top bar rates, and metering mode is now a banner on Traffic. The empty `Auto` grid row went with it: an empty row is not visually harmless, it is a row that anything given `Grid.Row="1"` lands in silently.

### Added
- **A degraded-state banner on Traffic** (section 10: `warn-bg` on a 1px `warn` hairline, 8px radius, above the header) shown only while metering is estimated rather than ETW-measured. It states a caveat about how to read the figures below it, so it lives on the screen with the figures and only while the caveat applies. A permanent label on every screen could express neither.

### Fixed
- **The connection prompt's details button has been rendering as a bare dot.** `PromptButton` carries `Padding="15,0"` — thirty pixels — and the button was `Width="34"`, leaving four pixels for a twelve-pixel chevron. It clipped to a sliver, beside Allow and Block, on the one window in this application where a misclick has consequences. Widened to 66. Behind it, a collapsed label had its two strings inverted; corrected while it was open, though nothing could ever have shown it.
- The prompt header read "App Is Blocked". Section 11 is sentence case throughout, and the title case read oddly regardless.
- **The ALLOWED statistic was green.** The design gives it `t1`: blocked-in-brand is the one sanctioned decorative use of the accent, and allowed is deliberately not its mirror. Green is a state colour and a running total is not a state. Verified against the design source rather than the rendered screenshot.

### Note
The metering banner was added as a new first row on Traffic, which left the panel's four existing children pointing at the wrong rows — two of them overlapping on the same one. Caught by counting row indices against children before packaging rather than by looking at it afterwards, which on a collapsed panel would not have shown anything.

---

## [0.99.42] — 2026-08-06

First of eight releases migrating the interface to the Claude Design handoff.
This one paints nothing new. It removes every colour that predates the design
and gives colour a single home, because each of the seven stages after it lands
on top of these tokens — fixing them afterwards would mean repainting whatever
had already been built.

### Fixed
- **The light-theme chart fix from 0.99.41 was still broken, in the elements that release did not look at.** Moving the series into the palettes was right. But six consumers referenced them with `StaticResource`, and `ApplyTheme` *replaces* the palette dictionary rather than editing it — so a static reference resolves once against whatever `App.xaml` merged, which is always the dark palette, and never moves again. The upload tick stayed near-white on a white card: the exact bug 0.99.41 was written to fix, surviving three feet away. All six bind late now. Worth stating plainly: the check that passed confirmed the palette had changed, which says nothing about what is still reading from somewhere else. That is the same shape as the chart that stayed blue in 0.99.39, and it is now a check rather than a lesson.
- **Eleven pre-design colours were still live in the shared dictionary.** A blue accent chain (`#3A86E0` with its hover, pressed, second and gradient forms), two verdict literals, and a four-colour data series — fourteen call sites between them. They survived the palette rewrite in 0.99.32 for a simple reason: that release changed the *palettes*, and these were never in the palettes. Blue appears nowhere in this design; section 2 is explicit that red is the only decoratively used hue.
- **The protection indicator was painted from three raw RGB literals — and not arbitrary ones.** They were `#FF453A`, `#30D158` and `#FF9F0A`: the *category* colours for invalid, valid and unsigned signatures. Section 2 names those specifically and says not to reuse them in the interface, and being user-editable they could have drifted to anything. Built with `new SolidColorBrush` in code, they never followed a theme change either.
- **"Monitoring Only" was painted in the blocked colour, and lockdown in the allowed one.** Five conditions were being collapsed into one boolean, so a state that blocks *nothing* wore the colour of the state that blocks *everything*, and the most restrictive state in the application read as green. The role is a three-state idea — lockdown is brand, protected is ok, anything else is warn — and `UpdateHero` has carried that mapping correctly since 0.99.35. The sidebar was contradicting it a few inches away on the same screen.
- **The light theme's row-hover value was the dark theme's**, white at 3% on a white ground. Nothing reads it yet, so nothing showed it; the table system in 0.99.45 would have. The parity check passed it because parity compares key *names* — both palettes defined the key, and only one of them defined it correctly.
- The Applications signature legend drew from a generic chart series while the dots it explains drew from `CategoryPalette`, so the legend and the table could disagree the moment anyone edited a colour. Both read the palette now. The Settings swatches deliberately still read the *typed* value and the legend the *applied* one — a half-finished hex should not repaint a legend on another page.
- The Traffic breakdown's share bar was a rounded blue bar with no track; the dashboard's was already correct. They were the same component drawn two different ways. Traffic now takes the dashboard's treatment, including the 60%-of-largest rule, so the accent marks the outlier rather than decorating every row.
- The Applications sparkline was blue; section 9 makes it `t3`. It is a shape hint, not a value.

### Added
- **The four design tokens no palette carried**: `brand-hi`, `fill-up`, `fill-down` and `skeleton`. `brand-hi` is worth noting — it *darkens* in the light theme where it lightens in dark, so a derived "hover = brighten" would invert it wrongly. It has exactly one sanctioned use, link hover, and exactly one consumer.
- **Tabular figures, which the specification calls mandatory and which appeared nowhere.** All four Instrument Sans faces carry the `tnum` feature, so the request resolves rather than silently doing nothing — verified against the font binaries rather than assumed. Set once per window root, since `Typography` attached properties inherit; the two secondary windows are separate trees and need it stated separately.
- **`tools/checks/` — the checks now live in the repository.** They have been written into `/tmp` and lost to a container reset before, taking with them the tests that caught the WFP struct offsets and the wrong layer GUIDs. A check that does not survive the session gets rewritten from memory, which is how it quietly stops testing what it was written for. Two are new: one for late binding against a swapped dictionary, one forbidding colour outside the palettes. Each exists because of a specific defect a passing check failed to catch.
- The dead-key check now carries an explicit allow-list of design tokens no stage has wired up yet, each naming the release that will consume it. Without it the check cannot tell "stale, delete it" from "not built yet", and a check that cries wolf is a check that gets ignored.

### Removed
- The `NavButton` style, which styled the Fluent rail replaced in 0.99.33 and has been referenced by nothing since. It is what the blue accent chain was still being kept alive for — a dead style holding a dead colour, each justifying the other.
- `BodyStrongFontSize`, a duplicate of `BodyFontSize`; both were 13.
- A `glow` brush assigned on every status update and never read, and the boolean the role logic replaced.

### Note
An element-reference check was written for this release and removed before it shipped. Its "looks locally declared" exclusion was wide enough to exclude everything, so it reported success by finding nothing at all — in a 6,200 line file, which should have been the tell. That is the 0.99.36 failure one level up: not a check scoped to the wrong names, but a check that could not fail. Doing it properly needs a parser rather than a regex, and the Roslyn pass already answers it, since a missing element is `CS0103`. The gap is marked in the file rather than papered over.


### Fixed
- **The upload line was invisible in the light theme.** Section 9 draws upload in the *text* colour, which inverts between themes — near-white on dark, near-black on light. The series had been placed in the shared dictionary, so it stayed near-white in both and vanished against a white card. Both series now live in the palettes, where a value that must invert belongs.
- **The connection state column clipped "Established"** at 100 pixels. Widened to fit the longest value it can hold rather than the shortest.
- The packet log page called itself "Packets Log" while the sidebar called it "Packet log" — the same page under two names. It uses the sidebar's.

---

## [0.99.40] — 2026-08-04

### Fixed
- **A resource lookup was placed in a static helper.** `FindResource` is an instance member of `FrameworkElement`, so it cannot be called from one. All twenty-one lookups now go through `Application.Current`, which reaches the same application-scoped dictionaries from anywhere — none of them was ever looking for an element-local resource, so nothing is lost and the rest can no longer break the same way if they move. A check for the pattern in static context runs alongside the others.

---

## [0.99.39] — 2026-08-04

### Fixed
- **The traffic chart was still blue and pink after being told not to be.** The previous release changed the series *resources*, which had no effect: the graph builds its brushes from colour literals in the redraw path, so the palette never reached it. That is why a change that verified clean still looked wrong on screen — the check confirmed the resource had changed, not that anything read it. The series, the baseline rule and the axis labels now resolve from the theme, and download is drawn first so it sits beneath upload as section 9 requires.
- **The update message overlapped the statistics labels.** It had been pinned to a row edge, and each time it was moved it simply collided with whatever that row held next — first the chart card, now the labels. It sits directly beneath the actions it reports on, which is the only place it has no neighbour to fight.

---

## [0.99.38] — 2026-08-04

### Fixed
- **The alert badge sat to the left of the bell.** It was the dock panel's first child with no dock set, so it took the default and docked left, appearing before the icon rather than at the end of the row. It docks right now.
- **The update message floated over the chart card.** It was pinned to the bottom of the statistics row with a negative margin; it belongs in the hero's own row, which is where it now sits.
- **The traffic chart used blue and pink**, a pair that predates the design and made the chart the only place in the interface using hues that mean nothing. Section 9 is explicit: upload is drawn in the text colour, download in the brand, and download draws underneath so the brand area reads as the larger mass. Red is the only decorative hue in this design, and the chart was the last thing disagreeing with that.

---

## [0.99.37] — 2026-08-04

### Fixed
- **The download, upload and connection cards were drawing on top of the statistics row.** Both occupied row 1 of the dashboard grid, so they overlapped rather than stacked. The design has no separate rate cards at all: those figures belong in the chart's header, because they are the chart's values — as cards above it they repeated what the graph already showed and cost a whole row to do it. They sit in the header now, each with the 8 × 2 colour tick the design uses in place of a legend box.
- **The primary action rendered as pale salmon.** It drew from the control library's accent chain, which derives a lighter, desaturated variant for dark themes — correct for chrome, wrong for a filled button. It is painted from GunWall's own brand token with white on it, which is what the design specifies, rather than hoping a derivation lands there.
- Removed a variable that was assigned five times and read never — it fed the status banner the hero replaced. The compiler had been saying so.

---

## [0.99.36] — 2026-08-04

### Fixed
- **The dashboard rebuild removed sixteen named elements the code still used**, which is twenty-eight compile errors. The replacement matched a larger block than intended and took the statistics row and status lines with the banner it was meant to replace.
  - The six figures are restored in the design's own arrangement — a divided row directly beneath the hero, separated by hairlines rather than sitting in a card, because they are a readout and not an object.
  - The shield graphic is *not* restored. The hero replaced what it did, so the code that painted it was removed rather than given elements to paint; keeping both would have meant two things claiming to state the posture.
- **The verification that missed this has been replaced.** It compared the code's references against a list of name prefixes — `Hero*`, `Nav*`, `Meta*` — so it could only find the classes of element already thought of, and sixteen names outside that list passed unnoticed. It now derives the set from the XAML itself and looks for any identifier the code accesses as an element, with no prefix assumptions.

---

## [0.99.35] — 2026-08-04

### Changed
- **The dashboard leads with posture rather than a title.** It had a generic "Dashboard" heading above a card restating the same thing in smaller type — two rows spent saying it twice. The page now opens with a kicker naming where the rules live, the state at 46px, one sentence of consequence, the actions that state is missing, and a meta column answering the questions a posture claim invites.
- **Every part of the hero is driven from real state.** The kicker, sentence and primary action differ across five conditions — no engine, lockdown, snoozed, protected, monitoring — because a hero reading "Protected" while the engine is idle would be worse than no hero at all.

### Added
- **A ruleset fingerprint**, and a real one: a SHA-256 over the things that actually change what the firewall does — mode, per-app verdicts, custom and system rules, blocked services — shown head-and-tail. Two machines showing the same fingerprint are enforcing the same policy, and one that changes when you changed nothing is worth investigating.
- **Uptime measured from when protection began**, not from process start. Those diverge the moment anyone toggles, and the honest number is the one answering "how long have I actually been covered?".
- **Top talkers and recent decisions**, side by side beneath the chart, from data the application already had: `TopAppBytes` and the packet log. The share bar turns red only when a row passes 60% of the largest, so it marks the outlier in whatever is actually happening — on a quiet machine nothing is red, on a busy one only the genuinely dominant application is. A fixed threshold would either shout constantly or never.

### Fixed
- The packet log's verdict colours were built from hardcoded literals predating the palette, so they did not follow a theme change and had drifted from the verdict colours used everywhere else. They resolve from the theme now.

---

## [0.99.34] — 2026-08-03

### Changed
- **Instrument Sans replaces Inter Tight**, in the four weights the design uses, alongside JetBrains Mono in two. Inter Tight was chosen from the earlier HTML export; the handoff names Instrument Sans and says plainly that Segoe UI is not a substitute. All six faces are static instances, because WPF renders a variable font's default instance only — a SemiBold request against a variable file returns Regular with no error to notice.
- **No system fallback**, on the spec's instruction. The reasoning is worth recording: a silent fall back to Segoe is worse than a visible failure, because the design's negative tracking at display sizes is set for these faces and Segoe at the same values looks wrong rather than merely different. A missing face should be obvious, not quietly absorbed.
- **Corner radii follow the design, not Fluent.** Controls are 7 and panels 12, where the Fluent default is 4 and 8. The spec calls this out by name, and it is one of three things it corrects that this project had already got wrong once.
- Component metrics from section 5: buttons 36 high with 18 or 15 of horizontal padding at 13/600 and 13/500, inputs 36, panel padding 22, body 13 and table cells 12.5.

---

## [0.99.33] — 2026-08-03

### Changed
- **The sidebar is rebuilt to the handoff: 238 pixels, grouped, horizontal rows.** It had been a 92-pixel strip of icon tiles, taken from an earlier reading of the design; the spec is explicit that the rail is wide, that rows read icon-then-label, and that they sit under three headings — MONITOR, ENFORCE and SYSTEM. The thirteen pages map onto those groups exactly, and the fuller names the design uses fit at this width: "Packet log", "DNS resolver", "Windows services", "Network scan".
- **The selection marker is a 2 × 16 rectangle in the gutter**, at −12, outside the row's padding box. The spec calls this out specifically: it is not a left border on the row, and drawing it as one would place it inside the rounded corner and clip. It grows from nothing over 150ms, with the hover tint at 120ms — both the spec's values.
- **The alert count is a badge rather than text.** It was being appended to the label as "Alerts (3)", which made the row's width change as the number did; in a narrow rail that pushed the label into an ellipsis. It is a fixed pill on the right now, capped at "99+".
- The sidebar header follows section 1: 20/20/18 padding, 24px mark, 14.5/600 wordmark, and the "ZERO TRUST EDGE" kicker.

---

## [0.99.32] — 2026-08-03

### Changed
- **Both palettes rewritten from the design handoff** (`docs/design/handoff/SPEC.md` section 2), which now lives in the repository alongside 32 rendered screens and a state gallery.
  - **The accent is red.** `#FF3B21` in dark, `#D92C11` in light. It is the only decoratively used hue: green and amber are *state* and never decoration, so a screen with nothing allowed, blocked or warning is red-and-ink only. Red doing double duty as the primary action *and* the blocked verdict is the design's decision rather than an oversight — in a firewall, the destructive action and the blocked outcome are the same idea.
  - **The window ground is `#0A0B0D`**, and the sidebar and content share it. Separation comes from hairlines (`#17191D`), not from stacked surfaces — the opposite of what the previous palette assumed. Panels are used only where a boundary is load-bearing.
  - The two themes are written out **separately, on the design's explicit instruction**: light is not an inversion, because the accent darkens and the status hues darken hard — the saturated dark-mode green and amber fail on white.
  - White is the foreground on the accent in **both** themes. Fluent pairs a light accent with black text in dark mode, so the library's derived value is wrong here and is replaced rather than worked around.

### Note
This supersedes much of the Fluent visual work. The library still supplies control behaviour — the window, Mica, the switch, the chrome — but the design is its own system and says so: 7px control radius rather than Fluent's 4, Lucide icon geometry rather than Segoe Fluent Icons, and no derivation of light from dark. Three of those are mistakes this project already made once and the spec names explicitly.

---

## [0.99.31] — 2026-08-03

### Added
- **The design's typefaces, embedded.** Inter Tight carries the interface and JetBrains Mono the data. Neither ships with Windows, so referencing them by name would have fallen back to Segoe UI on every machine except a designer's — they are resources in the assembly, with a system fallback after the comma so a failure to load degrades to Segoe rather than to whatever WPF picks when it cannot resolve a family at all.
  - Shipped as **static weight instances** rather than the variable files. WPF's variable-font support renders the default instance only: a SemiBold request against a variable file comes back Regular, with no error to notice. The 500 and 600 weights were instanced from the variable source and renamed into a single family so `FontWeight` resolves within it.
  - Both are OFL; the licences are in `third-party-licenses/`.
- **Monospace for machine data.** Addresses, MAC addresses and endpoints now render in JetBrains Mono with tabular figures across five columns. Every digit takes the same width, so a column of addresses lines up and a number that changes does not make the row jump. Names and descriptions stay in the interface face — monospace for prose would be an affectation, but for an IP address it is the difference between reading a column and scanning one.

---

## [0.99.30] — 2026-08-02

### Changed
- **The palette now comes from the console design, and it is a structural change rather than a recolour.** The interface previously drew the rail, the window and the content area from one value, so the whole window read as a single flat sheet no matter what else was adjusted. There are five distinct surface levels now — rail `#131417`, window `#16171A`, content `#1A1B1F`, card `#202227`, elevated `#24272D` — because depth in a dark interface comes from separating surfaces, not from adding shadows. The content column previously had no surface of its own at all; it showed whatever was behind it.
- The light theme was rebuilt on the same five-level structure inverted, so switching changes the brightness without changing how depth is expressed.
- **The accent is `#3B9DFF`**, a brighter blue than before, paired with a near-black foreground. That pairing is what makes it work: measured 6.71:1 against its own text and 6.11:1 against the content surface. Fluent already pairs a light accent with black text in dark mode, so the design and the library agree rather than fighting.
- Verdict and status colours were taken from the same specification, so allow, block and warning sit correctly on the new surfaces instead of being tuned for the old ones.

---

## [0.99.29] — 2026-08-02

### Changed
- **The theme switch carries its icon inside the knob, and animates.** A bare switch flanked by two static icons only says that two options exist; it does not say which one you are in. The moon and sun now ride inside the knob and cross over as it travels, so the control shows its current state rather than its available states. The library's `ToggleSwitch` has no slot for an icon, so this is a purpose-built template — but it follows the same motion convention as everything else: the knob travels in 167ms on a decelerating curve, and the icons swap in 80ms so the change lands *before* the knob settles rather than after, which would read as lag.

### Removed
- Three orphaned prompt-button colours. They were the muted fills of the hand-built prompt buttons; those derive from the library's template now and take its brushes, leaving the colours unreferenced — exactly the kind of leftover that gets copied later by someone assuming it is live. Every remaining literal colour in the styles is semantic: the verdict colours and the chart series, which must mean the same thing in both themes.

---

## [0.99.28] — 2026-08-02

### Fixed
- **The light theme rendered as light surfaces under white text.** The palette pointed each of its thirteen surface and text tokens at the control library's colour through a `DynamicResource`. That is tidier on paper and does not survive a theme change: switching replaces this project's dictionary while the library replaces its own at the same moment, so the two arrived out of step and the window was left half-converted. The values are copied concretely now — identical appearance, and nothing left to desynchronise. If the library's palette moves upstream, the thirteen keys to re-copy are named in the file.
- **The theme switch lost its sun and moon.** Adopting the library's `ToggleSwitch` dropped the icons the previous hand-built control carried, leaving a bare switch that said nothing about what it switched. The moon and sun sit either side of it again, so the control states its own subject rather than relying on the reader remembering which way is which.

---

## [0.99.27] — 2026-08-02

### Changed
- **The brand mark is now the wall.** Sixteen stones in four courses, three of which are not ordinary: one missing, one stopped in red, one drawn as an outline — a barrier, a block, and something under watch. It replaces the shield that preceded it, which said "security" in general where this says what GunWall actually does.
- The full brand kit lives in `branding/` — SVG source, PNG marks, lockups and app icons — so derived sizes come from artwork in the repository rather than being regenerated by guesswork.
- **The mark is applied everywhere it appears:** the application icon is rebuilt at nine sizes from the 1024-pixel master (downsampled from it rather than upscaled from something small), the window, taskbar, tray and title bar all resolve from that, and the navigation rail draws the same wall as geometry so it scales and takes the theme.
- The README opens with the horizontal lockup, with a reversed variant served to dark mode, and gains a short brand section. The previous logo file is deleted rather than left behind.

### Note
The red stone deliberately does **not** use the interface accent. In GunWall red means blocked, so the mark says the same thing the application does; making it the interaction colour would have taught two meanings for one colour, which is the confusion this palette was untangled to avoid.

---

## [0.99.26] — 2026-08-02

### Changed
- **A new application mark.** A shield with a wall course cut through its face — the two ideas the name joins, in one silhouette. It is drawn at eight times scale and downsampled into nine sizes from 16 to 256, because an icon is judged in a taskbar at 16 pixels far more often than in a folder at 256, and a thin outline that looks elegant large disappears small. The shapes are deliberately heavy for that reason.
- **The mark is now used everywhere it should be.** The window icon and taskbar come from the executable, the tray icon extracts from it, the title bar loads it through a pack URI (so it is embedded as a resource, not only stamped on the executable), and the navigation rail draws the same geometry as vector paths so it scales and takes the theme accent. The title bar previously used a stock `ShieldTask24` glyph — a symbol that happens to mean "security" rather than the mark people recognise.
- **The last legacy icon glyph is gone.** The sidebar's protection shield was a Segoe MDL2 character; it is a `SymbolIcon`. No Segoe MDL2 or Fluent-font character references remain in either window — only em-dashes, which are text.
- **Cards respond to the pointer.** The border brightens on hover. Deliberately a plain trigger rather than an animation: animating it would have to target the border brush's opacity, and that brush is a *shared* application resource — moving it would brighten every card in the window at once, and would throw outright if the brush is frozen. A `Border` style cannot introduce an overlay layer to fade instead, the way the navigation template does, so an instant state change is the honest answer rather than a broken smooth one.

---

## [0.99.25] — 2026-08-02

### Fixed
- **Cancelling the enable-protection dialog left the switch showing "on".** A `ToggleSwitch` has already moved by the time its Click event is raised, so returning without acting left the interface reporting protection that was not enabled — the one state it must never lie about. Cancelling now restores the switch from what the firewall actually reports.
- **Text was clipped in thirteen dropdowns, and the earlier diagnosis was wrong.** "Any" reading as "Anv" and "Country" losing its tail were **descenders cut off vertically**, not the horizontal clipping assumed twice before: every one carried a fixed height of 30 or 32 pixels, and the library's dropdown needs at least 33 — eight pixels of padding above and below roughly seventeen of text. The fixed heights are gone so each sizes to its content, and eleven text boxes beside them were converted to a minimum height so rows stay aligned.
- The Packets Log navigation label was truncated in a 56-pixel tile. It reads "Packets", which loses nothing — it is the only log page in the application.

---

## [0.99.24] — 2026-08-02

### Changed
- **The enable-protection confirmation is a Fluent dialog.** It was a Win32 message box — the most jarring thing left in the application, and it appeared at the single most important moment. Its wording was rewritten too: it now says what will happen rather than warning about it, and offers the Apps page as an alternative to answering prompts one at a time.
- **The prompt's badge is one mark instead of two.** A pin outline with a green "NEW" pill hung off its corner had the two shapes competing for the same space, and at that size the pill read as a stray dot rather than a label. It is a single tinted circle with the shield inside. The word itself is gone: the title already says this is first network access, so the badge was repeating it somewhere too small to read.

### Fixed
- **The rule dropdowns still clipped.** Widening them by guesswork was not enough, so the library's own measurements were read instead: its `ComboBox` spends about 49 pixels on chrome before any text — 10 and 10 of padding, 8 and 10 of chevron margin, and an 11-pixel chevron. The widths budget for that now rather than for the text alone.

---

## [0.99.23] — 2026-08-02

### Fixed
- **The protection pulse did not compile.** It used `KeyTime`, which lives in `System.Windows.Media.Animation` — a namespace this file does not import. It is now a single reversing animation instead of a key-frame sequence: fewer moving parts, and nothing outside the namespaces already in use. Worth recording why the offline check missed it: `CS0103` inside a `.xaml.cs` file is filtered, because WPF's generated field declarations produce that error when compiling away from Windows — which also hides genuinely unresolved names in exactly that file. A check for bare WPF type names without a covering `using` now runs alongside it.

---

## [0.99.22] — 2026-08-02

### Fixed
- **Four navigation tabs were off-screen.** The rail adopted the library's 60-pixel tile, which is sized for a rail of about six items; thirteen of them need 806 pixels and pushed Dashboard, Apps, Networks and Traffic above the visible area behind a scrollbar. The tile is 48 pixels now and all thirteen fit. Navigation you cannot see is worse than a tile that is not exactly to specification.
- **The connection prompt was almost invisible.** Aliasing the surfaces pointed `BgCard` at the library's card fill, which is a *translucent overlay* — correct in the main window, where it tints the Mica behind it, and useless in a transparent-background prompt where there is nothing behind it to tint. It uses the library's opaque surface now, at 96% opacity.
- **The accent stayed pale regardless of the seed.** The library takes the seed and, for a dark theme, uses a variant brightened by 17 and desaturated by 45 as the fill — so the lightening happens *after* the choice, and no seed avoids it. That is right for chrome on a dark surface but wrong for a filled button. Filled buttons are restored to the chosen accent, which is also the one measured for contrast; the lightened variant stays where the library intends it, on text and indicators.

### Added
- **The protection indicator pulses when the firewall is switched on.** Enabling protection is the most consequential action in this window and it happened with no acknowledgement at all. Only on the way on: celebrating the *removal* of protection would be the wrong signal, and motion that fires in both directions stops meaning anything.

---

## [0.99.21] — 2026-08-02

### Fixed
- **The build broke, and two toggles would have silently stopped working.** Migrating the switches changed their type but not what consumed them: a list was still declared as `List<CheckBox>`, which is the reported compile error, and *both* click handlers still matched `sender is not CheckBox`. That second one is the worse defect — it compiles perfectly, never matches, and makes every click do nothing at all. The list is typed to `ToggleButton` and both handlers match on it.

### Changed
- **GunWall's surface colours are now aliases of the library's.** This was the real reason the interface kept reading as its old self: its own surface and text tokens were referenced **115 times** across the window and control styles, against **18** references to the library's. The library was styling the controls while GunWall painted everything around them, so the two could never agree. Those thirteen tokens are now brushes whose colour points at the library's equivalent, which moves all 115 call sites without editing any of them.
  - The pointers are `DynamicResource`, not `StaticResource`, and that distinction matters: `ApplyTheme` loads the palette file standalone when switching themes, and a static alias would resolve against that lone dictionary — where the library's colours are not in scope — instead of the application. Late binding is what lets the alias survive the swap.
  - What stays GunWall's own is what carries meaning rather than surface: the allow/block/warn verdict colours and the chart series. "Allowed" has to mean the same thing in both themes, and no control library has an opinion about that.

---

## [0.99.20] — 2026-08-02

### Changed
- **Every switch in the application is now the library's own.** The four remaining hand-templated toggles — the theme switch and the ones built in code for blocklists and system rules — are `ToggleSwitch` controls. It derives from `ToggleButton`, so `IsChecked`, `Click` and `Tag` carried over untouched, and the knob animation and focus states come with it. A hand-drawn imitation of a control the library already provides has no upside; it can only drift away from the thing it imitates.

### Removed
- Six dead styles: `SlideToggle` and `ThemeSwitch` (superseded above), `SideNavIcon` (left behind when the rail moved to Fluent icons), and `TabButton`/`TabIcon`/`TabLabel` from a tab strip this window no longer has. The style dictionary is down from seventeen to eleven, none unused. Dead styles are not harmless — they get maintained, copied and reasoned about as though they were live, and this set was found by a check for definitions nothing references rather than by reading.

---

## [0.99.19] — 2026-08-01

### Added
- **The navigation rail animates its states**, on the library's own timings — and those are deliberately asymmetric: 167 milliseconds in, 80 out. Arriving gently and leaving quickly is what makes a surface feel responsive; an equal fade in both directions reads as hesitation. Hover and selection are separate layers so each can fade independently, and the selection indicator grows from nothing to its full height rather than appearing, exactly as the library does it. Opacity and height are animated rather than colours, which is both cheaper and smoother than interpolating brushes.
- **The connection prompt uses Fluent icons.** Its six glyphs were Segoe MDL2 characters set as text; they are `SymbolIcon` elements now, and no legacy icon-font reference remains in that window. The details chevron changes by setting `Symbol` rather than `Text` — a `SymbolIcon` ignores `Text`, so the previous approach would have compiled and quietly done nothing.

---

## [0.99.18] — 2026-08-01

### Fixed
- **White text on the accent button, properly this time.** The previous release retuned the seed colour, which could not have worked: Fluent *lightens* the accent for a dark theme so it stands off a dark surface, then pairs it with **black** text — `TextOnAccentFillColorPrimary` is `#000000` in dark and `#FFFFFF` in light. The button hardcoded white, so the foreground was simply the wrong half of the pair and no amount of adjusting the background would fix it. Both halves now come from the library and stay in step in either theme.
- **Dropdowns clipped their own contents** — "Any" and "Outbound" were cut off. Their widths were measured against the old dropdown template; the library's has a wider chevron well and different padding. They size to their content now, and two further dropdowns that would have clipped next were widened at the same time.

### Changed
- **The panel entrance matches the library's FadeInWithSlide.** It was translating 12 pixels with a cubic ease, against the library's 30 pixels with a deceleration ratio of 0.7 — which is why the motion felt unlike the rest of the surface. A deceleration ratio front-loads the movement so content arrives and settles; an easing function distributes it evenly and reads as a slower slide.
- Removed a `PanelTransition` style that was defined and never applied to anything — a quiet way for an interface to look animated while standing still. The entrance runs in code, because it has to fire on every navigation rather than once when an element loads.

### Performance
- The sampling loop was reviewed rather than assumed: the expensive list rebuilds are already gated on their panel being visible, and the 250 ms graph timer exits immediately when the dashboard is not shown. No change was needed, and none was made for its own sake.

---

## [0.99.17] — 2026-08-01

### Fixed
- **Services and Scan were blank.** Merging the two rules pages wrapped their content in a scroller, and both of those panels sat *between* the custom rules and the system rule library in the file — so they were swallowed into the wrapper and only rendered when Rules was selected. They are top-level panels again, with the library correctly nested inside the Rules page. The structure is now checked by walking the parsed element tree rather than by reading the markup, since this is precisely the kind of error that looks right in a diff.
- **The Alerts navigation item kept the old horizontal layout.** Its label carries a name for the unread count, which the conversion pattern did not allow for, so it alone was left with the icon beside the text. A previous check reported all thirteen as converted; that check was wrong, and it now verifies the absence of the old layout rather than the presence of the new one.
- **The protection card in the rail was clipped**, showing "M" and "W:". A card with an icon and two lines of text beside it cannot fit 92 pixels. It now reads as a shield above a single word, with the full state on hover.
- **White text on the accent was unreadable.** The accent measured 2.7:1 against white — the minimum for body text is 4.5 — which is the blending being reported. The seed is now #3A86E0, measuring 4.4:1 against white *and* 4.4:1 against the dark surface, so it carries white lettering and still stands off the background. The library derives its variants from that seed.

---

## [0.99.16] — 2026-08-01

### Changed
- **The navigation rail was rebuilt on the library's LeftFluent specification** — the one its own gallery uses. Items are now 60 by 60 tiles with a 24-pixel icon above an 11-pixel label, and the selected item is signalled twice, by a filled background *and* an accent-coloured icon, which is how the library makes selection read instantly. The previous attempt used the *standard* NavigationViewItem metrics — a 36-pixel row with the icon beside the label. Both are real parts of the library; matching the wrong one is why it did not resemble the screenshots it was meant to.
- **The rail narrowed from 218 to 92 pixels**, returning about 126 pixels to the content and letting the window material show across more of the window.
- **Custom rules and the system rule library share one Rules page**, custom first. They answer the same question — what have I told the firewall to do — and separating them made the answer take two places. A defect was fixed on the way: the library was built only when its old tab opened, so on the merged page it would have stayed permanently empty. The dashboard's system-rule figure also still pointed at the removed navigation item and would have thrown when clicked.
- **Connections is now Networks; Network is now Scan.**
- **The panel transition matches the library's FadeInWithSlide exactly**: translate from 30 with a deceleration ratio of 0.7, rather than 20 with a cubic ease. A deceleration ratio front-loads the movement, so content arrives and settles; an easing function distributes it differently and reads as a slower slide.

---

## [0.99.15] — 2026-08-01

### Changed
- **The navigation rail was rebuilt to the library's own NavigationViewItem specification** — geometry read from `NavigationLeftFluent.xaml` rather than estimated: a 4-pixel corner radius, a 2-pixel top margin, and a 3 by 24 pixel selection indicator offset into the pane gutter. It uses the library's own state brushes, so the rail is drawn from exactly the same values its real control would use.
- **All fourteen navigation glyphs are now Fluent system icons** (`ui:SymbolIcon`) rather than Segoe MDL2 characters. Every symbol was checked against the library's `SymbolRegular` enum before being used, since a wrong name compiles happily and renders nothing.

### Note on `ui:NavigationView`
The control itself was examined and deliberately not adopted. It throws unless every item names a `TargetPageType`: it navigates a `Frame` between `Page` classes, whereas this window keeps all fourteen panels inline and switches their visibility. Adopting it would mean splitting the window into fourteen pages and re-plumbing the one-second sampling loop that reaches into their elements directly — a rewrite of the application shell, not a styling change. The rail now matches its appearance exactly, which was the goal, and the swap remains available later without the look changing.

---

## [0.99.14] — 2026-08-01

### Changed
- **The main window is now a Fluent window with an application-drawn title bar.** With the system caption suppressed, the window material runs all the way to the top edge instead of stopping beneath a strip of system chrome — which is the single most visible difference between a Fluent window and a stock one. Rounded corners are requested, and the title bar carries the shield mark so the identity reads in the taskbar preview and the Alt-Tab card.
- The DWM dark-caption call is kept but re-scoped: the window no longer has a system caption, so it now only hints how the surrounding frame is composited. Its comment says so rather than leaving a future reader to work out why it is still there.

### Notes
- Close-to-tray is unaffected: the title bar's close button raises the same `Closing` event the existing handler already intercepts.
- The layout is re-nested one level (title bar row above the previous root grid) rather than rebuilt, so every named element and event handler is unchanged — verified rather than assumed.
### Fixed
- **Mica could never have appeared.** The window set `Background="{DynamicResource BgPrimary}"` as a local value. The library clears the background to transparent before applying a backdrop, but it does so through `SetCurrentValue`, which a locally set value outranks — so the request for Mica was made and silently discarded. Their own source states the rule outright: backdrop effects are not applied when the window has an opaque background. The runtime background is gone; a design-time one remains so the Visual Studio designer still shows a surface, which is how the library's own gallery handles it.

---

## [0.99.13] — 2026-08-01

### Changed
- **The control library now derives its own palette instead of having one imposed on it.** The previous attempt restated a dozen of its colour keys by hand. That was the wrong shape and it is why the result looked flat: the library *computes* its accent palette from a single seed — lighter variants for a dark theme, darker ones for a light theme — and writes around fourteen resources from it. Fixing a handful by hand left the rest at their defaults, so half the accent system was GunWall's and half was not, and the light theme was never derived at all. It is now given the seed colour and the theme and derives the whole set consistently. The two bridge dictionaries are deleted.
- **Mica is requested for the window.** It is the translucent material Windows 11 uses; the library checks whether the system can provide it and falls back on its own where it cannot.
- **GunWall's surfaces were re-based on the library's neutral (#202020 dark, #F3F3F3 light).** The navy chosen earlier cannot survive alongside it: the library's cards and controls are *translucent overlays* that tint whatever sits behind them, which is what lets a window material show through. A competing solid colour underneath would either cancel that or clash with it. This is not a retreat from having a palette — GunWall's identity lives in the accent and the status colours, and those stay.

---

## [0.99.12] — 2026-08-01

### Fixed
- **The panel transition style would not compile (MC3088).** Its `Style.Triggers` block sat between two `Setter` elements; XAML requires property elements before or after an element's content, never interleaved. Setters now come first and the trigger block last. Worth noting why the pre-release check missed it: the file is perfectly well-formed *XML*, so parsing it proved nothing about whether it is valid *XAML*. The verification now checks that ordering rule directly, across every style in the project.

---

## [0.99.11] — 2026-08-01

### Changed
- **Verified the integration against WPF UI's actual source** rather than inferring it. The findings mattered: the library auto-applies its styles to plain framework controls (`<Style BasedOn="{StaticResource DefaultButtonStyle}" TargetType="{x:Type Button}" />` with no key), so every ordinary Button, ScrollBar, ScrollViewer and ToolTip is already Fluent. Its ListView styles, however, target *its own* ListView type rather than the framework one, so this project's list styles are correctly retained instead of being redundant.
- The lists now draw from the library's own tokens — `ControlCornerRadius`, `ListViewItemBackgroundPointerOver` — so a row and the controls beside it come from one set of values and cannot drift apart. Every token depended on was confirmed present in the library source before being used.

---

## [0.99.9] — 2026-08-01

### Added
- **WPF UI (lepoco/wpfui, MIT) now supplies the Fluent control set.** Its dictionaries are merged ahead of GunWall's own, so anything this project defines still wins where it needs to. Its licence is reproduced in `third-party-licenses/`, as the MIT terms require.

### Changed
- **The dependency policy is narrower and more honest than it was.** GunWall claimed zero packages; it now has one, for the interface. The claim that was doing the actual work has been kept and stated precisely: **no third-party code runs in the filtering path.** The WFP engine, rule evaluator, DNS resolver and rule store still depend on nothing beyond the .NET base class library and Win32, so the part of this program that decides whether traffic lives or dies remains readable end to end. A control library draws buttons and has no access to any of that. Four documents claimed the old policy and all four have been updated rather than left to age.
- Styles for ComboBox, ComboBoxItem, PasswordBox, TextBox, ContextMenu, MenuItem and Separator were removed from this project so WPF UI's versions apply. Because GunWall's dictionaries merge last, leaving them in place would have silently cancelled the upgrade for exactly the controls it most improves. What stays is what carries a decision a library cannot know: the type ramp, the list metrics the fixed-width columns depend on, and every keyed style.

### Fixed
- **The two palettes would not have matched.** WPF UI ships its own dark theme built on neutral greys (#202020, #1C1C1C); GunWall's is navy. Left alone, every control the library styles — text boxes, dropdowns, menus — would have sat as a grey island on a navy panel, which reads as two applications sharing a window. A bridge dictionary restates WPF UI's own colour tokens in GunWall's palette, merged between their theme and their controls so their templates resolve our values. Their translucent control fills are deliberately left alone: an overlay tints to whatever is beneath it, so those already pick up our surfaces.
- **Switching to the light theme would have left the library dark.** The palette swap only replaced GunWall's dictionary; the bridge is now swapped with it, and both bridges are checked to define an identical set of keys.
- **Theme switching assumed the palette was the first merged dictionary** and replaced index 0 outright. That held only while GunWall's palette happened to be merged first; adding any dictionary ahead of it would have made switching themes overwrite that library instead. The palette is now located by name, so merge order is free to change.

---

## [0.99.8] — 2026-08-01

### Changed
- **List rows had the same defect as the navigation rail:** hover and selection were painted with the same colour, so a row you were pointing at looked identical to one you had chosen. They are separate surfaces now.
- **One set of control metrics.** Text boxes, dropdowns, list rows and column headers each carried their own height, corner radius and font size — four controls, four opinions. They now share a 32-pixel minimum height, a 4-pixel radius and the type ramp.
- **The dashboard figures are evenly distributed** rather than left-packed with hand-set 28-pixel gaps. Six numbers separated by arbitrary margins read as a jumble; the same six on a shared rhythm read as a summary.

---

## [0.99.7] — 2026-08-01

### Changed
- **Green was doing two unrelated jobs.** It marked the primary action *and* it meant "allowed" — the same colour on a button you should press and on a verdict you should notice, which is a large part of why the interface read as noisy. Interaction now uses a blue accent, as Fluent does; green and red are reserved for what a firewall actually decides.
- **A named type ramp replaces nine unrelated font sizes.** There were sizes of 11, 11.5, 12, 12.5, 13, 14, 16, 18 and 24 in the styles with no relationship between them, which is what makes text look placed rather than set. Body deliberately stays at 13 rather than Fluent's 14, because the lists have fixed column widths and widening the text without re-measuring every column would truncate names.
- **The navigation rail was rebuilt on Fluent NavigationView proportions.** Hover and selected were painted with the same colour, so pointing at an item looked identical to having chosen it — the one thing a navigation list must make obvious. They are separate surfaces now, the selected label is weighted, and the indicator is a short centred pill rather than a full-height bar.

---

## [0.99.6] — 2026-08-01

### Fixed
- **The connection prompt appeared too high on the screen.** Its position subtracted a hardcoded 380 pixels for the window height — a guess written when the window was that tall. After the prompt was rebuilt at roughly half that, it floated well above the corner it belonged in. The height is now measured rather than assumed, which means the placement happens once the window has actually been laid out, and again whenever it resizes so that opening Details grows it upward from the corner instead of walking it down off the screen. The shadow margin is accounted for, so the visible card sits the intended distance from the edge rather than the invisible window bounds.
- The prompt now appears on the display the pointer is on rather than always the primary one, with the placement scaled for the current DPI and clamped inside the work area.
- The blocked-app title was too long for the title line and truncated mid-word. It is shorter now, with the explanation moved to the subtitle where there is room for it.

### Changed
- **Cards are defined by their edge instead of a glow.** Every card carried a 22-pixel drop shadow, which softens the very boundary a card exists to draw — and costs a blur pass per card on a window that shows a dozen. They are flat now, with the one-pixel stroke doing the work, as Fluent does it; shadow is reserved for things that genuinely float above the page.
- Card radius came down from 14 to 8, completing the single radius scale started in 0.99.5.

---

## [0.99.5] — 2026-08-01

### Changed
- **Buttons were rebuilt to Fluent geometry.** They carried a 10-pixel corner radius on a 32-pixel body, which makes every button read as a pill — the single thing most responsible for the interface looking generic. Radius is now 4, height is fixed at 32, and padding is consistent, matching what Windows 11 itself uses.
- **One corner-radius scale across the whole application:** 4 for controls, 8 for cards and flyouts, 12 for large surfaces. Values of 9, 10, 11 and 15 had accumulated in different places, which is what makes an interface look assembled rather than designed.
- **Enabling and disabling the firewall is a switch.** It was a button that renamed itself between "Enable Firewall" and "Disable Firewall", which asks the reader to work out the current state from a verb. A switch shows the state instead of describing it, and it is labelled ON/OFF so it reads from across the room.

---

## [0.99.4] — 2026-08-01

### Changed
- **The connection prompt was rebuilt.** It had grown into a tall rounded card with a bright green primary button and five always-visible detail rows — the shape a dialog takes when nobody decides what it is for. It now asks one question in the space it needs: a ringed badge, a title, and two lines saying which application and where it is going, with the address, port, path and time behind **Details**.
- **The two action buttons are deliberately identical in colour.** A green Allow beside a red Block turns a decision into a reflex, and the reflex people learn is to click the coloured one — which in a security prompt is the wrong lesson to teach. The labels carry the meaning, the icons separate them at a glance, and dismissing the window still blocks.
- The corner radius, shadow and type scale were all pulled in. A window that interrupts someone unbidden should take as little room and attention as it can while still being answerable.
- The signature verdict moved into a chip beside the application name — one word, with the full finding on hover. The endpoint line now shows the hostname in place of the address as soon as one resolves, with the port appended, since a name is something a person can recognise or distrust and an address is not.

---

## [0.99.3] — 2026-08-01

### Changed
- **The dark theme is now a lifted navy rather than a near-black.** The previous pass went too far toward black: a monitoring tool is read for long stretches, and a raised, distinctly blue base is easier to sit with while giving the status colours somewhere to sit without glowing. The map is deliberately inverted — its landmasses are lighter than the page — so geography reads as the subject rather than the background. The light theme was retuned to match, so switching changes the brightness and not the personality.
- **The approval prompt opens compact.** It interrupts whatever the person was doing, so it now asks one short question — which application, connecting where — with **Details** revealing the address, host, port, path and time beneath it. Opening the details stops any countdown, on the reasoning that someone reading them is deciding rather than ignoring the prompt.
- The Windows notification for first network activity now names the destination rather than saying only that something is connecting, which was not enough to judge anything by.

---

## [0.99.2] — 2026-08-01

### Changed
- **Visual refresh, and a palette that can actually be changed.** The neutral ramp now carries a faint cool cast rather than being pure grey — a small shift, but it is the difference between a dark interface that reads as a decision and one that reads as a default. Both themes were rebuilt around the same structure so switching changes the brightness, not the personality.
- **The palette grew from 12 tokens to 28, matched across both themes.** Interaction states (hover, pressed, selected, focus ring), a third level of text emphasis, warning and information pills, chart and map surfaces, and four named data-series colours all have names now. Thirty colour values had been written directly into the window and controls, where no theme could reach them; fourteen are now tokens and the rest were already theme-invariant accents. The only literal colour left in the window is inside a sentence explaining hex codes to the user.
- Every referenced token is verified to resolve, and both themes are checked to define the same set, so a control can no longer end up with no brush at runtime.

---

## [0.99.1] — 2026-07-31

### Fixed
- **The 0.99.0 source did not compile.** The tamper-detection code referred to an `EngineStarted` property that had never been written — the codebase expresses the same thing as `EngineHandle != IntPtr.Zero`. The property now exists and both call sites resolve. The reason this escaped the pre-release check is worth recording: the offline compile filtered `CS0103` (unknown name) everywhere, because WPF's generated field declarations produce that error for `MainWindow.xaml.cs` when compiling away from Windows. Outside XAML code-behind there are no generated fields, so `CS0103` there is always genuine. The check now scopes that suppression to `.xaml.cs` files only.

---

## [0.99.0] — 2026-07-30

### Added
- **Tamper detection and self-healing for the firewall's filters.** Every 30 seconds GunWall asks the kernel whether each filter it installed is still there, and re-applies any that are not — raising an alert naming how many were removed. A manual check is available in Settings → Diagnostics, which also proves the recovery path by adding and deleting a throwaway filter, so the escape hatch every other safeguard depends on is demonstrated rather than assumed.
- Filter-integrity figures in the diagnostics export.

### Note on scope
The roadmap proposed protecting these objects with an access-control list. That was examined and deliberately not built: GunWall runs elevated as the user rather than as the system, so any descriptor restrictive enough to stop another administrator process would equally lock GunWall out of its own filters, with no way back. Genuine prevention needs the privilege split tracked for 1.0. What ships here is the part that can be done honestly — removal is no longer silent or lasting. A run of engine errors is deliberately never treated as tampering, so the watch cannot cry wolf and cannot trigger a needless rebuild.

---

## [0.98.0] — 2026-07-30

### Added
- **Per-service rules.** A rule written against an executable applies to everything inside it, and dozens of Windows services share a handful of `svchost.exe` processes — so blocking svchost to stop telemetry also stopped Windows Update, DHCP and time synchronisation. Services can now be blocked individually, by their own identity, from the Services page. Others in the same process are unaffected.
  - Enforcement uses the `ALE_USER_ID` condition against a security descriptor naming the service's own SID, on outbound and inbound layers for both address families.
  - The SID is derived from the service name exactly as Windows derives it — uppercase, UTF-16, SHA-1, read as five little-endian values — so it needs no privileges and no system call. It is checked in tests against Microsoft's published value for `TrustedInstaller`, because a wrong SID yields a filter that silently matches nothing rather than an error.
  - Rules are stored by service name, so they survive the service moving between host processes, and are re-asserted when the engine is rebuilt.
  - A rule that installs no filters is reported rather than recorded, so the interface never claims a block that does not exist.
- The Services page shows which services are blocked, and blocked-service names appear in the diagnostics export.

This completes what per-service attribution began in 0.85.0: that release made it possible to see which service was talking, this one makes it possible to do something about one without affecting the rest.

---

## [0.97.1] — 2026-07-29

### Fixed
- **Blocklist enforcement could install a machine-wide block on a loopback or private address.** Every hosts file opens by mapping `localhost` and friends to 127.0.0.1, and those lines were being accepted as blocklist entries; the enforcement then dutifully blocked the address they resolved to. Only the infrastructure loopback permit, which outranks it, prevented the machine being cut off from itself — that was luck, not design. Two independent guards now each stop this on their own: loopback plumbing (`localhost`, `broadcasthost`, the `ip6-*` names) is rejected as a blocklist entry, and enforcement will only ever target a public unicast address. Multicast and broadcast are excluded too, since a global block on the mDNS group would break local network discovery.

### Changed
- The blocklist description now states the real limitation: connection-layer blocking depends on Windows performing the lookup, so a browser using its own encrypted DNS is not covered until that setting is turned off in the browser.

---

## [0.97.0] — 2026-07-29

### Fixed
- **The DNS blocklist blocked nothing.** It only ever applied to lookups GunWall itself answered, so once the machine stopped sending its DNS here — deliberately, in 0.95.0 — the list became inert while the interface went on implying it worked. Adding a domain appeared to succeed and had no effect.

### Added
- **Blocklist enforcement at the connection layer.** The passive DNS watch knows which addresses each name resolved to, so a connection to an address belonging to a blocked domain is now blocked in the kernel and the existing session torn down. This is stronger than refusing the lookup: it holds for applications that bring their own resolver and never ask Windows at all, and it cannot be evaded by choosing a different DNS server. Filters are stored per domain, so removing a domain from the list removes everything it accumulated.
- A global remote-address block filter in the WFP engine, at the user-block weight, for enforcement that is not tied to a single application.

### Changed
- "Block direct connections" no longer requires GunWall's resolver to be running — it reads the same shared observations as domain rules.
- The blocklist description now states how blocking is actually applied, rather than implying that entries take effect on their own.

---

## [0.96.1] — 2026-07-29

### Fixed
- **The application terminated shortly after launch.** Two mistakes in the new DNS observer's native structure declarations, both from describing a layout by hand instead of reusing the one already proven in the byte meter:
  - `EVENT_TRACE_LOGFILEW` was declared sequentially with a guessed 208-byte gap for its two embedded structures. Those are 88 and 280 bytes, which places `EventRecordCallback` at offset 424; the hand-written version put it near 264. `OpenTrace` therefore read an arbitrary value as the callback address and `ProcessTrace` called into it, ending the process outright — with no managed exception and nothing in the log beyond a second startup line.
  - `EVENT_RECORD` had `ProviderId` and `EventId` transposed, so even once the session ran, no event would ever have matched and nothing would have been recorded.
  Both structures now use the explicit offsets shared with the byte meter, which is hardware-proven at over 110,000 events.
- **A crash-loop guard.** A fault inside an ETW callback ends the process immediately, so an application that dies during startup can otherwise never be recovered from its own interface. A marker is now written before the session is touched and cleared once it has run without incident; a launch that finds the marker still present skips the observer and says so, rather than repeating the crash. Turning the setting off and on again is a genuine retry.

### Added
- Offset assertions for both native structures, derived from the documented field layouts and checked against what the code declares — so this class of mistake fails a test rather than a machine.

---

## [0.96.0] — 2026-07-27

### Added
- **Passive observation of the system's DNS lookups**, restoring what removing DNS redirection took away. Domain rules and the "block direct connections" scope both need to know which name produced an address; until now the only source was GunWall's own resolver, so both were blind unless something was pointed at it. GunWall now subscribes to the events the Windows DNS client already emits and reads the results. It claims nothing, rewrites nothing and answers nothing — so unlike redirection it cannot come into conflict with security software or a VPN over ownership of port 53, and it sees every lookup the machine makes rather than only those routed through GunWall.
- A shared name-to-address memory that both the resolver and the observer write to, so the rule engine reads one source regardless of where a lookup was answered. IPv4-mapped IPv6 answers resolve to the same host, and aliases are deliberately never recorded as destinations.
- A **Names known** counter in the DNS panel and a **DNS watch** line in App Health, both stating plainly when domain rules have no data to match on. Observer event, answer and parse-failure counts appear in the diagnostics export.

### Changed
- Domain rules and direct-connection detection no longer require GunWall's resolver to be running.

---

## [0.95.1] — 2026-07-27

### Fixed
- **The removal migration could itself strand an adapter.** Restoring only the adapters recorded in the saved list left behind any that the old redirect had touched without recording — one that came up after the capture, or the case where a crash-leftover state was deliberately saved as nothing. Those would have kept pointing at the loopback resolver with no interface left to change them. The migration now also sweeps every adapter and returns any still aimed at 127.0.0.1 or ::1 to automatic.
- Corrected the claim that GunWall no longer changes this PC's DNS at all: *Security → Filtering DNS* still assigns a public resolver when asked. It never claims port 53 locally, so it carries none of the conflict that caused the removal.

---

## [0.95.0] — 2026-07-27

### Removed
- **"Route this PC's DNS through GunWall"**, along with the Gaming Session bypass that existed to suspend it. Redirecting the machine's DNS means claiming port 53, which puts GunWall in direct competition with other software that also claims it — security suites' DNS protection and VPN clients' leak protection in particular. Testing on hardware showed the failure precisely: plain UDP and DNS-shaped UDP both crossed loopback normally, while replies from the resolver on port 53 were discarded before delivery, with the resolver reporting every query answered. The result is a machine that appears to lose its internet connection, from a cause outside the firewall's control and impossible to fix in software. A feature that fails that way, on an unpredictable subset of machines, is not worth its cost.
- GunWall no longer points this PC's DNS at itself. The code that redirected adapters to the loopback resolver has been removed outright. *Security → Filtering DNS* is unaffected and still sets a public resolver (Cloudflare, Quad9, AdGuard) on request — that is an explicit choice of upstream provider and never claims port 53 locally, so it carries none of the conflict described above.

### Changed
- **Upgrading is safe even if routing was switched on.** On first launch the saved routing state is detected, the adapters are restored to the servers they used before, and the state is cleared. Restoring the saved list alone is not enough: the old redirect applied to whichever adapters were active when it ran, so one that appeared later could be redirected without ever being recorded, and a crash-leftover state was deliberately saved as nothing at all. The migration therefore also sweeps every adapter and returns any still aimed at the loopback resolver to automatic — so removing the feature cannot strand a machine with DNS pointing somewhere it can no longer manage. The sweep runs only during this one-time migration, never on every launch, because setting DNS to 127.0.0.1 by hand is now a legitimate way to use the resolver and must not be undone. A shutdown-time restore is retained as a further line of defence.
- The resolver keeps everything that did not depend on system routing: blocklists, DNS-over-HTTPS, CNAME-cloaking defence, the query log, and the name-to-address history that domain rules and the "block direct connections" scope rely on. Point an application — or this PC's DNS, deliberately and by hand — at 127.0.0.1 to use it.
- With nothing on the machine depending on GunWall for name resolution, fail-closed now means exactly that: a failed encrypted lookup fails rather than quietly falling back to plaintext.

---

## [0.94.0] — 2026-07-26

### Added
- **DNS-shaped loopback probe**, completing the diagnosis of the DNS-routing outage. Testing on real hardware established that plain UDP crosses loopback normally while the resolver's replies — sent successfully, with no send failures — are destroyed before delivery. Nothing in GunWall treats those two cases differently, so the difference is imposed from outside the application. The path test now also sends a genuine DNS message between two ordinary sockets, so the results triangulate the cause: whether loopback UDP is broken generally, whether DNS messages are dropped regardless of port, or whether port 53 specifically has been taken over. The verdict names the likely causes — security software with DNS protection, or a VPN client's DNS leak protection — and gives an ordered way to confirm which, including re-testing on port 5353.

---

## [0.93.0] — 2026-07-26

### Fixed
- **Blocklist entries that could never match, accepted silently.** A pasted URL such as `https://www.example.com/` was stored verbatim, and since DNS blocking matches host names, it could never match anything — the entry looked applied and did nothing. Lines are now reduced to the host name they mean: schemes, paths, queries, credentials, ports, `*.` wildcards and leading dots are all stripped, and hosts-file lines are understood. Anything genuinely unusable — a bare IP address, invalid characters, several words on one line — is now reported rather than quietly turned into a fragment.
- Applying the blocklist now clears the resolver's cache and flushes the Windows DNS cache. Without that, a name resolved before the rule existed kept being answered from cache, so a correct new block appeared not to work.

### Added
- Applying a blocklist reports what each line became: entries that were read as a domain name, entries that were ignored and why, and a reminder that blocking a name covers its subdomains but not its parent.

### Added
- **Raw loopback probe in the DNS path test.** Before testing the resolver, GunWall now sends a plain UDP datagram between two sockets inside its own process, with the resolver not involved at all. This separates two very different situations that previously looked identical: a fault in the resolver, versus loopback UDP delivery being broken on the machine by another network filter driver — in which case no local DNS server of any kind can work, and it is not something GunWall can fix in software.
- The resolver probe now samples its own receive and reply counters around the query, so a timeout reports whether the query was dropped on the way in or the reply was dropped on the way back.

### Fixed
- `FWP_CONDITION_FLAG_IS_APPCONTAINER_LOOPBACK` was `0x00000400`; the correct value is `0x00400000` (verified against `fwptypes.h` and Microsoft's win32metadata). This affected stealth mode's ICMP suppression, which used the flag to exempt loopback traffic.

---

## [0.92.0] — 2026-07-26

### Added
- **Diagnosis tooling for the DNS routing problem.** Three previous attempts at this fault were based on plausible theories that the logs could neither confirm nor rule out, because the resolver reported success for work whose outcome it never checked. This release adds the missing observability rather than another guess.
  - **Test DNS path** (DNS panel) sends a real DNS query to GunWall's own resolver over loopback — exactly as Windows does — on both IPv4 and IPv6, and reports the response code, answer count, round-trip time, and whether the reply's transaction ID matches. This distinguishes the two possibilities the counters cannot: a resolver that answers correctly but whose replies never reach the client, versus one the client never reaches at all.
  - The resolver now confirms **which loopback endpoints it actually bound**, rather than only logging failures, and counts queries received per address family, replies sent, and reply-send failures. A reply that fails to send was previously invisible: the query was recorded as answered while the client saw nothing and retried.
  - The diagnostics bundle now captures the DNS configuration **Windows is really using** — per-adapter servers for both address families, plus the **Name Resolution Policy Table**. NRPT rules override per-adapter DNS, so a VPN or mesh client with rules there wins over GunWall's redirection; `ipconfig` alone does not show them.

---

## [0.91.0] — 2026-07-26

### Fixed
- **Cached failures breaking name resolution — the real cause of the routing outage.** The response code of an upstream reply was never checked anywhere. Two consequences compounded: a `SERVFAIL` or `REFUSED` arrives as a perfectly valid HTTP 200 over DNS-over-HTTPS, so it was counted as a successful lookup (which is why diagnostics reported zero DoH failures during an outage); and it was then written to the cache, where the TTL fallback for an answer-less response is a full 60 seconds. Every subsequent lookup for that name was served the cached failure, and because clients retry hard on failure, they kept hitting the same poisoned entry — one transient upstream hiccup became a sustained outage for that name, while GunWall reported that it had answered every query. Switching adapters over to the resolver is exactly when such a hiccup occurs, which is why the problem appeared the moment routing was enabled and disappeared when it was turned off.
  - Upstream responses are now validated: only `NOERROR` and `NXDOMAIN` count as answers. Failure codes are treated as failures, so the retry and plaintext-fallback paths engage as intended.
  - Only genuine answers are cached. `NXDOMAIN` is cached briefly (30 s) rather than for the full TTL, since a stale "does not exist" is indistinguishable from a broken connection.
  - The cache is flushed when DNS routing is switched on or off, and on any resolver configuration change, so nothing captured mid-change can be served.
  - Diagnostics report an `upstreamRefused` count, making DNS-level failures visible instead of being hidden inside the success counter.

---

## [0.90.0] — 2026-07-25

### Fixed
- **Internet still dropping when routing this PC's DNS through GunWall — the real cause.** The resolver bound only IPv4 loopback (127.0.0.1) and routing set only the IPv4 DNS server, but Windows resolves over IPv6 as well and prefers it. With a VPN active, the machine kept an IPv6 DNS server (often the tunnel's) and sent lookups there, bypassing GunWall entirely; when that path was slow or unreachable, names "could not be found" even though GunWall's resolver was healthy and caching correctly. The resolver now also listens on IPv6 loopback (::1), routing points IPv6 DNS at ::1 too, and restore returns both families to automatic. This is the fix for the reported outage; the v0.89 connection-pool work stands, but the true problem was the IPv6 bypass.

---

## [0.89.0] — 2026-07-25

### Fixed
- **Internet dropping when this PC's DNS is routed through GunWall.** Two problems compounded. First, the DoH client used a single shared connection with a global 5-second timeout, so against a slower endpoint one slow request's timeout could abort the shared connection and break the next several in flight — seen as bursts of failures (one endpoint measured 208 aborted lookups against 82 successes, while a faster endpoint had zero). The client now uses a pooled, kept-warm `SocketsHttpHandler` with no global timeout; each query is bounded by its own token and retried once on the warm connection. Second, and more seriously, when a lookup did fail while the machine's own DNS pointed at GunWall, the OS got no answer and the machine appeared to lose internet. GunWall now keeps a plaintext path of last resort **whenever system DNS is routed through it**, so routed resolution can never black-hole — while still honouring fail-closed when GunWall is only an opt-in resolver for other clients.

---

## [0.88.0] — 2026-07-24

### Added
- **Domain rules in the access-rule engine.** A per-app rule can now target a domain — `block doubleclick.net`, or `allow github.com` above a block-all — completing the entity set alongside country, continent, ASN, IP, range and scope. Matching covers subdomains by default (`example.com` matches `cdn.example.com`) and is label-aware, so `evilexample.com` never matches `example.com`. An explicit `*.` prefix is accepted and behaves identically.
- The resolver now records which name produced each resolved address, which is what allows a rule about a *name* to be enforced against a connection that only carries an *address*. Domain rules therefore require GunWall's resolver to be running, and the block alert names the domain that matched.

---

## [0.87.0] — 2026-07-24

### Fixed
- **GeoIP silence.** With the local database never downloaded, every country and ASN lookup returned nothing — so the Countries breakdown was empty and country, continent and ASN rules could never match — with no indication anywhere that this was the cause. An empty Countries column read as "no foreign traffic" when it actually meant "no data". The state is now reported in three places: the App Health card, the diagnostics export, and an inline note on the Countries column itself.

---

## [0.86.0] — 2026-07-24

### Changed
- **The approval prompt now fails closed.** It no longer auto-decides by default — it waits for the user — and if the prompt is dismissed or closed by accident, the app is **blocked** rather than silently allowed. Previously the default was to auto-*allow* after 20 seconds, and closing the window decided nothing. A firewall that grants access to something you walked away from is not doing its job. The settings still allow a timeout, but the safe options are listed first and a missing setting falls back to block.
- **Service labels adapt to the column width.** Long Windows service display names were truncating mid-word; the label now fits as many names as will show and appends a count, with the full list in the tooltip.

---

## [0.85.0] — 2026-07-24

### Added
- **Per-service attribution.** Connections opened by service hosts now name the actual Windows service — `svchost (Windows Update, BITS)` rather than an indistinguishable row of `svchost` entries — with the full hosted-service list in a tooltip. The Apps list shows a service badge on host executables. Resolved through `EnumServicesStatusEx`, cached for 20 seconds, with the struct layout and constants verified against the Windows SDK headers and Microsoft's win32metadata.
- Service attribution counters in the diagnostics export.

---

## [0.84.0] — 2026-07-24

### Fixed
- **Error-log flooding.** Faults caused by GunWall itself cutting the network — lockdown, a new rule, the resolver stopping, an adapter dropping — abort in-flight sockets and pooled HTTPS connections. These are expected consequences, not defects, and a single lockdown could previously write 118 near-identical exception blocks to the log, crowding real errors out of the 300-entry error buffer. They are now classified and counted rather than logged individually. The classifier inspects every exception in an aggregate and treats anything unexpected as a real error.
- **Duplicate error entries.** The error buffer now deduplicates by context + type + message, collapsing repeats into one entry with a count and last-seen time. The log file records the full stack trace once, then milestone lines only (2nd, 10th, 100th repeat).

### Added
- Error counts (`N distinct, M total`) and benign-fault tallies in both the error-log viewer and the diagnostics export.

---

## [0.83.0] — 2026-07-24

### Fixed
- **Three incorrect Windows Filtering Platform GUIDs**, found by the new self-test and verified against the Windows SDK headers and Microsoft's win32metadata:
  - `FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4` — did not exist, so stealth mode's IPv4 ICMP-error suppression silently never installed.
  - `FWPM_CONDITION_IP_REMOTE_PORT` — invalid, so any filter carrying a remote-port condition failed to install.
  - `FWPM_CONDITION_ICMP_TYPE` — pointed at an unrelated real condition (`ALE_SIO_FIREWALL_SYSTEM_PORT`); the SDK defines it as an alias of `IP_LOCAL_PORT`.

### Added
- Condition-field probing in the kernel self-test, covering the class of bug a layer probe structurally cannot catch.

---

## [0.82.0] — 2026-07-24

### Added
- **Kernel layer self-test** (Settings → Diagnostics → *Verify kernel layers*). Probes every WFP layer GunWall uses and reports which this build of Windows accepts, with the error code for any rejection. The probe is a permit filter at weight 0, non-persistent, deleted immediately, so it cannot block traffic, outrank any existing filter, or survive a crash.
- Lockdown and system-rule application are now logged with filter counts, including a warning when a rule installs zero filters.

---

## [0.81.0] — 2026-07-24

### Added
- **Notification exclusions.** Alerts are categorised (security, protection changes, network, rules and profiles) and each category can be silenced independently.
- **Error-log viewer** (Settings → Diagnostics) showing this session's captured errors, with copy, clear, and refresh.
- **Tray single-click** to restore (opt-in; double-click always works).
- **Fit columns to content** in the Apps and Connections context menus.
- **UI size** setting (90% – 125%).

---

## [0.80.0] — 2026-07-24

### Added
- **Block routed (transit) traffic** system rule, closing a real gap: traffic merely routed through the machine — via a bridged VM, mesh VPN peer, or Internet Connection Sharing — never reaches the ALE layers and was previously unfiltered.
- **Block server / listening sockets** per-app scope, denying an application the server role outright across bind (TCP and UDP), listen, and accept.
- Lockdown now covers the forwarding layers, so "block everything" includes transit traffic.
- New WFP layers wired: `IPFORWARD` and `ALE_RESOURCE_ASSIGNMENT`, v4 and v6.

### Fixed
- Optional filters skipped by the kernel were reported only to the debugger and were therefore invisible in release builds. They now reach the diagnostics log, deduplicated.

---

## [0.79.0] — 2026-07-23

### Added
- **CNAME-cloaking defence.** Trackers evade domain blocklists by having a clean first-party name alias to a blocked one. The resolver now follows each answer's CNAME chain and denies the lookup if any hop is blocked. Cloaked answers are never cached.
- Full DNS name reader with compression-pointer support, loop guards, and bounds checking.
- *Cloaked* counter and log verdict in the DNS panel.

---

## [0.78.0] — 2026-07-23

### Added
- **Secure DNS (DNS-over-HTTPS, RFC 8484).** Queries can be forwarded encrypted over HTTPS. Built-in endpoints are IP-addressed, so enabling DoH needs no plaintext lookup to bootstrap itself.
- Fail-closed by default: if the encrypted resolver is unreachable, lookups fail rather than silently downgrading. Plaintext fallback is opt-in and stays with the same provider.

---

## [0.77.0] — 2026-07-22

### Added
- **Per-app entity rule engine.** Each application can carry an ordered, first-match-wins access policy. Rules match on country, continent, ASN, IP, CIDR range, network scope, or any, each set to allow or block, with a configurable default action. Includes an editor with reordering, enable/disable, and presets.

---

## [0.76.0] — 2026-07-22

### Added
- **Block Internet (allow LAN only)** per-app scope, enforced by 46 IPv4 CIDR filters covering exactly the public address space plus `2000::/3` for IPv6.
- **Block P2P / direct connections** per-app scope: connections to public addresses the application never resolved by name are blocked reactively and the session torn down. Requires GunWall's resolver to be running.

---

## [0.75.0] — 2026-07-22

### Added
- Animated connection arcs on the world map, from the local region to the busiest destinations.
- Per-app activity sparklines in the Apps list.

---

## [0.74.0] — 2026-07-22

### Added
- **Traffic breakdown** card splitting the session four ways: applications, remote hosts (with reverse-DNS names), traffic type, and countries, each with per-row bars.
- Port and protocol classifier covering HTTPS, QUIC, DNS, mail, VPN protocols, RDP, SMB, BitTorrent, discovery protocols, and more.

---

## [0.73.0] — 2026-07-22

### Added
- **Apps Usage Timeline.** Drag across the timeline to select any period and see which applications were active in it, busiest first, updating live during the drag.

---

## [0.72.0] — 2026-07-21

### Added
- Persistent footer status bar with live rates, session totals, protection state, metering mode, and unread alerts.
- Graph hover readout with cursor line and time-axis labels.

---

## [0.70.0] — 2026-07-21

### Added
- **Precise per-app metering** via an ETW kernel-network session, attributing bandwidth to processes from the kernel itself. Off by default; the estimation engine remains an automatic fallback, and a watchdog degrades to it if the session stops producing events.

---

## Earlier releases

Versions 0.9 through 0.69 established the foundations: Zero-Trust default-deny
with persistent approval, event-driven kernel detection with crash-loop
recovery, the packets log, custom rules, stealth mode, directional and timed
rules, the system-rule library, profiles, versioned backups, Windows Firewall
import, themes, VirusTotal lookups, blocklists, filtering DNS, Authenticode
signature verification, diagnostics export, network scopes, GeoIP with country
and ASN awareness, the built-in DNS resolver, domain heuristics, verdict
reasons, the captive-portal helper, and the notification centre.

See [`ROADMAP.md`](ROADMAP.md) for what is planned next.
