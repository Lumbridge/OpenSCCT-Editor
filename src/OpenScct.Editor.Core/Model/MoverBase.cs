using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Model;

/// <summary>
/// A mover starts the game at <c>BasePos + KeyPos[KeyNum]</c> turned to <c>BaseRot + KeyRot[KeyNum]</c>, and UE2's
/// editor moves the base along whenever the mover is moved or turned (AMover::PostEditMove). These helpers do the
/// same for every edit that moves an actor, so a moved mover does not jump back at game start. Actors without a
/// <c>BasePos</c>/<c>BaseRot</c> property (everything but movers) are left alone. A mover drawn at another key than
/// the one it starts at is being placed: moving or turning it sets that key instead, as UE2's editor does for a picked
/// key.
/// </summary>
public static class MoverBase
{
    /// <summary>Carries the base along after <paramref name="before"/> became <paramref name="after"/>, unless the edit
    /// set the base itself.</summary>
    public static void Follow(Actor before, Actor after)
    {
        if (ShownKey(before) is int key && PropValues.TryVector(after.Props["BasePos"], out Vec3 keyBase)
            && PropValues.TryRotator(after.Props["BaseRot"], out Rot keyBaseRot) && SameVector(before, after) && SameRotator(before, after))
        {
            if (after.Location != before.Location)
            {
                after.Props["KeyPos"] = WithElement(after.Props["KeyPos"], key, PropValues.Vector(after.Location - keyBase));
            }

            if (after.Rotation != before.Rotation)
            {
                after.Props["KeyRot"] = WithElement(after.Props["KeyRot"], key, PropValues.Rotator(new Rot(after.Rotation.Pitch - keyBaseRot.Pitch,
                    after.Rotation.Yaw - keyBaseRot.Yaw, after.Rotation.Roll - keyBaseRot.Roll)));
            }

            return;
        }

        if (after.Location != before.Location && SameVector(before, after)
            && PropValues.TryVector(after.Props["BasePos"], out Vec3 basePos))
        {
            after.Props["BasePos"] = PropValues.Vector(basePos + (after.Location - before.Location));
        }

        if (after.Rotation != before.Rotation && SameRotator(before, after)
            && PropValues.TryRotator(after.Props["BaseRot"], out Rot baseRot))
        {
            after.Props["BaseRot"] = PropValues.Rotator(new Rot(baseRot.Pitch + after.Rotation.Pitch - before.Rotation.Pitch,
                baseRot.Yaw + after.Rotation.Yaw - before.Rotation.Yaw, baseRot.Roll + after.Rotation.Roll - before.Rotation.Roll).Tidy());
        }
    }

    /// <summary>
    /// The key a mover is drawn at when that is not its start key (<c>KeyNum</c>): its location and rotation are
    /// exactly <c>BasePos + KeyPos[n]</c> and <c>BaseRot + KeyRot[n]</c> for that key. The key it was last shown at
    /// (<see cref="EditorMeta.MoverKey"/>) wins when several keys share the pose. Null for a mover at its start pose,
    /// one drawn between keys, and other actors.
    /// </summary>
    public static int? ShownKey(Actor a)
    {
        if (!PropValues.TryVector(a.Props["BasePos"], out Vec3 basePos) || !PropValues.TryRotator(a.Props["BaseRot"], out Rot baseRot))
        {
            return null;
        }

        int start = a.Props["KeyNum"] is SvJsonNumber n ? (int)PropValues.Num(n) : 0;
        int count = Math.Clamp(a.Props["NumKeys"] is SvJsonNumber k ? (int)PropValues.Num(k) : 2, 1, 8);
        bool At(int i)
        {
            Vec3 offset = PropValues.TryVector(Element(a.Props["KeyPos"], i), out Vec3 v) ? v : Vec3.Zero;
            Rot turn = PropValues.TryRotator(Element(a.Props["KeyRot"], i), out Rot r) ? r : Rot.Zero;
            return a.Location == basePos + offset
                && a.Rotation.Tidy() == new Rot(baseRot.Pitch + turn.Pitch, baseRot.Yaw + turn.Yaw, baseRot.Roll + turn.Roll).Tidy();
        }

        if (a.Editor.MoverKey is int shown && shown != start && shown < count && At(shown))
        {
            return shown;
        }

        if (At(start))
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            if (At(i))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Moves the base by <paramref name="delta"/> (a pasted or duplicated mover moved with its copy).</summary>
    public static void Shift(Actor a, Vec3 delta)
    {
        if (PropValues.TryVector(a.Props["BasePos"], out Vec3 basePos))
        {
            a.Props["BasePos"] = PropValues.Vector(basePos + delta);
        }
    }

    /// <summary>Maps the mover's KeyPos offsets (world-space vectors) by <paramref name="direction"/>, so a turned or
    /// mirrored mover still slides the same way relative to itself.</summary>
    public static void MapKeyPos(Actor a, Func<Vec3, Vec3> direction)
    {
        if (a.Props["KeyPos"] is { } keys)
        {
            a.Props["KeyPos"] = MapElements(keys, o => IsStruct(o, VectorFields) && PropValues.TryVector(o, out Vec3 v) ? PropValues.Vector(direction(v)) : null);
        }
    }

    /// <summary>Maps the mover's KeyRot offsets (rotators added to BaseRot component by component).</summary>
    public static void MapKeyRot(Actor a, Func<Rot, Rot> offset)
    {
        if (a.Props["KeyRot"] is { } keys)
        {
            a.Props["KeyRot"] = MapElements(keys, o => IsStruct(o, RotatorFields) && PropValues.TryRotator(o, out Rot r) ? PropValues.Rotator(offset(r)) : null);
        }
    }

    /// <summary>Gives a newly placed mover its base where it stands (it starts the game at BasePos + KeyPos[KeyNum]).
    /// Other actors are left alone.</summary>
    public static void Place(Actor a)
    {
        if (!IsMover(a))
        {
            return;
        }

        a.Props["BasePos"] ??= PropValues.Vector(a.Location);
        a.Props["BaseRot"] ??= PropValues.Rotator(a.Rotation);
    }

    public static bool IsMover(Actor a) => Services.EditorServices.Classes is { } cat
        ? cat.IsA(a.Class, "Engine.Mover")
        : a.Class.EndsWith("Mover", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps every element of a static array (<c>{"$elements":{..}}</c> or a list): <paramref name="leaf"/>
    /// gives the new value of an element, or null for an object to walk into.</summary>
    private static SvJsonNode MapElements(SvJsonNode node, Func<SvJsonObject, SvJsonNode?> leaf)
    {
        switch (node)
        {
            case SvJsonObject o when leaf(o) is { } mapped:
                return mapped;
            case SvJsonObject o:
                var copy = new SvJsonObject();
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    copy.Add(kv.Key, MapElements(kv.Value, leaf));
                }

                return copy;
            case SvJsonArray a:
                return new SvJsonArray(a.Select(n => MapElements(n, leaf)));
            default:
                return node;
        }
    }

    /// <summary>Element <paramref name="i"/> of a static array (<c>{"$elements":{"1":..}}</c> or a list), or null.</summary>
    private static SvJsonNode? Element(SvJsonNode? node, int i) => node switch
    {
        SvJsonObject o when o["$elements"] is SvJsonObject e => e[i.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        SvJsonArray arr when i < arr.Count => arr[i],
        _ => null,
    };

    /// <summary>A static array with element <paramref name="i"/> set, in the <c>$elements</c> form.</summary>
    private static SvJsonObject WithElement(SvJsonNode? node, int i, SvJsonNode value)
    {
        var elements = new SvJsonObject();
        for (int k = 0; k < 8; k++)
        {
            if ((k == i ? value : Element(node, k)) is { } item)
            {
                elements.Add(k.ToString(System.Globalization.CultureInfo.InvariantCulture), item);
            }
        }

        var o = new SvJsonObject();
        o.Add("$elements", elements);
        return o;
    }

    private static readonly string[] VectorFields = ["X", "Y", "Z"];
    private static readonly string[] RotatorFields = ["Pitch", "Yaw", "Roll"];

    /// <summary>An element written as a struct of these fields. Zero fields may be left out ({"Y": -142}), and an
    /// array of three elements ({"0","1","2"}) is not one.</summary>
    private static bool IsStruct(SvJsonObject o, string[] fields) =>
        o.Count > 0 && o.All(kv => fields.Contains(kv.Key, StringComparer.Ordinal));

    private static bool SameVector(Actor a, Actor b) =>
        PropValues.TryVector(a.Props["BasePos"], out Vec3 x) == PropValues.TryVector(b.Props["BasePos"], out Vec3 y) && x == y;

    private static bool SameRotator(Actor a, Actor b) =>
        PropValues.TryRotator(a.Props["BaseRot"], out Rot x) == PropValues.TryRotator(b.Props["BaseRot"], out Rot y) && x == y;
}
