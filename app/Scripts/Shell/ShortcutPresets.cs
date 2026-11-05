namespace OpenScctEditor.Shell;

/// <summary>
/// A named set of shortcuts in the style of another editor. A preset lists only the keys it changes (action id to
/// chord); every other action keeps its OpenSCCT default, unless that default is a chord the preset gives to another
/// action, in which case it is left without one so nothing clashes.
/// </summary>
public sealed record ShortcutPreset(string Name, string Description, IReadOnlyDictionary<string, string> Keys);

/// <summary>The shortcut presets on the Preferences Shortcuts page.</summary>
public static class ShortcutPresets
{
    /// <summary>
    /// The stock SCCT editor's keys: its accelerator table
    /// on the nearest OpenSCCT actions, and its Alt render modes. Keys whose stock command OpenSCCT lacks (the select
    /// memory) or has no action for (the F4 and F6 property windows; F5 Surface Properties is OpenSCCT's default too)
    /// are left out, and so is Shift+S, which the table gives to both Select All Surfaces and Adjacent Slants.
    /// </summary>
    private static readonly Dictionary<string, string> s_unrealEd = new(StringComparer.Ordinal)
    {
        ["file.save"] = "Ctrl+L",
        ["edit.duplicate"] = "Ctrl+W",
        ["edit.selectAll"] = "Shift+A",
        ["edit.selectNone"] = "Shift+Z",
        ["brush.add"] = "Ctrl+A",
        ["brush.subtract"] = "Ctrl+S",
        ["brush.deintersect"] = "Ctrl+D",
        ["build.play"] = "Ctrl+P",
        // Select Surfaces: Matching Brush, Adjacent Coplanars, Adjacent Floors/Ceilings, All Adjacents, Adjacent Walls,
        // Matching Texture, Reverse.
        ["surface.selectAllFaces"] = "Shift+B",
        ["surface.selectAdjacentCoplanar"] = "Shift+C",
        ["surface.selectAdjacentFloors"] = "Shift+F",
        ["surface.selectAdjacent"] = "Shift+J",
        ["surface.selectAdjacentWalls"] = "Shift+W",
        ["surface.selectSameMaterial"] = "Shift+T",
        ["surface.selectReverse"] = "Shift+Q",
        // Alt+1 wireframe, Alt+4 BSP cuts, Alt+5 dynamic light, Alt+6 textured: the nearest of OpenSCCT's view modes.
        ["view.render.wireframe"] = "Alt+1",
        ["view.render.brushcolors"] = "Alt+4",
        ["view.render.lit"] = "Alt+5",
        ["view.render.unlit"] = "Alt+6",
    };

    public static IReadOnlyList<ShortcutPreset> All { get; } =
    [
        new("OpenSCCT", "The editor's own shortcuts.", new Dictionary<string, string>()),
        new("UnrealEd (stock)",
            "The stock editor's key table: Save Ctrl+L, Duplicate Ctrl+W, Add Ctrl+A, Subtract Ctrl+S, Deintersect Ctrl+D, " +
            "Select All Shift+A, Select None Shift+Z, the Shift surface selections (B, C, F, J, Q, W, T), Play Ctrl+P and the Alt " +
            "render modes. Keys it shares with OpenSCCT (Ctrl+C, Ctrl+V, Ctrl+Z, Delete...) are unchanged.",
            s_unrealEd),
        new("RE+",
            "The stock keys with Reloaded Plus's Duplicate on Ctrl+D (Deintersect has no key). Its palette (Ctrl+Shift+P), " +
            "bookmarks (Ctrl+1-9, 1-9) and drop keys (End, Shift+End, Ctrl+End) are OpenSCCT's defaults already.",
            ReloadedPlus()),
        new("Hammer",
            "Valve Hammer's tool keys (Shift+S select, Shift+B block, Shift+E entity, Shift+X clip, Shift+V vertex, " +
            "Shift+A faces), H / Ctrl+H / U to hide, Shift+W grid snap, Ctrl+B snap to grid, Alt+P check, F9 play. Neighbour snap " +
            "stays on Shift+G.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tools.select"] = "Shift+S",
                ["tools.brush"] = "Shift+B",
                ["tools.place"] = "Shift+E",
                ["tools.clip"] = "Shift+X",
                ["tools.vertex"] = "Shift+V",
                ["tools.face"] = "Shift+A",
                ["outliner.hideSelected"] = "H",
                ["outliner.isolateSelected"] = "Ctrl+H",
                ["outliner.showAll"] = "U",
                ["view.grid.snap"] = "Shift+W",
                ["view.frame"] = "Ctrl+E",
                ["place.snapToGrid"] = "Ctrl+B",
                ["build.check"] = "Alt+P",
                ["build.play"] = "F9",
            }),
        new("Blender-like",
            "G move, R rotate, S scale, W select, A select all, Alt+A select none, Shift+D duplicate, Shift+A add brush, " +
            "H / Shift+H / Alt+H to hide, . frame selection, Home frame all, F3 command palette. Grid snap gives G up and " +
            "has no key; neighbour snap stays on Shift+G.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tools.select"] = "W",
                ["tools.move"] = "G",
                ["tools.rotate"] = "R",
                ["tools.scale"] = "S",
                ["edit.selectAll"] = "A",
                ["edit.selectNone"] = "Alt+A",
                ["edit.selectInvert"] = "Ctrl+I",
                ["edit.duplicate"] = "Shift+D",
                ["brush.add"] = "Shift+A",
                ["outliner.hideSelected"] = "H",
                ["outliner.isolateSelected"] = "Shift+H",
                ["outliner.showAll"] = "Alt+H",
                ["view.frame"] = ".",
                ["view.frameAll"] = "Home",
                ["view.commandPalette"] = "F3",
                ["view.neighbourSnap"] = "Shift+G",
            }),
    ];

    private static Dictionary<string, string> ReloadedPlus()
    {
        var keys = new Dictionary<string, string>(s_unrealEd, StringComparer.Ordinal) { ["edit.duplicate"] = "Ctrl+D" };
        keys.Remove("brush.deintersect");
        return keys;
    }
}
