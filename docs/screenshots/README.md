# Screenshots

Captures used in the project README. Refresh them when the interface changes
materially — a stale screenshot is a small dishonesty.

| File | Screen | Theme |
|---|---|---|
| `demo.gif` | GunWall in use: an app blocked, the approval prompt, allowed, blocked again | light |
| `overview-light.png` / `overview-dark.png` | Overview | both |
| `applications-light.png` / `applications-dark.png` | Applications | both |
| `traffic-light.png` / `traffic-dark.png` | Traffic | both |
| `security-light.png` / `security-dark.png` | Security & Privacy | both |
| `rules-light.png` / `rules-dark.png` | Rules | both |
| `dns-light.png` / `dns-dark.png` | DNS resolver | both |
| `activity-light.png` | Activity | light |
| `packet-log-light.png` | Packet log | light |

Captured on 0.99.174 (October 2026).

**Capturing.** Full window, 1400 px wide or larger, on a machine with real
traffic — an empty dashboard sells nothing. Avoid anything identifying: check
the process list, host names, the browser's address bar and bookmarks, and any
local network addresses before publishing. The Connections page shows this PC's
own LAN or VPN address in the LOCAL column, so it is not used here.

**The demo.** Recorded at about 24 frames per second and published at 6, 960 px
wide, to keep it near 6.5 MB — large GIFs load slowly on GitHub. Frames where the
browser's history drop-down was open were blurred before publishing.

Each pair is served through a `<picture>` element so the README shows whichever
matches the reader's theme.
