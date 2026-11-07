using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>Kind-independent views of placed objects (brushes, actors, GEs): lookup by id, class, location, bounds.</summary>
public static class MapObjects
{
    /// <summary>Finds an object by id (ids are unique across kinds, ignoring case).</summary>
    public static ObjectRef? Find(MapProject p, string id) => p.Find(id);

    public static string KindName(ObjectKind k) => k switch
    {
        ObjectKind.Stair => "stair",
        ObjectKind.Brush => "brush",
        ObjectKind.Actor => "actor",
        _ => "ge",
    };

    /// <summary>What a find by class matches: the actor class, <c>Engine.Brush</c> for brushes, <c>GE.&lt;kind&gt;</c> for GEs.</summary>
    public static string ClassOf(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => p.FindActor(r.Id)?.Class ?? "",
        ObjectKind.Brush or ObjectKind.Stair => "Engine.Brush",
        _ => "GE." + (p.FindGe(r.Id)?.Kind ?? ""),
    };

    public static PropSet? Props(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => p.FindActor(r.Id)?.Props,
        ObjectKind.Brush or ObjectKind.Stair => p.FindBrush(r.Id)?.Props,
        _ => null,
    };

    /// <summary>The object's origin: actor/brush Location, or a GE's first point.</summary>
    public static Vec3 Origin(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => p.FindActor(r.Id)?.Location ?? Vec3.Zero,
        ObjectKind.Brush or ObjectKind.Stair => p.FindBrush(r.Id)?.Location ?? Vec3.Zero,
        _ => p.FindGe(r.Id)?.Points.FirstOrDefault() ?? Vec3.Zero,
    };

    /// <summary>World bounds: brush faces, a volume actor's shape, an actor's collision cylinder (when it has one),
    /// or the GE's points.</summary>
    public static (Vec3 Min, Vec3 Max) Bounds(MapProject p, ObjectRef r)
    {
        switch (r.Kind)
        {
            case ObjectKind.Brush or ObjectKind.Stair when p.FindBrush(r.Id) is { } b:
                return BrushBounds(b);
            case ObjectKind.Actor when p.FindActor(r.Id) is { } a:
                return ActorBounds(a);
            case ObjectKind.Ge when p.FindGe(r.Id) is { } g && g.Points.Count > 0:
                return (g.Points.Aggregate(Vec3.Min), g.Points.Aggregate(Vec3.Max));
            default:
                return (Vec3.Zero, Vec3.Zero);
        }
    }

    public static (Vec3 Min, Vec3 Max) BrushBounds(Brush b) => PointsBounds(b.WorldFaces().SelectMany(f => f.Vertices), b.Location);

    public static (Vec3 Min, Vec3 Max) ActorBounds(Actor a)
    {
        if (a.Shape is { Faces.Count: > 0 } shape)
        {
            return PointsBounds(shape.Faces.SelectMany(f => f.Vertices).Select(a.LocalToWorld), a.Location);
        }

        (double radius, double height) = CollisionSize(a);
        var e = new Vec3(radius, radius, height);
        return (a.Location - e, a.Location + e);
    }

    /// <summary>The actor's collision cylinder (radius, half height): stored values, else class defaults, else 0.</summary>
    public static (double Radius, double Height) CollisionSize(Actor a)
    {
        double radius = Prop(a, "CollisionRadius");
        double height = Prop(a, "CollisionHeight");
        return (radius, height);
    }

    /// <summary>A numeric property: the stored value, else the class default (when the class catalog is loaded).</summary>
    public static double Prop(Actor a, string name)
    {
        if (ApiJson.TryNumber(a.Props[name], out double v))
        {
            return v;
        }

        return ApiJson.TryNumber(EditorServices.Classes?.Default(a.Class, name), out v) ? v : 0;
    }

    /// <summary>A property's effective value: stored, else the class default, else null.</summary>
    public static SvJsonNode? EffectiveProp(MapProject p, ObjectRef r, string name)
    {
        if (Props(p, r)?[name] is { } stored)
        {
            return stored;
        }

        return r.Kind == ObjectKind.Actor && p.FindActor(r.Id) is { } a ? EditorServices.Classes?.Default(a.Class, name) : null;
    }

    /// <summary>The effective Tag (UE2: the stored Tag, else the class name).</summary>
    public static string? Tag(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Actor => p.FindActor(r.Id)?.Tag,
        ObjectKind.Brush or ObjectKind.Stair => p.FindBrush(r.Id)?.Props.GetString("Tag"),
        _ => null,
    };

    public static string? Event(MapProject p, ObjectRef r) => Props(p, r)?.GetString("Event") is { Length: > 0 } e && e != "None" ? e : null;

    private static (Vec3 Min, Vec3 Max) PointsBounds(IEnumerable<Vec3> points, Vec3 fallback)
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        bool any = false;
        foreach (Vec3 v in points)
        {
            min = Vec3.Min(min, v);
            max = Vec3.Max(max, v);
            any = true;
        }

        return any ? (min, max) : (fallback, fallback);
    }

    /// <summary>True when <paramref name="className"/> is <paramref name="query"/> or (with subclasses) derives from it.
    /// Short names match too. Without a class catalog only the class itself matches.</summary>
    public static bool ClassMatches(string className, string query, bool subclasses)
    {
        if (NameMatches(className, query))
        {
            return true;
        }

        if (!subclasses || EditorServices.Classes is not { } cat)
        {
            return false;
        }

        string? full = cat.Find(query)?.Name;
        return full is not null && cat.Find(className) is not null && cat.IsA(className, full);
    }

    public static bool NameMatches(string className, string query) =>
        string.Equals(className, query, StringComparison.OrdinalIgnoreCase)
        || (!query.Contains('.') && string.Equals(Short(className), query, StringComparison.OrdinalIgnoreCase));

    public static string Short(string className) => className.Contains('.') ? className[(className.LastIndexOf('.') + 1)..] : className;
}
