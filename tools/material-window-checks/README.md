# Native material window acceptance

Run on Windows from the editor checkout:

```powershell
dotnet build app/OpenScctEditor.csproj -p:MaterialWindowChecks=true
python tools/material-window-checks/run_native.py --godot <Godot-4.7.2-mono-exe>
```

The opt-in check uses an invisible Windows desktop, isolated settings and actual native dialogs. It never opens on the user's desktop. The default fixture generates an image in every texture slot. `--fixture <copy.osmap>` additionally tests a project containing `MyLevel.Textures.Material` without saving changes to it.

The 81 assertions cover 1920×1080, 1280×720 and 1024×768 work areas at 100%, 150% and 200% content scale. They check the full native frame including decorations, reachable Save/Cancel buttons, and stable sizing while the expanded texture controls rebuild. Client screenshots and a log are written beneath `local/material-window-checks` (or `--out`). File-picker interaction is separate from these window-layout checks.

The 0.2.35 native baseline reproduced a 1100×4681 initial client area while its settled content minimum was only 892×608. Embedded screenshot tests missed the native wrapping behavior. Production builds omit this test shell part unless `MaterialWindowChecks=true` is explicitly supplied.
