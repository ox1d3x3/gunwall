<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../branding/png/banner-slim-dark.png">
  <img src="../branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

# Engineering notes

`ARCHITECTURE.md` describes how GunWall is built. `TESTING.md` describes how to
verify a build. This document records the defects that reached a release, the
reasoning behind each one, and the control that now prevents its recurrence.

Every entry below shipped. Each is paired with an automated check in
`tools/checks/check_theme.py`, or with an explicit statement of why no static
check can cover it.

---

## 1. What static analysis can and cannot establish

GunWall is a WPF application whose enforcement runs in the Windows kernel.
Neither layer can be exercised by reading source. The check suite proves
properties of the **text** of the program; it cannot render a control, install a
filter, or observe a theme swap. Every pre-release check is therefore a proxy,
and the build on Windows is the only complete test.

This constraint shapes every entry that follows. Two classes of defect in this
list were identifiable only from a rendered screenshot, and no source-reading
check could have found either.

The discipline that follows from it: when a check reports success, establish what
its output would have been had the defect been present. If the answer is "the
same", the check is not evidence. See §2.10.

---

## 2. The trap list

Each of these shipped. Each now has a check, or an explicit note saying why it
cannot have one. `tools/checks/check_theme.py` is the enforcement; this is the
reasoning.

### 2.1 A value that changed, and nothing that read it

Moving a colour into the palettes and verifying the palettes changed says
nothing about what is still reading it from somewhere else. Shipped three times:
the chart that stayed blue, eleven pre-design colours in the shared dictionary,
and fifteen raw colours in drawing code that the markup-only check never opened.

**Check:** `colour-home`, which scans **both** markup and C#.

### 2.2 `StaticResource` against a dictionary that gets swapped

`ApplyTheme` replaces the palette dictionary. A `StaticResource` resolves once,
against whatever `App.xaml` merged, and never moves again.

**Check:** `late-binding`.

### 2.3 `StaticResource` against a dictionary merged later

Forward references across dictionaries do not degrade to a missing value — they
throw `StaticResourceHolder` while the template instantiates, which surfaces as
an error dialog on every screen using the control. The symptom points nowhere
near the cause.

**Check:** `merge-order`, which reads the merge order from `App.xaml` rather
than assuming one.

### 2.4 Code assigning a property that markup already bound

A local value does not bypass a `DynamicResource` — it destroys the binding. The
element keeps that colour in every theme afterwards. This is the XAML trap in
2.2 wearing a different costume, and it shipped one release after the check for
2.2 was written, because that check reads XAML and this was a line of C#.

**Check:** `binding-override`, plus an assertion that `ApplyTheme` re-runs the
painters that state-driven elements depend on.

### 2.5 Font family names that disagree

WPF resolves a family by name ID 16 when present, ID 1 otherwise. Two files
disagreeing on ID 16 become **two families**; a weight request then finds a
partial set and WPF falls back to the system font — silently.

Renaming font files to "fix" this caused it. Upstream had already unified the
names; the rename split them.

**Rule:** read name ID 16 before renaming a font, and prefer not renaming.
**Check:** `font-family`.

### 2.6 `new FontFamily("pack://...")` in code

A pack URI resolves only against a base URI. XAML supplies one from the file it
is parsed in; the single-argument constructor has none, so the family matches
nothing and WPF falls back — with no exception and no log line.

Installed fonts kept working throughout, because those resolve by plain name.
That asymmetry made it look like the bundled *files* were at fault, and three
releases went into the files before the constructor was suspected.

**Check:** `font-packuri`. Bundled faces are declared in XAML and copied.

### 2.7 An attached property that runs before its element is in the tree

A style setter lands the property while the element is still being built, so
inherited values — `FontFamily` especially — have not resolved. Logic that
measures at that moment measures the wrong thing.

Worse: the guard added later ran on the *second* pass, correctly declined, and
left the first pass's damage in place. It reported itself working.

**Rule:** apply on `Loaded`, keep the original input, and make a skip **restore**
rather than merely decline.

### 2.8 Sizing a column for the string that happens to be visible

Labels change. "Block service" and "Unblock service" differ by 16px; a column
sized for one clips the other. Uppercasing headers made every one of them wider
at once and clipped the two with no slack.

**Rule:** size from the longest string a cell can hold, not the current one.
**Check:** headers are measured against their own column width.

### 2.9 A name collision that is really a duplicate job

Two elements named the same thing is `CS0102`. But a collision usually means two
elements were given one job — the connection prompt ended up with a kicker in a
state strip and a subtitle under the question, both stating the kind of moment.
The fix is rarely a rename; it is noticing the duplication the compiler
stumbled over.

**Check:** `duplicate-name`, excluding `ControlTemplate` bodies, which are
separate namescopes.

### 2.10 Checks that cannot fail

Three shipped:

- an element-reference check whose exclusion rule matched nearly every
  assignment, so it reported success by finding nothing — on a 6,200 line file
- a helper using `if not m: continue`, which skipped three tables whose names
  were wrong and reported success for the nine that matched
- `"0.99.50.0".rstrip(".0")` yielding `0.99.5`, because `rstrip` takes a
  character set rather than a suffix — latent until the first version ending in
  zero

**Rule:** a check written after a defect must be **shown to fail on that
defect** before it is trusted. Reintroduce the bug, watch it fail, remove it,
watch it pass. A check never demonstrated against its own defect is a guess.

### 2.11 Approximations documented as caveats rather than enforced

The letter-spacing helper inserts hair spaces. Its limits were written at the
top of the file — including that it only works with proportional fonts. Three
releases later the default font became monospace, where a hair space is a full
character wide and the character is not even in the font.

**Rule:** if a limit matters, encode it. A comment does not run.

### 2.12 A container that positions but does not reserve

A single-cell `Grid` with children aligned left, left and right looks like a
three-part row and is not one. Alignment places things; it does not give them
space, and nothing stops two of them occupying the same pixels. The connection
prompt's countdown hint was bounded by a hand-picked `MaxWidth` instead, chosen
against the buttons as they measured at the time, and ran underneath the Block
button.

This is 2.8 with the direction reversed: not a column sized for the string that
happens to be visible, but a string assuming space belonging to something else.
Where one part of a row is fixed, the other's width is **derived** — so it needs
a star column, not a number.

**Check:** `hint-width`, which asserts the columns exist *and* measures every
string against the budget the layout actually leaves. Both halves are needed:
short strings in a single cell are what shipped, and they grew.

### 2.13 A fixed column in a container that resizes

A `GridView` column is a fixed width and nothing in it stretches. Wider than the
sum of its columns and a table shows ruled empty space; narrower and it clips —
**silently**, because these tables have no horizontal scrollbar to reveal what
was lost. With a resizable window and an interface scale on top, no single
number is right at both ends.

Connections carried a 196px `LOCATION` that left 492px empty on a wide window
while still truncating the ASN it existed to display.

**Rule:** where the rest of a row is fixed, the remaining column's width is
arithmetic, not a preference. Derive it, floor it, and compare before assigning —
setting `Width` re-raises `SizeChanged`.

**Check:** `last-column`.

### 2.14 An offset inside a shared area, mistaken for reserved space

Positioning something at `height - 15` does not reserve fifteen pixels; it places
one thing inside an area another thing is still drawing into. The chart's time
labels sat at `h - 15` while the series drew to `h`, so the trace ran through the
digits.

This is 2.12 in a canvas instead of a `Grid`. Both are the same error: alignment
within a shared area read as a claim on space.

**Rule:** reserve the band as a named constant and derive every consumer from it —
the drawing height, the baseline, the cursor, the label. Then check that the raw
height no longer reaches the drawing calls, because a band that exists but is
bypassed looks exactly like no band.

**Check:** `graph-axis`.

### 2.15 An anchor assertion that counts instead of identifying

0.99.75 replaced a block of `MainWindow.xaml.cs` between two anchors and asserted
`old.count("private void") == 2` before doing it. The count was correct. The two
methods were `CollapseConnInspector` and `ConnList_SizeChanged`, the replacement
text only restored the second, and the first was deleted with two live call sites
still pointing at it. `CS0103`, discovered at build time rather than here.

The uniqueness discipline was followed. The assertion still passed on a wrong
edit, because **it checked a quantity rather than an identity**. Two of the right
shape is not the same as the two that were meant.

**Rule:** an assertion before a block replacement must name what is being removed
and confirm the replacement puts each of them back. `count(...) == n` is not that.

**Check:** `local-call` — every bare PascalCase call must resolve to a declaration
somewhere in the project. It is the first check here that looks at C# calling C#.

### 2.16 A gap documented as covered by something outside the loop

`element-ref` had a careful note saying the Roslyn pass answers it, since a
missing element is `CS0103`. True — and the Roslyn pass is the compiler on the
compiler, which is the far side of the loop the check exists to run in
front of. A gap deferred to the build is a gap, described politely.

**Rule:** "covered elsewhere" is only coverage if the elsewhere runs before the
release gate. Otherwise state plainly that it is not covered.

### 2.17 A comment describing what an API would do if it were kind

`RemoveAllFiltering` was documented as tearing down "the entire sublayer and
every filter inside it". WFP does not do that. `FwpmSubLayerDeleteByKey0` returns
`FWP_E_IN_USE` while a single filter still references the sublayer — it never
deletes filters, it refuses.

The comment was not describing the call; it was describing the intention, and
because it read like a fact nobody checked. The throw then fired *before* the
store was cleared, so a button labelled "run this before uninstalling" aborted
leaving both the kernel filters and the saved rules in place.

**Rule:** a comment on an interop call states what the call does, verified from
the documentation, not what the caller wanted. Where a comment and an error code
disagree, the error code is the one that ran.

**Check:** `reset-path`.

### 2.18 Enforcing a name-based decision at the address layer

A blocklist is a list of NAMES. The kernel filters ADDRESSES. Translating one into
the other looks obvious and is only safe when the address belongs to the name.

`AddDomainReactiveBlock` installed a global /32 block, above every application
rule, for whatever address a blocked domain resolved to. On a CDN edge that
address answers for thousands of names, so blocking one tracker took down every
unrelated service behind it — permanently, for every application, with nothing on
screen explaining it. A 12-hour session accumulated blocks on Akamai, CloudFront,
Cloudflare, Google and Microsoft edges and cost the machine its antivirus updates
and its git client.

The failure is silent by construction: the block is on the destination, so it
outranks the application rules the user reaches for, and allowing the affected app
changes nothing.

**Rule:** before translating a name decision into an address decision, establish
that the address belongs to that name alone. GunWall already observed the data to
know this and was discarding it — one name per address, last writer wins.

**Check:** `silent-failure` asserts the sharing test exists on both sides.

### 2.19 A teardown that removes the permits and leaves the denials

`SetStrictMode(false)` removed the zero-trust baseline and then iterated
`_data.Rules.Where(r => r.Status == AppStatus.Allowed)` to remove their filters.
That `Where` skipped exactly the rules whose filters **deny** traffic, and nine
other filter collections - lockdown, blocklists, domain-derived address blocks,
system rules, blocked services, entity filters - were never touched at all.

Turning the firewall off therefore removed what was letting traffic through and
kept what was stopping it. On a kernel holding PERSISTENT filters that survive the
app closing, the result is a machine locked by software that is no longer running
and no longer offering a way to unlock it.

**Rule:** an "off" that is partial is worse than no "off". A teardown enumerates
what it owns rather than filtering it - and if the thing being iterated has a
predicate on it, ask what the predicate excludes.

**Check:** `reset-path` asserts the sweep is reflective and that the Allowed-only
predicate has not returned.

### 2.20 Treating "nothing known" as "nothing present"

The startup reconcile removed every filter in GunWall's sublayer that the store
could not name. Sound reasoning, and it ran from the window's field initialisers -
before the store had loaded. It read **0 tracked against 116 live**, concluded all
116 were orphans, and deleted the entire working filter set of a protected machine.

The risk was seen while the code was being written and dismissed in a comment as
"correct behaviour too" if the store were lost. It is not. An empty answer from a
component that is not ready is not an answer.

**Rule:** before acting destructively on a comparison, establish that both sides
are true. A zero on one side is a reason to stop, not a licence.

**Check:** `reset-path` asserts both the readiness gate and the zero-tracked
refusal, because either alone would have prevented this and neither alone is
sufficient.

### 2.21 A readiness flag set by someone who cannot know

`ReconcileReady` was set by the window's `Loaded` handler, under a comment stating
that the store was loaded by then. It was not: `Initialize()` - which runs
`_data = _store.Load()` - is called thirty lines FURTHER DOWN the same method.

So the reconcile ran against an empty store on every launch. It never destroyed
anything, because a second guard refused to act on zero tracked filters, and that
is the only reason this looked like success. The feature simply never ran, for two
releases, while the log printed a reassuring sentence about declining to act.

**Rule:** a readiness flag belongs to the object whose state it describes, set at
the moment that state becomes true. A caller cannot know when that is - and if the
caller is the one who has to remember, the ordering is already a bug waiting.

**Check:** `reset-path` asserts positionally that the reconcile call site appears
after `Initialize()`, that no external `MarkReconcileReady` exists, and that
`Initialize()` sets it.

### 2.22 Reasoning about a control template instead of reading it

Three dropdowns clipped their own text. Two releases tried to fix it by widening
them — the first from a ComboBox "chrome" constant measured on a different control
in an earlier release and reused without re-measuring. The controls grew; the text
stayed clipped, because **width was never the constraint.**

WPF UI's ComboBox template lays content out in a `*` column with
`Margin="{TemplateBinding Padding}"`, and its default padding is `10,8,10,8`.
GunWall forced `Height = 28`, leaving twelve pixels of vertical room for text, and
the content grid clipped what did not fit.

The template is not in this repository, and both failed attempts treated that as a
reason to reason about it. It is fetched from `lepoco/wpfui` in seconds.

**Rule:** when a control from a third-party library misbehaves, read its template
before changing anything. A measurement taken from a screenshot of a different
control is not evidence about this one, and a constant that cannot be re-derived
should not be relied on twice.

**Check:** `access-rules` forbids pinning either `Width` or `Height` on these
controls rather than asserting a computed size — a property this project can
establish, where a number was a guess wearing a check's clothes.

### 2.23 Auditing one file and calling it audited

An interrupted edit left partial work across several files. A duplicate
`DeviceNote_Edit` was found in `MainWindow.xaml.cs`, removed, and the package was
shipped — while `FirewallManager.cs` carried two copies of `GetDeviceNote` and
`SetDeviceNote`, and `RuleStore.cs` two of `DeviceNotes`. Three CS0111/CS0102
errors, in a build that had been declared verified.

The search that found the first duplicate was run against one file. Nothing about
the cause was specific to that file.

**Rule:** when a defect is found, the next step is to search for it everywhere,
not to fix the instance. A defect discovered by accident is evidence about the
whole tree, not about the file it happened to surface in.

**Check:** `duplicate-member` matches on name AND parameter list across every
type in the project, so genuine overloads - `DnsMessage.TryReadName` exists twice
with different parameters - are not reported. A check that flagged those is one
people would learn to ignore.

### 2.24 A suppression list with one entry

`OnDispatcherUnhandledException` classified a single known WPF defect as benign
and routed everything else to a `MessageBox`. A second framework defect then
arrived and took the dialog path.

An application taking the display in **exclusive** fullscreen causes DWM to hand
off composition. Windows broadcasts `WM_DWMCOMPOSITIONCHANGED` to every top-level
window; WPF's `WindowChrome` — which `ui:FluentWindow` uses — answers by calling
`DwmExtendFrameIntoClientArea` against a composition that no longer exists, and
receives `DWM_E_COMPOSITIONDISABLED` (`0x80263001`). The call concerns the window
border only: no rule, no filter and no kernel state is involved.

Three occurrences were recorded in a single session under an exclusive-fullscreen
title, against zero under a borderless one on the same machine.

The dialog was never observed. The connection prompt is `Topmost="True"`; the
error box is an unowned `MessageBox` and is not, so it rendered beneath the prompt
and remained on the desktop behind the foreground application. The only symptom
that surfaced was a second loss of focus from the game, which was attributed to
the connection prompt — correct for the first, incorrect for the second.

Two costs, from one root: an "unexpected error" raised for a condition no user can
act on, and a genuine fault deposited where nobody looks.

**Rule:** a benign-fault list with one entry has not been reasoned about. When a
framework defect is classified benign, enumerate what else reaches the same
handler — and establish whether the dialog it raises is visible from the user's
position at the moment it fires.

**Rule:** identify a Win32 fault by **HRESULT**, never by message text. Windows
localises the message; the numeric code does not translate. A message match
succeeds on the machine it was authored against and fails silently everywhere
else.

**Rule:** do not match the outer stack frame. Two captures of this fault arrived
via `HwndSubclass.DispatcherCallbackOperation` and `HwndWrapper.WndProc`
respectively, because `WM_DWMCOMPOSITIONCHANGED` is a broadcast and the answering
window is not fixed. Match the frame that identifies the defect.

**Check:** `dwm-fault` — asserts the exception type, the HRESULT, and a
`WindowChromeWorker` frame; forbids a message-text match; asserts the classifier
is called, that it precedes **both** the error log and the dialog, that the fault
is counted, and that it is marked handled.

The first revision of that check asserted ordering against `MessageBox.Show`
alone. Under falsification, `DiagnosticLog.LogException` was moved above the
classifier and the check still passed — a state in which the fault would be
recorded as a real error *and* counted as benign, leaving the diagnostics error
total unchanged. The check was written, passed, and was wrong; falsification is
the only reason that is known. This is §2.10 in a new form: an ordering assertion
that names one of the two operations it orders against is not an ordering
assertion.

### 2.25 A default is indistinguishable from a saved value

`FirewallManager` exposes every persisted setting as a property over a `_data`
field holding a **default-constructed** `StoreData` until the store is read. A
caller that asks before that read receives the default. Not null, not an
exception, not a log line — the default, which is a plausible value.

`MainWindow.OnLoaded` read `ThemeDark` thirty lines above the `Initialize()` call
that loads the store, so every launch applied the default theme irrespective of
what had been saved. It was reported as the theme not being remembered, which
points at the save path; the save path was correct throughout.

This is the **third** occurrence of this ordering defect in this handler. 2.13
records the first two: 0.99.93 and 0.99.94 placed the reconcile-readiness flag
above the same call. The conclusion drawn then was that ordering which depends on
every caller remembering the ordering is not ordering, and readiness was moved
inside the object. The theme read was left outside that boundary because nobody
searched for other early reads once the reported symptom went away.

**Rule:** an uninitialised store must not be readable. Where that cannot be
arranged, the load must be idempotent and callable by anything that needs a
value, so no caller has to know what has already run. `EnsureSettingsLoaded()` is
that, and `Initialize()` loads through it rather than beside it.

**Rule:** when a defect is fixed by moving ownership inside an object, enumerate
every other reader that was relying on the old ordering. Fixing the reported
symptom is not fixing the defect — §3 already says a defect found by accident is
evidence about the whole tree, and this is the same rule applied to a defect found
on purpose.

**Rule:** treat "setting not remembered" as a **read**-ordering hypothesis before a
write one. A persistence symptom names the save path, and the save path is the
half that usually works, because it runs on user action when everything is
initialised. The read runs at startup, when things are not.

**Check:** `settings-before-load` — asserts the loader exists and is idempotent,
that `Initialize()` loads through it, and that the first `_firewall` reference in
`OnLoaded` is that call. The last condition generalises: it fails for any setting
added later, not only the theme.

**Note on the repair itself.** The scripted edit that introduced this fix asserted
anchor uniqueness once, ahead of two replacements. The first replacement inserted
text containing the second anchor, so the second replacement matched the newly
written copy instead of the intended one — producing a method that called itself
and an `Initialize()` still loading directly. §3 requires uniqueness to be
asserted before *every* scripted edit; asserting once for a batch does not satisfy
it, because an earlier edit in the same batch can invalidate a later assertion.
The check caught it on its first run.

### 2.26 Redacting a secret by mutating the object that holds it

`SanitizedConfigJson()` produced the settings document for the diagnostics bundle
by assigning `"(redacted)"` to `_data.VirusTotalApiKey`, serialising, and
restoring the real value in a `finally`.

That is correct single-threaded and wrong here. The export runs via `Task.Run`,
takes seconds because it shells out to `netsh` and `ipconfig` with eight-second
timeouts, and leaves the UI thread live throughout. There are ninety-plus
`_store.Save(_data)` call sites, and `Save` serialises the object handed to it —
the same object, in its redacted state. Approving one application at a prompt
during an export writes the placeholder to disk as the real credential.

The value is the only one in the profile the user cannot regenerate from inside
GunWall, and the trigger was exporting a bundle to report an unrelated fault.

**Rule:** never redact by mutating the live object. Redact the produced document.
A `finally` restores state on the thread that set it; it does not restore state
another thread has already read and written.

**Rule:** a scrubbing routine must fail closed on the shape it is scrubbing. This
one located the secret by property name and overwrote it. Overwriting a key that
is not present would *add* it, emitting the real credential alongside a redacted
decoy that looks correct on inspection. A rename must break the export, not
quietly widen it.

**Rule:** enumerate every path that touches a user credential before declaring
any of them safe. Four of the five here were already correct — the installer never
writes to the profile, uninstall asks and defaults to No, the update hands off to
the installer, and the reset keep-list names the credential explicitly. The
question asked was whether updates preserved the key, and the answer was yes; the
defect was on the fifth path, which nobody had asked about.

**Check:** `secret-handling` — all five paths, six falsifying mutations.

### 2.27 One method answering two questions

`RemoveAllFiltering()` reverted every change GunWall made to the machine and then
discarded the store. Two operations, one entry point, and the second was reachable
by a caller that wanted only the first.

The uninstaller calls `GunWall.exe --unblock` from `InitializeUninstall()`, before
`CurUninstallStepChanged` asks whether to keep the saved profile. The store was
emptied and written to disk before the question was put. Answering "No" preserved
an empty file, so uninstall-then-reinstall lost every rule and the user's
VirusTotal key while reporting that it had kept them — with a reassuring default
on the prompt.

**Rule:** when one method does two things, the caller that needs one of them will
eventually get both. Split by the question being answered, not by what happens to
sit adjacent in the call order. "Undo what we did to this machine" and "discard
what the user decided" are different questions with different callers.

**Rule:** a promise made in a prompt is a specification. If the text says
declining keeps the profile, then no code path reachable before that prompt may
touch the profile.

**Rule:** one list, not two. `ResetSettingsToDefaults` and `ClearStore` both need
to know what belongs to the user. A second copy drifts, and the drift is silent —
a credential survives one path and is destroyed by the other.

**Check:** `unblock-preserves-store`, nine falsifying mutations.

**Two defects in the check, both found by falsification, both the recurring
neighbourhood match.**

The guard asserting `--unblock` does not call `ClearStore` excluded comment lines,
because the comment above the call explains that `ClearStore()` is deliberately
not called there. The exclusion matched its own explanation and disabled the guard
permanently — the check could not fail. Comments are now stripped before the test.

The `ClearStore` assertion tested for the identifier `UserOwnedSettings`, which
also appears in that method's closing log line. Replacing the loop's source with
an empty array left the substring present and the check passed. It now asserts the
iteration itself.

Both passed review. Neither survived the falsification run. This is the reason the
run is mandatory rather than advisory, and it is now the third and fourth time a
check written in this project has been shown to be incapable of failing.

### 2.28 A guard that cannot tell teardown from attack

Tamper protection re-installs GunWall's filters when they disappear from the
kernel. The uninstaller removes those filters. Nothing told either about the
other, so uninstalling with GunWall open removed 24 filters and restored 28 nine
seconds later, and the uninstall completed over the top — leaving filtering
enforcing with nothing installed that could undo it.

The watchdog was not wrong. It has no way to distinguish a deliberate teardown
from an attack, and it must not: removing the sublayer is exactly what an attacker
would do, so "sublayer gone, stand down" would trade the whole feature for this
one case.

**Rule:** a self-healing component and a teardown path are in conflict by
construction. Whichever runs second undoes the other. The teardown must stop the
healer before it starts — not signal it, not race it.

**Rule:** locks scoped to a call do not close a race that resolves after the call
returns. `--unblock` completed in 50 ms and the conflict landed nine seconds
later; a mutex held for the duration would have been released long before, and
would have looked like a fix. Measure the actual interval before choosing the
mechanism.

**Rule:** when two paths do the same preparation, check they both do it. The
install path closed the running app; the uninstall path never did, and the
difference sat unnoticed because the two are read at different times and the
uninstall path is exercised least.

**Rule:** a defect whose outcome depends on incidental state - here, whether the
user happened to have the window open - produces contradictory reports and looks
intermittent. Two runs an hour apart disagreed completely. Find the variable
before theorising about the mechanism.

**Check:** `unblock-stops-app`, both layers, eight falsifying mutations.

### 2.29 Truncating the good copy before the new one arrives

`GeoIpService.Fetch` opened the destination with `File.Create` and streamed the
download into it. `File.Create` truncates on open, so the working country table
was destroyed before the first byte of its replacement had been received. A
connection dropped at 80% left a partial table that loaded without complaint. The
OUI download wrote its destination in a single call, which is better but still not
safe against a crash or a full disk.

Neither was reached by accident: the risk was bounded while these ran only on a
button press with someone watching the result. What changed is that they are about
to go on a daily timer, which removes the person who notices.

**Rule:** never open the live file to receive data that has not arrived yet. Write
a temp sibling, validate, move. The move is the only step that touches the
destination, and it happens after there is something worth keeping.

**Rule:** the temp file goes beside the destination, never in %TEMP%. `File.Move`
is atomic within a volume and a copy across volumes is not.

**Rule:** a successful transfer is not a valid file. A captive portal answering
200 with an HTML login page satisfies every check except reading it. A validated
temp file is the only place that check can live.

**Rule:** where several sources share one output, a partial result must not
replace a complete one. The three IEEE registries write one file, so a refresh
reaching one of them would have discarded the other two and reported success.

**Rule:** harden the operation before automating it. Automation does not create
these faults, it removes the human who was compensating for them. The ordering -
safety first, schedule second - is the whole reason this shipped as its own
release.

**Check:** `db-download-safety`, eleven falsifying mutations.

**One defect in the check, found by falsification.** The host-pinning assertion
counted occurrences of `DatabaseHost` and required three. Removing the
pre-request comparison entirely still left the identifier in both error messages
and in the redirect test, so the count held and the check passed — a service that
would fetch any URL at all. It now asserts the two comparisons by shape. This is
the fifth time a check here has been shown incapable of failing, and every one has
been the same thing: matching the neighbourhood instead of the thing.

### 2.30 Assuming the environment instead of reading the neighbours

`AtomicFile.cs` was a new file, and it used `Stream` without `using System.IO;`
on the assumption that `ImplicitUsings` covers it. It does not here. Every other
file in `Services/` that touches IO declares that using — the convention was
visible in every neighbouring file and was not read. The build failed with CS0246
on the maintainer's machine, which is the one failure this suite exists to
prevent.

**Rule:** when adding a file, read what the files beside it declare. A convention
followed by every existing file is a fact about the project, not a style
preference, and it is cheaper to copy than to reason about.

**Rule:** a defect found in a new file is still evidence about the whole tree.
Searching for the same pattern found one more candidate, which turned out to be a
tuple element named `Path` — worth the search either way.

**Check:** `usings-declared`. It cannot prove a program compiles; it proves the
specific thing that broke.

### The larger finding: several checks were reading mangled source

Checks removed comments and string literals with two regex passes, in both
possible orders. Neither works:

- comments first — the `//` inside `"https://standards-oui.ieee.org/..."` is read
  as a comment, the closing quote is consumed, and every literal after it is
  mis-paired. **Half of `OuiService.cs` was deleted before matching**, with no
  error and no output.
- strings first — a quote inside a comment opens a literal that never closes.

This was not one check. It was seven, including checks that had been trusted for
releases. `db-download-safety` passed a falsification run only because the file it
was reading had been truncated.

**Rule:** a lexical problem needs a lexer. Regex cannot strip C# comments and
strings, because each construct can contain the other's delimiter, and the
failure is silent — the check keeps reporting `ok` against text that is no longer
the program.

`strip_cs` is a single pass tracking which construct is open. Two faults were
found in it, both by the suite reporting against its own source:

- Interpolation holes were copied whole, so `$"0x{code:X8}"` presented `X8` as a
  symbol and `local-call` reported an undeclared function in `WfpEngine.cs`. The
  expression ends at the first `,` or `:` outside parentheses, which is where C#
  ends it — and is why a ternary in a hole must be parenthesised.
- Adjacent holes concatenated: `{ErrorCode:X8}{(ErrorCode == 0 ? a : b)}` became
  `ErrorCode(ErrorCode`, read as a call. A space did not fix it, because
  `Foo (x)` is a legal call in C#. Holes are terminated with a semicolon.

**Rule:** a shared helper used by many checks must be falsified through the checks
that use it, not only on its own. Both faults above appeared as failures in
unrelated checks, which is what identified them.

---

## 3. Working agreements

- **Re-read from disk before editing.** Never edit from memory of an earlier
  turn.
- **Assert before replacing.** Every scripted edit checks its anchor is unique.
  This has caught edits aimed at the wrong table twice. It was skipped once for
  speed and corrupted `Controls.xaml`.
- **Never index a string after reassigning it.** That is what corrupted it.
- **Never recall a WFP GUID or struct offset.** Verify against the SDK or
  win32metadata. Three were wrong once and matched nothing; one killed the
  process with no managed exception.
- **Every change to shipped code bumps the version; documentation alone does
  not.** Both halves matter, and both have been got wrong. Two versions were
  burned on documentation, then three code fixes rode along on a number already
  used. A build whose version matches a different build's behaviour makes every
  later bug report start with a question nobody can answer. If in doubt: did a
  compiled file change? Bump. See `CONTRIBUTING.md`.
- **A direct admission beats a quiet correction.** If something shipped broken,
  the changelog says so and says why the verification missed it.

---

## 4. Deliberate deviations from the design

Recorded so they are not mistaken for oversights:

| Deviation | Reason |
|---|---|
| Animated sun/moon toggle kept, then replaced by the spec's 30×30 icon button | Product decision, revised twice |
| Connection prompt is a separate always-on-top window, not an in-window modal | GunWall lives in the tray; an overlay inside a hidden window is a prompt nobody can answer, and the fail-closed timeout would then block traffic the person was never asked about |
| Prompt is compact with detail behind a chevron, not the design's 580px dialog | A large modal arriving unannounced is dismissed faster, not read more carefully |
| Firewall label states status, not action | It sits beside a switch, which already shows the action; two readings of one control appear to contradict |
| No footer | Its readouts became duplicates once the posture module and top bar landed; the two unique ones were rehomed |
| Negative letter-spacing not implemented | WPF has no character spacing, and the positive-only approximation cannot express it. Moot while the interface font is monospace |

---

## 5. What is deliberately not built

- **Self-signed certificates for app trust.** Rejected, not deferred. AV
  products weigh reputation, not signature validity; a self-signed certificate
  has none by construction. Making one *valid* means adding a root to the
  machine's trust store, which is a security downgrade a firewall should not
  ship. Re-signing third-party binaries destroys the vendor signature that
  GunWall's own Authenticode and hash checks depend on.
- **Four table columns the design shows** — Rules `HITS`, Windows services
  `Action`, Network scan `Vendor` and `Latency`, Traffic `Share`. Each needs a
  feature behind it. An empty column looks like conformance and means nothing.
