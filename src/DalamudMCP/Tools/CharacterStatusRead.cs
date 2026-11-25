using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DalamudMCP.Tools;

/// <summary>Copies a live character's optional status manager on the framework thread.</summary>
internal static unsafe class CharacterStatusRead
{
    public static StatusManager? Read(nint address)
    {
        if (address == 0)
            return null;

        var character = (Character*)address;
        if (character->VirtualTable == null || character->VirtualTable->GetStatusManager == null)
            return null;

        var manager = character->GetStatusManager();
        return manager == null ? null : MemoryProbe.TryReadStruct<StatusManager>((ulong)manager, out _);
    }
}
