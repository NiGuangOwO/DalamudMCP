using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DalamudMCP.Tools;

/// <summary>Copies a live character's optional cast state on the framework thread.</summary>
internal static unsafe class CharacterCastRead
{
    public static CastInfo? Read(nint address)
    {
        if (address == 0)
            return null;

        var character = (Character*)address;
        if (character->VirtualTable == null || character->VirtualTable->GetCastInfo == null)
            return null;

        var cast = character->GetCastInfo();
        return cast == null ? null : MemoryProbe.TryReadStruct<CastInfo>((ulong)cast, out _);
    }
}
