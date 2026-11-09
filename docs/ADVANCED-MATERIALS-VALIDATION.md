# Advanced materials acceptance

Local Windows validation, 2026-11-09. The editor worktree is isolated on `feature/advanced-materials`; the game worktree uses the same branch and includes the published 0.8.119 baseline. Publication is separate.

## Automated checks

- `tools/material-checks` (55 passed): texture-set recognition, ORM, colour spaces, unique/replace/skip, cancellation/failure integrity, animation ordering/playback, exact 16-bit height samples in both decoders, deterministic tessellation, pinned boundaries, collision overrides, save/reopen and Save As, native Texture/Shader objects, emissive occlusion, directional bake data, compiled materials and deterministic output.
- `tools/material-network-checks`: real PlaySession server/client movement on a physically displaced floor, as Spy and Merc at 0 and 100 ms simulated latency. Bullet and camera box traces agree; the flat floor is replaced. Repeated physical collision compilation is byte-identical. Settled client errors were 0.009 units for Spy and 0.005 for Merc.
- Game `authored-material` GPU smoke: 23 rendered variants, visible atlas frame changes, independent frame mips, alpha modes, actor/lightmap paths, refraction, normal response, DirectX/OpenGL conventions, triplanar, metallic/reflection and roughness changes, emission, grazing-angle parallax, directional static-mesh normal response, night/thermal/EMF, and three generated LOD levels. A compiled editor fixture loads through the actual game map loader with its directional mesh stream intact; reduced texture quality preserves atlas frames.
- Editor screenshot automation: material authoring and Texture Animation windows, with no UI findings or failed steps, repeated successfully with the exported editor. The bundled self-contained CLI imports and reopens a material. The exported game also passes all 23 GPU variants and the compiled-map loader check.
- Existing regression suites: 116 SDC export, 10 mesh import, 516 discovery/tutorial, 89 shadow-cache and 34 launcher-integration checks passed. The game baseline's spectator/perspective input tests also pass.

## Original engine

Independent Python `scctpkg` decoding verifies embedded mip pixels and native material references in both SDC layouts. RE+ 2.6.1 opens, saves, reopens and saves the plain and emissive editor fixtures. Captures retain the expected orange diffuse and green emissive pixels.

Direct cooked fixtures run in a task-owned isolated Reloaded install as both teams, without an editor resave. Spawn, 100 health and movement are observed. The native D3D9 test caught and fixed sub-4×4 DXT base textures; exports now use a minimum 4×4 base level and adjust only the export snapshot's UVs. No original-game assets are included in the delivered packages.

## Coverage boundaries

These checks establish the tested workflows and synthetic lighting cases. They do not establish visual parity for every stock map, all overlapping transparent/refraction arrangements, every possible seam/LOD transition or a minimum-hardware performance target. Broad stock-map capture comparisons, long multiplayer sessions and low-end GPU performance acceptance remain separate from this local feature package. The actual rendering/export limitations are listed in `ADVANCED-MATERIALS.md`.

Run the checks from this editor checkout, then run the GPU smoke in the pinned game checkout. Native acceptance requires an installed original game and a separately isolated RE+ harness; those binaries and assets are not redistributed.

## Supplemental stock checks

See [the post-build stock report](ADVANCED-MATERIALS-STOCK-VALIDATION.md). Normal/night/thermal comparisons and matching draw-call counts were observed, but additional stock tests have unresolved first-use, shutdown and EMF capture failures. These are separate from the successful authored-material and native-export checks.
