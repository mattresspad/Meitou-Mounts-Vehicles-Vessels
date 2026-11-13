using System.Text.Json.Nodes;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Transport;

internal static class MountPackChecks
{
    internal static void WriteAnimalData(string folder)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(folder)!, "resources.cfg"), "[General]\nFileSystem=./data\n");
        // A record pair and a one-bone Ogre rig keep parser/override tests independent of the licensed install.
        var file = new FcsFile();
        var race = new FcsRecord { Type = (int)FcsRecordType.Race, StringId = "test-race", Name = "Fixture race" };
        race.Filenames["male mesh"] = "data/test.mount.mesh";
        file.Records.Add(race);
        foreach (string id in new[] { "42201-rebirth.mod", "3999-gamedata.base" })
        {
            var animal = new FcsRecord { Type = (int)FcsRecordType.AnimalCharacter, StringId = id, Name = id };
            animal.References["race"] = [new(race.StringId, 1, 0, 0)];
            file.Records.Add(animal);
        }
        FcsWriter.WriteFile(file, Path.Combine(folder, "gamedata.base"));
        using (var mesh = new BinaryWriter(File.Create(Path.Combine(folder, "test.mount.mesh"))))
        {
            var link = "test.mount.skeleton\n"u8;
            mesh.Write((ushort)0x1000); mesh.Write("[MeshSerializer_v1.100]\n"u8);
            mesh.Write((ushort)0x3000); mesh.Write(13 + link.Length); mesh.Write(true);
            mesh.Write((ushort)0x6000); mesh.Write(6 + link.Length); mesh.Write(link);
        }
        using var rig = new BinaryWriter(File.Create(Path.Combine(folder, "test.mount.skeleton")));
        rig.Write((ushort)0x1000); rig.Write("[Serializer_v1.80]\n"u8);
        rig.Write((ushort)0x2000); rig.Write(OgreSkeletonReader.BoneLengthWithoutScale); rig.Write("Bip01 Spine1\n"u8);
        rig.Write((ushort)0); rig.Write(new byte[24]); rig.Write(1f);
    }

    internal static void Run(string mods, GameInstall install)
    {
        const string name = "Garru Saddle";
        var folder = Path.Combine(install.ModsDirectory, name);
        PackChecks.CopyPack(Path.Combine(mods, name), folder);
        Check.That(FcsReader.ReadFile(Path.Combine(folder, name + ".mod")).Dependencies.SequenceEqual(["Meitou Transport.mod"])
            && !Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Any(), "data-only saddle and FCS dependency");
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "transport.json")))!;
        var sample = original["mounts"]![0]!;
        var art = OgreMeshReader.ReadFile(Path.Combine(folder, (string)sample["saddleMesh"]!));
        Check.That(art.SubMeshes.Count == 1 && art.Bounds!.Value.Radius > 4, "original saddle geometry loads");
        var texture = DdsDecoder.Decode(DdsReader.ReadFile(Path.Combine(folder, (string)sample["texture"]!)));
        Check.That(texture.Width > 0 && texture.Height > 0, "original saddle diffuse texture decodes");
        var patch = Path.Combine(install.ModsDirectory, "Later");
        Directory.CreateDirectory(patch);
        File.Copy(Path.Combine(folder, name + ".mod"), Path.Combine(patch, "Later.mod"));
        var catalog = original.DeepClone();
        catalog["packId"] = "later";
        var fit = catalog["mounts"]![0]!.AsObject();
        fit["id"] = "later.fit";
        fit.Remove("saddleMesh"); fit.Remove("texture");
        fit["animalRecordIds"] = new JsonArray("3999-gamedata.base");
        catalog["placements"] = new JsonArray(new JsonObject
        {
            ["id"] = "later.mooring",
            ["label"] = "Later mooring",
            ["vessel"] = "western-hive.skiff",
            ["x"] = 0,
            ["z"] = 0,
            ["headingDegrees"] = 0,
            ["placeOnLoad"] = true
        });
        var errors = new List<string>(); var logs = new List<string>();
        File.WriteAllLines(Path.Combine(install.DataDirectory, "mods.cfg"), ["Meitou Transport.mod", name + ".mod", "Western Hive Skiff.mod", "Later.mod"]);
        TransportCatalog Read()
        {
            errors.Clear(); logs.Clear();
            File.WriteAllText(Path.Combine(patch, "transport.json"), catalog.ToJsonString());
            return TransportPacks.Load(GameDatabase.Load(LoadOrder.FromInstall(install)), install, logs.Add, errors.Add);
        }
        var loaded = Read();
        Check.That(errors.Count == 0 && loaded.Mounts.Count == 2 && loaded.Placements.Count == 2 && loaded.Placements["later.mooring"].PlaceOnLoad &&
            loaded.Mounts["3999-gamedata.base"].Id == "later.fit" && loaded.Mounts["42201-rebirth.mod"].Id == "garru-saddle.trail"
            && logs.Any(s => s.Contains("Later.mod") && s.Contains("Garru Saddle.mod") && s.Contains("later.fit") && s.Contains("garru-saddle.trail")),
            "mixed schema 3 placement and mount fit; override log identifies both FCS files: " + string.Join(" / ", errors));
        fit["anchorBone"] = "Missing saddle hook";
        loaded = Read();
        Check.That(errors.Single().Contains("Missing saddle hook") && errors[0].Contains("Later") &&
            loaded.Mounts.Values.All(f => f.Id == "garru-saddle.trail") && !loaded.Placements.ContainsKey("later.mooring"),
            "a bad bone reports its pack, rolls back its placement, and preserves the previous fit");
        fit["anchorBone"] = "Bip01 Spine1";
        catalog["schemaVersion"] = 2;
        Check.That(Read().Mounts.Count == 2 && errors.Count == 1, "old schemas cannot declare mount fits");
    }
}
