using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Emitters;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

public partial class EmitterLibraryPanel
{
    private void ToggleEditor()
    {
        bool open = !_editForm.Visible;
        _editForm.Visible = open;
        _description.Visible = !open;
        _edit.Text = open ? "Close Editor" : "Edit…";
        if (open)
        {
            BuildEditor();
            EditorLifetime.Defer(() => PanelUi.ScrollOf(_editForm.GetParent<VBoxContainer>()).ScrollVertical = 0);
        }
    }

    private void BuildEditor(int index = 0)
    {
        ClearForm(_editForm);
        if (_draft is not { } draft) return;
        var systems = new List<(int Actor, int Part, string Label)>();
        for (int actor = 0; actor < draft.Actors.Count; actor++)
        {
            if (EmitterBuild.SystemsOf(draft.Actors[actor]) is not { } parts) continue;
            for (int part = 0; part < parts.Count; part++)
            {
                string kind = String((parts[part] as SvJsonObject)?["class"]).Split('.').Last();
                string owner = draft.Actors.Count > 1 ? $"{draft.Actors[actor].Id} / " : "";
                systems.Add((actor, part, $"{owner}{part + 1}. {kind}"));
            }
        }

        if (systems.Count == 0) return;
        PanelUi.Header(_editForm, "Particle Systems");
        OptionButton choice = PanelUi.Options(_editForm, "Choose the particle system to edit");
        foreach (var part in systems) choice.AddItem(part.Label);
        choice.Select(Math.Min(index, systems.Count - 1));
        HFlowContainer actions = PanelUi.Toolbar(_editForm);
        PanelUi.Button(actions, "Revert", "Restore the saved effect", () => { _draft = _selected; BuildEditor(choice.Selected); _updatePreview.Queue(); });
        PanelUi.Button(actions, "Save as New…", "Save a new copy to Your effects", () => { if (_draft is { } current) SaveDialog(current); });
        if (draft.Group == EmitterLibrary.UserGroup)
        {
            PanelUi.Button(actions, "Save", "Update this saved effect", () => { if (_draft is { } current) Save(current); });
        }

        var fields = new VBoxContainer();
        _editForm.AddChild(fields);
        void EditPart(int item)
        {
            var (actor, part, _) = systems[item];
            BuildFields(fields, actor, part);
        }

        choice.ItemSelected += item => EditPart((int)item);
        EditPart(choice.Selected);
    }

    private void BuildFields(VBoxContainer form, int actor, int part)
    {
        ClearForm(form);
        SvJsonObject original = SystemAt(actor, part);
        string cls = String(original["class"]);
        UeSchema? schema = UeSchema.Load(EditorServices.ContentRoot);
        SvJsonNode? Effective(string name) => ((SystemAt(actor, part)["props"] as SvJsonObject)?[name]) ?? schema?.DefaultOf(cls, name);
        void Set(string name, SvJsonNode value) => Edit(actor, part, name, value);

        NumberField(form, "Particle count", Num(Effective("MaxParticles"), 10), 0, 4096, 1,
            v => Set("MaxParticles", SvJsonNode.Integer((long)v)));
        NumberField(form, "Rate", Num(Effective("ParticlesPerSecond")), 0, 4096, 0.1,
            v => Set("ParticlesPerSecond", SvJsonNode.Float((float)v)));
        var auto = new CheckBox
        {
            Text = "Automatic Initial Spawn",
            ButtonPressed = Effective("AutomaticInitialSpawning")?.Kind != SvJsonKind.False,
            TooltipText = "Fill the system over its first particle lifetime. Turn this off to set the initial rate.",
        };
        form.AddChild(auto);
        SpinBox initial = NumberField(form, "Initial rate", Num(Effective("InitialParticlesPerSecond")), 0, 4096, 0.1,
            v => Set("InitialParticlesPerSecond", SvJsonNode.Float((float)v)));
        initial.Editable = !auto.ButtonPressed;
        auto.Toggled += on => { initial.Editable = !on; Set("AutomaticInitialSpawning", SvJsonNode.Bool(on)); };
        RangeField(form, "Lifetime (s)", Effective("LifetimeRange"), 4, 0, 600, value => Set("LifetimeRange", value));

        PanelUi.Header(form, "Starting Size");
        foreach (string axis in new[] { "X", "Y", "Z" }) VectorRange("StartSizeRange", axis, 100, 0, 100000);
        PanelUi.Header(form, "Starting Velocity");
        foreach (string axis in new[] { "X", "Y", "Z" }) VectorRange("StartVelocityRange", axis, 0, -100000, 100000);
        PanelUi.Header(form, "Acceleration");
        foreach (string axis in new[] { "X", "Y", "Z" })
        {
            NumberField(form, axis, Num(Member(Effective("Acceleration"), axis)), -100000, 100000, 0.1,
                v => Set("Acceleration", Replace(Vector(Effective("Acceleration"), range: false, 0), axis, SvJsonNode.Float((float)v))));
        }

        void VectorRange(string prop, string axis, double fallback, double min, double max) =>
            RangeField(form, axis, Member(Effective(prop), axis), fallback, min, max,
                v => Set(prop, Replace(Vector(Effective(prop), range: true, fallback), axis, v)));

        PanelUi.Header(form, "Appearance");
        LineEdit texture = TextField(form, "Texture", String(Member(Effective("Texture"), "$asset")));
        texture.PlaceholderText = "Package.Group.Texture";
        void ApplyTexture()
        {
            string path = texture.Text.Trim();
            if (path == String(Member(Effective("Texture"), "$asset"))) return;
            Set("Texture", path.Length == 0 ? SvJsonNode.Null : Object(("$asset", SvJsonNode.String(path))));
        }

        texture.TextSubmitted += _ => ApplyTexture();
        texture.FocusExited += ApplyTexture;
        PanelUi.Button(form, "Use Selected Texture", "Use the last texture selected in Assets", () =>
        {
            if (AssetBrowserPanel.CurrentTexture is not { } path) return;
            texture.Text = path;
            ApplyTexture();
        });
        SvJsonNode? tint = Effective("ColorMultiplierRange");
        var pick = new ColorPickerButton
        {
            Color = new Color((float)Num(Member(Member(tint, "X"), "Min"), 1), (float)Num(Member(Member(tint, "Y"), "Min"), 1),
                (float)Num(Member(Member(tint, "Z"), "Min"), 1)),
            EditAlpha = false, SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        PanelUi.Row(form, "Tint", labelWidth: 78).AddChild(pick);
        pick.ColorChanged += color => Set("ColorMultiplierRange", Object(("X", Range(color.R, color.R)),
            ("Y", Range(color.G, color.G)), ("Z", Range(color.B, color.B))));

        var curve = new VBoxContainer();
        form.AddChild(curve);
        var keys = (Effective("ColorScale") as SvJsonArray ?? []).OfType<SvJsonObject>().Select(key => (
            Time: Num(key["RelativeTime"]), Colour: ColorOf(key["Color"]))).ToList();
        var enabled = new CheckBox { Text = "Colours over Life", ButtonPressed = Effective("UseColorScale")?.Kind == SvJsonKind.True };
        curve.AddChild(enabled);
        var keyForm = new VBoxContainer();
        curve.AddChild(keyForm);
        enabled.Toggled += on => { Set("UseColorScale", SvJsonNode.Bool(on)); keyForm.Visible = on; };
        keyForm.Visible = enabled.ButtonPressed;
        void WriteKeys() => Set("ColorScale", new SvJsonArray(keys.OrderBy(k => k.Time).Select(k => Object(
            ("RelativeTime", SvJsonNode.Float((float)k.Time)), ("Color", ColorNode(k.Colour))))));
        void FillKeys()
        {
            ClearForm(keyForm);
            for (int i = 0; i < keys.Count; i++)
            {
                int index = i;
                var row = new HBoxContainer();
                keyForm.AddChild(row);
                SpinBox time = PanelUi.Spin(row, 0, 1, 0.01, keys[i].Time, "Fraction of the particle's lifetime");
                var color = new ColorPickerButton { Color = keys[i].Colour, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddChild(color);
                time.ValueChanged += v => { keys[index] = (v, keys[index].Colour); WriteKeys(); };
                color.ColorChanged += v => { keys[index] = (keys[index].Time, v); WriteKeys(); };
                PanelUi.IconButton(row, "trash", "Remove this colour key", () => { keys.RemoveAt(index); WriteKeys(); FillKeys(); }, danger: true);
            }

            PanelUi.Button(keyForm, "Add Colour Key", "Add a colour at the end of the particle's life", () =>
            {
                if (keys.Count >= 64) return;
                keys.Add((keys.Count == 0 ? 0 : 1, Colors.White));
                WriteKeys();
                FillKeys();
            });
        }

        FillKeys();
    }

    private SvJsonObject SystemAt(int actor, int part) => (SvJsonObject)EmitterBuild.SystemsOf(_draft!.Actors[actor])![part];

    private void Edit(int actor, int part, string name, SvJsonNode value)
    {
        if (_draft is not { } draft) return;
        Actor[] actors = draft.Actors.ToArray();
        Actor changed = actors[actor].Clone();
        SvJsonArray systems = EmitterBuild.SystemsOf(changed)!;
        var original = (SvJsonObject)systems[part];
        SvJsonObject edited = Replace(original, "props", Replace(original["props"] as SvJsonObject ?? new(), name, value));
        if (name == "Texture")
        {
            edited = Replace(Replace(edited, "texture", null), "textureOrigin", null);
        }

        EmitterBuild.WithSystems(changed, new SvJsonArray(systems.Select((p, i) => i == part ? edited : p)));
        actors[actor] = changed;
        _draft = draft with { Actors = actors };
        _updatePreview.Queue();
    }

    private static void ClearForm(Control form)
    {
        foreach (Node child in form.GetChildren())
        {
            form.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static SpinBox NumberField(Control form, string label, double value, double min, double max, double step, Action<double> edit)
    {
        SpinBox field = PanelUi.Spin(PanelUi.Row(form, label, labelWidth: 78), min, max, step, value);
        field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        field.ValueChanged += v => edit(v);
        return field;
    }

    private static void RangeField(Control form, string label, SvJsonNode? range, double fallback, double min, double max, Action<SvJsonObject> edit)
    {
        HBoxContainer row = PanelUi.Row(form, label, labelWidth: 78);
        SpinBox low = PanelUi.Spin(row, min, max, 0.1, Num(Member(range, "Min"), fallback), $"{label} minimum", width: 50);
        SpinBox high = PanelUi.Spin(row, min, max, 0.1, Num(Member(range, "Max"), fallback), $"{label} maximum", width: 50);
        low.SizeFlagsHorizontal = high.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        low.ValueChanged += v => { if (v > high.Value) high.SetValueNoSignal(v); edit(Range(v, high.Value)); };
        high.ValueChanged += v => { if (v < low.Value) low.SetValueNoSignal(v); edit(Range(low.Value, v)); };
    }

    private static SvJsonObject Vector(SvJsonNode? original, bool range, double fallback) => original as SvJsonObject ??
        Object(new[] { "X", "Y", "Z" }.Select(axis => (axis,
            range ? (SvJsonNode)Range(fallback, fallback) : SvJsonNode.Float((float)fallback))).ToArray());

    private static SvJsonObject Range(double min, double max) => Object(("Min", SvJsonNode.Float((float)min)), ("Max", SvJsonNode.Float((float)max)));

    private static SvJsonNode? Member(SvJsonNode? node, string name) => (node as SvJsonObject)?[name];

    private static double Num(SvJsonNode? value, double fallback = 0) => value switch
    {
        SvJsonNumber n when n.IsFloat => n.FloatValue,
        SvJsonNumber n when n.TryGetInt64(out long number) => number,
        _ => fallback,
    };

    private static string String(SvJsonNode? value) => (value as SvJsonString)?.Value ?? "";

    private static Color ColorOf(SvJsonNode? node) => new((float)Num(Member(node, "R"), 255) / 255f,
        (float)Num(Member(node, "G"), 255) / 255f, (float)Num(Member(node, "B"), 255) / 255f, (float)Num(Member(node, "A"), 255) / 255f);

    private static SvJsonObject ColorNode(Color c) => Object(("R", SvJsonNode.Integer(c.R8)), ("G", SvJsonNode.Integer(c.G8)),
        ("B", SvJsonNode.Integer(c.B8)), ("A", SvJsonNode.Integer(c.A8)));

    private static SvJsonObject Replace(SvJsonObject original, string key, SvJsonNode? value)
    {
        var result = new SvJsonObject();
        foreach (var pair in original)
        {
            if (!pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) result.Add(pair.Key, pair.Value);
        }

        if (value is not null) result.Add(key, value);
        return result;
    }

    private static SvJsonObject Object(params (string Name, SvJsonNode Value)[] fields)
    {
        var result = new SvJsonObject();
        foreach (var (name, value) in fields) result.Add(name, value);
        return result;
    }
}
