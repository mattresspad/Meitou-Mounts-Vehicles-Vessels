using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Session;

namespace Meitou.Transport;

internal sealed record VesselView(string Mesh, string Texture, Vector3 Previous, Vector3 Current,
    float PreviousYaw, float Yaw, CharacterId Rider, Vector3 SeatOffset);

internal sealed record MountView(CharacterId Animal, CharacterId Rider, MountDefinition Fit);
internal sealed record TransportView(long Tick, VesselView[] Vessels, MountView[] Mounts);

/// <summary>The engine-facing integration for Meitou v0.17.4.</summary>
internal static class Presentation
{
    private static readonly Type GameHost = GameType("Meitou.Game.GameHost");
    private static readonly Type Renderer = GameType("Meitou.Game.Rendering.Characters.CharacterRenderer");
    private static readonly PropertyInfo DrawSource = Property(Renderer, "Source");
    private static readonly FieldInfo Gpu = Field(GameHost, "Gpu");
    private static readonly FieldInfo Debris = Field(Gpu.FieldType, "Debris");
    private static readonly FieldInfo Player = Field(GameHost, "Player");
    private static readonly PropertyInfo TitleOpen = Property(Player.FieldType, "TitleOpen");
    private static readonly PropertyInfo Editor = Property(Player.FieldType, "CharacterEditor");
    private static readonly PropertyInfo EditorOpen = Property(Editor.PropertyType, "EditorOpen");
    private static readonly MethodInfo DrawKey = Method(GameHost, "DrawKey");
    private static readonly ConditionalWeakTable<WorldSnapshot, TransportView> Views = new();
    private static readonly ConditionalWeakTable<object, Frame> Frames = new();
    private static readonly CharacterPose[] SeatedPose = [new("sitting chair", 1, 1)];

    public static void Install(Harmony harmony, bool mounts)
    {
        TransportControls.Install(harmony, mounts);
        harmony.Patch(Method(typeof(World), "Publish"), postfix: Hook(nameof(CaptureSnapshot)));
        harmony.Patch(Method(GameHost, "FillDrawList"), postfix: Hook(nameof(PrepareFrame)));
        harmony.Patch(Method(GameHost, "FillWorldDrawList"), postfix: Hook(nameof(PrepareFrame)));
        harmony.Patch(Method(Renderer, "CollectBolts"), postfix: Hook(nameof(AddMeshes)));
        if (mounts) MountedDrawing.Install(harmony, Renderer);
    }

    private static Type GameType(string name) => typeof(IMod).Assembly.GetType(name, throwOnError: true)!;
    private static MethodInfo Method(Type owner, string name) => AccessTools.Method(owner, name)
        ?? throw new MissingMethodException(owner.FullName, name);
    private static FieldInfo Field(Type owner, string name) => AccessTools.Field(owner, name)
        ?? throw new MissingFieldException(owner.FullName, name);
    private static PropertyInfo Property(Type owner, string name) => AccessTools.Property(owner, name)
        ?? throw new MissingMemberException(owner.FullName, name);
    private static HarmonyMethod Hook(string name) => new(Method(typeof(Presentation), name));

    // Tick-ahead can publish a newer world while the renderer is still showing an earlier snapshot.
    private static void CaptureSnapshot(World __instance)
    {
        var snapshot = __instance.Snapshot;
        if (TransportControls.For(__instance)?.View is { } view && view.Tick == snapshot.Tick)
            Views.AddOrUpdate(snapshot, view);
        else Views.Remove(snapshot);
    }

    internal readonly record struct Seat(Vector3 Position, float Yaw);

    internal sealed class Frame(object host)
    {
        public readonly Func<CharacterId, long> KeyFor = DrawKey.CreateDelegate<Func<CharacterId, long>>(host);
        public readonly List<DebrisPiece> Meshes = [];
        internal readonly Dictionary<long, Seat> Seats = [];
        internal readonly Dictionary<long, (long Rider, MountDefinition Fit)> Mounts = [];
        internal readonly HashSet<long> MountedRiders = [];
        internal readonly Dictionary<long, int> LiveSlots = [];
        public readonly Dictionary<(string Mesh, string Texture), string> Assets = [];
        public readonly HashSet<string> Missing = new(StringComparer.Ordinal);

        public string MeshKey(string mesh, string texture)
        {
            var key = (mesh, texture);
            if (Assets.TryGetValue(key, out var asset)) return asset;
            // The loose-mesh loader strips directories. Enabled mod resource folders resolve the unique filenames.
            asset = FileName(mesh);
            if (texture.Length > 0) asset += DebrisPiece.TextureMark + FileName(texture);
            Assets.Add(key, asset);
            return asset;
        }

        private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));
    }

    internal static Frame? FrameFor(object renderer) => DrawSource.GetValue(renderer) is Delegate { Target: { } host }
        && Frames.TryGetValue(host, out var frame) ? frame : null;

    private static void PrepareFrame(object __instance, WorldSession ___Session, CharacterDrawList list)
    {
        var frame = Frames.GetValue(__instance, static host => new Frame(host));
        frame.Meshes.Clear();
        frame.Seats.Clear();
        frame.Mounts.Clear();
        frame.MountedRiders.Clear();
        if (!Views.TryGetValue(___Session.CurrentSnapshot, out var view)) return;
        if (Player.GetValue(__instance) is { } player &&
            (TitleOpen.GetValue(player) is true || EditorOpen.GetValue(Editor.GetValue(player)) is true)) return;

        var alpha = ___Session.SimulationAlpha;
        foreach (var vessel in view.Vessels)
        {
            var origin = Vector3.Lerp(vessel.Previous, vessel.Current, alpha);
            var yaw = Interp.LerpAngle(vessel.PreviousYaw, vessel.Yaw, alpha);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            frame.Meshes.Add(new DebrisPiece(frame.MeshKey(vessel.Mesh, vessel.Texture), 1, rotation, origin));
            if (!vessel.Rider.IsNone)
                frame.Seats[frame.KeyFor(vessel.Rider)] = new Seat(origin + Vector3.Transform(vessel.SeatOffset, rotation), yaw);
        }

        foreach (var mount in view.Mounts)
        {
            long rider = frame.KeyFor(mount.Rider);
            frame.Mounts[frame.KeyFor(mount.Animal)] = (rider, mount.Fit);
            frame.MountedRiders.Add(rider);
        }
        if (frame.Seats.Count == 0 && frame.MountedRiders.Count == 0) return;
        for (int i = 0; i < list.Items.Count; i++)
        {
            var actor = list.Items[i];
            if (frame.Seats.TryGetValue(actor.Key, out var seat))
                list.Items[i] = actor with { Position = seat.Position, Yaw = seat.Yaw, Rotation = null, Poses = SeatedPose, Bones = null };
            else if (frame.MountedRiders.Contains(actor.Key))
                list.Items[i] = actor with { Poses = SeatedPose, Bones = null, FullRateAnimation = true };
        }
    }

    private static void AddMeshes(object __instance, List<DebrisPiece> into)
    {
        // The title screen and editor can swap the character renderer's source. These are gameplay meshes.
        if (DrawSource.GetValue(__instance) is not Delegate { Target: { } host } || !Frames.TryGetValue(host, out var frame)
            || frame.Meshes.Count == 0 || Gpu.GetValue(host) is not { } gpu || Debris.GetValue(gpu) is not DebrisRenderer renderer) return;

        foreach (var piece in frame.Meshes)
        {
            if (renderer.Centre(piece.Mesh) is not { } centre)
            {
                if (frame.Missing.Add(piece.Mesh)) Console.Error.WriteLine($"Meitou Transport could not load mesh {piece.Mesh}.");
                continue;
            }
            // This renderer centres rigid mesh vertices on their bounds. Seat offsets refer to the asset's authored origin.
            into.Add(piece with { Position = piece.Position + Vector3.Transform(centre * piece.Scale, piece.Rotation) });
        }
    }
}
