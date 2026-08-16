# GunWall — Null Cell identity v1.0

A 4x4 modular grid: 15 cells filled, one held blank (zero knowledge), one red (the packet
under inspection — zero trust). Geometry only. No gradients, no radius, no bevels.

## Contents

svg/
  mark-color.svg              Primary mark, ink + signal red. Use this by default.
  mark-reversed.svg           For dark grounds (#201E1D or darker).
  mark-black.svg              Single-ink black (print, fax, engraving, embroidery).
  mark-white.svg              Single-ink white knockout.
  mark-compact-color.svg      3x3 compact mark — use BELOW 32px.
  mark-compact-reversed.svg   Compact, dark grounds.
  app-icon.svg                512 square, ink field, reversed mark, full bleed.

png/
  mark-color-1024.png / -256.png       transparent background
  mark-reversed-1024.png                transparent background
  mark-black-512.png / mark-white-512.png
  app-icon-1024 / -512 / -192.png
  favicon-64 / -32 / -16.png            compact mark
  lockup-horizontal.png                 mark + wordmark + tagline, light
  lockup-horizontal-reversed.png        same, dark ground
  lockup-stacked.png                    mark above wordmark, flush left

## Colors

  Signal Red   #EC3013   inspected cell, primary action only
  Ink          #201E1D   the wall, all type
  Ground       #F3F2F2   page, light lockups

Never introduce a third color into the mark.

## Type

Archivo (Google Fonts). Wordmark: Archivo 800, ALL CAPS, letter-spacing -0.045em.
Tagline: Archivo 400, ALL CAPS, letter-spacing 0.3em, neutral gray.
Tagline copy: "Nothing trusted · Nothing known"

## Geometry & clear space

Drawn on a 96-unit square: cells of 21 units, gutters of 4.
Red cell: row 1, column 3. Void cell (2px outline): row 3, column 2.
Clear space on all four sides = one cell (21 units, 22% of mark width).
Nothing enters the clear space — no type, rule, or container edge.

## Minimum sizes

  Full 4x4 mark    32px / 10mm
  Compact 3x3      16px / 5mm
Below 32px on screen, switch to the compact mark.

## Favicon

  <link rel="icon" href="/favicon-32.png" sizes="32x32">
  <link rel="icon" href="/mark-compact-color.svg" type="image/svg+xml">
  <link rel="apple-touch-icon" href="/app-icon-192.png">

## Don't

  - round the cells
  - apply gradients, glows, shadows, or a third color
  - fill the void cell or remove the red cell
  - center, italicise, or loosely letterspace the wordmark
  - rotate the mark, or place it on a busy photograph
