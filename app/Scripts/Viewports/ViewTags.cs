using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>One run of text in a tag, in its own colour (an axis letter, a value, a dim unit).</summary>
public readonly record struct TagPart(string Text, Color Color);

/// <summary>
/// Text drawn over a view in <c>_Draw</c> code: tags on the <see cref="EditorTheme.ViewTagBox"/> backdrop (the selection's
/// name, a drag readout, a measurement, the grid scale) and world labels on a smaller pill
/// (<see cref="EditorTheme.WorldLabelBox"/>). Everything here uses the caption size
/// unless asked otherwise, and numbers use the minus sign (−16).
/// </summary>
public static class ViewTags
{
    /// <summary>Space between a tag and the edge of its view, and between a tag and what it labels.</summary>
    public const float Inset = EditorTheme.SpaceM;

    /// <summary>World labels longer than this many characters end in "…".</summary>
    public const int LabelMaxChars = 24;

    private static readonly StyleBoxFlat s_box = EditorTheme.ViewTagBox();
    private static readonly StyleBoxFlat s_labelBox = EditorTheme.WorldLabelBox();

    /// <summary>The backdrop of a tag.</summary>
    public static StyleBox Box => s_box;

    /// <summary>The size of a tag holding <paramref name="parts"/>, backdrop included.</summary>
    public static Vector2 Size(Font font, IReadOnlyList<TagPart> parts, int fontSize = EditorTheme.FontSizeCaption)
    {
        float width = 0;
        foreach (TagPart p in parts)
        {
            width += font.GetStringSize(p.Text, fontSize: fontSize).X;
        }

        return new Vector2(width, font.GetHeight(fontSize)) + s_box.GetMinimumSize();
    }

    /// <summary>Draws a tag with its top left at <paramref name="at"/>; returns its rectangle.</summary>
    public static Rect2 Draw(Control canvas, Vector2 at, IReadOnlyList<TagPart> parts, int fontSize = EditorTheme.FontSizeCaption)
    {
        Font font = canvas.GetThemeDefaultFont();
        var rect = new Rect2(at.Round(), Size(font, parts, fontSize));
        canvas.DrawStyleBox(s_box, rect);
        var pen = new Vector2(rect.Position.X + s_box.ContentMarginLeft, rect.Position.Y + s_box.ContentMarginTop + font.GetAscent(fontSize));
        foreach (TagPart p in parts)
        {
            canvas.DrawString(font, pen, p.Text, fontSize: fontSize, modulate: p.Color);
            pen.X += font.GetStringSize(p.Text, fontSize: fontSize).X;
        }

        return rect;
    }

    /// <summary>Draws a one-colour tag with its top left at <paramref name="at"/>.</summary>
    public static Rect2 Draw(Control canvas, Vector2 at, string text, Color color, int fontSize = EditorTheme.FontSizeCaption) =>
        Draw(canvas, at, [new TagPart(text, color)], fontSize);

    /// <summary>Draws a tag beside a point (below and right of it, as beside a cursor), kept inside the view.</summary>
    public static Rect2 Beside(Control canvas, Vector2 point, IReadOnlyList<TagPart> parts, int fontSize = EditorTheme.FontSizeCaption)
    {
        Vector2 size = Size(canvas.GetThemeDefaultFont(), parts, fontSize);
        return Draw(canvas, Inside(canvas, point + new Vector2(Inset * 2, Inset * 2), size), parts, fontSize);
    }

    /// <summary>Draws one tag per line, stacked below and right of a point (a cursor), kept inside the view.</summary>
    public static void BesideLines(Control canvas, Vector2 point, IEnumerable<TagPart> lines)
    {
        Font font = canvas.GetThemeDefaultFont();
        Vector2 at = point + new Vector2(Inset * 2, Inset * 2);
        foreach (TagPart line in lines)
        {
            TagPart[] parts = [line];
            Rect2 drawn = Draw(canvas, Inside(canvas, at, Size(font, parts)), parts);
            at.Y = drawn.End.Y + EditorTheme.SpaceXxs;
        }
    }

    /// <summary>
    /// Draws several tags stacked beside a point (a cursor), one line each, the stack kept inside the view and clear of
    /// the bottom row where the view's scale tag sits (a long drag readout, then its neighbour snap lines).
    /// </summary>
    public static void BesideStack(Control canvas, Vector2 point, IReadOnlyList<IReadOnlyList<TagPart>> lines)
    {
        Font font = canvas.GetThemeDefaultFont();
        var sizes = lines.Select(l => Size(font, l)).ToArray();
        if (sizes.Length == 0)
        {
            return;
        }

        float width = sizes.Max(s => s.X);
        float height = sizes.Sum(s => s.Y) + EditorTheme.SpaceXxs * (sizes.Length - 1);
        float bottomRow = sizes[0].Y + EditorTheme.SpaceXxs;
        Vector2 at = Inside(canvas, point + new Vector2(Inset * 2, Inset * 2), new Vector2(width, height + bottomRow));
        for (int i = 0; i < lines.Count; i++)
        {
            Rect2 drawn = Draw(canvas, at, lines[i]);
            at.Y = drawn.End.Y + EditorTheme.SpaceXxs;
        }
    }

    /// <summary>The top left that keeps a box of <paramref name="size"/> at <paramref name="at"/> inside the view, at least
    /// <see cref="Inset"/> from its edges.</summary>
    public static Vector2 Inside(Control canvas, Vector2 at, Vector2 size)
    {
        Vector2 max = canvas.Size - size - new Vector2(Inset, Inset);
        return new Vector2(Mathf.Clamp(at.X, Inset, Math.Max(Inset, max.X)), Mathf.Clamp(at.Y, Inset, Math.Max(Inset, max.Y)));
    }

    /// <summary>A world label: caption-size text on a dark pill, its baseline at <paramref name="baseline"/>; returns
    /// the box it covers.</summary>
    public static Rect2 Label(Control canvas, Font font, Vector2 baseline, string text, Color color)
    {
        const int size = EditorTheme.FontSizeCaption;
        Rect2 box = LabelBox(font, baseline, text);
        canvas.DrawStyleBox(s_labelBox, box);
        canvas.DrawString(font, baseline, text, fontSize: size, modulate: color);
        return box;
    }

    /// <summary>The box a <see cref="Label"/> covers, its pill included.</summary>
    public static Rect2 LabelBox(Font font, Vector2 baseline, string text)
    {
        const int size = EditorTheme.FontSizeCaption;
        var textBox = new Rect2(baseline - new Vector2(0, font.GetAscent(size)), font.GetStringSize(text, fontSize: size));
        return textBox.GrowIndividual(s_labelBox.ContentMarginLeft, s_labelBox.ContentMarginTop, s_labelBox.ContentMarginRight, s_labelBox.ContentMarginBottom);
    }

    /// <summary><paramref name="text"/> cut to <see cref="LabelMaxChars"/>, ending in "…".</summary>
    public static string Shorten(string text) => text.Length <= LabelMaxChars ? text : text[..(LabelMaxChars - 1)].TrimEnd() + "…";

    /// <summary>A number for a readout: up to two decimals, with the minus sign.</summary>
    public static string Number(double v) =>
        v.ToString("0.##", CultureInfo.InvariantCulture).Replace('-', '−');

    /// <summary>A world offset or position as "X 80  Y −16  Z 0", the letters in their axis colours.</summary>
    public static TagPart[] Axes(Vec3 v) =>
    [
        new("X ", SceneStyle.AxisX), new(Number(v.X) + "  ", EditorTheme.Text),
        new("Y ", SceneStyle.AxisY), new(Number(v.Y) + "  ", EditorTheme.Text),
        new("Z ", SceneStyle.AxisZ), new(Number(v.Z), EditorTheme.Text),
    ];

    /// <summary>A label and a value: the label dim, the value in the text colour ("Extrude 32").</summary>
    public static TagPart[] Value(string label, string value) =>
        [new(label + " ", EditorTheme.TextDim), new(value, EditorTheme.Text)];

    /// <summary>The parts as plain text (for the status bar).</summary>
    public static string Plain(IEnumerable<TagPart> parts) => string.Concat(parts.Select(p => p.Text)).Trim();
}
