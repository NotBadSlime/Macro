# MacroHID Studio UI Overhaul Design

## Summary

Transform the existing MacroHID Studio WPF application from a static single-theme UI into a polished, animated, dual-theme desktop application with silky drag-and-drop interactions and refined micro-animations.

## Goals

- Dual theme system (Light + Dark) with runtime switching
- Smooth drag-and-drop with ghost preview and animated gaps
- Component decomposition for maintainability
- Micro-interaction animations throughout (hover, press, select, expand)
- Refined scrollbars, focus states, and shadow system
- No external UI framework dependencies; pure WPF implementation

## Non-Goals

- MVVM migration (keeping code-behind pattern)
- Feature additions (no new functionality)
- Breaking changes to localization or library storage

---

## 1. Theme System

### File Structure

```
src/ui/MacroStudio/
  Themes/
    ThemeKeys.cs
    LightTheme.xaml
    DarkTheme.xaml
    SharedStyles.xaml
  Services/
    ThemeService.cs
```

### Mechanism

- `ThemeService` swaps `ResourceDictionary` in `Application.Current.Resources.MergedDictionaries`
- All color references converted from `StaticResource` to `DynamicResource`
- Preference persisted to `%APPDATA%/MacroHID/settings.json`
- Toggle button in title bar (sun/moon icon)

### Color Tokens

| Token | Light | Dark |
|-------|-------|------|
| PageBackground | #F5F5F7 | #1C1C1E |
| PanelBackground | #FFFFFF | #2C2C2E |
| PanelBorder | #DADDE5 | #3A3A3C |
| PrimaryText | #1D1D1F | #F5F5F7 |
| SecondaryText | #6E6E73 | #98989D |
| Accent | #34C759 | #30D158 |
| AccentBlue | #007AFF | #0A84FF |
| AccentOrange | #FF9500 | #FF9F0A |
| AccentPink | #FF2D55 | #FF375F |
| Danger | #FF3B30 | #FF453A |
| Hover | #EEF2F7 | #3A3A3C |
| Selection | #EAF7EA | #1A3A1A |
| SelectionBorder | #34C759 | #30D158 |

### Shadow System

- Light: `DropShadowEffect Opacity=0.06, BlurRadius=12, Direction=270, ShadowDepth=2`
- Dark: `DropShadowEffect Opacity=0.4, BlurRadius=16, Direction=270, ShadowDepth=4`
- Drag ghost: `Opacity=0.2, BlurRadius=20`

---

## 2. Animation Infrastructure

### Files

```
src/ui/MacroStudio/
  Controls/
    AnimatedListBox.cs
    DragGhostAdorner.cs
    DragDropManager.cs
    SmoothGridSplitter.cs
```

### Drag-and-Drop Flow

1. Mouse down + 4px movement threshold triggers drag
2. `DragGhostAdorner` renders `VisualBrush` of dragged item at 0.7 opacity, follows mouse
3. Hit-testing calculates insertion index; other items animate via `TranslateTransform` + `CubicEase` (200ms) to open a gap
4. On drop: ghost fades out (150ms), target item springs into position
5. On cancel (Escape or leave bounds): ghost fades back to origin

### Add Panel to Sequence Drag

Same ghost mechanism but content is a simplified action-template card. Sequence list responds to `DragOver` with gap animation.

### Micro-Interactions

| Interaction | Animation | Duration | Easing |
|-------------|-----------|----------|--------|
| Button hover | Background color transition | 150ms | CubicEase Out |
| Button press | Scale to 0.97 | 100ms | CubicEase In |
| Button release | Scale to 1.0 | 200ms | BackEase Out |
| Panel expand/collapse | Height + Opacity | 250ms | CubicEase InOut |
| List item select | Background + Border color | 200ms | CubicEase Out |
| Theme switch | Global opacity crossfade | 300ms | Linear |
| Content switch | Opacity 0-1 + TranslateY 4px-0 | 200ms | CubicEase Out |
| Focus ring | Border color to AccentBlue + glow | 200ms | CubicEase Out |

### SmoothGridSplitter

- Uses `CompositionTarget.Rendering` with linear interpolation (lerp factor 0.3) for smooth column width follow
- Double-click resets to default proportions with 250ms CubicEase animation

---

## 3. Component Decomposition

### UserControls

```
src/ui/MacroStudio/
  Controls/
    MacroLibraryPanel.xaml/.cs
    SequencePanel.xaml/.cs
    ActionPalettePanel.xaml/.cs
    StepEditorPanel.xaml/.cs
    PlaybackPanel.xaml/.cs
    ConversionPanel.xaml/.cs
    DiagnosticsPanel.xaml/.cs
```

### Communication

- Each UserControl exposes C# events for outward communication
- MainWindow subscribes to events and coordinates inter-panel interaction
- Shared state via `MacroEditorState` class (document, selection, playback state)
- MainWindow.xaml.cs reduces from ~2250 to ~300 lines

### MacroEditorState

```csharp
public class MacroEditorState
{
    public MacroDocument? Document { get; set; }
    public string? SelectedMacroId { get; set; }
    public int SelectedStepIndex { get; set; } = -1;
    public bool IsPlaying { get; set; }
    public event Action? StateChanged;
    public void NotifyChanged() => StateChanged?.Invoke();
}
```

### MainWindow Layout

```xml
<DockPanel>
    <Border DockPanel.Dock="Top"> <!-- Title bar chrome --> </Border>
    <Grid>
        <local:MacroLibraryPanel />
        <local:SmoothGridSplitter />
        <local:SequencePanel />
        <local:SmoothGridSplitter />
        <ScrollViewer>
            <StackPanel>
                <local:ActionPalettePanel />
                <local:StepEditorPanel />
                <local:PlaybackPanel />
                <local:ConversionPanel />
                <local:DiagnosticsPanel />
            </StackPanel>
        </ScrollViewer>
    </Grid>
</DockPanel>
```

---

## 4. Window Chrome Upgrades

- Theme toggle button before window control buttons
- Window control buttons: animated hover backgrounds (150ms CubicEase)
- Close button: red background + white foreground on hover
- Title bar bottom border adapts to theme

---

## 5. Detail Polish

### Scrollbars

- Custom `ScrollBar` ControlTemplate: thin track (6px), rounded thumb
- Thumb expands to 8px on hover with opacity animation
- Dark thumb: #5A5A5E, Light thumb: #C7C7CC

### Focus States

- TextBox/ComboBox focus: border transitions to AccentBlue (200ms) with soft outer glow (1px)

### Transitions

- Panel content switches (macro selection, step editor fields) use Opacity + TranslateY animation
- Expander uses smooth height animation instead of instant show/hide

---

## Implementation Order

1. Theme infrastructure (ThemeKeys, ThemeService, Light/Dark dictionaries, DynamicResource migration)
2. SharedStyles.xaml (ControlTemplates with animation triggers)
3. Component decomposition (extract UserControls one at a time, verify no regressions)
4. Drag-and-drop system (DragDropManager, DragGhostAdorner, AnimatedListBox)
5. SmoothGridSplitter
6. Scrollbar and focus styling
7. Integration testing and polish pass
