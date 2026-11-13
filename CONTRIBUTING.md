# Building and testing Meitou Transport

Use the **.NET 11 SDK** (11.0.100 for this candidate), **Python 3.9+**, and the **Windows x64 v0.18.0 game** release. Extract the game to a directory of your choice. `MEITOU_DIR` points to the directory containing `MeitouClient.dll`, `Meitou.Core.dll`, `Meitou.Data.dll`, `Meitou.Simulation.dll` and `0Harmony.dll`. If you prefer a project-local install, the default is `.local/game/Meitou-0.18.0-win-x64/`. The framework references these assemblies at build time. Players get them from Meitou. [MeitouClient v0.18.0 release](https://github.com/VlcekM/MeitouClient/releases/tag/v0.18.0)

## Build and check a change

Run these commands in PowerShell from the root of the checkout or extracted source ZIP.

```powershell
$env:MEITOU_DIR = (Resolve-Path (Read-Host 'Extracted Meitou v0.18.0 game directory')).Path
dotnet build framework/Meitou.Transport.csproj -c Release
dotnet run --project tests/Meitou.Transport.Tests.csproj -c Release
python tools/package.py
```

The Release build stages the framework DLL beside its FCS header. The packager writes four independently installable ZIPs to `dist/`. Use the same SDK and released assemblies to compare builds. Embedded debug symbols map source paths to `/_/`.

The regular test suite runs without starting Kenshi. It reads the real FCS headers and sample art, exercises pack discovery and overrides, and compares boat and mount simulation hashes tick by tick with one and four workers. Add a focused regression check for a change to pack loading, save compatibility, seat occupancy or movement. For rendering and native save changes, use the documented Windows live scenarios. They use an installed copy of Kenshi and a separate trial directory. [Live verification guide](tests/LiveVoyage/README.md)

## Repository conventions

Use UTF-8, LF and spaces. C# uses four spaces and braces on new lines. JSON and MSBuild files use two spaces. The SDK treats build warnings as errors. These conventions follow the parts of Meitou's style that apply to this repository. Keep diffs focused and preserve shipped pack, craft and placement IDs across upgrades. [Meitou editor settings](https://github.com/VlcekM/MeitouClient/blob/v0.18.0/.editorconfig)

For a formatting pass, restore or build the relevant projects first. The last project below is the optional live verifier.

```powershell
$projects = 'framework/Meitou.Transport.csproj', 'tests/Meitou.Transport.Tests.csproj', 'tests/LiveVoyage/LiveVoyage.csproj', 'tools/DesertRide/DesertRide.csproj'
foreach ($project in $projects) {
    dotnet format whitespace $project --verify-no-changes
    if ($LASTEXITCODE) { throw "Formatting differs in $project" }
}
git diff --check
```

Track source, original pack headers and art, guides and test fixtures. Keep SDKs, game files, compiled DLLs, ZIPs, local settings, caches, test saves and logs outside the tracked tree. Put full live trials in a separate directory or under `.local/`. The code and original pack assets have different reuse terms. [License scopes](LICENSING.md)

## Build a local tester handoff

Set the version in the framework project, code-mod manifest and live-verifier manifest together. Commit the reviewed tree. Run this command with Git, Python and the SDK on PATH and `MEITOU_DIR` set.

```powershell
python tools/package.py --handoff
python tests/check_handoff.py
```

The first command exports Git HEAD, builds its framework in a fresh directory and packages the mod folders. The `*-handoff.zip` in `dist/` contains those installable archives, corresponding source, guides, license texts and SHA-256 checksums. The second command checks the source commit and checksums, then rebuilds all four installable ZIPs from both the checkout and the source ZIP and compares their bytes against the kit. The handoff command requires a clean checkout so a binary and its source cannot silently come from different revisions. Tag the verified commit with an annotated prerelease tag matching the manifest.
