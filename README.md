# Meitou Transport

**[Download the playtest](https://github.com/mattresspad/MeitouTransport/releases/download/v0.3.0-playtest.4/Meitou-Transport-0.3.0-playtest.4-handoff.zip)** · **[Watch the Stoat ride](https://streamable.com/5592xt)**

Ride a squad animal or sail a one-seat vessel in Kenshi with **MeitouClient v0.18.0 for Windows x64**. This optional framework handles seating, movement, rendering and saves. Content packs choose which animals can carry a rider and where boats appear. Enabling the framework alone leaves the transport world empty.

**Public playtest `0.3.0-playtest.4`** includes four installable ZIPs, corresponding source and checksums. Extract the kit, then extract each mod you want directly into `Kenshi/mods/`. [Release notes and individual downloads](https://github.com/mattresspad/MeitouTransport/releases/tag/v0.3.0-playtest.4)

Players using MeitouClient **v0.17.4** can use the [previous playtest](https://github.com/mattresspad/MeitouTransport/releases/tag/v0.3.0-playtest.3).

| Install | In game |
| --- | --- |
| **Meitou Transport** | The dependency for all transport packs. |
| **Garru Saddle** | A selected squad member can ride an owned stock Garru or Pack Beast using the animal's ordinary move orders. |
| **Western Hive Skiff** | One skiff waits off the Western Hive at X −120780, Z −17640 and keeps its position across saves. |
| **Stoat Desert Ride** | Optional new-game start for recording. A dressed Scorchlander starts outside Stoat with a Garru and a Domesticated Bull. Requires Garru Saddle. |

**For the ride in the video**, enable **Meitou Transport → Garru Saddle → Stoat Desert Ride**, then choose **New Game → Stoat Desert Ride**. Select Sable beside the Garru and choose **F10 → Mount nearby animal**. Right-click the dunes to ride.

**For the skiff**, enable **Meitou Transport → Western Hive Skiff**. Select one squad member, open the console with **Insert**, and enter `tp -120780 -17640`. Choose **F10 → Board nearby vessel**, then **Sail to camera target**. Both examples can run together. [Install and playtest guide](PLAYTEST.md)

For playtest feedback, include the game and mod versions, load order and what you saw. [Report a Transport issue](https://github.com/mattresspad/MeitouTransport/issues)

**Pack authors** can give a custom animal a saddle or place a boat using an FCS dependency, JSON and optional art. The worked examples create a separately named `Pack Bull Saddle.mod` and `Skiff.mod` without compiling C#. You may freely reuse the original sample art under CC0. [Pack author guide](AUTHORING.md) · [Licenses](LICENSING.md)

**Contributors** can rebuild the candidate using the released game assemblies and run the one- and four-thread simulation checks. The integration notes document the rendering and cache issues encountered during the playtest work and the engine changes worth discussing. [Build and contribution guide](CONTRIBUTING.md) · [Integration notes for Meitou](docs/upstream-notes.md)
