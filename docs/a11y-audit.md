# Accessibility audit (static)

This is a **static** conformance record for the Avalonia desktop UI. It is not a
screen-reader certification; runtime behaviour with Orca/NVDA still has to be
verified on a real desktop session.

Run the reproducible check:

```bash
./scripts/a11y-audit.sh
```

It currently checks **66 interactive/AT-visible controls across 9 views** and
passes: each has an explicit `AutomationProperties.Name`, or is explicitly
`AccessibilityView="Raw"` when it only duplicates adjacent text.

## What is exposed to assistive technology

| Area | Treatment |
| --- | --- |
| Window regions | `AutomationProperties.LandmarkType` = `Banner` (status/range), `Navigation` (sidebar), `Main` (content). |
| Navigation | Eight named buttons with stable `AutomationId` values (`nav-overview`, `nav-applications`, `nav-interfaces`, `nav-diagnostics`, `nav-reports`, `nav-history`, `nav-settings`, `nav-comparison`). |
| Headings | Headline text = `HeadingLevel 1`; section titles = `HeadingLevel 2`. |
| Form controls | Range/interface/comparison selectors and search boxes have names plus `HelpText`. Custom range dates use `LabeledBy` pointing at their visible labels. |
| Tables | Header cells carry `IsColumnHeader="True"`; `ItemsControl`/`ListBox` containers are named. |
| Status changes | Daemon notice, export result and last-refresh text use `LiveSetting="Polite"`, so updates are announced without interrupting. |
| Custom chart | Real `AutomationPeer` with control type `Image`, name/help text from the real caption, and `ItemStatus` for the focused point. Focusable; Left/Right/Home/End select a point and a white marker shows it. The selected point is spoken as "point N of M, local time, RX, TX, total". |
| Split/proportion bars | Real `AutomationPeer` with control type `ProgressBar`; `ItemStatus` exposes the actual fractions. Inside repeated rows they are `Raw`, because the adjacent text already carries the values. |

## Keyboard map

- `Tab` / `Shift+Tab`: banner controls → sidebar navigation → page content, in
  visual order.
- Arrow keys on the time-series chart: move the focused bucket; `Home`/`End`
  jump to the first/last bucket. `Focus` alone selects the first bucket and
  exposes its text.
- All sorting/filtering is implemented with real `Button` controls, so it is
  keyboard-operable by default; sort buttons expose the current sort summary as
  help text.
- Export and refresh are real buttons; their results are live regions.

## Known gaps (honest)

- No runtime screen-reader pass has been executed (no AT session in this
  environment). The audit verifies markup and peers, not actual announcements.
- The chart has no zoom/pan and no per-point tooltip for pointer users; keyboard
  point inspection exists but is not a full interactive chart.
- Table row/column coordinates are not exposed as a full `Table` automation
  pattern; named headers and named containers are provided instead.
- The custom-range `DatePicker` controls are labelled, but entering arbitrary
  times is still date-only; ranges are whole local days.
- Status-signal and daemon-health announcements depend on the AT honouring
  `LiveSetting="Polite"`.

## Scope note

Accessibility changes do not alter measured data or attribution caveats. Every
automation string comes from the same real snapshot the UI displays; no values
are invented for AT consumers.
