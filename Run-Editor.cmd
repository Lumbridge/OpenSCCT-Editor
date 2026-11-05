@echo off
rem Starts the OpenSCCT Editor. Double-click it, or drop a .osmap folder on it to open that map.
rem Each start brings the pinned OpenSCCT checkout (..\OpenSCCT-Editor-deps, at openscct.ref) up to date, rebuilds the
rem editor if anything changed and re-imports its files after a pull, so a fresh pull is always what runs.
rem Godot comes from OSE_GODOT, else tools\godot in the OpenSCCT clone (OSE_OPENSCCT_GIT, else ..\OpenSCCT next to this
rem repo, else the clone an existing ..\OpenSCCT-Editor-deps worktree belongs to).
setlocal
set "HERE=%~dp0"
set "GODOT_IN_CLONE=tools\godot\4.7.2-stable\Godot_v4.7.2-stable_mono_win64.exe"
set "GODOT=%OSE_GODOT%"
if not defined GODOT if defined OSE_OPENSCCT_GIT set "GODOT=%OSE_OPENSCCT_GIT%\%GODOT_IN_CLONE%"
if not defined GODOT if exist "%HERE%..\OpenSCCT\%GODOT_IN_CLONE%" set "GODOT=%HERE%..\OpenSCCT\%GODOT_IN_CLONE%"
if not defined GODOT call :clone_of_deps
if not defined GODOT if defined DEPS_CLONE if exist "%DEPS_CLONE%\%GODOT_IN_CLONE%" set "GODOT=%DEPS_CLONE%\%GODOT_IN_CLONE%"
if not defined GODOT set "GODOT=%HERE%..\OpenSCCT\%GODOT_IN_CLONE%"
if not exist "%GODOT%" (
    echo Godot 4.7.2 .NET was not found at:
    echo   %GODOT%
    echo Set OSE_GODOT to Godot_v4.7.2-stable_mono_win64.exe, or run tools\Get-Godot.ps1 in your OpenSCCT clone.
    pause
    exit /b 1
)

echo Checking the pinned OpenSCCT checkout...
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%tools\Sync-OpenScct.ps1" || (pause & exit /b 1)

rem An up-to-date build takes a few seconds; a stale one is rebuilt.
echo Building the editor...
dotnet build "%HERE%app\OpenScctEditor.csproj" -nologo -v:q -clp:ErrorsOnly || (pause & exit /b 1)

rem Re-import when the editor's files changed since the last import (a new commit), or were never imported.
set "HEAD="
for /f %%h in ('git -C "%HERE%." rev-parse HEAD 2^>nul') do set "HEAD=%%h"
set "STAMP=%HERE%app\.godot\ose-imported-head"
set "LAST="
if exist "%STAMP%" set /p LAST=<"%STAMP%"
set "IMPORT="
if not exist "%HERE%app\.godot\imported" set "IMPORT=1"
if not "%HEAD%"=="%LAST%" set "IMPORT=1"
if defined IMPORT (
    echo Importing the editor's files...
    "%GODOT:mono_win64.exe=mono_win64_console.exe%" --headless --path "%HERE%app" --import
    if defined HEAD (echo %HEAD%)>"%STAMP%"
)

rem This session's own log, so no other run of the editor (a headless check, a background run) can take it over.
rem The editor keeps the newest ten.
set "LOGDIR=%APPDATA%\OpenSCCT Editor\logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
set "SESSION="
for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "SESSION=%%t"
if not defined SESSION set "SESSION=%RANDOM%"
set "LOG=%LOGDIR%\editor-%SESSION%.log"

if "%~1"=="" (
    start "" "%GODOT%" --path "%HERE%app" --log-file "%LOG%"
) else (
    start "" "%GODOT%" --path "%HERE%app" --log-file "%LOG%" -- "--open=%~f1"
)
exit /b 0

rem Sets DEPS_CLONE to the OpenSCCT clone that ..\OpenSCCT-Editor-deps is a worktree of, when there is one.
:clone_of_deps
set "DEPS_CLONE="
set "DEPS_COMMON="
if not exist "%HERE%..\OpenSCCT-Editor-deps\.git" exit /b 0
for /f "delims=" %%c in ('git -C "%HERE%..\OpenSCCT-Editor-deps" rev-parse --path-format^=absolute --git-common-dir 2^>nul') do set "DEPS_COMMON=%%c"
if not defined DEPS_COMMON exit /b 0
for %%d in ("%DEPS_COMMON:/=\%\..") do set "DEPS_CLONE=%%~fd"
exit /b 0
