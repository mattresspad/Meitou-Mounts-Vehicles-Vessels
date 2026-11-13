using Meitou.Data.Fcs;

// Rebuild the original, data-only FCS scenario. Run from the repository root.
const string mod = "Stoat Desert Ride.mod";
var file = new FcsFile
{
    Author = "Meitou Transport contributors",
    Description = "A Scorchlander rider, an adult Garru and a Domesticated Bull in the dunes outside Stoat.",
    NextId = 4
};
file.Dependencies.AddRange(["Meitou Transport.mod", "Garru Saddle.mod"]);
FcsRecord Record(int id, FcsRecordType type, string name)
{
    var record = new FcsRecord { ByteSize = 1, RecordType = type, Name = name, StringId = $"{id}-{mod}" };
    file.Records.Add(record);
    return record;
}

const string scorchlander = "18019-gamedata.base";
var rider = Record(1, FcsRecordType.Character, "Sable");
rider.Ints["female chance"] = 100;
rider.References["race"] = [new(scorchlander, 100, 0, 0)];
rider.References["clothing"] =
[
    new("557-gamedata.base", 1, 100, 0),       // Wooden Sandals
    new("51663-Newwworld.mod", 1, 100, 0),     // Noble's Robe
    new("51662-Newwworld.mod", 1, 100, 0),     // Noble's Trousers
    new("2168-gamedata.base", 1, 100, 0)       // Square Goggles
];

var squad = Record(2, FcsRecordType.SquadTemplate, "Stoat riding party");
squad.Bools["dont multiply"] = true;
squad.Floats["animal age random"] = 0;
squad.References["leader"] = [new(rider.StringId, 1, 0, 0)];
squad.References["animals"] = [new("42201-rebirth.mod", 1, 1, 110)];
squad.References["animals2"] = [new("43949-rebirth.mod", 1, 1, 110)];

var start = Record(3, FcsRecordType.NewGameStartoff, "Stoat Desert Ride");
start.Bools["force start pos"] = true;
start.Ints["start pos X"] = 34595;
start.Ints["start pos Z"] = -70128;
start.Strings["description"] = "The morning road is yours. An adult Garru and a Domesticated Bull wait with you outside Stoat.";
start.Strings["style"] = "Desert ride";
start.References["squad"] = [new(squad.StringId, 1, 0, 0)];
start.References["force race"] = [new(scorchlander, 100, 0, 0)];

var folder = Path.Combine("mods", "Stoat Desert Ride");
if (!File.Exists(Path.Combine(folder, "transport.json")))
    throw new DirectoryNotFoundException("Run the generator from the Meitou Transport repository root.");
FcsWriter.WriteFile(file, Path.Combine(folder, mod));
