# GunWall Console — design handoff

Everything needed to build the design in WPF without rendering the HTML.

```
SPEC.md                  the contract: tokens, metrics, states, motion, copy rules
screenshots/dark/        16 PNGs — 15 screens at 1440×1000 + the state gallery
screenshots/light/       the same 16, light theme
source/                  the live design (open the .dc.html in a browser)
```

## Screens

| # | File | Screen |
| --- | --- | --- |
| 01 | `01-dashboard.png` | Overview |
| 02 | `02-applications.png` | Applications |
| 03 | `03-connections.png` | Connections + inspector |
| 04 | `04-traffic.png` | Traffic |
| 05 | `05-dns-resolver.png` | DNS resolver |
| 06 | `06-windows-services.png` | Windows services |
| 07 | `07-network-scan.png` | Network scan (results) |
| 08 | `08-packet-log.png` | Packet log |
| 09 | `09-rules.png` | Rules |
| 10 | `10-security-privacy.png` | Security & privacy |
| 11 | `11-activity.png` | Activity |
| 12 | `12-alerts.png` | Alerts |
| 13 | `13-settings.png` | Settings |
| 14 | `14-connection-prompt.png` | First-connection approval prompt |
| 15 | `15-command-palette.png` | ⌘K command palette |
| 16 | `16-state-gallery.png` | Control states, table lifecycle states, motion table |

All screens are 1440 × 1000 at 1:1 (no scaling, no retina doubling), so a pixel in the PNG is a
CSS pixel in the design. The gallery is 1440 × 2861.

## Reading order

1. `16-state-gallery.png` — the states a static screen can't show.
2. `SPEC.md` §2 (tokens) and §5 (component metrics) — the two tables you'll build from.
3. The screens, in order.

## The live design

`source/GunWall Console v2.dc.html` opens in any browser (keep `support.js` beside it) and is
interactive: click through all 13 screens, toggle the firewall, select a connection, press ⌘K.
`source/GunWall State Gallery.dc.html` has a theme switch in its top-right corner.

Both files are the source of truth for anything this package doesn't state.
