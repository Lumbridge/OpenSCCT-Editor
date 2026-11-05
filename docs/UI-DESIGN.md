# OpenSCCT Editor: design system

How the editor should look, and the pieces that make it look that way: the tokens and theme variations in
`app/Scripts/Shell/EditorTheme.cs`, the icons in `Shell/Icons.cs` and `Shell/ToolbarIcons.cs`, and the builders in
`Panels/Common/PanelUi.cs` and `PanelIcons.cs`. Read this before adding or restyling UI.

The target is a calm, dense, readable dark editor in the family of Godot 4, Blender 4, Rider and Unreal 5. Hierarchy
comes from weight and space before colour; the accent is kept for selection, focus, "on" and the primary action.

Screenshots of the whole app, taken in the background, are how a change is checked: see [SCREENSHOTS.md](SCREENSHOTS.md).

---

## Using it

**Code against these names; never against literal colours or sizes.**

`EditorTheme` (all `public`):

- Colours: the tokens in §1 (`Background`, `Field`, `Bar`, `Panel`, `Popup`, `PanelLight`, `Hover`, `Pressed`,
  `Border`, `Divider`, `BorderStrong`, `Text`, `TextBright`, `TextHeading`, `TextDim`, `TextDisabled`, `TextOnAccent`,
  `TextHidden`, `GlyphRest`, `GlyphFaint`, `Tile`, `TileMesh`, `ToolIconOn`, `Accent`, `AccentHover`, `AccentPressed`, `AccentWash`, `Selected`, `HoverWash`, `Ok`/`Success`, `Warning`, `Error`,
  `Info`, `AxisX/Y/Z`, `CsgAdd`, `CsgSubtract`, `TimelineTrack`, `TimelineSegment`, `TimelineHold`, `TimelineEvent`,
  `Playhead`, `Scrim`, `Shadow`) and `Wash(c)` / `Line(c)` for a semantic background and outline.
- Constants: spacing `SpaceXxs`…`SpaceXxl`, radii `RadiusS`…`RadiusPill`, type sizes `FontSizeTitle`, `FontSize`,
  `FontSizeSmall`, `FontSizeCaption`, `FontSizeMicro`, icon sizes `IconSize`, `IconSizeHero`, `IconSizeTile`, and the
  control sizes of §1.12.
- Dock sizes: `DockLeft(canvasWidth)`, `DockRight(canvasWidth)`, `DockBottom(canvasHeight)`.
- Fonts: `UiFont`, `UiFontSemibold`, `MonoFont`, `NumberFont` (tabular digits).
- `Box(...)` (a StyleBoxFlat from tokens), `Empty(padX, padY)`, `ViewTagBox()` (the view tag for `_Draw` code).
- Area hooks: `EditorTheme.Shell.cs`, `.Panels.cs`, `.Viewports.cs`, `.Workbenches.cs` each implement one
  `static partial void …Styles(Theme t)`, called at the end of `Create()`. An area adds the theme items only it needs
  there, and its own icons with `Icons.Draw(key, svg, colour)` in its own files.

`PanelUi`:

| Builder | Gives |
|---|---|
| `Toolbar(parent)` | A wrapping row of panel controls |
| `Button(parent, text, tooltip, pressed, flat = false)` | A command; `flat` is the `FlatButton` look |
| `Primary(parent, text, tooltip, pressed)` | The one primary action of a surface |
| `IconButton(parent, icon, tooltip, pressed, danger = false)` | A 24 px row action (`IconButton` / `DangerIconButton`) |
| `Toggle(parent, text, tooltip, on, toggled)` | A `Chip` filter toggle |
| `SeverityToggle(parent, severity, count, on, toggled)` | A severity chip ("Warnings 3"); returns a `SeverityChip`, call `Refresh()` when counts change |
| `Segmented(parent, items, selected, chosen)` | A segmented control; `selected` -1 makes the parts plain commands |
| `Spin(parent, min, max, step, value, tooltip, width = SpinMinWidth, scrub = false)` | A spin box; `scrub` for dense grids |
| `Options(parent, tooltip)` / `CapPopup(menu)` | A drop-down that never widens its dock; a height-capped popup |
| `Columns(tree, (title, minWidth, expand)...)` | Left-aligned column titles, clipping cells |
| `EmptyState(parent, icon, title, hint, action, run)` | The centred empty state |
| `Header`, `Title`, `Caption(parent, text, wrap = true)`, `Callout(parent, severity, text)` | Text styles |
| `Row(parent, name, tooltip, labelWidth = LabelColumn)` | A labelled form row |
| `Search(parent, placeholder, changed, tooltip)` | A search box (keep the placeholder short) |
| `Mixed`, `MixedTooltip`, `Count(n, one, many)`, `DisplayName(unrealName)`, `Words(key)`, `WrapIdentifier(name)`, `SeverityNoun(s)` | Text helpers (§1.10) |

`PanelIcons`: `Severity(s)`, `Eye(open)`, `Lock(locked)`, `Dot(colour)`, `Type(ObjectRow)`, `Type(className)` and
`TypeIconName(className)`.

`Icons.Get(name, colour, size = IconSize)`, `Icons.Draw(key, svg, colour, size)`, `Icons.Has(name)`, `Icons.Names`;
`ToolbarIcons.For(actionId)`.

Core: `DockSide.Workbench` (its own dock under the right dock, hidden while it has no panel) and `EditorAction.RadioGroup` (menus
show actions with one as radio items).

---

## 0. Principles

1. **One source of style.** Every colour, size, radius and spacing comes from `EditorTheme` (UI) or `SceneStyle`
   (inside the 3D/2D views) as a named constant or a theme type variation. No `new Color(...)`, font-size override or
   magic padding in panel, tool or shell code. If a panel needs a new look, it adds a variation (see §5.1).
2. **One idiom per job.** One section header style, one way to show a row action (icon button), one command button,
   one toggle (chip), one empty state, one mixed-value mark, one severity mark.
3. **Hierarchy by weight and space before colour.** The accent (blue) means "selected, focused, on, or the primary
   action". It never means "header" or "this property is stored".
4. **The user owns the layout.** Docks never change width because of their content. Content shrinks, wraps at word
   boundaries or ends in "…" with the full text in a tooltip.
5. **Quiet at rest, clear on interaction.** Passive glyphs (eye, lock, chevrons) are dim at rest. Every clickable thing
   shows hover. Disabled things look disabled and stay readable.
6. **Function must not regress.** Changes are look, layout, styling, icons and label wording, plus small structural UI
   fixes. No behaviour changes beyond enabling/disabling buttons with the predicate that already guards the action.

---

## 1. Tokens

All names below are `public` members of `EditorTheme` unless noted. Existing names keep their value unless a new value
is given. Hex values are for reference; the code uses `new Color(r, g, b)`.

### 1.1 Surfaces (elevation, darkest to lightest)

| Token | Value | Hex | Use |
|---|---|---|---|
| `Background` (E0) | 0.075, 0.078, 0.090 | #131417 | Window backdrop, gaps between docks, scrim base |
| `Field` (inset) | 0.086, 0.090, 0.104 | #16171b | Inputs, trees, lists, tables, timeline tracks |
| `Bar` (E1) | 0.098, 0.102, 0.118 | #191a1e | Menu bar, main toolbar, status bar, tree column titles, dialog title bar |
| `Panel` (E2) | 0.122, 0.127, 0.145 | #1f2025 | Dock bodies, dialog bodies |
| `Popup` (E3, new) | 0.145, 0.151, 0.176 | #25272d | Menus, popups, tooltips, command palette (replaces `PanelLight.Darkened(0.12f)`) |
| `PanelLight` (E3 control) | 0.165, 0.172, 0.200 | #2a2c33 | Button rest, foldable bars, thumbnail tiles, chips off-hover |
| `Hover` | 0.200, 0.208, 0.240 | #33353d | Button/menu-bar/foldable hover |
| `Pressed` | 0.235, 0.245, 0.285 | #3c3e49 | Push button while held (not toggles) |

### 1.2 Lines

| Token | Value | Use |
|---|---|---|
| `Border` | 0.20, 0.21, 0.245 (#333640) | Field outlines, popup outlines, separators |
| `Divider` | `new Color(Border, 0.6f)` | Header hairlines, table row/column hairlines, status-bar segment separators |
| `BorderStrong` | 0.27, 0.28, 0.33 (#45474f) | Dialog frame, focused-but-not-accent outlines, swatch frames |
| `FocusRing` | `Accent` | 1 px outline of a focused field (already used by LineEdit focus) |

### 1.3 Text

| Token | Value | Use |
|---|---|---|
| `Text` | 0.86, 0.87, 0.90 (#dbdee6) | Body text, values, primary labels |
| `TextBright` | 0.96, 0.97, 0.99 | Hover/pressed text, selected row text, Title variation (replaces scattered `Colors.White`) |
| `TextHeading` | 0.76, 0.78, 0.84 (#c2c7d6) | Section headers (semibold) |
| `TextDim` | 0.56, 0.58, 0.64 (#8f94a3) | Field names, captions, hints, units, passive icons |
| `TextDisabled` | 0.42, 0.44, 0.49 (#6b707d) | Disabled text and icons (≈3:1 on Panel; replaces `TextDim@0.55`) |
| `TextOnAccent` | white | Text on an Accent fill (Primary button, menu hover) |

### 1.4 Accent and state

| Token | Value | Use |
|---|---|---|
| `Accent` | 0.33, 0.58, 0.96 (#5494f5) | Selection, focus, "on", primary action, links only |
| `AccentHover` | `Accent.Lightened(0.08f)` | Primary hover |
| `AccentPressed` | `Accent.Darkened(0.12f)` | Primary pressed |
| `AccentWash` | `new Color(Accent, 0.16f)` | Chip on, tool on (fill) |
| `Selected` | `new Color(Accent, 0.32f)` | Selected row/tile/text fill |
| `HoverWash` | `new Color(Text, 0.07f)` | Row, tab and list hover over Panel/Field (replaces 0.05/0.03) |

### 1.5 Semantic

| Token | Value | Wash (bg) / line | Use |
|---|---|---|---|
| `Ok` (alias `Success`) | 0.40, 0.80, 0.50 | `@0.10` / `@0.35` | Success, Play icon, "Live" |
| `Warning` | 0.95, 0.72, 0.25 | `@0.10` / `@0.35` | Warnings, locked state, warning callouts |
| `Error` | 0.93, 0.36, 0.36 | `@0.10` / `@0.35` | Errors, destructive hover on delete icons |
| `Info` | 0.45, 0.68, 0.95 | `@0.10` / `@0.35` | Info findings and callouts |

Helpers: `EditorTheme.Wash(Color c) => new(c, 0.10f)`, `EditorTheme.Line(Color c) => new(c, 0.35f)`.

### 1.6 Domain colours used in the UI

| Token | Value | Use |
|---|---|---|
| `AxisX`, `AxisY`, `AxisZ` | 0.90,0.30,0.30 / 0.35,0.85,0.35 / 0.35,0.55,1.0 | Axis letters in AxisFields, tripod, gizmo; `SceneStyle.AxisX…` forward to these |
| `CsgAdd`, `CsgSubtract` | = `SceneStyle` stock add (0.38,0.55,1) / subtract (1,0.62,0.2) | Toolbar CSG icons (replace the literals in ToolbarIcons) |
| `TimelineTrack` | `Field` | Mover / Event Sequencer track |
| `TimelineSegment` | `new Color(Accent, 0.35f)` + 1 px `Accent` top edge | Moving segment |
| `TimelineHold` | `PanelLight` | Stay-open / wait segment |
| `TimelineEvent` | 0.72, 0.60, 0.98 | Event markers (replaces purple literal) |
| `Playhead` | `Warning` | Playhead line (replaces 1,0.85,0.3 literal) |
| `Scrim` | `new Color(Background, 0.55f)` | Behind modal dialogs |
| `Shadow` | `new Color(0, 0, 0, 0.45f)` | Popup/dialog shadows |

### 1.7 Viewport colours (`SceneStyle`, owned by the viewports area)

Brush colours stay UnrealEd's (add blue, subtract orange, semisolid pink, non-solid green, portal yellow, builder red),
and stay user-editable in Preferences. Selection keeps its hue (users know it and can change it), but **yellow is
reserved for selection**: overlays move off saturated yellow, and selection gets a stronger treatment.

| Token | Value | Use |
|---|---|---|
| `Selection` | 1.0, 0.85, 0.25 (unchanged) | Selected objects: 2 px lines, 10 % fill in 2D views, name tag text |
| `Hover` | 0.6, 0.95, 1.0 (unchanged) | Pre-highlight, outline only, never a fill |
| `FaceSelection` | 0.30, 0.62, 1.0 | Picked faces: fill α 0.22, outline α 0.9 (no longer `Selection`) |
| `DropTarget` | 0.62, 1.0, 0.30 | The face a dragged material or texture would go on: fill α 0.38, 3 px outline; the other picked faces it would go on with it, fill α 0.2 |
| `SnapTarget` | 0.80, 0.45, 1.0 | Neighbour snap: the face a moved object sticks to, fill α 0.30, outline α 0.95; the moving face outlined |
| `SnapGuide` | `SnapTarget` α 0.8 | Neighbour snap's dashed in-line guide and the turn guide |
| `OverlayGuide` | 0.62, 0.60, 0.42, α 0.55 | Dashed guides, frustums, help paths, light/sound radii lines (was yellow) |
| `OverlayLink` | 0.45, 0.65, 1.0, α 0.8 | Event/trigger links and arrows |
| `OverlayText` | 0.80, 0.78, 0.62 | World labels (zone tips, mission text) with backdrop |
| `TeamSpy`, `TeamMerc` (named, existing values) | — | Spy green / Merc red start and team labels |
| `GridMinor` | 0.13, 0.135, 0.155 (was 0.22…) | Minor grid lines; fade out below 8 px spacing |
| `GridMajor` | 0.20, 0.21, 0.25 (was 0.32…) | Major lines |
| `GridAxis` | axis colour α 0.5 | World axes on the grid |
| `PerspectiveBackground`, `OrthoBackground` | unchanged | — |

Changing stock values only changes `Defaults`; saved user colours keep winning.

### 1.8 Spacing scale

`SpaceXxs = 2, SpaceXs = 4, SpaceS = 6, SpaceM = 8, SpaceL = 12, SpaceXl = 16, SpaceXxl = 24`

| Where | Value |
|---|---|
| Gap inside a control group (icon to text, label to its field, segmented parts) | `SpaceXs` 4 (segmented 0) |
| Gap between controls in a toolbar or row | `SpaceXs` 4 (panel toolbars), `SpaceXxs` 2 (main toolbar buttons) |
| Gap between form rows | `SpaceXs` 4 |
| Space above a section header | `SpaceL` 12; below it `SpaceXs` 4 |
| Dock content padding | `SpaceM` 8 (was 6) |
| Dialog padding / button gap / gap before Cancel | `SpaceXl` 16 / `SpaceM` 8 / `SpaceL` 12 |
| Status bar segment gap | `SpaceM` 8 each side of a `Divider` |
| Empty state stack | `SpaceM` 8 between icon, title, hint, button |

### 1.9 Radii

`RadiusS = 3` (checkbox, chip-inner, scroll grabbers, swatches, badges), `RadiusM = 4` (buttons, fields, tags, tiles,
tooltips), `RadiusL = 6` (menus, popups, palette, dock panels), `RadiusXl = 8` (dialogs), `RadiusPill = 11` (chips).
Tree/list selection and hover: **0** (rows join into one band).

### 1.10 Typography

- `UiFont`: SystemFont Segoe UI → Inter → Noto Sans → sans-serif (unchanged), gray AA, light hinting.
- `UiFontSemibold`: same names, `FontWeight = 600` (Segoe UI Semibold ships with Windows).
- `MonoFont`: SystemFont Cascadia Mono → Consolas → monospace. Only for codes and identifiers in tables (Map
  Check code column, Log timestamps).
- `NumberFont`: `FontVariation` of `UiFont` with `OpentypeFeatures` `tnum = 1`, for the status bar cursor and
  numeric fields that update live (Segoe's digits are tabular already; this guards the fallbacks).

| Token | px | Weight | Use |
|---|---|---|---|
| `FontSizeTitle` | 15 | 600 | Selection title (Inspector, Surface), dialog message title, empty-state title |
| `FontSize` | 14 | 400 | Body, fields, buttons, menus, tree rows |
| `FontSizeSmall` | 13 | 400 / 600 | Tabs, tooltips, column titles, chips, status bar, view labels; headers at 600 |
| `FontSizeCaption` | 12 | 400 | Captions, hints, units, badges, grid tag, world labels, storey caption |
| `FontSizeMicro` | 11 | 400 | Axis tripod letters, timeline tick labels only |

Nothing below 11. No other literal sizes anywhere.

Text conventions (all areas):
- **Title Case**: menu items, palette entries, context-menu items, window and tab titles, column titles.
  **Sentence case**: buttons, field names, headers, hints, tooltips, placeholders, status text.
- "…" (U+2026) on commands that ask for more input (Open…, Import Mesh…) and for truncation. Never "...".
- "−" (U+2212) for negative numbers in labels (Turn −90), "–" for ranges (Ctrl+1–9), "→" for transitions (0 → 1),
  " · " as the one separator in compound status/hints, a non-breaking hyphen in "Double‑click".
- Keys: `Ctrl+Shift+Z`, `Esc`, `Del`, `Enter`.
- Mixed values: `PanelUi.Mixed` = "—" in `TextDim`, tooltip "Multiple values". Unset: "(not set)".
- Counts through `PanelUi.Count(n, "actor")` → "1 actor", "2 actors"; "GE"/"GEs" handled.
- Class and property names shown to users go through `PanelUi.DisplayName` ("bCollideActors" → "Collide actors",
  "EchelonEMF" → "Echelon EMF"); camelCase keys through `PanelUi.Words`. Raw names stay in tooltips and search.

### 1.11 Icons

- 16-unit `viewBox`, drawn inside 1.5…14.5, stroke **1.4**, round caps and joins, `fill="none"`; solid fill only for
  small marks (dots, play, check fill, pupils). Colour through the `C` placeholder.
- `IconSize = 16` everywhere (menus, trees, buttons, **main toolbar**: was 18, which put strokes on half pixels).
  `IconSizeHero = 32` (empty states), `IconSizeTile = 48` (thumbnail placeholders).
- Rest colour: `TextDim` for passive glyphs (chevrons, eye, lock, steppers), `Text` for action icons, semantic or
  Accent for state. Buttons tint with `icon_*_color` modulates, so one texture serves all states.
- All icons go through `Icons.Get` (SVG → `DpiTexture`, sharp at every UI scale). No `Image.SetPixel` icons, no font
  glyphs (★, ▶, ⇕, x, ^, v) as icons.

### 1.12 Sizes

| Token | Value | Use |
|---|---|---|
| `ControlHeight` | 28 | Buttons, fields, option buttons, spin boxes (14 px line + 2×4 pad + border) |
| `ControlHeightCompact` | 24 | Icon buttons in rows, chips (22 + margin), segmented items |
| `RowHeight` | 24 | Tree/list rows (tree `v_separation` 2 + line) |
| `MenuRowHeight` | 24 | Popup menu rows (`v_separation` 5, was 7) |
| `TabHeight` | 30 | Dock tabs (`padY` 5) |
| `ToolButtonSize` | 28 | Main toolbar buttons |
| `IconButtonSize` | 24 | `IconButton` variation |
| `LabelColumn` | 104 | Default label width in form rows (Inspector was 100, `PanelUi.Row` 120) |
| `LabelColumnWide` | 168 | Forms in wide containers (Preferences, Traversal) |
| `LabelColumnNarrow` | 56 | Nested settings in a workbench (a picture's focal point, a camera's look) |
| `SpinMinWidth` | 72 | Default spin box width (5 digits + stepper) |
| `SpinCompactWidth` | 56 | Spin boxes in dense grids (Match win grid) |
| `SpinButtonsWidth` | 16 | Stepper column inside a spin box |
| `NumberFieldWidth` | 96 | Fixed-width numeric settings (Preferences) |
| `SearchMinWidth` | 110 | Search boxes (existing) |
| `PopupMaxHeight` | 480 | Cap for long OptionButton/MenuButton popups |
| `DialogMinWidth` | 420 | Message dialogs |
| `PickerWidth` | 150 | A picker beside a field (event sequencer send and answer types) |
| `TimelineHeight` / `LaneTimelineHeight` | 56 / 96 | A workbench timeline (Mover) / a lane timeline (Event Sequencer) |
| `StatusCursorWidth` | 168 | The status bar's cursor field, so later fields do not move |
| `PreferencesNavWidth` | 132 | The Preferences category list |
| `SwatchSize` | 44×20 | A colour swatch in a form |
| `DockLeft` / `DockRight` / `DockBottom` | clamp(0.19 W, 230, 300) / clamp(0.24 W, 280, 380) / clamp(0.24 H, 160, 280) | Initial split offsets from the canvas (1920×1080: 300/380/260; 1280×720: 243/307/173) |

---

## 2. Theme type variations (the component vocabulary)

Defined in `EditorTheme`. Use them by name (`ThemeTypeVariation = "…"`) or through the
`PanelUi` helpers.

| Variation | Base | Look |
|---|---|---|
| (default) `Button` | Button | Secondary command: `PanelLight` rest, `Hover` hover, `Pressed` held, `RadiusM`, pad 10×4. Toggle-on (`pressed` when ToggleMode): `AccentWash` + 1 px `Accent@0.6` |
| `PrimaryButton` | Button | `Accent` fill, `TextOnAccent`, hover `AccentHover`, pressed `AccentPressed`, disabled `PanelLight@0.5` + `TextDisabled` |
| `FlatButton` | Button | Empty at rest (same margins), `Hover` box on hover, `Pressed` held, `Accent` text when toggled on. Replaces `Button.Flat = true` everywhere |
| `IconButton` | Button | 24×24, Empty at rest, `HoverWash` box on hover, icon modulate `TextDim`→`Text` on hover |
| `DangerIconButton` | IconButton | Same, icon turns `Error` on hover (delete key, remove action/picture/place) |
| `ToolButton` | Button | Main toolbar (existing): Empty at rest, `Hover`, on = `AccentWash` + `Accent@0.85` outline |
| `ToolMenu` | MenuButton | Toolbar drop-down: same margins as ToolButton in every state, `Hover` box on hover and while open, chevron-down icon right in `TextDim` |
| `Chip` | Button | Filter toggles: off = Empty + 1 px `Border` outline, `TextDim` text; on = `AccentWash` fill, no border, `Text`; `RadiusPill`, pad 9×2, `FontSizeSmall` |
| `Segment`, `SegmentFirst`, `SegmentLast` | Button | Segmented control parts: shared `PanelLight`, radius only on the outer ends, 1 px `Divider` between, on = `AccentWash` |
| `ViewLabel` | MenuButton | View tag (existing), background alpha 0.85 (was 0.72), `FontSizeSmall` |
| `ViewTag` | PanelContainer | Same box as ViewLabel, for in-view widgets (storey slider, drag readout); `EditorTheme.ViewTagBox()` returns it for `_Draw` code |
| `Header` | Label | `UiFontSemibold`, `FontSizeSmall`, `TextHeading`; `normal` stylebox: transparent, margin top 12, bottom 4, 1 px `Divider` bottom border (full width hairline) |
| `Title` | Label | `UiFontSemibold`, `FontSizeTitle`, `TextBright` |
| `Caption` | Label | `FontSizeCaption`, `TextDim`, autowrap word-smart (status lines, hints, info rows) |
| `Dim` | Label | `TextDim` (existing) |
| `Mono` | Label | `MonoFont`, `FontSizeSmall`, `TextDim` |
| `Callout`, `CalloutInfo`, `CalloutError` | PanelContainer | `Wash(Warning/Info/Error)` fill, 1 px `Line(…)`, `RadiusM`, pad 8×6 |
| `Palette` | PanelContainer | The PopupMenu box (`Popup`, `RadiusL`, 1 px `Border`, shadow) for the command palette |
| `SubTabs` | TabBar | Inner tabs: transparent, `TextDim`, selected = `Text` + 2 px `Accent` **bottom** border, pad 8×4, `FontSizeSmall` |
| `ScrubSpin` | SpinBox | Spin box without visible stepper (empty `updown`, `buttons_width` 0): wheel and keys only. Dense grids only |

Theme items set directly (no variation):
- **SpinBox**: `buttons_width` 16, `field_and_buttons_separation` 0, `set_min_buttons_width_from_icons` 0. The up
  half is a `Field` box with a top and right border and the top-right radius; the down half has a right and bottom
  border, the bottom-right radius and a top border that separates the halves. Hovered `Hover`, pressed `Pressed`,
  disabled `Field@0.5`; the 12 px `spin-up`/`spin-down` chevrons are tinted `TextDim` → `Text` → `TextBright`.
  `SpinBoxInnerLineEdit`: left radius only and `minimum_character_width` **4**, so no spin box collapses to a stub;
  plain LineEdit keeps 0.
- **Sliders** (`HSlider`, `VSlider`): `slider` = 4 px track `Box(Text@0.12, RadiusS)`, `grabber_area` `Accent@0.55`,
  `grabber_area_highlight` `Accent`, `grabber`/`grabber_highlight` = 12 px `slider-knob` icon (Text / TextBright),
  `grabber_disabled` TextDisabled, `tick` 1 px `TextDim@0.4`.
- **ProgressBar**: `background` 4 px `Field` radius 2, `fill` `Accent` radius 2, font `FontSizeCaption`.
- **Tree / ItemList**: `selected`, `selected_focus`, `hovered`, `hovered_dimmed` radius 0; hovered `HoverWash`;
  `title_button_*` pad 8×4 on `Bar`, font `FontSizeSmall` `TextDim`; `v_separation` 2; `font_selected_color`
  `TextBright`.
- **Tabs** (`TabContainer`, `TabBar`): pad 10×5 (was 12×6), `FontSizeSmall`; `tab_hovered` = `Hover@0.7` with
  `font_hovered_color` `TextBright` (the 5 % hover was invisible); `menu`/`menu_highlight` icons = `more` (for the
  dock overflow popup).
- **PopupMenu**: panel `Popup`; `v_separation` 5; `font_disabled_color` `TextDisabled`; `font_accelerator_color`
  `TextDim@0.8` (shell additionally hides the shortcut of disabled items).
- **Buttons**: `font_disabled_color` `TextDisabled`, `icon_disabled_color` `TextDisabled` alpha; hover/pressed text
  `TextBright`.
- **FoldableContainer**: `folded_arrow` `chevron-right`, `expanded_arrow` `chevron-down` (TextDim) so the default
  off-centre arrows go; title bars stay `PanelLight` with 2 px between bars.
- **Window / AcceptDialog**: `embedded_border` is the whole frame in `Bar` (the title band's colour), `RadiusXl`,
  1 px `BorderStrong`, a soft shadow, reaching 31 px above the window for the title. AcceptDialog's `panel` (the body)
  covers it below the title in `Panel`, with a `Divider` hairline on top, so the title reads as a bar.
  `title_font` `UiFontSemibold` at `FontSizeSmall`. Body padding 16×16, button gap 8, buttons at least 80 wide.
- **Tooltip**: `Popup` box, `RadiusM`, 1 px `Border`, pad 8×5, `FontSizeSmall`, max width handled by autowrap.

---

## 3. Component rules

**Buttons.** One primary per surface at most (dialog default, Run check, Save as prefab, Apply in a form, Play on
the toolbar). Commands are boxed (`Button`). Row actions (move up/down, delete, clear, go to) are `IconButton`s,
right-aligned in a fixed trailing slot of the row. `FlatButton` only for inline text actions inside dense rows where a
box would be noise (Mover "Show"); it still hovers. Never `Flat = true`. Never letters as icons. Destructive actions
are never the default and sit apart (left of the dialog button row, or `DangerIconButton`).

**Toggles.** Filters and view switches are `Chip`s (severity filters, "Changed only", "Deep check"). Mutually
exclusive options are a segmented control (`Segment*`: All / Usual / None). Tools are `ToolButton`s.

**Inputs.** `ControlHeight`, `Field` fill, 1 px `Border`, focus = 1 px `Accent`. Placeholders sentence case, dim,
short enough for a 280 px dock (the tooltip explains). Fields fill their row (`ExpandFill`) with a small minimum.
Read-only = half-alpha box and `TextDim`.

**Spin boxes.** Always through `PanelUi.Spin` (width `SpinMinWidth`, or `ExpandFill` in a form row); stepper joined
inside the field box. `ScrubSpin` in grids of more than ~6 numbers. Units go in `Suffix` (" s", " u").

**Drop-downs.** `OptionButton.FitToLongestItem = false` everywhere (so long items never widen a dock), text ends in
"…", popup capped at `PopupMaxHeight` (through `PanelUi.Options`/`PanelUi.CapPopup`). Lists of more than ~25 items
(classes) use a searchable popup (search field + tree), not a flat PopupMenu.

**Checkboxes.** Theme icons only. A flag mixed across the selection shows the `check-mixed` icon (clicking sets all).
Checkbox grids use the same columns and order everywhere the same flags appear (3 columns in a 340 px dock).

**Trees and lists.** Row selection is one full-width band (select every column of the item; radius 0). Hover is a
visible `HoverWash`. Column titles left-aligned, Title Case, set through `PanelUi.Columns`. Counts right-aligned in
`TextDim`. Type icon at the start of object rows. Lists fill the remaining height (`ExpandFill`); empty lists show a
centred `EmptyState` inside the list area and hide column titles.

**Thumbnail grids.** Every tile on a fixed square `PanelLight` tile (radius 4, 1 px `Divider`), images letterboxed,
meshes on a `Hover`-coloured backdrop; one or two caption lines that break only at spaces, `_`, `.` (insert U+200B
after `_` and `.`), then "…"; full name in the item tooltip. Prefabs without geometry show their main actor's type
icon at 48 px in `TextDim` with a count badge.

**Palette tiles (Placeables).** A palette of classes to place is a flow of borderless tiles, not a thumbnail grid:
each tile is the class's mesh picture at thumbnail size with its kind icon in a badge at the top-left, or the kind icon
alone at `IconSizeTile` in `Text` when the class has no mesh, over a centred name of at most two lines (every tile
two lines tall, so a row lines up). A tile lights with `RowHover` under the pointer and shows the pointing hand; it
is a `PanelContainer`, not a button, so it can be dragged as well as clicked. Favourites have their name in `Accent`
and a star in their tooltip. The tooltip is the name, the class and group, then what it does, how it is placed and
how many stock maps and this map use it, wrapped at 80 characters. Group headings (`PanelUi.Header`) only when more
than one group shows; a group's tab needs none. One line of `Caption` help at the bottom, ellipsised, the whole of it
in its tooltip.

**Tabs and docks.** Dock tabs: `Panel` selected with 2 px `Accent` top line (existing), quieter padding, visible hover.
Every dock has an overflow menu (`TabContainer.SetPopup`) listing all its panels. Nested tabs use `SubTabs`.
A dock's width and height belong to the splitter; panels clamp their minimum width to 0 and clip or wrap.

**Panel skeleton.** Toolbar row (`PanelUi.Toolbar`) → main list/tree/form (`ExpandFill`) → detail or form block
pinned at the bottom → at most one line of `Caption` help at the very bottom (or only in tooltips). Status lines are
`Caption`, not `Header`.

**Section headers.** Only `PanelUi.Header` (the `Header` variation: 12 px above, hairline below). Foldable groups
(`FoldableContainer`) are for long property groups only (Inspector Properties, Map info), and use the same chevrons.
No ad-hoc spacer controls.

**Property grid rows.** Label column `LabelColumn` (one value in a panel; Inspector, Map and PropertyGrid share it),
label `TextDim`, ellipsis + tooltip. A changed (stored) property: label in `Text`, the `reset` IconButton visible;
defaults in `TextDim`. No Accent on labels. Info lines ("6 faces · corridor", "Flags 0x0") are full-width `Caption`
under their section, not in the label column. Axis fields: letter in its axis colour, Pitch/Yaw/Roll spelled out in
tooltips. Colours get a `Swatch` bar (`ColourSwatch`) in the value column that opens Godot's colour picker in a
`PopupPanel` (no screen sampler, no presets, no alpha); it takes the keyboard focus (Enter or Space opens it, and Ctrl+Z
after a pick undoes the pick). Color structs: the swatch on the property's row and the R G B A boxes on a full-width line
under it (the A box edits alpha). UE2 hue/saturation/brightness bytes (LightHue, AmbientHue...): a "Light colour" row
with the swatch before the three number rows, also under "Changed only" when any of them is stored. It converts with
FGetHSV's own hue triangle (`LightColour.Chroma` / `FromColour`; saturation 0 is the full colour, 255 white); a light's
swatch is brightness / 255, a zone's ambient FGetHSV's curve (`LightColour.Scale`). Only what a drag moves beyond the
picker's own steps is written (hue, saturation, brightness each on its own), so a brightness drag keeps the hue and
saturation bytes. A drag is one undo step; Escape in the picker puts back the colour it opened on by undoing its
picks; several objects with different colours show a dash.

**Toolbars.** Main toolbar: file | undo/redo | tools | snap segments | CSG | layout | Build, Check, Play
(Play = primary), then the tool's options on the same `Bar`, wrapping to a second line when the window is too narrow
(so the run buttons never move when the tool changes). A snap toggle and its size drop-down form one segmented
control: grid snap, rotation snap, then neighbour snap, whose drop-down (View > Neighbour Snap Options) shows its
distance and holds, under a separator, check items for what it applies to (Move, Resize, Rotate, In Line, Baked world). `ToolMenu` drop-downs keep a fixed width (widest item) and open at their bottom-left + 2 px. Tool option rows: tool controls first, then `Divider`, then
the hint as `Caption` at the end, all hints sentence case with " · ".

**Menus.** Title Case. Groups by `Order / 100` (§6.2 bands). Exclusive choices (tools, render mode, layout) are
radio items. Disabled items show no shortcut (or a dimmed one). Context menus show the same accelerators as the main
menus.

**Tooltips.** Sentence case, end without a full stop unless two sentences, include the shortcut in TextDim
("Move  W").

**Dialogs.** Modal = full-window `Scrim` behind, `DialogMinWidth`, `Title` line + `Caption` explanation + optional
icon, buttons right-aligned: [Don't save] … [Cancel] [Save]: destructive on the left, primary rightmost.
Fields in dialogs are fixed width (`NumberFieldWidth`), never stretched across.

**Status bar.** Segments: status (ExpandFill, ellipsis, tooltip with full text) | cursor (fixed min width, right
aligned, `NumberFont`) | grid · rot · neighbours ("Neighbours 8 px" or "Neighbours off") | tool | selection (display name in `Text` + kind in `TextDim`) | counts
(pluralised) | Live. 1 px `Divider` between segments, `FontSizeSmall`. A progress segment (4 px bar + job name +
cancel IconButton) appears while a background job runs.

**Viewport labels and overlays.** View tag top-left (ViewLabel). Grid/scale tag bottom-right (ViewTag style,
`FontSizeCaption`). Storey slider: a slim 10 px themed VSlider in a `ViewTag` at the top-right, inset 8 px, height
12 px per storey (max 160), caption "All"/storey name in `Caption`. World labels: `FontSizeCaption`, `OverlayText`,
drawn with a 3 px outline in `Background@0.85`, truncated at ~24 chars, culled when zoomed out (> 2 u/px ortho,
> 2,000 u perspective) unless selected or hovered. Selection name tag at the top-left of the selection's screen bounds
(+6 px), hidden while dragging a gizmo. Drag readout: ViewTag beside the cursor, "X 80  Y −16  Z 0" with axis-coloured
letters.

**Empty states.** `PanelUi.EmptyState`: centred stack of a 32 px TextDim icon, `FontSizeTitle`-ish title in `Text`,
`Caption` hint, optional `PrimaryButton` (Map Check: "No check run yet" + Run check).

**Badges for errors and warnings.** Shape + colour: `error` (circle ×), `warning` (triangle !), `info` (circle i)
icons, 16 px, in the semantic colour. Text next to them stays `Text`/`TextDim` (never colour the whole code or row).
Severity filter chips: icon + plural noun + count in `TextDim` ("Errors 0"), the same helper in Map Check and Log.
Callouts (`Callout*`) for inline warnings in forms, with the icon and `Text` body.

---

## 4. Icon list (`Icons.cs`)

Basic: `chevron-down/up/right/left`, `updown`, `check-on`, `check-off`, `radio-on`, `radio-off`, `close`,
`star`, `star-outline`, `play`, `stop`, `arrow-right/left/up/down`, `reset`, `goto`, `search`, `dot`, and
`spin-up`/`spin-down` (the spin box stepper, drawn at 12 px).

New UI icons:

| Name | Drawing |
|---|---|
| `eye` | Almond (two arcs 2.5→13.5) with a 2.25 r pupil circle |
| `eye-off` | `eye` without pupil plus a 3→13 diagonal slash |
| `lock` | Rounded body 4×7…12×13.5 (rx 1.5), closed shackle arc r 2.5 over it |
| `lock-open` | Same body, shackle arc lifted and open on the right |
| `check-mixed` | `check-on` box (filled C) with a horizontal bar in #16171b |
| `error` | Circle r 6 with an × inside |
| `warning` | Rounded triangle with "!" (stroke + dot) |
| `info` | Circle r 6 with "i" (dot + stem) |
| `success` | Circle r 6 with a tick |
| `plus` | Plus 4→12 |
| `minus` | Minus 4→12 |
| `more` | Three dots horizontal (filled, r 1.1) — overflow menus |
| `menu` | Three lines — dock overflow alternative |
| `filter` | Funnel |
| `copy` | Two offset rounded rects |
| `paste` | Clipboard with a sheet |
| `trash` | Bin with lid and two stems |
| `folder` | Folder outline (for "Show folder") |
| `external` | = `goto` (alias) |
| `pin` | Map pin (bookmarks, places) |
| `camera-bookmark` | Camera body + small star (bookmark camera) |
| `refresh` | Two-arrow circle (Reload, Reindex) |
| `import` | Arrow down into a tray |
| `export` | Arrow up out of a tray |
| `link` | Two chain links (connections) |
| `unlink` | Broken chain |
| `timeline-key` | Diamond (mover keys) |
| `slider-knob` | Filled circle r 4.5 with a 1 px `Background` ring (slider grabber, drawn at 12 px) |
| `drag-handle` | Six dots 2×3 (reorder rows) |
| `settings` | Gear (Preferences) |
| `help` | Circle with "?" |
| `keyboard` | Keyboard outline (Shortcuts page) |
| `palette` | Three rounded tiles and a pointer over the fourth corner (Placeables panel, its command and the tools' Palette button) |

Object type icons (outliner, classes, inspector title, prefab placeholders, place picker):

| Name | Drawing |
|---|---|
| `type-brush` | Cube (as `tools.brush`) |
| `type-brush-subtract` | Cube with dashed edges |
| `type-volume` | Dashed box (BlockingVolume, triggers, zones) |
| `type-light` | Bulb |
| `type-sound` | Speaker with one wave |
| `type-mesh` | Static mesh: faceted gem/teapot-like polyhedron |
| `type-emitter` | Three dots fanning from a point |
| `type-mover` | Box with a right arrow |
| `type-start` | Person with a flag (PlayerStart) |
| `type-camera` | Security camera on an arm |
| `type-alarm` | Bell |
| `type-laser` | Two posts with a zigzag beam |
| `type-trigger` | Dashed box with a lightning bolt |
| `type-event` | Lightning bolt (SMagicEvent) |
| `type-objective` | Target rings (SObjective, SMission) |
| `type-computer` | Monitor (objective computer) |
| `type-door` | Door with knob |
| `type-tip` | Speech bubble (SZoneTips) |
| `type-path` | Dotted path with an end dot (SHelpPath) |
| `type-ge` | Ladder (as `tools.ge`) |
| `type-zone` | Portal frame (zone portal, ZoneInfo) |
| `type-actor` | Generic: circle with a dot (fallback) |
| `type-group` | Folder (outliner groups) |
| `type-layer` | Stacked sheets |

Actor kind icons (`ActorIcons`: the perspective view's actor icons, and any list of actors or classes). Each kind of
actor (`ActorKinds` in Core, the one class → kind mapping, by class inheritance) draws one of the type icons above or
one of these:

| Name | Drawing | Kinds |
|---|---|---|
| `type-light` | Bulb | Point light |
| `type-light-spot` | Filled lamp shade on a stem, three rays fanning down | Spot light (LightEffect spot), SProjoFixe |
| `type-light-sun` | Circle with eight rays | Sun light (LightEffect sun) |
| `type-light-trigger` | Small bulb with a lightning bolt beside it | STriggerLight |
| `type-start-spy` | Solid figure with a goggle band across the head, pennant on a pole | Spy start (TeamNumber 0) |
| `type-start-merc` | Solid figure in a helmet (dome and brim), pennant on a pole | Mercenary start (TeamNumber 1) |
| `type-camnet` | Camera body with a lens, wired to two nodes below | SCamNetwork |
| `type-sensor-motion` | Dome with two waves over it | SVolumetricSensor, motion tracking |
| `type-sensor-presence` | Person in a dashed circle | SPresenceTrigger, SPresDetector |
| `type-mine` | Circle with spikes | SMines and subclasses, SBallTrap |
| `type-laptop` | Open laptop | SComputerObjectiveTrigger |
| `type-bomb` | Round bomb with a fuse | SBombTargetObjectiveTrigger |
| `type-disk` | Disc with a hole and a shine arc | SDisk |
| `type-flag` | Pole with a swallow-tail flag | SFlag |
| `type-flag-zone` | Flag standing in a dashed ground ellipse | SFlagDropZone |
| `type-mission` | Clipboard with a tick (as `check-list`) | SMission |
| `type-ammo` | Three cartridges | SAmmoStock |
| `type-health` | Cross in a rounded square | SInfirmary |
| `type-switch` | Wall plate with a lever | SInterrupteur, SGPOControl |
| `type-lift` | Car with up and down chevrons | SLift |
| `type-message` | Speech bubble with two text lines | SMessageTrigger |
| `type-glass` | Pane with two shine strokes | SVitre |
| `type-gameplay` | Hexagon with a dot | Other gameplay objects (SBasicGamePlayObject) |

The other kinds use `type-camera` (cameras), `type-laser`, `type-alarm`, `type-objective` (SObjective, objective
triggers), `type-door`, `type-mover`, `type-trigger`, `type-volume`, `type-zone` (ZoneInfo), `type-sound`,
`type-emitter`, `type-projector`, `type-event` (SMagicEvent, SShaker), `pin` (keypoints), `type-note`, `type-tip`,
`type-path`, `type-conveyor`, `type-mesh`, `type-pawn`, and `type-actor` for anything else. `ActorIcons.For(className)`
gives a class's icon (16 px, `TextDim`); `ActorIcons.For(actor, colour)` refines it by the actor's own values.

**In the perspective view** (`ActorIconBatch`), an actor with nothing else to draw shows its kind icon instead of its
box: the drawing in white with a dark halo (so it reads on light and dark surfaces), tinted by the actor's category
colour (`ActorLook`), as a screen-facing billboard. It is `Preferences.ActorIconSize` UI pixels (default 26, 12–64,
Edit > Preferences > Views) while near and shrinks with distance to no less than 55 % of that (and never below 10 px).
It shows whole while the world leaves its actor in view and hides whole behind a wall; an actor buried just behind a
surface (less than 320 units along the view: a start inside a crate) shows faint through it, as does an actor greyed
out of the match. Icons sort among themselves. Selected: the selection colour, full size however far, drawn over
everything, with the actor's box and arrow. Actors whose facing matters (starts, cameras, lasers, spot and sun lights,
switches, stations, emitters…) keep their direction arrow, long enough to reach out from under the icon. Volumes and
other brush-shaped actors keep their shape and get no icon; actors drawn with a mesh keep it and show a small icon
(75 %) only while selected. The 2D views and the camera monitor keep the boxes. View > Show > Actor Icons turns the icons
off (boxes again); it is greyed out while View > Show > Actors is off.

Clicking follows what is drawn: an icon on show takes the click over the lines, boxes and surfaces it is drawn over
(the surface it sits on, or a faint one it is buried behind), and an actor drawn as an icon has no box to click until
it is selected. What View > Show hides is not picked.

Toolbar icons (`ToolbarIcons.cs`, size 16): existing set kept; `brush.add` and `brush.subtract` redrawn as a cube with
a +/− badge in `CsgAdd`/`CsgSubtract`; `tools.face` with a clearly filled top face (Accent fill α 0.6) so it differs
from `tools.brush`; new `tools.conveyor` (two rollers and a belt).

---


## 5. Menu order bands

Each owner sets `Order` on the actions it registers; menus group by `Order / 100`.

- **Edit**: 0 Undo, Redo, Add Undo Checkpoint · 100 Cut, Copy, Paste, Paste Properties, Duplicate, Delete ·
  200 Select All, Select None, Invert, Select group… · 300 Hide Selected, Isolate Selected, Show All, Lock Selected ·
  400 Layers (New Layer, Move to Layer) · 800 Preferences… · 900 everything else.
- **View**: 0 Layout, Render Mode, Game Mode · 100 Show, Overlays, Grid (with the Neighbour Snap toggle), Grid Size,
  Rotation Snap, Neighbour Snap Options (distance, Move, Resize, Rotate, In Line, Baked world), Storeys ·
  200 Frame Selection, Frame All, Maximize View · 300 Bookmark Camera, Bookmarks · 400 UI Scale ·
  900 Search Classes, Search Outliner, Command Palette.
- Shortened titles: "Bookmark Camera", "Search Outliner", "Hide Selected", "Isolate Selected", "Show All",
  "Lock Selected", "New Layer", "Move to Layer", "Add Undo Checkpoint", "Search Classes".

---
