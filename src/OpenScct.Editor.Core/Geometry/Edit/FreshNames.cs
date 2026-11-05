using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>Hands out ids no object of a map uses (ignoring case), remembering the ones it gave: Stem0, Stem1...</summary>
public sealed class FreshIds(MapProject project)
{
    private readonly HashSet<string> _used = new(project.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

    public string Next(string stem)
    {
        string baseStem = stem.TrimEnd("0123456789".ToCharArray());
        if (baseStem.Length == 0)
        {
            baseStem = "Object";
        }

        for (int i = 0; ; i++)
        {
            string id = baseStem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_used.Add(id))
            {
                return id;
            }
        }
    }
}

/// <summary>Hands out event Tags no actor of a map uses as its Tag or Event (ignoring case): Door1, Door2...</summary>
public sealed class FreshTags(MapProject project)
{
    private readonly HashSet<string> _used = new(
        project.Actors.SelectMany(a => new[] { a.Tag, a.Props.GetString("Event") ?? "" }).Where(t => t.Length > 0),
        StringComparer.OrdinalIgnoreCase);

    public string Next(string tag)
    {
        string stem = tag.TrimEnd("0123456789".ToCharArray());
        if (stem.Length == 0)
        {
            stem = "Tag";
        }

        for (int i = 1; ; i++)
        {
            string fresh = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_used.Add(fresh))
            {
                return fresh;
            }
        }
    }
}
