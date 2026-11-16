# Original PS2 objectives

Editor 0.2.48 adds **PS2 ND133** and **PS2 vial** beside the existing Computer, Bomb target and Disk to carry tiles
in the Placeables palette. They require OpenSCCT 0.8.158 or newer.

Drag **PS2 ND133** onto a reachable surface for an original animated console that spies hack in Neutralization.
Its objective and activating Neutralization mission are included. CaptureTime and DominationCaptureTime are ten
seconds initially and remain editable. Hacking opens the original case; this mode does not create a carried tube.

Drag **PS2 vial** onto a surface for an Extraction tube with its animated console companion. The two form a linked
assembly, so moving, turning or duplicating them keeps their relationship. With the vial selected, place the existing
**Disk drop zone** tile: it becomes the original PS2 touch-delivery zone and is linked to that vial. The zone is a
hidden volume in the game, not a PC disk case. Spies take and carry the tube; a merc can return a dropped tube, and
touching a linked delivery zone completes its objective. The original source right-thigh attachment is retained.

Placing either preset selects its mode and map rules for Play. The modes also appear in Map Settings and
Build > Play Options. Set how many objectives are required on the activating mission. Put cases within reach,
and light the actors with normal in-game lights or zone ambient lighting as on other maps.

The Editor release carries only the bounded console/vial library, under `Data/Gameplay/Ps2Objectives`. It includes
the original PS2 vial geometry and textures, console materials and the four matching PS2 animation sequences.
The source/hash manifest records provenance; original binary inputs and the converted bundle stay outside Git.
Keep Data with both the application and the packaged command-line tools.

On first save, the library is copied into the project's own `assets/ps2-objectives` folder. Saving, Save As and
reopening do not depend on installed PS2 map packs. A build carries the console model, animation metadata, state
skins, textures and vial library mesh in the resulting SVMap. Existing project assets with conflicting aliases
are preserved and reported before any bundled files are copied.

The presets serialize ordinary supported `SComputerObjectiveTrigger`, `SFlag` and `SFlagDropZone` classes with
explicit per-instance PS2 properties. Stock PC classes retain their usual defaults. These authoring profiles build
for OpenSCCT; original-game exports refuse them rather than silently substituting a PC disk. Use RE+'s native ND133
preset for original-game Neutralization authoring.
