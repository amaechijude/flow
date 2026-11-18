---
name: Obsidian Telemetry
colors:
  surface: '#10141a'
  surface-dim: '#10141a'
  surface-bright: '#353940'
  surface-container-lowest: '#0a0e14'
  surface-container-low: '#181c22'
  surface-container: '#1c2026'
  surface-container-high: '#262a31'
  surface-container-highest: '#31353c'
  on-surface: '#dfe2eb'
  on-surface-variant: '#bcc9cd'
  inverse-surface: '#dfe2eb'
  inverse-on-surface: '#2d3137'
  outline: '#869397'
  outline-variant: '#3d494c'
  surface-tint: '#4cd7f6'
  primary: '#4cd7f6'
  on-primary: '#003640'
  primary-container: '#06b6d4'
  on-primary-container: '#00424f'
  inverse-primary: '#00687a'
  secondary: '#4edea3'
  on-secondary: '#003824'
  secondary-container: '#00a572'
  on-secondary-container: '#00311f'
  tertiary: '#d0bcff'
  on-tertiary: '#3c0091'
  tertiary-container: '#b395ff'
  on-tertiary-container: '#4900ae'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#acedff'
  primary-fixed-dim: '#4cd7f6'
  on-primary-fixed: '#001f26'
  on-primary-fixed-variant: '#004e5c'
  secondary-fixed: '#6ffbbe'
  secondary-fixed-dim: '#4edea3'
  on-secondary-fixed: '#002113'
  on-secondary-fixed-variant: '#005236'
  tertiary-fixed: '#e9ddff'
  tertiary-fixed-dim: '#d0bcff'
  on-tertiary-fixed: '#23005c'
  on-tertiary-fixed-variant: '#5516be'
  background: '#10141a'
  on-background: '#dfe2eb'
  surface-variant: '#31353c'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 32px
    fontWeight: '600'
    lineHeight: 40px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 26px
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Inter
    fontSize: 15px
    fontWeight: '600'
    lineHeight: 22px
  body-lg:
    fontFamily: Inter
    fontSize: 15px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 20px
  body-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 18px
  code-lg:
    fontFamily: JetBrains Mono
    fontSize: 14px
    fontWeight: '500'
    lineHeight: 20px
  code-md:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 18px
  code-sm:
    fontFamily: JetBrains Mono
    fontSize: 11px
    fontWeight: '400'
    lineHeight: 16px
  label-caps:
    fontFamily: JetBrains Mono
    fontSize: 10px
    fontWeight: '600'
    lineHeight: 14px
    letterSpacing: 0.06em
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  gutter: 1rem
  gutter-desktop: 1.5rem
  margin: 1rem
  margin-desktop: 2rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1.25rem
  space-xl: 2rem
---

## Brand & Style

This design system targets systems engineers, cloud-native infrastructure operators, and kernel/eBPF developers running telemetry tools on Linux workstations and server dashboards. The core persona operates within dense terminal sessions, multi-monitor operations desks, and low-light operations centers.

The aesthetic blends **Modern Technical Minimalism** with **Tactile Glass-Console** precision:
- Deep obsidian foundation layers paired with cold graphite structural containers.
- High-frequency live data streams highlighted by electric cyan, emerald, and phosphor violet signals.
- Utilitarian layout discipline: immediate legibility under scrutiny, no decorative filler, and zero latency in visual visual triage.
- Tactical mechanical feel: subtle surface micro-borders, low-radiance status glows, and dense informational clarity reminiscent of specialized hardware monitoring interfaces.

## Colors

The palette is engineered dark-first to eliminate visual fatigue while delivering instantaneous semantic clarity across dense telemetry streams:

- **Primary (`#06b6d4` - Electric Cyan):** Downstream bandwidth, ingress network traffic, operational probes, active system threads, and primary interactive highlights.
- **Secondary (`#10b981` - Emerald Phosphor):** Upstream throughput, healthy process states, committed buffer pipelines, and validated eBPF filter outputs.
- **Tertiary (`#8b5cf6` - Kernel Violet):** eBPF tracepoints, socket hooks, kernel daemon state transitions, and context-switch events.
- **Neutral (`#0d1117` - Deep Obsidian Canvas):** Layered with tonal surface steps:
  - Surface Background: `#080b0f`
  - Card/Container Surface: `#0f141c`
  - Elevated Popovers/Tooltips: `#161f2c`
  - Subtle Border Strokes: `#212c3d`
  - Inactive/Muted Labels: `#64748b`
  - High-Contrast Text: `#f1f5f9`
- **Diagnostic Semantics:**
  - Amber Warning (`#f59e0b`): Unattributed socket traffic, throttled I/O cycles, and non-fatal dropped packets.
  - Rose Critical (`#f43f5e`): Kernel panics, killed pids, OOM faults, and saturated interface alerts.

## Typography

The typographic hierarchy implements an intentional dual-engine approach:

- **Inter (Interface & Scaffolding):** Drives high-level context, modal headers, navigation, and operational summaries. Rendered with tight tracking in display sizes to maintain architectural density.
- **JetBrains Mono (System Telemetry & Metrics):** Dedicated to tabular data, network identifiers (IPv4, IPv6, CIDRs, MACs), process IDs (PIDs), memory offsets, raw byte rates, and timeline timestamps.
- **Rules of Usage:**
  - Never use proportional fonts for numeric columns, counters, timestamps, or telemetry values.
  - Tabular figures must always be enforced to prevent column jitter when real-time numbers update dynamically.
  - Category titles and structural metric headings leverage uppercase micro-labels (`label-caps`) with open tracking (`0.06em`) for instant scanning across multi-panel layouts.

## Layout & Spacing

The layout philosophy follows a high-density, viewport-optimized modular grid tailored for workstation resolution and multi-pane tile managers:

- **Grid Architecture:** 12-column fluid grid system with compact gutters to maximize viewport usage for graphs, tables, and stream inspectors.
- **Adaptive Canvas Model:**
  - **Desktop / Workstation (>= 1280px):** Multi-column split-pane layout (sidebar navigation + live telemetry canvas + inspection drawer). Inner gutter fixed at `1.5rem` (`gutter-desktop`), outer frame margins at `2rem` (`margin-desktop`).
  - **Compact Displays / Laptops (768px - 1279px):** Inspector slides over or docks to bottom; 12-column grid collapses to 6-column modular card tracks. Gutter shifts to `1rem`.
  - **Mobile / Diagnostic View (< 768px):** Single-column stacked stream view. Horizontal scrolling strictly confined to wide data tables with frozen PID/Metric columns. Gutter and margin scale to `1rem`.
- **Rhythm Rules:** Internal component padding stays within compact tokens (`space-xs` through `space-md`) to ensure dense telemetry screens require minimal vertical scrolling.

## Elevation & Depth

Visual hierarchy does not rely on soft, generic dropshadows. Instead, elevation leverages **Tonal Layering** paired with **Precision Micro-Strokes** and **Subtle Colored Glows**:

- **Ground Plane (0dp):** `#080b0f` base canvas representing the root workspace.
- **Container Tiers (1dp):** `#0f141c` with a 1px solid stroke (`#212c3d`). No blur shadows; separation is defined purely by chromatic boundary contrast.
- **Active Panels & Focused Tiles (2dp):** `#161f2c` background with a selective accent stroke (e.g., `#06b6d4` at 40% opacity) indicating active terminal focus or stream lock.
- **Floating Overlays & Modals (3dp):** `#1a2433` layered over a 40% blurred backdrop (`backdrop-filter: blur(8px)`). Shadow is sharp and technical: `0 12px 32px -4px rgba(0, 0, 0, 0.7), 0 0 0 1px rgba(255, 255, 255, 0.08)`.
- **Signal Illumination:** Critical alerts and high-throughput channels employ localized soft luminescence (e.g., `box-shadow: 0 0 12px rgba(6, 182, 212, 0.25)`) to immediately catch the operator's eye without washing out surrounding text.

## Shapes

The design system maintains a controlled, tactical geometry using **Soft (Level 1)** curvature:

- **Standard Containers, Cards, and Inputs:** Corner radius calibrated to `0.25rem` (4px). This preserves the sharp, structured ethos of desktop engineering tools while eliminating harsh corner artifacts.
- **Badges and Pills:** Corner radius scaled to `0.5rem` (`rounded-lg`) or fully round pill structures where immediate categorization is required (e.g., status indicators, protocol tags).
- **Embedded Metric Sparklines & Waveforms:** Inline graphing bounds utilize precise 2px radius boundaries, ensuring graphs mate flush with card corners and table grid lines.

## Components

### Buttons
- **Primary:** High-contrast cyan background (`#06b6d4`), deep slate text (`#080b0f`), bold JetBrains Mono typography, 4px border radius. Hover state adds localized glow (`0 0 10px rgba(6, 182, 212, 0.4)`).
- **Secondary / Action:** `#161f2c` background, `#f1f5f9` text, 1px border (`#212c3d`). Hover transition shifts border to `#06b6d4`.
- **Destructive / Reset:** Semi-transparent rose tinted fill (`rgba(244, 63, 94, 0.12)`), `#f43f5e` border and label.

### Status Pills & Badges
- Compact height (20px), monospaced text in `label-caps`.
- Ingress Badges: Cyan text on `rgba(6, 182, 212, 0.12)` with a 1px border of `rgba(6, 182, 212, 0.3)`.
- Egress Badges: Emerald text on `rgba(16, 185, 129, 0.12)` with `rgba(16, 185, 129, 0.3)` border.
- Trace/eBPF Badges: Violet text on `rgba(139, 92, 246, 0.12)`.
- Warnings: Amber text with optional pulsing dot indicator for uninspected packets.

### Telemetry Cards
- Dark graphite background (`#0f141c`), 1px outline (`#212c3d`).
- Header split: Left contains Inter category title in `headline-sm`; right contains mono rate counter (e.g., `42.8 Gbps`) in cyan or emerald.
- Body incorporates inline vector sparklines with a vertical gradient fill tapering to 0% opacity at baseline.

### Data Tables
- Flush edge-to-edge layout with zero row alternating bands (zebra stripes omitted in favor of clean 1px bottom border `#16202e`).
- Hover state: Row illuminates with `#131b26` background tint and persistent left-edge 2px indicator in cyan.
- Numeric and diagnostic columns right-aligned using `code-md` for tabular numeric precision.

### Inputs & Filters
- Background: `#090d14`, border: 1px `#212c3d`, font: `code-md`.
- Focus state: Border shifts to active primary cyan with `box-shadow: 0 0 0 1px #06b6d4`.
- Integrated token chips within the input field for instant filter tags (e.g., `proto:tcp`, `port:443`, `pid:1042`).

### Checkboxes & Radios
- Square checkbox (4px radius) and circular radio with `#0f141c` base and `#2b374a` border.
- Active checked state fills with `#06b6d4` using deep dark checkmark icon. No soft drop shadow.