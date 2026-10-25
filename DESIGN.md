# Kinetic — the design system behind Hypt & Vyu

**Kinetic** is a dark-first, motion-first design system. One set of tokens, components and motion rules
shared by two products; only the accent changes. The live, interactive sheet is
[`docs/design.html`](docs/design.html) (published on the project site), and the WPF implementation lives in
`src/*/Kinetic/` (`Tokens.xaml`, `Controls.xaml`, `Motion.cs`).

## Principles

1. **Content is loud, chrome is quiet.** Clips and video carry the color; the UI is near-black glass.
2. **Motion explains.** Everything enters with the same *rise*; presses squash, toggles spring. Nothing waits more than half a second.
3. **One accent per product.** Hypt = *Blaze → Coral* on charcoal, Vyu = *Amethyst → Lilac* with white. Accents are gradients, used sparingly.
4. **Native and light.** System fonts and icons (Segoe UI Variable, Segoe Fluent Icons), no web view, layers that stop drawing when hidden.
5. **Respect the user.** Honour “Animation effects: off”, keep contrast ≥ 4.5:1 for text, never steal focus from a game.

## Color

| Token | Value | Use |
|---|---|---|
| `Bg0` | `#141418` | app void, home backdrops |
| `Bg1` | `#1E1E24` | window, inputs — the Hypt brand charcoal |
| `Bg2` | `#25252C` | cards |
| `Bg3` | `#2E2E36` | buttons, chips, elevated |
| `Bg4` | `#393942` | hover, tracks |
| `Line` / `Line2` | `#FFFFFF` @ 8 % / 15 % | hairlines, borders |
| `Text1…4` | `#F5F5F7` `#B4B4C0` `#8A8A96` `#5C5C68` | primary → disabled |
| **Hypt** `Accent1 → Accent2` | `#FF4500 → #FF6F59` | *Blaze → Coral* (brand sheet: `#FF4500` `#FF6F59` `#1E1E24`) |
| **Vyu** `Accent1 → Accent2` | `#7C3AED → #B07CFF` | *Amethyst → Lilac*, paired with white (the play button is white with a purple glyph) |
| `Success` `Warning` `Danger` `Info` `Live` | `#2EE59D` `#FFC23D` `#FF4D5E` `#4DA3FF` `#FF2E4D` | status |

Gradients always run 135° (top-left → bottom-right) for fills and left → right for bars.
The **aurora** is a radial wash of the accent at 25 % → 0 behind hero areas.

## Type

Segoe UI Variable (*Display* for headings, *Text* for UI), Cascadia Mono for timecodes.

| Style | Size / weight |
|---|---|
| Display | 38–46 / Bold, tight tracking |
| H1 | 26 / SemiBold |
| H2 | 20 / SemiBold |
| H3 | 16 / SemiBold |
| Body | 14 / Regular |
| Caption | 12.5 / Regular, `Text3` |
| Overline | 11 / Bold, uppercase, `Text3` or accent |

## Space, shape, depth

- 4-pt grid: 2 · 4 · 8 · 12 · 16 · 20 · 24 · 32 · 40 · 48 · 64.
- Radius: XS 6 · S 10 (buttons, inputs) · M 14 · L 20 (cards) · XL 28 (sheets) · Pill.
- Elevation: `Shadow1` (14 blur, 2 down, 35 %) · `Shadow2` (32 blur, 8 down, 50 %) · `Glow` (accent, 28 blur, 55 %) for the primary action only.

## Motion

| Token | Duration | Curve |
|---|---|---|
| Instant | 90 ms | linear — press down |
| Fast | 160 ms | Out — hovers, fades |
| Base | 240 ms | Out — state changes |
| Slow | 360 ms | Spring — toggles, release |
| Deliberate | 520 ms | Out — entrances, sheets |

- **Out** = exponential ease-out (≈ `cubic-bezier(.16, 1, .3, 1)`), **Spring** = back ease-out, amplitude 0.35 (≈ `cubic-bezier(.34, 1.56, .64, 1)`), **InOut** = cubic.
- **Rise**: opacity 0 → 1 (360 ms) + translate-Y 14 → 0 px (520 ms), Out. Lists stagger 35 ms per item, capped at 12.
- **Press**: scale 0.95 in 90 ms, spring back in 360 ms. **Hover**: white overlay 0 → 7 %, cards scale 1.025.
- **Pop**: scale 0.9 → 1 spring (toasts, QR codes, toggled stars). **Pulse**: live dot ring 1 → 2.6×, 1.6 s loop.

## Components

Buttons (primary gradient + glow, secondary, ghost, icon, danger) · toggle switch · segmented control ·
filter chips · slider · text & password fields · combo box · cards · toasts · nav rail · clip card with
hover-scrub · seek bar with preview · ring progress · level meter · context menu · tooltip · dialogs.

## Brand marks

- **Hypt** — the brand artwork: an *H* inside a recording frame; a player leaps off the crossbar and the right stem becomes a camera. The wordmark is **Hy** in white + **pt** in `#FF4500`.
- **Vyu** — an eye whose pupil is a play button, white on the Amethyst → Lilac squircle.
- Vyu's squircle radius = 23.5 % of the size, top-down 20 % white gloss. Minimum size 16 px; clear space = ¼ of the mark.
- Hypt's icons are cut from the artwork by `design/brand-from-art.ps1` (hypt repo); Vyu's are drawn in code by `design/make-icons.ps1` (vyu repo). Both write a multi-size `.ico` plus PNGs.

## Icons

Segoe Fluent Icons (Windows 11, falls back to Segoe MDL2 Assets). Common glyphs: play `E768`, pause `E769`,
record `E7C8`, camera `E722`, clips `E8B2`, phone `E8EA`, lock `E72E`, settings `E713`, star `E734/E735`,
mic `E720`, mute `E74F`, voice `EFA9`, QR `ED14`, lightning `E945`, shield `EA18`, subtitles `E7F0`,
playlist `E90B`, tune `E9E9`, full screen `E740`, picture-in-picture `E92C`.
