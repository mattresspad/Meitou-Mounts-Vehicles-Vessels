using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Meitou.Transport.Tests")]

namespace Meitou.Transport;

public sealed record VesselDefinition(
    string Id, string Label, string HullPath, string Texture, Vector3 SeatOffset,
    float Length, float Width, float Draft, float Speed, float TurnDegrees);

public sealed record TransportPlacement(string Id, string Label, VesselDefinition Definition, float X, float Z, float HeadingDegrees)
{
    public bool PlaceOnLoad { get; init; }
}

internal sealed record MountDefinition(string Id, string Label, string SourceMod, string Bone, Vector3 SaddleOffset,
    Quaternion SaddleRotation, Vector3 SeatOffset, float SeatYaw, string Mesh, string Texture);

internal sealed class TransportCatalog
{
    internal SortedDictionary<string, TransportPlacement> Placements { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, MountDefinition> Mounts { get; } = new(StringComparer.Ordinal);
}

internal static class TransportPacks
{
    private const string FileName = "transport.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static TransportCatalog Load(GameDatabase data, GameInstall? install,
        Action<string> log, Action<string> logError)
    {
        var catalog = new TransportCatalog();
        if (install is null)
        {
            logError("Could not locate the active Kenshi installation to resolve transport packs.");
            return catalog;
        }
        var definitions = new Dictionary<string, VesselDefinition>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var paths = ModList.FindMods(install);
        var rigs = new Lazy<CharacterRigs>(() => CharacterRigs.For(data, new AssetLocator(install)));
        var animalRigs = new Dictionary<string, SkeletonRig>(StringComparer.Ordinal);
        SkeletonRig RigFor(string id)
        {
            if (animalRigs.TryGetValue(id, out var cached)) return cached;
            if (data.Find(id) is not { Type: FcsRecordType.AnimalCharacter } animal)
                throw new InvalidDataException($"Unknown ANIMAL_CHARACTER '{id}'.");
            var appearance = CharacterAppearance.BuildAnimal(data, install.Root, animal);
            if (rigs.Value.FindMesh(appearance.BodyMesh!) is not { } body || rigs.Value.BodySkeletonName(body) is not { } skeleton
                || rigs.Value.Rig(skeleton) is not { } rig) throw new InvalidDataException($"Missing body rig for '{id}'.");
            return animalRigs[id] = rig;
        }
        foreach (string applied in data.Files)
        {
            if (LoadOrder.BaseFiles.Contains(applied, StringComparer.OrdinalIgnoreCase) || !applied.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)
                || !paths.TryGetValue(Path.GetFileNameWithoutExtension(applied), out var modPath)) continue;

            string directory = Path.GetDirectoryName(modPath)!;
            string manifest = Path.Combine(directory, FileName);
            if (!File.Exists(manifest)) continue;
            try
            {
                using var input = File.OpenRead(manifest);
                var pack = JsonSerializer.Deserialize<Pack>(input, Json) ?? throw new InvalidDataException("Empty document.");
                if (owners.Contains(pack.PackId)) throw new InvalidDataException($"Duplicate packId '{pack.PackId}'.");
                AddPack(pack, modPath, definitions, catalog, RigFor, log);
                owners.Add(pack.PackId);
                log($"{applied} registered {pack.Vessels?.Length ?? 0} vessel(s), {pack.Placements.Length} placement(s) and {pack.Mounts?.Length ?? 0} mount fit(s).");
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or KeyNotFoundException or OgreFormatException)
            {
                logError($"Skipped {manifest}: {error.Message}");
            }
        }
        return catalog;
    }

    private static void AddPack(Pack pack, string modPath, Dictionary<string, VesselDefinition> definitions,
        TransportCatalog catalog, Func<string, SkeletonRig> rigFor, Action<string> log)
    {
        string directory = Path.GetDirectoryName(modPath)!;
        string sourceMod = Path.GetFileName(modPath);
        if (pack.SchemaVersion is < 1 or > 3 || string.IsNullOrWhiteSpace(pack.PackId) || pack.PackId != pack.PackId.Trim()
            || pack.Placements is null || (pack.SchemaVersion < 3 ? pack.Vessels is null || pack.Mounts is not null : pack.Vessels is null && pack.Mounts is null))
            throw new InvalidDataException("Expected a packId and vessels (schemas 1–3) or mounts (schema 3).");

        var newDefinitions = new Dictionary<string, VesselDefinition>(StringComparer.Ordinal);
        var newPlacements = new Dictionary<string, TransportPlacement>(StringComparer.Ordinal);
        foreach (var v in pack.Vessels ?? [])
        {
            if (v is null || !Named(pack.PackId, v.Id, v.Label) || v.SeatOffset is not { Length: 3 } || !v.SeatOffset.All(float.IsFinite)
                || !Positive(v.Length) || !Positive(v.Width) || !Positive(v.Draft) || !Positive(v.Speed) || !Positive(v.TurnDegrees))
                throw new InvalidDataException("A vessel needs an id, label, three seatOffset coordinates and positive dimensions and movement limits.");

            var definition = new VesselDefinition(v.Id, v.Label, Asset(directory, v.HullPath), Asset(directory, v.Texture),
                new Vector3(v.SeatOffset[0], v.SeatOffset[1], v.SeatOffset[2]), v.Length, v.Width, v.Draft, v.Speed, v.TurnDegrees);
            if (definitions.ContainsKey(v.Id) || !newDefinitions.TryAdd(v.Id, definition))
                throw new InvalidDataException($"Duplicate vessel id '{v.Id}'.");
        }

        foreach (var p in pack.Placements)
        {
            if (p is null || !Named(pack.PackId, p.Id, p.Label) || string.IsNullOrWhiteSpace(p.Vessel)
                || !float.IsFinite(p.X) || !float.IsFinite(p.Z) || !float.IsFinite(p.HeadingDegrees))
                throw new InvalidDataException("A placement needs an id, label, vessel id and finite coordinates and heading.");

            if (!newDefinitions.TryGetValue(p.Vessel, out var vessel) && !definitions.TryGetValue(p.Vessel, out vessel))
                throw new InvalidDataException($"Placement '{p.Id}' refers to unknown vessel '{p.Vessel}'.");

            bool placeOnLoad = p.PlaceOnLoad.ValueKind switch
            {
                JsonValueKind.Undefined => false,
                JsonValueKind.True or JsonValueKind.False when pack.SchemaVersion >= 2 => p.PlaceOnLoad.GetBoolean(),
                _ => throw new InvalidDataException($"Placement '{p.Id}': placeOnLoad must be a boolean in schema 2 or 3."),
            };
            var placement = new TransportPlacement(p.Id, p.Label, vessel, p.X, p.Z, p.HeadingDegrees) { PlaceOnLoad = placeOnLoad };
            if (catalog.Placements.ContainsKey(p.Id) || !newPlacements.TryAdd(p.Id, placement))
                throw new InvalidDataException($"Duplicate placement id '{p.Id}'.");
        }

        var fits = new Dictionary<string, MountDefinition>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in pack.Mounts ?? [])
        {
            if (m is null || !Named(pack.PackId, m.Id, m.Label) || !ids.Add(m.Id) || string.IsNullOrWhiteSpace(m.AnchorBone)
                || m.AnimalRecordIds is not { Length: > 0 } || !float.IsFinite(m.SeatYawDegrees))
                throw new InvalidDataException("A mount needs a unique id, label, anchorBone, animalRecordIds and finite seat yaw.");
            var degrees = Vector(m.SaddleRotationDegrees, m.Id) * (MathF.PI / 180);
            var rotation = Quaternion.CreateFromYawPitchRoll(degrees.Y, degrees.X, degrees.Z);
            if (m.Texture is not null && m.SaddleMesh is null) throw new InvalidDataException($"'{m.Id}' supplies a texture without a saddle mesh.");
            var fit = new MountDefinition(m.Id, m.Label, sourceMod, m.AnchorBone, Vector(m.SaddleOffset, m.Id), rotation,
                Vector(m.SeatOffset, m.Id), m.SeatYawDegrees * (MathF.PI / 180),
                m.SaddleMesh is null ? "" : Asset(directory, m.SaddleMesh), m.Texture is null ? "" : Asset(directory, m.Texture));
            foreach (var record in m.AnimalRecordIds)
            {
                if (string.IsNullOrWhiteSpace(record) || !fits.TryAdd(record, fit))
                    throw new InvalidDataException($"'{m.Id}' has an empty or repeated animal record '{record}'.");
                if (rigFor(record).Handle(fit.Bone) is null)
                    throw new InvalidDataException($"'{m.Id}': animal '{record}' has no bone '{fit.Bone}'.");
            }
        }
        foreach (var item in newDefinitions) definitions.Add(item.Key, item.Value);
        foreach (var item in newPlacements) catalog.Placements.Add(item.Key, item.Value);
        foreach (var (record, fit) in fits)
        {
            if (catalog.Mounts.TryGetValue(record, out var previous))
                log($"Animal '{record}': {fit.SourceMod} ({fit.Id}) overrides {previous.SourceMod} ({previous.Id}) in enabled mod order.");
            catalog.Mounts[record] = fit;
        }
    }

    private static Vector3 Vector(float[]? values, string id) => values is { Length: 3 } && values.All(float.IsFinite)
        ? new(values[0], values[1], values[2]) : throw new InvalidDataException($"'{id}' needs three finite coordinates per transform.");

    private static bool Named(string owner, string? id, string? label) =>
        id is not null && id.StartsWith(owner + ".", StringComparison.Ordinal) && id.Length > owner.Length + 1
        && id == id.Trim() && !string.IsNullOrWhiteSpace(label);

    private static bool Positive(float value) => float.IsFinite(value) && value > 0;

    private static string Asset(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':')
            || name.Contains('\\') || name.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Expected a pack-relative asset path, got '{name}'.");
        if (!File.Exists(Path.Combine(directory, name)))
            throw new InvalidDataException($"Missing asset '{name}'.");
        return name;
    }

    private sealed record Pack
    {
        public required int SchemaVersion { get; init; }
        public required string PackId { get; init; }
        public Vessel[]? Vessels { get; init => field = value ?? throw new JsonException("vessels must be an array."); }
        public Mount[]? Mounts { get; init => field = value ?? throw new JsonException("mounts must be an array."); }
        public Placement[] Placements { get; init; } = [];
    }

    private sealed record Mount
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string[] AnimalRecordIds { get; init; }
        public required string AnchorBone { get; init; }
        public required float[] SaddleOffset { get; init; }
        public required float[] SaddleRotationDegrees { get; init; }
        public required float[] SeatOffset { get; init; }
        public required float SeatYawDegrees { get; init; }
        public string? SaddleMesh { get; init; }
        public string? Texture { get; init; }
    }

    private sealed record Vessel
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string HullPath { get; init; }
        public required string Texture { get; init; }
        public required float[] SeatOffset { get; init; }
        public required float Length { get; init; }
        public required float Width { get; init; }
        public required float Draft { get; init; }
        public required float Speed { get; init; }
        public required float TurnDegrees { get; init; }
    }

    private sealed record Placement
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Vessel { get; init; }
        public required float X { get; init; }
        public required float Z { get; init; }
        public required float HeadingDegrees { get; init; }
        public JsonElement PlaceOnLoad { get; init; }
    }
}
