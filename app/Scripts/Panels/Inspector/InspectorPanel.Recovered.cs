using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc.Lights;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>Where the selected recovered lights came from: an .sdc import brings back the static lights the cook
/// stripped (from the map's editor copy, the file itself, or as stand-ins fitted to its lightmaps). A light the cooked
/// lightmaps do not confirm says so in a warning callout; the others in a caption.</summary>
public partial class InspectorPanel
{
    private void RecoveredSection()
    {
        List<ObjectRef> lights = _refs.Where(r => r.Kind == ObjectKind.Actor && Index.Actor(r) is { } a && LostLightRecovery.Explain(a) is not null)
            .ToList();
        if (lights.Count == 0)
        {
            return;
        }

        PanelUi.Header(_sections, "Recovered light");
        PanelContainer review = PanelUi.Callout(_sections, Severity.Warning, "");
        Label reviewText = review.GetChild(0).GetChild<Label>(1);
        Label text = PanelUi.Caption(_sections, "");
        _syncers.Add(() =>
        {
            List<Actor> actors = lights.Select(Index.Actor).OfType<Actor>().ToList();
            var lines = actors.Select(LostLightRecovery.Explain).OfType<string>().Distinct().ToList();
            string shown = lines.Count switch
            {
                0 => "",
                1 => lines[0],
                _ => $"{PanelUi.Count(actors.Count, "recovered light")} from different sources; select one to see where it came from.",
            };
            // A light to check before a relight is a warning, not a note.
            bool check = lines.Count == 1 && actors.Any(a => BakedContent.Recovered(a)?["review"] is { Kind: SvJsonKind.True });
            review.Visible = check;
            reviewText.Text = check ? shown : "";
            text.Visible = !check && shown.Length > 0;
            text.Text = check ? "" : shown;
        });
    }
}
