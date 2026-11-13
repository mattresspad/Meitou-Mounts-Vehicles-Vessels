using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Transport;

internal static class VoyageChecks
{
    private sealed class Coast : IWalkability
    {
        public float BankX { get; set; } = float.PositiveInfinity;
        public float TerrainHeight(float x, float z) =>
            x >= BankX ? WorldWater.Height + 2 : z >= 85 && z <= 105 ? WorldWater.Height - 1 : WorldWater.Height - 30;
        public float GroundHeight(float x, float z) => MathF.Max(WorldWater.Height, TerrainHeight(x, z));
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => new([from, to]);
    }

    public static List<ulong> Run(TransportPlacement sample, int threads)
    {
        var placement = sample with { X = 0, Z = 0, HeadingDegrees = 0 };
        // The center of the first hull is deep, its bow is on the bar.
        var shallow = placement with { Id = "a.shallow", Z = 65 };
        var manual = placement with { Id = "a.manual", X = 500, PlaceOnLoad = false };
        var placements = new Dictionary<string, TransportPlacement>
        {
            [shallow.Id] = shallow,
            [manual.Id] = manual,
            [placement.Id] = placement,
        };
        var errors = new List<string>();
        var transport = new TransportSystem(placements, null, errors.Add);
        var coast = new Coast();
        var settings = new WorldSettings { Threads = threads, Seed = 382, PublishSnapshots = false };
        using var world = new World(settings, coast, [transport]);
        world.Player.Faction = 1;

        CharacterId Spawn(Vector3 position, bool player) => world.Characters.Spawn(
            new CharacterHot { Position = position },
            new CharacterCold { IsPlayer = player, Faction = player ? 1 : 2, Race = new RaceData { Swims = true } }, 0);

        var start = new Vector3(0, WorldWater.Height, 0);
        var rider = Spawn(start, true);
        var other = Spawn(start, true);
        var distant = Spawn(start + new Vector3(500, 0, 0), true);
        for (int i = 0; i < 65; i++) Spawn(start + new Vector3(i, 0, -100), false);
        var hashes = new List<ulong>();

        void Step(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                world.RunTick();
                hashes.Add(world.StateHash());
            }
        }
        void Command(TransportAction action, CharacterId actor, Vector3 target = default)
        {
            world.Commands.Enqueue(new TransportCommand(action == TransportAction.Spawn ? placement.Id : "", action, actor, target) { Tick = world.Tick });
            Step();
        }
        VesselView Boat() => transport.View.Vessels.Single();

        Command(TransportAction.Board, rider);
        Check.That(Boat().Current == start && Boat().Rider == rider, "first-tick boarding sees the single waiting vessel without Spawn");
        Command(TransportAction.Spawn, CharacterId.None);
        Check.That(Boat().Rider == rider && Boat().Current == start, "Spawn locates the occupied automatic vessel");
        Command(TransportAction.Board, other);
        Check.That(Boat().Rider == rider, "an occupied seat must keep its rider");
        Command(TransportAction.Sail, other, new Vector3(50, 0, 0));
        Step(6);
        Check.That(Boat().Current == start, "a non-occupant cannot steer the boat");

        Command(TransportAction.Sail, rider, new Vector3(0, 0, 65));
        Step(20);
        var forward = Boat().Current;
        Check.That(forward.Z > 4 && Math.Abs(forward.X) < 0.01f, "the skiff must follow the forward order");
        CheckMovingSave(transport, placement, coast, settings);
        CheckPrototypeSave(placement, coast, settings);
        Command(TransportAction.Sail, rider, new Vector3(70, 0, forward.Z));
        Step(55);
        Check.That(Boat().Current.X > 4 && Boat().Rider == rider, "a fresh sail order must turn the occupied hull");

        Command(TransportAction.Disembark, rider);
        Check.That(Boat().Rider.IsNone && world.Characters.Previous[rider.Slot].Position.Y == WorldWater.Height,
            "disembarking in open water must return the rider to the water surface");
        Command(TransportAction.Board, distant);
        Check.That(Boat().Rider.IsNone, "a distant character cannot board");
        Command(TransportAction.Board, rider);
        Check.That(Boat().Rider == rider, "a swimmer beside the hull must be able to board again");

        Command(TransportAction.Sail, rider, new Vector3(Boat().Current.X, 0, 180));
        Step(220);
        var stopped = Boat().Current;
        Step(20);
        Check.That(stopped == Boat().Current && stopped.Z < 85 && stopped.Z > forward.Z,
            "the bow must stop short of a shallow bar crossing an otherwise deep route");

        using var state = new MemoryStream();
        ((IModSaveData)transport).Write(state);
        var restoreErrors = new List<string>();
        var restored = new TransportSystem(placements, null, restoreErrors.Add);
        using var freshWorld = new World(settings, coast, [restored]);
        state.Position = 0;
        ((IModSaveData)restored).Read(state);
        freshWorld.RunTick();
        var saved = restored.View.Vessels.Single();
        Check.That(saved.Current == stopped && saved.Rider.IsNone && saved.Yaw == Boat().Yaw,
            "reload must restore the skiff pose and discard transient rider links");
        freshWorld.RunTicks(10);
        Check.That(restored.View.Vessels.Single().Current == stopped, "a restored skiff must wait for its next helm order");
        Check.That(restoreErrors.Count == 1 && restoreErrors[0].Contains(shallow.Id), "report the invalid site once in the newly loaded world");

        coast.BankX = stopped.X + 1;
        Command(TransportAction.Disembark, rider);
        var onShore = world.Characters.Previous[rider.Slot].Position;
        Check.That(Boat().Rider.IsNone && onShore.X >= coast.BankX && onShore.Y == coast.TerrainHeight(onShore.X, onShore.Z),
            "a reachable dry bank must be preferred to swimming");
        Check.That(errors.Count == 1 && errors[0].Contains(shallow.Id), "reject the invalid hull footprint once and still seed other sites");
        coast.BankX = float.PositiveInfinity;
        world.Commands.Enqueue(new TransportCommand(manual.Id, TransportAction.Spawn, CharacterId.None, default) { Tick = world.Tick });
        Step();
        Check.That(transport.View.Vessels.Length == 2 && transport.View.Vessels.Any(v => v.Current.X == manual.X),
            "a manual site remains available through F10");
        return hashes;
    }

    private static void CheckPrototypeSave(TransportPlacement sample, Coast coast, WorldSettings settings)
    {
        // Fixed 0.1.0 payload. A change to today's writer must not also change the test input.
        using var sidecar = new MemoryStream();
        var oldPosition = new Vector3(-120769.17f, WorldWater.Height, -17618.348f);
        const float oldYaw = 0.4636476f;
        using (var writer = new BinaryWriter(sidecar, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write("western-hive.anchorage");
            writer.Write("western-hive.skiff");
            writer.Write(oldPosition.X);
            writer.Write(oldPosition.Z);
            writer.Write(oldYaw);
        }
        var added = sample with { Id = "western-hive.new-mooring", X = -80 };
        var errors = new List<string>();
        var system = new TransportSystem(new Dictionary<string, TransportPlacement>
        {
            [sample.Id] = sample with { Z = 90 }, // Even an edited, shallow anchorage cannot displace the saved boat.
            [added.Id] = added,
        }, null, errors.Add);
        sidecar.Position = 0;
        system.Read(sidecar);
        using var world = new World(settings, coast, [system]);
        world.RunTicks(3);
        var boats = system.View.Vessels;
        Check.That(errors.Count == 0 && boats.Length == 2 && boats.Any(b => b.Current == oldPosition && b.Yaw == oldYaw)
            && boats.Any(b => b.Current == new Vector3(added.X, WorldWater.Height, added.Z)),
            "restore the prototype save and seed just the newly authored site");
    }

    private static void CheckMovingSave(TransportSystem transport, TransportPlacement placement, Coast coast, WorldSettings settings)
    {
        var expected = transport.View.Vessels.Single();
        using var save = new MemoryStream();
        transport.Write(save);
        // A disabled sample remains absent through a resave, then returns at its saved pose.
        var absent = new TransportSystem(new Dictionary<string, TransportPlacement>());
        save.Position = 0;
        absent.Read(save);
        using (var disabledWorld = new World(settings, coast, [absent])) disabledWorld.RunTicks(3);
        Check.That(absent.View.Vessels.Length == 0, "disabled packs must not draw");
        using var carriedSave = new MemoryStream();
        absent.Write(carriedSave);
        var restored = new TransportSystem(new Dictionary<string, TransportPlacement> { [placement.Id] = placement });
        carriedSave.Position = 0;
        restored.Read(carriedSave);
        using var world = new World(settings, coast, [restored]);
        var actor = world.Characters.Spawn(new CharacterHot { Position = expected.Current with { Y = WorldWater.Height - 30 } },
            new CharacterCold { IsPlayer = true, Race = new RaceData { Swims = true } }, 0);
        world.RunTicks(40);
        var actual = restored.View.Vessels.Single();
        Check.That(actual.Current == expected.Current && actual.Yaw == expected.Yaw && actual.Rider.IsNone,
            "a moving boat restores stopped with its helm free");
        world.Commands.Enqueue(new TransportCommand(placement.Id, TransportAction.Spawn, CharacterId.None, default) { Tick = world.Tick });
        world.RunTick();
        Check.That(restored.View.Vessels.Single().Current == expected.Current, "locating the restored craft must leave its anchorage empty");
        world.Commands.Enqueue(new TransportCommand("", TransportAction.Board, actor, default) { Tick = world.Tick });
        world.RunTick();
        Check.That(restored.View.Vessels.Single().Rider == actor, "the reloaded swimmer can board from inside the hull's water column");
    }
}
