using System.Diagnostics;
using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Interop.Sdc.Lights;

/// <summary>How far the light recovery of a cooked .sdc import goes.</summary>
public enum LightRecoveryMode
{
    /// <summary>Keep the cooked bake only (the lights the cook stripped stay lost).</summary>
    Off,

    /// <summary>Exact sources only: the editor copy whose lightmaps are the map's (twin) and lights the file itself still
    /// holds.</summary>
    Exact,

    /// <summary>Exact sources; without a twin, an older editor copy checked light by light against the bake, then
    /// stand-in lights fitted to the bake when its unexplained light is more than noise. The default.</summary>
    Full,
}

public sealed record LightRecoveryOptions
{
    public LightRecoveryMode Mode { get; init; } = LightRecoveryMode.Full;

    /// <summary>The game install (its editor copies and the installs beside it are searched for twins).</summary>
    public string? GameRoot { get; init; }

    /// <summary>More folders of editor copies to search.</summary>
    public IReadOnlyList<string> TwinFolders { get; init; } = [];

    /// <summary>Do not use a twin even when there is one (to measure the fit on a map whose twin is known).</summary>
    public bool IgnoreTwins { get; init; }

    /// <summary>Fit stand-ins even when the map's unexplained light looks like noise (the fit gate,
    /// <see cref="LostLightRecovery.FitGateError"/>).</summary>
    public bool ForceFit { get; init; }

    public LightFitSettings Fit { get; init; } = new();

    public string? ContentRoot { get; init; }

    public Action<string>? Progress { get; init; }

    public CancellationToken Cancel { get; init; }
}

/// <summary>
/// Brings back the static lights the stock editor's cook stripped from a cooked .sdc (docs/SDC-FORMAT.md section 13,
/// <see cref="CookRule"/>), best source first:
/// <list type="number">
/// <item>Tier 1a, an exact twin: the editor copy, from any install or autosave, whose lightmap atlases are byte for byte
/// the cooked map's (<see cref="TwinLibrary"/>). Its lights the cook dropped are the lost lights, with every property.</item>
/// <item>Tier 1b, orphans: lights the cook took out of Level.Actors but still wrote, because an IgnoredEchelonLights or
/// ObjectLights list refers to them. The native read adds them; they are exact.</item>
/// <item>Tier 1c (full mode), a stale twin: an editor copy of the same map that is not the bake's (edited since). Each
/// of its lights is checked against the bake (support: the share of its predicted light the atlases hold): lights under
/// 0.3 are left out, lights the bake cannot check are kept and listed for review.</item>
/// <item>Tier 2 (full mode), fitted stand-ins (<see cref="LightFitter"/>) for whatever light the bake holds that no
/// light explains, when no exact twin exists and that light is more than noise (the fit gate). They restore the look,
/// not the author's lights.</item>
/// <item>Tier 3: the cooked bake itself is always kept; a relight is safe only when an exact twin gave back the lost
/// lights and the bake holds the light of every one of them.</item>
/// </list>
/// Recovered lights are cooked-out lights (they bake, they do not ship: the cook strips them again), live in
/// <see cref="Folder"/>/&lt;source&gt;, and carry their source and confidence in their source JSON
/// (<see cref="BakedContent.Recovered"/>). The map's report is kept in <see cref="MapProject.Extra"/> under
/// <see cref="ReportKey"/>.
/// </summary>
public static class LostLightRecovery
{
    public const string Folder = "Render/Recovered lights";

    public const string ReportKey = "sdcLightRecovery";

    /// <summary>Twin lights whose support is under this are listed for review.</summary>
    public const double ReviewSupport = 0.3;

    /// <summary>A stale twin is taken when the bake holds at least this share of its lights' predicted light.</summary>
    private const double StaleTwinSupport = 0.5;

    /// <summary>Pooled precision of fitted lights by peak (bytes), measured with this fitter on the fifteen of sixteen maps
    /// with a known editor copy that pass the fit gate (2,202 stand-ins, one-to-one within 128 uu of a lost light): the
    /// chance a stand-in sits where an author's light was.</summary>
    private static readonly (double Peak, double Precision)[] s_fitPrecision = [(40, 0.56), (30, 0.35), (20, 0.26), (0, 0.23)];

    /// <summary>The fit gate: stand-ins are fitted only when the remaining lights' rebake is at least this far from the
    /// atlases (mean bytes)...</summary>
    public const double FitGateError = 2.0;

    /// <summary>...and at least this share of the samples hold unexplained light above the noise floor. Below either,
    /// the residual is mostly model error and a fit places noise lights (AeroD: 80 stand-ins for 13 lost lights).</summary>
    public const double FitGateShare = 0.01;

    private const int PlatformPs2Only = 1;

    /// <summary>
    /// Runs the recovery on a project made from cooked copy <paramref name="sdc"/> whose converted SVMap folder is
    /// <paramref name="converted"/>. Lights recovered by an earlier run are replaced. Returns null for an editor copy
    /// (it holds every light).
    /// </summary>
    public static LightRecoveryReport? Run(MapProject p, string sdc, string? converted, UeSchema? schema, LightRecoveryOptions o,
        List<Finding> findings)
    {
        var clock = Stopwatch.StartNew();
        string mapId = p.Id;
        UPackage pkg = UPackage.Load(sdc, mapId);
        (AtlasFingerprint fingerprint, _, SdcLayout layout) = AtlasFingerprint.Of(pkg);
        if (layout != SdcLayout.Cooked)
        {
            return null;
        }

        RemoveEarlier(p);
        var report = new LightRecoveryReport { Map = pkg.Name, Mode = o.Mode, Atlases = fingerprint.Hashes.Count, TwinsIgnored = o.IgnoreTwins };
        var rules = new ActorRules(ClassDefaults.Create(o.ContentRoot ?? EditorServices.ContentRoot, EditorServices.Classes));
        var used = new HashSet<string>(p.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        MarkOrphans(p, rules, OutsideLights(pkg, schema), report);
        if (o.Mode == LightRecoveryMode.Off)
        {
            report.Notes.Add("light recovery is off: the lights the cook stripped stay lost; the cooked bake is kept");
            Finish(p, report, findings, clock);
            return report;
        }

        // Tier 1a / 1c: editor copies.
        TwinLibrary? library = null;
        if (!o.IgnoreTwins && !fingerprint.IsEmpty)
        {
            o.Progress?.Invoke("light recovery: looking for the editor copy the map was cooked from");
            library = TwinLibrary.Scan(TwinLibrary.DefaultFolders(sdc, o.GameRoot, o.TwinFolders), o.Progress);
            report.TwinsSearched = library.Entries.Count;
        }

        var cookedNames = new HashSet<string>(pkg.Exports.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        TwinEntry? exact = library is null ? null : PickExact(library.FindExact(fingerprint, pkg.Name), pkg.Name, cookedNames, report);
        List<TwinLight> twinLights = [];
        if (exact is not null)
        {
            try
            {
                twinLights = TwinLights(exact.Path, mapId, schema, cookedNames);
                report.Twin = exact.Path;
                report.TwinExact = true;
                report.LostEstimate = twinLights.Count;
            }
            catch (Exception ex) when (Unreadable(ex))
            {
                report.Notes.Add($"the editor copy {exact.Path} has this map's lightmaps but its lights do not read ({ex.Message}); it is not used");
                exact = null;
            }
        }

        ImportedBake? bake = Bake(p, converted, rules, report, o);
        if (bake is null)
        {
            AddTwinLights(p, twinLights, RecoveredSource.Twin, used, report);
            Finish(p, report, findings, clock);
            return report;
        }

        double levelBrightness = WorldLighting.LevelBrightness(p, rules.Classes, "Engine.LevelInfo");
        BakeSamples samples = bake.Samples(o.Fit.Thin);
        float[] atlas = samples.Read(bake.Atlases);
        float[] ambient = AmbientOf(samples, bake);
        List<BakeLight> known = KnownLights(p, rules, bake, levelBrightness, null);

        // The class default of LightRadius differs between the editors that baked the stock maps: measure it.
        o.Progress?.Invoke($"light recovery: baking the map's {known.Count(l => l.Baked)} remaining static lights to compare with its atlases");
        double? radius = CalibrateRadius(p, rules, bake, levelBrightness, twinLights, report, o);
        known = KnownLights(p, rules, bake, levelBrightness, radius);
        List<byte[]> knownBake = bake.Bake(known, o.Cancel);
        report.Before = bake.Compare(knownBake);
        float[] residual = Subtract(atlas, samples.Read(knownBake));

        if (exact is not null)
        {
            Support(twinLights, samples, residual, bake, rules, levelBrightness, radius, o.Cancel);
            AddTwinLights(p, twinLights, RecoveredSource.Twin, used, report);
        }
        else if (o.Mode == LightRecoveryMode.Full)
        {
            if (library is not null)
            {
                (TwinEntry Entry, List<TwinLight> Lights)? stale = PickStale(library, fingerprint, pkg.Name, mapId, schema, cookedNames, samples,
                    residual, bake, rules, levelBrightness, radius, report, o);
                if (stale is { } s)
                {
                    report.Twin = s.Entry.Path;
                    AddTwinLights(p, s.Lights, RecoveredSource.StaleTwin, used, report);
                    // The fit works on what the stale twin leaves unexplained.
                    List<BakeLight> withTwin = KnownLights(p, rules, bake, levelBrightness, radius);
                    residual = Subtract(atlas, samples.Read(bake.Bake(withTwin, o.Cancel)));
                }
            }

            Fit(p, samples, atlas, ambient, residual, bake, levelBrightness, used, report, o);
        }

        o.Progress?.Invoke("light recovery: baking again with the recovered lights");
        report.After = bake.Compare(bake.Bake(KnownLights(p, rules, bake, levelBrightness, radius), o.Cancel));
        Finish(p, report, findings, clock);
        return report;
    }

    /// <summary>Runs the recovery again on a project imported from a cooked .sdc (its source file and its baked
    /// world's SVMap folder).</summary>
    public static LightRecoveryReport? Rerun(MapProject p, string? projectFolder, LightRecoveryOptions o, List<Finding> findings)
    {
        if (p.Source is not { Kind: "sdc", File: { } sdc } || !File.Exists(sdc))
        {
            throw new InvalidOperationException("the project was not imported from an .sdc file that is still there (map.json source.file)");
        }

        string? converted = p.Baked?.SourceMap is { Length: > 0 } m
            ? Path.IsPathRooted(m) || projectFolder is null ? m : Path.GetFullPath(Path.Combine(projectFolder, m))
            : null;
        UeSchema? schema = UeSchema.Load(o.ContentRoot ?? EditorServices.ContentRoot);
        return Run(p, sdc, converted, schema, o, findings);
    }

    /// <summary>One recovered light in plain words (where it came from, how sure, how much of its light the bake
    /// holds), or null for a light that was not recovered.</summary>
    public static string? Explain(Actor a)
    {
        if (BakedContent.Recovered(a) is not { } rec)
        {
            return null;
        }

        string what = BakedContent.RecoveredSource(a) switch
        {
            "twin" => "From the map's editor copy, whose lightmaps are this map's: the author's light, as it was baked",
            "orphan" => "Kept in the cooked file outside the level (another actor refers to it): the author's light",
            "staleTwin" => "From an editor copy of the map that is not this bake (older or newer): kept because the cooked lightmaps "
                + "hold its light, but it may not be the light this bake had",
            _ => "A stand-in fitted to the cooked lightmaps: it restores the look there, it is not the author's light",
        };
        double Num(string key) => rec[key] switch
        {
            SvJsonNumber { IsFloat: true } f => f.FloatValue,
            SvJsonNumber n when n.TryGetInt64(out long l) => l,
            _ => double.NaN,
        };
        string confidence = double.IsNaN(Num("confidence")) ? "" : $". Confidence {Num("confidence"):0.00}";
        string support = double.IsNaN(Num("support")) ? "" : $", {Num("support"):0%} of its light is in the bake";
        string review = rec["review"] is not { Kind: SvJsonKind.True } ? ""
            : double.IsNaN(Num("support")) ? ". Check it: the cooked lightmaps cannot confirm it (it lights too few lightmap texels)"
            : ". Check it: the cooked lightmaps hold little of its light";
        return $"{what}{confidence}{support}{review}." + (BakedContent.IsCookedOut(a) ? " It bakes into the lightmaps and is left out of the game." : "");
    }

    /// <summary>The project's recovered lights and its recovery report, as the API query and <c>ose sdc-lights</c>
    /// give them.</summary>
    public static SvJsonObject Describe(MapProject p)
    {
        var o = new SvJsonObject();
        o.Add("report", p.Extra[ReportKey] ?? SvJsonNode.Null);
        var lights = new SvJsonArray();
        foreach (Actor a in p.Actors)
        {
            if (BakedContent.Recovered(a) is not { } rec)
            {
                continue;
            }

            var l = new SvJsonObject();
            l.Add("id", SvJsonNode.String(a.Id));
            l.Add("folder", SvJsonNode.String(a.Editor.Folder ?? ""));
            l.Add("loc", new SvJsonArray([SvJsonNode.Float((float)a.Location.X), SvJsonNode.Float((float)a.Location.Y), SvJsonNode.Float((float)a.Location.Z)]));
            foreach ((string key, SvJsonNode value) in rec)
            {
                l.Add(key, value);
            }

            foreach (string prop in new[] { "LightBrightness", "LightRadius", "LightHue", "LightSaturation", "LightEffect" })
            {
                if (a.Props[prop] is { } v)
                {
                    l.Add(prop, v);
                }
            }

            lights.Add(l);
        }

        o.Add("lights", lights);
        return o;
    }

    // ---- tiers ------------------------------------------------------------------------------------------------------

    /// <summary>A light of a twin the cook dropped, as a project actor, with what the twin says about it.</summary>
    private sealed class TwinLight(Actor actor, bool bspLit)
    {
        public Actor Actor { get; } = actor;

        /// <summary>The twin's lightmaps list it: its last LIGHT APPLY baked it into the BSP.</summary>
        public bool BspLit { get; } = bspLit;

        public double? Support { get; set; }
    }

    /// <summary>The cook-dropped lights of an editor copy that the cooked file does not hold.</summary>
    private static List<TwinLight> TwinLights(string path, string mapId, UeSchema? schema, HashSet<string> cookedNames)
    {
        UPackage twin = UPackage.Load(path, mapId);
        SdcMap map = SdcMapReader.Read(twin, schema);
        (_, UModel? world, _) = AtlasFingerprint.Of(twin);
        var result = new List<TwinLight>();
        foreach (SdcActor n in map.Actors.Skip(1))
        {
            bool light = schema?.IsA(n.Class, "Engine.Light") ?? InteropValues.ShortClass(n.Class).Contains("Light", StringComparison.OrdinalIgnoreCase);
            SvJsonNode? Value(string prop) => n.Props[prop] ?? schema?.DefaultOf(n.Class, prop);
            if (!light || n.Position == SdcActor.OutsideLevel || SdcMapReader.IsDeleted(n, schema) || cookedNames.Contains(n.Name)
                || !CookRule.DropsLight(Value) || (Value("Platform") is SvJsonNumber pl && PropValues.Num(pl) == PlatformPs2Only))
            {
                continue;
            }

            Actor a = SdcOverlay.ActorFrom(n, map, n.Name);
            var src = new SvJsonObject();
            var sdcRef = new SvJsonObject();
            sdcRef.Add("file", SvJsonNode.String(path));
            sdcRef.Add("export", SvJsonNode.Integer(n.Ref));
            src.Add("sdc", sdcRef);
            a.Source = InteropValues.Compact(src);
            result.Add(new TwinLight(a, world?.BakedLights.Contains(n.Ref) ?? false));
        }

        return result;
    }

    /// <summary>
    /// Lights a cooked copy holds outside Level.Actors (<paramref name="outside"/>): the cook dropped them from the level
    /// but wrote them, because a saved object refers to them (an IgnoredEchelonLights or ObjectLights list). Exact. The
    /// conversion keeps most of them as render lights; the overlay adds the rest as cooked-out lights. Ones an earlier
    /// run marked are counted again (a rerun keeps them); PS2-only lights are skipped (their category is Skipped).
    /// </summary>
    private static void MarkOrphans(MapProject p, ActorRules rules, IReadOnlySet<string> outside, LightRecoveryReport report)
    {
        string orphan = SourceName(RecoveredSource.Orphan);
        foreach (Actor a in p.Actors)
        {
            if (!(outside.Contains(a.Id) || BakedContent.IsCookedOut(a)) || !rules.IsLight(a) || rules.CategoryOf(a) == ActorCategory.Skipped)
            {
                continue;
            }

            if (BakedContent.RecoveredSource(a) is { } s)
            {
                if (s == orphan)
                {
                    report.Count(RecoveredSource.Orphan);
                }

                continue;
            }

            Mark(a, RecoveredSource.Orphan, 1, null, null);
            report.Count(RecoveredSource.Orphan);
        }

        if (report.Counts.GetValueOrDefault(RecoveredSource.Orphan) is > 0 and var n)
        {
            report.Notes.Add($"{n} lights the cook took out of the level are still in the file (another object refers to them): they are the "
                + "author's and bake as they did");
        }
    }

    /// <summary>
    /// Adds a twin's lights. An exact twin's lights all come back (they are the author's), those its bake holds little
    /// of listed for review. A stale twin's lights come back only where the bake holds their light; those the bake
    /// cannot check (too few lightmap texels) come back listed for review.
    /// </summary>
    private static void AddTwinLights(MapProject p, List<TwinLight> lights, RecoveredSource source, HashSet<string> used, LightRecoveryReport report)
    {
        var review = new List<string>();
        int added = 0, left = 0;
        foreach (TwinLight t in lights)
        {
            if (source == RecoveredSource.StaleTwin && t.Support is < ReviewSupport)
            {
                left++;
                continue;
            }

            Actor a = t.Actor;
            a.Id = Unique(a.Id, used);
            bool flag = source == RecoveredSource.Twin ? t.Support is < ReviewSupport : t.Support is null;
            double confidence = source == RecoveredSource.Twin ? 1 : t.Support is { } sup ? Math.Min(sup, 1) : 0.3;
            Mark(a, source, confidence, t.Support, t.BspLit, flag);
            p.Actors.Add(a);
            report.Count(source);
            added++;
            if (flag)
            {
                review.Add(a.Id);
            }
        }

        report.Review.AddRange(review);
        if (lights.Count == 0)
        {
            return;
        }

        if (source == RecoveredSource.Twin)
        {
            int notBaked = lights.Count(l => !l.BspLit);
            report.Notes.Add($"{lights.Count} lights the cook dropped came back from the editor copy {report.Twin}, whose atlases are this map's byte for byte"
                + (notBaked > 0 ? $" ({notBaked} of them light no BSP lightmap of the copy: static meshes only, or added after its last light build)" : "")
                + (review.Count > 0 ? $"; the cooked lightmaps hold less than {ReviewSupport:0.0} of the light of {review.Count} of them, listed for review" : ""));
            return;
        }

        report.Notes.Add($"{added} lights came from the editor copy {report.Twin}, which is not this map's bake ({left} left out: the cooked "
            + $"lightmaps hold less than {ReviewSupport:0.0} of their light)"
            + (review.Count > 0 ? $", and {review.Count} the lightmaps cannot check are listed for review" : ""));
    }

    /// <summary>
    /// The exact twin among editor copies with the cooked map's atlases (<see cref="TwinLibrary.FindExact"/>). Two game
    /// modes of one map can share a bake (MuseD and MuseE), so when none has the cooked package's name and they are of
    /// different maps, the one holding most of the cooked map's objects by name is taken.
    /// </summary>
    private static TwinEntry? PickExact(IReadOnlyList<TwinEntry> candidates, string package, HashSet<string> cookedNames, LightRecoveryReport report)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates[0].Package.Equals(package, StringComparison.OrdinalIgnoreCase)
            || candidates.Select(e => e.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return candidates[0];
        }

        (TwinEntry Entry, int Shared, int Extra)? best = null;
        foreach (TwinEntry e in candidates)
        {
            int shared, extra;
            try
            {
                var names = UPackage.Load(e.Path).Exports.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                shared = cookedNames.Count(names.Contains);
                extra = names.Count - shared;
            }
            catch (Exception ex) when (Unreadable(ex))
            {
                continue;
            }

            if (best is null || shared > best.Value.Shared || (shared == best.Value.Shared && extra < best.Value.Extra))
            {
                best = (e, shared, extra);
            }
        }

        if (best is { } b)
        {
            report.Notes.Add($"{candidates.Count} editor copies of different maps have these lightmaps "
                + $"({string.Join(", ", candidates.Select(e => e.Package).Distinct(StringComparer.OrdinalIgnoreCase))}); "
                + $"{b.Entry.Package} holds most of the cooked map's objects ({b.Shared} of {cookedNames.Count})");
        }

        return best?.Entry;
    }

    /// <summary>The names of the lights a cooked copy holds outside Level.Actors.</summary>
    private static HashSet<string> OutsideLights(UPackage pkg, UeSchema? schema)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema is null)
        {
            return names;
        }

        foreach (SdcActor n in SdcMapReader.Read(pkg, schema).Actors)
        {
            if (n.Position == SdcActor.OutsideLevel && schema.IsA(n.Class, "Engine.Light") && !SdcMapReader.IsDeleted(n, schema))
            {
                names.Add(n.Name);
            }
        }

        return names;
    }

    /// <summary>A file of another install that fails to read for any reason is skipped, never the import's failure.</summary>
    private static bool Unreadable(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    /// <summary>The editor copy that explains the bake best of those that are not its exact twin, when the bake holds at
    /// least half of its lights' predicted light.</summary>
    private static (TwinEntry, List<TwinLight>)? PickStale(TwinLibrary library, AtlasFingerprint fingerprint, string package, string mapId,
        UeSchema? schema, HashSet<string> cookedNames, BakeSamples samples, float[] residual, ImportedBake bake, ActorRules rules,
        double levelBrightness, double? radius, LightRecoveryReport report, LightRecoveryOptions o)
    {
        (TwinEntry, List<TwinLight>, double)? best = null;
        foreach (TwinEntry e in library.Stale(fingerprint, package).Take(3))
        {
            List<TwinLight> lights;
            try
            {
                lights = TwinLights(e.Path, mapId, schema, cookedNames);
            }
            catch (Exception ex) when (Unreadable(ex))
            {
                report.Notes.Add($"editor copy {e.Path} does not read ({ex.Message}); skipped");
                continue;
            }

            if (lights.Count == 0)
            {
                continue;
            }

            o.Progress?.Invoke($"light recovery: checking {lights.Count} lights of {e.Path} against the bake");
            double overall = Support(lights, samples, residual, bake, rules, levelBrightness, radius, o.Cancel);
            report.Notes.Add($"editor copy {e.Path} ({e.Fingerprint.Shared(fingerprint)} of {fingerprint.Hashes.Count} atlases in common): "
                + $"the bake holds {overall:0.00} of its {lights.Count} lights' predicted light");
            if (overall >= StaleTwinSupport && (best is null || overall > best.Value.Item3))
            {
                best = (e, lights, overall);
            }
        }

        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    /// <summary>
    /// Per twin light, the share of its predicted light (hard shadows, on texels it lights by 8 bytes or more) the
    /// residual holds: near 1 when the bake has it, near 0 when it was added or moved after the bake. Returns the
    /// share over all of them.
    /// </summary>
    private static double Support(List<TwinLight> lights, BakeSamples samples, float[] residual, ImportedBake bake, ActorRules rules,
        double levelBrightness, double? radius, CancellationToken cancel)
    {
        var grid = new SampleGrid(samples.Position, 256);
        double held = 0, predicted = 0;
        foreach (TwinLight t in lights)
        {
            cancel.ThrowIfCancellationRequested();
            BakeLight l = WorldLighting.LightOf(rules, t.Actor, bake.World.ZoneAt(t.Actor.Location), levelBrightness, radius);
            if (!l.Baked)
            {
                continue;
            }

            int[] idx = l.IsSun ? Enumerable.Range(0, samples.Count).ToArray() : grid.Within(samples.Position, l.Location, l.Radius);
            Vec3 colour = l.Colour * 255;
            var own = new double[idx.Length];
            var res = new double[idx.Length];
            Parallel.For(0, idx.Length, k =>
            {
                int i = idx[k];
                double f = l.Surface(samples.Position[i], samples.Normal[i], samples.TwoSided[i]);
                if (f <= 0 || (l.OwnZoneOnly && samples.Zone[i] != l.Zone)
                    || !LightmapBaker.Lit(l, samples.Position[i], samples.Normal[i], samples.TwoSided[i], l.Location, -l.Direction, bake.Occluders))
                {
                    return;
                }

                own[k] = (Math.Min(255, f * colour.X) + Math.Min(255, f * colour.Y) + Math.Min(255, f * colour.Z)) / 3;
                res[k] = Math.Max(0, (residual[3 * i] + residual[(3 * i) + 1] + residual[(3 * i) + 2]) / 3);
            });
            double h = 0, pr = 0;
            int n = 0;
            for (int k = 0; k < idx.Length; k++)
            {
                if (own[k] >= 8)
                {
                    h += Math.Min(res[k], own[k]);
                    pr += own[k];
                    n++;
                }
            }

            if (n >= 5)
            {
                t.Support = h / pr;
                held += h;
                predicted += pr;
            }
        }

        return predicted > 0 ? held / predicted : 0;
    }

    /// <summary>Tier 2: stand-ins for the light no remaining or recovered light explains.</summary>
    private static void Fit(MapProject p, BakeSamples samples, float[] atlas, float[] ambient, float[] residual, ImportedBake bake,
        double levelBrightness, HashSet<string> used, LightRecoveryReport report, LightRecoveryOptions o)
    {
        var saturated = new bool[samples.Count];
        var knownLight = new float[residual.Length];
        for (int i = 0; i < samples.Count; i++)
        {
            saturated[i] = Math.Max(atlas[3 * i], Math.Max(atlas[(3 * i) + 1], atlas[(3 * i) + 2])) >= 254.5f;
            for (int k = 0; k < 3; k++)
            {
                // What the known lights add: the atlas less the ambient fill and what is left unexplained.
                knownLight[(3 * i) + k] = atlas[(3 * i) + k] - ambient[(3 * i) + k] - residual[(3 * i) + k];
            }
        }

        var fitter = new LightFitter(samples, residual, saturated, bake.Occluders, o.Fit);
        double floor = Math.Max(fitter.NoiseFloor(knownLight), o.Fit.StopResidual);
        (double share, double excess) = fitter.Structure(floor);
        report.NoiseFloor = floor;
        report.MeasuredNoise = fitter.MeasuredFloor;
        report.Samples = samples.Count;
        report.AboveFloor = share;
        report.ExcessAboveFloor = excess;
        double before = report.Before?.Mae ?? double.PositiveInfinity;
        string residualText = $"the remaining lights rebake {before:0.00} bytes from the cooked lightmaps and {share:0.0%} of the lightmap samples "
            + $"hold unexplained light above the noise floor ({floor:0.0} bytes)";
        if (!o.ForceFit && (before < FitGateError || share < FitGateShare))
        {
            report.FitGated = true;
            string needs = string.Join(" and ", new[]
            {
                before < FitGateError ? $"at least {FitGateError:0.0} bytes" : null,
                share < FitGateShare ? $"at least {FitGateShare:0%} of the samples" : null,
            }.OfType<string>());
            report.Notes.Add($"no stand-ins were fitted: {residualText}; a fit needs {needs}, so what is left is mostly model error and a "
                + "fit would place noise lights (ose sdc-lights --force-fit fits anyway)");
            return;
        }

        o.Progress?.Invoke($"light recovery: fitting stand-in lights to {samples.Count} lightmap samples");
        List<FittedLight> fitted = fitter.Run(floor, o.Progress, o.Cancel);
        int n = 0;
        foreach (FittedLight f in fitted)
        {
            Actor a = StandIn(f, levelBrightness, Unique($"RecoveredLight{n++}", used));
            double confidence = s_fitPrecision.First(b => f.Peak >= b.Peak).Precision;
            Mark(a, RecoveredSource.Fitted, confidence, f.Support, null, peak: f.Peak);
            p.Actors.Add(a);
            report.Count(RecoveredSource.Fitted);
        }

        report.Notes.Add(fitted.Count == 0
            ? $"{residualText}, but no stand-in light explains it"
            : $"{fitted.Count} stand-in lights were fitted to the bake ({residualText}); they restore the look, not the author's lights "
                + "(about two in five sit where a lost light was)");
    }

    /// <summary>A fitted light as a Light actor: colour through FGetHSV, LightRadius stored (the class default is not what
    /// was fitted), a bake-only static light as the cook strips (so a cook drops it again).</summary>
    private static Actor StandIn(FittedLight f, double levelBrightness, string id)
    {
        (double brightness, int hue, int saturation) = ColourOf(f.Amplitude, levelBrightness);
        var a = new Actor(id, "Engine.Light") { Location = f.Location };
        a.Props.Set("LightBrightness", SvJsonNode.Float((float)Math.Round(brightness, 2)));
        a.Props.Set("LightHue", SvJsonNode.Integer(hue));
        a.Props.Set("LightSaturation", SvJsonNode.Integer(saturation));
        a.Props.Set("LightRadius", SvJsonNode.Float((float)Math.Round((f.Radius / 25) - 1, 2)));
        a.Props.Set("Group", SvJsonNode.String("RecoveredLights"));
        return a;
    }

    private static readonly Lazy<Vec3[]> s_hues = new(() =>
    {
        var table = new Vec3[256 * 256];
        for (int h = 0; h < 256; h++)
        {
            for (int s = 0; s < 256; s++)
            {
                LightRgb c = ActorLightRules.Colour((byte)h, (byte)s, 1f);
                table[(h * 256) + s] = new Vec3(c.R, c.G, c.B);
            }
        }

        return table;
    });

    /// <summary>LightBrightness, LightHue and LightSaturation whose bake colour is closest to an amplitude (bytes at a
    /// full term): the hue and saturation of smallest angle, then the brightness by least squares.</summary>
    public static (double Brightness, int Hue, int Saturation) ColourOf(Vec3 amplitude, double levelBrightness)
    {
        Vec3 a = amplitude / 255;
        if (a.Length <= 0)
        {
            return (0, 0, 255);
        }

        Vec3[] table = s_hues.Value;
        Vec3 dir = a.Normalized();
        int best = (0 * 256) + 255;
        double bestCos = double.MinValue;
        for (int i = 0; i < table.Length; i++)
        {
            double cos = Vec3.Dot(table[i], dir) / table[i].Length;
            if (cos > bestCos)
            {
                (bestCos, best) = (cos, i);
            }
        }

        Vec3 unit = table[best] * Math.Max(levelBrightness, 1e-6);
        return (Vec3.Dot(a, unit) / Vec3.Dot(unit, unit), best / 256, best % 256);
    }

    // ---- the bake ---------------------------------------------------------------------------------------------------

    private static ImportedBake? Bake(MapProject p, string? converted, ActorRules rules, LightRecoveryReport report, LightRecoveryOptions o)
    {
        if (converted is null || !File.Exists(Path.Combine(converted, "render", "world.glb")) || !File.Exists(Path.Combine(converted, "render", "lighting.json")))
        {
            report.Notes.Add("the converted map has no baked render to compare with, so no light could be checked or fitted");
            return null;
        }

        o.Progress?.Invoke("light recovery: reading the converted map's lightmaps and shadow scene");
        var byId = p.Actors.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        bool CastsShadow(string id) => byId.TryGetValue(id, out Actor? a) && rules.Flag(a, "bShadowCast") && !rules.Flag(a, "bHidden");
        PropSet li = p.LevelInfo;
        Vec3 levelAmbient = LightColour.LightmapAmbient(li.GetInt("AmbientHue"), li["AmbientSaturation"] is null ? 255 : li.GetInt("AmbientSaturation"),
            li.GetInt("AmbientBrightness"));
        ImportedBake bake = ImportedBake.Load(converted, CastsShadow, levelAmbient);
        report.MeshOccluders = bake.World.MeshTriangles;
        return bake;
    }

    /// <summary>The project's lights as a relight sees them: render and gameplay lights, and the cooked-out (and
    /// recovered) lights that only bake.</summary>
    private static List<BakeLight> KnownLights(MapProject p, ActorRules rules, ImportedBake bake, double levelBrightness, double? radius)
    {
        var lights = new List<BakeLight>();
        foreach (Actor a in p.Actors)
        {
            ActorCategory category = rules.CategoryOf(a);
            if (!rules.IsLight(a) || category is ActorCategory.Editor or ActorCategory.Skipped)
            {
                continue;
            }

            bool cookedOut = BakedContent.IsCookedOut(a);
            lights.Add(WorldLighting.LightOf(rules, a, bake.World.ZoneAt(a.Location), levelBrightness, radius) with
            {
                Gameplay = category == ActorCategory.Gameplay,
                BakeOnly = cookedOut,
            });
        }

        return lights;
    }

    /// <summary>
    /// The LightRadius a light that stores none baked with: the class default (64) or 32, whichever bake of the map's
    /// lights (and an exact twin's) is nearer its atlases. Null when 64 wins or no light lacks the property.
    /// </summary>
    private static double? CalibrateRadius(MapProject p, ActorRules rules, ImportedBake bake, double levelBrightness, List<TwinLight> twin,
        LightRecoveryReport report, LightRecoveryOptions o)
    {
        int lacking = p.Actors.Concat(twin.Select(t => t.Actor))
            .Count(a => rules.IsLight(a) && a.Props["LightRadius"] is null && rules.CategoryOf(a) is not (ActorCategory.Editor or ActorCategory.Skipped));
        if (lacking == 0)
        {
            return null;
        }

        BakeError Error(double? radius)
        {
            List<BakeLight> lights = KnownLights(p, rules, bake, levelBrightness, radius);
            lights.AddRange(twin.Select(t => WorldLighting.LightOf(rules, t.Actor, bake.World.ZoneAt(t.Actor.Location), levelBrightness, radius)
                with { BakeOnly = true }));
            return bake.Compare(bake.Bake(lights, o.Cancel));
        }

        BakeError at64 = Error(null), at32 = Error(32);
        report.RadiusTrial = $"{lacking} lights store no LightRadius: as 64 (class default) {at64.Mae:0.00}, as 32 {at32.Mae:0.00} MAE";
        if (at32.Mae < at64.Mae)
        {
            report.DefaultRadius = 32;
            p.Lighting.BakeDefaultLightRadius = 32;
            return 32;
        }

        report.DefaultRadius = 64;
        p.Lighting.BakeDefaultLightRadius = null;
        return null;
    }

    private static float[] AmbientOf(BakeSamples samples, ImportedBake bake)
    {
        var amb = new float[samples.Count * 3];
        for (int i = 0; i < samples.Count; i++)
        {
            Vec3 a = bake.Ambient(samples.Zone[i]);
            (amb[3 * i], amb[(3 * i) + 1], amb[(3 * i) + 2]) = ((float)Math.Floor(a.X * 255 + 1e-6), (float)Math.Floor(a.Y * 255 + 1e-6),
                (float)Math.Floor(a.Z * 255 + 1e-6));
        }

        return amb;
    }

    private static float[] Subtract(float[] a, float[] b)
    {
        var d = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            d[i] = a[i] - b[i];
        }

        return d;
    }

    // ---- project ----------------------------------------------------------------------------------------------------

    /// <summary>Undoes a run that failed part way: the lights it added, its calibration and its report go.</summary>
    public static void Discard(MapProject p) => RemoveEarlier(p);

    /// <summary>Drops the lights an earlier run added (twins and fits; orphans come from the file and stay) and its
    /// calibration.</summary>
    private static void RemoveEarlier(MapProject p)
    {
        p.Actors.RemoveAll(a => BakedContent.RecoveredSource(a) is { } s && s != SourceName(RecoveredSource.Orphan));
        p.Lighting.BakeDefaultLightRadius = null;
        p.Extra = Without(p.Extra, ReportKey);
    }

    private static SvJsonObject Without(SvJsonObject o, params string[] keys)
    {
        var copy = new SvJsonObject();
        foreach ((string key, SvJsonNode value) in o)
        {
            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                copy.Add(key, value);
            }
        }

        return copy;
    }

    /// <summary>Marks a recovered light: cooked out (it bakes, it does not ship), its source and confidence, its folder.
    /// A light kept in the file stays as the import made it (the conversion keeps most of them as render lights).</summary>
    private static void Mark(Actor a, RecoveredSource source, double confidence, double? support, bool? bspLit, bool review = false,
        double? peak = null)
    {
        bool cookedOut = source != RecoveredSource.Orphan || BakedContent.IsCookedOut(a);
        SvJsonObject src = Without(a.Source is { Length: > 0 } s && s[0] == '{' && SvJson.Parse(s) is SvJsonObject o ? o : new SvJsonObject(),
            "cookedOut", "recovered");
        if (cookedOut)
        {
            src.Add("cookedOut", SvJsonNode.True);
        }

        var rec = new SvJsonObject();
        rec.Add("source", SvJsonNode.String(SourceName(source)));
        rec.Add("confidence", SvJsonNode.Float((float)Math.Round(confidence, 3)));
        if (support is { } sup)
        {
            rec.Add("support", SvJsonNode.Float((float)Math.Round(sup, 3)));
        }

        if (bspLit is { } lit)
        {
            rec.Add("bspLit", SvJsonNode.Bool(lit));
        }

        if (peak is { } pk)
        {
            // A stand-in's brightest channel at a full term, bytes: what its confidence was read from.
            rec.Add("peak", SvJsonNode.Float((float)Math.Round(pk, 1)));
        }

        if (review)
        {
            rec.Add("review", SvJsonNode.True);
        }

        src.Add("recovered", rec);
        a.Source = InteropValues.Compact(src);
        a.Editor.Folder = $"{Folder}/{FolderName(source)}";
    }

    public static string SourceName(RecoveredSource s) => s switch
    {
        RecoveredSource.Twin => "twin",
        RecoveredSource.Orphan => "orphan",
        RecoveredSource.StaleTwin => "staleTwin",
        _ => "fitted",
    };

    public static string FolderName(RecoveredSource s) => s switch
    {
        RecoveredSource.Twin => "Editor copy",
        RecoveredSource.Orphan => "Kept in file",
        RecoveredSource.StaleTwin => "Older editor copy",
        _ => "Fitted stand-ins",
    };

    private static string Unique(string id, HashSet<string> used)
    {
        string unique = id;
        for (int i = 1; !used.Add(unique); i++)
        {
            unique = id + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }

    private static void Finish(MapProject p, LightRecoveryReport report, List<Finding> findings, Stopwatch clock)
    {
        report.Seconds = clock.Elapsed.TotalSeconds;
        report.UnsafeReason = UnsafeReason(report);
        p.Extra = Without(p.Extra, ReportKey);
        p.Extra.Add(ReportKey, report.ToJson());
        ObjectRef[] review = report.Review.Take(50).Select(ObjectRef.Actor).ToArray();
        findings.Add(Finding.Info("import.sdcLights", report.Summary()));
        if (review.Length > 0)
        {
            // Even a byte-identical twin can hold lights added or moved after its last light build (the save keeps
            // the old atlases), so its lights get the same check.
            string twin = report.TwinExact ? "the map's editor copy" : "an editor copy that is not this map's bake";
            findings.Add(Finding.Warning("import.sdcLightsReview",
                $"{report.Review.Count} lights from {twin} are not confirmed by the cooked lightmaps (they hold little of their light, or "
                + "the light reaches too few texels to tell; added, moved or changed after its last light build?): check them before a relight",
                review));
        }

        if (report.UnsafeReason is { } why)
        {
            findings.Add(Finding.Warning("import.sdcRelightUnsafe",
                $"a relight of this map will not match its cooked lightmaps: {why}. The cooked lightmaps are kept until you relight"));
        }
    }

    /// <summary>
    /// Why a relight with the project's lights would not reproduce the cooked lightmaps, or null when it would: an exact
    /// twin gave back the lost lights, the bake was checked and holds the light of every one of them, and no stand-in
    /// was needed. Without a twin nothing tells how many lights were lost, so it is never safe.
    /// </summary>
    private static string? UnsafeReason(LightRecoveryReport r)
    {
        int fitted = r.Counts.GetValueOrDefault(RecoveredSource.Fitted), stale = r.Counts.GetValueOrDefault(RecoveredSource.StaleTwin);
        var why = new List<string>();
        if (r.Mode == LightRecoveryMode.Off)
        {
            why.Add("light recovery was off, so the lights the cook stripped are missing and a relight comes out darker");
        }
        else if (!r.TwinExact)
        {
            why.Add(fitted + stale == 0
                ? "no editor copy with its lightmaps was " + (r.TwinsIgnored ? "used" : "found") + ", so the lights the cook stripped are missing "
                    + "and a relight comes out darker"
                : "the lights the cook stripped came back only as " + string.Join(" and ", new[]
                    {
                        stale > 0 ? $"{stale} lights of an editor copy of another bake" : null,
                        fitted > 0 ? $"{fitted} fitted stand-ins" : null,
                    }.OfType<string>()) + $" ({Folder}), which may not be the lights this bake had");
        }
        else if (r.Before is null)
        {
            why.Add("the editor copy's lights could not be checked against the cooked lightmaps (the converted map has no baked render)");
        }

        if (r.TwinExact && r.Review.Count > 0)
        {
            why.Add($"{r.Review.Count} lights of the editor copy are not confirmed by its lightmaps, so a relight may light where the original did not");
        }

        return why.Count == 0 ? null : string.Join("; ", why);
    }
}

public enum RecoveredSource
{
    Twin,
    Orphan,
    StaleTwin,
    Fitted,
}

/// <summary>What the light recovery of one import found and how close the bake came (<see cref="LostLightRecovery"/>).</summary>
public sealed class LightRecoveryReport
{
    public string Map { get; init; } = "";

    public LightRecoveryMode Mode { get; init; }

    public int Atlases { get; init; }

    public int TwinsSearched { get; set; }

    /// <summary>Editor copies were not searched (<see cref="LightRecoveryOptions.IgnoreTwins"/>).</summary>
    public bool TwinsIgnored { get; init; }

    /// <summary>The editor copy the lights came from (exact or stale), or null.</summary>
    public string? Twin { get; set; }

    public bool TwinExact { get; set; }

    /// <summary>How many lights the cook dropped, when an exact twin tells.</summary>
    public int? LostEstimate { get; set; }

    public Dictionary<RecoveredSource, int> Counts { get; } = new();

    /// <summary>Recovered lights (ids) to check by hand.</summary>
    public List<string> Review { get; } = new();

    /// <summary>The faithful bake of the map's remaining lights against its atlases.</summary>
    public BakeError? Before { get; set; }

    /// <summary>The same with the recovered lights.</summary>
    public BakeError? After { get; set; }

    /// <summary>The fit's noise floor (bytes): the measured model error, at least 10.</summary>
    public double? NoiseFloor { get; set; }

    /// <summary>The model error measured on the map (bytes), before the floor's bounds.</summary>
    public double? MeasuredNoise { get; set; }

    /// <summary>The share of samples whose smoothed unexplained light is above the noise floor (the fit gate).</summary>
    public double? AboveFloor { get; set; }

    /// <summary>The mean excess of that light over the floor, bytes per sample.</summary>
    public double? ExcessAboveFloor { get; set; }

    /// <summary>The fit gate kept stand-ins out: what the bake leaves unexplained looks like model error.</summary>
    public bool FitGated { get; set; }

    public int Samples { get; set; }

    public int MeshOccluders { get; set; }

    /// <summary>The LightRadius a light that stores none bakes with on this map (32 or 64).</summary>
    public double? DefaultRadius { get; set; }

    public string? RadiusTrial { get; set; }

    /// <summary>Why a relight would not reproduce the cooked lightmaps, or null when it would.</summary>
    public string? UnsafeReason { get; set; }

    /// <summary>True when a relight reproduces the original: an exact twin gave back the lost lights and the bake
    /// confirms every one.</summary>
    public bool RelightSafe => UnsafeReason is null;

    public double Seconds { get; set; }

    public List<string> Notes { get; } = new();

    public void Count(RecoveredSource s) => Counts[s] = Counts.GetValueOrDefault(s) + 1;

    public string Summary()
    {
        string counts = Counts.Count == 0 ? "no lights recovered"
            : string.Join(", ", Counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {LostLightRecovery.FolderName(kv.Key).ToLowerInvariant()}"));
        string twin = Twin is not null ? (TwinExact ? $"from its editor copy {Twin}" : $"from an editor copy that is not this bake, {Twin}")
            : Mode == LightRecoveryMode.Off ? "recovery off"
            : TwinsIgnored ? "editor copies not used"
            : $"no editor copy with its lightmaps among {TwinsSearched} searched";
        string bake = Before is { } b
            ? $"; a rebake differs from the cooked lightmaps by {b.Mae:0.00} bytes on average with the lights the map kept, {(After ?? b).Mae:0.00} with these"
            : "";
        return $"light recovery of {Map} ({Mode.ToString().ToLowerInvariant()}): {counts} ({twin}){bake}"
            + $"{(DefaultRadius is { } r ? $"; lights without a LightRadius bake as {r:0}" : "")}; "
            + (RelightSafe ? "a relight matches the original" : "a relight will not match the original (cooked lightmaps kept)");
    }

    public SvJsonObject ToJson()
    {
        var o = new SvJsonObject();
        o.Add("map", SvJsonNode.String(Map));
        o.Add("summary", SvJsonNode.String(Summary()));
        o.Add("mode", SvJsonNode.String(Mode.ToString().ToLowerInvariant()));
        o.Add("relightSafe", SvJsonNode.Bool(RelightSafe));
        if (UnsafeReason is not null)
        {
            o.Add("unsafeReason", SvJsonNode.String(UnsafeReason));
        }

        if (Twin is not null)
        {
            o.Add("twin", SvJsonNode.String(Twin));
            o.Add("twinExact", SvJsonNode.Bool(TwinExact));
        }

        o.Add("twinsSearched", SvJsonNode.Integer(TwinsSearched));
        if (TwinsIgnored)
        {
            o.Add("twinsIgnored", SvJsonNode.True);
        }
        if (LostEstimate is { } lost)
        {
            o.Add("lost", SvJsonNode.Integer(lost));
        }

        var counts = new SvJsonObject();
        foreach ((RecoveredSource s, int n) in Counts.OrderBy(kv => kv.Key))
        {
            counts.Add(LostLightRecovery.SourceName(s), SvJsonNode.Integer(n));
        }

        o.Add("recovered", counts);
        if (Before is { } b)
        {
            o.Add("before", Error(b));
        }

        if (After is { } a)
        {
            o.Add("after", Error(a));
        }

        if (NoiseFloor is { } f)
        {
            o.Add("noiseFloor", SvJsonNode.Float((float)Math.Round(f, 2)));
            if (MeasuredNoise is { } m)
            {
                o.Add("measuredNoise", SvJsonNode.Float((float)Math.Round(m, 2)));
            }

            o.Add("aboveFloor", SvJsonNode.Float((float)Math.Round(AboveFloor ?? 0, 4)));
            o.Add("excessAboveFloor", SvJsonNode.Float((float)Math.Round(ExcessAboveFloor ?? 0, 3)));
            o.Add("fitGated", SvJsonNode.Bool(FitGated));
            o.Add("samples", SvJsonNode.Integer(Samples));
        }

        o.Add("meshOccluders", SvJsonNode.Integer(MeshOccluders));
        if (DefaultRadius is { } r)
        {
            o.Add("defaultLightRadius", SvJsonNode.Float((float)r));
        }

        if (RadiusTrial is not null)
        {
            o.Add("radiusTrial", SvJsonNode.String(RadiusTrial));
        }

        if (Review.Count > 0)
        {
            o.Add("review", new SvJsonArray(Review.Select(SvJsonNode.String)));
        }

        o.Add("notes", new SvJsonArray(Notes.Select(SvJsonNode.String)));
        o.Add("seconds", SvJsonNode.Float((float)Math.Round(Seconds, 1)));
        return o;
    }

    private static SvJsonObject Error(BakeError e)
    {
        var o = new SvJsonObject();
        o.Add("mae", SvJsonNode.Float((float)Math.Round(e.Mae, 3)));
        o.Add("bias", SvJsonNode.Float((float)Math.Round(e.Bias, 3)));
        o.Add("texels", SvJsonNode.Integer(e.Texels));
        return o;
    }
}
