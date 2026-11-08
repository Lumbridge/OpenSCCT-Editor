using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>
/// The export for the original editor: a folder with what RE+ needs to make the map again. The map's T3D (LevelInfo and
/// builder brush first, for File &gt; Import into a new map); the actors of classes the map defines itself, and the RE+
/// outfit and player-count actors a map without them gets, in a second T3D with the classes' sources; the map's own
/// textures, meshes and sounds as DDS, ASE and WAV with a script that imports them into MyLevel; the map's texts for
/// <c>System\_PC_</c>; what the original editor has no place for, which the T3D import reads back; and a README for the
/// steps done by hand in RE+ (import, Build All, save).
/// </summary>
[EditorService]
public sealed class OriginalExporter : IMapExporter
{
    /// <summary>The list of files an export wrote, so the next export into the folder removes only those.</summary>
    public const string ManifestFile = "openscct-export.json";

    public const string ImportScript = "ImportAssets.txt";

    public string Name => "original";

    public string Extension => "";

    public TargetGame Target => TargetGame.Original;

    public bool WritesFolder => true;

    public ExportResult Export(MapProject project, string path, ExportOptions options)
    {
        if (project.Actors.Any(a => MoverBase.IsMover(a) && OpenScct.Editor.Core.Gameplay.MoverSetup.Read(a).Keys.Count > 8))
            return new ExportResult(false, [], [Finding.Error("mover.original-limit", "This map uses mover paths longer than eight stops. Build for OpenSCCT, or shorten those paths before exporting to the original game.")]);
        if (project.Actors.Any(a => a.Door is not null))
            return new ExportResult(false, [], [Finding.Error("door.originalUnsupported", "Editable doors currently require an OpenSCCT build")]);
        if (project.Actors.Any(a => a.Window is not null))
            return new ExportResult(false, [], [Finding.Error("window.originalUnsupported", "Editable shaped windows currently require an OpenSCCT build; original-game window export is not supported")]);
        try
        {
            project = GeGeometry.Expand(project);
        }
        catch (ArgumentException ex)
        {
            return new ExportResult(false, [], [Finding.Error("ge.geometry", ex.Message)]);
        }

        string folder = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (ClearFolder(folder) is { } refused)
        {
            return new ExportResult(false, [], [refused]);
        }

        var writer = new Writer(project, folder, options);
        try
        {
            writer.Run();
        }
        finally
        {
            // Also after a failure part way, so the next export can clear what this one wrote.
            File.WriteAllBytes(Path.Combine(folder, ManifestFile), CanonicalJson.ToFileBytes(Manifest(folder, writer.Files)));
        }

        bool ok = !writer.Findings.Any(f => f.Severity == Severity.Error);
        return new ExportResult(ok, writer.Files, writer.Findings);
    }

    /// <summary>Empties the folder of an earlier export's files; refuses a folder that holds other things. Only the files
    /// the earlier export listed are deleted, one by one, and never through a link (a junction to an install).</summary>
    private static Finding? ClearFolder(string folder)
    {
        if (File.Exists(folder))
        {
            return Finding.Error("export.folder", $"{folder} is a file: the export for the original editor writes a folder");
        }

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            return null;
        }

        if (IsLink(folder))
        {
            return Finding.Error("export.folder", $"{folder} is a link: export into a real folder");
        }

        if (!Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return null;
        }

        string manifest = Path.Combine(folder, ManifestFile);
        SvJsonArray? files = null;
        try
        {
            files = File.Exists(manifest) && SvJson.Parse(File.ReadAllBytes(manifest), manifest) is SvJsonObject o ? o["files"] as SvJsonArray : null;
        }
        catch (SvMapFormatException)
        {
            // A damaged list is no list: the folder is refused below rather than guessed at.
        }

        if (files is null)
        {
            return Finding.Error("export.folder", $"{folder} is not empty and holds no earlier export (or its {ManifestFile} is damaged): pick an empty folder");
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in files.OfType<SvJsonString>().Select(s => s.Value))
        {
            string full = Path.GetFullPath(Path.Combine(folder, relative));
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || ThroughLink(folder, full))
            {
                continue;
            }

            File.Delete(full);
            for (string? d = Path.GetDirectoryName(full); d is not null && d.Length > folder.Length; d = Path.GetDirectoryName(d))
            {
                folders.Add(d);
            }
        }

        foreach (string d in folders.OrderByDescending(d => d.Length))
        {
            if (Directory.Exists(d) && !IsLink(d) && !Directory.EnumerateFileSystemEntries(d).Any())
            {
                Directory.Delete(d);
            }
        }

        File.Delete(manifest);
        return null;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ThroughLink(string root, string file)
    {
        for (string? d = Path.GetDirectoryName(file); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(d) && IsLink(d))
            {
                return true;
            }
        }

        return File.Exists(file) && IsLink(file);
    }

    private static SvJsonObject Manifest(string folder, IEnumerable<string> files)
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String("osmap.export/1"));
        o.Add("files", new SvJsonArray(files.Select(f => SvJsonNode.String(Path.GetRelativePath(folder, f).Replace('\\', '/')))));
        return o;
    }

    /// <summary>One asset of the map's own package to import, in import order (textures before the meshes that use
    /// them).</summary>
    private sealed record Import(string Command, string Path, string File, string Note);

    private sealed class Writer
    {
        private readonly MapProject _p;
        private readonly string _folder;
        private readonly ExportOptions _options;
        private readonly UeSchema? _schema;
        private readonly AssetCatalog _catalog;
        private readonly AssetResolver _resolver;
        private readonly Dictionary<string, Import> _imports = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _stockPackages = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Actor> _classActors = [];
        private readonly List<Actor> _made = [];

        // The actors <Map>-MapClasses.t3d holds, by short class and name: converted conveyors still as imported are
        // their RE+ belts there, changed ones the belts lowered from them.
        private readonly List<(string Class, string Name)> _classFile = [];
        private readonly Dictionary<string, string> _carried = new(StringComparer.OrdinalIgnoreCase);

        public Writer(MapProject p, string folder, ExportOptions options)
        {
            _p = p;
            _folder = folder;
            _options = options;
            string? content = options.ContentRoot ?? EditorServices.ContentRoot;
            _schema = UeSchema.Load(content);
            _catalog = AssetCatalog.For(content, options.ProjectFolder);
            _resolver = LocalAssets.Resolver(p, content, options.ProjectFolder);
        }

        public List<string> Files { get; } = [];

        public List<Finding> Findings { get; } = [];

        private string MapFile => _p.Id + ".t3d";

        private string? ProjectFolder => _options.ProjectFolder;

        public void Run()
        {
            _options.Progress?.Invoke("checking round-trip compatibility");
            Findings.AddRange(RoundTripLint.Check(_p, _schema, wholeMap: true, _catalog, _resolver, ProjectFolder, carriesAssets: true));

            _options.Progress?.Invoke($"writing {MapFile}");
            Dictionary<string, MapClass> own = MapClasses.Of(_p);
            bool OwnClass(Actor a) => ConveyorDesign.Is(a) || RoundTripLint.IsMapClassActor(_p, a, _schema, own);
            _classActors.AddRange(_p.Actors.Where(OwnClass));
            _made.AddRange(LiftedWriteBack.Made(_p, projectFolder: ProjectFolder));
            byte[] map = Encoding.Latin1.GetBytes(T3dWriter.Write(T3dExport.FromProject(_p, _schema, leaveOut: OwnClass, projectFolder: ProjectFolder)));
            Write(MapFile, map);
            if (_classActors.Count + _made.Count > 0)
            {
                T3dDocument classes = T3dExport.FromProject(_p, _schema, _classActors.Select(a => ObjectRef.Actor(a.Id)).ToList(), projectFolder: ProjectFolder,
                    added: _made);
                Write(_p.Id + HandoffFile.ClassesSuffix, Encoding.Latin1.GetBytes(T3dWriter.Write(classes)));
                _classFile.AddRange(classes.Actors.Select(a => (a.Class[(a.Class.LastIndexOf('.') + 1)..], a.Name)));
            }

            // RE+'s conveyor classes are made by RE+ itself (README), not from the map's older sources.
            foreach (MapClass c in own.Values.Where(c => c.Script is not null && !IsReConveyorClass(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                Write(Path.Combine("Classes", c.Name + ".uc"), Encoding.Latin1.GetBytes(c.Script!));
            }

            _options.Progress?.Invoke("writing the map's own assets");
            CollectAssets();
            WriteAssets();

            _options.Progress?.Invoke("writing the map's texts");
            if (InterfaceTexts.SettingsFile(_p) is { } settingsTexts)
            {
                Write(Path.Combine("System", "_PC_", _p.Id + "-i.int"), settingsTexts);
            }

            // The title as the T3D's LevelInfo has it: a real one only (an import names the map by its id).
            string title = Svmap.IdentityImport.ImportedTitle(_p);
            if (_p.LevelInfo.GetString("Title") is null && title == _p.Id)
            {
                title = "";
            }

            if (_schema is not null && InterfaceTexts.ActorFile(_p, _schema, _p.Actors, T3dExport.LevelInfoName(_p), title) is { } actorTexts)
            {
                Write(Path.Combine("System", "_PC_", _p.Id + ".int"), actorTexts);
            }

            Write(_p.Id + HandoffFile.Suffix,
                CanonicalJson.ToFileBytes(HandoffFile.ToJson(_p, ProjectFolder, Carry, CarryAssetLibrary(), _made, map)));
            Write("README.txt", Encoding.Latin1.GetBytes(Readme()));
        }

        /// <summary>Copies a file the handoff file names into the export (a model with the files it names beside it) and
        /// returns the path the handoff file stores; null when the file is missing.</summary>
        private string? Carry(string file, string group)
        {
            if (!File.Exists(file))
            {
                return null;
            }

            if (_carried.TryGetValue(file, out string? known))
            {
                return known;
            }

            bool model = Path.GetExtension(file).Equals(".glb", StringComparison.OrdinalIgnoreCase);
            string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            string relative;
            for (int n = 1; ; n++)
            {
                string suffix = n == 1 ? "" : $"-{n}";
                relative = model
                    ? $"{HandoffFile.FilesFolder}/{group}/{stem}{suffix}/{stem}{ext}"
                    : $"{HandoffFile.FilesFolder}/{group}/{stem}{suffix}{ext}";
                if (!_carried.ContainsValue(relative))
                {
                    break;
                }
            }

            _carried[file] = relative;
            Write(relative, File.ReadAllBytes(file));
            if (model)
            {
                string from = Path.GetDirectoryName(file)!, to = Path.GetDirectoryName(relative)!;
                foreach (string uri in ProjectOutfits.GlbFiles(file).Where(u => File.Exists(Path.Combine(from, u))))
                {
                    Write(Path.Combine(to, uri), File.ReadAllBytes(Path.Combine(from, uri)));
                }
            }

            return relative;
        }

        /// <summary>Copies the project's own asset library (meshes, materials, sounds it imported, as the editor keeps
        /// them) into the export, so the import gets them back; returns its folder there, or null when it has none.</summary>
        private string? CarryAssetLibrary()
        {
            if (ProjectFolder is not { } project
                || Path.Combine(Path.GetFullPath(project), ProjectAssets.Folder) is not { } from
                || !File.Exists(Path.Combine(from, ProjectAssets.ManifestFile)))
            {
                return null;
            }

            string to = $"{HandoffFile.FilesFolder}/{ProjectAssets.Folder}";
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                Write(Path.Combine(to, Path.GetRelativePath(from, file)), File.ReadAllBytes(file));
            }

            return to;
        }

        private void Write(string relative, byte[] bytes)
        {
            string full = Path.Combine(_folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            Files.Add(full);
        }

        // ---- the map's own assets ------------------------------------------------------------------------------------

        /// <summary>The assets of the map's own package its objects use (as the export writes them), and the outfit
        /// textures of the RE+ outfit actors it writes.</summary>
        private void CollectAssets()
        {
            foreach ((string path, string key, _) in RoundTripLint.AssetRefs(_p, ProjectFolder).Where(r => RoundTripLint.IsLocalPath(_p, r.Path)))
            {
                AddAsset(path, key);
            }

            if (!_p.Actors.Concat(_made).Any(a => BakedContent.LiftedData(a) == "outfits"))
            {
                return;
            }

            foreach (OutfitTexture t in LiftedWriteBack.Textures(_p, ProjectFolder))
            {
                string local = LocalAssets.LevelPath(t.Path);
                if (_imports.ContainsKey(local))
                {
                    continue; // the map's own texture, found in its assets above
                }

                string png = ProjectFolder is { } project && !Path.IsPathFullyQualified(t.File) ? Path.GetFullPath(Path.Combine(project, t.File)) : t.File;
                AddTexture(local, png, masked: null, blend: null, "outfit");
            }
        }

        private void AddAsset(string path, string key)
        {
            string local = LocalAssets.LevelPath(path);
            if (_imports.ContainsKey(local) || LocalAssets.Find(_p, _catalog, _resolver, path, key) is not { HasSource: true } asset)
            {
                return; // RoundTripLint reports what has no source (roundtrip.mapLocalAsset)
            }

            switch (asset)
            {
                case { Mesh: { } mesh }:
                    AddMesh(local, mesh);
                    break;
                case { Kind: AssetKind.Sound, File: { } file }:
                    AddSound(local, file);
                    break;
                case { Material: { } material, File: { } file }:
                    AddMaterial(local, material, file);
                    break;
                case { File: { } file }:
                    AddTexture(local, file, masked: null, blend: null, "");
                    break;
            }
        }

        private void AddMaterial(string local, LibraryMaterial m, string png)
        {
            bool Flag(string name) => m.Flags[name] is JsonValue v && v.TryGetValue(out bool b) && b;
            string note = string.Join(", ", new[] { Flag("two_sided") ? "two-sided" : "", Flag("unlit") ? "unlit" : "" }.Where(n => n.Length > 0));
            AddTexture(local, png, Flag("masked") ? true : null, Flag("translucent") ? true : null, note);
        }

        /// <summary>A file under <c>Assets\&lt;kind&gt;\</c>, in folders by the asset's group so same-named assets of two
        /// groups stay apart.</summary>
        private static string AssetFile(string kind, string group, string name, string extension) =>
            Path.Combine("Assets", kind, group.Length > 0 ? group.Replace('.', Path.DirectorySeparatorChar) : "", name + extension);

        private void AddTexture(string local, string png, bool? masked, bool? blend, string note)
        {
            if (!File.Exists(png))
            {
                Findings.Add(Finding.Warning("export.assetFile", $"{local}: its image {png} is missing, so it is not exported"));
                return;
            }

            RgbaImage image;
            try
            {
                image = RgbaImage.Decode(File.ReadAllBytes(png));
            }
            catch (InvalidDataException ex)
            {
                Findings.Add(Finding.Warning("export.assetFile", $"{local}: {Path.GetFileName(png)} could not be read ({ex.Message})"));
                return;
            }

            AlphaKind alpha = blend == true ? AlphaKind.Blend : masked == true ? AlphaKind.Mask : DdsFile.AlphaOf(image);
            (string _, string group, string name) = AssetPaths.Split(local);
            string file = AssetFile("Textures", group, name, ".dds");
            DdsTexture dds = DdsFile.Encode(image, alpha);
            Write(file, dds.Bytes);
            // The DDS keeps its own format and mip chain through the import.
            string command = string.Create(CultureInfo.InvariantCulture,
                $"TEXTURE IMPORT FILE=\"{Path.Combine(_folder, file)}\" NAME=\"{name}\" PACKAGE=\"{AssetPaths.LocalPackage}\"{Group(group)} MIPS=1 "
                + $"MASKED={(alpha == AlphaKind.Mask ? 1 : 0)} ALPHATEXTURE={(alpha == AlphaKind.Blend ? 1 : 0)}");
            string size = string.Create(CultureInfo.InvariantCulture, $"{dds.Format} {dds.Width}x{dds.Height}, {dds.Mips} mips")
                + (dds.Width != image.Width || dds.Height != image.Height
                    ? string.Create(CultureInfo.InvariantCulture, $", resized from {image.Width}x{image.Height}")
                    : "");
            _imports[local] = new Import(command, local, file, string.Join("; ", new[] { note, size }.Where(s => s.Length > 0)));
            if (note.Length > 0 && note != "outfit")
            {
                Findings.Add(Finding.Info("export.materialFlags", $"{local} is {note} in the map: the import makes a plain texture; set it in the texture's properties in RE+"));
            }
        }

        private void AddMesh(string local, LibraryMesh mesh)
        {
            List<MeshSection> render = MeshSections.Read(mesh);
            List<MeshSection>? collision = mesh.CollisionMesh is int c ? MeshSections.Read(mesh.GlbPath, mesh.RenderDir, c) : null;
            foreach (string material in render.Select(s => s.Material).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (RoundTripLint.IsLocalPath(_p, material) || AssetPaths.Split(material).Package.Equals(mesh.Origin, StringComparison.OrdinalIgnoreCase))
                {
                    AddAsset(material, "Material");
                }
                else
                {
                    _stockPackages.Add(AssetPaths.Split(material).Package);
                }
            }

            (string _, string group, string name) = AssetPaths.Split(local);
            AseMesh ase = AseFile.Write(name, render, collision);
            if (ase.CollisionTriangles > AseFile.CollisionTriangleCap)
            {
                Findings.Add(Finding.Warning("export.meshCollision", string.Create(CultureInfo.InvariantCulture,
                    $"{local} collides with {ase.CollisionTriangles} triangles: more than {AseFile.CollisionTriangleCap} crash the original game; give it hulls or split it")));
            }

            if (ase.SharedNames.Count > 0)
            {
                Findings.Add(Finding.Warning("export.meshMaterialName",
                    $"{local} has different materials of one name ({string.Join(", ", ase.SharedNames)}): the stock mesh import finds a material by its name "
                    + "alone, so those slots get the same one; set the right one in the mesh's properties in RE+"));
            }

            string file = AssetFile("Meshes", group, name, ".ase");
            Write(file, Encoding.ASCII.GetBytes(ase.Text));
            string command = $"STATICMESH IMPORT FILE=\"{Path.Combine(_folder, file)}\" NAME=\"{name}\" PACKAGE=\"{AssetPaths.LocalPackage}\"{Group(group)}";
            _imports[local] = new Import(command, local, file, string.Create(CultureInfo.InvariantCulture,
                $"{ase.Triangles} triangles, {(ase.Hulls > 0 ? $"{ase.Hulls} collision hulls" : "collides with its triangles")}"));
        }

        private void AddSound(string local, string source)
        {
            if (!Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(Finding.Warning("export.soundFormat", $"{local} is {Path.GetExtension(source)}: the original editor imports WAV only, so it is not exported"));
                return;
            }

            (string _, string group, string name) = AssetPaths.Split(local);
            string file = AssetFile("Sounds", group, name, ".wav");
            Write(file, File.ReadAllBytes(source));
            _imports[local] = new Import($"AUDIO IMPORT FILE=\"{Path.Combine(_folder, file)}\" NAME=\"{name}\" PACKAGE=\"{AssetPaths.LocalPackage}\"{Group(group)}",
                local, file, "");
        }

        private static string Group(string group) => group.Length > 0 ? $" GROUP=\"{group}\"" : "";

        /// <summary>The import script: textures first (meshes look their materials up by name), then meshes, then sounds.</summary>
        private void WriteAssets()
        {
            if (_imports.Count == 0)
            {
                return;
            }

            IEnumerable<Import> ordered = _imports.Values.OrderBy(i => i.Command.StartsWith("TEXTURE", StringComparison.Ordinal) ? 0 : i.Command.StartsWith("STATICMESH", StringComparison.Ordinal) ? 1 : 2)
                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
            Write(ImportScript, Encoding.Latin1.GetBytes(string.Concat(ordered.Select(i => i.Command + "\r\n"))));
            if (_folder.Contains(' ', StringComparison.Ordinal))
            {
                Findings.Add(Finding.Warning("export.folderPath",
                    $"the export folder's path has spaces ({_folder}): the stock editor's command parser may cut the file names in {ImportScript}; export to a path without spaces"));
            }
        }

        // ---- README ----------------------------------------------------------------------------------------------

        private string Readme()
        {
            var r = new StringBuilder();
            void Line(string text = "") => r.Append(text).Append("\r\n");
            string title = _p.Title.Length > 0 ? _p.Title : _p.Id;
            string classesFile = _p.Id + HandoffFile.ClassesSuffix;
            bool hasClasses = Files.Any(f => Path.GetFileName(f).Equals(classesFile, StringComparison.OrdinalIgnoreCase));
            List<string> texts = Files.Where(f => Path.GetDirectoryName(f)!.EndsWith(Path.Combine("System", "_PC_"), StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName).OfType<string>().ToList();
            Line($"{title} ({_p.Id}) for the original editor");
            Line($"Exported by the OpenSCCT Editor on {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.");
            Line();
            Line("WHAT IS HERE");
            Line($"  {MapFile}  the map: LevelInfo, builder brush, brushes, actors and GEs.");
            if (hasClasses)
            {
                Line($"  {classesFile}  {_classFile.Count} actors of classes the map makes itself (imported after those classes exist).");
            }

            if (Files.Any(f => f.Contains(Path.DirectorySeparatorChar + "Classes" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                Line("  Classes\\  the source of the map's own classes, as the original map had them.");
            }

            if (_imports.Count > 0)
            {
                Line($"  Assets\\  {_imports.Count} assets of the map's own package (MyLevel: DDS textures with their mips, ASE meshes with their collision");
                Line($"      hulls, WAV sounds); {ImportScript} imports them.");
            }

            if (texts.Count > 0)
            {
                Line($"  System\\_PC_\\  the map's texts: {string.Join(" and ", texts)}.");
            }

            Line($"  {_p.Id}{HandoffFile.Suffix}  what RE+ has no place for (identity, map settings, player counts and outfits as");
            Line($"      the editor keeps them; the files they name are in {HandoffFile.FilesFolder}\\);");
            Line($"      the OpenSCCT Editor reads it back when it imports {MapFile}.");
            Line("  The steps below name files by where this folder was written: if you move it, export again.");
            Line();
            Line("STEPS IN RE+");
            int step = 1;
            void Step(string text) => Line($"{step++.ToString(CultureInfo.InvariantCulture)}. {text}");
            Step("File > New.");
            if (_stockPackages.Count > 0)
            {
                Step($"Open these packages in the Texture Browser, so the meshes find their materials: {string.Join(", ", _stockPackages)}.");
            }

            if (_imports.Count > 0)
            {
                Step($"In the command bar at the bottom, type: EXEC \"{Path.Combine(_folder, ImportScript)}\"");
                Line("   When it asks to save changes to the map, answer No. The script imports into MyLevel:");
                foreach (Import i in _imports.Values.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Line($"     {i.Path}{(i.Note.Length > 0 ? "  (" + i.Note + ")" : "")}");
                }
            }

            Step($"File > Import, pick {MapFile}.");
            Line("   - \"Import into existing map?\": answer No. The file brings its own LevelInfo and builder brush for a new map.");
            if (_imports.Count > 0)
            {
                Line("     The MyLevel assets of the step before should stay. If the import then shows \"contains no static mesh\" boxes for");
                Line("     MyLevel meshes, they did not: start again from step 1 and answer Yes here.");
            }

            Line("   - \"Do you want to paste Geometric Events associated with ...?\": answer No. The file carries the map's GEs.");
            Line("   - A \"contains no static mesh\" box names an actor whose mesh is not loaded (see the export's findings); answer No");
            Line("     to seeing it again.");
            if (hasClasses)
            {
                Step("Make the map's own classes:");
                foreach (IGrouping<string, (string Class, string Name)> g in _classFile.GroupBy(a => ClassStep(a.Class)).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    Line($"   - {g.Key} ({string.Join(", ", g.Select(a => a.Name).Take(6))}{(g.Count() > 6 ? ", ..." : "")})");
                }

                Step($"In the command bar, type: MAP IMPORTADD FILE=\"{Path.Combine(_folder, classesFile)}\"");
                Line("   An actor whose class does not exist yet is left out (the import may not say so): check the actors above are in the map.");
            }

            Step("Build > Build All.");
            Step($"File > Save As, name {_p.Id}.sdc, in the game's Packages\\MapsEd folder (the editor keeps editor copies there and refuses");
            Line($"   maps in Maps; the save also writes the playable Packages\\Maps\\{_p.Id}.sdc and Textures\\{_p.Id}-i.utc).");
            if (texts.Count > 0)
            {
                Step($"Copy {string.Join(" and ", texts.Select(t => "System\\_PC_\\" + t))} into the game's System\\_PC_ folder.");
            }

            List<string> modes = InterfaceTexts.ModeOrder(_p);
            if (modes.Count > 0)
            {
                Step("Set the map's settings (modes, pictures) in RE+ as for any map. List the modes in this order, which the texts follow:");
                foreach (string mode in modes)
                {
                    string values = _p.MapSettings.TryGetValue(mode, out PropSet? s)
                        ? string.Join(", ", s.Select(kv => $"{kv.Key} {kv.Value}"))
                        : "texts only";
                    Line($"     {mode}: {values}");
                }
            }

            if (Findings.Any(f => f.Severity != Severity.Info))
            {
                Line();
                Line("WHAT THE EXPORT COULD NOT CARRY");
                foreach (Finding f in Findings.Where(f => f.Severity != Severity.Info))
                {
                    Line($"  {f.Severity.ToString().ToLowerInvariant()} {f.Code}: {f.Message}");
                }
            }

            return r.ToString();
        }

        /// <summary>RE+'s conveyor classes: the belt RE+ places and the motor, items and rollers it spawns.</summary>
        private static bool IsReConveyorClass(string cls) =>
            cls is "ConveyorBelt" or "ConveyorMotor" or "ConveyorItem" or "ConveyorRoller";

        /// <summary>How RE+ makes the class of a map-class actor, by its short class.</summary>
        private string ClassStep(string cls)
        {
            if (IsReConveyorClass(cls))
            {
                return "Conveyor belts: RE+ Tools > Add Conveyor Belt once, then delete that belt";
            }

            // RE+'s tools compile their current classes; any other is loaded from its source.
            if (cls == LiftedWriteBack.OutfitsClass)
            {
                return $"{cls}: RE+ Tools > Character Skins..., Apply with nothing set; then delete the {cls} actor it places";
            }

            if (cls == LiftedWriteBack.MatchClass)
            {
                return $"{cls}: give any objective a player count in RE+ and clear it again (RE+ compiles the class and removes its actor)";
            }

            return MapClasses.Of(_p).GetValueOrDefault(cls)?.Script is not null
                ? $"{cls}: CLASS LOAD FILE=\"{Path.Combine(_folder, "Classes", cls + ".uc")}\" PACKAGE=MyLevel NAME={cls}, then SCRIPT MAKE "
                  + "(RE+ compiles map classes through its own compiler patch, so this may fail; then its actors stay out)"
                : $"{cls}: the export has no source for it; bring the class from the original map";
        }
    }
}
