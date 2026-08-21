<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/png/banner-slim-dark.png">
  <img src="branding/png/banner-slim-light.png" alt="GunWall" width="100%">
</picture>

</div>

# Contributing to GunWall

Thanks for your interest. GunWall is a firewall, so the bar for changes is
deliberately higher than for a typical desktop app: a mistake here can cut
someone off the network or leave them believing they are protected when they are
not.

Security vulnerabilities go through [`SECURITY.md`](SECURITY.md), **not** the
public issue tracker.

## Building

Prerequisites: **Visual Studio 2022** (17.8+) with the **.NET desktop development** workload, or the standalone [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

**Visual Studio**

1. Open `GunWall.sln`.
2. Set configuration **Release**, platform **x64**.
3. **Build → Build Solution** (`Ctrl+Shift+B`).
4. The executable appears in `src/GunWall/bin/Release/net8.0-windows/GunWall.exe`.

**Command line**

```powershell
dotnet build GunWall.sln -c Release
```

Single self-contained executable:

```powershell
dotnet publish src/GunWall/GunWall.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true
```


## Getting set up

**Requirements**

- Windows 10 (2004+) or Windows 11, 64-bit
- Visual Studio 2022 (17.8+) with the *.NET desktop development* workload, or
  the standalone [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Administrator rights to run — the Windows Filtering Platform will not accept
  filters otherwise

**Build**

```powershell
dotnet build GunWall.sln -c Release
```

The executable lands in `src/GunWall/bin/Release/net8.0-windows/GunWall.exe`.

Read [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) before changing anything in
`Services/Wfp` — it explains the sublayer model, filter weights, and why removal
is done the way it is.

## Non-negotiables

**Nothing third-party in the filtering path.** GunWall has exactly one package
dependency — [WPF UI](https://github.com/lepoco/wpfui), MIT, for the interface —
and that is where it ends. The WFP engine, the rule evaluator, the DNS resolver
and the rule store use the .NET base class library and direct Win32 P/Invoke
only. A pull request that adds a dependency to any of those will not be merged,
whatever it does; the part of this program that decides whether traffic lives or
dies has to stay readable end to end. Additions to the interface layer are
discussable, but the bar is high: the single portable executable is still a
goal, and every package is a supply chain someone has to trust.

**No telemetry.** GunWall makes no outbound request the user did not ask for.

**A check must be shown to fail before it is trusted.** Reintroduce the defect
it was written for, watch the check fail, remove the defect, watch it pass. This
project has shipped three checks that could not fail — one whose exclusion rule
matched everything, one that skipped misses silently, and one whose own string
handling was wrong. A check never demonstrated against its own defect is a guess
that counts as coverage. [`docs/HANDOVER.md`](docs/HANDOVER.md) lists them.

**Every change to shipped code gets a new version; documentation alone does not.**

Bump whenever a compiled file changes — a fix, a feature, a one-line guard. The
version is how a bug report is tied to a build, and code that ships under a number
already used makes every later report begin with a question nobody can answer.

Do not bump for documentation, artwork or changes confined to `tools/`. Those ride
along with the next release that contains code.

If in doubt, the test is simple: **did a file that gets compiled change?**

Four files carry the version and must agree:
`src/GunWall/GunWall.csproj`, `src/GunWall/app.manifest`, the About string in
`MainWindow.xaml.cs`, and `CurrentVersion` in `Services/UpdateService.cs`.
`tools/checks/check_theme.py` fails the build if they disagree.

Nothing else should reference a version. The README badge states the stage, not
the number, and the roadmaps describe what is open rather than which release it
belongs to — because a version attached to a plan is a promise about sequencing,
and this project reorders freely. `CHANGELOG.md` is the one place versions and
dates belong.

**Everything must be removable.** Any filter GunWall installs must be removable
again, including after a crash. Persist filter identifiers, and make removal
idempotent — deleting something that is already gone is not an error.

**Fail safe, not silent.** A filter that cannot be installed must say so.
Swallowing an error so the interface looks fine is worse than showing the
failure: the user believes they are protected when they are not. This is not
hypothetical — three wrong WFP identifiers hid behind silent failure handling
for months before a self-test surfaced them.

## Working with WFP

The kernel interop is where mistakes are most expensive and least visible.

- **Verify every GUID against the Windows SDK headers**, not against memory or
  another project's copy. A wrong identifier can be a valid-but-different layer,
  which installs successfully and filters the wrong thing.
- **Run Settings → Diagnostics → *Verify kernel layers*** after touching layers
  or conditions. It probes each one and reports what the kernel accepts.
- **New layers are opt-in first.** Ship behind a toggle that is off by default,
  with a description saying plainly when *not* to enable it.
- **Test the removal path**, not just the install path, and confirm filter
  counts in the diagnostics log.

## Testing

There is no test project; logic that can be tested without Windows is verified
with small standalone harnesses, and everything else is validated on real
hardware with a diagnostics export.

- **Pure logic gets tests.** Parsers, evaluators, and classifiers should be
  side-effect-free and covered — the DNS message parser, the access-rule
  evaluator, the IP scope classifier, and the usage aggregation all are.
- **Kernel and UI behaviour gets a hardware run.** Build, exercise the feature,
  export diagnostics, and check the relevant counters actually moved. "It
  compiled" is not evidence.
- **Include adversarial input** for anything that parses bytes off the network:
  truncated buffers, compression-pointer loops, and impossible lengths.

## Code style

- C# 12, `net8.0-windows`, nullable enabled, Release/x64.
- Follow the conventions already in the file you are editing rather than
  introducing new ones.
- Comment the *why*, not the *what*. Struct offsets, filter weights, and
  ordering constraints deserve an explanation; a property setter does not.
- Keep the WFP facade small and honest. Features belong in services, not in the
  interop layer.

## Pull requests

1. One logical change per pull request.
2. Say what you tested and on which Windows build. If it touches WFP, include
   the *Verify kernel layers* result.
3. Update [`CHANGELOG.md`](CHANGELOG.md) under a new version heading.
4. Update the documentation if behaviour changed — stale docs on a security tool
   are a defect in their own right.

## Reporting bugs

Open an issue with the GunWall version, your Windows build, what you expected,
what happened, and a diagnostics export if you can (Settings → Diagnostics →
*Export diagnostics*). Review the bundle before attaching it; it contains your
rules, adapters, and recent network activity.
