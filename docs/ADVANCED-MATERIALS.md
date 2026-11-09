# Texture import, authored materials and animation

The Assets menu and command search provide **Import Textures…**, **Import Animated Texture…**, **Create Material…** and **Edit Material…**. Help → Browse Tutorials includes six material lessons: texture import, animation, lighting and shine, depth/collision, emissive baking and original-game export. Ctrl+Shift+P finds them by topic. These features require the matching advanced-materials OpenSCCT build for gameplay preview.

Editor 0.2.36 and game 0.8.124 include the browser, repeat-filtering, native-window bounds and shallow-angle parallax corrections described in [material fixes validation](MATERIAL-FIXES-VALIDATION.md).

## Import and edit

1. Save the map. An unsaved map prompts for Save As before import starts.
2. Select PNG, JPEG, BMP or WebP files together. Common suffixes group a texture set: `_albedo`, `_basecolor`, `_diffuse`, `_normal`, `_normalgl`, `_normaldx`, `_roughness`, `_gloss`, `_metallic`, `_height`, `_bump`, `_ao`, `_emissive`, `_opacity` and `_orm`.
3. Review each proposed material. Expand a named texture slot to choose a different file, channel, inversion or colour space. ORM defaults to red occlusion, green roughness and blue metallic. Gloss maps invert into roughness. Colour and emission default to sRGB; normal, height and masks default to linear.
4. Choose Keep both, Replace or Skip per material. Keep both generates a unique name. Uncheck materials to exclude them. Errors are reported individually; successful rows are excluded from a retry. Background import can be cancelled.
5. Compare sphere, cube, plane or **Current selection**, adjust the light angle, and choose a preset or edit Surface, Depth and Texture Animation. Mapping rotation is in degrees in the window; JSON stores radians.
6. Import or Save. The material is revealed in Assets. Import does not paint anything. Use the existing Surface controls to apply it to brush faces, or **Apply Material to Mesh Sections…** for static meshes. Section −1 applies to every section.

Material edits and imports support Undo/Redo. Source images stay in the project so Undo can restore an earlier definition. Save As copies the material manifest and all its images. Replacing a material preserves its asset identity and gameplay metadata. Editing a library material creates a map-local copy.

**In this map** includes every project-owned asset, even before placement, plus referenced library assets. Material windows expand their property list and preview when resized. Texture pickers return to their owning material window.

Each texture explicitly shows **Linear (data)** or **sRGB (colour)**. **Use recommended colour spaces** corrects the draft's per-slot defaults; Save commits it with Undo support. Normal, height, roughness, metallic and AO should normally be linear. In the map view use **Lit Preview** to judge normal and shine response. Assigning a height map alone leaves relief off: enable Parallax Depth for apparent depth, or Displacement and Subdivision Level for geometry.

PNG bytes are retained, including 16-bit data maps. CPU displacement and the editor/game data-texture decoder preserve the low bits of non-interlaced grayscale, RGB and RGBA PNGs. Colour-image previews do not replace those source files. The image budget is 64 million pixels; interlaced PNG data maps must be re-exported without interlacing.

## Animated textures

**Import Animated Texture…** packs equal-sized numbered frame images into an atlas, ordered naturally (`frame2` precedes `frame10`). For an existing sprite sheet, use Import Textures and enter its Columns, Rows and Frames in Texture Animation. Frames run left to right, then down.

Set FPS, Loop, Ping pong and Phase (in frames). FPS 0 freezes a frame. Non-looping ping pong plays out and back once. Scroll U/V are tiles per second; rotation speed is degrees per second in the window and radians per second in JSON. Pause and Restart affect the authoring preview. Map surfaces and meshes use the scene clock at run time.

Surface texture slots share one frame layout and timeline; the reflection environment is a separate, static equirectangular image. Different per-slot timelines, GIF/video decoding and animated gameplay displacement are not provided. Real displacement requires a static texture set. Animated surface maps remain available for normal, shine, opacity and emission effects.

Power-of-two frame dimensions receive independent colour, normal and scalar mip chains, clamped before frames would merge. Other frame dimensions use bilinear base-level sampling to avoid neighbouring-frame bleed. Colour mip generation uses linear values; normal mip generation renormalizes normals. Packed data maps share an image allocation when their interpretations match.

Repeat filtering keeps continuous UV derivatives for ordinary textures and repeats within the selected atlas frame for animation, including parallax. The source image must itself tile: opposite-edge content mismatches remain visible and need a tileable source texture.

## Supported feature matrix

| Feature | Editor and matching OpenSCCT game | Direct original-game SDC |
|---|---|---|
| Base colour, tint, opacity | Texture and factors, linear evaluation | Baked colour/opacity, native DXT mip data |
| Opaque, cutout, translucent, two-sided, unlit | Supported; cutout threshold editable | Texture flags and native Shader output blending where required |
| Normal and bump | Strength, OpenGL/DirectX convention, height-derived normals, mirrored UV tangent frames | Relief flattened; differences reported |
| Parallax | UV projection, 4–64 coarse height steps with extra grazing-angle samples (up to 64) and four intersection refinements; stable mip gradients | Flattened and reported |
| Real displacement | Deterministic CPU subdivision 0–6 and height/midpoint; render LODs | Visual relief flattened and reported |
| Displaced collision | Optional per face or mesh section; full final shape replaces flat collision | Export refused until disabled or replaced by compatible geometry |
| Roughness/gloss, metallic, specular colour/strength | Maps, channels and factors, direct highlights and supplied reflection environment | Compatible environment/specular approximation; PBR differences reported |
| AO | Map, channel and strength | Multiplied into the colour approximation |
| Emission | Map/colour/intensity, optional static illumination | Native Shader self-illumination and mask; HDR clamped |
| Clearcoat, anisotropy | Analytic finish approximations | Reduced to legacy appearance and reported |
| Refraction | Screen-space approximation for translucent materials | Omitted and reported |
| Detail colour/normal | Shared mapping and detail strength | Omitted and reported |
| Mapping | Tiling, offset, rotation, UV or world triplanar | UV transforms on export snapshot; triplanar uses source UVs and is reported |
| Texture animation | Atlases, frame sequences, looping, ping pong, phase, scrolling and rotation | First-frame approximation, reported per material |
| glTF material import/output | Base/MR, normals, AO, emission, alpha/sides and supported finish factors; standard output plus full authored extras | Original-editor mesh handoff retains its established ASE workflow |

The original-editor export folder includes a native `Map-Editor.sdc` when the map uses authored materials. Open that file in RE+ to retain its embedded Texture/Shader graphs; it uses the same conversion as direct SDC export. The folder also retains the T3D/DDS/ASE alternative, whose diffuse DDS imports have a simpler appearance and do not recreate those Shader graphs. Neither path changes the open project's images, geometry or UVs. Conversion failure preserves the prior export. Imported source-package export slots are retained when compatible; incompatible group/object classes require a different material/group name.

## Geometry and lighting limits

Subdivision generates `4^level` triangles for each source triangle, capped at 262144 triangles per patch or mesh section. The window reports the multiplier and the selected geometry estimate before import. Start at level 2 or 3. Render LODs simplify the generated shape; collision retains the complete shape. Build, inspect silhouettes and test both teams before choosing a higher density.

Brush boundaries and mesh material/UV/normal seams are pinned and faded to their original shape to prevent cracks between independently processed patches. This deliberately limits displacement at those seams. Source editing geometry remains intact. Mesh secondary UVs and vertex colours are interpolated; shader tangent frames are derived consistently from final geometry and mapped UVs. Real depth and parallax require UV projection; triplanar remains available for colour and normal authoring.

**Use Displaced Collision**, **Use Original Collision** and **Use Material Collision Setting** in Surface override the material default per face. The mesh-section application dialog offers the same choice per section. A mesh with displaced collision uses its final render sections for collision, including the undisplaced sections; it does not retain a second overlapping hull.

Authored materials use a separate shader path; stock materials retain their existing renderer. Baked irradiance remains separate from colour. World lightmaps and static-mesh vertices carry a dominant direction and coherence for static normal response, shared with Lit Preview. This is a directional approximation, not a full multi-lobe lighting field. Static meshes retain vertex irradiance and interpolate their directional data across triangles.

Bake Emission is opt-in. World patches and static-mesh triangles become deterministic area-weighted static emitters with ordinary occlusion. Texture emission is averaged, with at most 256 emitter samples per mesh section; animated emission bakes its average appearance. Changing emission re-evaluates lighting while reusable visibility masks may remain cached. This illuminates static world and static-mesh receivers and their baked darkness data. Character illumination stays controlled by placed lights. Emission does not set heat or EMF properties.

Fog, shadow visibility and vision-mode conversion run through the game pipeline after authored shading. Supplied reflection environments are equirectangular images; clearcoat/anisotropy and screen refraction are approximations. The compatibility test report separates automated acceptance from broader visual and hardware coverage.

## CLI

```text
ose texture-import Map.osmap stone_albedo.png stone_normal.png stone_orm.png --json
ose texture-import Map.osmap frame1.png frame2.png frame10.png --sequence --fps 12
ose material show Map.osmap MyLevel.Textures.Stone
ose material create Map.osmap MyLevel.Textures.New --definition material.json
ose material update Map.osmap MyLevel.Textures.Stone --definition material.json
```

`--conflict rename|replace|skip`, `--group` and `--alpha auto|opaque|masked|translucent` are available for texture import. Definitions use the shared `OpenScct.Content.Materials.AuthoredMaterial` version 1 contract; bindings name files relative to `assets/`, never absolute source paths. The UI and CLI use the same importer and validation.
