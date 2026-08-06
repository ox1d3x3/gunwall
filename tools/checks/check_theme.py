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
    "Skeleton":       "0.99.46 table lifecycle states",
    "FillUp":         "0.99.47 chart area fills",
    "FillDown":       "0.99.47 chart area fills",
    "SurfacePressed": "0.99.45 table row states",
    "FocusRing":      "0.99.49 focus rings",
    "InfoText":       "0.99.45 neutral pill text",
    "StatFontSize":   "0.99.47 primary stat, 30px",
    "IconCheck":      "0.99.48 verdict rows in the table system",
    "TableFontSize":  "0.99.45 table cell, 12.5px",
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
LIBRARY_KEYS = {"PaneFluentButtonHeight", "PaneFluentButtonWidth"}

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
    if not any(f.startswith("[colour-home]") for f in failures):
        notes.append("colour-home: no colour defined outside the palettes")


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
    notes.append("element-ref: not checked here - covered by the Roslyn pass (CS0103)")


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
        seen[label] = found[0].rstrip(".0") if label == "app.manifest" else found[0]
    vals = {v.removesuffix(".0") if v.count(".") == 3 else v for v in seen.values()}
    if len(vals) > 1:
        fail("version", f"versions disagree: {seen}")
    elif vals:
        notes.append(f"version: {vals.pop()} in all four files")


def main():
    check_theme_parity()
    check_late_binding()
    check_colour_home()
    check_dead_keys()
    check_element_references()
    check_binding_override()
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
