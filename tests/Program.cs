using Meitou.Transport;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "mods", "Western Hive Skiff", "transport.json")))
    root = root.Parent;
if (root is null) throw new DirectoryNotFoundException("Unable to locate the sample mods next to the framework source.");

TransportPlacement sample = PackChecks.Run(Path.Combine(root.FullName, "mods"));
Console.WriteLine("PASS  framework alone, FCS packages, schemas 1/2, load order, Workshop, mesh and texture");
var one = VoyageChecks.Run(sample, threads: 1);
var four = VoyageChecks.Run(sample, threads: 4);
Check.That(one.SequenceEqual(four), "every tick must have the same world hash with one and four workers");
Console.WriteLine($"PASS  automatic/manual sites, boarding, steering, draft, 0.1.0 saves, pack re-enabling and {one.Count} matching tick hashes");
TransportControls.Install(new HarmonyLib.Harmony("meitou.transport.tests"), mounts: true);
var ride = RideChecks.Run(1);
Check.That(ride.SequenceEqual(RideChecks.Run(4)), "mounted world hashes differ by worker count");
Console.WriteLine($"PASS  schema 3, load-order override, bad bone, native riding and selection, forced and blocked exits, {ride.Count} matching ride hashes");

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
