using System;
using System.Buffers.Binary;

namespace DalamudMCP.Network;

/// <summary>
/// Copies bounded native packet storage using the validated zone transport contract.
/// Send: call header 32, IPC-size u64 at +8 includes IPC header 16; body starts at +32.
/// Receive: dequeue result +16 -> descriptor; descriptor +48 = IPC size, +56 = data.
/// There is no pointer subtraction and no fallback packet length.
/// </summary>
internal static class NativePacketReader
{
    internal const int MaxDeclaredLength = 1024 * 1024;
    internal const string SendLayout = "zone_send_call_header32";
    internal const string ReceiveLayout = "zone_received_ipc_header16";

    internal static bool ValidateReceiveWrapper(ReadOnlySpan<byte> bytes) => bytes.Length == 19 &&
        bytes[..12].SequenceEqual(new byte[] { 0x48, 0x8B, 0x89, 0x98, 0, 0, 0, 0x48, 0x85, 0xC9, 0x0F, 0x85 }) &&
        bytes[16..].SequenceEqual(new byte[] { 0x32, 0xC0, 0xC3 });

    internal static bool Send(PacketCapture capture, ulong address)
    {
        var header = Exact(address, 32);
        if (header == null) { capture.Invalid(); return false; }
        var opcode32 = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var ipcLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        if (opcode32 > ushort.MaxValue || ipcLength is < 16 or > MaxDeclaredLength - 16)
        { capture.Invalid(); return false; }
        var opcode = (ushort)opcode32;
        if (!capture.Accepts("send", opcode)) return false;
        var length = checked((int)ipcLength + 16);
        return Copy(capture, "send", opcode, null, length, address, SendLayout);
    }

    internal static bool Receive(PacketCapture capture, ulong outputAddress)
    {
        var wrapper = Exact(outputAddress, 24);
        if (wrapper == null) { capture.Invalid(); return false; }
        var descriptorAddress = BinaryPrimitives.ReadUInt64LittleEndian(wrapper.AsSpan(16));
        var descriptor = Exact(descriptorAddress, 64);
        if (descriptor == null) { capture.Invalid(); return false; }
        var length = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(48));
        var dataAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(56));
        if (length is < 16 or > MaxDeclaredLength) { capture.Invalid(); return false; }
        var header = Exact(dataAddress, 16);
        if (header == null) { capture.Invalid(); return false; }
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        if (!capture.Accepts("receive", opcode)) return false;
        var target = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(32));
        return Copy(capture, "receive", opcode, target, (int)length, dataAddress, ReceiveLayout);
    }

    private static bool Copy(PacketCapture capture, string direction, ushort opcode, uint? target, int length, ulong address, string layout)
    {
        var data = Exact(address, Math.Min(length, capture.MaxBytesPerPacket));
        if (data == null) { capture.Invalid(); return false; }
        capture.Add(direction, opcode, target, length, data, layout);
        return true;
    }

    private static byte[]? Exact(ulong address, int length)
    {
        if (address == 0 || address > ulong.MaxValue - (ulong)length) return null;
        var data = MemoryProbe.TryReadBytes(address, length, out _);
        return data?.Length == length ? data : null;
    }
}
