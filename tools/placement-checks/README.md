# Placement regression checks

Run `dotnet run --project tools/placement-checks/PlacementChecks.csproj`.

Checks off-origin mesh contact and footprint centring through eight 45-degree turns on floors,
axis-aligned walls and oblique walls; group spacing and shared-centre rotation; merged move/turn
undo and redo; full-turn drift; the bomb and extinguisher rules; window/switch mounting-plane alignment on walls, floors and ceilings;
and free logical-marker defaults and the universal Shift placement hint.

The project references the app to exercise the actual transform snapshot code. It does not open
an editor or change user maps. Visual previews and input routing still require an editor run.
