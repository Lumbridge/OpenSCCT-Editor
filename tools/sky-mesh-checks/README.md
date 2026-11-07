Run `dotnet run --project tools/sky-mesh-checks`. No content packs or Godot required.

The fixture imports an OBJ and compiles two vertically stacked rooms. It checks mesh sky/unlit flags, the editor's
sky-zone classification, and exclusion of the upper sky room from the strategic map. It then keeps the compiled
world, swaps the mesh actors between rooms, and verifies that their current properties replace the stored flags.
Removing the sky camera must clear the cached preview. Temporary files are removed on success and failure.

Visual checks use an isolated background editor (see `tools/Start-BackgroundEditor.ps1`): inspect a textured sky mesh
in Unlit and Lit, delete/undo its SkyZoneInfo, fly into the sky room itself, switch to Wireframe and back, and fly
through the playable world. The sky must follow camera direction without following translation; its geometry must
remain editable in its room and in orthographic views. Check stderr for rendering failures. These checks do not
prove that the intermittent Godot mesh-upload crash is eliminated.
