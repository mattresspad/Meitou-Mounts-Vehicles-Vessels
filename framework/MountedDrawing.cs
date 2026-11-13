using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Characters;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Transport;

// Release-specific bridge to the posed characters. PoseLive has joined all its workers at this boundary.
internal static class MountedDrawing
{
    private delegate void Attach(object renderer, Vector3 eye, Presentation.Frame frame);
    private static Attach attach = null!;

    internal static void Install(Harmony harmony, Type renderer)
    {
        var live = AccessTools.Inner(renderer, "Live");
        var adapter = typeof(PosedFrame<,,,>).MakeGenericType(renderer, live,
            AccessTools.Field(live, "Asset").FieldType, AccessTools.Field(live, "State").FieldType);
        attach = AccessTools.Method(adapter, "AttachRiders").CreateDelegate<Attach>();
        harmony.Patch(AccessTools.Method(renderer, "PoseLive"),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MountedDrawing), nameof(AfterPose))));
    }

    private static void AfterPose(object __instance, Vector3 eye)
    {
        if (Presentation.FrameFor(__instance) is { Mounts.Count: > 0 } frame) attach(__instance, eye, frame);
    }

    // Typed field delegates let the per-frame pass read the client's private struct array in place.
    private static class PosedFrame<TRenderer, TLive, TAsset, TState>
        where TRenderer : class where TLive : struct where TAsset : class where TState : class
    {
        private static readonly AccessTools.FieldRef<TRenderer, TLive[]> Entries = AccessTools.FieldRefAccess<TRenderer, TLive[]>("_live");
        private static readonly AccessTools.FieldRef<TRenderer, int> Count = AccessTools.FieldRefAccess<TRenderer, int>("_liveCount");
        private static readonly AccessTools.StructFieldRef<TLive, long> Key = AccessTools.StructFieldRefAccess<TLive, long>("Key");
        private static readonly AccessTools.StructFieldRef<TLive, bool> Portrait = AccessTools.StructFieldRefAccess<TLive, bool>("PortraitOnly");
        private static readonly AccessTools.StructFieldRef<TLive, Matrix4x4> World = AccessTools.StructFieldRefAccess<TLive, Matrix4x4>("World");
        private static readonly AccessTools.StructFieldRef<TLive, Vector3> Centre = AccessTools.StructFieldRefAccess<TLive, Vector3>("Centre");
        private static readonly AccessTools.StructFieldRef<TLive, TAsset> Asset = AccessTools.StructFieldRefAccess<TLive, TAsset>("Asset");
        private static readonly AccessTools.StructFieldRef<TLive, TState> State = AccessTools.StructFieldRefAccess<TLive, TState>("State");
        private static readonly AccessTools.FieldRef<TAsset, SkeletonRig> Rig = AccessTools.FieldRefAccess<TAsset, SkeletonRig>("Rig");
        private static readonly AccessTools.FieldRef<TState, Matrix4x4[]> Bones = AccessTools.FieldRefAccess<TState, Matrix4x4[]>("Bones");
        private static readonly AccessTools.FieldRef<TState, Matrix4x4> StateWorld = AccessTools.FieldRefAccess<TState, Matrix4x4>("World");
        private static readonly AccessTools.FieldRef<TState, Vector3> StateCentre = AccessTools.FieldRefAccess<TState, Vector3>("Centre");
        private static readonly AccessTools.FieldRef<TRenderer, bool> Motion = AccessTools.FieldRefAccess<TRenderer, bool>("_partMotion");
        private delegate void PlaceParts(TRenderer renderer, in TLive character, Vector3 eye, bool motion);
        private static readonly PlaceParts Place = AccessTools.Method(typeof(TRenderer), "PlaceParts").CreateDelegate<PlaceParts>();

        internal static void AttachRiders(object instance, Vector3 eye, Presentation.Frame frame)
        {
            var renderer = (TRenderer)instance;
            var entries = Entries(renderer);
            frame.LiveSlots.Clear();
            for (int i = 0; i < Count(renderer); i++)
            {
                ref var entry = ref entries[i];
                long key = Key(ref entry);
                if (Portrait(ref entry)) continue;
                if (frame.MountedRiders.Contains(key))
                {
                    frame.LiveSlots[key] = i;
                    // Keep the HUD portrait, but wait to draw this rider in the world until the animal's rig is ready.
                    Portrait(ref entry) = true;
                }
                else if (frame.Mounts.ContainsKey(key)) frame.LiveSlots[key] = i;
            }
            foreach (var (animal, pair) in frame.Mounts)
            {
                if (!frame.LiveSlots.TryGetValue(animal, out int a)) continue;
                ref var mount = ref entries[a];
                var fit = pair.Fit;
                if (BoneFrame(ref mount, fit.Bone) is not { } anchor) continue;
                var saddle = Matrix4x4.CreateFromQuaternion(fit.SaddleRotation) * Matrix4x4.CreateTranslation(fit.SaddleOffset)
                    * anchor * World(ref mount);
                if (!Matrix4x4.Decompose(saddle, out var scale, out var rotation, out var origin)) continue;
                if (fit.Mesh.Length > 0)
                    frame.Meshes.Add(new DebrisPiece(frame.MeshKey(fit.Mesh, fit.Texture), scale.X, rotation, origin));
                if (!frame.LiveSlots.TryGetValue(pair.Rider, out int r)) continue;
                ref var rider = ref entries[r];
                var old = World(ref rider);
                if (!Matrix4x4.Invert(old, out var inverse)) continue;
                float bodyScale = Vector3.TransformNormal(Vector3.UnitY, old).Length();
                var pelvis = BoneFrame(ref rider, "Bip01 Pelvis")?.Translation ?? Vector3.Zero;
                var placed = Matrix4x4.CreateTranslation(-pelvis) * Matrix4x4.CreateScale(bodyScale)
                    * Matrix4x4.CreateRotationY(fit.SeatYaw) * Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(Vector3.Transform(fit.SeatOffset, saddle));
                var centre = Vector3.Transform(Vector3.Transform(Centre(ref rider), inverse), placed);
                var state = State(ref rider);
                Centre(ref rider) = StateCentre(state) = centre;
                World(ref rider) = StateWorld(state) = placed;
                Portrait(ref rider) = false;
                Place(renderer, in rider, eye, Motion(renderer));
            }
        }

        // The skin palette maps the rest model to its animated pose. Starting with model-space axes at the bind
        // bone origin lets authors fit their art facing +Z without knowing the exporter-specific bone orientation.
        private static Matrix4x4? BoneFrame(ref TLive entry, string name)
        {
            var rig = Rig(Asset(ref entry));
            var bones = Bones(State(ref entry));
            return rig?.Handle(name) is int h && h < bones.Length
                ? Matrix4x4.CreateTranslation(rig.BindPosition[h]) * bones[h] : null;
        }
    }
}
