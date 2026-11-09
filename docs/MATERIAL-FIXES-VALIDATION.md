# Material browser, repeat sampling and authoring window fixes

Editor 0.2.35 pairs with OpenSCCT 0.8.123. These fixes extend the advanced-materials acceptance report; they do not replace its coverage boundaries.

- 59 material checks pass, including project-owned materials before placement, case-insensitive full/short references from shaped actors, generated traversal geometry and parametric windows. Unused external library assets remain excluded by In this map.
- The game GPU smoke passes 35 rendered variants on Vulkan and Direct3D 12. Six added comparisons exercise negative and positive UV repeats, static textures, animated atlas frames, parallax, normals and roughness against independent hardware repeat sampling with matching trilinear filters. The old shader fails the first comparison (peak channel error 64); the fixed shader passes all six with peak error at most 1.
- The authoring dialog was captured at 1100×760 and 1300×900. Properties, asset list and preview expand with it. A project-copy check loads the reported brick material, applies recommended colour spaces in the draft and inspects Depth. The original project was left untouched. Screenshot runs report no failed steps or UI lint findings.
- Native file pickers now belong to the material window and defer focus restoration to it on selection or cancellation. The material window is transient to the main editor. Native focus switching is not exercised by background screenshot automation, which deliberately refuses native dialogs.

The reported material has sRGB enabled for normal, height and roughness, and height relief disabled. Use recommended colour spaces corrects those data bindings; Parallax Depth or Displacement must be enabled explicitly for height relief. Unlit map views deliberately omit normal/shine response; use Lit Preview. Repeat filtering cannot repair mismatched content at the source image's opposite edges.

## Follow-up: native bounds and shallow-angle parallax

Editor 0.2.36 and game 0.8.124 correct two follow-up issues. The native 0.2.35 dialog reproduced an initial 1100×4681 client area despite a settled 892×608 content minimum. Native wrapping is now disabled, the form has a scroll boundary, and initial sizing includes the current work area and actual decorations. The title bar and action buttons remain within bounds. The opt-in native acceptance harness covers three work areas at three scales, checks loaded/expanded slots and rebuilds, and retains native client captures. It runs off the user's desktop with isolated settings.

Parallax now increases coarse samples at shallow angles, refines the first crossing four times and interpolates the result. Texture gradients are captured before the ray march for stable mip selection, and height sampling uses the footprint's mip level. The GPU suite adds an independently solved sloped-height reference at qualities 4, 16 and 64 (41 total rendered variants), in addition to the existing repeat checks.

Both Vulkan and Direct3D 12 pass all 41 cases with peak analytic-reference channel error 1. The old shader fails the new shallow-angle test with peak error 25. Native checks pass 81 assertions with the reported project-copy material and 81 with every slot populated. Material checks now include `_Displacement` height-map recognition (60 checks); tutorials explicitly explain that naming.
