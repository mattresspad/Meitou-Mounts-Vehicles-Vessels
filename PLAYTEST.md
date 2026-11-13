# Meitou Transport playtest

**Public playtest `0.3.0-playtest.4`** targets the **Windows x64 MeitouClient game v0.18.0**, API 2, with an installed copy of Kenshi. Check the **game** version in the launcher. Other client versions may change the renderer and save interfaces used by this build. The ZIPs are ready to install. Players do not need the .NET SDK, Python or the development checkout. [MeitouClient v0.18.0](https://github.com/VlcekM/MeitouClient/releases/tag/v0.18.0)

## Install

1. Unzip the **handoff** into a temporary folder. Keep its source ZIP and `SHA256SUMS.txt` there. To check a file in PowerShell, run `Get-FileHash -Algorithm SHA256 "filename.zip"` and compare the result with its line in the checksum list.
2. Extract the **Meitou Transport** ZIP and whichever example ZIPs you want directly into your Kenshi installation's `mods` directory. A correctly placed file is `Kenshi/mods/Meitou Transport/Meitou Transport.mod`. To upgrade an earlier prototype, close the client and replace each corresponding mod folder. Keep a copy of the save you plan to use.
3. For the recording start, enable **Meitou Transport → Garru Saddle → Stoat Desert Ride** in the launcher. For the boat, enable **Meitou Transport → Western Hive Skiff**. To play both together, use **Meitou Transport → Garru Saddle → Stoat Desert Ride → Western Hive Skiff**. Launch the v0.18.0 game.

## Ride a Garru

For the recording start, install and enable **Meitou Transport → Garru Saddle → Stoat Desert Ride** in that order. Choose **New Game → Stoat Desert Ride** and accept the rider's appearance. You start at **X 34595, Z −70128** outside Stoat with Wooden Sandals, Noble's Robe, Noble's Trousers and Square Goggles already equipped. Select just the Scorchlander and mount the nearby Garru. To switch, dismount, walk beside the Domesticated Bull and use the same mount action. The nearest eligible animal gets the rider. Move the animals apart if needed. **F7** hides the HUD for recording and restores it when pressed again.

1. Bring one conscious humanoid squad member beside an owned Garru or Pack Beast. Select the humanoid. In the **F10** menu, find the **Mods** actions and choose **Mount nearby animal**. Selection moves to the nearest eligible animal with a free seat. The saddle appears while ridden.
2. Right-click walkable ground. Try the animal's usual speed and stop controls, a turn, and a slope. The seated rider and saddle should follow the animal's back. Its backpack and inventory stay equipped. On v0.18.0 the stock Pack Beast carries its visible bags behind the saddle.
3. Choose **F10 → Dismount** on clear ground. The animal stops. The rider should be selected and able to walk immediately. If neither side offers an exit, ride to clearer ground and try again.
4. Save while mounted, continue riding, then reload that slot. Look for the original pair on foot and remount. The Garru example fits two animal records. For a new-game test with the Wandering Trader's **Pack Bull**, use the separate recipe in the author guide. [Fit a Pack Bull saddle](AUTHORING.md#fit-a-pack-bull-saddle)

## Sail the Western Hive skiff

1. Select a conscious humanoid in your squad. To reach the mooring quickly, open the console with **Insert**, enter `tp -120780 -17640` and press **Esc**. The skiff waits offshore on the first simulation tick. If it has sailed elsewhere in this save, **F10 → Spawn Western Hive Skiff** reports its current location.
2. Close to the hull, choose **F10 → Board nearby vessel**. Pan the camera until its center is over open water, then choose **F10 → Sail to camera target**. Choose another target to redirect. The boat checks depth across its hull and stops in shallows. Steer around solid obstacles yourself.
3. Save away from the anchorage and reload. The same boat should be stopped at its saved position and heading with an empty helm. Reboard and choose **Disembark**. The rider can take an ordinary order on a reachable bank or swim offshore.

Ordinary saves preserve vessel placement IDs, including while a pack is temporarily disabled. A Meitou save import starts mod data fresh. To end the trial, dismount or disembark, close the client and disable the examples and framework. Your pre-trial save copy remains available.

## Send feedback

Open an issue on the Transport repository with the playtest and game versions, enabled packs in load order, whether you used a new or existing save, and the shortest steps that reproduce what you saw. Include what you expected and what happened. For a saddle issue, the animal type, age, equipment and a side-view screenshot during a turn help distinguish the fit from the motion. For a boat, include the location and whether you had already sailed or reloaded. Relevant log lines are enough. You can redact local paths. [Report a Transport issue](https://github.com/mattresspad/MeitouTransport/issues)
