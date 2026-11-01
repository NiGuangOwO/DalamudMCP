using System;
using System.Buffers.Binary;
using Dalamud.Hooking;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Application.Network;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>
/// Experimental ABI bridge for the independently inspected CN executable and installed SDK.
/// Fails closed on a different build. SDK SendPacket is authoritative for the send address;
/// the adjacent receive wrapper in the SDK's original search copy and its tail target are verified.
/// Disabled hooks are retained for reuse so Stop does not free an in-flight trampoline.
/// </summary>
internal sealed unsafe class PacketHookSource
{
    internal const uint SupportedPeTimestamp = 0x6AC3DCC1;
    internal const uint SupportedImageSize = 0x03811000;
    internal const int SendRva = 0x01D008B0;
    internal const int ReceiveRva = 0x01D00890;
    internal static readonly Guid SupportedSdkMvid = new("3eb275ee-6770-44ea-96e5-3e2bdd9e29c4");
    private readonly GameServices svc;
    private Hook<ZoneClient.Delegates.SendPacket>? send;
    private Hook<ReceiveDelegate>? receive;
    private volatile PacketCapture? capture;
    private (nint Send, nint Receive, string? Error)? validated;
    private delegate byte ReceiveDelegate(ZoneClient* client, nint output);

    internal PacketHookSource(GameServices services) => svc = services;

    internal JObject Status()
    {
        var addresses = Resolve();
        return new JObject
        {
            ["supported"] = addresses.Error == null, ["unavailableReason"] = addresses.Error,
            ["sendEnabled"] = send?.IsEnabled == true, ["receiveEnabled"] = receive?.IsEnabled == true,
            ["sdkMvid"] = typeof(ZoneClient).Assembly.ManifestModule.ModuleVersionId.ToString(),
            ["supportedSdkMvid"] = SupportedSdkMvid.ToString(),
            ["supportedPeTimestamp"] = $"0x{SupportedPeTimestamp:X8}", ["supportedImageSize"] = SupportedImageSize,
            ["sendRva"] = SendRva, ["receiveRva"] = ReceiveRva,
            ["sendLayout"] = NativePacketReader.SendLayout, ["sendHeaderBytes"] = 32,
            ["receiveLayout"] = NativePacketReader.ReceiveLayout, ["receiveHeaderBytes"] = 16,
            ["receiveLengthSource"] = "zone_dequeue_descriptor_size_u64",
            ["sendLengthSource"] = "send_call_ipc_size_u64_plus_16",
            ["abiCodeSource"] = svc.SigScanner?.IsCopy == true ? "sdk_original_search_copy" : "live_entry",
            ["liveTrafficValidated"] = false,
        };
    }

    private (nint Send, nint Receive, string? Error) Resolve()
    {
        if ((send != null || receive != null) && validated is { } known) return known;
        try
        {
            if (typeof(ZoneClient).Assembly.ManifestModule.ModuleVersionId != SupportedSdkMvid)
                return (0, 0, "Unsupported FFXIVClientStructs build; native ABI must be revalidated");
            var module = (ulong)svc.SigScanner.Module.BaseAddress;
            var dos = MemoryProbe.TryReadBytes(module, 64, out _);
            if (dos?.Length != 64 || dos[0] != 'M' || dos[1] != 'Z') return (0, 0, "Game PE header unavailable");
            var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(60));
            if (peOffset is < 64 or > 4096) return (0, 0, "Unexpected game PE offset");
            var pe = MemoryProbe.TryReadBytes(module + peOffset, 84, out _);
            if (pe?.Length != 84 || BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550 ||
                BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)) != 0x8664 ||
                BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(8)) != SupportedPeTimestamp ||
                BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(80)) != SupportedImageSize)
                return (0, 0, "Unsupported game executable; capture is restricted to the inspected CN PE build");
            var sendAddress = (nint)ZoneClient.MemberFunctionPointers.SendPacket;
            if ((ulong)sendAddress != module + SendRva) return (0, 0, "SDK send address differs from inspected game build");
            var receiveAddress = (nint)(module + ReceiveRva);
            // The SDK keeps an original module copy specifically for signature/ABI discovery.
            // Existing Dalamud hooks can patch the live entry; let the SDK hook backend manage
            // chaining instead of following or rewriting another hook's trampoline ourselves.
            var codeBase = svc.SigScanner.IsCopy ? (ulong)svc.SigScanner.SearchBase : module;
            if (codeBase == 0 || codeBase > ulong.MaxValue - SupportedImageSize)
                return (0, 0, "Original SDK search image is unavailable");
            var wrapper = MemoryProbe.TryReadBytes(codeBase + ReceiveRva, 19, out _);
            if (wrapper?.Length != 19 || !NativePacketReader.ValidateReceiveWrapper(wrapper))
                return (0, 0, "Zone dequeue wrapper differs from inspected ABI in the SDK search image");
            var tail = (ulong)((long)receiveAddress + 16 + BinaryPrimitives.ReadInt32LittleEndian(wrapper.AsSpan(12)));
            if (tail != module + 0x1D70230 || !Executable(tail) || !Executable((ulong)sendAddress) || !Executable((ulong)receiveAddress))
                return (0, 0, "Zone transport function targets are not the inspected executable functions");
            validated = (sendAddress, receiveAddress, null);
            return validated.Value;
        }
        catch (Exception ex) { return (0, 0, "Native capture unavailable: " + ex.GetType().Name); }
    }

    private static bool Executable(ulong address) => MemoryProbe.Query(address) is { Executable: true, Readable: true, Guarded: false, Committed: true };

    internal void Start(PacketCapture next)
    {
        // Existing disabled hooks can have patched entry bytes, so validate once before allocation.
        if (send == null && receive == null)
        {
            var address = Resolve();
            if (address.Error != null) throw new ToolException(address.Error);
            try
            {
                send = svc.Interop.HookFromAddress<ZoneClient.Delegates.SendPacket>(address.Send, Send);
                receive = svc.Interop.HookFromAddress<ReceiveDelegate>(address.Receive, Receive);
            }
            catch { Dispose(); throw; }
        }
        capture = next;
        try
        {
            if (next.Direction is "both" or "receive") receive!.Enable();
            if (next.Direction is "both" or "send") send!.Enable();
        }
        catch { Stop(); throw; }
    }

    private bool Send(ZoneClient* client, nint packet, uint a3, uint a4, bool a5)
    {
        var current = capture;
        try
        {
            if (current?.Active == true && svc.Config.AllowMutatingTools)
                NativePacketReader.Send(current, (ulong)packet);
        }
        catch (Exception) { current?.Invalid(); }
        return send!.Original(client, packet, a3, a4, a5);
    }

    private byte Receive(ZoneClient* client, nint output)
    {
        // The dequeue result is owned by the caller until its subsequent dispatch/free.
        var result = receive!.Original(client, output);
        var current = capture;
        try
        {
            if (result != 0 && current?.Active == true && svc.Config.AllowMutatingTools)
                NativePacketReader.Receive(current, (ulong)output);
        }
        catch (Exception) { current?.Invalid(); }
        return result;
    }

    internal void Stop()
    {
        capture = null;
        if (send?.IsEnabled == true) send.Disable();
        if (receive?.IsEnabled == true) receive.Disable();
    }

    internal void Dispose()
    {
        Stop();
        send?.Dispose(); receive?.Dispose(); send = null; receive = null;
        validated = null;
    }
}
