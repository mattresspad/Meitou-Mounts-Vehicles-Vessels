using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Transport;

internal static class Navigation
{
    internal static Vector3 AtSurface(Vector2 p) => new(p.X, WorldWater.Height, p.Y);

    internal static Vector2 AtOffset(Vector2 center, float yaw, float right, float forward)
    {
        var (sin, cos) = MathF.SinCos(yaw);
        return center + new Vector2(cos * right + sin * forward, -sin * right + cos * forward);
    }

    internal static bool InWorld(Vector2 point) =>
        MathF.Abs(point.X) < WorldLayout.HalfWorldSize && MathF.Abs(point.Y) < WorldLayout.HalfWorldSize;

    // Check the centerline and both rails at the bow, amidships and stern.
    internal static bool Afloat(IWalkability walk, VesselDefinition hull, Vector2 position, float yaw)
    {
        for (int fore = -1; fore <= 1; fore++)
        {
            for (int side = -1; side <= 1; side++)
            {
                var p = AtOffset(position, yaw, side * hull.Width / 2, fore * hull.Length / 2);
                if (!InWorld(p) || !(WorldWater.Height - walk.TerrainHeight(p.X, p.Y) >= hull.Draft))
                    return false;
            }
        }
        return true;
    }

    // A short sweep bounds the motion of the ends as well as that of the pivot.
    // Consume one turn or forward step at a time, always retaining the last safe pose.
    internal static bool Advance(IWalkability walk, Vessel boat, float seconds)
    {
        if (boat.Target is not { } target) return true;
        var difference = target - boat.Position;
        float distance = difference.Length();
        if (distance < 0.05f) { boat.Target = null; return true; }
        var d = boat.Placement.Definition;
        float desired = MathF.Atan2(difference.X, difference.Y);
        float angle = MathF.IEEERemainder(desired - boat.Yaw, MathF.Tau);
        float rate = d.TurnDegrees * (MathF.PI / 180);
        float sample = Math.Clamp(MathF.Min(d.Width, d.Length) / 4, 0.5f, 2f);
        float radius = MathF.Sqrt(d.Length * d.Length + d.Width * d.Width) / 2;
        float angularStep = sample / MathF.Max(radius, sample);
        for (int i = 0; i < 256 && seconds > 0; i++)
        {
            if (MathF.Abs(angle) > 0.0001f)
            {
                float turn = MathF.CopySign(MathF.Min(MathF.Abs(angle), MathF.Min(rate * seconds, angularStep)), angle);
                float next = MathF.IEEERemainder(boat.Yaw + turn, MathF.Tau);
                if (!Afloat(walk, d, boat.Position, next)) return false;
                boat.Yaw = next;
                angle -= turn;
                seconds -= MathF.Abs(turn) / rate;
                continue;
            }
            float step = MathF.Min(distance, MathF.Min(d.Speed * seconds, sample));
            var nextPosition = step >= distance ? target : boat.Position + difference / distance * step;
            if (!Afloat(walk, d, nextPosition, boat.Yaw)) return false;
            boat.Position = nextPosition;
            if (step >= distance) { boat.Target = null; break; }
            seconds -= step / d.Speed;
            difference = target - nextPosition;
            distance = difference.Length();
        }
        return true;
    }

    internal static Vector3? Exit(IWalkability walk, Vessel boat)
    {
        var d = boat.Placement.Definition;
        float beam = d.Width / 2 + 3, length = d.Length / 2 + 3;
        Vector3? water = null;
        foreach (var offset in new Vector2[] { new(beam, 0), new(-beam, 0), new(0, length), new(0, -length),
                     new(beam + 5, 0), new(-beam - 5, 0), new(0, length + 5), new(0, -length - 5) })
        {
            var at = AtOffset(boat.Position, boat.Yaw, offset.X, offset.Y);
            if (!InWorld(at)) continue;
            float bed = walk.TerrainHeight(at.X, at.Y);
            if (walk.TryStand(at.X, at.Y, WorldWater.Height + 3, out float floor)
                && (bed >= WorldWater.Height || floor > WorldWater.Height + 1)
                && MathF.Abs(floor - WorldWater.Height) <= 8)
                return new(at.X, floor, at.Y);
            if (bed < WorldWater.Height) water ??= AtSurface(at);
        }
        return water;
    }
}
