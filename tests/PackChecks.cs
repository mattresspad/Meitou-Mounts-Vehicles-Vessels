using System.Text.Json.Nodes;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Transport;

internal static class PackChecks
{
    public static TransportPlacement Run(string mods)
    {
        const string coreName = "Meitou Transport";
        const string sampleName = "Western Hive Skiff";
        string sample = Path.Combine(mods, sampleName);
        var coreFile = FcsReader.ReadFile(Path.Combine(mods, coreName, coreName + ".mod"));
        var sampleFile = FcsReader.ReadFile(Path.Combine(sample, sampleName + ".mod"));
        Check.That(coreFile.Records.Count == 0 && sampleFile.Dependencies.SequenceEqual([coreName + ".mod"]),
            "FCS reader must recognize the framework and sample header dependency");

        var temp = Directory.CreateTempSubdirectory("meitou-transport-tests-");
        try
        {
            string steamApps = Path.Combine(temp.FullName, "steamapps");
            string root = Path.Combine(steamApps, "common", "Kenshi");
            string data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            MountPackChecks.WriteAnimalData(data);
            var install = GameInstall.Open(root);
            CopyPack(Path.Combine(mods, coreName), Path.Combine(install.ModsDirectory, coreName));
            CopyPack(sample, Path.Combine(install.ModsDirectory, sampleName));
            MountPackChecks.Run(mods, install);

            string second = Path.Combine(steamApps, "workshop", "content", "233860", "12345678");
            MakeVariant(sample, second, "Workshop Canoe", "acceptance.canoe", "acceptance.dock");
            string malformed = Path.Combine(install.ModsDirectory, "Broken");
            MakeVariant(sample, malformed, "Broken", "acceptance.canoe", "acceptance.incomplete", invalid: true);
            MakeVariant(sample, Path.Combine(install.ModsDirectory, "Disabled"), "Disabled", "disabled.boat", "disabled.dock");
            var errors = new List<string>();
            var files = Path.Combine(data, "mods.cfg");
            File.WriteAllLines(files, [coreName + ".mod"]);
            var onlyCore = TransportPacks.Load(GameDatabase.Load(LoadOrder.FromInstall(install)), install, _ => { }, errors.Add);
            Check.That(onlyCore.Placements.Count == 0 && onlyCore.Mounts.Count == 0 && errors.Count == 0, "core alone must discover no transports");
            var empty = new TransportSystem(onlyCore, install, errors.Add);
            using (var world = new World(new(), new OpenGroundWalkability((_, _) => 0), [empty]))
                world.RunTicks(3);
            Check.That(empty.View.Vessels.Length == 0 && errors.Count == 0, "framework alone must neither create boats nor open a heightmap");

            File.WriteAllLines(files, [coreName + ".mod", sampleName + ".mod", "Broken.mod", "Workshop Canoe.mod"]);
            var packs = TransportPacks.Load(GameDatabase.Load(LoadOrder.FromInstall(install)), install, _ => { }, errors.Add).Placements;
            Check.That(errors.Count == 1 && errors[0].Contains("Broken"), "identify only the malformed pack");
            Check.That(packs.Count == 2 && packs["acceptance.dock"].Definition.Id == "acceptance.canoe",
                "discover enabled local and Workshop packs in order, rolling back incomplete registrations");
            var skiff = packs["western-hive.anchorage"];
            Check.That(skiff.X == -120780 && skiff.Z == -17640 && skiff.PlaceOnLoad, "the sample opts into the Western Hive anchorage");
            CheckOffscreenDepth(install, skiff);
            CheckPlacementSchemas(install, files, second, errors);
            string library = Path.Combine(install.ModsDirectory, sampleName, "transport.json");
            var libraryJson = JsonNode.Parse(File.ReadAllText(library))!.AsObject();
            libraryJson.Remove("placements");
            File.WriteAllText(library, libraryJson.ToJsonString());
            string dock = Path.Combine(second, "transport.json");
            var dockJson = JsonNode.Parse(File.ReadAllText(dock))!;
            dockJson["vessels"] = new JsonArray();
            dockJson["placements"]![0]!["vessel"] = skiff.Definition.Id;
            File.WriteAllText(dock, dockJson.ToJsonString());
            File.WriteAllLines(files, [coreName + ".mod", sampleName + ".mod", "Workshop Canoe.mod"]);
            errors.Clear();
            var shared = TransportPacks.Load(GameDatabase.Load(LoadOrder.FromInstall(install)), install, _ => { }, errors.Add).Placements;
            Check.That(errors.Count == 0 && shared.Count == 1 && shared["acceptance.dock"].Definition.Id == skiff.Definition.Id,
                "a placement-only data pack can launch a vessel supplied by an earlier definition-only pack");
            var mesh = OgreMeshReader.ReadFile(Path.Combine(sample, skiff.Definition.HullPath));
            var texture = DdsDecoder.Decode(DdsReader.ReadFile(Path.Combine(sample, skiff.Definition.Texture)));
            Check.That(mesh.SubMeshes.Any(m => m.Indices.Indices.Length >= 3) && mesh.Bounds is not null, "parse original skiff geometry");
            Check.That(texture.Width > 0 && texture.Height > 0, "decode original skiff texture");
            return skiff;
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void CheckOffscreenDepth(GameInstall install, TransportPlacement site)
    {
        string file = Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // A 2x2, little-endian, 16-bit whole-world TIFF with four samples at sea-bed height zero.
        using (var image = new BinaryWriter(File.Create(file)))
        {
            image.Write((ushort)0x4949);
            image.Write((ushort)42);
            image.Write(8u);
            image.Write((ushort)5);
            void Tag(ushort key, ushort type, uint value)
            {
                image.Write(key);
                image.Write(type);
                image.Write(1u);
                image.Write(value);
            }
            Tag(256, 3, 2); Tag(257, 3, 2); Tag(258, 3, 16);
            Tag(273, 4, 8 + 2 + 5 * 12 + 4); Tag(279, 4, 8);
            image.Write(0u);
            image.Write(0UL);
        }
        var errors = new List<string>();
        var system = new TransportSystem(new Dictionary<string, TransportPlacement> { [site.Id] = site }, install, errors.Add);
        // The streamed world's ground looks dry here. Only the full-map survey can place this boat.
        using var world = new World(new(), new OpenGroundWalkability((_, _) => WorldWater.Height + 50), [system]);
        world.RunTick();
        Check.That(errors.Count == 0 && system.View.Vessels.Length == 1, "automatic placement uses the installed heightmap far from the player");
    }

    private static void CheckPlacementSchemas(GameInstall install, string files, string packFolder, List<string> errors)
    {
        string path = Path.Combine(packFolder, "transport.json");
        string original = File.ReadAllText(path);
        var json = JsonNode.Parse(original)!;
        var placement = json["placements"]![0]!.AsObject();
        File.WriteAllLines(files, ["Meitou Transport.mod", "Workshop Canoe.mod"]);
        try
        {
            void Verify(int schema, bool valid, bool automatic)
            {
                json["schemaVersion"] = schema;
                File.WriteAllText(path, json.ToJsonString());
                errors.Clear();
                var result = TransportPacks.Load(GameDatabase.Load(LoadOrder.FromInstall(install)), install, _ => { }, errors.Add).Placements;
                Check.That(valid ? errors.Count == 0 && result.Single().Value.PlaceOnLoad == automatic
                                 : errors.Count == 1 && result.Count == 0, $"schema {schema} handling of {placement}");
            }

            placement.Remove("placeOnLoad");
            Verify(1, valid: true, automatic: false);
            Verify(2, valid: true, automatic: false);
            placement["placeOnLoad"] = false;
            Verify(2, valid: true, automatic: false);
            placement["placeOnLoad"] = true;
            Verify(2, valid: true, automatic: true);
            Verify(1, valid: false, automatic: false);
            placement["placeOnLoad"] = "true";
            Verify(2, valid: false, automatic: false);
        }
        finally { File.WriteAllText(path, original); }
    }

    internal static void CopyPack(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static void MakeVariant(string sample, string folder, string name, string vesselId, string placementId, bool invalid = false)
    {
        CopyPack(sample, folder);
        File.Move(Path.Combine(folder, "Western Hive Skiff.mod"), Path.Combine(folder, name + ".mod"));
        string path = Path.Combine(folder, "transport.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["packId"] = vesselId.Split('.')[0];
        var vessel = json["vessels"]![0]!;
        vessel["id"] = vesselId;
        vessel["label"] = name;
        var placement = json["placements"]![0]!;
        placement["id"] = placementId;
        placement["label"] = name + " dock";
        placement["vessel"] = invalid ? "missing.definition" : vesselId;
        File.WriteAllText(path, json.ToJsonString());
    }
}
