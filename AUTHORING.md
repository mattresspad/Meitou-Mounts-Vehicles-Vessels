# Make a transport pack

Install **Meitou Transport 0.3.0-playtest.4** for **MeitouClient v0.18.0**. Players enable the framework and the pack you author. Your pack decides which boats are placed and which animal records have a riding fit. These recipes require only FCS, JSON and the art you choose.

The original skiff, saddle, textures, example headers and definitions are dedicated to the public domain under CC0. You may rename, modify and redistribute them in your own packs, including commercial work, without attribution. [License terms](LICENSING.md)

## Create the pack

1. Create `Kenshi/mods/Skiff/`. In the Forgotten Construction Set (FCS), create **Skiff.mod**, select **Meitou Transport.mod** as a dependency, and save. An empty record list is sufficient. For a quick local example, you can instead copy the record-free **Western Hive Skiff.mod** supplied in the example ZIP into this new directory and rename the copy **Skiff.mod**. Its header already declares the framework dependency.
2. Put your Ogre hull mesh in `meshes/` and its diffuse DDS or PNG in `textures/`. Give each asset a distinct filename across your enabled mods. Meitou resolves loose meshes and textures by basename. For a first test you can copy the example artwork and rename the files as shown below.
3. Save the following as `Skiff/transport.json`. These coordinates put the **Saltwind Courier** 100 world units east of the Western Hive example. This boat starts facing east, cruises at 24 world units per second and turns at up to 75 degrees per second.

```json
{
  "schemaVersion": 2,
  "packId": "saltwind",
  "vessels": [{
    "id": "saltwind.courier",
    "label": "Saltwind Courier",
    "hullPath": "meshes/saltwind_courier.mesh",
    "texture": "textures/saltwind_courier.dds",
    "seatOffset": [0, 1, -7],
    "length": 52,
    "width": 18,
    "draft": 2.5,
    "speed": 24,
    "turnDegrees": 75
  }],
  "placements": [{
    "id": "saltwind.east-mooring",
    "label": "Saltwind Courier",
    "vessel": "saltwind.courier",
    "x": -120680,
    "z": -17640,
    "headingDegrees": 90,
    "placeOnLoad": true
  }]
}
```

4. Enable **Meitou Transport** followed by **Skiff** and restart Meitou. For a quick test, select a conscious humanoid, open the console with **Insert** or **Ctrl + backquote**, enter `tp -120680 -17640`, then close the console. The Courier waits at its mooring. Board it using **F10 → Board nearby vessel**. Pan the camera over clear water and use **Sail to camera target**. **Disembark** gives the character ordinary movement again.
5. To distribute the add-on, ZIP the **Skiff/** folder itself, including `Skiff.mod`, `transport.json` and both asset directories. List **Meitou Transport** as the dependency. The framework DLL ships in the framework ZIP.

## Fit a different hull

- Use a stable, unique `packId` and prefix each authored vessel and placement ID with it. Labels are display names. You can change them while retaining the boat's save identity.
- The hull's local origin is at water level on its centerline. **+Y** is up and **+Z** points toward the bow. `seatOffset` locates the rider's animation root relative to that origin. Fit it with a seated character in game. `length` and `width` describe the hull footprint in world units and `draft` is the required water depth.
- Each placement creates at most one boat. Omit `placeOnLoad` or set it to `false` for an F10-only launch site. Schema 1 packs retain manual spawning. A definition-only pack can omit `placements`. A later pack can reference an earlier pack's vessel ID by also declaring that pack as an FCS dependency.
- On load, a saved boat returns to its saved position and heading with the helm free. Temporarily disabling the pack leaves the pose dormant through a resave. Re-enable the same IDs to bring it back. If an automatic launch site has insufficient depth or extends outside the world, Meitou's log identifies the placement.

## Fit a Pack Bull saddle

1. Create `Kenshi/mods/Pack Bull Saddle/Pack Bull Saddle.mod` in FCS and add **Meitou Transport.mod** as its dependency. For a quick trial, copy the record-free `Garru Saddle.mod` from the sample ZIP and rename the copy. Use the FCS **ANIMAL_CHARACTER** string ID of the animal you want to ride. The Wandering Trader starts with **Pack Bull** (`47929-rebirth.mod`). **Domesticated Bull** has a different record and rig.
2. Supply your own mesh and texture. To try this fit using the example art, copy its mesh to `meshes/trader_bull_seat.mesh` and its DDS to `textures/trader_bull_seat.dds` inside your pack. Write this `transport.json` beside the new `.mod`.

```json
{
  "schemaVersion": 3,
  "packId": "trader-bull",
  "mounts": [{
    "id": "trader-bull.travelling-seat",
    "label": "Trader's Bull Saddle",
    "animalRecordIds": ["47929-rebirth.mod"],
    "anchorBone": "Bip01 Spine2",
    "saddleOffset": [0.28, 7.6, 2.2],
    "saddleRotationDegrees": [-5, 0, 0],
    "seatOffset": [0, 1.8, -0.35],
    "seatYawDegrees": 0,
    "saddleMesh": "meshes/trader_bull_seat.mesh",
    "texture": "textures/trader_bull_seat.dds"
  }]
}
```

3. Enable only **Meitou Transport** and **Pack Bull Saddle** in that order. Start **The Wandering Trader**. Select the trader beside the bull, use **F10 → Mount nearby animal**, then right-click open ground. The bull takes the ordinary move order. Use the usual speed controls to check the fit at a walk and a run. **F10 → Dismount** selects the trader again. Save during a ride and reload to check the fit a second time.
4. To distribute the pack, ZIP the **Pack Bull Saddle/** directory and list **Meitou Transport** as its dependency. Tell players the framework and game versions you tested. The FCS file, JSON and art are the complete pack.

Measure `saddleOffset` from the named bone's rest position, in the animal model's axes and units. +Y is up and +Z is forward. `saddleRotationDegrees` is [pitch about X, yaw about Y, roll about Z], using Meitou's yaw/pitch/roll convention. The animated bone carries this fit through every stride. `seatOffset` locates the seated rider's pelvis in saddle-local axes. `seatYawDegrees` turns the rider about the saddle's +Y. Adjust height and facing in game. Growth scales the saddle and the seat placement while the rider keeps their own size.

Omit both art fields if another part of your mod draws the saddle, or omit just `texture` to use the mesh material. In v0.18.0 the stock Pack Beast shows its equipped backpack. Leave room for the bags behind the seat when fitting art. Use `saddleMesh` for artwork you want the framework to draw while ridden. Eligibility comes from owning a healthy matching animal. Your pack can supply custom FCS animal records, saddle items and art.

Schema 3 accepts mount-only packs and mixed packs with `mounts`, `vessels` and `placements`. Within one pack each animal record has one fit. Across packs the last valid fit in enabled mod order wins. The game log names both `.mod` files and fit IDs on an override, or the manifest and missing bone if validation fails.

Each animal or vessel has one seat. The animal owns its movement and gait. The boat accepts a direct destination and tests depth as it advances. Towing, motors, wind, and multi-seat decks are outside this candidate's schema.

## Supply a recording start

**Stoat Desert Ride** shows how a third pack can depend on an existing saddle pack. Its FCS file adds a new-game start, a rider and a squad template. The start fixes X and Z. The template gives the player one Garru and one Domesticated Bull. The rider record chooses Scorchlander, a 100 percent female chance, and four stock clothing records in distinct slots. Existing saves gain access to the bull fit when this pack is enabled. Only selecting the new start creates the recording party.

Open `Stoat Desert Ride.mod` in FCS to edit these records. Its `transport.json` supplies a fit for **Domesticated Bull** (`43949-rebirth.mod`) on the animal's own rig. **Garru Saddle** supplies the Garru fit. The bull reuses the CC0 saddle art under pack-specific filenames. In the corresponding source ZIP, `tools/DesertRide/Program.cs` shows exactly how the three FCS records were built.
