using System.Numerics;
using Meitou.Content;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Transport;

public sealed class TransportMod : IMod
{
    public void Initialize(ModContext context)
    {
        var install = GameInstall.Locate();
        var catalog = TransportPacks.Load(context.Data, install, context.Log, context.LogError);
        context.AddSystem("transport", _ => new TransportSystem(catalog, install, context.LogError), SystemAnchor.End);
        if (catalog.Placements.Count == 0 && catalog.Mounts.Count == 0) return;

        Presentation.Install(context.Harmony, catalog.Mounts.Count > 0);
        if (catalog.Mounts.Count > 0)
        {
            context.AddDebugCommand("Mount nearby animal", debug => Queue(debug, TransportAction.Mount));
            context.AddDebugCommand("Dismount", debug => Queue(debug, TransportAction.Dismount));
        }
        if (catalog.Placements.Count == 0) return;
        foreach (var placement in catalog.Placements.Values)
        {
            context.AddDebugCommand($"Spawn {placement.Label}", debug =>
                Queue(debug, TransportAction.Spawn, placement.Id));
        }
        context.AddDebugCommand("Board nearby vessel", debug => Queue(debug, TransportAction.Board));
        context.AddDebugCommand("Sail to camera target", debug => Queue(debug, TransportAction.Sail));
        context.AddDebugCommand("Disembark", debug => Queue(debug, TransportAction.Disembark));
    }

    private static string Queue(DebugCommandContext debug, TransportAction action, string placementId = "")
    {
        if (action != TransportAction.Spawn && debug.Selected.Count != 1)
            return "Select one squad member.";

        var character = action == TransportAction.Spawn ? CharacterId.None : debug.Selected[0];
        debug.Enqueue(new TransportCommand(placementId, action, character, debug.Target) { Tick = debug.Tick });
        return $"{action} queued.";
    }
}

public enum TransportAction { Spawn, Board, Sail, Disembark, Mount, Dismount }

public sealed record TransportCommand(string PlacementId, TransportAction Action, CharacterId Character, Vector3 Target) : SimCommand;
