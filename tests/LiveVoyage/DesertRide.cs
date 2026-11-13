using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Transport.Verification;

internal static class DesertRide
{
    internal const string Start = "Stoat Desert Ride";
    internal const string Garru = "42201-rebirth.mod";
    internal const string PackBeast = "3999-gamedata.base";
    internal const string StockBag = "4002-gamedata.base";
    internal const string Bull = "43949-rebirth.mod";
    private static readonly Vector2 Pin = new(34595, -70128);
    private static readonly (string Section, string Record)[] Outfit =
    [
        ("boots", "557-gamedata.base"), ("armour", "51663-Newwworld.mod"),
        ("legs", "51662-Newwworld.mod"), ("head", "2168-gamedata.base")
    ];

    internal static void CheckRider(World world, CharacterSnapshot rider)
    {
        var look = rider.Appearance;
        if (rider.RecordId != "1-Stoat Desert Ride.mod" || look?.Race.StringId != "18019-gamedata.base" || !look.Female)
            throw new InvalidOperationException("Expected the desert start's female Scorchlander rider.");
        var items = world.Characters.Cold(rider.Id.Slot)?.Inventory?.Items;
        foreach (var (section, record) in Outfit)
            if (items?.Any(i => i.Section == section && i.Record == record) != true)
                throw new InvalidOperationException($"Expected {record} equipped in {section}.");
    }

    internal static void CheckStart(World world, WorldSnapshot snapshot, CharacterSnapshot rider, string mountRecord)
    {
        CheckRider(world, rider);
        var party = snapshot.Characters.Where(c => c.IsPlayer).ToArray();
        if (party.Length != 3 || party.Count(c => c.RecordId == mountRecord) != 1 || party.Count(c => c.RecordId == Bull) != 1)
            throw new InvalidOperationException($"Expected one rider, one owned {mountRecord} and one owned Domesticated Bull.");
        foreach (var member in party)
            if (Vector2.Distance(new(member.Position.X, member.Position.Z), Pin) > 40)
                throw new InvalidOperationException($"{member.Name} spawned away from the pin at {member.Position}.");
        Console.WriteLine($"LIVE RIDE Stoat start at {rider.Position}: female Scorchlander, all four items equipped, {mountRecord} and Domesticated Bull present.");
    }
}
