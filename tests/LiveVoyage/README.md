# Live riding and voyage verification

Set `MEITOU_DIR` to the extracted v0.18.0 Windows game directory. Build the framework and this verifier in Release, then run `python tools/package.py`. For the Stoat Pack Beast trial, also run `dotnet build tools/DesertRide/DesertRide.csproj -c Release`. Both Stoat scenarios exercise the local v0.18.0 build.

## Windows release trials

```powershell
$kenshiInstall = (Resolve-Path (Read-Host 'Kenshi installation directory')).Path
$trial = Join-Path ([IO.Path]::GetTempPath()) ("transport-bull-" + [guid]::NewGuid())
.\tests\LiveVoyage\run.ps1 -Scenario bull -Kenshi $kenshiInstall -Game $env:MEITOU_DIR -TrialDirectory $trial
```

Use a fresh trial directory for each run. The runner starts one headless client, captures the actual Vulkan frames, then asks the client to quit. It links the installed game data, stages the ZIPs into private mod folders, and keeps test settings, temporary files, screenshots, logs and new saves under that trial. It skips the derived terrain cache because each temporary installation path would generate a separate copy. Optional `-CacheDirectory` directs object and impostor caches to a chosen drive. The checks use four or fewer worker processors, a 24 fps cap and on-demand JIT compilation.

For a lightweight visual check, choose **fit**. It runs the released client's flat animation scene with just a trader and an adult Garru. The driver boards, walks, runs, turns, dismounts, remounts and checks ordinary rider movement. Normal-world trials cover slopes and save restoration. To render the two-character scene entirely on the CPU, pass an installed software Vulkan ICD JSON path with `-CpuVulkanIcd`. The runner checks the chosen adapter before creating the device.

- **bull** follows the packaged author guide to create **Pack Bull Saddle.mod** with its own manifest and asset filenames. Only the framework and this pack are enabled. The client starts **The Wandering Trader** with its ordinary Pack Bull.
- **stoat** starts the packaged **Stoat Desert Ride** with its normal FCS squad. It checks the authored position, female Scorchlander and all four equipment slots before mounting at the actual spawn. After riding and reloading it also mounts, rides and dismounts the Domesticated Bull. The player can independently choose this start from New Game to record without the driver.
- **stoat-bags** swaps the adult Garru for an adult stock Pack Beast in the trial's copy of the FCS squad. The client equips and renders its own stock backpack. The driver checks its item ID, skinned luggage mesh and inventory through save and reload. Run **stoat** and **stoat-bags** consecutively in separate trial directories to compare the same turn, camera angle, and original saddle fit. Each run saves HUD-free `ride-<tick>.png` frames during the run-up and turn, plus `walk.png` and `turn.png`. The log records heading and maximum saddle and pelvis placement error in world units.
- **bare** uses an adult stock Garru and **garru** and **bags** use the trader fixture's younger Pack Beast. The fixture supplies the animal record and starting age, then the game creates both characters normally. **mixed** also enables Western Hive Skiff.
- Each ride goes through the real F10 handler and native move orders. The driver compares the submitted saddle and the rider's posed pelvis with the animal bone each rendered frame, then saves, reloads, remounts, dismounts and moves on foot. Look for `LIVE RIDE PASS` and inspect `walk.png` and `turn.png`.
- **core** checks the empty framework, **example** replays the Western Hive voyage, and **old-save** takes an additional `-OldSave` path to an existing Western Hive slot. It copies the slot, verifies the saved hull pose, then boards and sails again. Look for `LIVE VOYAGE PASS`.

## Run the boat driver manually

Run `python tests/LiveVoyage/prepare.py author <empty-QA-mods-directory>` for the independent-author trial. The script extracts the packaged framework and follows its bundled guide to create the data-only **Skiff.mod**. Use `core` for the framework alone or `example` for the two shipping ZIPs. Point Meitou at a test installation with the original game data and those mod folders. Supply a separate `--mods-cfg` listing the framework first and the chosen content mod second.

Set the following variables for the **test process** and start the v0.18.0 game with `--new-game Wanderer`.

| Variable | Value |
| --- | --- |
| `DOTNET_STARTUP_HOOKS` | Absolute path to the built `Meitou.Transport.LiveVoyage.dll` under `tests/LiveVoyage/bin/Release/net11.0`. This runs the QA driver outside the game's mod list. |
| `MEITOU_TRANSPORT_LIVE_TEST` | `1` |
| `MEITOU_TRANSPORT_LIVE_PACK` | `Skiff`, `Western Hive Skiff`, or `none`, matching the trial. |

Alternatively, the build stages the same verification driver as an optional test mod for running it through the launcher instead of using the startup hook.

The driver checks the real transport system after a simulation tick. It verifies either an empty world or exactly one rendered hull at the authored site while the Wanderer is still far away. It then selects and teleports the character, boards via the normal F10 handler, holds the seated view for 15 seconds, sails clear of the anchorage, disembarks and gives an ordinary movement order. `LIVE VOYAGE PASS` marks completion.

Set `MEITOU_TRANSPORT_LIVE_SAVE=1` as well to save during the crossing. The native game writes a new GUID-named slot inside `live-voyage-saves` under its working directory. The driver loads that slot, compares the hull's world position and heading to its saved pose for 30 ticks, confirms the helm is empty, and boards the same squad member again before finishing the movement check.
