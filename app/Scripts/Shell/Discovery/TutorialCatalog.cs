namespace OpenScctEditor.Shell;

public sealed record TutorialStep(string Title, string Body, string Target);
public sealed record EditorTutorial(string Id, string Title, string Keywords, TutorialStep[] Steps);

/// <summary>Small task-based lessons. Targets resolve against the current UI, never cached control paths.</summary>
public static partial class TutorialCatalog
{
    private static TutorialStep S(string title, string body, string target) => new(title, body, target);
    public static IReadOnlyList<EditorTutorial> All { get; } =
    [
        new("ladder", "Set up a ladder", "climb traversal ge", [
            S("Choose Ladder", "The GE tool is now set to Ladder. Its options control the visible ladder and its climbable route. Leave geometry enabled when you need a ladder mesh as well as traversal.", "tool:ge|ladder"),
            S("Draw from bottom to top", "In a view, click the ladder's foot then its top, or drag between them. Keep the approach and the space above the landing clear. Escape cancels an unfinished draw.", "view:"),
            S("Adjust the route", "Drag the endpoint dots to adjust height; the facing dot points toward the side the spy climbs. Grid and neighbour snapping help align it with the wall. Undo removes a placement.", "tool:ge|ladder"),
            S("Check and try climbing", "Run Map Check, then Build and Play. Approach the foot as a spy and test the top exit in both directions; a drawn route still needs a gameplay test.", "action:build.check")]),
        new("laptop", "Set up a laptop objective", "computer hack terminal story mission", [
            S("Place a computer once", "If you already have a computer, select it and use Show me again. Otherwise drag Computer onto a reachable floor or table once. It creates its own objective unless an objective was selected. The Building Objective computer prefab also includes a linked Story mission.", "placeable:SBase.SComputerObjectiveTrigger"),
            S("Use its existing objective", "Select the placed computer. Its objective was created or linked during placement. Inspect that existing objective in Missions instead of creating a second one.", "setup:objective"),
            S("Check its mission", "Inspect the existing objective's mission. Keep an existing link; only choose or create a Story mission if this objective has none.", "setup:mission"),
            S("Check the device link", "The placed computer is already connected to its objective. Check the relationship in Missions. Add devices is only needed for a missing link or an additional device.", "setup:devices"),
            S("Validate and play", "Run Map Check, then play as a spy. Check that the laptop appears, can be reached and completes the intended objective when hacked.", "action:build.check")]),
        new("bomb", "Set up a bomb objective", "target plant defuse explosive story", [
            S("Place a bomb target once", "Reuse a target already placed. Otherwise drag Bomb target onto its mounting surface once. Placement creates an objective or uses the one selected; leave room for planting and defusing.", "placeable:SBase.SBombTargetObjectiveTrigger"),
            S("Use its existing objective", "The placed bomb target already has an objective. Select the target to inspect that relationship rather than creating another objective for it.", "setup:objective"),
            S("Check its mission", "Keep an existing mission link. If the generated objective has no mission, select that objective and use Choose mission to add it to the intended Story mission.", "setup:mission"),
            S("Check the target link", "The bomb target is already connected to its objective after normal placement. Use Add devices only to repair a missing link or add another target.", "setup:devices"),
            S("Test both teams", "Check the map and play the plant, countdown and defuse interactions with both teams. Linking alone does not prove the full round works.", "action:build.check")]),
        new("disk", "Set up a disk and drop-off objective", "disc flag carry extraction dropoff drop zone", [
            S("Place a carry disk once", "Reuse an existing carry disk, or drag Disk to carry into the map once. It creates an objective or uses the one selected. Hunt disk is a different game-mode device.", "placeable:SBase.SFlag"),
            S("Place its drop zone", "Select the disk first, then drag Disk drop zone to the destination. Placeables use the selected disk as the wiring target. Keep Wiring To Selection helps when placing several zones.", "placeable:SBase.SFlagDropZone"),
            S("Inspect the disk connection", "Select the disk and inspect its DropZone references. Confirm they point at the intended drop zones; give the disk and destination readable names.", "setup:inspect"),
            S("Use the existing disk objective", "The carry disk is already connected to the objective made during placement. Keep an existing mission link; choose a mission only when that objective has none.", "setup:mission"),
            S("Try the full carry route", "Check the map and play as a spy: take the disk, carry it to the drop zone, and confirm the intended objective completes. Check that mercenaries can contest the route.", "action:build.check")]),
        new("chain", "Set up chained objectives", "mission sequence order sequential story chain", [
            S("Choose the existing mission", "Open the Mission / objective diagram below to see how tasks fit together. In Missions, select the intended existing mission. Keep its linked objectives; Add objectives… is only for tasks missing from this mission. New objective is for a separate task with no objective yet.", "panel:Missions"),
            S("Enable the chain", "Select the intended mission actor in the map or mission tree. In Inspector, enable bChained. Use All properties if it is hidden. A chain requires every child in order; MinimumObjectives and RandomObjectives apply only when bChained is off.", "panel:Inspector"),
            S("Set the order", "Return to Missions. Use the up/down arrows beside linked objectives to set their order. Each objective needs its own connected computer, bomb target or disk.", "panel:Missions"),
            S("Check progression", "Use Check to find broken links, then play through the chain from the beginning. Confirm later objectives activate in the intended order.", "panel:Missions|Check")]),
        new("zones", "Set up mission zones", "zoned map stages areas phases nested missions story chain hierarchy", [
            S("Plan the hierarchy", "Open the Mission / objective diagram below. One Story mission contains zone missions; each zone contains its own objectives and devices. Reuse existing objectives and devices. A mission zone groups gameplay tasks and does not create a physical room or BSP zone.", "panel:Missions"),
            S("Prepare the zone missions", "Select an existing zone mission, or use New mission once for each missing zone. In Inspector, name it and set GameMode to Undefined (GM_Undefined, 0). Keep Story (GM_Multi, 5) on the top mission only, so the game activates zones through their parent.", "panel:Inspector"),
            S("Put existing tasks in their zones", "Select each zone in Missions and use Add objectives… to link its existing tasks. When moving a task out of the top mission or another zone, unlink its old parent row with ×. This keeps the objective and its devices; each task should have one parent.", "panel:Missions|Add objectives…"),
            S("Link zones to the top mission", "Select the top Story mission and use Add objectives… to link the existing zone missions in the intended order. Keep the zone missions' GameMode Undefined. Do not also list their individual objectives in the top mission, and do not link a mission back into its own descendants.", "panel:Missions|Add objectives…"),
            S("Choose progression within each level", "Enable bChained on the top mission for Zone A then Zone B. Use the tree arrows to order its zones. Each zone has its own bChained setting: turn it off for simultaneous tasks, and set MinimumObjectives for a required count (0 means all). A plain objective with several devices completes when any one is taken.", "panel:Inspector"),
            S("Open routes and test the stages", "For doors, lights or messages, select the zone and enable Completion actions. Wire the intended actors; a mission zone does not close routes by itself. Check the map and play Story from the start: finish Zone A, verify Zone B activates, and confirm the final zone ends the round.", "panel:Missions|Completion actions")]),
        new("name", "Name your level", "rename map title identity author description", [
            S("Set the name", "The Identity panel's Name row controls the project title and the name shown in map menus. Enter a short, recognisable name.", "panel:Identity|Name"),
            S("Add the map details", "Fill in Authors, Description and recommended Players. The dossier's When, Where and Summary describe the mission setting.", "panel:Identity|Authors"),
            S("Save your project", "Save the .osmap project in its own folder. Use Build when you want the game to see the updated map identity.", "action:file.save")]),
        new("pictures", "Set up a loading screen and thumbnail", "image picture menu banner identity screenshot", [
            S("Set the menu thumbnail", "Identity → Pictures → Menu is the square map-list thumbnail. Use Set to choose an image file, a camera bookmark or the generated plan where offered.", "panel:Identity|Menu"),
            S("Add spy loading pictures", "At Loading (spies), choose Add and an image or camera source. The game picks one of this side's loading pictures each time the map loads.", "panel:Identity|Loading (spies)"),
            S("Add mercenary loading pictures", "Set Loading (mercs) too. Use each picture's crop controls to keep the subject visible. Banner is the optional wide map-browser image.", "panel:Identity|Loading (mercs)"),
            S("Render camera pictures", "Camera recipes need a saved camera bookmark. Use Render now to render those pictures through OpenSCCT and rebuild; file-based pictures do not need a camera render.", "panel:Identity|Render now"),
            S("Save and inspect in game", "Save and build, then inspect the map list and loading screens in the game. Choose legible pictures that describe the map's layout and atmosphere.", "action:file.save")]),
        new("doors", "Create and edit doors", "door doorway hinge swing sliding roller frame opening resize", [
            S("Choose a door style", "Doors contains six built-in styles. Choose one, then set Width, Height and Movement below it. A hinged door swings about its left or right jamb; sliding and raised doors move clear of the opening.", "panel:Doors"),
            S("Preview the doorway", "Click Place Door, then point at the bottom of an editable wall. The preview shows the closed leaf and its open position. Wall placement cuts the opening; retained baked walls need an existing opening. R flips wall facing or rotates a free-standing door. Escape cancels.", "panel:Doors|Place Door"),
            S("Place and resize", "Click once to place the door or drag a style tile into a view. Select the placed door and activate Scale for Width and Height handles. Height changes keep the bottom on the floor. Undo reverses each completed edit.", "action:tools.scale"),
            S("Set its opening", "In Inspector's Door section change style, movement, angle or travel, Open time and Stay open. Contact · timed opens on player contact. Show open and Show closed let you inspect both poses while the frame stays fixed. Door settings rebuild the two movement keys.", "panel:Inspector"),
            S("Connect triggered doors", "The Trigger activation choices need a linked switch or trigger. The Door with a switch prefab includes the event connection. Use the separate door-switch tutorial for that workflow.", "panel:Prefabs"),
            S("Build and test both sides", "Run Map Check, Build and Play. Walk through from both sides, check the full swing or sliding space, and confirm the door closes as intended. Editable doors build for OpenSCCT. Save the project to keep its editable design.", "action:build.check")]),
        new("door-switch", "Place a door with a switch", "door trigger switch event toggle timed button prefab", [
            S("Find the linked assembly", "In Prefabs search for Door with a switch. This assembly includes a generated door, frame, doorway cut and a switch already linked to that door. Each placed copy has its own event connection.", "panel:Prefabs"),
            S("Set dimensions before placing", "Choose the prefab. Set width, height and wall depth, then choose state: TriggerToggle alternates open and closed, TriggerOpenTimed closes after stayOpen seconds, and TriggerControl follows the trigger. Keep mesh empty for the editable built-in door.", "panel:Prefabs"),
            S("Place across a wall", "Drag the prefab into the map at the floor in the middle of a wall. Its wall runs along Y and the switch is on +X. Move or rotate the whole instance so the opening reaches open space on both sides. The switch needs a reachable approach.", "view:"),
            S("Edit a placed assembly", "Select any part and use Show in list in Prefabs to reveal its definition. Change the parameters and choose Apply to selected to rebuild that instance. To keep individual hand edits, Break the instance first. Door dimensions and movement can also be edited in Inspector.", "panel:Prefabs"),
            S("Test the switch and door", "Run Map Check, then build and play. Use the switch from its front and watch the door open. Try a second copy too: each switch should control its own door. The switch model requires shared gameplay content.", "action:build.check")]),
        .. DoorLessons(),
        .. AnimationLessons(),
        new("prefabs", "Browse and customise prefabs", "prefab library favourites furniture storage industrial props parameters thumbnail", [
            S("Browse the library", "Prefabs includes shaded previews of architecture, furniture, storage, industrial props, lights, doors and gameplay assemblies. Search by name or description, then narrow the category and library filters.", "panel:Prefabs"),
            S("Keep useful pieces", "Select a tile to inspect its description and settings. Add favourite marks a frequently used piece; turn on Favourites to show your starred library. Settings stay with a tile while you browse and resize the panel.", "panel:Prefabs"),
            S("Place with your settings", "Adjust the selected prefab's parameters, then drag its tile onto a surface. Double-click or Place uses the view centre. Reset settings restores defaults. The whole assembly is placed as one undoable edit.", "panel:Prefabs"),
            S("Edit instances or make your own", "Select a placed part to update, select all parts or break its link in Prefabs. Apply to selected rebuilds that instance with the shown settings. Save selection as prefab stores your own reusable assembly in this map or your personal library.", "panel:Prefabs")]),
        new("glass", "Use the glass and windows tool", "window breakable frame tint", [
            S("Choose a window", "The Windows palette offers glass and framed windows in several shapes and finishes. Choose a preset that fits your wall.", "panel:Windows"),
            S("Place or fit the glass", "Drag the preset into a view. Framed windows cut editable walls. To fill an existing frame, right-click inside the frame and choose the glass-fitting option.", "panel:Windows"),
            S("Adjust size and finish", "Select the window and use its handles or Inspector to resize it and change the available glass properties. Leave the frame and glass linked when resizing the assembly.", "panel:Inspector"),
            S("Test collision and breakage", "Check, build and play. Inspect the glass from both sides, test breakage and collision, and rebuild lighting after changing its shape or tint.", "action:build.check")]),
        new("surface", "Use Surface tools to build connected rooms", "wall doorway corridor floor ceiling opening hatch", [
            S("Select Surface", "Click the highlighted Surface tool. It picks individual brush faces, so you can work on a wall, floor or ceiling without selecting the whole brush.", "action:tools.face"),
            S("Open the surface menu", "With Surface active, click an editable wall. Hover near its floor and click the blue + badge. Choose a doorway, then use its far wall to extend a corridor and another room.", "view:"),
            S("Work on floors and ceilings", "A floor or ceiling's + menu offers openings, shafts and room connections. Pick the actual editable slab; imported baked geometry may need brush recovery first.", "action:tools.face"),
            S("Edit the selected surface", "Surface provides material and texture alignment controls. Keep your face selection while changing these values; Undo restores an unwanted edit.", "panel:Surface"),
            S("Check the connection", "Run Map Check and inspect both ends. A visible doorway must connect open space on both sides to be walkable.", "action:build.check")]),
        new("meshes", "Add meshes and props", "model staticmesh assets import gltf obj", [
            S("Find a mesh", "Assets is open on Meshes. Search by name, package or plain-English description. Ctrl+Shift+P can also find a specific indexed asset directly.", "assetkind:Mesh"),
            S("Place it", "Drag a mesh tile into the view and drop it on the intended surface. Double-click or Place uses the selected mesh. Select the placed prop to adjust it.", "assetkind:Mesh"),
            S("Import your own", "Use Assets → Import Mesh for glTF, GLB or OBJ files. Save the project first so imported files have a project asset folder. Import writes files; map placement can be undone.", "action:assets.importMesh"),
            S("Inspect and test", "Use Inspector to inspect the placed mesh's transform and collision settings. Check its scale and test whether players can move around it as intended.", "panel:Inspector")]),
        new("textures", "Add textures and materials", "paint skin image uv align surface assets", [
            S("Import a texture set", "Use Assets → Import Textures… and choose PNG, JPEG, BMP or WebP images together. Save As first if the map is unsaved. Review the proposed named slots: _albedo, _normal, _roughness, _metallic, _height, _ao and _emissive are recognised; ORM uses red AO, green roughness and blue metallic. Import does not paint the selection.", "action:assets.importTextures"),
            S("Author and preview", "Create Material… makes a new material; Edit Material… changes the material selected in Assets. Review colour spaces and channels, choose a preset, and compare sphere, cube, plane or Current selection with adjustable lighting. Surface, Depth and Texture Animation hold the remaining controls. Import or Save commits an undoable edit.", "action:assets.createMaterial"),
            S("Select the faces", "Activate Surface and select the brush faces you want to paint. Use the tool's selection modifiers to select more than one face.", "action:tools.face"),
            S("Find a material", "Assets → Materials contains ready-to-use surface materials. Search for a suitable finish, then click the tile to apply it to the picked faces, or drag it onto a face.", "assetkind:Material"),
            S("Browse textures", "Textures are the source images. A texture is applied through a material that shows it. Choose Materials when you want the complete surface appearance.", "assetkind:Texture"),
            S("Align and scale the texture", "Use the Surface panel to adjust alignment and texture scale on your selected faces. Texture scale changes the pattern; object Scale changes the geometry. For static meshes, use Assets → Apply Material to Mesh Sections…; −1 replaces every section.", "panel:Surface")]),
        new("animated-textures", "Make an animated texture", "flipbook frames atlas sprite sheet looping ping pong scrolling fps material animation", [
            S("Choose frames", "Use Import Animated Texture… and choose a numbered PNG, JPEG, BMP or WebP sequence. Frames must have the same dimensions. Numeric ordering puts frame2 before frame10. The importer packs a project-local atlas for review.", "action:assets.importAnimatedTexture"),
            S("Review playback", "In Texture Animation set FPS, Loop, Ping pong and Phase (frames). Pause and Restart let you inspect it. For an existing sprite sheet, use Import Textures… and enter Columns, Rows and Frames; playback reads left to right, then down.", "action:assets.editMaterial"),
            S("Animate the whole material", "All assigned maps use the same atlas layout and frame. Scroll U, Scroll V and Rotate add continuous UV motion. FPS 0 holds a frame. Real displacement requires a static texture set so render geometry and gameplay collision agree.", "panel:Assets"),
            S("Apply and test", "Import, select the revealed material and apply it through Surface or Apply Material to Mesh Sections…. Build and Play to check the animation under the game's lighting and vision modes. Original-game export reports a first-frame fallback; it does not reproduce modern playback.", "action:build.check")]),
        new("scale", "Move, rotate and scale things", "resize size dimensions transform bigger smaller", [
            S("Select something", "Use Select to pick an object. For individual surface editing use Surface; for a whole prop or brush use Select.", "action:tools.select"),
            S("Move", "Activate Move and drag an axis handle. Grid and neighbour snapping help position objects precisely. Escape cancels the active gesture.", "action:tools.move"),
            S("Rotate", "Activate Rotate and drag a rotation ring. For exact values, edit the selected object's transform in Inspector.", "action:tools.rotate"),
            S("Scale", "Activate Scale and drag its handles. Builder shapes, stairs, windows, doors and GE routes also have dedicated dimension handles. Review collision and texture appearance after large changes.", "action:tools.scale"),
            S("Correct mistakes", "Undo restores the previous edit; Redo brings it back. The History panel lets you inspect the recent edit sequence.", "panel:History")]),
        new("geometry", "Build rooms with Geometry and the builder", "brush shape box subtract add blockout", [
            S("Pick a shape", "Geometry contains floors, walls, rooms and passages. Choose a preset or use the builder preview to customise one before committing it.", "panel:Geometry"),
            S("Position and resize", "In Builder Brush, use Place Here to preview surface contact. Arrows move, squares resize with the opposite face fixed, and circles change radii.", "action:tools.brush"),
            S("Commit the shape", "Add creates solid geometry; Subtract carves space out of it. Check the preview before committing, and Undo if the shape goes in the wrong place.", "action:brush.subtract"),
            S("Connect spaces", "Switch to Surface and use the + menu on a wall to add a doorway or corridor between rooms.", "action:tools.face")]),
        new("stairs", "Build stairs between floors", "staircase steps storey height", [
            S("Choose a staircase", "Open Stairs and choose a preset. Place the assembly where the two floor heights need a connection.", "panel:Stairs"),
            S("Fit the assembly", "Select the stairs. Use their handles and Inspector controls to adjust dimensions and landings. Keep sufficient headroom above the route.", "panel:Inspector"),
            S("Check the opening", "Inspect the connection from above and below first. Keep an opening already made by a surface connector. Only if a slab still blocks the route, use Surface on that floor or ceiling to open a shaft or connect stairs.", "action:tools.face"),
            S("Walk the route", "Check and play the map. Test both directions and both teams so the step and landing geometry is usable.", "action:build.check")]),
        new("lights", "Add and tune lighting", "light fixture brightness radius bake shadows ambient", [
            S("Choose a light", "Lights contains fixtures and raw-light presets. Drag a fixture into a view to place its mesh and editable light together.", "panel:Lights"),
            S("Tune the light", "Select the light and use Inspector to adjust the properties available for that light: brightness, colour, range and animation.", "panel:Inspector"),
            S("Rebuild and inspect", "Build the map after static lighting changes. Inspect dark routes, glass transmission and how visible each team is during play.", "action:build.build")]),
        new("starts", "Set up team spawns", "player start spy mercenary teams spawn", [
            S("Find the starts", "Outliner lists your actors. Find the spy and mercenary player starts; click to select and double-click to frame them. New starter maps include one of each.", "panel:Outliner"),
            S("Move to a clear floor", "Use Move to position each start in clear space above a walkable floor. Face it toward the team's intended route and leave room around other starts.", "action:tools.move"),
            S("Set team and round rules", "Inspector exposes each start's properties. Match previews the round's missions and player-count rules.", "panel:Match"),
            S("Check both teams", "Check and play with both teams. Confirm players spawn on the floor, can move, and cannot see or attack the other spawn immediately unless intended.", "action:build.check")]),
        new("security", "Place cameras, lasers and alarms", "security sensor tripwire network", [
            S("Place an alarm", "Select an alarm tile in Placeables and drag it into the map. It can become the target of several security devices.", "panel:Placeables"),
            S("Choose the wiring target", "Select the alarm before placing a camera or laser. Keep Wiring To Selection pins that target while you place several devices.", "panel:Placeables|Keep Wiring To Selection"),
            S("Place and aim devices", "Security tiles mount on the aimed surface. Use Inspector to check each device's Event, range and orientation; keep the beam or view clear.", "panel:Inspector"),
            S("Inspect connections", "Use Connections to inspect event wiring. Check and play as a spy to confirm detection and alarm behaviour.", "panel:Connections")]),
        new("save", "Save, reopen and recover a map", "project osmap backup autosave recovery", [
            S("Save the project", "Save writes an .osmap project folder. Keep its files together, including project-local assets. Choose Save As when you need a separate copy.", "action:file.save"),
            S("Review changes", "History shows undoable edits in this session. Undo and Redo apply to map edits; they do not undo an imported file on disk.", "panel:History"),
            S("Reopen or recover", "Use File → Open for an existing project. If startup offers a recovery copy after a crash, inspect it and save a separate copy before replacing your original.", "action:file.open")]),
        new("check", "Find and fix map problems", "validate errors warnings map check", [
            S("Run Map Check", "Run the highlighted command to check the current map. This reports structural and gameplay findings without changing the map.", "action:build.check"),
            S("Inspect the findings", "Open Map Check. Select a finding to locate its object and read its explanation. Apply an offered fix only after checking its effect.", "panel:Map Check"),
            S("Check again after edits", "An old result becomes stale when the map changes. Run the check again, then build and play to verify collision, visuals and live gameplay.", "action:build.check")]),
        new("play", "Build and play your map", "test compile launch game bots", [
            S("Save and check", "Save the project and run Map Check. Resolve errors that prevent building or make the round unplayable.", "action:build.check"),
            S("Build and Play", "Build and Play compiles the current map and opens it in OpenSCCT. The game and required content must be configured; the first-map setup guide can help with missing prerequisites.", "action:build.play"),
            S("Review problems", "After play, inspect Log and Map Check for build errors. Test both teams and the full objective sequence; a successful launch is only the first check.", "panel:Log")]),
        new("assets", "Find assets and install content packs", "library search downloads materials mesh texture sound", [
            S("Search all installed assets", "Assets searches installed names, packages and English descriptions. Ctrl+Shift+P also finds individual assets without needing the right asset tab first.", "panel:Assets"),
            S("Install a pack", "Get Packs… opens the content-pack browser. Choose a pack to inspect before downloading it. Newly installed assets are indexed automatically.", "panel:Assets|Get Packs…"),
            S("Keep useful assets", "Use the asset favourite star for frequently used items. The command palette has a separate star for favourite commands, tutorials and destinations.", "panel:Assets")]),
        new("navigation", "Navigate the map and editor", "camera fly zoom orbit focus views shortcuts", [
            S("Fly around", "In the 3D view, hold the right mouse button to look and use W A S D to fly. Release it to interact with editor controls.", "view:"),
            S("Find objects", "Type @ in Ctrl+Shift+P to search map objects by id, class or tag. Enter selects and frames the result. Ordinary search also includes matching map objects.", "panel:Outliner"),
            S("Find any control", "Search a task, panel, class or asset name. Show me (Alt+Enter) reveals a command's control without running it. Move the mouse into and then out of a search highlight to dismiss it.", "action:view.commandPalette"),
            S("Learn your shortcuts", "F1 lists the current keyboard shortcuts, including customised bindings. You can keep tutorials beside the editor and use Back, Next or Cancel at any time.", "action:help.shortcuts")]),
        new("events", "Connect events and completion actions", "wiring trigger door message delay relay", [
            S("Inspect connections", "Connections shows event relationships. Select the actors you want to connect and inspect their tags and event targets.", "panel:Connections"),
            S("Add mission completion behaviour", "Choose your mission in Missions, then enable Completion actions. The expanded controls can fire actors, add messages and set timing.", "panel:Missions|Completion actions"),
            S("Build a sequence", "Event Sequencer supports more involved timing and event sequences. Inspect each target and test the sequence in game, including any delayed actions.", "panel:Event Sequencer")]),
        new("layers", "Organise a map with layers", "hide lock group organisation outliner", [
            S("Inspect your objects", "Use Outliner to find and select actors and geometry by name. Readable names make large maps and mission pickers easier to work with.", "panel:Outliner"),
            S("Use layers", "Layers groups related work. Hide or lock a layer while editing another part of the map, then restore visibility before the final inspection.", "panel:Layers"),
            S("Use history", "Review recent edits in History and undo a change when needed. Save working copies at milestones before larger layout changes.", "panel:History")]),
        new("bookmarks", "Save camera views and map pictures", "bookmark screenshot camera identity", [
            S("Choose a view", "Fly to a useful camera view. Keep the map's subject in frame and consider the crop used by the menu or loading picture.", "view:"),
            S("Save the camera", "Open Bookmarks and save the current camera with a readable name. You can return to it while editing or use it as a picture source.", "panel:Bookmarks"),
            S("Use it as a picture", "In Identity → Pictures, choose Set or Add, then a camera bookmark. Render now produces the game-rendered picture.", "panel:Identity|Pictures")]),
        new("emitters", "Add smoke and particle effects", "emitter particles fire effect", [
            S("Choose an effect", "Emitter Library contains stock effects available from installed content and any effects you have saved. Browse and preview before placing.", "panel:Emitter Library"),
            S("Place and tune", "Place the effect where it belongs, select its actor and inspect its emitter properties. Keep expensive effects limited to where they improve the scene.", "panel:Inspector"),
            S("Inspect in play", "Build and play to review the effect with the map's lighting, viewpoints and gameplay. Check visibility and performance around smoke.", "action:build.play")]),
        new("prefab-authoring", "Save reusable prefab assemblies", "prefab assembly kit template duplicate", [
            S("Choose a piece", "Prefabs contains built-in pieces, your library and project-local pieces. Choose a named piece to inspect its description and parameters before placing it.", "panel:Prefabs"),
            S("Place the piece", "Drag it into a view, or use the placement controls. A prefab can contain several linked actors; inspect the instance before breaking it into independent objects.", "panel:Prefabs"),
            S("Save your own", "Select the objects that belong in the reusable piece. Use the prefab panel's save controls, give it a readable name and choose a project-local or library destination.", "panel:Prefabs")]),
        new("sounds", "Find and preview sounds", "audio ambient sound effects music assets", [
            S("Find a sound", "Assets → Sounds lists the installed audio. Search by name, package or description and select a result to inspect its details.", "assetkind:Sound"),
            S("Preview it", "With a sound selected, use Play Selected Sound or the play button. Stop ends the preview. Browsing or searching alone does not play audio.", "action:assets.playSound"),
            S("Use the sound", "For an actor with a sound property, inspect that actor and set its asset reference. Save, build and listen in game to check range and timing.", "panel:Inspector")]),
        new("camnet", "Set up the mercenary camera network", "camnet network camera ring line surveillance", [
            S("Place network cameras", "Use Network camera, which belongs to the mercenaries' CamNet system. It is distinct from the security camera that detects spies.", "placeable:SBase.SCamNetwork"),
            S("Check the network already made", "Placement joins the selected network camera's ring, or the map's only network, automatically. Inspect that existing network first. Only for an unlinked or deliberately reordered set, select cameras in viewing order and use Ring or Line.", "panel:Camera Network|Ring"),
            S("Name and aim each camera", "Select a camera to edit its HUD name, turning limits and preview in Camera Network. Check the view from each camera and test with the mercenary CamNet gadget.", "panel:Camera Network")]),
        new("movers", "Build a moving platform and edit its stops", "mover platform path stops keyframe keys elevator lift trigger", [
            S("Create or select a platform", "In Mover choose Create platform when nothing is selected, or select a solid brush or static mesh and use Make selection move. Reuse a platform already placed. For ordinary doors use the dedicated Doors tutorials; this lesson covers general mover paths.", "panel:Mover"),
            S("Edit the stops", "Select the mover. Stop chooses a position along its path. Use Edit in view, then move or rotate the selected stop in a viewport, or edit Position and Rotation here. Move whole mover returns to the start pose so moving it shifts the complete path.", "panel:Mover"),
            S("Extend and time the path", "Extend path, Duplicate stop and Add at view centre create more stops. Travel time controls the leg entering a stop; At this stop controls its easing. Review the preview and timeline. Editing an authored door's Door settings later rebuilds its two-stop path.", "panel:Mover"),
            S("Connect and test", "Use Connections to review the trigger and the mover's activation. Check, Build and Play the complete route and return journey with both teams. Confirm riders, overhead clearance and collision along every leg, then save the source project.", "panel:Connections")]),
        new("storeys", "Work on multiple floors and storeys", "storey story floor height filter duplicate above below", [
            S("Inspect storeys", "Storeys lists the map's floor-height ranges. Choose a storey to focus the view on that part of the map.", "panel:Storeys"),
            S("Author the ranges", "Use the panel's controls to make detected storeys your own, then rename or adjust their heights. Keep the floor names meaningful to the map's layout.", "panel:Storeys"),
            S("Inspect the whole building", "Restore the full height range when checking connections between floors. Test stairs, shafts and ladders in both directions.", "panel:Traversal")]),
        new("rounds", "Set game modes and round rules", "match story diskhunt deathmatch player count time lives", [
            S("Choose map rules", "Map holds the map-wide and per-mode settings, including the available game modes, time limits and lives. Change the values for the mode you intend to play.", "panel:Map"),
            S("Preview the round", "Match previews which missions and objectives take part for the chosen player count and rules. Inspect anything left out before testing.", "panel:Match"),
            S("Review objectives", "Missions shows the named mission/objective/device links. Ensure the intended game mode activates the correct mission and enough connected objectives.", "panel:Missions"),
            S("Play with the intended rules", "Build and Play uses the current Play options. Test the intended mode and team size as well as the default configuration.", "action:build.play")]),
        new("traversal", "Add pipes, zip lines and overhead routes", "ge pole fence hand over hand ledge movement climb", [
            S("Choose a route type", "GE offers ladders, climbable pipes, zip lines, overhead bars, fences and ledge grabs. Choose the type that matches the physical route you want to build.", "action:tools.ge"),
            S("Draw and adjust", "Draw the route in a view, then drag its dots to fit the geometry. Keep the approach, exit and space around the spy clear. Escape cancels an unfinished draw.", "view:"),
            S("Inspect route activation", "Traversal contains ledge detection, GE IDs and activation-mask controls. Review the selected routes and which are active at round start.", "panel:Traversal"),
            S("Test the movement", "Check and play as a spy. Follow the complete route, including transfers between different traversal pieces.", "action:build.check")]),
        new("importexport", "Import and export map projects", "sdc svmap t3d original build convert save", [
            S("Open a source map", "File → Open and the import commands read supported map sources. Save an imported map as an .osmap working project before making larger edits.", "action:file.open"),
            S("Inspect imported geometry", "Use Outliner and Surface to inspect what is editable. Cooked maps may contain baked geometry; brush recovery and rebuilt lighting can differ from the original.", "panel:Outliner"),
            S("Choose the destination", "Build compiles an OpenSCCT map. The File menu's export commands target formats such as T3D or SDC. Read the export's checks and requirements for the intended game.", "action:build.build"),
            S("Verify the output", "Keep the .osmap project as the source. Open and play the exported or built result in its target game; a successful file write does not verify gameplay or visual parity.", "panel:Log")]),
        new("properties", "Inspect and edit actor properties", "inspector class setting fields advanced property sets", [
            S("Choose an object", "Select the actor or geometry you want to inspect in a view or Outliner. Inspector follows the selection and shows the settings that apply to it.", "panel:Outliner"),
            S("Find the property", "Use Inspector's property filter and available property views to find a setting by name. Hover for an explanation; select multiple objects for compatible batch edits.", "panel:Inspector"),
            S("Review the edit", "Change one setting at a time and inspect its effect. Undo restores the previous map value. Properties provides the workbench for supported property-set operations.", "panel:Properties")]),
    ];
}
