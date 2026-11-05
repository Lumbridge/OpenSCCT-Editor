using Godot;
using OpenScct.Content;
using OpenScct.Core;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>What each team wears on this map (SVMap 1.2 <c>render/outfits.json</c>): body and head textures, a
/// replacement model with its goggle glow, and whose cosmetics win. Picked files are copied into the map's
/// <c>assets/outfits/</c> once it has a folder.</summary>
public partial class MapSettingsPanel
{
    private static readonly string[] s_stockModels = ["SPerso.ATT_01", "SPerso.DEF_01"];

    private void OutfitsSection()
    {
        PanelUi.Header(_content, "Spy outfit");
        PanelUi.Caption(_content, "Textures replace the team's body and head skins; a model replaces the whole character and plays the team's own animations.");
        TeamOutfitRows(spy: true);
        PanelUi.Header(_content, "Merc outfit");
        TeamOutfitRows(spy: false);
        PanelContainer errors = PanelUi.Callout(_content, Severity.Error, "");
        PanelContainer warnings = PanelUi.Callout(_content, Severity.Warning, "");
        _syncers.Add(() =>
        {
            List<OutfitProblem> list = ProjectOutfits.Problems(Context.Project.Outfits, Context.Session.Folder, EditorServices.ContentRoot);
            ShowProblems(errors, list.Where(p => p.Error));
            ShowProblems(warnings, list.Where(p => !p.Error));
        });
    }

    /// <summary>Shows a callout with one line per problem, or hides it when there are none.</summary>
    private static void ShowProblems(PanelContainer callout, IEnumerable<OutfitProblem> problems)
    {
        string text = string.Join("\n", problems.Select(p => p.Message));
        callout.Visible = text.Length > 0;
        callout.GetChild(0).GetChild<Label>(1).Text = text;
    }

    private void TeamOutfitRows(bool spy)
    {
        string team = spy ? "Spy" : "Merc";
        TeamOutfit Team() => Context.Project.Outfits.For(spy) ?? new TeamOutfit(null, null, null);
        IEditCommand Set(string name, Func<TeamOutfit, TeamOutfit> change) => new ProjectSettingsCommand($"{team} outfit: {name}", p =>
        {
            TeamOutfit t = change(p.Outfits.For(spy) ?? new TeamOutfit(null, null, null));
            TeamOutfit? kept = t.IsEmpty ? null : t;
            p.Outfits = spy ? p.Outfits with { Spy = kept } : p.Outfits with { Merc = kept };
        });

        FileRow("Body", $"{team} body", "PNG texture for the body skin", "*.png", () => Team().Body?.Texture,
            path => Set("body", t => t with { Body = path is null ? null : new OutfitSlot(path) }));
        FileRow("Head", $"{team} head", "PNG texture for the head skin", "*.png", () => Team().Head?.Texture,
            path => Set("head", t => t with { Head = path is null ? null : new OutfitSlot(path) }));
        FileRow("Model", $"{team} model", "A skinned glTF model; it needs every bone the team's animations drive", "*.glb",
            () => Team().Model is { } m ? m.Glb ?? m.Stock : null,
            path => Set("model", t => t with { Model = path is null ? null : new OutfitModel(path, null, t.Model?.GoggleBone ?? OutfitsDoc.DefaultGoggleBone, t.Model?.GoggleOffset ?? default) }),
            stock: name => Set("model", t => t with { Model = new OutfitModel(null, name, t.Model?.GoggleBone ?? OutfitsDoc.DefaultGoggleBone, t.Model?.GoggleOffset ?? default) }));
        TextRow("Goggle bone", $"{team}: the bone the goggle glow follows on the model", () => Team().Model?.GoggleBone ?? "",
            text => Team().Model is null || text.Trim().Length == 0 ? null : Set("goggle bone", t => t with { Model = t.Model! with { GoggleBone = text.Trim() } }));
        HBoxContainer offsetRow = PanelUi.Row(_content, "Goggle offset", $"{team}: the goggle glow's offset from that bone");
        AxisFields offset = AxisFields.Create(["X", "Y", "Z"]);
        offsetRow.AddChild(offset);
        offset.Edited += (axis, v, final) =>
        {
            if (final && Team().Model is { } m && Math.Abs(Axis(m.GoggleOffset, axis) - v) > 1e-6)
            {
                Context.Execute(Set("goggle offset", t => t with { Model = t.Model! with { GoggleOffset = WithAxis(t.Model!.GoggleOffset, axis, (float)v) } }));
            }
        };
        _syncers.Add(() =>
        {
            offset.Editable = Team().Model is not null;
            offset.SetValues(Team().Model is { } m ? [m.GoggleOffset.X, m.GoggleOffset.Y, m.GoggleOffset.Z] : [null, null, null]);
        });
        OptionRow("Cosmetics", [("The map's outfit wins", "map"), ("Players' own cosmetics win (textures only)", "player"), ("No cosmetics", "none")],
            () => OutfitsDoc.PolicyName(Team().Cosmetics),
            v => Set("cosmetics", t => t with { Cosmetics = OutfitsDoc.TryParsePolicy(v, out OutfitCosmetics c) ? c : t.Cosmetics }));
    }

    /// <summary>A file shown by name, with Pick (copied into the map) and Clear; for models Pick is a menu that also offers
    /// the stock meshes. <paramref name="what"/> names it in full for the file dialog ("Spy body").</summary>
    private void FileRow(string label, string what, string tooltip, string filter, Func<string?> read, Func<string?, IEditCommand> commit, Func<string, IEditCommand>? stock = null)
    {
        HBoxContainer row = PanelUi.Row(_content, label, $"{what}: {tooltip}");
        var name = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(1, 0),
        };
        row.AddChild(name);
        void PickFile() => Dialogs.OpenFile(what, [filter], path =>
        {
            (string stored, List<(string From, string To)> copies) = ProjectOutfits.Adopt(path, Context.Session.Folder);
            if (Context.Session.Folder is { } folder)
            {
                ProjectOutfits.Copy(folder, copies);
            }

            Context.Execute(commit(stored));
        });
        if (stock is null)
        {
            PanelUi.Button(row, "Pick…", $"Choose a {filter[2..]} file", PickFile);
        }
        else
        {
            // One menu, so a model row is no wider than a texture row: a file, or one of the shared character meshes.
            var menu = new MenuButton
            {
                Text = "Pick", TooltipText = $"Choose a {filter[2..]} file or a stock character mesh",
                ThemeTypeVariation = EditorTheme.CommandMenu, Flat = false,
                Icon = Icons.Get("chevron-down", EditorTheme.TextDim), IconAlignment = HorizontalAlignment.Right,
            };
            PopupMenu popup = menu.GetPopup();
            popup.AddItem($"File ({filter[2..]})…", 0);
            popup.AddSeparator("Stock meshes");
            for (int i = 0; i < s_stockModels.Length; i++)
            {
                popup.AddItem(s_stockModels[i], i + 1);
            }

            popup.IdPressed += id =>
            {
                if (id == 0)
                {
                    PickFile();
                }
                else
                {
                    Context.Execute(stock(s_stockModels[(int)id - 1]));
                }
            };
            row.AddChild(menu);
        }

        Button clear = PanelUi.IconButton(row, "close", "Clear: use the stock one", () =>
        {
            if (read() is not null)
            {
                Context.Execute(commit(null));
            }
        }, danger: true);
        _syncers.Add(() =>
        {
            string? value = read();
            name.Text = value is null ? "Stock" : Path.GetFileName(value);
            name.TooltipText = value ?? "Stock: the game's own";
            name.AddThemeColorOverride("font_color", value is null ? EditorTheme.TextDim : EditorTheme.Text);
            clear.Disabled = value is null;
        });
    }

    private static double Axis(FVector v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static FVector WithAxis(FVector v, int axis, float value) => axis switch
    {
        0 => new FVector(value, v.Y, v.Z),
        1 => new FVector(v.X, value, v.Z),
        _ => new FVector(v.X, v.Y, value),
    };
}
