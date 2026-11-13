using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;

namespace Meitou.Transport;

internal sealed class Vessel(TransportPlacement placement, Vector2 position, float yaw)
{
    internal readonly TransportPlacement Placement = placement;
    internal Vector2 Position = position, Previous = position;
    internal float Yaw = yaw, PreviousYaw = yaw;
    internal CharacterId Rider = CharacterId.None;
    internal Vector2? Target;
}

public sealed partial class TransportSystem(IReadOnlyDictionary<string, TransportPlacement> placements)
    : ITickSystem, IStateHashed, IModSaveData
{
    private sealed record SavedPose(string Craft, Vector2 Position, float Yaw);

    private readonly SortedDictionary<string, Vessel> boats = new(StringComparer.Ordinal);
    // A disabled data pack can return later, even after another save.
    private readonly SortedDictionary<string, SavedPose> dormant = new(StringComparer.Ordinal);
    private readonly Dictionary<CharacterId, Vessel> helms = [];
    private readonly GameInstall? install;
    private readonly Action<string> reportError = Console.Error.WriteLine;
    private bool awaitingPlacements = true;
    private TransportView view = new(0, [], []);
    internal TransportView View => Volatile.Read(ref view);

    internal TransportSystem(IReadOnlyDictionary<string, TransportPlacement> placements, GameInstall? install, Action<string> reportError)
        : this(placements)
    {
        this.install = install;
        this.reportError = reportError;
    }

    internal TransportSystem(TransportCatalog catalog, GameInstall? install, Action<string> reportError)
        : this(catalog.Placements, install, reportError) => fits = catalog.Mounts;

    // Membership is changed only during serial phases of this world.
    public bool IsSeated(CharacterId id) => helms.ContainsKey(id) || riders.ContainsKey(id);

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        if (awaitingPlacements) PlaceOnLoad(world);
        foreach (var command in commands)
        {
            if (command is not TransportCommand c) continue;
            if (c.Action == TransportAction.Spawn) { Spawn(world, c.PlacementId); continue; }
            if (!world.Characters.TryResolveNext(c.Character, out int slot)
                || world.Characters.Cold(slot)?.IsPlayer != true) continue;

            if (c.Action == TransportAction.Mount) { Mount(world, c.Character, slot); continue; }
            if (c.Action == TransportAction.Dismount) { Dismount(world, c.Character); continue; }
            if (c.Action == TransportAction.Board) { Board(world, c.Character, slot); continue; }
            if (!helms.TryGetValue(c.Character, out var boat))
            {
                world.Post("That squad member is not at a helm.");
                continue;
            }
            if (c.Action == TransportAction.Disembark)
            {
                boat.Target = null;
                if (Navigation.Exit(world.Walkability, boat) is { } exit)
                {
                    Release(boat);
                    Stand(world, slot, exit);
                    world.Post($"Left {boat.Placement.Label}.");
                }
                else world.Post("There is no clear place to disembark here.");
            }
            else if (c.Action == TransportAction.Sail)
            {
                var target = new Vector2(c.Target.X, c.Target.Z);
                if (!Navigation.InWorld(target)) { world.Post("Destination is outside the world."); continue; }
                boat.Target = target;
                world.Post($"Sailing to X {target.X:F0}, Z {target.Y:F0}.");
            }
        }
    }

    // Meitou restores the sidecar before this world's first Inputs phase. A site whose
    // saved craft is temporarily missing also owns its ID until its pack returns.
    private void PlaceOnLoad(World world)
    {
        awaitingPlacements = false;
        var missing = placements.Values
            .Where(p => p.PlaceOnLoad && !boats.ContainsKey(p.Id) && !dormant.ContainsKey(p.Id))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;

        try
        {
            // The local navigation tiles at a distant anchorage may not have streamed yet.
            // The installed map reads just the samples under each hull. Synthetic worlds
            // without an install supply their own terrain through IWalkability.
            using var map = install is null ? null : TerrainHeightmap.Open(install);
            IWalkability terrain = map is null ? world.Walkability : new OpenGroundWalkability((x, z) => map.HeightAt(x, z));
            foreach (var p in missing)
                if (Launch(p, terrain) is null)
                    reportError($"Skipped automatic placement '{p.Id}' at X {p.X}, Z {p.Z}: the hull is outside the world or needs deeper water.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            reportError($"Could not read the world heightmap for automatic vessel placement: {ex.Message}");
        }
    }

    private Vessel? Launch(TransportPlacement p, IWalkability terrain)
    {
        var at = new Vector2(p.X, p.Z);
        float yaw = MathF.IEEERemainder(p.HeadingDegrees, 360) * (MathF.PI / 180);
        if (!Navigation.Afloat(terrain, p.Definition, at, yaw)) return null;
        var boat = new Vessel(p, at, yaw);
        boats.Add(p.Id, boat);
        return boat;
    }

    private void Spawn(World world, string id)
    {
        if (!placements.TryGetValue(id, out var p)) return;
        if (!boats.TryGetValue(id, out var boat))
        {
            boat = Launch(p, world.Walkability);
            if (boat is null)
            {
                world.Post($"{p.Label} needs deeper water at its launch point.");
                return;
            }
            dormant.Remove(id);
        }
        world.Post($"{p.Label} at X {boat.Position.X:F0}, Z {boat.Position.Y:F0}.");
    }

    private void Board(World world, CharacterId rider, int slot)
    {
        if (IsSeated(rider)) { world.Post("Already aboard."); return; }
        var cold = world.Characters.Cold(slot)!;
        if (!Available(cold) || cold.Animal is not null)
        {
            world.Post("That squad member cannot board now.");
            return;
        }
        var root = world.Characters.Next[slot].Position;
        var here = new Vector2(root.X, root.Z);
        // Meitou's coordinate teleport may stand a swimmer on the seabed before the
        // offshore navigation tile streams in. They can still reach the hull above.
        float bed = world.Walkability.TerrainHeight(root.X, root.Z);
        bool nearSurface = MathF.Abs(root.Y - WorldWater.Height) <= 22;
        bool inWater = cold.Race?.Swims == true && bed < WorldWater.Height && root.Y >= bed - 3 && root.Y <= WorldWater.Height;
        Vessel? nearest = null;
        float best = float.PositiveInfinity;
        foreach (var boat in boats.Values)
        {
            var d = boat.Placement.Definition;
            var relative = Navigation.AtOffset(Vector2.Zero, -boat.Yaw, here.X - boat.Position.X, here.Y - boat.Position.Y);
            var gap = Vector2.Max(Vector2.Abs(relative) - new Vector2(d.Width, d.Length) / 2, Vector2.Zero);
            float distance = gap.LengthSquared();
            if ((nearSurface || inWater) && distance <= 12 * 12 && distance < best)
                (nearest, best) = (boat, distance);
        }
        if (nearest is null) { world.Post("Move closer to a vessel to board."); return; }
        if (!nearest.Rider.IsNone) { world.Post("The helm is occupied."); return; }
        StopOrders(world, slot);
        nearest.Rider = rider;
        helms.Add(rider, nearest);
        world.Post($"Boarded {nearest.Placement.Label}.");
    }

    private static void StopOrders(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        cold.OrderQueue.Clear();
        TaskStart.End(world, slot, TaskEnd.Stopped);
        cold.FollowOrder = CharacterId.None;
    }

    private static void Stand(World world, int slot, Vector3 position)
    {
        StopOrders(world, slot);
        ref var actor = ref world.Characters.Next[slot];
        actor.Position = position;
        actor.Velocity = Vector3.Zero;
        actor.Goal = new(position.X, position.Z);
    }

    private void Release(Vessel boat)
    {
        helms.Remove(boat.Rider);
        boat.Rider = CharacterId.None;
        boat.Target = null;
    }

    public void SlowWorld(World world)
    {
        stoppedOnDismount.Clear(); // All movement workers have joined.
        if (boats.Count == 0 && rides.Count == 0 && view.Mounts.Length == 0) return;
        AdvanceRides(world);
        foreach (var boat in boats.Values)
        {
            boat.Previous = boat.Position;
            boat.PreviousYaw = boat.Yaw;
            if (boat.Rider.IsNone) continue;
            if (!world.Characters.TryResolveNext(boat.Rider, out int slot)
                || world.Characters.Cold(slot) is not { IsPlayer: true, Medical: not { Incapacitated: true }, CarriedBy.IsNone: true, InSomething: HeldState.None })
            {
                Release(boat);
                continue;
            }
            if (!Navigation.Advance(world.Walkability, boat, world.TickSeconds))
            {
                boat.Target = null;
                world.Post($"{boat.Placement.Label} stopped in the shallows.");
            }
            var offset = boat.Placement.Definition.SeatOffset;
            var seat = Navigation.AtOffset(boat.Position, boat.Yaw, offset.X, offset.Z);
            ref var actor = ref world.Characters.Next[slot];
            // Save a valid unmounted position. The visual seat height lives on the hull.
            actor.Position = Navigation.AtSurface(seat);
            actor.Yaw = boat.Yaw;
            actor.Velocity = Vector3.Zero;
            world.Characters.Cold(slot)!.WaterState = 0;
        }
        Publish(world.Tick + 1);
    }

    private void Publish(long tick)
    {
        var pieces = new VesselView[boats.Count];
        int index = 0;
        foreach (var b in boats.Values)
        {
            var d = b.Placement.Definition;
            pieces[index++] = new(d.HullPath, d.Texture, Navigation.AtSurface(b.Previous), Navigation.AtSurface(b.Position),
                b.PreviousYaw, b.Yaw, b.Rider, d.SeatOffset);
        }
        Volatile.Write(ref view, new(tick, pieces, rides.Values.Select(p => new MountView(p.Animal, p.Rider, p.Fit)).ToArray()));
    }

    public void Hash(ref StateHasher hash)
    {
        foreach (var ride in rides.Values)
        {
            hash.Add(ride.Animal);
            hash.Add(ride.Rider);
            hash.Add(ride.Footing);
            foreach (char c in ride.Fit.Id) hash.Add((int)c);
        }
        hash.Add(awaitingPlacements);
        hash.Add(boats.Count);
        foreach (var (id, boat) in boats)
        {
            hash.Add(id.Length);
            foreach (char c in id) hash.Add((int)c);
            hash.Add(boat.Position);
            hash.Add(boat.Yaw);
            hash.Add(boat.Rider);
            hash.Add(boat.Target.HasValue);
            if (boat.Target is { } target) hash.Add(target);
        }
    }

    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write(boats.Count + dormant.Count);
        foreach (var (id, b) in boats)
            WritePose(writer, id, new(b.Placement.Definition.Id, b.Position, b.Yaw));
        foreach (var (id, pose) in dormant) WritePose(writer, id, pose);
    }

    private static void WritePose(BinaryWriter w, string id, SavedPose p)
    {
        w.Write(id);
        w.Write(p.Craft);
        w.Write(p.Position.X);
        w.Write(p.Position.Y);
        w.Write(p.Yaw);
    }

    public void Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported transport save version.");
        int count = reader.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("Invalid vessel count.");
        var restored = new SortedDictionary<string, SavedPose>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var id = reader.ReadString();
            var pose = new SavedPose(reader.ReadString(), new(reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());
            if (!Navigation.InWorld(pose.Position) || !float.IsFinite(pose.Yaw) || !restored.TryAdd(id, pose))
                throw new InvalidDataException("Invalid saved vessel pose or duplicate placement.");
        }
        boats.Clear();
        dormant.Clear();
        helms.Clear();
        rides.Clear();
        riders.Clear();
        stoppedOnDismount.Clear();
        awaitingPlacements = true;
        foreach (var (id, p) in restored)
        {
            if (placements.TryGetValue(id, out var placement) && placement.Definition.Id == p.Craft)
                boats.Add(id, new(placement, p.Position, p.Yaw));
            else dormant.Add(id, p);
        }
        Publish(0);
    }
}
