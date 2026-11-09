# Texture import, authored materials and built-in tutorials

Editor **0.2.34** and game **0.8.122** add matching authored-material support. The game includes the published 0.8.119 baseline.

- Import complete texture sets with reviewed assignments, packed channels, colour spaces and preserved 16-bit height data.
- Create and edit materials with Undo/Redo, presets, adjustable previews and existing surface/mesh-section application controls.
- Animate atlases or numbered frames with playback, ping pong, phase, scrolling, rotation and independent frame mipmaps.
- Use normal/bump maps, parallax, tessellation/displacement, optional physical collision, PBR shine, emission, AO, detail layers and approximate extended finishes.
- Share authored sampling and directional baked-light response between editor and game; optionally bake emissive world and static-mesh illumination.
- Embed native Texture/Shader objects in original-game exports, report legacy differences and refuse unsupported physical relief. Original-editor folders include the same native editor copy.
- Add texture import and material inspection/editing commands to `ose`, plus six built-in material tutorials: texture import, animation, lighting and shine, depth and collision, emissive baking, and original-game export.

Extract the editor and game into separate folders. Use the matching game's executable for editor play tests. The game needs the usual installed/converted game content; original-game assets are not included. Existing installations and unrelated worktrees are preserved.

See `ADVANCED-MATERIALS.md` for the exact feature matrix and limits, and `ADVANCED-MATERIALS-VALIDATION.md` for acceptance coverage. Native export reduces animation to a reported first frame, flattens visual relief, and cannot retain displaced gameplay collision. Refraction, anisotropy and the single-lobe baked-light model are approximations. Broad low-end hardware and full stock-map visual acceptance remain outstanding.

Find the lessons in **Help → Browse Tutorials**, or press **Ctrl+Shift+P** and search by topic. Stock EMF capture, first-use/driver and large-map shutdown acceptance remains open as described in the supplemental stock report; those checks are not reported as passing.
