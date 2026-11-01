using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label) { try { action(); } catch (ToolException) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
PacketCapture New(string direction = "both", ushort[]? opcodes = null, int maxPackets = 1000, int maxBytes = 512) => new(direction, opcodes ?? [], 120, maxPackets, maxBytes);

Reject(() => new PacketCapture("all", [], 10, 100, 512), "unknown direction rejected");
Reject(() => new PacketCapture("both", [], 0, 100, 512), "unbounded duration rejected");
Reject(() => new PacketCapture("both", [], 121, 100, 512), "duration ceiling enforced");
Reject(() => new PacketCapture("both", [], 10, 4097, 512), "packet ceiling enforced");
Reject(() => new PacketCapture("both", [], 10, 100, 4097), "per-packet byte ceiling enforced");
Reject(() => new PacketCapture("both", Enumerable.Range(0, 65).Select(x => (ushort)x), 10, 100, 512), "opcode filter ceiling enforced");
foreach (var token in new JToken[] { "1", 1.0, true, JValue.CreateNull(), JToken.Parse("9999999999999999999999999999999") })
    Reject(() => PacketAnalysis.Int(new JObject { ["count"] = token }, "count", 30, 1, 100), "strict bounded integer " + token.Type);
Reject(() => PacketAnalysis.Opcodes(JObject.Parse("{\"opcodes\":[65536]}")), "opcode range enforced");
Reject(() => PacketAnalysis.Opcodes(JObject.Parse("{\"opcodes\":[\"12\"]}")), "opcode strings rejected");
Reject(() => PacketAnalysis.Text(JObject.Parse("{\"sessionId\":null}"), "sessionId"), "null session rejected");
Check(PacketAnalysis.Opcodes(JObject.Parse("{\"opcodes\":[0,65535,0]}" )).SequenceEqual(new ushort[]{0,65535}), "opcode zero supported and duplicate filters collapsed");
var wrapperCode = Convert.FromHexString("488B89980000004885C90F8590F9060032C0C3");
Check(NativePacketReader.ValidateReceiveWrapper(wrapperCode), "inspected dequeue instruction shape accepted");
wrapperCode[3] = 0x90;
Check(!NativePacketReader.ValidateReceiveWrapper(wrapperCode), "different native connection offset refused");
Check(!NativePacketReader.ValidateReceiveWrapper(wrapperCode.AsSpan(1)), "partial native function refused");

using var native = new NativePages();
var payload = native.Address + 128;
var source = Enumerable.Range(0, 48).Select(x => (byte)x).ToArray();
BinaryPrimitives.WriteUInt32LittleEndian(source, 0x1234);
BinaryPrimitives.WriteUInt64LittleEndian(source.AsSpan(8), 32); // 16 IPC header + 16 payload -> 32 call header + 16 payload
Marshal.Copy(source, 0, payload, source.Length);
var send = New();
Check(NativePacketReader.Send(send, (ulong)payload), "sender reads descriptor length");
Check(send.Get(1).Data.Length == 48 && send.Get(1).DeclaredLength == 48 && send.Get(1).Opcode == 0x1234, "send range uses IPC size plus 16, not plus 32");
Marshal.WriteByte(payload + 32, 255);
Check(send.Get(1).Data[32] == 32, "native data copied before buffer reuse");
var truncated = New(maxBytes:32);
NativePacketReader.Send(truncated, (ulong)payload);
Check(truncated.Get(1).Metadata().Value<bool>("truncated") && truncated.Get(1).Data.Length == 32, "per-packet truncation explicit");
var filtered = New("receive");
Check(!NativePacketReader.Send(filtered, (ulong)payload) && filtered.Snapshot().Length == 0 && filtered.Status().Value<int>("filteredPackets") == 1, "direction filter precedes payload copy");
filtered = New(opcodes:[0x2345]);
Check(!NativePacketReader.Send(filtered, (ulong)payload) && filtered.Status().Value<int>("filteredPackets") == 1, "opcode allowlist precedes payload copy");

var wrapper = native.Address + 512;
var descriptor = native.Address + 640;
var received = native.Address + 768;
Marshal.WriteInt64(wrapper + 16, descriptor.ToInt64());
Marshal.WriteInt32(descriptor + 32, unchecked((int)0xABCD1234));
Marshal.WriteInt64(descriptor + 48, 24);
Marshal.WriteInt64(descriptor + 56, received.ToInt64());
Marshal.WriteInt16(received + 2, unchecked((short)0xFEED));
Marshal.WriteInt64(received + 16, 12345678);
var receive = New();
Check(NativePacketReader.Receive(receive, (ulong)wrapper), "receiver follows dequeue wrapper to owned descriptor");
Check(receive.Get(1).Data.Length == 24 && receive.Get(1).TargetId == 0xABCD1234 && receive.Get(1).Opcode == 0xFEED, "receive uses actual descriptor length and IPC opcode");
Check(receive.Get(1).Layout == NativePacketReader.ReceiveLayout, "send and receive header layouts distinguished");
Marshal.WriteInt64(descriptor + 48, 0);
Check(!NativePacketReader.Receive(receive, (ulong)wrapper), "zero receive length refused without fallback");
Marshal.WriteInt64(descriptor + 48, 1024 * 1024 + 1);
Check(!NativePacketReader.Receive(receive, (ulong)wrapper), "oversized receive length refused");
Marshal.WriteInt64(descriptor + 48, -1);
Check(!NativePacketReader.Receive(receive, (ulong)wrapper), "wrapped receive length refused");
Check(!NativePacketReader.Receive(receive, 0) && !NativePacketReader.Send(send, 1), "null and unmapped native pointers refused");
Check(!NativePacketReader.Send(send, ulong.MaxValue - 5), "address overflow refused");

// Place each valid structure immediately before a PAGE_NOACCESS page. Any excess read
// either causes an isolated-process failure or must be explicitly refused by the real reader.
var boundary = native.Address + native.PageSize;
var edgeSend = boundary - 32;
var edgeBytes = new byte[32];
BinaryPrimitives.WriteUInt32LittleEndian(edgeBytes, 5);
BinaryPrimitives.WriteUInt64LittleEndian(edgeBytes.AsSpan(8), 16);
Marshal.Copy(edgeBytes, 0, edgeSend, 32);
var edge = New();
Check(NativePacketReader.Send(edge, (ulong)edgeSend) && edge.Get(1).Data.Length == 32, "exact send range succeeds against no-access boundary");
Marshal.WriteInt64(edgeSend + 8, 17);
Check(!NativePacketReader.Send(edge, (ulong)edgeSend) && edge.Snapshot().Length == 1, "one extra byte rejected; clamped read never treated as complete");
Check(!NativePacketReader.Send(edge, (ulong)(boundary - 31)), "short native header refused before field access");
Marshal.WriteInt64(descriptor + 48, 16);
Marshal.WriteInt64(descriptor + 56, (boundary - 16).ToInt64());
Marshal.WriteInt16(boundary - 14, 123);
Check(NativePacketReader.Receive(receive, (ulong)wrapper), "exact receive range succeeds against no-access boundary");
Marshal.WriteInt64(descriptor + 48, 17);
Check(!NativePacketReader.Receive(receive, (ulong)wrapper), "receive range cannot cross native allocation boundary");
Marshal.WriteInt64(wrapper + 16, (boundary - 63).ToInt64());
Check(!NativePacketReader.Receive(receive, (ulong)wrapper), "short descriptor refused before unmarshalling");

var packet = new CapturedPacket(1, DateTimeOffset.UtcNow, "receive", 5, 1, 80, Enumerable.Range(0, 64).Select(x => (byte)x).ToArray(), NativePacketReader.ReceiveLayout);
var read = PacketAnalysis.Read(packet, 16, 18);
Check(read.Value<int>("count") == 18 && read.Value<int>("nextOffset") == 34 && (int)read["rows"]![0]!["offset"]! == 16, "hex paging preserves absolute offsets");
Check(PacketAnalysis.Read(packet, 64, 256).Value<int>("count") == 0, "end-of-capture page empty");
Reject(() => PacketAnalysis.Read(packet, 65, 1), "read past captured range refused");
var decode = PacketAnalysis.Decode(packet, JObject.Parse("""
{"baseOffset":16,"fields":[{"name":"a","type":"u16","offset":0},{"name":"b","type":"u16","offset":0,"endian":"big"},{"name":"tail","type":"u32","offset":47},{"name":"array","type":"u8","offset":0,"count":3},{"name":"wide","type":"u64","offset":0}]}
"""));
Check((int)decode["fields"]![0]!["value"]! == 0x1110 && (int)decode["fields"]![1]!["value"]! == 0x1011, "little and big endian decoding");
Check(!(bool)decode["fields"]![2]!["available"]!, "truncated fields explicit, never padded");
Check(decode["fields"]![3]!["value"] is JArray {Count:3}, "scalar arrays decoded");
Check(decode["fields"]![4]!["value"]!.Type == JTokenType.String && !decode.Value<bool>("semanticsVerified"), "64-bit values lossless; semantics unverified");
foreach (var field in new[]{"{\"name\":\"x\",\"type\":\"pointer\",\"offset\":0}","{\"name\":\"x\",\"type\":\"u8\"}","{\"name\":\"x\",\"type\":\"u8\",\"offset\":-1}","{\"name\":\"x\",\"type\":\"u8\",\"offset\":0,\"count\":257}"})
    Reject(()=>PacketAnalysis.Decode(packet, new JObject { ["fields"] = new JArray(JObject.Parse(field)) }), "invalid layout rejected " + field);
var many = new JArray(Enumerable.Range(0, 3).Select(i=>JObject.Parse("{\"name\":\"x\",\"type\":\"u64\",\"offset\":0,\"count\":256}")));
Reject(()=>PacketAnalysis.Decode(packet, new JObject { ["fields"] = many }), "decoded output budget enforced even outside captured range");
var invalidText = packet with {Data = [0xFF,0xFF]};
var invalidDecoded = PacketAnalysis.Decode(invalidText,JObject.Parse("{\"fields\":[{\"name\":\"x\",\"type\":\"utf8\",\"offset\":0,\"count\":2}]}"));
Check((string?)invalidDecoded["fields"]![0]!["reason"] == "invalid_utf8", "invalid UTF-8 explicit");
var floatPacket = packet with { Data = [0,0,0x80,0x7F] };
Check(PacketAnalysis.Decode(floatPacket,JObject.Parse("{\"fields\":[{\"name\":\"x\",\"type\":\"f32\",\"offset\":0}]}"))["fields"]![0]!["value"]!.Type == JTokenType.String, "nonfinite floats serialize as strings");
var right = packet with {Id = 2, Data = [0,1,4]};
var compared = PacketAnalysis.Compare(packet, right, 0, 4);
Check(((JArray)compared["differences"]!).Count == 2 && compared["differences"]![1]!["right"]!.Type == JTokenType.Null, "comparison distinguishes missing byte from zero");
Check(compared.Value<string>("comparisonScope") == "captured_bytes_only" && compared.Value<int>("nextOffset") == 4, "comparison scope and pagination explicit");

var limited = New(maxPackets:2);
limited.Add("send",1,null,1,[1],"test"); limited.Add("send",1,null,1,[2],"test"); limited.Add("send",1,null,1,[3],"test");
Check(!limited.Active && limited.Snapshot().Length == 2 && limited.Status().Value<string>("stopReason") == "packet_limit", "packet limit automatically ends capture");
limited.Stop("requested");
Check(limited.Status().Value<string>("stopReason") == "packet_limit", "first stop reason preserved");
var bytesLimited = New(maxPackets:4096,maxBytes:4096);
for(var i=0; i<2050; i++) bytesLimited.Add("send",1,null,4096,new byte[4096],"test");
Check(!bytesLimited.Active && bytesLimited.Status().Value<string>("stopReason") == "byte_limit" && bytesLimited.Status().Value<int>("capturedBytes") == PacketCapture.ByteBudget, "total byte ceiling enforced");
var concurrent = New(maxPackets:4096);
Parallel.For(0,10000,i=> { if(concurrent.Accepts("send",1)) concurrent.Add("send",1,null,1,[(byte)i],"test"); if(i%100==0) concurrent.Snapshot(); });
var ids = concurrent.Snapshot().Select(p=>p.Id).ToArray();
Check(ids.SequenceEqual(Enumerable.Range(1,ids.Length)) && ids.Length <= 4096, "concurrent capture preserves stable sequential packet ids");
var timed = new PacketCapture("both",[],1,100,32);
await Task.Delay(1100);
var cleanupCalls = 0;
var timedStatus = timed.Status(() => cleanupCalls++);
Check(!timedStatus.Value<bool>("active") && timedStatus.Value<string>("stopReason") == "timeout", "monotonic deadline ends capture without client polling");
Check(cleanupCalls == 1, "first deadline snapshot reconciles inactive hooks before returning without a framework tick");
New().Status(() => cleanupCalls++);
Check(cleanupCalls == 1, "active snapshot does not disable running hooks");
limited.Status(() => cleanupCalls++);
Check(cleanupCalls == 2, "capacity-ended snapshot also reconciles hooks");
checks += await OpcodeTests.Run();
checks += SchemaTests.Run();
checks += await SchemaUpdateTests.Run();
Console.WriteLine($"{checks} checks passed");

sealed class NativePages : IDisposable
{
    public readonly int PageSize = Environment.SystemPageSize;
    public readonly nint Address;
    public NativePages()
    {
        Address=VirtualAlloc(0,(nuint)(PageSize*2),0x3000,0x04);
        if(Address==0 || !VirtualProtect(Address+PageSize,(nuint)PageSize,0x01,out _)) throw new Exception("Cannot allocate guard pages");
    }
    public void Dispose()=>VirtualFree(Address,0,0x8000);
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint p,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint p,nuint size,uint protection,out uint old);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint p,nuint size,uint type);
}
