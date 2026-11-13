using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Saving;

namespace Meitou.Transport;

internal static class TransportControls
{
    private sealed class Lookup(World world)
    {
        public readonly TransportSystem? System = world.Systems.OfType<TransportSystem>().FirstOrDefault();
    }
    private static readonly ConditionalWeakTable<World, Lookup> Systems = new();
    private static readonly Action<OrderQueue, OrderEntry> AppendOrder =
        AccessTools.Method(typeof(OrderQueue), "Add").CreateDelegate<Action<OrderQueue, OrderEntry>>();
    internal static TransportSystem? For(World world) => Systems.GetValue(world, static w => new Lookup(w)).System;

    internal static void Install(Harmony harmony, bool mounts)
    {
        void Prefix(MethodInfo method, string name) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(TransportControls), name));
        Prefix(AccessTools.Method(typeof(MovementSystem), "MoveCharacter"), nameof(AllowWalking));
        if (!mounts) return;
        Prefix(AccessTools.Method(typeof(MovementSystem), "AddSeparation"), nameof(AllowNeighbour));
        var write = typeof(SaveCapture).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "WriteCharacter" && m.GetParameters().Any(p => p.Name == "position"));
        Prefix(write, nameof(SaveFooting));
        Prefix(AccessTools.Method(typeof(OrderSave), nameof(OrderSave.Write)), nameof(SaveOrders));
    }

    // MoveCharacter handles one slot per worker. AddSeparation is synchronous within that walk.
    [ThreadStatic] private static int passengerSlotPlusOne;
    private static bool AllowWalking(World world, int i, ref CharacterHot n, CharacterCold cold)
    {
        var system = For(world);
        var id = world.Characters.IdOf(i);
        passengerSlotPlusOne = system?.PassengerSlotPlusOne(id) ?? 0;
        // Native walking integrates from the previous tick's velocity, even after its orders are cleared.
        // Preserve the exact positions chosen in Inputs for this dismount, then release both actors.
        if (system?.JustDismounted(id) == true)
        {
            cold.AgentSpeed = 0;
            n.Velocity = Vector3.Zero;
            n.Animation = 0;
            return false;
        }
        if (system?.IsSeated(id) != true) return true;
        cold.WaterState = 0;
        n.Velocity = Vector3.Zero;
        return false;
    }

    private static bool AllowNeighbour(int other) => other + 1 != passengerSlotPlusOne;

    private static void SaveFooting(World world, CharacterId id, ref Vector3 position)
    {
        if (For(world)?.SavedFooting(id) is { } footing) position = footing;
    }

    private static void SaveOrders(World world, ref CharacterCold cold)
    {
        if (For(world)?.IsRidingParticipant(world, cold) != true) return;
        // Only the native queue serializer sees this projection. It reads SavedOrders or OrderQueue.
        // The live route continues, and the ordinary jobs, combat settings and inventory are saved independently.
        var saved = new CharacterCold { SavedOrders = cold.SavedOrders?.Where(o => o.Row != TaskType.MoveCusOrdered).ToList() };
        foreach (var order in cold.OrderQueue)
            if (order.Row != TaskType.MoveCusOrdered) AppendOrder(saved.OrderQueue, order);
        cold = saved;
    }
}
