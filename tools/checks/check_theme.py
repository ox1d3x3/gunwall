#!/usr/bin/env python3
"""
GunWall theme checks.

Run from the repository root:  python3 tools/checks/check_theme.py

These live in the repository deliberately. The offline check suites used to be
written into /tmp and were lost to a container reset, taking with them the tests
that caught the WFP struct offsets and the wrong layer GUIDs. A check that does
not survive the session is a check that gets rewritten from memory next time,
which is how a check quietly stops testing the thing it was written for.

Two checks are new in 0.99.42. Both exist because of a specific defect that a
passing check failed to catch:

  LATE-BINDING   0.99.41 moved the chart series into the palettes, which is
                 correct, and verified that the palettes changed. Six consumers
                 referenced them with StaticResource. ApplyTheme REPLACES the
                 palette dictionary at runtime, so a StaticResource resolves once
                 against whatever App.xaml merged - Theme.Dark - and never moves
                 again. The upload series stayed near-white on a white card:
                 exactly the bug 0.99.41 was written to fix, surviving in the
                 elements the fix did not look at.

  COLOUR-HOME    0.99.32 rewrote both palettes. Eleven pre-design colours were
                 not in the palettes - they were in the shared dictionary - so
                 the rewrite did not touch them and everything reading them
                 carried on working. A blue accent chain and a four-colour chart
                 series survived three releases of "the palette is now red".

The lesson both encode: verifying that a value CHANGED says nothing about what
is still READING somewhere else.
"""

import collections
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src" / "GunWall"
DARK = APP / "Themes" / "Theme.Dark.xaml"
LIGHT = APP / "Themes" / "Theme.Light.xaml"
SHARED = APP / "Themes" / "Controls.xaml"
ICONS = APP / "Themes" / "Icons.xaml"

# Tokens the design defines that no phase has wired up yet. Each names the
# release that will consume it. Without this list the dead-key check cannot tell
# "stale, delete it" from "not yet built", and a check that cries wolf is a check
# that gets ignored. Delete an entry when its phase lands; if an entry is still
# here several releases later, the token was never really needed.
PENDING_TOKENS = {
    "InfoText":       "0.99.45 neutral pill text",
    "StatFontSize":   "0.99.47 primary stat, 30px",
    "IconCheck":      "0.99.48 verdict rows in the table system",
    "IsDarkTheme":    "read by future theme-dependent drawing",
}

# KNOWN LIMITATION: the dead-key search looks for the literal key name. Code that
# builds a key at runtime - UpdateStatusBanner does role + "Text" and role + "Fill"
# - is invisible to it, so AllowText/WarnText/BlockText and their Fill partners can
# look dead while being read every second. They are kept honest by being referenced
# in XAML as well. If a token is ever consumed ONLY through a constructed name, add
# it here with that noted, or the check will tell you to delete something live.

# Keys GunWall defines FOR the control library rather than for itself. Nothing in
# this repository references them by design.
LIBRARY_KEYS = {
    "PaneFluentButtonHeight", "PaneFluentButtonWidth",
    # WPF looks this one up internally for any element that has not specified
    # its own focus visual. Nothing in this repository references it by name and
    # nothing should - that is how the hook works.
    "{x:Static SystemParameters.FocusVisualStyleKey}",
}

failures = []
notes = []


def fail(check, msg):
    failures.append(f"[{check}] {msg}")


def xaml_files():
    return sorted(APP.glob("*.xaml")) + sorted((APP / "Themes").glob("*.xaml"))


def keys_in(path):
    return set(re.findall(r'x:Key="([^"]+)"', path.read_text(encoding="utf-8")))


def check_theme_parity():
    d, l = keys_in(DARK), keys_in(LIGHT)
    for k in sorted(d - l):
        fail("parity", f"{k} is in the dark palette only")
    for k in sorted(l - d):
        fail("parity", f"{k} is in the light palette only")
    notes.append(f"parity: {len(d)} keys, both palettes")


def check_duplicate_names():
    """No x:Name may appear twice in one XAML file.

    WPF generates a field per x:Name, so a duplicate is CS0102 - a compile error,
    not a runtime one. That means it is caught, but only by the maintainer on the
    other side of a build, which in this project is the slowest feedback loop
    there is. It costs nothing to catch here.

    0.99.70 shipped one: restructuring the connection prompt added a subtitle
    named HeaderText while the state strip still had a TextBlock of that name.
    The duplicate was really a design duplicate - two elements doing one job -
    and the compiler said so in the bluntest available way.
    """
    for f in sorted(APP.glob("*.xaml")) + sorted((APP / "Themes").glob("*.xaml")):
        text = f.read_text(encoding="utf-8")
        # Each ControlTemplate is its own NAMESCOPE, so a name repeated across
        # templates is legal and normal - Controls.xaml has five borders called
        # "Bd" and always has. Only the window's own scope can collide, so the
        # templates are removed before counting. Getting this wrong would have
        # meant a check that fails on correct code, which is the same defect as
        # one that passes on broken code wearing a more convincing face.
        scope = re.sub(r"<ControlTemplate\b.*?</ControlTemplate>", "", text, flags=re.S)
        names = re.findall(r'x:Name="([^"]+)"', scope)
        for n in sorted({x for x in names if names.count(x) > 1}):
            fail("duplicate-name",
                 f"{f.name}: x:Name {n!r} appears {names.count(n)} times - WPF "
                 "generates one field per name, so this is CS0102")
    if not any(f.startswith("[duplicate-name]") for f in failures):
        notes.append("duplicate-name: every x:Name unique within its file")


def check_merge_order():
    """No StaticResource may reference a key from a dictionary merged later.

    StaticResource resolves at PARSE time against the current dictionary and the
    ones merged before it. A forward reference across dictionaries does not
    degrade to a missing icon - it throws StaticResourceHolder while the template
    is being instantiated, which surfaces as an unhandled error dialog on every
    screen that uses the control. The symptom points nowhere near the cause.

    0.99.64 shipped exactly one of these: the table empty-state referenced an
    icon geometry from Icons.xaml, which App.xaml merges AFTER Controls.xaml.
    Every panel containing a ListView threw on load.
    """
    app = (APP / "App.xaml").read_text(encoding="utf-8")
    order = re.findall(r'<ResourceDictionary\s+Source="Themes/([^"]+)"', app)
    if not order:
        return
    defined_by = {}
    for i, name in enumerate(order):
        f = APP / "Themes" / name
        if not f.exists():
            continue
        for k in re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8")):
            defined_by.setdefault(k, i)

    for i, name in enumerate(order):
        f = APP / "Themes" / name
        if not f.exists():
            continue
        for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            code = re.sub(r"<!--.*?-->", "", line)
            for m in re.finditer(r"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}", code):
                key = m.group(1)
                at = defined_by.get(key)
                if at is not None and at > i:
                    fail("merge-order",
                         f"{name}:{n} uses StaticResource {key}, defined in "
                         f"{order[at]} which is merged later - it cannot resolve at "
                         "parse time and will throw when the template instantiates")
    if not any(f.startswith("[merge-order]") for f in failures):
        notes.append(f"merge-order: no forward StaticResource across {len(order)} dictionaries")


def check_late_binding():
    """StaticResource against a key that only the swapped palettes define."""
    palette_only = (keys_in(DARK) | keys_in(LIGHT)) - keys_in(SHARED)
    hits = 0
    for f in xaml_files():
        for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            for m in re.finditer(r"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}", line):
                if m.group(1) in palette_only:
                    hits += 1
                    fail("late-binding",
                         f"{f.relative_to(ROOT)}:{n} {m.group(1)} is StaticResource; "
                         "ApplyTheme swaps that dictionary, so this freezes at "
                         "load and will not follow a theme change")
    if not hits:
        notes.append(f"late-binding: 0 frozen refs across {len(palette_only)} palette-only keys")


def check_colour_home():
    """Colour must be defined in the palettes and nowhere else."""
    text = SHARED.read_text(encoding="utf-8")
    body = re.sub(r"<!--.*?-->", "", text, flags=re.S)   # comments may cite hex
    for n, line in enumerate(body.splitlines(), 1):
        for m in re.finditer(r'(?:Color|Background|Foreground|Fill|Stroke)\s*=\s*"(#[0-9A-Fa-f]{3,8})"', line):
            fail("colour-home",
                 f"Controls.xaml: literal {m.group(1)} - colour belongs in the palettes")
        if re.search(r"<(SolidColorBrush|LinearGradientBrush|RadialGradientBrush|Color)\b", line):
            fail("colour-home", f"Controls.xaml: colour resource defined outside the palettes")
    # ...and the same rule in C#. The XAML-only version of this check passed for
    # fourteen releases while ten raw colours sat in the drawing code, including
    # a blue that was the System CATEGORY colour reused as chart chrome. Markup
    # was never the only place a colour could be written; it was just the only
    # place anyone was looking.
    RAW = re.compile(r"Color\.From(?:Rgb|Argb)\(\s*0x[0-9A-Fa-f]{2}\s*,\s*0x[0-9A-Fa-f]{2}")
    for cs_file in sorted(APP.rglob("*.cs")):
        for n, line in enumerate(cs_file.read_text(encoding="utf-8").splitlines(), 1):
            if RAW.search(line):
                fail("colour-home",
                     f"{cs_file.relative_to(ROOT)}:{n} builds a colour from literal "
                     "channels - read it from the palette instead, or it will not "
                     "follow a theme change")

    if not any(f.startswith("[colour-home]") for f in failures):
        notes.append("colour-home: no colour outside the palettes, in markup or code")


def check_dead_keys():
    all_xaml = "\n".join(f.read_text(encoding="utf-8") for f in xaml_files())
    cs = "\n".join(p.read_text(encoding="utf-8") for p in APP.rglob("*.cs"))
    defined = {}
    for f in xaml_files():
        for k in re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8")):
            defined.setdefault(k, f)
    dead = []
    for k in defined:
        if k in LIBRARY_KEYS or k in PENDING_TOKENS:
            continue
        n = len(re.findall(r"(?:Static|Dynamic)Resource\s+" + re.escape(k) + r"\s*[}\s]", all_xaml))
        n += len(re.findall(r'"' + re.escape(k) + r'"', cs))
        if n == 0:
            dead.append(k)
    for k in sorted(dead):
        fail("dead-key", f"{k} is defined in {defined[k].name} and referenced nowhere")
    if not dead:
        notes.append(f"dead-key: none ({len(PENDING_TOKENS)} pending tokens allow-listed)")


def check_local_calls():
    """Every bare method call must resolve to something declared in the project.

    This is the gap that shipped 0.99.75. `CollapseConnInspector` was deleted by a
    block replacement and two call sites were left pointing at nothing. Every
    check in this file passed: the XAML was well-formed, every x:Name resolved,
    every XAML event handler had a method. None of them look at C# calling C#.

    `element-ref` below says the Roslyn pass answers this. It does - but the
    Roslyn pass is the compiler on the maintainer's machine, which is the far side
    of the loop. Deferring to it is deferring to the thing the check was supposed
    to run in front of.

    Scope, deliberately narrow: a bare PascalCase call - not `x.Foo(`, not
    `new Foo(` - must be declared somewhere in the project's .cs files. That
    catches a deleted or renamed method, which is the actual failure, without
    needing to resolve types. Anything inherited or attribute-shaped is
    allow-listed by name, and the list is short enough to read.

    KNOWN LIMITATION, found by this check firing on correct code in 0.99.86:
    quotes nested inside an interpolation hole - `$"x={(b ? "ON (a)" : "OFF")}"`
    - defeat the string stripping below, which pairs quotes left to right. The
    text between the inner quotes is then scanned as if it were code, and
    `ON (` reads as a call.

    Left unfixed on purpose. Matching C# interpolation properly needs a parser,
    and the alternative - allow-listing whatever words leak out - is how a check
    stops failing. Writing the branch into a local first is clearer code anyway,
    so a false positive here is a nudge rather than an obstacle.
    """
    import glob as _glob
    srcs = {f: Path(f).read_text(encoding="utf-8")
            for f in _glob.glob(str(APP / "**" / "*.cs"), recursive=True)}
    if not srcs:
        fail("local-call", "no .cs files found")
        return

    def strip(t):
        t = re.sub(r'@"(?:[^"]|"")*"', '""', t)
        t = re.sub(r'"(?:\\.|[^"\\])*"', '""', t)
        t = re.sub(r"//[^\n]*", "", t)
        return re.sub(r"/\*.*?\*/", "", t, flags=re.S)

    declared = set()
    for t in srcs.values():
        for m in re.finditer(
                r"(?:^|[;{}\)]\s*)(?:\[[^\]]*\]\s*)*"
                r"(?:(?:public|private|protected|internal|static|async|override|virtual"
                r"|sealed|new|partial|extern|unsafe|readonly)\s+)*"
                # The return type: at least one NON-SPACE token, then mandatory
                # whitespace before the name. The first version of this allowed the
                # type to be whitespace only, so ") CollapseConnInspector(" parsed
                # as a declaration with a blank return type - every call site
                # registered itself as its own definition, and the check passed on
                # the exact deletion it was written for. Third time this session.
                r"[\w<>\[\],\.\?]+[\w<>\[\],\.\?\s]*\s+"
                r"(\w+)\s*(?:<[^(){};=]*>)?\s*\(", strip(t)):
            declared.add(m.group(1))

        # Tuple return types - "(ulong h1, ulong h2) Hash(" - which the pattern
        # above cannot see, because allowing parentheses in a return type would let
        # it match call sites again. A separate, tighter pattern instead: an access
        # modifier, a parenthesised type, then the name.
        for m in re.finditer(
                r"(?:public|private|protected|internal)\s+"
                r"(?:(?:static|async|override|virtual|sealed|new|readonly|partial)\s+)*"
                r"\([^()]*\)\s+(\w+)\s*(?:<[^(){};=]*>)?\s*\(", strip(t)):
            declared.add(m.group(1))

    KEYWORDS = {
        "if", "while", "for", "foreach", "switch", "catch", "lock", "using", "return",
        "fixed", "do", "else", "when", "nameof", "typeof", "sizeof", "default",
        "checked", "unchecked", "stackalloc", "await", "throw", "is", "as", "not",
        "get", "set", "value", "var", "yield",
    }
    # Names that resolve without being declared in this project. Grouped by REASON,
    # not collected ad hoc: an allow-list that grows one name at a time is how a
    # check quietly stops failing. Anything that does not fit one of these three
    # categories is a finding, not an entry.
    ALLOWED = (
        # 1. Instance members inherited from Window / FrameworkElement / Control,
        #    called unqualified from a code-behind class that derives from them.
        {"Activate", "BeginAnimation", "Close", "DragMove", "FindResource",
         "TryFindResource", "Hide", "Show", "InitializeComponent",
         "Shutdown"}   # Application.Shutdown, from the emergency-unblock path
        # 2. Attribute constructors, which are syntactically calls.
        | {"DllImport", "FieldOffset", "MarshalAs", "StructLayout"}
        # 3. Generic BCL types whose construction spans a line break, so the
        #    "not preceded by new" guard cannot see the new.
        | {"HashSet", "IReadOnlyDictionary"}
    )

    unresolved = {}
    for f, t in srcs.items():
        for m in re.finditer(r"(?<![\w\.])(?<!new\s)([A-Z]\w*)\s*(?:<[^(){};]*>)?\s*\(",
                             strip(t)):
            n = m.group(1)
            if n in KEYWORDS or n in ALLOWED or n in declared:
                continue
            unresolved.setdefault(n, set()).add(Path(f).name)

    for n, files in sorted(unresolved.items()):
        fail("local-call",
             f"'{n}' is called in {', '.join(sorted(files))} but declared nowhere "
             "in the project - this is CS0103 waiting for the next build")

    if not unresolved:
        notes.append(f"local-call: {len(declared)} declarations, every bare call resolves")


def check_element_references():
    """Deliberately not implemented here - see the note below.

    A regex version of this check was written for 0.99.42 and removed before it
    shipped. It compared identifiers the code-behind accesses as elements against
    the x:Name set, and subtracted anything that looked locally declared. The
    "looks locally declared" pattern matched nearly every assignment in the file,
    so the check reported success by finding nothing at all - on a 6,200 line
    file, which should have been the tell.

    That is the same failure the 0.99.36 entry describes, one level up: not a
    check scoped to the wrong names, but a check whose exclusion rule was wide
    enough to exclude everything. A check that cannot fail is worse than no
    check, because it is counted as coverage.

    Doing this properly needs to know what is a local, a field, a type and a
    generated element field, which means a parser rather than a regex. The
    Roslyn pass over the project already answers it: an element that does not
    exist is CS0103, and CS0103 outside a .xaml.cs file is always real. This
    function stays as a marker so the gap is visible rather than forgotten.
    """
    notes.append("element-ref: element identifiers still need the compiler; "
                 "bare method calls are covered by local-call above")


def check_binding_override():
    """Code must not assign a brush property that markup already bound.

    A DynamicResource in markup re-resolves when ApplyTheme replaces the palette.
    A local value assigned in code outranks it permanently: the binding is not
    merely bypassed for that assignment, it is gone. The element keeps whatever
    colour the palette held at that instant, in every theme thereafter.

    0.99.43 shipped exactly this. PostureName had Foreground="{DynamicResource
    TextPrimary}" in markup, and UpdateStatusBanner also assigned it from
    FindResource. On a machine whose saved theme was light, the assignment
    resolved to near-black ink, the binding died, and switching to dark left the
    posture state name invisible against its own card - so the module read as
    nothing but "Turn firewall off", which looks exactly like a firewall that is
    off. The late-binding check passed the whole time: it reads XAML, and this
    was a line of C#.

    Properties that legitimately vary with STATE rather than theme - a role dot,
    a status pill - must be assigned in code. Those must not carry a
    DynamicResource for the same property in markup, or the two fight and the
    code always wins. Give them a plain literal default instead, and re-resolve
    them on theme change.
    """
    # Properties that vary with STATE, not theme, so they must be painted in
    # code. They keep their markup DynamicResource for the first paint, and the
    # repaint invariant below is what stops them freezing: ApplyTheme re-runs the
    # painters after every swap. Adding to this list is a promise that the
    # element's painter is on that path - check before you add.
    STATE_PAINTED = {
        ("MainWindow", "PostureDot", "Fill"),
        ("MainWindow", "EngineDot", "Fill"),
        ("MainWindow", "HeroDot", "Fill"),
        ("MainWindow", "HeroKicker", "Foreground"),
        # AlertWindow is constructed per prompt, so it always resolves at the
        # current theme. A swap while a prompt is open would still freeze it.
        ("AlertWindow", "SignatureText", "Foreground"),
        # The subject tile takes the ROLE colour, which depends on why the prompt
        # appeared rather than on the theme. Same exemption, same condition: the
        # window is constructed per prompt, so it always resolves at the current
        # theme. A theme switch with a prompt open would still freeze it.
        ("AlertWindow", "SubjectTile", "Background"),
        ("AlertWindow", "SubjectIcon", "Stroke"),
    }
    BRUSH_PROPS = ("Foreground", "Background", "Fill", "Stroke", "BorderBrush")
    for xaml in APP.glob("*.xaml"):
        cs = xaml.with_suffix(".xaml.cs")
        if not cs.exists():
            continue
        text = xaml.read_text(encoding="utf-8")
        src = cs.read_text(encoding="utf-8")
        # element -> properties bound with DynamicResource in markup
        bound = {}
        for m in re.finditer(r'x:Name="([^"]+)"([^>]*)>', text, re.S):
            name, attrs = m.group(1), m.group(2)
            for p in BRUSH_PROPS:
                if re.search(p + r'="\{DynamicResource', attrs):
                    bound.setdefault(name, set()).add(p)
        for name, props in bound.items():
            for p in sorted(props):
                if (xaml.stem, name, p) in STATE_PAINTED:
                    continue
                if re.search(r"\b" + re.escape(name) + r"\s*(?:\?)?\.\s*" + p + r"\s*=[^=]", src):
                    fail("binding-override",
                         f"{cs.name}: assigns {name}.{p}, which {xaml.name} binds with "
                         "DynamicResource - the local value kills the binding and freezes "
                         "the colour at the theme in force when it runs")
    # The repaint invariant the allow-list depends on. Without this, every
    # state-painted element above freezes at the theme in force when it last ran.
    src = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    body = src[src.find("private void ApplyTheme"):]
    body = body[:body.find("\n    private ", 10)]
    for painter in ("UpdateStatusBanner", "SyncLockdownButton"):
        if painter + "()" not in body:
            fail("binding-override",
                 f"ApplyTheme does not call {painter}() - the state-painted elements "
                 "it repaints will freeze at the previous theme")
    if not any(f.startswith("[binding-override]") for f in failures):
        notes.append(f"binding-override: clean ({len(STATE_PAINTED)} state-painted, repaint invariant holds)")


def check_no_code_pack_fonts():
    """No FontFamily may be constructed from a pack URI in C#.

    A pack URI resolves only against a base URI. XAML supplies one from the file
    it is parsed in; the single-argument FontFamily(string) constructor has none,
    so new FontFamily("pack://application:,,,/Fonts/#X") matches nothing and WPF
    falls back to the system font - silently, with no exception and no log line.

    0.99.59 shipped this. The picker built its families in code, and applying one
    at startup overwrote the working XAML resource with a non-resolving copy.
    Installed fonts kept working because they resolve by plain name, which made
    it look like the bundled files were at fault - three releases were spent on
    the font files before the constructor was suspected.
    """
    hits = []
    for cs in sorted(APP.rglob("*.cs")):
        for n, line in enumerate(cs.read_text(encoding="utf-8").splitlines(), 1):
            # Strip line comments first. The note explaining THIS rule contains
            # the exact pattern it forbids, and the first version of this check
            # flagged its own documentation.
            code = re.sub(r"//.*$", "", line)
            if re.search(r'new\s+FontFamily\s*\(\s*"pack://', code):
                hits.append(f"{cs.relative_to(ROOT)}:{n}")
    for h in hits:
        fail("font-packuri",
             f"{h} constructs a FontFamily from a pack URI - it has no base URI to "
             "resolve against and will silently fall back. Copy the XAML resource "
             "instead")
    if not hits:
        notes.append("font-packuri: no pack-URI FontFamily built in code")


def check_font_families():
    """Every bundled weight of a family must agree on its typographic name.

    WPF resolves a FontFamily by name ID 16 when present, falling back to ID 1.
    If two files in one family disagree on ID 16 they become TWO families, and a
    weight request against either finds a partial set - at which point WPF falls
    back to the system UI font and the whole interface silently stops using the
    bundled face.

    0.99.61 did exactly that. Renaming files "to fold the weights into one
    family" set ID 16 on two of four, splitting Regular+Bold from
    Medium+SemiBold. Upstream had already unified them; the rename was solving a
    solved problem and broke it. Nothing errored, nothing logged - the text just
    stopped being monospaced.
    """
    fonts = sorted((APP / "Fonts").glob("*.ttf"))
    if not fonts:
        return
    try:
        from fontTools.ttLib import TTFont
    except ImportError:
        notes.append("font-family: skipped (fontTools not installed)")
        return

    groups = {}
    for f in fonts:
        t = TTFont(str(f), lazy=True)
        n = t["name"]
        typo, fam = n.getDebugName(16), n.getDebugName(1)
        weight = t["OS/2"].usWeightClass
        t.close()
        # Group by the stem before the weight suffix, which is what a human
        # means by "this family's files".
        stem = f.stem.split("-")[0]
        groups.setdefault(stem, []).append((f.name, typo or fam, weight))

    for stem, items in sorted(groups.items()):
        resolved = {name for _, name, _ in items}
        if len(resolved) > 1:
            fail("font-family",
                 f"{stem}: files disagree on the name WPF resolves - {sorted(resolved)}. "
                 "They will register as separate families and weight selection "
                 "will fall back to the system font")
        weights = sorted(w for _, _, w in items)
        if len(weights) != len(set(weights)):
            fail("font-family", f"{stem}: duplicate weights {weights}")

    if not any(f.startswith("[font-family]") for f in failures):
        notes.append(f"font-family: {len(groups)} bundled families, each internally consistent")


def check_hint_width():
    """The connection prompt's countdown hint must fit the space left over.

    0.99.71 put the hint in a single-cell Grid between the chevron and the
    buttons, bounded by MaxWidth="210" - a number picked against the buttons as
    they measured at the time. A single cell reserves nothing, so nothing
    enforced it, and "Blocks automatically in 18s" rendered underneath the Block
    button.

    0.99.72 made it a real star column, which stops the OVERLAP. It does not
    stop the string being too long: a hint that ellipsises to "Blocks
    automatically i..." has lost the seconds, which is the only part with a
    deadline attached. So the budget is derived here from the same metrics the
    layout uses, and the strings are measured against it.

    Every input is READ, not assumed - window width, chevron width, button
    MinWidth, the hint's margins and font size, and the font's own advance
    width from the TTF. If any of those change, this recomputes rather than
    going quietly stale, which is the whole complaint in trap 2.11.
    """
    alert_x = (APP / "AlertWindow.xaml").read_text(encoding="utf-8")
    alert_c = (APP / "AlertWindow.xaml.cs").read_text(encoding="utf-8")
    ctrls = SHARED.read_text(encoding="utf-8")

    def one(pat, text, what, flags=re.S):
        # DOTALL by default: several of these anchors span lines, and without it
        # they match nothing and the check reports "found 0" rather than reading
        # a value. The interpolated format string opts OUT, because it contains
        # its own quotes and has to be read to end-of-line instead.
        m = re.findall(pat, text, flags)
        if len(m) != 1:
            fail("hint-width", f"expected exactly 1 match for {what}, found {len(m)}")
            return None
        return m[0]

    win = one(r'\n\s*Width="(\d+)" SizeToContent="Height"', alert_x, "window width")
    # Border Margin, BorderThickness, then the inner StackPanel's Margin.
    marg = one(r'CornerRadius="12" Margin="(\d+)"', alert_x, "card margin")
    bord = one(r'BorderBrush="\{DynamicResource BorderBrush\}" BorderThickness="(\d+)"',
               alert_x, "card border")
    pad = one(r'<StackPanel Margin="16,(\d+),16,(\d+)">', alert_x, "content padding")
    chev = one(r'x:Key="PromptChevron".*?<Setter Property="Width" Value="(\d+)"',
               ctrls, "chevron width")
    btnw = one(r'x:Key="PromptSecondary".*?<Setter Property="MinWidth" Value="(\d+)"',
               ctrls, "button MinWidth")
    gap = one(r'x:Name="BlockButton".*?Margin="0,0,(\d+),0"', alert_x, "button gap")
    hm = one(r'x:Name="CountdownHint".*?Margin="(\d+),0,(\d+),0"', alert_x, "hint margins")
    fs = one(r'x:Name="CountdownHint".*?FontSize="([\d.]+)"', alert_x, "hint font size")
    budget_decl = one(r"HintBudgetChars\s*=\s*(\d+)", alert_c, "HintBudgetChars")
    if None in (win, marg, bord, pad, chev, btnw, gap, hm, fs, budget_decl):
        return

    # The hint inherits the window's UiFont. JetBrainsMono is the default and,
    # being monospaced, is the WORST case - Instrument Sans averages narrower.
    # Read the advance from the file rather than recalling 0.6em: trap 2.5 was a
    # font metric taken on trust.
    font = APP / "Fonts" / "JetBrainsMonoNerdFont-Regular.ttf"
    try:
        adv, upm = _ttf_advance(font, ord("W"))
    except Exception as ex:
        fail("hint-width", f"could not read advance from {font.name}: {ex}")
        return

    row = int(win) - 2 * (int(marg) + int(bord) + 16)
    free = row - int(chev) - (2 * int(btnw) + int(gap)) - (int(hm[0]) + int(hm[1]))
    per_char = float(fs) * adv / upm
    budget = int(free // per_char)

    if budget != int(budget_decl):
        fail("hint-width",
             f"HintBudgetChars says {budget_decl} but the layout gives {budget} "
             f"({free:.0f}px free / {per_char:.2f}px per char)")

    # Every string the hint can hold, including the widest countdown value the
    # timeout picker offers. Sized from the LONGEST it can hold, not the one
    # visible when this was written - trap 2.8.
    strings = [one(r'FailClosedHint\s*=\s*"([^"]*)"', alert_c, "FailClosedHint")]
    # NOT [^"]*: the format string holds "Allow" and "Block" in their own quotes,
    # so that stops after nineteen characters and measures a fragment. It passed
    # doing exactly that, because the fragment happened to be under budget - a
    # check succeeding on a value it never really read, which is 2.10 again.
    fmt = one(r'CountdownText\(\)\s*=>\s*\$"(.+)";\s*$', alert_c, "CountdownText",
              flags=re.M)
    if None in (strings[0], fmt):
        return
    rendered = (fmt.replace('{(_defaultAllow ? "Allow" : "Block")}', "Block")
                   .replace("{_secondsLeft}", "999"))
    if "{" in rendered:
        fail("hint-width",
             f"CountdownText has an interpolation this check cannot render: {rendered}")
        return
    strings.append(rendered)

    for s in strings:
        if len(s) > budget:
            fail("hint-width",
                 f'"{s}" is {len(s)} chars, over the {budget}-char budget '
                 f"({len(s) * per_char:.0f}px into {free:.0f}px)")

    before = len(failures)
    _check_hint_column(alert_x)

    # Only report if nothing above failed. The first version appended this
    # unconditionally, so a broken column printed "ok ... star column" directly
    # beside a FAIL saying the column was wrong - a check contradicting itself in
    # the same output, at the moment someone most needs to read it plainly.
    if len(failures) == before:
        worst = max(strings, key=len)
        notes.append(f"hint-width: {budget} chars fit, longest is {len(worst)} "
                     f'("{worst}"), star column')


def _check_hint_column(alert_x):
    """The structural half: the hint must live in a star column of its own.

    Short strings alone are not the fix. They were short once before, in a
    single-cell Grid, and grew. A star column bounds the hint whatever it says,
    so this asserts the column exists rather than trusting that the strings stay
    disciplined - and it is a real XML parse, because a regex looking for
    ColumnDefinitions near a name is the kind of loose match that reports success
    on the wrong Grid.
    """
    import xml.etree.ElementTree as ET
    NS = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
    X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
    root = ET.fromstring(alert_x)

    owner = None
    for grid in root.iter(NS + "Grid"):
        for child in grid:
            if child.get(X + "Name") == "CountdownHint":
                owner = (grid, child)
                break
        if owner:
            break
    if owner is None:
        fail("hint-width", "CountdownHint is not a direct child of any Grid")
        return

    grid, hint = owner
    cols = [c for defs in grid.findall(NS + "Grid.ColumnDefinitions")
            for c in defs.findall(NS + "ColumnDefinition")]
    if len(cols) != 3:
        fail("hint-width",
             f"the actions Grid has {len(cols)} column definitions, expected 3 "
             "(chevron / hint / buttons). With fewer, children overlap instead "
             "of being given space, which is how 0.99.71 shipped.")
        return

    idx = hint.get("Grid.Column")
    if idx != "1":
        fail("hint-width", f'CountdownHint is in column {idx!r}, expected "1"')
        return
    if cols[1].get("Width") != "*":
        fail("hint-width",
             f'the hint column is Width={cols[1].get("Width")!r}, expected "*" - '
             "a fixed or Auto width does not shrink to what is left over")
    if hint.get("MaxWidth"):
        fail("hint-width",
             f'CountdownHint has MaxWidth={hint.get("MaxWidth")!r}. The star '
             "column already bounds it; a hand-picked maximum is the number that "
             "was wrong in 0.99.71.")
    if hint.get("TextTrimming") != "CharacterEllipsis":
        fail("hint-width",
             "CountdownHint must set TextTrimming=CharacterEllipsis, so an "
             "over-long string ends in ... rather than at a cut character")


def _ttf_advance(path, codepoint):
    """Advance width and unitsPerEm for one glyph, straight out of the TTF.

    Hand-parsed so the check has no dependency the repository does not already
    carry. Reads the table directory, then head.unitsPerEm, hhea.numberOfHMetrics
    and the hmtx entry for the glyph cmap format 4 maps the codepoint to.
    """
    import struct
    b = path.read_bytes()
    n = struct.unpack(">H", b[4:6])[0]
    tabs = {}
    for i in range(n):
        o = 12 + 16 * i
        tag = b[o:o + 4].decode("latin-1")
        tabs[tag] = struct.unpack(">I", b[o + 8:o + 12])[0]

    upm = struct.unpack(">H", b[tabs["head"] + 18:tabs["head"] + 20])[0]
    num_h = struct.unpack(">H", b[tabs["hhea"] + 34:tabs["hhea"] + 36])[0]

    # cmap: find a format 4 subtable and walk it for the codepoint.
    c = tabs["cmap"]
    best = None
    for i in range(struct.unpack(">H", b[c + 2:c + 4])[0]):
        o = c + 4 + 8 * i
        pid, eid, off = struct.unpack(">HHI", b[o:o + 8])
        if (pid, eid) in ((3, 1), (3, 10), (0, 3), (0, 4)):
            best = c + off
            break
    if best is None:
        raise ValueError("no unicode cmap subtable")
    if struct.unpack(">H", b[best:best + 2])[0] != 4:
        raise ValueError("cmap subtable is not format 4")

    segx2 = struct.unpack(">H", b[best + 6:best + 8])[0]
    seg = segx2 // 2
    ends = struct.unpack(f">{seg}H", b[best + 14:best + 14 + segx2])
    so = best + 16 + segx2
    starts = struct.unpack(f">{seg}H", b[so:so + segx2])
    do = so + segx2
    deltas = struct.unpack(f">{seg}h", b[do:do + segx2])
    ro = do + segx2
    ranges = struct.unpack(f">{seg}H", b[ro:ro + segx2])

    gid = 0
    for i in range(seg):
        if starts[i] <= codepoint <= ends[i]:
            if ranges[i] == 0:
                gid = (codepoint + deltas[i]) & 0xFFFF
            else:
                p = ro + 2 * i + ranges[i] + 2 * (codepoint - starts[i])
                gid = struct.unpack(">H", b[p:p + 2])[0]
                if gid:
                    gid = (gid + deltas[i]) & 0xFFFF
            break
    if gid == 0:
        raise ValueError(f"U+{codepoint:04X} is not in the font")

    idx = min(gid, num_h - 1)
    adv = struct.unpack(">H", b[tabs["hmtx"] + 4 * idx:tabs["hmtx"] + 4 * idx + 2])[0]
    return adv, upm


def check_graph_axis():
    """The chart's time labels must sit in a reserved band, not in the plot.

    0.99.72 drew the series and the baseline to the full canvas height and then
    placed the labels at `h - 15` — an offset inside the area the chart is drawn
    into. That is not a reserved band; it is an overlap written as arithmetic,
    and the trace ran through the digits on every dip toward zero.

    The rule this encodes: the plot height and the label position must both come
    from the band constant, and the raw canvas height must not reach the drawing
    calls at all. Testing for the ABSENCE of the old pattern matters as much as
    the presence of the new one — a band that exists but is bypassed looks
    identical to no band.
    """
    before = len(failures)
    src = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")

    m = re.search(r"GraphAxisBand\s*=\s*([\d.]+)", src)
    if not m:
        fail("graph-axis", "GraphAxisBand is not declared")
        return
    band = float(m.group(1))

    body = re.search(r"private void RedrawGraph\(\).*?\n    }\n", src, re.S)
    if not body:
        fail("graph-axis", "could not locate RedrawGraph")
        return
    body = body.group(0)

    if not re.search(r"double\s+ph\s*=.*?h\s*-\s*GraphAxisBand", body):
        fail("graph-axis", "RedrawGraph does not derive a plot height from GraphAxisBand")
        return

    # The raw canvas height must not reach the drawing calls.
    for call, pat in (("DrawBaseline", r"DrawBaseline\(canvas,\s*w,\s*(\w+)\)"),
                      ("AddSmoothSeries", r"AddSmoothSeries\([^;]*?,\s*w,\s*(\w+),")):
        for arg in re.findall(pat, body):
            if arg != "ph":
                fail("graph-axis",
                     f"{call} is given '{arg}' as its height, expected the plot "
                     "height 'ph' - drawing to the full canvas puts the chart "
                     "over the axis labels")

    tops = re.findall(r"Canvas\.SetTop\(tb,\s*([^)]+)\)", body)
    if len(tops) != 1:
        fail("graph-axis", f"expected 1 axis-label SetTop, found {len(tops)}")
        return
    top = tops[0].strip()
    if not top.startswith("ph"):
        fail("graph-axis",
             f"axis labels are positioned at '{top}', which is not relative to "
             "the plot height - that is exactly how they ended up inside the plot")
        return

    off = re.match(r"ph\s*\+\s*([\d.]+)", top)
    if off:
        # 9.5px text is about 13px of line box. It has to clear the baseline and
        # still fit inside the band, or the fix trades one overlap for a clip.
        need = float(off.group(1)) + 13
        if need > band:
            fail("graph-axis",
                 f"labels sit at ph+{off.group(1)} and need ~{need:.0f}px, but the "
                 f"band is only {band:.0f}px - they would be clipped")

    # Gated, like hint-width: a check that prints "ok" beside its own FAIL is
    # unreadable at the one moment someone needs it plain.
    if len(failures) == before:
        notes.append(f"graph-axis: {band:.0f}px band, plot and labels both derived")


def check_table_last_column():
    """The Connections table's last column must be derived, not declared.

    GridView columns are fixed widths and nothing stretches. Wider than the sum
    and the table shows ruled empty space; narrower and it clips silently, since
    these tables have no horizontal scrollbar. The window is resizable from
    1000px and carries an interface scale on top, so no single number is right at
    both ends - which is why 196px left a 490px empty band on a wide window while
    still truncating the ASN it was meant to show.
    """
    before = len(failures)
    xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    src = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")

    lv = re.search(r'<ListView x:Name="ConnList".*?</ListView>', xaml, re.S)
    if not lv:
        fail("last-column", "could not locate the ConnList ListView")
        return
    if 'SizeChanged="ConnList_SizeChanged"' not in lv.group(0):
        fail("last-column",
             "ConnList does not hook SizeChanged, so its last column keeps a "
             "fixed width and cannot follow the window")
        return
    if not re.search(r"private void ConnList_SizeChanged", src):
        fail("last-column", "ConnList_SizeChanged is hooked in XAML but not defined")
        return

    body = re.search(r"private void ConnList_SizeChanged.*?\n    }\n", src, re.S)
    if not body:
        fail("last-column", "could not read the body of ConnList_SizeChanged")
        return
    body = body.group(0)

    # Re-entrancy: assigning Width raises SizeChanged again.
    if "Math.Abs" not in body:
        fail("last-column",
             "ConnList_SizeChanged assigns a width without comparing against the "
             "current one; setting Width re-raises SizeChanged and the layout "
             "pass will not settle")
    # The defect this replaced: a floor applied to the GROWING column. It does not
    # create space, it only decides which end the overflow leaves from - and an
    # overflowing column is cut by the scroll area, outside TextTrimming's reach,
    # mid-glyph. "LOCATIO" instead of "LOCATION". Test for the absence of the old
    # pattern as well as the presence of the new one.
    if re.search(r"double\s+last\s*=\s*Math\.Max\(\s*ConnLocationWant", body):
        fail("last-column",
             "the last column is floored at ConnLocationWant. A floor on the "
             "column being grown cannot create room; it pushes the total past the "
             "table and the overflow is cut mid-glyph. Take the space from the "
             "columns that have slack instead.")
    if "ConnColumnFloors" not in body or "_connDeclaredWidths" not in body:
        fail("last-column",
             "no shrink pass: without declared widths and per-column floors there "
             "is nothing to take space from when the inspector opens")
    if not re.search(r"if\s*\(\s*last\s*<\s*0\s*\)", body):
        fail("last-column",
             "nothing handles the case where even the floors do not fit. The total "
             "must be scaled to the table, or it overflows and cuts mid-glyph.")

    _check_inspector_toggle(xaml, src)

    if len(failures) == before:
        notes.append("last-column: ConnList last column derived from the table width")


def _check_inspector_toggle(xaml, src):
    """The inspector closes on a real deselection, not on the transient one.

    The connections list clears and refills every sample, so it deselects roughly
    once a second. Closing the panel on that would flicker, which is why 0.99.72
    never closed it at all and 0.99.73 left it permanently open. The correct
    behaviour needs both halves: close on deselection, but only once the rebuild
    has settled.

    Only the guard is checked, because that is the half that is easy to lose. A
    later edit that drops it produces a panel strobing once a second - obvious on
    screen, invisible to every other check here.
    """
    if 'x:Name="InspPlaceholder"' in xaml or "InspPlaceholder" in src:
        fail("last-column",
             "InspPlaceholder is back. A panel that collapses when nothing is "
             "selected has no state in which a placeholder can be seen - it was "
             "unreachable markup in 0.99.73 and should not return.")

    if not re.search(r"private bool _connRebuilding", src):
        fail("last-column", "_connRebuilding guard is missing")
        return

    rb = re.search(r"private void RebuildConnList\(\).*?\n    }\n", src, re.S)
    if not rb:
        fail("last-column", "could not locate RebuildConnList")
        return
    rb = rb.group(0)
    if "_connRebuilding = true" not in rb or "finally { _connRebuilding = false; }" not in rb:
        fail("last-column",
             "RebuildConnList does not raise _connRebuilding in a try/finally. "
             "Without finally an exception mid-refill leaves the guard raised and "
             "the inspector can never close again.")

    cs_ = re.search(r"private void ConnSelected.*?\n    }\n", src, re.S)
    if not cs_:
        fail("last-column", "could not locate ConnSelected")
        return
    if "_connRebuilding" not in cs_.group(0):
        fail("last-column",
             "ConnSelected closes the inspector without consulting "
             "_connRebuilding - the list deselects transiently every sample, so "
             "the panel would strobe once a second")


def check_header_fit():
    """Every fixed-width table column must be wide enough for its own header.

    `DESTINATIONS` in the Traffic apps table was 110px against a header needing
    108. The final S was cut mid-glyph - `DESTINATION` with a sliver - which is
    the LOCATIO failure again in a different table: a header clipped by the column
    boundary rather than trimmed by its own TextBlock, so TextTrimming never
    engages and there is no ellipsis to warn you.

    The first version of this scan found nothing, because it used 0.600em per
    character - the raw font advance. Headers carry `Tracking.Em="0.10"` on top of
    that, so the true cost is 0.700em, and twelve characters of it is 8px more
    than the guess. **Every number below is read** - font size, tracking and
    padding out of the header style, the advance out of the TTF. That is the
    difference between this check and the one that passed on a clipping header.
    """
    before = len(failures)
    ctrls = SHARED.read_text(encoding="utf-8")
    xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")

    style = re.search(r'<Style TargetType="GridViewColumnHeader">.*?</Style>', ctrls, re.S)
    if not style:
        fail("header-fit", "no GridViewColumnHeader style found")
        return
    style = style.group(0)

    def one(pat, what):
        m = re.findall(pat, style)
        if len(m) != 1:
            fail("header-fit", f"expected 1 {what} in the header style, found {len(m)}")
            return None
        return m[0]

    fs = one(r'<Setter Property="FontSize" Value="([\d.]+)" />', "FontSize")
    pad = one(r'Padding="([\d.]+),[\d.]+,([\d.]+),[\d.]+"', "Padding")
    track = one(r'gw:Tracking\.Em="([\d.]+)"', "Tracking.Em")
    if None in (fs, pad, track):
        return

    font = APP / "Fonts" / "JetBrainsMonoNerdFont-SemiBold.ttf"
    if not font.exists():
        font = APP / "Fonts" / "JetBrainsMonoNerdFont-Regular.ttf"
    try:
        adv, upm = _ttf_advance(font, ord("W"))
    except Exception as ex:
        fail("header-fit", f"could not read advance from {font.name}: {ex}")
        return

    per = float(fs) * (adv / upm + float(track))
    padding = float(pad[0]) + float(pad[1])

    # A header that only just fits is a header that clips on the next font or
    # padding change. DESTINATIONS had 1.8px and was already cut.
    MARGIN = 6.0

    worst = None
    for m in re.finditer(r'<GridViewColumn Header="([^"]+)"\s+Width="([\d.]+)"', xaml):
        head, width = m.group(1), float(m.group(2))
        need = len(head) * per + padding
        slack = width - need
        if worst is None or slack < worst[0]:
            worst = (slack, head, width, need)
        if slack < MARGIN:
            line = xaml[:m.start()].count("\n") + 1
            fail("header-fit",
                 f'MainWindow.xaml:{line} header "{head}" needs {need:.0f}px but its '
                 f"column is {width:.0f} ({slack:.1f}px slack, {MARGIN:.0f} required) - "
                 "the column boundary cuts it mid-glyph, and TextTrimming cannot "
                 "reach a header that believes it has room")

    _check_combo_fit(xaml, per_char_base=float(fs))

    if len(failures) == before and worst:
        notes.append(f"header-fit: {per:.2f}px/char incl. {track}em tracking; "
                     f'tightest is "{worst[1]}" with {worst[0]:.0f}px spare')


def _check_combo_fit(xaml, per_char_base):
    """A fixed-width ComboBox must fit its longest literal item.

    `UsageWindowCombo` was 140px against "Last 5 minutes" needing 155, and
    rendered "Last 5 minut" - cut on the t, no ellipsis. Five more were the same,
    including the popup-timeout selector, which could not show "Never
    (recommended)" - its own default.

    PROVENANCE, because these two numbers are not equally solid. The per-character
    cost is DERIVED: TableFontSize out of the shared dictionary, advance out of
    the TTF. The chrome allowance - padding plus the chevron well - is MEASURED
    from a 0.99.77 screenshot at 49px, because the ComboBox template comes from
    WPF-UI and is not in this repository to read. A measured constant is weaker
    evidence than a read one and is called out here rather than blended in; if the
    control library changes, this is the number that goes stale silently.
    """
    import xml.etree.ElementTree as ET
    NS = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
    X = "{http://schemas.microsoft.com/winfx/2006/xaml}"

    base = SHARED.read_text(encoding="utf-8")
    m = re.search(r'x:Key="TableFontSize">([\d.]+)<', base)
    if not m:
        fail("header-fit", "TableFontSize not found; cannot size ComboBox text")
        return
    size = float(m.group(1))

    try:
        adv, upm = _ttf_advance(APP / "Fonts" / "JetBrainsMonoNerdFont-Regular.ttf", ord("W"))
    except Exception as ex:
        fail("header-fit", f"could not read advance: {ex}")
        return

    per = size * adv / upm
    CHROME = 49.0   # measured, see docstring
    MARGIN = 8.0

    root = ET.fromstring(xaml)
    for cb in root.iter(NS + "ComboBox"):
        w = cb.get("Width")
        if not w:
            continue  # auto-sizes to its widest item; nothing to get wrong
        items = [i.get("Content") for i in cb.findall(NS + "ComboBoxItem") if i.get("Content")]
        if not items:
            continue  # bound at runtime; not knowable from markup
        longest = max(items, key=len)
        need = len(longest) * per + CHROME
        if float(w) - need < MARGIN:
            fail("header-fit",
                 f'ComboBox {cb.get(X + "Name") or "(unnamed)"} is {float(w):.0f}px but '
                 f'"{longest}" needs {need:.0f} - the text is cut, not ellipsised')


def check_preset_protocols():
    """No system-rule preset may name a protocol the engine cannot express.

    The engine turns a preset's Protocol string into an IP_PROTOCOL condition. A
    value it does not recognise yields **no condition at all**, and a filter with
    no protocol condition matches every protocol on its ports. So an unrecognised
    protocol does not fail - it silently WIDENS the rule, and on an allow preset
    at weight 0x0B that is a permit for everything.

    This is the same shape as trap 2.4: a value that resolves to nothing rather
    than to an error, so the failure looks like success. It is checked here
    because both halves live in this repository - the vocabulary the engine
    accepts, and every string the catalogue asks for.

    Also asserts that a preset with no ports carries a protocol, because a permit
    with neither is a permit for everything outbound.
    """
    before = len(failures)
    engine = (APP / "Services" / "Wfp" / "WfpEngine.cs").read_text(encoding="utf-8")
    cat = (APP / "Models" / "SystemRuleCatalog.cs").read_text(encoding="utf-8")

    sw = re.search(r"byte\?\s+proto\s*=\s*protocol\?\.ToUpperInvariant\(\)\s*switch\s*\{(.*?)\};",
                   engine, re.S)
    if not sw:
        fail("preset-protocol", "could not read the engine's protocol mapping")
        return
    sw = sw.group(1)

    names = set(re.findall(r'"(\w+)"\s*=>', sw))
    numeric_ok = "byte.TryParse" in sw

    for m in re.finditer(
            r'new\("(\w+)",\s*"((?:[^"\\]|\\.)*)".*?"(allow|block)",\s*'
            r'(?:true|false),\s*(?:true|false),\s*"\w+",\s*"(\w+)",\s*'
            r'(new\[\]\{[\d,\s]*\}|System\.Array\.Empty<int>\(\))\)', cat, re.S):
        key, name, category, proto, ports = m.groups()
        has_ports = "Empty" not in ports and re.search(r"\d", ports)

        if proto not in names and proto != "Any":
            if not (proto.isdigit() and numeric_ok and 0 < int(proto) < 256):
                fail("preset-protocol",
                     f'preset "{key}" asks for protocol "{proto}", which the engine '
                     "does not map. It would not error - it would drop the protocol "
                     "condition and permit EVERY protocol on those ports.")
                continue

        if not has_ports and proto == "Any":
            special = re.search(rf'new\("{key}",.*?"(?:allow|block)",\s*(true|false)', cat, re.S)
            if special and special.group(1) == "false":
                fail("preset-protocol",
                     f'preset "{key}" has neither ports nor a protocol and is not '
                     "marked Special - that is a filter with no conditions at all")

    _check_v4_v6_parity(engine)

    if len(failures) == before:
        vocab = ", ".join(sorted(names)) + (", plus raw numbers" if numeric_ok else "")
        notes.append(f"preset-protocol: every preset expressible; engine accepts {vocab}")


def _check_v4_v6_parity(engine):
    """A rule path that adds a v4 layer must add the matching v6 layer.

    `AddServiceRule` added only v4 while every other path in the same file added
    both, so every port/protocol preset was IPv4-only. `Block file sharing (SMB,
    port 445)` did not block SMB over IPv6 - and unlike a rule that fails to
    apply, it looked applied, because the v4 half worked.

    Checked structurally rather than by counting: within each rule-building
    method, a v4 ALE layer with no v6 counterpart is the defect. Counting
    occurrences file-wide would pass as soon as any method mentioned v6, which is
    exactly how this survived - the file already said `..._V6` eleven times.
    """
    for m in re.finditer(r"\n    (?:public|private)[^\n]*?\b(\w+)\([^)]*\)\s*\n?\s*\{", engine):
        name = m.group(1)
        start = m.end()
        depth, end = 1, start
        for i in range(start, len(engine)):
            if engine[i] == "{":
                depth += 1
            elif engine[i] == "}":
                depth -= 1
                if depth == 0:
                    end = i
                    break
        body = engine[start:end]

        # COUNTS, not set membership. The first version compared the set of layer
        # names in the method, which proves only that a v6 layer is mentioned
        # somewhere in it - so removing one of two v4/v6 pairs left the sets equal
        # and the check passed on the real defect. This docstring already warned
        # that file-wide counting was too coarse; the same mistake at method scope
        # is no better.
        v4 = collections.Counter(re.findall(r"(FWPM_LAYER_\w+)_V4", body))
        v6 = collections.Counter(re.findall(r"(FWPM_LAYER_\w+)_V6", body))

        # Two exemptions, both by REASON rather than by name, so a new method
        # cannot inherit one by being called something similar:
        #
        #  - it guards on AddressFamily.InterNetwork, so it only ever handles an
        #    IPv4 literal and a v6 layer would have nothing to match on;
        #  - it is a probe rather than a rule path, adding a filter only to prove
        #    the removal path works.
        if "AddressFamily.InterNetwork" in body:
            continue
        if "recovery" in body.lower() and "delete" in body.lower():
            continue
        if "SelfTest" in name or "Probe" in name or "Layers" in name:
            continue

        # A switch of independent rules must be checked case by case. Counted
        # whole, AddSystemRule hides a gap: it holds v6-only rules (Block IPv6)
        # whose surplus absorbs a missing v6 elsewhere, so deleting block_rdp_in's
        # v6 filter left the method total balanced and the check quiet. Each case
        # label is its own rule and gets its own count.
        segments = [(name, body)]
        cases = re.split(r'\n\s+case\s+"([^"]+)":', body)
        if len(cases) > 1:
            segments = [(f"{name}/{cases[i]}", cases[i + 1])
                        for i in range(1, len(cases) - 1, 2)]

        for label, seg in segments:
            s4 = collections.Counter(re.findall(r"(FWPM_LAYER_\w+)_V4", seg))
            s6 = collections.Counter(re.findall(r"(FWPM_LAYER_\w+)_V6", seg))
            for layer, n4 in s4.items():
                n6 = s6.get(layer, 0)
                # Only an UNPAIRED V4 is the defect. More v6 than v4 is deliberate -
                # "Block IPv6" adds a v6 filter with no v4 counterpart on purpose.
                # Requiring equality flagged two correct methods, which is how a
                # check earns the right to be ignored.
                if n4 > n6:
                    fail("preset-protocol",
                         f"{label} adds {layer}_V4 {n4}x but {layer}_V6 only {n6}x - "
                         "the unpaired v4 covers IPv4 only while appearing applied")


def check_reset_path():
    """The reset must delete filters before the sublayer, and clear the store.

    0.99.79 went straight to FwpmSubLayerDeleteByKey0. WFP does not cascade, so
    it returned FWP_E_IN_USE (0x8032000A) while filters still referenced the
    sublayer - and the throw happened BEFORE the store was cleared, so the reset
    aborted with both the kernel filters and the saved rules intact. The button
    says "run this before uninstalling".

    Two things are asserted, plus the precondition of the fix:

    1. The manager deletes tracked filters before calling into the engine.
    2. The engine treats FWP_E_IN_USE as a reported outcome, not an exception.
    3. Every `ulong` in the persisted model is a filter id - which is what makes
       collecting them by walking the object graph safe. If a non-filter ulong is
       ever added to the store, the sweep would hand it to FwpmFilterDeleteById0
       as though it were a filter. That is a silent, wrong deletion, so it is
       checked rather than left in a comment.
    """
    before = len(failures)
    mgr = (APP / "Services" / "FirewallManager.cs").read_text(encoding="utf-8")
    eng = (APP / "Services" / "Wfp" / "WfpEngine.cs").read_text(encoding="utf-8")
    store = (APP / "Services" / "RuleStore.cs").read_text(encoding="utf-8")
    models = (APP / "Models" / "Models.cs").read_text(encoding="utf-8")

    body = re.search(r"public bool RemoveAllFiltering\(\).*?\n    \}", mgr, re.S)
    if not body:
        fail("reset-path", "FirewallManager.RemoveAllFiltering not found, or no longer "
                           "returns a status the UI can report")
        return
    body = body.group(0)

    del_at = body.find("RemoveFilters")
    sub_at = body.find("_engine.RemoveAllFiltering")
    if del_at < 0:
        fail("reset-path", "the reset never deletes tracked filters - the sublayer "
                           "delete will fail with FWP_E_IN_USE")
    elif sub_at < 0 or del_at > sub_at:
        fail("reset-path", "the sublayer is deleted before the filters that reference "
                           "it; WFP refuses that with FWP_E_IN_USE")

    clear_at = body.find("new StoreData()")
    if clear_at < 0 or clear_at < sub_at:
        fail("reset-path", "the store is not cleared after the kernel work - an "
                           "exception in between leaves the saved rules behind")

    eng_body = re.search(r"public bool RemoveAllFiltering\(\).*?\n    \}", eng, re.S)
    if not eng_body:
        fail("reset-path", "WfpEngine.RemoveAllFiltering not found or does not report "
                           "whether the sublayer went away")
    elif "0x8032000A" not in eng_body.group(0):
        fail("reset-path", "the engine does not handle FWP_E_IN_USE (0x8032000A). An "
                           "orphaned filter would throw a raw WFP code at the user and "
                           "abort the reset.")

    # Precondition for walking the graph: no ulong may mean anything but a filter id.
    for path, text in (("RuleStore.cs", store), ("Models.cs", models)):
        for m in re.finditer(r"public\s+([\w<>,\s]*?ulong[\w<>,\s]*?)\s+(\w+)\s*\{", text):
            typ = " ".join(m.group(1).split())
            if typ not in ("List<ulong>", "Dictionary<string, List<ulong>>"):
                fail("reset-path",
                     f"{path}: '{m.group(2)}' is {typ}. The reset collects every ulong "
                     "in the store as a filter id; a ulong that is not one would be "
                     "passed to FwpmFilterDeleteById0 as though it were.")

    if len(failures) == before:
        notes.append("reset-path: filters before sublayer, store cleared, IN_USE handled")


def check_fault_suppression():
    """A suppressed exception must be narrowly identified, never a whole type.

    The WPF automation-peer KeyNotFoundException is a framework defect
    (dotnet/wpf #2152) that GunWall reaches because it refreshes its tables every
    second. It is counted as a benign fault rather than shown, which is right -
    and one edit away from being wrong. Suppressing `KeyNotFoundException`
    outright would hide GunWall's own dictionary bugs behind a counter and they
    would never be seen again.

    So: the classifier must test the exception TYPE and a WPF-internal stack
    frame, and the handler must still reach its dialog for everything else.
    """
    before = len(failures)
    app = (APP / "App.xaml.cs").read_text(encoding="utf-8")

    cls = re.search(r"private static bool IsWpfAutomationPeerFault.*?\n    \}", app, re.S)
    if not cls:
        fail("fault-suppression", "IsWpfAutomationPeerFault not found")
        return
    cls = cls.group(0)

    if "KeyNotFoundException" not in cls:
        fail("fault-suppression",
             "the classifier does not check the exception type - it would match "
             "on a stack frame alone")
    if not re.search(r"MS\.Internal\.WeakDictionary|System\.Windows\.Automation\.Peers", cls):
        fail("fault-suppression",
             "the classifier does not require a WPF-internal frame, so it would "
             "swallow GunWall's own KeyNotFoundExceptions")

    handler = re.search(r"private void OnDispatcherUnhandledException.*?\n    \}", app, re.S)
    if not handler:
        fail("fault-suppression", "OnDispatcherUnhandledException not found")
        return
    handler = handler.group(0)

    if "MessageBox.Show" not in handler:
        fail("fault-suppression",
             "the handler no longer shows anything - every unhandled UI fault "
             "would now be silent")
    if "NoteBenignFault" not in handler:
        fail("fault-suppression",
             "the suppressed fault is not counted, so it would vanish from "
             "diagnostics entirely rather than being recorded quietly")

    if len(failures) == before:
        notes.append("fault-suppression: narrow (type + WPF frame), counted, dialog intact")


def check_silent_failures():
    """No DoH failure path may return without recording why.

    The resolver reported `failures=2` across two diagnostics bundles and not one
    word about the cause, because every exception went through
    `catch { return null; }` and every non-2xx status through a bare `return
    null`. The single fact needed to diagnose "GunWall goes offline when the
    resolver is on" was thrown away at the point it was known.

    A counter without a reason is a fact nobody can act on. This asserts that
    the DoH path has no bare catch, and that each of its early returns is
    preceded by a NoteDohFailure - so a new failure mode cannot be added silently.
    """
    before = len(failures)
    src = (APP / "Services" / "DnsResolver.cs").read_text(encoding="utf-8")

    doh = re.search(r"private async Task<byte\[\]\?> ForwardDohAsync.*?\n    \}", src, re.S)
    if not doh:
        fail("silent-failure", "ForwardDohAsync not found")
        return
    body = doh.group(0)

    # A catch with no exception binding swallows the reason by construction.
    for m in re.finditer(r"catch\s*(?:when[^{]*)?\{", body):
        seg = body[m.start():m.start() + 400]
        if "NoteDohFailure" not in seg and "return null;" in seg and "ct.IsCancellationRequested" not in seg:
            fail("silent-failure",
                 "ForwardDohAsync has a catch that returns without calling "
                 "NoteDohFailure - the cause is discarded where it was known")
            break

    if "NoteDohFailure" not in body:
        fail("silent-failure", "ForwardDohAsync never records a failure reason")

    # Each early return in the DoH body should be accounted for: cancellation,
    # a retry continue, or a recorded failure.
    # Anywhere, not only at line start. The first version anchored on "\n\s*",
    # so `if (!resp.IsSuccessStatusCode) return null;` - a return mid-line, and
    # one of the two silent paths that shipped - was never even examined.
    #
    # The 300-character window is a heuristic and is stated as one: it asks
    # whether a reason was recorded NEARBY, not whether this exact path records
    # one. It catches a return that discards its cause outright, which is the
    # failure that happened twice here.
    for m in re.finditer(r"(return null;|continue;)", body):
        window = body[max(0, m.start() - 300):m.start()]
        if m.group(1) == "return null;" and "NoteDohFailure" not in window \
                and "IsCancellationRequested" not in window:
            line = src[:doh.start() + m.start()].count("\n") + 1
            fail("silent-failure",
                 f"DnsResolver.cs:{line} returns null from the DoH path with no "
                 "NoteDohFailure in the preceding lines - another silent failure")

    if "DohFailureDetail" not in src:
        fail("silent-failure", "the failure detail is recorded but never exposed")

    mw = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    if "DohFailureDetail" not in mw:
        fail("silent-failure",
             "diagnostics report the failure count without the reason - which is "
             "the state that made two bundles unreadable")

    # A field describing a live state must be cleared when that state ends. The
    # resolver's ListenerStatus was set on start and never on stop, so a bundle
    # read "dnsRunning=False" beside "listening=[127.0.0.1:53 and [::1]:53]".
    # Contradicting yourself in one export costs more than the field is worth.
    stop = re.search(r"public void Stop\(\).*?\n    \}", src, re.S)
    if not stop:
        fail("silent-failure", "DnsResolver.Stop not found")
    elif "ListenerStatus" not in stop.group(0):
        fail("silent-failure",
             "Stop() does not clear ListenerStatus, so diagnostics will report a "
             "listener that is no longer listening alongside dnsRunning=False")
    elif "SelfCheck" not in stop.group(0):
        fail("silent-failure",
             "Stop() does not clear SelfCheck - a stopped resolver would keep "
             "reporting that it answers on loopback")

    # Binding proves nothing. Two separate faults shipped behind a socket that
    # bound successfully and answered nobody, and both were found only because
    # someone pressed a button by hand.
    if not re.search(r"private async Task SelfCheckAsync", src):
        fail("silent-failure",
             "the resolver has no automatic self-check - it can bind, answer "
             "nothing, and report a healthy listener")
    else:
        start = re.search(r"public .*? Start\(.*?\n    \}", src, re.S)
        if start and "SelfCheckAsync" not in start.group(0):
            fail("silent-failure",
                 "SelfCheckAsync exists but Start() never runs it, so it proves "
                 "nothing unless someone calls it by hand - which is the state "
                 "that let two resolver faults ship")
        if "SelfCheck" not in mw:
            fail("silent-failure",
                 "the self-check result never reaches diagnostics, so a bundle "
                 "still cannot say whether the resolver works")

    # Enforcement posture is the control variable for every test in this project.
    # A bundle that does not state it cannot separate "GunWall dropped this" from
    # "something else did", which is exactly the question three sessions have been
    # unable to answer from the logs alone.
    if "Posture: protection=" not in mw:
        fail("silent-failure",
             "diagnostics do not record the enforcement posture - every path test "
             "and self-check result in the bundle is uninterpretable without it")
    fm = (APP / "Services" / "FirewallManager.cs").read_text(encoding="utf-8")
    setter = re.search(r"public void SetStrictMode\(bool enabled\).*?\n    \}", fm, re.S)
    if setter and "DiagnosticLog.Log" not in setter.group(0):
        fail("silent-failure",
             "turning protection on or off leaves no trace in the log, so the "
             "isolation test cannot be verified after the fact")

    # A domain-derived block is a global /32 above every app rule. Installing one
    # on a shared CDN address takes down every other service behind it, for every
    # application, permanently, with nothing on screen saying why.
    fm2 = (APP / "Services" / "FirewallManager.cs").read_text(encoding="utf-8")
    blk = re.search(r"public bool AddDomainReactiveBlock.*?\n    \}", fm2, re.S)
    if not blk:
        fail("silent-failure", "AddDomainReactiveBlock not found")
    elif "NameCountForIp" not in blk.group(0):
        fail("silent-failure",
             "AddDomainReactiveBlock installs a global address block without "
             "checking whether the address is shared - one tracker name then "
             "blocks every other service on that CDN edge")

    obs = (APP / "Services" / "DnsObservations.cs").read_text(encoding="utf-8")
    if "NameCountForIp" not in obs:
        fail("silent-failure",
             "DnsObservations cannot report how many names an address served, so "
             "sharing cannot be detected")

    # An error worth interrupting someone for is worth recording. A "Could not
    # allow" dialog appeared on a machine whose bundle then read "Errors this
    # session: 0 distinct, 0 total".
    aw = (APP / "AlertWindow.xaml.cs").read_text(encoding="utf-8")
    allow = re.search(r"private void Allow_Click.*?\n    \}", aw, re.S)
    if allow and "MessageBox.Show" in allow.group(0) and "LogException" not in allow.group(0):
        fail("silent-failure",
             "Allow_Click shows an error dialog without logging it - the user sees "
             "a failure the diagnostics bundle denies happened")

    # "Run this before uninstalling" has to mean it. Filters are not the only
    # thing GunWall changes: it writes the hosts file and it can repoint adapter
    # DNS, and neither was being undone.
    fm3 = (APP / "Services" / "FirewallManager.cs").read_text(encoding="utf-8")
    reset = re.search(r"public bool RemoveAllFiltering\(\).*?\n    \}", fm3, re.S)
    if reset:
        r = reset.group(0)
        # SetBlockedDomains specifically, not "HostsFileService" anywhere: the
        # reset also calls FlushDns on the same class, which satisfied a substring
        # test while the clearing call was gone. Seventh time this session a check
        # matched the neighbourhood instead of the thing.
        if "SetBlockedDomains" not in r:
            fail("reset-path",
                 "the reset does not clear the hosts file - domains stay blocked "
                 "with nothing installed to explain or undo it")
        # Tracked ids cannot reach an orphan. Without a sweep, "remove all
        # filtering" leaves persistent filters enforcing forever with nothing able
        # to name them - which is what FWP_E_IN_USE reports.
        if "FindAllSublayerFilterIds" not in r:
            fail("reset-path",
                 "the reset never sweeps orphaned filters, so it cannot return the "
                 "machine to Windows defaults - anything whose id was lost keeps "
                 "filtering permanently")
        eng2 = (APP / "Services" / "Wfp" / "WfpEngine.cs").read_text(encoding="utf-8")
        # A DECLARATION, not the name anywhere: the method that replaced this
        # approach explains in a comment why FwpmFilterEnum0 was avoided, and the
        # first version of this check failed on that comment. Ninth time this
        # session a check matched the neighbourhood instead of the thing.
        nat = (APP / "Services" / "Wfp" / "WfpNative.cs").read_text(encoding="utf-8")
        if re.search(r"extern\s+\w+\s+FwpmFilterEnum0", nat + eng2):
            fail("reset-path",
                 "FWPM_FILTER0 is being marshalled by hand. Its layout has not been "
                 "verified against win32metadata and cannot be tested here - trap 2.5 "
                 "was exactly this and it killed a process silently.")
        # Recoverable is not the same as impossible. Without a startup reconcile,
        # an orphan lives for the life of the installation; one machine reached 843
        # against 4 tracked.
        # The DECLARATION. Testing for the bare name passed when the method was
        # renamed to ReconcileOrphanFiltersX, because the old name is a substring
        # of the new one. Tenth time this session.
        if not re.search(r'public int ReconcileOrphanFilters\(', fm3):
            fail('reset-path',
                 'no startup reconcile - orphaned filters would accumulate for the '
                 'life of the installation instead of dying at the next launch')
        # Deleting on the strength of an empty store is how 0.99.92 disarmed a
        # protected machine. Both guards are asserted because either alone would
        # have prevented it and neither alone is enough.
        rec = re.search(r'public int ReconcileOrphanFilters\(\).*?\n    \}', fm3, re.S)
        if rec:
            if 'tracked.Count == 0' not in rec.group(0):
                fail('reset-path',
                     'the reconcile does not refuse when nothing is tracked - it '
                     'would read an unloaded store as proof that every live filter '
                     'is an orphan and delete the working set')
        # A rule holding filters is enforcing something; only inert ones may go.
        prune = re.search(r'public int PruneDeadRules\(\).*?\n    \}', fm3, re.S)
        if prune and 'FilterIds.Count == 0' not in prune.group(0):
            fail('reset-path',
                 'PruneDeadRules does not require the rule to hold no filters - it '
                 'could drop a rule that is still enforcing, or one whose path is '
                 'briefly unreachable')
        if rec:
            if 'ReconcileReady' not in rec.group(0):
                fail('reset-path',
                     'the reconcile does not check readiness, so it can run before '
                     'the store has loaded')
        # ORDERING, checked positionally. This is the one that would have caught
        # 0.99.93: the reconcile was fired thirty lines ABOVE the Initialize() call
        # that loads the store, in the same method, under a comment claiming the
        # store was already loaded. The guards held, so nothing broke - but the
        # feature never once ran, and a check asserting only that both exist saw
        # nothing wrong for two releases.
        if 'ReconcileOrphanFilters' in mw and '_firewall.Initialize()' in mw:
            if mw.index('_firewall.Initialize()') > mw.index('ReconcileOrphanFilters'):
                fail('reset-path',
                     'the startup reconcile is fired before _firewall.Initialize(), '
                     'which is where the store is loaded - it would read an empty '
                     'store and decline to act on every run')
        # And readiness must be owned by the object whose state it describes.
        if 'MarkReconcileReady' in fm3:
            fail('reset-path',
                 'ReconcileReady is set by an external caller, so a caller can - and '
                 'did - claim readiness before the store was loaded')
        # NOT over-escaped. Written through a heredoc the first time, which turned
        # the escapes into literal backslashes, so it matched nothing and the check
        # skipped silently instead of failing. Eleventh time this session. The None
        # branch below is what makes that impossible to repeat.
        init = re.search("public void Initialize" + re.escape("()") + ".*?\n    }", fm3, re.S)
        if init is None:
            fail("reset-path", "could not locate FirewallManager.Initialize()")
        elif "ReconcileReady = true" not in init.group(0):
            fail('reset-path',
                 'Initialize() does not mark reconcile readiness, so readiness is '
                 'not tied to the store actually being loaded')
        if 'ReconcileOrphanFilters' not in mw:
            fail('reset-path',
                 'ReconcileOrphanFilters exists but nothing calls it at startup, so '
                 'it only runs if someone asks - which is the state that let 843 '
                 'orphans accumulate')
        if "RestoreAdapters" not in r:
            fail("reset-path",
                 "the reset does not restore adapter DNS - the PC would keep "
                 "pointing at a resolver that is no longer running")

    # OFF must mean nothing is enforced. It used to remove the baseline and the
    # filters of ALLOWED rules only - so every explicitly blocked app kept its
    # block filters, and lockdown, blocklists, domain blocks, system rules and
    # blocked services were never touched. Turning the firewall off removed the
    # permits and left the denials.
    off = re.search(r"public void SetStrictMode\(bool enabled\).*?\n    \}", fm3, re.S)
    if off:
        o = off.group(0)
        if "CollectFilterIds" not in o:
            fail("reset-path",
                 "turning protection off does not sweep every filter collection - "
                 "blocked apps, system rules and blocklists would stay enforced on "
                 "a kernel that keeps filtering after the app closes")
        if re.search(r"Where\(r => r\.Status == AppStatus\.Allowed\)[\s\S]{0,200}RemoveFilters", o):
            fail("reset-path",
                 "protection-off still removes filters only for Allowed rules - the "
                 "Where() skips exactly the rules whose filters deny traffic")

    if "Rule targets:" not in mw:
        fail("silent-failure",
             "diagnostics never report rules pointing at a missing executable - "
             "they list as Allowed, hold no filters, and throw when re-applied")

    if len(failures) == before:
        notes.append("silent-failure: causes recorded, resolver self-checks, posture logged, "
                     "shared addresses spared, orphan rules reported")


def check_recovery_path():
    """The --unblock run must start nothing and identify itself.

    It removes filters and exits in about a tenth of a second. Anything that
    starts asynchronously behind it is still starting when the process dies -
    which, from the outside, is indistinguishable from a crash.

    That is not hypothetical: the first version tripped the DNS observer's own
    crash guard on every run. The observer set its "starting" marker, the process
    exited, and the next real launch refused to start DNS watching and told the
    user to go toggle a setting. A recovery tool must not leave the thing it
    recovered in a worse state than it found it.
    """
    before = len(failures)
    app = (APP / "App.xaml.cs").read_text(encoding="utf-8")

    body = re.search(r"private static int RunEmergencyUnblock\(\).*?\n    \}", app, re.S)
    if not body:
        fail("recovery", "RunEmergencyUnblock not found")
        return
    body = body.group(0)

    if "HeadlessRecovery" not in body:
        fail("recovery",
             "the recovery run does not flag itself, so background subsystems start "
             "behind it and are killed mid-start when it exits")
    if "Emergency unblock" not in body:
        fail("recovery",
             "the recovery run is not named in the log - its lines are identical to "
             "a button press, distinguishable only by what is missing")

    # The flag has to be set before anything is constructed, or it is decoration.
    fi = body.find("HeadlessRecovery")
    ni = body.find("new FirewallManager")
    if fi >= 0 and ni >= 0 and fi > ni:
        fail("recovery",
             "HeadlessRecovery is set after the manager is constructed - too late "
             "for anything that starts during construction")

    dns = (APP / "Services" / "DnsEventMonitorService.cs").read_text(encoding="utf-8")
    st = re.search(r"public bool Start\(\).*?\n    \}", dns, re.S)
    if st and "HeadlessRecovery" not in st.group(0):
        fail("recovery",
             "the DNS observer does not check HeadlessRecovery, so a recovery run "
             "still starts it and still trips its crash guard")

    if len(failures) == before:
        notes.append("recovery: --unblock starts nothing and names itself in the log")


def check_version_consistency():
    files = {
        "GunWall.csproj":            (APP / "GunWall.csproj",              r"<Version>([0-9.]+)</Version>"),
        "app.manifest":              (APP / "app.manifest",                r'assemblyIdentity version="([0-9.]+)"'),
        "MainWindow.xaml.cs":        (APP / "MainWindow.xaml.cs",          r'GunWall v([0-9.]+)'),
        "Services/UpdateService.cs": (APP / "Services" / "UpdateService.cs", r'CurrentVersion = "([0-9.]+)"'),
    }
    seen = {}
    for label, (path, pat) in files.items():
        found = re.findall(pat, path.read_text(encoding="utf-8"))
        if len(found) != 1:
            fail("version", f"{label}: expected exactly 1 version match, found {len(found)}")
            continue
        # removesuffix, NOT rstrip: rstrip takes a SET of characters, so
        # "0.99.50.0".rstrip(".0") strips every trailing dot and zero and yields
        # "0.99.5". Latent until the first version ending in zero, which is
        # exactly the kind of bug a check is supposed to catch rather than be.
        seen[label] = found[0].removesuffix(".0") if label == "app.manifest" else found[0]
    vals = set(seen.values())
    if len(vals) > 1:
        fail("version", f"versions disagree: {seen}")
    elif vals:
        notes.append(f"version: {vals.pop()} in all four files")


def main():
    check_theme_parity()
    check_late_binding()
    check_merge_order()
    check_duplicate_names()
    check_colour_home()
    check_dead_keys()
    check_local_calls()
    check_element_references()
    check_binding_override()
    check_font_families()
    check_no_code_pack_fonts()
    check_hint_width()
    check_graph_axis()
    check_table_last_column()
    check_header_fit()
    check_preset_protocols()
    check_reset_path()
    check_recovery_path()
    check_fault_suppression()
    check_silent_failures()
    check_version_consistency()

    for n in notes:
        print(f"  ok   {n}")
    if failures:
        print()
        for f in failures:
            print(f"  FAIL {f}")
        print(f"\n{len(failures)} failure(s)")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
