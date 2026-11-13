using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Transport.Verification;

/// <summary>Explicitly enabled in-game checks, staged separately from the release mods.</summary>
public sealed class LiveVoyage : IMod
{
    private const string ProbeId = "meitou.transport.live-voyage";
    private sealed record TestSite(string PlacementId, string CraftId, string MeshKey, Vector3 Anchorage);
    private static TestSite? site;
    private static Trial? trial;
    private static PropertyInfo source = null!, draws = null!;
    private static FieldInfo hostSession = null!, gpu = null!, debris = null!;
    private static MethodInfo drawKey = null!;
    private static FieldInfo modding = null!;
    private static PropertyInfo rows = null!;
    private static bool finished;
    private static bool verifySave;
    private static (Vector3 At, float Yaw)? oldPose;

    public void Initialize(ModContext c)
    {
        LiveRide.Arm();
        Arm();
    }

    internal static void Arm()
    {
        string[] args = Environment.GetCommandLineArgs();
        int game = Array.FindIndex(args, s => s.Equals("--new-game", StringComparison.OrdinalIgnoreCase));
        string? oldSlot = Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_OLD_SAVE");
        bool old = !string.IsNullOrEmpty(oldSlot) && args.Contains("--load") && args.Contains(oldSlot);
        if (Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_LIVE_TEST") != "1" || (!old && (game < 0
            || (game + 1 < args.Length && !args[game + 1].StartsWith("--") && !args[game + 1].Equals("Wanderer", StringComparison.OrdinalIgnoreCase))))) return;
        if (Harmony.HasAnyPatches(ProbeId)) return;
        var harmony = new Harmony(ProbeId);
        string pack = Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_LIVE_PACK") ?? "Western Hive Skiff";
        site = pack == "none" ? null : ReadSite(pack);
        if (old) oldPose = SaveCheck.ReadVessel(oldSlot!, site!.PlacementId, site.CraftId);
        verifySave = Environment.GetEnvironmentVariable("MEITOU_TRANSPORT_LIVE_SAVE") == "1";
        var host = typeof(IMod).Assembly.GetType("Meitou.Game.GameHost", true)!;
        var renderer = typeof(IMod).Assembly.GetType("Meitou.Game.Rendering.Characters.CharacterRenderer", true)!;
        source = AccessTools.Property(renderer, "Source");
        draws = AccessTools.Property(renderer, "Draws");
        hostSession = AccessTools.Field(host, "Session");
        gpu = AccessTools.Field(host, "Gpu");
        debris = AccessTools.Field(gpu.FieldType, "Debris");
        drawKey = AccessTools.Method(host, "DrawKey");
        modding = AccessTools.Field(host, "Modding");
        rows = AccessTools.Property(modding.FieldType, "ModDebugRows");
        // Read the completed serial phase; the driver adds no simulation systems.
        harmony.Patch(AccessTools.Method(typeof(World), "RunSlowWorld"),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(LiveVoyage), nameof(Observe))));
        var afterTransport = new HarmonyMethod(AccessTools.Method(typeof(LiveVoyage), nameof(RenderFrame)))
        {
            after = ["meitou.transport"]
        };
        harmony.Patch(AccessTools.Method(renderer, "CollectBolts"), postfix: afterTransport);
        Log(old ? $"Checking a previous-release save with '{pack}'." : $"Armed for the fresh Wanderer, content pack '{pack}'.");
    }

    private static TestSite ReadSite(string name)
    {
        var install = GameInstall.Locate() ?? throw new InvalidOperationException("Kenshi install missing.");
        var path = Path.Combine(Path.GetDirectoryName(ModList.FindMods(install)[name])!, "transport.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var placement = json.RootElement.GetProperty("placements").EnumerateArray().Single();
        string craft = placement.GetProperty("vessel").GetString()!;
        var vessel = json.RootElement.GetProperty("vessels").EnumerateArray().Single(v => v.GetProperty("id").GetString() == craft);
        var mesh = Path.GetFileName(vessel.GetProperty("hullPath").GetString());
        var texture = Path.GetFileName(vessel.GetProperty("texture").GetString());
        return new(placement.GetProperty("id").GetString()!, craft, $"{mesh}{DebrisPiece.TextureMark}{texture}",
            new(placement.GetProperty("x").GetSingle(), WorldWater.Height, placement.GetProperty("z").GetSingle()));
    }

    private enum Stage { Waiting, Arrive, Board, ObserveBoarded, Sail, SaveWriting, Loading, Restored, Reboard, Exit, Walk }
    private sealed record Observation(long Tick, Vector3 Position, bool Seated, int BoatCount, Vector3? NextWalk);
    private sealed class Trial(World world, CharacterId rider, long key)
    {
        internal readonly World World = world;
        internal readonly CharacterId Rider = rider;
        internal readonly long DrawKey = key;
        // The shipping DLL belongs to Meitou's per-mod load context. Inspect the
        // real system made by the loader without referencing or bundling its DLL.
        internal readonly ITickSystem Transport = world.Systems.Single(s => s.GetType().FullName == "Meitou.Transport.TransportSystem");
        private Func<CharacterId, bool>? seated;
        private PropertyInfo? view, vessels;
        internal bool IsSeated() => (seated ??= AccessTools.Method(Transport.GetType(), "IsSeated")
            .CreateDelegate<Func<CharacterId, bool>>(Transport))(Rider);
        internal int BoatCount()
        {
            view ??= AccessTools.Property(Transport.GetType(), "View");
            vessels ??= AccessTools.Property(view.PropertyType, "Vessels");
            return ((Array)vessels.GetValue(view.GetValue(Transport))!).Length;
        }
        internal Observation? Observed;
        internal Stage Stage;
        internal long Since = Stopwatch.GetTimestamp(), Status = Stopwatch.GetTimestamp();
        internal Vector3 Start, Goal;
        internal SaveCheck? Save;
    }
    private static void Observe(World __instance)
    {
        var run = Volatile.Read(ref trial);
        if (run is null || !ReferenceEquals(run.World, __instance) || !__instance.Characters.TryResolveNext(run.Rider, out int i)) return;
        var at = __instance.Characters.Next[i].Position;
        var goal = WalkNeighbour(__instance.Walkability, at);
        Volatile.Write(ref run.Observed, new Observation(__instance.Tick + 1, at, run.IsSeated(), run.BoatCount(), goal));
    }

    private static readonly Vector2[] Directions = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];
    private static Vector3? WalkNeighbour(IWalkability walk, Vector3 from)
    {
        foreach (var d in Directions)
        {
            var x = from.X + d.X * 36;
            var z = from.Z + d.Y * 36;
            if (walk.TryStand(x, z, from.Y, out var y) && MathF.Abs(y - from.Y) < 12)
                return new Vector3(x, y, z);
        }
        return null;
    }

    private static void RenderFrame(object __instance, List<DebrisPiece> into)
    {
        if (finished || source.GetValue(__instance) is not Delegate { Target: { } host }
            || !hostSession.DeclaringType!.IsInstanceOfType(host) || hostSession.GetValue(host) is not WorldSession session) return;
        try
        {
            var run = Volatile.Read(ref trial);
            if (run is null)
            {
                var player = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer && c.Appearance is not null);
                if (player is null) return;
                run = new Trial(session.World, player.Id, (long)drawKey.Invoke(host, [player.Id])!);
                Volatile.Write(ref trial, run);
                session.Simulation.SetSpeed(1);
                Log($"SETUP fresh actor {player.Id}, {player.Name}, starting at {player.Position}.");
                return;
            }
            if (Stopwatch.GetElapsedTime(run.Since).TotalSeconds > 150)
            {
                Complete(false, $"{run.Stage} timeout. Last simulation observation: {Volatile.Read(ref run.Observed)}.");
                return;
            }
            if (run.Stage == Stage.Loading)
            {
                if (ReferenceEquals(session.World, run.World)) return;
                var loaded = session.CurrentSnapshot.Characters.SingleOrDefault(run.Save!.IsOriginalRider);
                if (loaded is null) return;
                var next = new Trial(session.World, loaded.Id, (long)drawKey.Invoke(host, [loaded.Id])!) { Save = run.Save, Stage = Stage.Restored };
                Volatile.Write(ref trial, next);
                Log($"LOAD new world with {loaded.Name}, record={loaded.RecordId}, saved portrait={loaded.PortraitSerial}, id={loaded.Id}.");
                session.World.Commands.Enqueue(new SelectCommand([loaded.Id]) { Tick = session.World.Tick });
                session.Simulation.SetSpeed(1);
                return;
            }
            if (!ReferenceEquals(run.World, session.World)) { Complete(false, "Active world changed during the test."); return; }
            if (run.Stage == Stage.Restored && session.Simulation.Speed == 0) session.Simulation.SetSpeed(1);
            if (Volatile.Read(ref run.Observed) is not { } sim) return;
            if (site is null)
            {
                var registered = (IReadOnlyList<ModDebugCommand>)rows.GetValue(modding.GetValue(host))!;
                if (sim.BoatCount != 0 || registered.Any(r => r.Owner == "meitou.transport"))
                    throw new InvalidOperationException("An empty framework exposed boats or vessel actions.");
                Complete(true, $"framework alone has zero vessels and zero transport actions at tick {sim.Tick}.");
                return;
            }
            if (sim.BoatCount != 1) throw new InvalidOperationException($"Expected one boat, saw {sim.BoatCount}.");
            var hull = into.SingleOrDefault(p => p.Mesh == site.MeshKey);
            var actor = ((CharacterDrawList)draws.GetValue(__instance)!).Items.FirstOrDefault(a => a.Key == run.DrawKey);
            bool chair = actor.Poses?.Any(p => p.Animation == "sitting chair") == true;
            Vector3? origin = hull.Mesh is not null && gpu.GetValue(host) is { } g && debris.GetValue(g) is DebrisRenderer r
                && r.Centre(hull.Mesh) is { } centre ? hull.Position - Vector3.Transform(centre, hull.Rotation) : null;
            bool attached = origin is { } o && chair && Vector3.Distance(actor.Position, o) < 12;
            if (Stopwatch.GetElapsedTime(run.Status).TotalSeconds > 8)
            {
                Log($"WAIT {run.Stage} tick={sim.Tick} at={sim.Position} seated={sim.Seated} hull={origin} chair={chair} walkable={sim.NextWalk}.");
                run.Status = Stopwatch.GetTimestamp();
            }
            switch (run.Stage)
            {
                case Stage.Waiting:
                    if (origin is null) return;
                    if (oldPose is { } old)
                    {
                        if (Vector3.Distance(origin.Value, old.At) > .05f || sim.Seated || chair
                            || Math.Abs(Quaternion.Dot(hull.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, old.Yaw))) < .99999f)
                            throw new InvalidOperationException("Previous-release save did not restore its occupied voyage with the helm free at the saved pose.");
                        Log($"PREVIOUS RELEASE restored {site.CraftId} at {old.At}, yaw {old.Yaw}, empty helm.");
                        session.Camera.Place(new(old.At.X, old.At.Z), .9f, .4f, 108);
                        session.World.Commands.Enqueue(new SelectCommand([run.Rider]) { Tick = session.World.Tick });
                        Advance(run, Stage.Arrive);
                        break;
                    }
                    if (Horizontal(origin.Value, site.Anchorage) > 1 || Horizontal(sim.Position, site.Anchorage) < 1000)
                        throw new InvalidOperationException("Expected the authored hull at its mooring while the player is still far away.");
                    Log($"WAITING exactly one automatic {site.CraftId}, {hull.Mesh}, at {origin}, before player arrival from {sim.Position}.");
                    var view = session.Camera.Current;
                    session.Camera.Place(new Vector2(site.Anchorage.X, site.Anchorage.Z), view.Yaw, view.Pitch, 135);
                    session.World.Commands.Enqueue(new SelectCommand([run.Rider]) { Tick = session.World.Tick });
                    session.World.Commands.Enqueue(new DebugTeleport([run.Rider], site.Anchorage + new Vector3(8, 0, 0)) { Tick = session.World.Tick });
                    Advance(run, Stage.Arrive);
                    break;
                case Stage.Arrive:
                    if (origin is null || Horizontal(sim.Position, oldPose?.At ?? site.Anchorage) > 23) return;
                    Command(host, session, "Board nearby vessel");
                    Advance(run, Stage.Board);
                    break;
                case Stage.Board:
                    if (!sim.Seated || !attached || origin is null) return;
                    Log($"BOARD tick={sim.Tick} actor={actor.Position} pose=sitting chair hull={origin}.");
                    run.Start = origin.Value;
                    session.Camera.Place(new Vector2(origin.Value.X, origin.Value.Z), .9f, .40f, 108);
                    Log($"OBSERVE BOARD for 15 seconds. Camera target={session.Camera.Current.Target} eye={session.Camera.Current.Eye}.");
                    Advance(run, Stage.ObserveBoarded);
                    break;
                case Stage.ObserveBoarded:
                    if (!sim.Seated || !attached || Stopwatch.GetElapsedTime(run.Since).TotalSeconds < 15) return;
                    Command(host, session, "Sail to camera target", run.Start + new Vector3(80, 0, 120));
                    Advance(run, Stage.Sail);
                    break;
                case Stage.Sail:
                    if (!sim.Seated || !attached || origin is null || Horizontal(run.Start, origin.Value) < 70) return;
                    Log($"SAIL tick={sim.Tick} travelled={Horizontal(run.Start, origin.Value):F1} actor={actor.Position} hull={origin}, rotation={hull.Rotation}.");
                    if (verifySave)
                    {
                        var rider = session.CurrentSnapshot.Characters.Single(c => c.Id == run.Rider);
                        run.Save = new SaveCheck(host, rider, site.PlacementId, site.CraftId);
                        Log($"SAVE requested while occupied and moving, rider={rider.Name} portrait={rider.PortraitSerial}, slot={run.Save.Directory}.");
                        Advance(run, Stage.SaveWriting);
                        break;
                    }
                    Command(host, session, "Disembark");
                    Advance(run, Stage.Exit);
                    break;
                case Stage.SaveWriting:
                    if (!run.Save!.BeginLoadWhenWritten()) return;
                    Log($"SAVE wrote native quick.save and framework sidecar, hull={run.Save.Origin} yaw={run.Save.Yaw}. Loading the same QA slot.");
                    Advance(run, Stage.Loading);
                    break;
                case Stage.Restored:
                    if (origin is null || actor.Poses is null) return;
                    if (sim.Seated || chair || !run.Save!.AtSavedPose(origin.Value, hull.Rotation))
                        throw new InvalidOperationException($"Restored helm, pose or rotation differs. rider={sim}, hull={origin}, chair={chair}, rotation={hull.Rotation}.");
                    if (run.Save.FirstRestoredTick is null)
                    {
                        run.Save.FirstRestoredTick = sim.Tick;
                        Log($"RESTORED empty hull at {origin}, rotation={hull.Rotation}, unseated rider {actor.Position}.");
                    }
                    if (sim.Tick - run.Save.FirstRestoredTick < 30) return;
                    Log($"STATIONARY for {sim.Tick - run.Save.FirstRestoredTick} ticks after native load.");
                    Command(host, session, "Board nearby vessel");
                    Advance(run, Stage.Reboard);
                    break;
                case Stage.Reboard:
                    if (!sim.Seated || !attached) return;
                    Log($"REBOARD same player, actual seated draw={actor.Position}, tick={sim.Tick}.");
                    Command(host, session, "Disembark");
                    Advance(run, Stage.Exit);
                    break;
                case Stage.Exit:
                    if (sim.Seated || chair || sim.NextWalk is not { } target) return;
                    run.Start = sim.Position;
                    run.Goal = target;
                    Log($"EXIT tick={sim.Tick} actor={sim.Position}, unseated, next ordinary movement target={target}.");
                    session.World.Commands.Enqueue(new MoveOrder([run.Rider], target) { Tick = session.World.Tick });
                    Advance(run, Stage.Walk);
                    break;
                case Stage.Walk:
                    if (!sim.Seated && Horizontal(run.Goal, sim.Position) < Horizontal(run.Goal, run.Start) - 5)
                        Complete(true, $"normal MoveOrder advanced {Horizontal(run.Start, sim.Position):F1} units from {run.Start} toward {run.Goal}, tick={sim.Tick}.");
                    break;
            }
        }
        catch (Exception e) { Complete(false, e.ToString()); }
    }

    // Exactly the normal F10 handler, including its live selection and camera target.
    internal static void Command(object host, WorldSession session, string label, Vector3? target = null)
    {
        if (target is { } p)
        {
            var v = session.Camera.Current;
            session.Camera.Place(new Vector2(p.X, p.Z), v.Yaw, v.Pitch, v.Distance);
        }
        var menu = AccessTools.Field(host.GetType(), "DebugMenu").GetValue(host)!;
        var apply = AccessTools.Method(menu.GetType(), "DebugApply");
        var modding = AccessTools.Field(host.GetType(), "Modding").GetValue(host)!;
        var registered = (IReadOnlyList<ModDebugCommand>)AccessTools.Property(modding.GetType(), "ModDebugRows").GetValue(modding)!;
        int nativeRows = ((string[])AccessTools.Field(menu.GetType(), "DebugLabels").GetValue(null)!).Length;
        int index = Enumerable.Range(0, registered.Count).Single(i => registered[i].Owner == "meitou.transport" && registered[i].Label == label);
        var row = Enum.ToObject(apply.GetParameters()[0].ParameterType, nativeRows + index);
        string? result = (string?)apply.Invoke(menu, [row, "", default((string?, string)), null]);
        Log($"F10 {label} -> {result}");
        if (result?.EndsWith("queued.", StringComparison.Ordinal) != true) throw new InvalidOperationException($"The F10 row was refused: {result}");
    }
    private static float Horizontal(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static void Advance(Trial run, Stage stage) { run.Stage = stage; run.Since = Stopwatch.GetTimestamp(); }
    private static void Log(string line) => Console.WriteLine("LIVE VOYAGE " + line);
    private static void Complete(bool ok, string line)
    {
        finished = true;
        Volatile.Write(ref trial, null);
        Log((ok ? "PASS " : "FAIL ") + line);
    }
}
