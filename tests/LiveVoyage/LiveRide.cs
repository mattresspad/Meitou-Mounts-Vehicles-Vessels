using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using HarmonyLib;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Objects;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Transport.Verification;

// An external observation/input driver. The consumer trial uses the unmodified stock trader start.
internal static class LiveRide
{
    private const string ProbeId = "meitou.transport.live-ride";
    private enum Stage { Start, Approach, Mount, Walk, Run, Turn, FitPhoto, Saving, Loading, Remount, Exit, Foot, ApproachBull, MountBull, RideBull, LeaveBull, Done }
    private static Stage stage;
    private static string record = "", pack = "", mesh = "", bone = "";
    private static Vector3 saddleOffset, seatOffset, start, corner;
    private static Quaternion saddleRotation;
    private static float seatYaw;
    private static long since = Stopwatch.GetTimestamp(), status = since;
    private static World? originalWorld;
    private static SaveCheck? save;
    private static uint animalSerial;
    private static string? backpack;
    private static string[] cargo = [];
    private static float initialYaw, low, high;
    private static readonly HashSet<string> gaits = [];
    private static int frames;
    private static bool flatFit;
    private static bool desert;
    private static long? restoredTick;
    private static readonly int StageTimeoutSeconds =
        Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_TEST_CPU_VULKAN") == "1" ? 480 : 150;
    private static Vector3 restoredAnimal;
    private static Vector3 dismountedAnimal;
    private static long? dismountedTick;
    private sealed record Course(Vector3 Site, Vector3 First, Vector3 Second);
    private static Course? course;

    internal static void Arm()
    {
        string? trial = Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_RIDE");
        if (trial is not ("fit" or "garru" or "bare" or "bags" or "bull" or "mixed" or "stoat")) return;
        var args = Environment.GetCommandLineArgs();
        int startOption = Array.IndexOf(args, "--new-game");
        flatFit = trial == "fit";
        desert = trial == "stoat";
        if (flatFit ? !args.Contains("--sandbox") : startOption < 0 || startOption + 1 >= args.Length
            || args[startOption + 1] != (desert ? DesertRide.Start : "The Wandering Trader")) return;
        if (Harmony.HasAnyPatches(ProbeId)) return;
        record = trial switch { "bull" => "47929-rebirth.mod", "bare" or "fit" or "stoat" => DesertRide.Garru, _ => "3999-gamedata.base" };
        pack = trial == "bull" ? "Pack Bull Saddle" : "Garru Saddle";
        var install = GameInstall.Locate()!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(ModList.FindMods(install)[pack])!, "transport.json")));
        var fit = doc.RootElement.GetProperty("mounts").EnumerateArray()
            .Single(f => f.GetProperty("animalRecordIds").EnumerateArray().Any(id => id.GetString() == record));
        Vector3 Vec(string field) { var v = fit.GetProperty(field); return new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()); }
        saddleOffset = Vec("saddleOffset");
        seatOffset = Vec("seatOffset");
        var rot = Vec("saddleRotationDegrees") * (MathF.PI / 180);
        saddleRotation = Quaternion.CreateFromYawPitchRoll(rot.Y, rot.X, rot.Z);
        seatYaw = fit.GetProperty("seatYawDegrees").GetSingle() * (MathF.PI / 180);
        bone = fit.GetProperty("anchorBone").GetString()!;
        mesh = Path.GetFileName(fit.GetProperty("saddleMesh").GetString()) + DebrisPiece.TextureMark + Path.GetFileName(fit.GetProperty("texture").GetString());
        var h = new Harmony(ProbeId);
        if (flatFit)
            h.Patch(AccessTools.Method(typeof(PopulationSystem), nameof(PopulationSystem.SpawnSandbox)), prefix: Hook(nameof(PlaceOwnedAnimal)));
        else if (!desert && trial != "bull")
            h.Patch(AccessTools.Method(typeof(SquadTemplate), "From"), postfix: Hook(nameof(StockAnimalFixture)));
        if (trial == "bags")
            h.Patch(AccessTools.Method(typeof(CharacterAppearance), nameof(CharacterAppearance.BuildAnimal)), postfix: Hook(nameof(LuggageFitFixture)));
        var renderer = typeof(IMod).Assembly.GetType("Meitou.Game.Rendering.Characters.CharacterRenderer", true)!;
        var observe = Hook(nameof(Frame));
        observe.after = ["meitou.transport"];
        h.Patch(AccessTools.Method(renderer, "CollectBolts"), postfix: observe);
        h.Patch(AccessTools.Method(typeof(World), "RunSlowWorld"), postfix: Hook(nameof(Survey)));
        Log($"using {pack} for {record}");
    }

    private static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(LiveRide), name));
    private static void Survey(World __instance)
    {
        if (Volatile.Read(ref course) is not null || __instance.Tick % 30 != 0) return;
        var table = __instance.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Cold(i) is not { IsPlayer: true, Animal: { } species } || species.StringId != record) continue;
            var origin = table.Next[i].Position;
            var walk = __instance.Walkability;
            if (flatFit)
            {
                Volatile.Write(ref course, new(new Vector3(0, walk.GroundHeight(0, 0), 0), Vector3.UnitX, Vector3.UnitZ));
                return;
            }
            bool Ground(Vector3 at, out Vector3 stood)
            {
                float near = walk.TerrainHeight(at.X, at.Z);
                bool ok = walk.TryStand(at.X, at.Z, near, out float y);
                stood = at with { Y = y };
                return ok && Math.Abs(y - near) < 4 && y > 104;
            }
            foreach (float radius in desert ? new[] { 0f } : new[] { 90f, 180, 280, 410 })
                for (int side = 0; side < 8; side++)
                {
                    var dir = new Vector3(MathF.Cos(side * MathF.PI / 4), 0, MathF.Sin(side * MathF.PI / 4));
                    var turn = new Vector3(-dir.Z, 0, dir.X);
                    if (!Ground(origin + dir * radius, out var p) || !Ground(p + dir * 120, out var q)
                        || !Ground(p + dir * 65 + turn * 100, out var t) || !Ground(p + dir * 65, out var corner)
                        || !desert && Math.Max(Math.Abs(q.Y - p.Y), Math.Abs(t.Y - p.Y)) < 2
                        || !Ground(p + dir * 32, out _) || !Ground(corner + turn * 40, out _)) continue;
                    Volatile.Write(ref course, new(p, dir, turn));
                    Log($"survey at {p}, first {dir} towards {q}, turn towards {t}"); return;
                }
            return;
        }
    }
    // The native animation scene constructs its two characters with the same record. Supply the adult animal
    // for the second spawn and make it player-owned before the native squad and inventory are constructed.
    private static void PlaceOwnedAnimal(ref string recordId, ref bool player)
    {
        if (player || recordId != "1995-gamedata.base") return;
        recordId = record;
        player = true;
    }
    private static void StockAnimalFixture(ref SquadTemplate __result)
    {
        if (__result.Id != "47928-rebirth.mod") return;
        bool adult = record == "42201-rebirth.mod";
        __result = __result with
        {
            Animals = [.. __result.Animals.Select(a => a with { Id = record, V2 = adult ? 100 : a.V2 })],
            AnimalAgeRandom = adult ? 0 : __result.AnimalAgeRandom
        };
    }

    private static IEnumerable<string> InventoryEntries(Inventory? inventory, string parent = "")
    {
        if (inventory is null) yield break;
        foreach (var item in inventory.Items)
        {
            string key = $"{parent}/{item.Section}({item.X},{item.Y})/{item.Record}";
            yield return $"{key} x{item.Quantity}";
            foreach (string nested in InventoryEntries(item.Contents, key)) yield return nested;
        }
    }

    // v0.17.4 equips the stock luggage and draws its light. Show its original skinned mesh in this one QA profile.
    private static void LuggageFitFixture(GameDatabase db, GameRecord animal, CharacterAppearance __result)
    {
        if (animal.StringId != record || db.Find("4002-gamedata.base") is not { } bag) return;
        var path = bag.GetPath("mesh");
        if (path.Length == 0) throw new InvalidDataException("Pack Beast luggage mesh missing.");
        if (__result.Parts.All(p => p.Mesh != path))
            __result.Parts.Add(new CharacterPart { Record = bag, Mesh = path, Field = "mesh", Mode = AttachMode.SharedSkeleton });
    }
    private static object? Field(object o, string n) => AccessTools.Field(o.GetType(), n).GetValue(o);
    private static void Go(Stage next) { stage = next; since = Stopwatch.GetTimestamp(); }
    private static void Log(string text) => Console.WriteLine("LIVE RIDE " + text);
    private static void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }

    private static void Frame(object __instance, List<DebrisPiece> into)
    {
        if (stage == Stage.Done || AccessTools.Property(__instance.GetType(), "Source").GetValue(__instance) is not Delegate { Target: { } host }
            || host.GetType().Name != "GameHost" || Field(host, "Session") is not WorldSession session) return;
        try
        {
            var world = session.World;
            if (stage == Stage.Loading && ReferenceEquals(world, originalWorld)) return;
            session.Simulation.SetSpeed(1);
            var snap = session.CurrentSnapshot;
            var animal = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.RecordId == record);
            var rider = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Appearance is { Animal: null });
            var bull = desert ? snap.Characters.FirstOrDefault(c => c.IsPlayer && c.RecordId == DesertRide.Bull) : null;
            if (Stopwatch.GetElapsedTime(since).TotalSeconds > StageTimeoutSeconds) throw new TimeoutException($"{stage}, animal={animal?.Position}, rider={rider?.Position}");
            if (rider is null || animal is null) return;
            if (Volatile.Read(ref course) is not { } route) return;
            var saddle = into.SingleOrDefault(p => p.Mesh == mesh);
            bool drawn = saddle.Mesh is not null;
            float distance = Vector3.Distance(rider.Position, animal.Position);
            if (Stopwatch.GetElapsedTime(status).TotalSeconds > 8)
            {
                Log($"{stage} tick={snap.Tick} animal={animal.Position} selected={animal.Selected} rider={rider.Position} distance={distance:F1} saddle={drawn} gait={string.Join(',', animal.Animations.Select(l => l.Name))}");
                status = Stopwatch.GetTimestamp();
            }
            void Move(CharacterSnapshot who, Vector3 to) => world.Commands.Enqueue(new MoveOrder([who.Id], to) { Tick = world.Tick });
            void Camera() => session.Camera.Place(new(animal.Position.X, animal.Position.Z), 1.1f, .26f, 98);
            void Shot(string name)
            {
                var channel = Field(host, "AgentChannel")!;
                AccessTools.Field(channel.GetType(), "ShotPath").SetValue(channel, Path.GetFullPath(name + ".png"));
                AccessTools.Field(host.GetType(), "ScreenshotRequested").SetValue(host, true);
            }
            var inventory = world.Characters.Cold(animal.Id.Slot)?.Inventory;
            string? bag = inventory?.Backpack?.Record;
            bool SameInventory() => bag == backpack && cargo.SequenceEqual(InventoryEntries(inventory).Order(StringComparer.Ordinal));
            if (drawn && stage is >= Stage.Mount and <= Stage.Saving)
            {
                CheckFrame(__instance, host, animal, rider, saddle);
                foreach (var layer in animal.Animations) gaits.Add(layer.Name);
                low = Math.Min(low, animal.Position.Y);
                high = Math.Max(high, animal.Position.Y);
                frames++;
            }
            switch (stage)
            {
                case Stage.Start:
                    originalWorld = world;
                    animalSerial = animal.PortraitSerial;
                    backpack = bag;
                    cargo = [.. InventoryEntries(inventory).Order(StringComparer.Ordinal)];
                    string? expectedBag = record switch { "3999-gamedata.base" => "4002-gamedata.base", "47929-rebirth.mod" => "43948-rebirth.mod", _ => null };
                    Check(backpack == expectedBag, $"missing stock inventory, expected {expectedBag}, got {backpack}");
                    Log($"start {rider.Name} and {animal.Name}, backpack={backpack}, inventory entries={cargo.Length}, scale={animal.Scale}, position={animal.Position}");
                    var site = route.Site;
                    if (desert) DesertRide.CheckStart(world, snap, rider);
                    else world.Commands.Enqueue(new DebugTeleport([animal.Id, rider.Id], site) { Tick = world.Tick });
                    world.Commands.Enqueue(new SelectCommand([rider.Id]) { Tick = world.Tick });
                    session.Camera.Place(new(site.X, site.Z), 1.1f, .26f, 98);
                    Go(Stage.Approach);
                    break;
                case Stage.Approach:
                    if (!rider.Selected || distance > 19 || Vector3.Distance(animal.Position, route.Site) > 30 || snap.Tick < 15) return;
                    Check(!drawn, "saddle appeared before mounting");
                    LiveVoyage.Command(host, session, "Mount nearby animal"); Go(Stage.Mount); break;
                case Stage.Mount:
                    if (!drawn || !animal.Selected) return;
                    start = animal.Position;
                    low = high = start.Y;
                    world.Commands.Enqueue(new RunSpeedCommand([animal.Id], -animal.RunSpeed) { Tick = world.Tick });
                    Move(animal, start + route.First * 115); Camera(); Go(Stage.Walk); break;
                case Stage.Walk:
                    Camera();
                    if (Vector3.Distance(animal.Position, start) < 23 || animal.RunSpeed != 0) return;
                    Shot("walk"); Log($"walk at {animal.Position}, {string.Join(',', animal.Animations.Select(a => a.Name))}");
                    world.Commands.Enqueue(new RunSpeedCommand([animal.Id], 2) { Tick = world.Tick }); Go(Stage.Run); break;
                case Stage.Run:
                    Camera();
                    if (Vector3.Distance(animal.Position, start) < 65 || animal.RunSpeed != 2) return;
                    corner = animal.Position; initialYaw = animal.Yaw;
                    Move(animal, corner + route.Second * 125); Go(Stage.Turn); break;
                case Stage.Turn:
                    Camera();
                    if (frames < 10 || Vector3.Distance(corner, animal.Position) < 42 || Math.Abs(MathF.IEEERemainder(animal.Yaw - initialYaw, MathF.Tau)) < .65f) return;
                    Check(drawn && animal.Selected && distance < .1f && (flatFit || desert || high - low > .6f), $"ride/turn/slope evidence, frames={frames}, relief={high - low}");
                    Shot("turn");
                    if (flatFit)
                    {
                        Log($"adult fit verified across {frames} posed frames, scale={animal.Scale}, clips={string.Join(',', gaits)}");
                        Go(Stage.FitPhoto);
                        break;
                    }
                    save = new SaveCheck(host, rider);
                    Log($"verified {frames} posed frames; climb={high - low:F1}, clips={string.Join(',', gaits)}, saving {save.Directory}");
                    Go(Stage.Saving); break;
                case Stage.FitPhoto:
                    if (!File.Exists("turn.png")) return;
                    LiveVoyage.Command(host, session, "Dismount");
                    Go(Stage.Remount);
                    break;
                case Stage.Saving:
                    Check(drawn && animal.Selected, "save interrupted the live ride");
                    if (save!.BeginLoadWhenWritten()) Go(Stage.Loading);
                    break;
                case Stage.Loading:
                    if (snap.Tick < 2) return;
                    if (restoredTick is null) { restoredTick = snap.Tick; restoredAnimal = animal.Position; }
                    if (snap.Tick - restoredTick < 30) return;
                    Check(!drawn && distance is > 2 and < 24 && save!.IsOriginalRider(rider) && animal.PortraitSerial == animalSerial
                        && SameInventory() && Vector3.Distance(restoredAnimal, animal.Position) < 3,
                        $"restored native pair, route or inventory differs; distance {distance}, backpack {bag}, travel {Vector3.Distance(restoredAnimal, animal.Position)}");
                    Log($"reload on foot beside original animal, distance={distance:F1}, backpack={bag}, inventory entries={cargo.Length}");
                    if (desert) DesertRide.CheckRider(world, rider);
                    world.Commands.Enqueue(new SelectCommand([rider.Id]) { Tick = world.Tick });
                    Go(Stage.Remount); break;
                case Stage.Remount:
                    if (!rider.Selected) return;
                    LiveVoyage.Command(host, session, "Mount nearby animal"); Go(Stage.Exit); break;
                case Stage.Exit:
                    if (!animal.Selected || !drawn) return;
                    LiveVoyage.Command(host, session, "Dismount"); Go(Stage.Foot); break;
                case Stage.Foot:
                    if (!rider.Selected || drawn) return;
                    if (dismountedTick is null)
                    {
                        dismountedAnimal = animal.Position;
                        dismountedTick = snap.Tick;
                        corner = rider.Position;
                        var away = Vector3.Normalize(new Vector3(corner.X - animal.Position.X, 0, corner.Z - animal.Position.Z));
                        Move(rider, corner + away * 35);
                        return;
                    }
                    if (snap.Tick - dismountedTick < 15 || Vector3.Distance(corner, rider.Position) < 5) return;
                    Check(Vector3.Distance(dismountedAnimal, animal.Position) < 1, "dismounted animal continued moving");
                    Check(SameInventory(), "animal inventory changed on dismount");
                    Log($"mount stopped at {animal.Position}; rider walked {Vector3.Distance(corner, rider.Position):F1} units");
                    if (desert)
                    {
                        if (bull is null) throw new InvalidOperationException("Domesticated Bull missing after reload.");
                        Move(rider, bull.Position);
                        Go(Stage.ApproachBull);
                        break;
                    }
                    Log(flatFit ? "PASS adult two-actor fit, native movement, remount, dismount and ordinary rider order"
                        : "PASS native movement, save, remount, dismount and ordinary rider order");
                    Go(Stage.Done);
                    break;
                case Stage.ApproachBull:
                    if (bull is null || Vector3.Distance(rider.Position, bull.Position) > 14) return;
                    LiveVoyage.Command(host, session, "Mount nearby animal"); Go(Stage.MountBull); break;
                case Stage.MountBull:
                    if (bull is null || !bull.Selected || Vector3.Distance(rider.Position, bull.Position) > .1f) return;
                    start = bull.Position;
                    Move(bull, start + route.First * 65);
                    Go(Stage.RideBull); break;
                case Stage.RideBull:
                    if (bull is null || Vector3.Distance(start, bull.Position) < 24 || Vector3.Distance(rider.Position, bull.Position) > .1f) return;
                    session.Camera.Place(new(bull.Position.X, bull.Position.Z), 1.1f, .26f, 98);
                    if (!into.Any(p => p.Mesh?.StartsWith("stoat_bull_saddle.mesh", StringComparison.Ordinal) == true)) return;
                    Shot("bull");
                    LiveVoyage.Command(host, session, "Dismount"); Go(Stage.LeaveBull); break;
                case Stage.LeaveBull:
                    if (!rider.Selected || !File.Exists("bull.png")) return;
                    Log("PASS Stoat outfit, authored start, Garru ride and turn, save, reload, remount, and Domesticated Bull ride");
                    Go(Stage.Done); break;
            }
        }
        catch (Exception e) { Log("FAIL " + e); Go(Stage.Done); }
    }

    // Compare the actual character-renderer palette and the loose-mesh submission, independently of framework state.
    private static void CheckFrame(object renderer, object host, CharacterSnapshot animal, CharacterSnapshot rider, DebrisPiece piece)
    {
        long Key(CharacterId id) => (long)AccessTools.Method(host.GetType(), "DrawKey").Invoke(host, [id])!;
        long a = Key(animal.Id), r = Key(rider.Id);
        var live = (Array)Field(renderer, "_live")!;
        object? carrier = null, passenger = null;
        for (int i = 0; i < (int)Field(renderer, "_liveCount")! && (carrier is null || passenger is null); i++)
        {
            var entry = live.GetValue(i)!;
            if ((bool)Field(entry, "PortraitOnly")!) continue;
            var key = (long)Field(entry, "Key")!;
            if (key == a) carrier = entry;
            if (key == r) passenger = entry;
        }
        if (carrier is null || passenger is null) throw new InvalidOperationException("missing rendered character");
        static Matrix4x4 Anchor(object live, string name)
        {
            var rig = (SkeletonRig)Field(Field(live, "Asset")!, "Rig")!;
            int h = rig.Handle(name)!.Value;
            var skin = ((Matrix4x4[])Field(Field(live, "State")!, "Bones")!)[h];
            return Matrix4x4.CreateTranslation(rig.BindPosition[h]) * skin * (Matrix4x4)Field(live, "World")!;
        }
        var expected = Matrix4x4.CreateFromQuaternion(saddleRotation) * Matrix4x4.CreateTranslation(saddleOffset) * Anchor(carrier, bone);
        var centre = ((DebrisRenderer)Field(Field(host, "Gpu")!, "Debris")!).Centre(piece.Mesh)!.Value;
        var origin = piece.Position - Vector3.Transform(centre * piece.Scale, piece.Rotation);
        Check(Matrix4x4.Decompose(expected, out var scale, out var rot, out var point) && Vector3.Distance(point, origin) < .08f
            && MathF.Abs(Quaternion.Dot(rot, piece.Rotation)) > .999f && Math.Abs(scale.X - piece.Scale) < .005f, "saddle differs from the current back bone");
        var body = (Matrix4x4)Field(passenger, "World")!;
        var facing = Vector3.Transform(Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.CreateRotationY(seatYaw)), rot);
        Check(Vector3.Distance(Anchor(passenger, "Bip01 Pelvis").Translation, Vector3.Transform(seatOffset, expected)) < .08f
            && Math.Abs(Vector3.TransformNormal(Vector3.UnitX, body).Length() - rider.Scale) < .01f
            && Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, body)), facing) > .999f,
            "rider pelvis, facing or proportions differ from the current saddle");
    }
}
