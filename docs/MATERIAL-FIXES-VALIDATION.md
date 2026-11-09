# Material browser, repeat sampling and authoring window fixes

Editor 0.2.35 pairs with OpenSCCT 0.8.123. These fixes extend the advanced-materials acceptance report; they do not replace its coverage boundaries.

- 59 material checks pass, including project-owned materials before placement, case-insensitive full/short references from shaped actors, generated traversal geometry and parametric windows. Unused external library assets remain excluded by In this map.
- The game GPU smoke passes 35 rendered variants. Six added comparisons exercise negative and positive UV repeats, static textures, animated atlas frames, parallax, normals and roughness against independent hardware repeat sampling. The old shader fails the first comparison (mean channel error 1.0552, peak 64); the fixed shader passes all six.
- The authoring dialog was captured at 1100×760 and 1300×900. Properties, asset list and preview expand with it. A project-copy check loads the reported brick material, applies recommended colour spaces in the draft and inspects Depth. The original project was left untouched. Screenshot runs report no failed steps or UI lint findings.
- Native file pickers now belong to the material window and defer focus restoration to it on selection or cancellation. The material window is transient to the main editor. Native focus switching is not exercised by background screenshot automation, which deliberately refuses native dialogs.

The reported material has sRGB enabled for normal, height and roughness, and height relief disabled. Use recommended colour spaces corrects those data bindings; Parallax Depth or Displacement must be enabled explicitly for height relief. Unlit map views deliberately omit normal/shine response; use Lit Preview. Repeat filtering cannot repair mismatched content at the source image's opposite edges.
