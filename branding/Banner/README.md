# GunWall — README banners

Seven PNGs, exported at 2× (retina) so they stay sharp on GitHub. Listed size is the
**display** size — put that in the `width` attribute and the browser downscales cleanly.

| File | Display size | Use |
| --- | --- | --- |
| `banner-hero-light.png` | 1280 × 300 | README top banner, light theme |
| `banner-hero-dark.png` | 1280 × 300 | README top banner, dark theme |
| `social-preview-light.png` | 1280 × 640 | GitHub social preview (Settings → Social preview) |
| `social-preview-dark.png` | 1280 × 640 | same, if you prefer the dark card |
| `banner-slim-light.png` | 1280 × 160 | compact strip, light theme |
| `banner-slim-dark.png` | 1280 × 160 | compact strip, dark theme |
| `poster-square-red.png` | 640 × 640 | docs cover, social post, sponsor card |

## Drop-in markup

**Theme-aware banner** — GitHub swaps these automatically with the reader's theme:

```html
<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/banner-hero-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="docs/banner-hero-light.png">
    <img src="docs/banner-hero-light.png" alt="GunWall — Guard Your Net Firewall" width="100%">
  </picture>
</p>
```

**Single banner**, simplest version:

```markdown
![GunWall — Guard Your Net Firewall](docs/banner-hero-dark.png)
```

The social preview is not a README image — upload it under
**Settings → General → Social preview**. It's what shows when the repo is linked on
X, Slack, Discord or Google.

## Notes

- Set at Archivo 800 on the brand's own palette: `#201e1d` ink, `#f3f2f2` ground,
  `#ec3013` accent — the same values as `gunwall-logo/svg/mark-color.svg`, so a banner and
  the logo files sit together without a colour shift.
- Flat, zero corner radius, 2px rules. The red poster is the one place the accent runs as a
  field; everywhere else it's ink on ground with red used sparingly.
- The tagline is set verbatim: **Guard Your Net Firewall**.
- Source: `GunWall README Banners.dc.html`. Open it in a browser to re-export or edit — each
  frame is marked with `data-shot`, and the frames are the artwork at 1:1.
