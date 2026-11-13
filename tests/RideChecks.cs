using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Transport;

internal static class RideChecks
{
    private sealed class Hills : IWalkability
    {
        public bool Blocked;
        public float GroundHeight(float x, float z) => 300 + x * .08f + z * .04f;
        public bool IsWalkable(float x, float z) => !Blocked;
        public PathResult FindPath(Vector3 from, Vector3 to) => new([from, to]);
        public Vector3 At(float x, float z) => new(x, GroundHeight(x, z), z);
    }

    internal static List<ulong> Run(int threads)
    {
        var ground = new Hills();
        var catalog = new TransportCatalog();
        var fit = new MountDefinition("test.saddle", "test saddle", "Saddle.mod", "Spine", default,
            Quaternion.Identity, default, 0, "", "");
        catalog.Mounts.Add("garru", fit);
        var transport = new TransportSystem(catalog, null, _ => { });
        var settings = new WorldSettings { Threads = threads, MinPartitionSize = 8, Seed = 814, PublishSnapshots = false };
        using var world = new World(settings, ground, [new PlayerSystem(), new MovementSystem(new PathService(ground, true)), transport]);
        world.Player.Faction = 1;
        CharacterId Spawn(float x, float z, string? record = null, bool owned = true)
        {
            var animal = record is null ? null : new AnimalData { StringId = record };
            return world.Characters.Spawn(new CharacterHot { Position = ground.At(x, z), MaxSpeed = 40, WalkSpeed = 12 },
                new CharacterCold
                {
                    Name = record ?? "rider",
                    IsPlayer = owned,
                    Faction = owned ? 1 : 2,
                    Animal = animal,
                    Race = new RaceData(),
                    Medical = new MedicalState([])
                }, world.Tick);
        }
        var rider = Spawn(0, 0);
        var other = Spawn(0, 13);
        Spawn(0, 6, "garru", false);
        Spawn(-7, 0, "unregistered");
        var hashes = new List<ulong>();
        void Step(int count = 1)
        {
            for (int i = 0; i < count; i++) { world.RunTick(); hashes.Add(world.StateHash()); }
        }
        void Action(TransportAction action, CharacterId who)
        {
            world.Commands.Enqueue(new TransportCommand("", action, who, default) { Tick = world.Tick });
            Step();
        }
        Vector3 Pos(CharacterId id) => world.Characters.Previous[id.Slot].Position;
        Action(TransportAction.Mount, rider);
        Check.That(!transport.IsSeated(rider), "wild and unregistered animals cannot mount a rider");
        var mount = Spawn(6, 3, "garru");
        for (int i = 0; i < 68; i++) Spawn(500 + i * 10, 260, owned: false);
        Action(TransportAction.Mount, rider); Step();
        Check.That(transport.IsSeated(rider) && transport.PassengerSlotPlusOne(mount) == rider.Slot + 1
            && world.Player.Selection.SequenceEqual([mount]), "mounting selects the actual animal");
        Action(TransportAction.Mount, other);
        Check.That(!transport.IsSeated(other) && transport.View.Mounts.Length == 1, "one occupant per saddle");
        var spare = Spawn(14, 21, "garru");
        Action(TransportAction.Mount, other);
        Check.That(transport.PassengerSlotPlusOne(spare) == other.Slot + 1 && transport.View.Mounts.Length == 2,
            "a second rider can mount the next free animal when the closer saddle is occupied");
        Action(TransportAction.Dismount, spare);
        world.Characters.Remove(spare);
        Action(TransportAction.Board, rider);
        Check.That(transport.View.Mounts.Single().Rider == rider && world.TakeMessages().Contains("Already aboard."), "saddle and helm share rider occupancy");
        var start = Pos(mount);
        world.Commands.Enqueue(new MoveOrder([mount], ground.At(85, 16)) { Tick = world.Tick });
        Step(55);
        Check.That(Vector3.Distance(start, Pos(mount)) > 7 && Pos(rider) == Pos(mount), "native animal move carries the rider");
        var turn = Pos(mount);
        world.Commands.Enqueue(new MoveOrder([mount], ground.At(turn.X, turn.Z + 110)) { Tick = world.Tick });
        Step(65);
        Check.That(Pos(mount).Z > turn.Z + 10 && Pos(rider) == Pos(mount), "native redirect on a slope");
        world.Commands.Enqueue(new StopCommand([mount]) { Tick = world.Tick });
        Step(14); // Native movement decelerates after the order is cancelled.
        var halted = Pos(mount);
        Step(5);
        Check.That(transport.IsSeated(rider) && Pos(rider) == halted && Pos(mount) == halted
            && world.Characters.Previous[mount.Slot].Velocity == Vector3.Zero,
            "native Stop halts the mount with the rider still seated");
        world.Commands.Enqueue(new MoveOrder([mount], ground.At(halted.X + 55, halted.Z)) { Tick = world.Tick });
        Step(20);
        Check.That(Vector3.Distance(halted, Pos(mount)) > 3, "native movement resumes after a mounted stop");
        var safe = transport.SavedFooting(rider)!.Value;
        Check.That(Vector3.Distance(Pos(rider), safe) > 7 && Math.Abs(safe.Y - ground.GroundHeight(safe.X, safe.Z)) < .01f, "footing projection stays beside the moving animal");
        using (var payload = new MemoryStream())
        {
            transport.Write(payload);
            Check.That(transport.IsSeated(rider) && payload.Length == 8, "save releases neither the live ride nor a runtime character ID into the vessel payload");
        }
        ground.Blocked = true;
        Action(TransportAction.Dismount, mount);
        Check.That(transport.IsSeated(rider), "blocked voluntary exit retains both occupants and control");
        ground.Blocked = false;
        world.Commands.Enqueue(new MoveOrder([mount], ground.At(Pos(mount).X + 90, Pos(mount).Z)) { Tick = world.Tick });
        Step(15);
        var exitAt = Pos(mount);
        Check.That(world.Characters.Previous[mount.Slot].Velocity.Length() > 5, "dismount fixture must be moving");
        Action(TransportAction.Dismount, mount);
        Check.That(Pos(mount) == exitAt && world.Characters.Previous[mount.Slot].Velocity == Vector3.Zero,
            "a successful dismount stops the moving animal on its transfer tick");
        Step();
        Check.That(!transport.IsSeated(rider) && transport.View.Mounts.Length == 0 && world.Player.Selection.SequenceEqual([rider]), "unmount selects the rider and removes the last visible saddle");
        var stopped = Pos(mount);
        var onFoot = Pos(rider);
        world.Commands.Enqueue(new MoveOrder([rider], ground.At(onFoot.X + 65, onFoot.Z)) { Tick = world.Tick });
        Step(26);
        Check.That(Vector3.Distance(Pos(rider), onFoot) > 3 && Vector3.Distance(Pos(mount), stopped) < 2, "ordinary rider move and stopped animal");
        var fresh = Spawn(Pos(mount).X + 8, Pos(mount).Z);
        Action(TransportAction.Mount, fresh);
        world.Characters.Cold(mount.Slot)!.Medical!.KnockOut(60);
        Step();
        Check.That(!transport.IsSeated(fresh) && transport.View.Mounts.Length == 0, "incapacitated animal releases its rider");
        var replacement = Spawn(Pos(fresh).X + 7, Pos(fresh).Z, "garru");
        Action(TransportAction.Mount, fresh);
        Check.That(transport.PassengerSlotPlusOne(replacement) > 0, "a second animal may be mounted after a forced exit");
        world.Characters.Remove(replacement);
        var reused = Spawn(Pos(fresh).X, Pos(fresh).Z, "garru");
        Step();
        Check.That(reused.Slot == replacement.Slot && reused.Generation != replacement.Generation && !transport.IsSeated(fresh)
            && transport.PassengerSlotPlusOne(reused) == 0, "removal cannot transfer a rider to a reused character slot");
        Action(TransportAction.Mount, fresh);
        world.Characters.Cold(fresh.Slot)!.Medical!.KnockOut(40);
        Step();
        Check.That(!transport.IsSeated(fresh), "incapacitated rider is detached");
        return hashes;
    }
}
