# The `.sdc` map package format

This is the original game's map package as the editor reads and writes it: both the editor copy the stock editor
saves to `Packages\MapsEd` (with its brushes) and the cooked copy it writes to `Packages\Maps` (what the game loads).
Every field is listed. Where a field is only in one layout, it says so. Section 16 is how the editor's own writer
lays a package out.

Sources:

- The stock editor's serializers, decompiled from the SVM 4.0 mapping build's `ChaosTheory_Editor.exe`
  (x86, image base 0x10E00000; Engine, Core and Editor are linked into the exe). Addresses below are in that exe.
- The whole map corpus: every `.sdc` of the SVM 3.6 and 4.0 installs (mapping and player versions, UE Upgrade, Vanilla
  Reference, Enhanced Beta 3 and Beta 5) and a set of community maps. That is 1,765 files, 572 unique by
  SHA-256: 284 editor copies, 278 cooked copies and 10 broken autosaves. Every Model, Polys and StaticMeshInstance
  export of every file was parsed with the rules below and ends on its last byte.
- OpenSCCT's readers, which this spec corrects where they are wrong: `tools/scctpkg/scctpkg` (`package.py`,
  `properties.py`, `bsp.py`, `bsplight.py`, `level.py`), `re/formats/scctformats` (`ge.py`, `classdefaults.py`),
  `content/svmbuild/sources/modeltail.py`.

## Notation

All values are little-endian.

| Name | Meaning |
|---|---|
| `u8 u16 u32 i32 u64 f32` | Fixed-size integers and floats. |
| `compact` | UE2 compact index: first byte bit 7 = sign, bit 6 = more, bits 0-5 = value; each further byte bit 7 = more, bits 0-6 = next 7 bits. At most 5 bytes. |
| `name` | SCCT name reference: **two** compacts, the package-local name index, then a cooked global name index. Ignore the second; property tag sizes do not count its bytes. |
| `objref` | Object reference, a compact: `> 0` export index + 1, `< 0` -(import index + 1), `0` None. |
| `FString` | compact length n (negative = UTF-16, -n chars), then the chars including the trailing NUL. The bytes are XOR-obfuscated like names (below) when the name table is. |
| `FVector` | f32 X, Y, Z. |
| `FRotator` | i32 Pitch, Yaw, Roll (65536 = full turn). |
| `FBox` | FVector Min, FVector Max, u8 IsValid (25 bytes). |
| `FSphere` | f32 X, Y, Z, W (16 bytes). |
| `FPlane` | f32 X, Y, Z, W. |
| `FMatrix` | 16 x f32. |
| `TArray<T>` | compact n, then n x T. |
| `ArVer` | The package version. The serializers test only ArVer, never the licensee version. The highest threshold any of them uses is 0xAF (175), so at version 300 every newer branch applies. |

## 1. Container

A file is either a raw package (it starts with the tag `C1 83 2A 9E`) or a run of zlib chunks:

```
repeat until end of file:
    u32 UncompressedSize
    u32 CompressedSize
    CompressedSize bytes: one zlib stream that inflates to exactly UncompressedSize bytes
```

The package is the chunks' output joined in order. Every `.sdc` in the corpus is compressed.

- A normal save writes one chunk, even for a 205 MB package.
- Autosaves (`MapsEd\Auto0..9.sdc`) write 15,728,640-byte (15 MiB) chunks: 11 chunks for a 167 MB package. OpenSCCT's
  `Package.load` reads only the first chunk, so it sees nothing past 15 MiB.
- Sanity limits worth enforcing (from RE+ `SdcBlockModel.h`): 1 GiB unpacked in total, inflate ratio at most 1032.

## 2. Package summary (header)

```
offset 0   u32 Tag            0x9E2A83C1
       4   u16 Version        300 (two community cooked maps are 175: Polar Base Map17, Steel Squat SQU13)
       6   u16 Licensee       0 in every file
       8   u32 PackageFlags   0x1 in every file (both layouts)
      12   i32 NameCount
      16   i32 NameOffset     64 in every file
      20   i32 ExportCount
      24   i32 ExportOffset
      28   i32 ImportCount
      32   i32 ImportOffset
      36   16 bytes GUID
      52   i32 GenerationCount   1 in every file
      56   GenerationCount x {i32 ExportCount, i32 NameCount}   (equal to the summary counts)
```

File order: summary, name table, export data, import table, export table (the tables are at the end).

Nothing in the summary (flags, GUID, version) says whether the package is an editor copy or a cooked copy. See
section 7.

**Autosaves with an unpatched summary.** The 4.0 mapping build's ten autosaves (`MapsEd\Auto0..9.sdc`) have all six
counts and offsets set to 0 (the GUID and generation are present). They are recoverable:

1. The name table starts at offset 64. Read entries until one fails the name rules (length 2..64, NUL-terminated,
   printable after the XOR).
2. Find the import table: scan forward from the end of the names for an import record `Core`, `Package` (or `Class`),
   Outer 0. For each candidate, earliest first, read import records one at a time; after each, try to read export
   records from that point to the end of the file. The right split is the one where the export records end exactly at
   the end of the file and every export's data range lies before the import table.
3. All ten recover (7,150-7,408 names, 354-355 imports, 6,750-6,998 exports) and parse as editor copies.

## 3. Tables

**Name table** at NameOffset, NameCount entries:

```
compact Length          (1..1024, includes the NUL)
Length bytes            the name, NUL-terminated
u32 Flags               (not obfuscated)
```

The name bytes are XOR-obfuscated: byte i of the name, at absolute offset `o` in the unpacked package, is stored as
`b ^ (o & 0xFF)`. Some packages ported from the single-player game store plain names; detect it from the first entry
(plain when it is printable text ending in a NUL). Every `.sdc` in the corpus is obfuscated. Lookups ignore case
(exact case wins).

**Import table** at ImportOffset, ImportCount records:

```
name ClassPackage, name ClassName, i32 Outer (objref form: < 0 import, 0 none), name ObjectName
```

**Export table** at ExportOffset, ExportCount records:

```
compact Class          objref; 0 = Core.Class
compact Super          objref
i32     Outer          objref form; 0 = the package
name    ObjectName
u32     ObjectFlags
compact SerialSize
compact SerialOffset   only when SerialSize > 0
```

An object's path is its outer chain joined with dots. In a map, the map's own objects sit in the `MyLevel` group
package; imports name other packages (`Engine.Light`, `Pkg.Group.Texture`).

Object flags that matter here:

| Flag | Meaning |
|---|---|
| `0x02000000` RF_HasStack | The export data starts with a state frame (actors). |
| `0x1` RF_Transactional, `0x10000` RF_LoadForClient, `0x20000` RF_LoadForServer, `0x40000` RF_LoadForEdit, `0x100000` RF_NotForClient, `0x200000` RF_NotForServer | Editor-only objects (brush actors, their models and polys) are `0x340001` / `0x2340001`; the models a cooked copy keeps are `0x70001`. Informative only: the layout is not recorded here. |

## 4. Export data

Each export's data is SerialSize bytes at SerialOffset. Unless a class says otherwise it is:

```
[state frame]       only when ObjectFlags & RF_HasStack
tagged properties   ending in the name "None"
[native data]       per class (sections 8-12)
```

FString XOR uses the absolute offset in the package, so a reader over an export must know where the export starts.

**State frame** (FStateFrame):

```
objref Node, objref StateNode, u64 ProbeMask, i32 LatentAction, [compact Offset when Node != 0]
```

### 4.1 Tagged properties

```
repeat:
    name   PropertyName          "None" ends the list
    u8     Info                  bits 0-3 type, bits 4-6 size code, bit 7 array flag (for bool: the value)
    [name  StructName]           only when type = 10 (struct)
    size                         size code 0..4 = 1, 2, 4, 12, 16 bytes; 5 = u8 follows; 6 = u16 follows; 7 = u32 follows
                                 (a bool with size code 5 still carries its size byte)
    [index]                      only when the array flag is set and the type is not bool:
                                 u8 b; b < 0x80: b; (b & 0xC0) == 0x80: ((b & 0x7F) << 8) | u8;
                                 else ((b & 0x3F) << 24) | u8 << 16 | u8 << 8 | u8
    value
```

Type ids: 1 Byte, 2 Int, 3 Bool, 4 Float, 5 Object (also ClassProperty), 6 Name, 7 String (also DelegateProperty),
8 Class, 9 Array, 10 Struct, 11 Vector, 12 Rotator, 13 Str, 14 Map, 15 FixedArray.

Values, by the property's declared type (from the class data: OpenSCCT `shared/classes.json`, the editor's
`UeSchema`):

| Declared type | Value |
|---|---|
| Byte (with or without an enum) | u8 |
| Int | i32 |
| Float | f32 |
| Bool | no bytes: the value is the array-flag bit. Inside an array or struct member list read by item, u8. |
| Object, Class | objref |
| Name | name |
| Str, String | FString |
| Delegate | objref Object, name Function |
| Struct `Vector`, `Rotator`, `Color` | native: FVector, FRotator, or 4 bytes B, G, R, A |
| Any other struct (`Scale`, `Plane`, `PointRegion`, `Range`...) | a nested tagged property list ending in "None" |
| Array | compact n, then n items of the inner type |
| Static array (ArrayDim > 1) | one tag per stored element, with its index |

Rules that matter for a reader:

- The size of a struct tag can be wrong, and so can the size of an array of structs, in both directions: it leaves out
  some elements' "None" terminators or a byte of some two-byte object references (FactD, TME30/32/33 lifts), and UE
  Upgrade ShipD's `SLift19837.DoorsAndSwitches` claims 28 bytes for 27. The engine never checks it. Read struct values
  by their members until "None"; never skip by the size. Use the size only to skip a property whose type is unknown
  (a property the class data does not have).
- Object and name values are compacts whatever the size code says.
- Array tags do not say the element type. It comes from the class data. A heuristic reader (scctpkg
  `properties.py`) guesses, and fails on some arrays of structs (`SLift.DoorsAndSwitches` in TME30/32/33): use the
  declared types.
- Classes defined inside a map (Class exports in `MyLevel`) are not in the class data. Their actors need the map's own
  class exports, or a reader that keeps the property raw.
- `FScale` (Brush `MainScale`, `PostScale`, `TempScale`) is a nested list: `Scale` (Vector), `SheerRate` (Float),
  `SheerAxis` (Byte, ESheerAxis).

## 5. Level export (class `Level`)

ULevelBase::Serialize 0x111251C0, ULevel::Serialize 0x11129320. No state frame. Same layout in both copies; only the
content differs (section 13).

```
tagged properties         always just "None"
i32 ActorsNum, i32 ActorsMax
ActorsNum x objref        Actors: [0] LevelInfo; in an editor copy [1] is the builder brush; the list order is the
                          actor order and the CSG order of the brushes. An entry can be 0 (rare).
FURL:
    FString Protocol, FString Host, FString Map, FString Portal
    compact n, n x FString Op
    i32 Port, i32 Valid
i32 bHasModelPSX2, [objref ModelPSX2 when bHasModelPSX2]      (ULevel +0x144)
i32 bHasModelXBOX, [objref ModelXBOX when bHasModelXBOX]      (ULevel +0x14C)
f32 TimeSeconds
objref FirstDeleted
16 x objref TextBlocks
TravelInfo: compact n, n x (FString Key, FString Value)
GE partition              (see re/formats/scctformats/ge.py; same in both copies)
```

**The world model is ModelXBOX.** It is the PC world: the stock editor (platform 2) loads ModelXBOX and falls back to
ModelPSX2 only when there is no XBOX model. Cooked copies never have a PSX2 model (written with it detached). Most
editor copies have only the XBOX model; 19 of the 4.0 mapping build's editor copies (CineE, CineS, DeftD, DeftS, Entry,
KrauE, KrauU, Lobby...) also hold a PSX2 model, usually the smaller one but not always (in the 3.6 mapping build,
ScheE's and Entry's are larger and Lobby's is the same size), so the largest Model is not the world. OpenSCCT's `ge.py`
names them ModelA/ModelB and svmbuild picks `model_a or model_b`, which takes the PS2 world on those maps; scctpkg's
glTF export picks the largest Model export. Both should pick ModelXBOX first.

**GE partition** (ge.py has the full layout and a writer):

```
i32 PartitionVersion (4), FVector BoundsMin, FVector BoundsMax
compact n, n x GE:  i32 Kind (POLE 1, FENCE 2, HOH 4, LG 8, LADDER 16, PIPE 32, ZL 64), i32 Flags,
                    i32 Platform (ver >= 170), u8 ID (ver >= 174), 2 x FVector (3 for LADDER and PIPE)
compact m, m x i32 GE indices used by the tree leaves
k-d tree, pre-order: i32 type; 1 empty; 2/4/8 split on X/Y/Z: f32 value, then both children; 16 leaf: i32 first, i32 end
```

Saving a cooked copy drops the GEs of other platforms (0x11307F90). Editor and cooked copies of the same save had the
same GE list on every map checked.

## 6. Actors

There is no native data after the tagged properties of any actor class used in maps, including Brush, Volume and
Mover (no native Serialize for them). A brush is its Brush actor's properties, its `Brush` Model and that Model's
`Polys`. No BrushBuilder objects are stored in maps.

**AActor::Serialize (0x110B2490)** is UObject::Serialize with one rule on three properties:

| | `StaticMeshInstance` | `StaticMeshInstanceOnPSX2` | `StaticMeshInstanceOnXBOX` |
|---|---|---|---|
| Editor copy | not written (None) | the PS2 lighting instance | the PC lighting instance |
| Cooked copy (PC) | the PC lighting instance (the editor's OnXBOX object) | not written | not written |

A reader of either copy should take the PC lighting instance as `StaticMeshInstance` if present, else
`StaticMeshInstanceOnXBOX`. These are baked vertex-light objects (section 11), not authored content.

**Brush actors** (`Engine.Brush`, and the brush part of volumes, movers and anti-portals). The properties seen on the
52,910 brushes of the two mapping installs:

| Property | Meaning |
|---|---|
| `Brush` | objref to the brush's Model (whose `Polys` holds its faces). |
| `CsgOper` | ECsgOper: 0 CSG_Active (the builder brush), 1 CSG_Add, 2 CSG_Subtract. Missing = 0. |
| `PolyFlags` | Brush-wide EPolyFlags (Portal 0x04000000, Mirrored 0x08000000, Semisolid 0x20, NotSolid 0x08...). |
| `Location`, `PrePivot` | FVector. |
| `Rotation` | FRotator. No brush in the corpus stores one: the stock editor bakes rotation into the polys. |
| `MainScale`, `PostScale`, `TempScale` | FScale (nested list). Stored on most brushes, usually unit. |
| `Group`, `Tag`, `bHiddenEd`, `bLightChanged`, collision and vision props (`HeatOpacity`, `EMFOpacity`...) | Kept as ordinary properties. |
| `Level`, `Region`, `bMustInitNetChannels` | Engine state on every actor; recomputed, not authored. |

Brush local-to-world, checked against every world surface of about 60 editor copies (largest error 0.0005 units):

```
world = Location + PostScale * Rotate(Rotation, MainScale * (p - PrePivot))
```

(Shear, `SheerRate` != 0, is not used by any brush in the corpus.)

## 7. Editor copy or cooked copy

**Nothing in the file records the layout.** Each serializer below branches on a runtime global, the save/cook target
at `*(*(0x11691D7C) + 0x80)`: 0 (or -1) = editor layout, 1, 2 or 3 = cooked (1 = PS2, 2 = Xbox/PC, 3 = like 2).
Core has no reader of it. The stock editor runs with 0, so it can only load editor layout and refuses `Packages\Maps`
("maps must be loaded from MapsEd directory"). RE+ loads cooked maps by forcing the value to 1 (any of 1-3 reads the
same layout).

One stock Save (0x10EE4D30) writes two files:

1. `Packages\MapsEd\X.sdc` with target 0 (editor layout);
2. `Packages\Maps\X.sdc` with target 2 (0x10EE4750; path = the MapsEd path without "Ed"), after rebuilding the actor
   list for the platform (0x10EE13B0) and detaching the PS2 world model. For `Entry` and `Lobby` it also empties the
   XBOX model's LightMapTextures.

The PS2 save (0x10EDF020, `MAP SAVE FILE="%sPSX2\%s"`) uses target 1. Autosaves and `Autoplay.sdc` use target 0.

**How to tell them apart.** Folders are a hint only (RE+ keeps cooked copies under `MapsEd\Recovery\*\CompiledInput.sdc`,
and the editor's importer stands an editor copy in for the install's `Maps` copy). The decisive test, proven on all
293,389 Model exports of the corpus: parse a Model export in both layouts; exactly one ends on the export's last byte.
No Model in the corpus parses in both layouts, and none in neither. Even an empty brush model differs: the editor copy
has one extra byte (the empty LightMaps array). Parse the smallest Model export (size > 0) for speed; if the package
has none, it has no BSP and the layout does not matter.

A cheap second hint, with no exception in the corpus: editor copies use the property names
`StaticMeshInstanceOnXBOX`/`OnPSX2` and never `StaticMeshInstance`; cooked copies the reverse.

Readers of the target value (all of them, found by a byte scan): AActor::Serialize, FBspSurf (0x110CD830) and its
FPoly link (0x110CC800), FLightMap light entries (0x110CD700), UModel::Serialize (0x110D20E0), StaticMeshInstance and
its light entries (0x110E8980, 0x110E7040), ULevel::PostLoad (0x111252F0; in the editor it rebuilds the render data),
and the GE platform filter (0x11307F90, 0x11308370).

## 8. Model export (class `Model`, UModel::Serialize 0x110D20E0)

No state frame. The same record for the world model and for brush, volume and mover models.

```
tagged properties            ("None")
FBox    BoundingBox          (UPrimitive)
FSphere BoundingSphere       (UPrimitive)
TArray<FVector>  Vectors     normals and texture axes
TArray<FVector>  Points      vertices and texture bases
TArray<FBspNode> Nodes
TArray<FBspSurf> Surfs                                 <- differs by layout
TArray<FVert>    Verts       {compact pVertex, compact iSide}
i32 NumSharedSides
i32 NumZones                 (0..64)
NumZones x FZoneProperties   {objref ZoneActor, u64 Connectivity, f32 LastRenderTime}
objref Polys                 the model's UPolys export (0 when none)
TArray<FBox>     Bounds
TArray<i32>      LeafHulls
TArray<FLeaf>    Leaves      {compact iZone, compact iPermeating, compact iVolumetric}   iPermeating: start of the
                             leaf's run in Lights (ended by a 0), or -1
TArray<objref>   Lights
i32 RootOutside              (0 or 1)
i32 Linked                   (0 or 1)
TArray<FBspSection> Sections
TArray<FLightMap>   LightMaps                          <- EDITOR COPY ONLY
TArray<FLightMapTexture> LightMapTextures
```

The model ends there; the reader must land on the export's last byte.

**Brush, volume and mover models are in the brush's own space**, the space of its Polys (before Location, Rotation,
DrawScale and PrePivot), BoundingBox included: a moved volume keeps its model unchanged. Only the world model is in
world space.

**Leaves and Lights.** A leaf's iPermeating and iVolumetric index the flat Lights list; the leaf's lights run from
there to the next None, and every leaf's run ends with one (a leaf with no lights points at a None). AquaD's world:
302 leaves, 1473 entries, 301 of them None. A writer that drops a light has to take it out of the list and move the
indices; writing it as None would end the leaf's run early.

**Bounds and LeafHulls** are the editor's bspBuildBounds output, made from the nodes alone. The rule below reproduces
every node's iRenderBound and iCollisionBound and every Bounds box of the world and volume models of AquaD and ClubD
(3.6) and ShipD (the 4.0 mapping build); the hull boxes agree to float noise (theirs carry more rounding):

1. Start with the six faces of the box +-262144 (HALF_WORLD_MAX) at node 0, with Outside = RootOutside.
2. At each node add a cap: the node's plane, clipped to that box and to the half-spaces of the path to the node,
   tagged with the node's index.
3. Split every face by the node's plane (0.25 units on the plane counts as on it; a face on the plane goes both ways,
   and the cap's front copy is tagged `index | 0x40000000`, a flipped plane).
4. The front child gets `Outside || CSG`, the back child `Outside && !CSG`, where CSG means NF_NotCsg is clear. Where
   a child is missing, a front leaf is empty when `Outside || CSG`, a back leaf when `Outside && !CSG`.
5. An empty leaf adds its faces to the node's bound. A solid leaf writes a hull and sets the node's iCollisionBound to
   it: the distinct tags of its faces in order, -1, then the box of its faces as six floats (min X, Y, Z, max X, Y, Z,
   stored as their i32 bits).
6. After both children, a node with at least one child gets iRenderBound = the next Bounds entry, the box of the
   empty space below it (so the deepest nodes come first). Nodes reached only through iPlane, and nodes whose two
   children are both leaves, have none.

A convex brush model is a chain of its faces (each node Front -1, Back the next): 6 nodes, 5 bounds and one 13-entry
hull for a box.

### FBspNode (0x110CDC80). Same in both layouts.

```
FPlane Plane
u64    ZoneMask
u8     NodeFlags             (the engine keeps bits 0x1F on load; NF_NotCsg 0x01, NF_NotVisBlocking 0x20...)
compact iVertPool, iSurf, iBack, iFront, iPlane, iCollisionBound, iRenderBound
FSphere ExclusiveSphereBound
FSphere (unused)             a stack temporary the writer never fills: garbage. Ignore it; it is why "the node bytes
                             of the two copies match" is only nearly true.
u8 iZone[0], u8 iZone[1], u8 NumVertices
i32 iLeaf[0], i32 iLeaf[1]
i32 iSection, i32 iFirstVertex, i32 iLightMap
```

### FBspSurf (0x110CD830)

```
objref Material
u32    PolyFlags
compact pBase, vNormal, vTextureU, vTextureV           (indices into Points / Vectors)
FPlane Plane
f32    LightMapScalePSX2
f32    LightMapScaleXBOX                               (the PC scale)
then:
  editor copy, every surf:   FPoly, objref Actor, compact iBrushPoly
  cooked copy:               FPoly only when PolyFlags & 0x0C000000 (PF_Portal | PF_Mirrored), else nothing
```

In an editor copy each surf's FPoly is its source brush face in world space: when saving, the editor copies
`Actor.Brush.Polys[iBrushPoly]` and transforms it by the brush's local-to-world. `Actor` is the Brush actor (None on a
few surfs, 169 of 262,843) and `iBrushPoly` indexes that brush's Polys. The FPoly's own Actor and iBrushPoly fields are
None and -1 there: the link is the trailing pair. Material and ItemName match the brush poly in every case.

### FPoly (0x110CBDF0). Same in surfs and in UPolys.

```
compact NumVertices          3..16 in the corpus (the engine asserts 0..65535; scctpkg's limit of 512 is too loose
                             to catch a misaligned read)
FVector Base                 texture origin
FVector Normal
FVector TextureU
FVector TextureV
NumVertices x FVector        vertices
u32    PolyFlags
objref Actor
objref Material
name   ItemName              (None when unset)
compact iLink
compact iBrushPoly
f32    LightMapScalePSX2
f32    LightMapScaleXBOX     (the PC scale)
```

There are no pans (`PanU`/`PanV` exist only below version 0x4E, folded into Base). The two lightmap scales often
differ: 3,620 of 21,465 brush polys in 40 maps (mostly PSX2 32 with XBOX 16 or 256). The stock T3D export writes
neither scale nor pans, and the stock T3D import reads no scale; the editor's T3D export writes both scales
(`LightMapScale=`, `LightMapScalePsx2=`) for its own import.

### FBspSection (0x110CD3F0)

```
TArray<FBspVertex> Vertices  FBspVertex = FVector Position, FVector Normal, f32 U, V, LightU, LightV (40 bytes)
i32    (likely Revision)
objref Material
i32    (likely NumNodes)
u32    PolyFlags
i32    iLightMapTexture
```

(scctpkg `bsplight.py` describes this trailer differently and finds the sections by scanning. This is the decoded
layout.)

### FLightMap (0x110D08B0). Editor copy only.

```
7 x compact                  texture (index into the section's LightMapTextures), iSurf, iZone, OffsetX, OffsetY, SizeX, SizeY
FMatrix WorldToLightMap
FVector, FVector, FVector    (by analogy: origin, X axis, Y axis)
TArray<FLightMapLight>       {objref Light, TArray<u8> visibility bits, 7 x i32}
objref
i32
```

Every model of an editor copy has the array, a brush model included (then a single compact 0). The cook omits it.
The third field is a zone: it is the front zone (iZone[1]) of a node drawn with the lightmap on most records (ShipD
2317 of 2473, AquaD 1473 of 1509); the rest are lightmaps shared across a zone border.

The FLightMapLight lists of the world model name every light that baked into the BSP (a light that lights only
static meshes, or no surface at all, is in none). The light recovery uses them to tell which of an editor copy's
lights the cooked lightmaps hold (`UModel.BakedLights`).

The field order is decoded from the stock editor and checked on SteeE: read this way, the lights of 794 of the 806
lightmaps that hold a run-time light are the lights a decode of the leaf runs gives their surf. There is one lightmap
per surf and zone. The light list
holds every light the editor baked into that lightmap: `UModel::Illuminate` takes them from the surf's light list
(`CalculateSurfaceLights`, 0x1119f5d0), which comes from the BSP leaves' light runs (`CalculateNodeLights`,
0x1119f880). The leaf runs saved in `Lights` are not those lists: before a save the editor rebuilds them with only the
lights the game uses at run time (`TestLights`, 0x1101dcb7), so a light that only bakes is in no run (SteeE: 15 of
its 252 baked lights are in a run, 225 in some FLightMap list).

### FLightMapTexture (0x110D0A80). Both layouts.

```
objref                       owner
TArray<i32> LightMaps        ids of the lightmaps packed into it
u64
i32
FStaticLightMapTexture:
    2 x TLazyArray<u8>       {i32 (0 when written), compact n, n bytes}
    u8  Format
    i32 Width, i32 Height
    i32 (likely Revision)
```

The first lazy array holds the atlas texels, 4 bytes each in B, G, R, A order (Format 5, 512 x 512 or 256 x 256);
the second is empty in every file read. The texels are the same in an editor copy and the cooked copy of the same
save, so the sorted SHA-1 hashes of a map's atlases fingerprint its bake: a cooked copy's editor twin is the editor
copy with the same set, in any install (the stock 3.6 maps' twins are in SVM 3.6 and its mapping build).

### Recovering brushes from the world model

A cooked copy has no CSG brushes (section 13), but its world model's node tree still says where solid and empty space
are, and its surfs still say what covers each wall. `Interop/Sdc/Recovery` turns that back into brushes, as RE+'s
Recover Compiled Map does (`RecoveredBspGeometry`, `RecoveredSurfacePartition`, Merge Recovered Geometry), in doubles
with RE+'s tolerances:

0. **Polygons.** Each node polygon loses corners that double back along one line (some cooked polygons fold back on
   themselves, z -448 -> -453 -> -450 -> -467: a spike with no area that reads as a wall facing the wrong way) and
   corners that run straight on, before its normal and winding are taken.
1. **Cells.** Walk the tree as the engine does (front when `n.p >= d`; the state turns empty in front of a CSG node and
   stays as it was behind a non-CSG one, where CSG means `NumVertices != 0` and NF_NotCsg clear), clipping a box 64
   units round every point of the model down to the leaves (epsilon 1e-6, caps ordered round their centre). Each
   empty leaf of a world that starts solid (RootOutside 0, every stock map) is a convex cell that becomes a subtract
   brush (a world that starts empty gives add brushes from its solid leaves). A cell open to the box, degenerate at the
   tolerance or thinner than 0.01 units is left out and counted.
2. **Join and grow.** Cells that share a whole face and whose union is convex with the same volume are joined. In merge
   mode (the default) each brush then grows over its neighbours: the hull of a brush and a neighbour is taken when the
   original cells cover it exactly (covered volume equal to the hull's within `1e-6 x area + 1e-12 x volume`), and the
   neighbours it swallows are absorbed. Grown brushes may overlap, as RE+'s do.
3. **Faces.** Each brush face is matched to the node polygons on its plane that face the way its surface does (into
   the empty side: against a subtract brush's face, along an add brush's), normals within 1e-5. On the plane means
   within 0.02 units first, then, while those leave part of the face uncovered, within the BSP's 0.25 coplanar band
   (cooked polygon points stray up to 0.22 off their plane), measured at the face: the node's plane at every corner
   of the face, or the polygon's own points (a split plane tilted by 1e-4 is a unit off at the origin 10,000 units
   out, so a distance taken there misses it). A face between solid and empty space that none covers is looked at
   again against every polygon of the map, ending with polygons within a unit and 2.5 degrees.
   The surf covering most of the face's area gives it its material, poly flags (less Portal, NotSolid and Semisolid),
   texture base and axes and lightmap scales; other surfs that cover more than 0.01 square units of it cut their
   pieces out of it (a cut thinner than 0.1 units is not made, and a piece under 0.01 square units goes with the
   face's largest piece). Surfs alike in material, flags,
   mapping (within 0.01 texels) and lightmap scale count as one, so a wall the BSP cut into many surfs stays one face.
   A part no surf covers takes the material of the face's largest covered part, else of the surf whose plane made it,
   else the map's most common one. One between solid and empty space is a wall the compiled map drew nothing on: it
   is made Invisible, so it draws nothing as in the original, and reported as unsurfaced.
4. **Clean-up.** Corners weld at 0.002 units per axis at float precision to the earliest corner seen (structural
   corners first), straight-on corners go, and faces of more than 16 corners are cut into convex parts that each have
   area. The material cuts leave T-junctions on the edges of the brush's neighbouring faces; they are not closed
   (inserting the welded corner there would bend the edge, and the face would no longer be convex): Map Check pairs an
   edge with the run of collinear edges facing it, as RE+'s brush check does.
5. **Check and split back.** The brushes are built with the editor's own compiler and probed half a unit either side of
   every structural node polygon against the compiled tree (a probe whose answer changes within 0.05 units is a tie and
   left out). Grown brushes near a miss are split back into what they were grown from, at widening reach, up to six
   times; the attempt with fewest misses, then fewest brushes, is kept.
6. **Sheets and portals.** The polygons of NotCsg nodes come back as non-solid add brushes, one per surf and plane, their
   fragments joined back where they stay convex and corners within 0.01 units of a straight edge dropped. A zone
   portal comes back from its surf's authored FPoly when that lies within 0.25 units of the portal's node plane
   (Portal, Invisible and NotSolid), else from its polygons.

The import runs this for every cooked copy. A cooked copy that kept brush exports of its own (OffsD's one stray
Semisolid brush, 3.6's Auto1 and a community port) is recovered too unless its own brushes, built by the
editor's compiler, reproduce its compiled BSP (all but 1% of the probes match: that port, whose 365 brushes match all
6724); the kept brushes stay, after the recovered ones (Auto1 rebuilds to its 22 zones that way). The report says
which way it went, and points at the map's editor copy when the light recovery found the one of this exact bake (its
brushes are the author's). `ose sdc-brushes <project.osmap> --save` recovers a project's brushes again from the file it
was imported from.

Measured with `ose sdc-brushes` on the 83 Beta 3 maps, 3.6's OffsD and TestRange (every one recovers, in about 20 s
at most with the comparison): no point of a 48 x 48 x 48 grid through any map differs between the rebuilt brushes and
the compiled tree, all probes match on 81 of the 85 (GhosS 2, SkysD, SkysP and SlauS 1, where the editor's compiler
splits differently, as it does for their editor copies), and at least 99.88% of each map's drawn area is drawn again
with the same material (mean 99.995%; the editor copies of the same saves score 98.65-100% the same way, mean 99.79%,
RE+'s ShadD recovery 91.4%). Where the same material is drawn, its texture offsets,
lightmap scale and flags match the compiled surf's at 99.97% of 753,443 samples over the Beta 3 maps (PanaD and PanaS
keep about 110 each, where the compiled map has two overlapping polygons of one material mapped differently; StatD,
StatE and StatS a few), and at every sample on 3.6's OffsE, LvuD, HELI02 and a community port (StroD keeps 2, on a cooked
polygon that is not convex). Brush counts are RE+'s where its numbers are
known: AquaD 302 cells, 234 joined, 215 brushes plus 71 portals; CineU 318, MounU 291 and ShadD 207 in all (OffsD 860
against RE+'s 871, PrisD 1109 against 1117). Map Check's shape rules find 7 faces with no area (cells with a
face of a few thousandths of a unit), 4 faces a hair from convex, 4 open brushes and 17 brushes a hair from convex
(corners 0.05-0.3 units in front of a narrow face piece's plane, which its welded float corners tilt) in the 21,540
brushes.
Not recovered: lightmaps (the import keeps the baked world), semisolid brushes (they come back as ordinary walls), and
the original brush split (one original brush can come back as several, and the other way round).

## 9. Polys export (class `Polys`, UPolys::Serialize 0x11052E10)

Same in both layouts. No state frame.

```
tagged properties ("None")
i32 Num
i32 Max                      (Num == Max in every file)
Num x FPoly                  (plain FPoly, no Actor/iBrushPoly tail)
```

A brush's faces are its Model's Polys, in the brush's local space. They are the authored faces: the editor-copy
corpus has 283,249 Polys exports with 1,732,964 FPolys, every one 3 to 16 vertices. Brush polys have Actor None and
iBrushPoly -1, except 2,625 that point back at their own brush. `iLink` is usually >= 0.

A brush Model's own BSP is empty, except models that cooking also keeps (volumes, movers): those have nodes and surfs
too.

## 10. Brushes and CSG

- Every Brush actor of an editor copy is in `Level.Actors` (275,310 checked). The list order is the CSG order:
  LevelInfo first, then the builder brush (CsgOper 0), then the rest.
- CsgOper in the corpus: 233,448 add, 41,616 subtract, 246 CSG_Active (the builder brushes).
- Portal and mirror sheets are ordinary brushes with `PolyFlags` 0x04000009 / 0x08000009.
- Volumes (BlockingVolume, PhysicsVolume, SDamageVolume, ZoneInfo-less volumes, ERainVolume...), movers and
  anti-portals hold their shape the same way: `Brush` -> Model -> Polys. Cooked copies keep these.

## 11. StaticMeshInstance export (UStaticMeshInstance::Serialize 0x110E8980)

```
tagged properties
TArray<FColor> ColorStream   compact n, n x BGRA (per-vertex baked light)
i32                          (stream revision)
TArray<entry> Lights         EDITOR COPY ONLY: {objref Light, TArray<u8> visibility bits, i32}
```

All 202,140 editor-copy and 185,932 cooked-copy instances end exactly with this rule.

## 12. Other classes

No other class depends on the layout. Texture, StaticMesh, Shader and ConvexVolume data are byte-identical between an
editor copy and its cooked twin; Sound, SkeletalMesh, the soft-body actors and TextBuffer differ at most by objref
compact sizes.

**Soft bodies** (ESBPatch, ESBStripDoor) keep their simulation state after the tagged properties:

```
FBox (25), FSphere (16), i32, i32
objref                       the owning actor
i32
compact n, n x 72 bytes      particles
compact m, m x 12 bytes      links
161 bytes                    settings (no object references)
```

All 99 soft bodies of the 26 round-trip maps (on 10 of them) end exactly there. OpenSCCT's `level.py` reads it only as far as the links.

**Map classes.** A map can define UnrealScript classes of its own (RE+ compiles its tools' classes into MyLevel;
map authors added scripts too). Each is a `Class` export of the map package with a `TextBuffer` export named
`ScriptText` under it. The TextBuffer's data after its tagged properties is `Pos` (int32), `Top` (int32) and `Text`
(FString): the class's source. The `.sdc` import keeps the sources in the project (`map.json` `extra.mapClasses`:
name, parent class, source), and the export for the original editor writes them to `Classes\<Name>.uc`.

## 13. What only the editor copy holds

Measured on the 180 editor/cooked pairs that come from the same save (AeroD: 11,001 exports against 8,555;
Level.Actors 5,083 against 4,259; Model and Polys 883 against 72).

Only in the editor copy:

- every plain Brush actor with its Model and Polys (all CSG brushes, the builder brush included);
- static lights: Light and STriggerLight actors the cook drops (see below);
- the PS2 StaticMeshInstance objects (`OnPSX2`) and StaticMeshActors with `Platform=1` (PS2 only);
- deleted actors (`bDeleteMe`, not in Level.Actors): ZoneInfo, EFFECT_*, SAlarmZoneInfo;
- in models: an FPoly with its brush link on every surf, and the LightMaps array;
- the PS2 world model, on some maps.

Only in the cooked copy: one `Camera` tagged `LightingViewport` made by the cook's lighting pass (deleted, at the
end of Level.Actors; ShipD's twin has three).

`Mover.SavedPos` / `SavedRot` are not a difference: 8 of the 10 twin pairs with movers keep them in the cooked copy,
always the unset values (-12345, -12345, -12345 and 123, 456, 789). MissP's and 3.6 Auto1's cooked copies have none.

Everything else is the same once object references are compared by name: properties, nodes (but the garbage sphere),
Verts, zone, bound, hull, leaf and light tables, sections, lightmap textures, GEs.

**The cook's actor filter** (0x10EE13B0, predicate 0x10EDD1E0; `Interop/Sdc/CookRule.cs`, svmbuild
`mapconv/source.py left_out_by_cook`):

- an actor with LightType (actor +0x2D1) != 0 is kept only when it is dynamic-relevant: actor flags +0x2EC & 0x800000,
  or predicate 0x10EDACF0 (flags +0x2E8 & 0x20000, or +0x2D2 == 0x16, or both floats +0x174 and +0x178 non-zero).
  By property: bApplyToInGameLighting or bDynamicLight set, LightEffect 22 (ESpotShadow), or both HeatRadius and
  HeatIntensity non-zero. On every editor/cooked pair checked the rule names exactly the lights missing from the
  cooked Level.Actors;
- an actor whose class derives from ABrush (class object 0x1181B048) is kept only when it also derives from the class
  at 0x1181B278 (predicate 0x10EDB890);
- other actors are kept.

**What the filter takes out stays when something kept refers to it.** Checked on 23 twin pairs: a static light named
by a kept actor (a zone's or light's `IgnoredEchelonLights`, as on AquaD, a mesh's `ObjectLights`) and a deleted
ZoneInfo named by actors' `Region` are still written, as exports outside Level.Actors. Such lights keep their
properties (SteeE keeps 11, AquaD 21; the converter lists them in `lighting.json` as static, not in-game, lights).
Deleted actors and static lights that nothing written refers to are dropped (of those lights only their light in the
atlases and the static meshes' vertex colours remains), and so are the StaticMeshInstances of actors that are not
written.

The cook also moves a few actors from the end of Level.Actors to just before the editor cameras. The rule is not
decoded. Entry's and Lobby's LightMapTextures are emptied as section 7 says, but the 3.6 Lobby twin keeps them.

## 14. Files that do not fit

| Files | What is different |
|---|---|
| The 4.0 mapping build's ten autosaves (`MapsEd\Auto0..9.sdc`) | 11 zlib chunks and an unpatched summary (sections 1-2). Editor layout. |
| `Map17.sdc` (Polar Base), `SQU13.sdc` (Steel Squat) | Version 175, cooked layout. Every version test is at 175 or lower, so they read like 300. |
| RE+ `MapsEd\Recovery\*\CompiledInput.sdc` (7) | Cooked copies sitting in MapsEd. |
| Community editor copies, and editor backups and autosaves kept outside MapsEd | Editor copies outside MapsEd. |
| RED1, a community port, 3.6 `Maps\Auto1`, OffsD (Beta 5), OIL33, museumvsclubhouse, ZCT03 | Cooked copies that keep the Brush exports the cook took out of Level.Actors (3.6 Auto1: exactly its editor twin's brushes). They are read as the map's CSG brushes. |
| CineS, CineE, CineU (cooked) | Keep one Brush that is in Level.Actors. |
| 3.6 Auto1 and three community ports | Their MapsEd and Maps twins come from different saves. |

## 15. How it maps to the project

| Original | Project (`.osmap`, see FORMAT.md) |
|---|---|
| Level.Actors[0] LevelInfo | `MapProject.LevelInfo`; the camera from `CameraLocationDynamic`/`CameraRotationDynamic` |
| Builder brush (CsgOper 0) | dropped (the project has its own builder brush) |
| Brush actor, in Level.Actors order | `Brush` in `MapProject.Brushes` (that order is the CSG order): CsgOper -> `Oper`, PolyFlags -> `PolyFlags`, Location/Rotation/PrePivot, MainScale/PostScale -> Vec3 (a shear stays in `Props`), every other property in `Props`; `Source` `{"sdc":{"export","hash"},"baked":true}` (built into the baked world, FORMAT.md) |
| Brush Model's Polys, one FPoly each | `BrushFace`: vertices, Material path, PolyFlags -> `Flags`, Base -> `TextureBase`, TextureU/V, LightMapScaleXBOX -> `LightMapScale`, LightMapScalePSX2 -> `LightMapScalePsx2` (only when different), ItemName, iLink -> `Link`; pans 0 |
| Volume, mover, anti-portal brush | `Actor.Shape` from its Polys |
| Other actors | `Actor` with every stored property (an editor copy's StaticMeshInstanceOnXBOX as StaticMeshInstance, the PS2 one dropped, as the cook stores them); a value the class data cannot type as `{"$raw"}` |
| RE+ player-count rules, outfits, bot controller, conveyor belt (`ConveyorBelt`, `ConveyorMotor`, `ConveyorItem`, `ConveyorRoller`) and AeroD tread script (`AerTreadScroll`) actors of the map's own classes (lifted into native data by the conversion) | `Actor` with `"lifted":true` in `Source`, left out of the compile (the project's match rules, outfits and converted `Svm.Conveyor` actors build), written by the T3D export (belts only while their converted conveyors are as imported; `extra.sdcLifted.conveyors` records each chain's belts and conveyors at import) |
| Static lights the cook drops, `Platform=1` actors | `Actor` with `"cookedOut":true` in `Source`, left out of the compile; the dropped lights still light the bake (relight, added brushes and meshes, a rebuild without the baked render) without being written, as in the cooked map; PS2-only lights are not in the PC game and light nothing |
| A cooked copy's actor exports outside Level.Actors that the cook filter takes out (static lights, CSG brushes) | read like Level.Actors' (lights get their properties and place, brushes become CSG brushes); the rest outside Level.Actors were deleted in the editor |
| A reference to any actor | `{"$actor": name}` (the compile writes render actors back as the map's assets) |
| Cameras, deleted actors (`bDeleteMe`/`bPendingDelete`, in or outside Level.Actors) | dropped; references to a deleted actor are None |
| GE partition | `MapProject.Ges` with ID (mask), platform and flags |
| World model (ModelXBOX), lightmaps, StaticMeshInstances | the baked world (converted by OpenSCCT's builder); not editable content |
| A cooked copy's world model nodes and surfs | recovered brushes (section 8) in `MapProject.Brushes`, layer `Recovered brushes`, `Source` `{"recovered":{"mode"},"sdc":{"hash"},"baked":true}` (built into the baked world, FORMAT.md); the report in `extra.sdcBrushRecovery` |
| FLightMap, surf FPoly links, PS2 lighting, Mover SavedPos | engine state: rebuilt by a build, not imported |

## 16. Writing a package

The editor writes both layouts itself (`Interop/Sdc/Write`, `ose export --format sdc`). What a writer has to get right,
beyond the fields above:

- **Order.** Summary, names, export data in export order, imports, exports, then zero bytes: a normal stock save ends
  with 64, an autosave with none. The loader never reads them. One zlib chunk holds the whole package.
- **Offsets key the obfuscation.** Names and FStrings are XORed with the low byte of their absolute offset, so export
  data must be written in place. Data copied from another package must start at an offset with the same low byte as
  it had there; the writer pads between exports. A few stock files leave a gap between the names and the first
  export's data (OffsD 4.0: 13 bytes); a write-back keeps it.
- **The second compact of a name.** The stock editor writes its session's global name index there; the loader reads
  and drops it. Property tag sizes leave those bytes out. A write-back keeps each name's stored value, so an
  unchanged map writes back byte for byte; a new name gets 0.
- **The GUID.** Every stock save makes a new one. The writer keeps the source's when nothing changed and makes one
  from the content otherwise.
- **A map written back** keeps the source's names and imports, and copies the objects it does not edit (the baked
  world, StaticMeshInstances, map textures and meshes, script classes, emitters). An object whose data the writer can
  read whole (tagged properties, a state frame, a soft body's state) is relocatable: every object reference, tag size
  and string in it is known, so it can move to another slot with its references remapped and its strings keyed again.
  The exports are then packed in source order with no gaps, as a stock save would hold them. Map-local assets
  (StaticMesh, Texture, Sound, script classes, TextBuffer) are copied as bytes and cannot be read whole; when a map has
  any, every asset of the map keeps its slot and a removed slot below them holds a placeholder Package export named
  `Unused` (custom maps such as AeroD and MoonD). Level content (actors, their sub-objects, brush and volume models,
  StaticMeshInstances, the LevelSummary) always moves.
- **New and copied brushes and volumes** get a model of their own (`Model<name>`, `Polys<name>`); a duplicate never
  shares the original's. A changed shape is rebuilt in the brush's space (section 8).
- **Removed lights** leave the world's per-leaf light lists and the lightmap records (section 8). The written-back
  lightmaps and StaticMeshInstances are otherwise the source's: an edit that moves or changes lights or meshes needs a
  rebuild in the stock editor before its lighting is right.
- **Names that the source already uses** for objects the import leaves out (the builder brush, cameras, deleted
  actors) are not reused: a new actor named like one of them is written under the next free name.
- **A cooked copy** written from an editor copy keeps the whole source name table (fewer names would change the width
  of name compacts in copied data), drops what the cook drops (section 13: the cook's filter, then what nothing kept
  refers to; GEs of the PS2 only), and writes StaticMeshInstance as the PC instance. It writes no `LightingViewport`
  camera. Portal and mirror surfaces keep their FPoly, as in stock cooked copies.
- **An editor copy is not written from a cooked copy**: the cook dropped the CSG brushes and the lightmap records
  that the world's nodes still name.
- **Conveyors.** The original game has no conveyor class, so a native conveyor is written as RE+ belts, as the export
  for the original editor lowers them. A converted chain still as imported is written as its imported belts; one
  changed since is lowered to new belts of the map's own `ConveyorBelt` class, in place of its imported belts. Those
  belts set only the settings the class declares (its property exports) and are typed by them. The class's `Seg`
  array is how many belts one chain runs items over (16 in AeroD's version-1 class): a longer lowered chain is
  reported. A package with no `ConveyorBelt` class of its own (a map made in the editor, or one without RE+ belts)
  cannot hold them: its conveyors are left out, with a warning.
- **A map made in the editor** has no baked world to copy. The writer builds one with the editor's compiler: CSG, the
  solid-leaf BSP, zones and portals, then the lightmaps in faithful mode (one chart per brush face) packed into 512 x
  512 BGRA atlases, each face lit by the lights its leaves list (as the editor's own build bakes it). It writes it the
  way the stock BSP holds it: each node one polygon, further polygons on its plane chained through iPlane, NF_NotCsg
  on nodes that only split and on drawn polygons that do not collide, a surf per brush face with its FPoly and brush
  link (editor copy), one render section per material, flags and atlas (Revision = NumNodes), leaves numbered for the
  zones with one None light each, zone connectivity bits for each reachable zone, and bounds and hulls by the rule in
  section 8. A zone portal's split node holds its piece of the portal sheet on a surf flagged Portal and Invisible.
  The lightmap record's zone is the front zone of its node. Volume and mover models are their faces' BSP in the
  brush's space (RootOutside, no leaves, no zones). CSG brush models are empty (RootOutside, no nodes) with their
  bounds in the brush's space, as in stock files. The LevelInfo gets a Title and a Summary, the LevelSummary object
  (Title, Author) every stock map has.

