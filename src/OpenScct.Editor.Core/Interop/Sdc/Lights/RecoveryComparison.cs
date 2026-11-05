using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc.Lights;

/// <summary>
/// How close a light recovery came to the lights a map really lost, measured against its editor copy: the copy's
/// lights the cook dropped that its lightmaps list (they baked into the BSP), matched one to one (Hungarian) to the
/// recovered lights within a distance. For a map with a known twin, recovered with the twin ignored, it says how good
/// the fitted stand-ins are.
/// </summary>
public static class RecoveryComparison
{
    public static SvJsonObject Compare(MapProject p, string editorCopy, UeSchema? schema, double within = 128)
    {
        UPackage cooked = UPackage.Load(p.Source.File!, p.Id);
        var cookedNames = new HashSet<string>(cooked.Exports.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        UPackage twin = UPackage.Load(editorCopy, p.Id);
        SdcMap map = SdcMapReader.Read(twin, schema);
        (_, UModel? world, SdcLayout layout) = AtlasFingerprint.Of(twin);
        if (layout != SdcLayout.Editor)
        {
            throw new InvalidOperationException($"{editorCopy} is not an editor copy");
        }

        var rules = new ActorRules(ClassDefaults.Create(EditorServices.ContentRoot, EditorServices.Classes));
        double levelBrightness = WorldLighting.LevelBrightness(p, rules.Classes, "Engine.LevelInfo");
        var truth = new List<BakeLight>();
        foreach (SdcActor n in map.Actors.Skip(1))
        {
            SvJsonNode? Value(string prop) => n.Props[prop] ?? schema?.DefaultOf(n.Class, prop);
            if (n.Position == SdcActor.OutsideLevel || SdcMapReader.IsDeleted(n, schema) || cookedNames.Contains(n.Name)
                || !CookRule.DropsLight(Value) || world?.BakedLights.Contains(n.Ref) != true)
            {
                continue;
            }

            truth.Add(WorldLighting.LightOf(rules, SdcOverlay.ActorFrom(n, map, n.Name), 0, levelBrightness, p.Lighting.BakeDefaultLightRadius));
        }

        List<Actor> recovered = p.Actors.Where(a => BakedContent.RecoveredSource(a) is { } s && s != LostLightRecovery.SourceName(RecoveredSource.Orphan))
            .ToList();
        var found = recovered.Select(a => WorldLighting.LightOf(rules, a, 0, levelBrightness, p.Lighting.BakeDefaultLightRadius)).ToList();
        int[] match = Assign(truth, found, within);
        var errors = new List<double>();
        var radius = new List<double>();
        var brightness = new List<double>();
        var angle = new List<double>();
        for (int t = 0; t < truth.Count; t++)
        {
            if (match[t] < 0)
            {
                continue;
            }

            BakeLight a = truth[t], b = found[match[t]];
            errors.Add((a.Location - b.Location).Length);
            radius.Add(b.Radius / a.Radius);
            Vec3 ca = a.Colour, cb = b.Colour;
            brightness.Add(cb.Length / Math.Max(ca.Length, 1e-9));
            double cos = Vec3.Dot(ca, cb) / Math.Max(ca.Length * cb.Length, 1e-12);
            angle.Add(Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI);
        }

        int matched = errors.Count;
        var o = new SvJsonObject();
        o.Add("editorCopy", SvJsonNode.String(editorCopy));
        o.Add("lost", SvJsonNode.Integer(truth.Count));
        o.Add("recovered", SvJsonNode.Integer(found.Count));
        o.Add("matched", SvJsonNode.Integer(matched));
        o.Add("within", SvJsonNode.Float((float)within));
        o.Add("recall", Num(truth.Count == 0 ? 0 : (double)matched / truth.Count));
        o.Add("precision", Num(found.Count == 0 ? 0 : (double)matched / found.Count));
        if (matched > 0)
        {
            o.Add("positionErrorMedian", Num(Quantile(errors, 0.5)));
            o.Add("positionErrorP90", Num(Quantile(errors, 0.9)));
            o.Add("radiusRatioMedian", Num(Quantile(radius, 0.5)));
            o.Add("brightnessRatioMedian", Num(Quantile(brightness, 0.5)));
            o.Add("colourAngleMedian", Num(Quantile(angle, 0.5)));
        }

        // Stand-ins by peak (the bands their confidence is read from): how many sit where a lost light was.
        var hit = new bool[found.Count];
        foreach (int f in match.Where(f => f >= 0))
        {
            hit[f] = true;
        }

        var bands = new SvJsonArray();
        double upper = double.PositiveInfinity;
        foreach (double from in new double[] { 40, 30, 20, 0 })
        {
            int[] inBand = Enumerable.Range(0, found.Count)
                .Where(i => BakedContent.Recovered(recovered[i])?["peak"] is SvJsonNumber pk && PropValues.Num(pk) >= from && PropValues.Num(pk) < upper)
                .ToArray();
            upper = from;
            if (inBand.Length == 0)
            {
                continue;
            }

            var band = new SvJsonObject();
            band.Add("peakFrom", SvJsonNode.Float((float)from));
            band.Add("lights", SvJsonNode.Integer(inBand.Length));
            band.Add("matched", SvJsonNode.Integer(inBand.Count(i => hit[i])));
            bands.Add(band);
        }

        if (bands.Count > 0)
        {
            o.Add("standInsByPeak", bands);
        }

        return o;
    }

    private static SvJsonNode Num(double v) => SvJsonNode.Float((float)Math.Round(v, 3));

    private static double Quantile(List<double> v, double q)
    {
        var s = v.Order().ToList();
        return s[Math.Clamp((int)Math.Round(q * (s.Count - 1)), 0, s.Count - 1)];
    }

    /// <summary>A one-to-one assignment of truth to found lights of least total distance, pairs farther than
    /// <paramref name="within"/> not allowed (Hungarian algorithm on the padded square matrix). Per truth light, its
    /// found light or -1.</summary>
    private static int[] Assign(List<BakeLight> truth, List<BakeLight> found, double within)
    {
        int n = Math.Max(truth.Count, found.Count);
        var result = Enumerable.Repeat(-1, truth.Count).ToArray();
        if (truth.Count == 0 || found.Count == 0)
        {
            return result;
        }

        // Unmatchable pairs cost more than any set of matchable ones, so they are taken last and dropped.
        double big = within * (n + 1);
        double Cost(int i, int j)
        {
            if (i >= truth.Count || j >= found.Count)
            {
                return big;
            }

            double d = (truth[i].Location - found[j].Location).Length;
            return d <= within ? d : big;
        }

        // Rows i = 1..n (truth), columns j = 1..n (found); potentials u, v; p[j] = row matched to column j.
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.MaxValue, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.MaxValue;
                for (int j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    double cur = Cost(i0 - 1, j - 1) - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (int j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        for (int j = 1; j <= n; j++)
        {
            int i = p[j] - 1, f = j - 1;
            if (i < truth.Count && f < found.Count && (truth[i].Location - found[f].Location).Length <= within)
            {
                result[i] = f;
            }
        }

        return result;
    }
}
