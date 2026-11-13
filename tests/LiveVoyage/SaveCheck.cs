using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Save;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Transport.Verification;

/// <summary>Queues the engine's named-folder save/load requests and checks the exact pose captured at the save boundary.</summary>
internal sealed class SaveCheck
{
    private readonly object saves;
    private readonly FieldInfo loadRequest, activeSlot, writing;
    private readonly string name, record;
    private readonly string? placementId, craftId;
    private readonly uint portrait;
    public string Directory { get; }
    public Vector3 Origin { get; private set; }
    public float Yaw { get; private set; }
    public long? FirstRestoredTick { get; set; }

    public SaveCheck(object host, CharacterSnapshot actor, string? placementId = null, string? craftId = null)
    {
        saves = AccessTools.Field(host.GetType(), "Saves").GetValue(host)!;
        var type = saves.GetType();
        var saveRequest = AccessTools.Field(type, "SaveRequestedFolder");
        loadRequest = AccessTools.Field(type, "LoadRequestedFolder");
        activeSlot = AccessTools.Field(type, "SaveInUse");
        writing = AccessTools.Field(type, "SaveWrite");
        if (saveRequest.GetValue(saves) is not null || loadRequest.GetValue(saves) is not null)
            throw new InvalidOperationException("A native save/load request is already pending.");
        Directory = Path.GetFullPath(Path.Combine("live-voyage-saves", $"{(placementId is null ? "Ride" : "Western-Hive")}-{Guid.NewGuid():N}"));
        if (System.IO.Directory.Exists(Directory) || File.Exists(Directory))
            throw new IOException($"QA save path already exists: {Directory}");
        (name, record, portrait) = (actor.Name, actor.RecordId, actor.PortraitSerial);
        this.placementId = placementId;
        this.craftId = craftId;
        saveRequest.SetValue(saves, Directory);
    }

    public bool BeginLoadWhenWritten()
    {
        if (!Equals(activeSlot.GetValue(saves), Directory) || writing.GetValue(saves) is not Task { IsCompleted: true } task)
            return false;
        task.GetAwaiter().GetResult();
        if (!File.Exists(Path.Combine(Directory, SaveFolder.QuickSaveName)))
            throw new InvalidDataException("The QA slot is missing the native quick.save file.");
        if (placementId is not null) (Origin, Yaw) = ReadVessel(Directory, placementId, craftId!);
        loadRequest.SetValue(saves, Directory);
        return true;
    }

    internal static (Vector3, float) ReadVessel(string directory, string placementId, string craftId)
    {
        byte[] sidecar = File.ReadAllBytes(Path.Combine(directory, SaveFolder.ModDataName("meitou.transport")));
        var reader = typeof(IMod).Assembly.GetType("Meitou.Modding.ModSaveFile", true)!;
        var systems = (Dictionary<string, byte[]>)AccessTools.Method(reader, "Read").Invoke(null, [sidecar])!;
        using var stream = new BinaryReader(new MemoryStream(systems["transport"]));
        if (stream.ReadInt32() != 1) throw new InvalidDataException("Unexpected transport save version.");
        if (stream.ReadInt32() != 1 || stream.ReadString() != placementId || stream.ReadString() != craftId)
            throw new InvalidDataException("Unexpected vessel in the QA slot.");
        return (new Vector3(stream.ReadSingle(), 100, stream.ReadSingle()), stream.ReadSingle());
    }

    public bool IsOriginalRider(CharacterSnapshot c) => c.IsPlayer && c.Name == name && c.RecordId == record && c.PortraitSerial == portrait;
    public bool AtSavedPose(Vector3 at, Quaternion rotation) => Vector3.Distance(at, Origin) < 0.05f
        && MathF.Abs(Quaternion.Dot(rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw))) > 0.99999f;
}
