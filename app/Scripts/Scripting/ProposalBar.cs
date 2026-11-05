using Godot;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Scripting;

/// <summary>The bar over the views while a proposal is pending: what it is, and Accept, Reject and Frame. When an
/// edit since then means the proposal no longer applies, the bar says why and Accept is off.</summary>
public sealed partial class ProposalBar : PanelContainer
{
    private readonly Label _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _accept;
    private readonly StyleBoxFlat _style;

    public ProposalBar(Action accept, Action reject, Action frame)
    {
        Name = "ProposalBar";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.CenterTop);
        GrowHorizontal = GrowDirection.Both;
        OffsetTop = 28;
        // The palette's raised box, outlined in the ghost's colour: green while the proposal applies, red when not.
        _style = EditorTheme.Box(EditorTheme.Popup, EditorTheme.RadiusL, 1, GhostLayer.CreatedColor, EditorTheme.SpaceM, EditorTheme.SpaceS);
        _style.ShadowColor = EditorTheme.Shadow;
        _style.ShadowSize = EditorTheme.SpaceM;
        AddThemeStyleboxOverride("panel", _style);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(row);
        row.AddChild(_text);
        row.AddChild(MakeButton("Frame", "Move the views to the proposal", frame, "FlatButton"));
        row.AddChild(MakeButton("Reject", "Drop the proposal; the map stays as it is", reject));
        _accept = MakeButton("Accept", "Apply the proposal as one undo step", accept, "PrimaryButton");
        row.AddChild(_accept);
    }

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public ProposalBar()
        : this(() => { }, () => { }, () => { })
    {
    }

    /// <summary>The problem shown now (null when the proposal applies).</summary>
    public string? Problem { get; private set; }

    public void ShowFor(Proposal? p)
    {
        Visible = p is not null;
        Problem = p?.Problem;
        if (p is null)
        {
            return;
        }

        _text.Text = p.Problem is null
            ? $"Proposal: {p.Preview.Label} · {p.Preview.Counts}"
            : $"Proposal: {p.Preview.Label} · no longer applies: {p.Problem}";
        _accept.Disabled = p.Problem is not null;
        _style.BorderColor = p.Problem is null ? GhostLayer.CreatedColor : GhostLayer.DeletedColor;
        TooltipText = $"Proposal {p.Id} by {p.Author ?? "the document's author"}. Green: new, cyan: changed, red: deleted." +
            (p.Problem is null ? "" : "\nThe map changed since it was proposed; undo that edit or reject the proposal.");
        // Re-centre for the new text width.
        ResetSize();
        OffsetLeft = -Size.X / 2;
        OffsetRight = Size.X / 2;
    }

    private static Button MakeButton(string text, string tip, Action run, string variation = "")
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, ThemeTypeVariation = variation };
        b.Pressed += run;
        return b;
    }
}
