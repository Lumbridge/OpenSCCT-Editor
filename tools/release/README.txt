{{TITLE}}
{{UNDERLINE}}

A map editor for Splinter Cell: Chaos Theory Versus maps and OpenSCCT.
https://github.com/Lumbridge/OpenSCCT-Editor


Running it
----------

The Windows OpenSCCT launcher also offers an optional Editor tab. It can install the editor
and its game/content prerequisites together, then open or update it. The editor's own updater
continues to work for launcher installations. This needs releases with launcher integration.

Double-click "OpenSCCT Editor.exe". Keep the whole folder together: the exe needs the
"data_OpenScctEditor_windows_x86_64" folder next to it.

The editor is not signed yet, so Windows may show "Windows protected your PC" the first time.
Click "More info", then "Run anyway". Updates the editor installs itself do not ask again.

Put the folder somewhere your account can write to (Documents, a games folder). In Program Files
the editor still tells you about new versions, but you have to install them yourself.

To open a map at start, run:
    "OpenSCCT Editor.exe" -- "C:\maps\MyMap.osmap"


Updates
-------

A few seconds after it starts, the editor asks GitHub whether a newer version is out (turn it off
in Edit > Preferences > General). A new version shows in the status bar. Click it to see what is
new, then Update now: the editor downloads it in the background, then restarts into it with your
map. Quitting with an update downloaded installs it too. Help > Check for Updates asks at any time.

The update is checked file by file before it is installed, and the new version is started once to
make sure it runs; if anything goes wrong the previous version is put back and the editor says why.
If an update is cut off half way (the computer shuts down), "OpenSCCT Editor.exe" may be missing
for a moment: sign out and in again (or restart Windows) and the previous version is put back. If
that does not help, download the zip again from the releases page and extract it over this folder;
your maps and settings are not in this folder.


Where things are
----------------

Your maps:           wherever you save them (.osmap folders)
Settings, autosaves: %APPDATA%\OpenSCCT Editor
Editor data:         %LOCALAPPDATA%\OpenSCCT-Editor (imports, caches, downloaded updates)

The editor needs OpenSCCT installed with its launcher (https://github.com/Lumbridge/OpenSCCT),
version 0.8.93 or newer. It uses the content the launcher installs (class data, converted maps,
materials and meshes) and the game itself (for Play and test rounds). Importing a map from, or
exporting one for, the original game reads your own copy of SCCT Versus.


Command line
------------

"ose.exe" in the ose folder is the editor's command line: it creates, checks, builds and edits maps
without the window, for scripts. Run "ose\ose.exe help" for the commands.


About
-----

OpenSCCT Editor is a fan project, not affiliated with or endorsed by Ubisoft. Splinter Cell and
Chaos Theory are trademarks of Ubisoft Entertainment. The editor contains no game files: it reads
your own game files and OpenSCCT's content at run time.

THIRD-PARTY-NOTICES.txt lists the licences of what the editor includes: the Godot engine, the
.NET runtime and others.
