# Security Policy

GunWall is a firewall. A defect in it can expose the machine it is meant to
protect, so security reports are treated as the highest priority in this
project.

## Supported versions

GunWall is pre-1.0 and under active development. **Only the latest release is
supported.** Fixes are made on the current version rather than backported; if
you are running an older build, update before reporting.

## Reporting a vulnerability

**Please do not open a public issue for a security vulnerability.**

Report it privately through GitHub's
[Security Advisories](https://github.com/ox1d3x3/gunwall/security/advisories/new)
form, which is visible only to the maintainer until a fix is published.

Please include, as far as you can:

- What the issue is and why it matters
- The GunWall version and Windows build
- Steps to reproduce, or a proof of concept
- A diagnostics export (Settings → Diagnostics → *Export diagnostics*) if the
  issue is reproducible at runtime — review it first and remove anything you
  would rather not share

You will get an acknowledgement as soon as the report is read. Since this is a
single-maintainer project rather than a funded team, fixes are made as quickly
as is practical rather than to a fixed SLA. Credit is given in the changelog
unless you prefer otherwise.

## What is in scope

Anything that causes GunWall to fail at its job or to weaken the system:

- A filter that does not block what the interface says it blocks, or that
  silently fails to install
- Bypasses of Zero-Trust mode, lockdown, network scopes, or per-app access rules
- Incorrect WFP layer or condition identifiers, or filters that outrank or
  weaken protections
- Filters left behind after removal, uninstall, or a crash
- Privilege escalation, or a way for an unprivileged process to alter GunWall's
  rules or stored configuration
- Tampering with the rule store, backups, or the hosts-file integration
- Leaks of data GunWall promises to keep local, including DNS queries escaping
  unencrypted when Secure DNS is configured fail-closed

## What is out of scope

- **Antivirus false positives.** A firewall performs the same low-level
  operations malware does; unsigned builds are sometimes flagged heuristically.
  See the README for context.
- **The absence of code signing and installer hardening.** These are known gaps,
  tracked openly in the roadmap toward 1.0.
- **The single elevated process model.** GunWall does not yet split privileges
  into a service. This is a known architectural limitation, not an undisclosed
  flaw.
- Findings that require an attacker who already has Administrator rights, since
  such an attacker can disable any user-mode firewall.
- Reports produced by automated scanners with no demonstrated impact.

## Design commitments

These are the properties GunWall intends to hold. A demonstrated break in any of
them is a valid security report:

1. **No telemetry.** The only outbound requests are ones the user asked for:
   reverse-DNS lookups, optional VirusTotal hash checks, blocklist downloads,
   and DNS forwarding.
2. **Local-only storage.** Rules and settings stay in a folder on the machine,
   in plain readable JSON.
3. **Explicit actions only.** Every filter corresponds to something the user
   turned on. A fresh install changes nothing until protection is enabled.
4. **Clean removal.** Every persistent filter can be removed, including after a
   crash — the sublayer is deleted by key rather than relying on a stored list.
5. **No third-party packages.** The dependency surface is the .NET base class
   library and Win32, so the supply chain stays auditable.
