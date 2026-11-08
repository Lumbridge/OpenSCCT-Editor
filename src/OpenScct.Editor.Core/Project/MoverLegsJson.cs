using OpenScct.Content.Json;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Gameplay;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// The JSON of a mover's own per-key timing (<c>"legs"</c> in <c>world/actors.json</c>), in the key form of SVMap 1.2
/// <c>sim/movers.json</c>: one object per key from key 0, <c>{"time": seconds, "smooth": bool}</c>, members at their
/// default (the mover's own MoveTime and glide) left out, trailing empty keys too.
/// </summary>
public static class MoverLegsJson
{
    public static SvJsonArray ToJson(MoverLegs legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        int count = legs.Keys.Count;
        while (count > 0 && legs.Keys[count - 1].IsDefault)
        {
            count--;
        }

        var keys = new SvJsonArray();
        for (int k = 0; k < count; k++)
        {
            var o = new SvJsonObject();
            if (legs.Keys[k].Time is { } time)
            {
                o.Add("time", SvJsonNode.Float(time));
            }

            if (legs.Keys[k].Smooth is { } smooth)
            {
                o.Add("smooth", SvJsonNode.Bool(smooth));
            }

            keys.Add(o);
        }

        return keys;
    }

    /// <summary>Reads the stored keys loosely (a time on key 0 or out of range is dropped, keys past the native resource guard too), so a
    /// hand-edited project still opens; null when nothing is set.</summary>
    public static MoverLegs? FromJson(SvJsonNode? node)
    {
        if (node is not SvJsonArray list)
        {
            return null;
        }

        var keys = new List<MoverKeyTiming>();
        foreach (SvJsonNode item in list.Take(MoverPath.MaxKeys))
        {
            SvJsonObject? o = item as SvJsonObject;
            double seconds = o?["time"] is SvJsonNumber t ? ModelJson.Num(t) : 0;
            float? time = keys.Count > 0 && MoverTiming.IsLegTime(seconds) ? (float)seconds : null;
            bool? smooth = o?["smooth"]?.Kind switch
            {
                SvJsonKind.True => true,
                SvJsonKind.False => false,
                _ => null,
            };
            keys.Add(new MoverKeyTiming(time, smooth));
        }

        return MoverTiming.OrNull(keys);
    }
}
