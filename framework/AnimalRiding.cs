using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Transport;

public sealed partial class TransportSystem
{
    private sealed class Ride(CharacterId animal, CharacterId rider, MountDefinition fit, Vector3 footing)
    {
        internal readonly CharacterId Animal = animal, Rider = rider;
        internal readonly MountDefinition Fit = fit;
        internal Vector3 Footing = footing;
    }

    private readonly IReadOnlyDictionary<string, MountDefinition> fits = new Dictionary<string, MountDefinition>();
    private readonly SortedDictionary<int, Ride> rides = [];
    private readonly Dictionary<CharacterId, Ride> riders = [];
    private readonly HashSet<CharacterId> stoppedOnDismount = [];
    private static readonly Vector2[] ExitDirections = [Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY];

    internal int PassengerSlotPlusOne(CharacterId animal) => rides.TryGetValue(animal.Slot, out var ride)
        && ride.Animal == animal ? ride.Rider.Slot + 1 : 0;
    internal Vector3? SavedFooting(CharacterId rider) => riders.TryGetValue(rider, out var ride) ? ride.Footing : null;
    internal bool JustDismounted(CharacterId actor) => stoppedOnDismount.Contains(actor);
    internal bool IsRidingParticipant(World world, CharacterCold cold) => rides.Values.Any(p =>
        ReferenceEquals(world.Characters.Cold(p.Animal.Slot), cold) || ReferenceEquals(world.Characters.Cold(p.Rider.Slot), cold));

    private static bool Available(CharacterCold? c) => c is
    {
        IsPlayer: true, Medical: not { Incapacitated: true },
        InSomething: HeldState.None, CarriedBy.IsNone: true, Carrying.IsNone: true
    };

    private void Mount(World world, CharacterId rider, int slot)
    {
        var table = world.Characters;
        if (!Available(table.Cold(slot)) || table.Cold(slot)!.Animal is not null || IsSeated(rider))
        {
            world.Post("Select a conscious, unoccupied squad member to mount.");
            return;
        }
        var from = table.Next[slot].Position;
        int nearest = -1;
        float best = float.PositiveInfinity;
        bool occupied = false;
        for (int i = 0; i < table.HighWater; i++)
        {
            var cold = table.Cold(i);
            if (!table.Next[i].Alive || !Available(cold) || cold!.Animal is not { } animal || !fits.ContainsKey(animal.StringId)) continue;
            Vector3 gap = table.Next[i].Position - from;
            float distance = gap.LengthSquared();
            float reach = MathF.Max(16, 22 * animal.Scale(cold.ShownAge));
            if (distance > reach * reach || MathF.Abs(gap.Y) > 10) continue;
            if (rides.ContainsKey(i)) { occupied = true; continue; }
            if (distance < best) (nearest, best) = (i, distance); // Equal distances keep the lower slot.
        }
        if (nearest < 0)
        {
            world.Post(occupied ? "The nearby saddle is occupied." : "Move beside a rideable animal in your squad.");
            return;
        }
        ref readonly var mount = ref table.Next[nearest];
        var coldMount = table.Cold(nearest)!;
        var id = new CharacterId(nearest, mount.Generation);
        var exit = Footing(world.Walkability, mount, coldMount);
        // The rider's current footing remains available if the local navigation tiles are still streaming.
        var ride = new Ride(id, rider, fits[coldMount.Animal!.StringId], exit ?? from);
        StopOrders(world, slot);
        riders.Add(rider, ride);
        rides.Add(nearest, ride);
        Select(world, id);
        world.Post($"Mounted {coldMount.Name} ({ride.Fit.Label}).");
    }

    private void Dismount(World world, CharacterId character)
    {
        if (!riders.TryGetValue(character, out var ride)
            && (!rides.TryGetValue(character.Slot, out ride) || ride.Animal != character))
        {
            world.Post("Select a ridden animal or its rider to dismount.");
            return;
        }
        if (!world.Characters.TryResolveNext(ride.Animal, out int a) || !world.Characters.TryResolveNext(ride.Rider, out int r)) return;
        ref readonly var animal = ref world.Characters.Next[a];
        if (Footing(world.Walkability, animal, world.Characters.Cold(a)!) is not { } exit)
        {
            world.Post("No walkable ground beside this animal. Move to a clearer spot.");
            return;
        }
        ReleaseRide(ride);
        Stand(world, a, animal.Position);
        Stand(world, r, exit);
        stoppedOnDismount.Add(ride.Animal);
        stoppedOnDismount.Add(ride.Rider);
        Select(world, ride.Rider);
        world.Post("Dismounted.");
    }

    private static void Select(World world, CharacterId actor) =>
        world.Defer(new SelectCommand([actor]) { Tick = world.Tick + 1 });

    private static bool OnGround(IWalkability walk, Vector3 at, out float y) =>
        walk.TryStand(at.X, at.Z, at.Y, out y) && MathF.Abs(y - at.Y) <= 6
        && (y > WorldWater.Height || walk.WaterBed(at.X, at.Z, y) >= WorldWater.Height);

    private static Vector3? Footing(IWalkability walk, in CharacterHot animal, CharacterCold cold)
    {
        float radius = MathF.Max(9, 11 * (cold.Animal?.Scale(cold.ShownAge) ?? 1));
        var from = animal.Position;
        foreach (var d in ExitDirections)
        {
            var offset = Navigation.AtOffset(Vector2.Zero, animal.Yaw, d.X * radius, d.Y * radius);
            if ((walk.Slide(from, offset, 2) - offset).LengthSquared() > 1) continue;
            var at = from + new Vector3(offset.X, 0, offset.Y);
            if (OnGround(walk, at, out float y) && walk.IsOnSurface(at.X, at.Z, y)) return at with { Y = y };
        }
        return null;
    }

    private void ReleaseRide(Ride ride)
    {
        rides.Remove(ride.Animal.Slot);
        riders.Remove(ride.Rider);
    }

    private void AdvanceRides(World world)
    {
        List<Ride>? ended = null;
        var table = world.Characters;
        foreach (var ride in rides.Values)
        {
            bool riderAlive = table.TryResolveNext(ride.Rider, out int r);
            bool animalAlive = table.TryResolveNext(ride.Animal, out int a);
            if (!riderAlive || !animalAlive || !Available(table.Cold(r)) || !Available(table.Cold(a)))
            {
                (ended ??= []).Add(ride);
                if (riderAlive && table.Cold(r) is { InSomething: HeldState.None, CarriedBy.IsNone: true })
                {
                    table.Next[r].Position = ride.Footing;
                    table.Next[r].Velocity = Vector3.Zero;
                    if (world.Player.Selection.Contains(ride.Animal)) Select(world, ride.Rider);
                }
                continue;
            }
            ref readonly var animal = ref table.Next[a];
            ride.Footing = Footing(world.Walkability, animal, table.Cold(a)!) ?? ride.Footing;
            ref var rider = ref table.Next[r];
            rider.Position = animal.Position;
            rider.Yaw = animal.Yaw;
            rider.Velocity = Vector3.Zero;
        }
        if (ended is not null) foreach (var ride in ended) ReleaseRide(ride);
    }
}
