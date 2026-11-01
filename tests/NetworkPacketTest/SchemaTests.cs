using System.Buffers.Binary;
using System.Text;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

internal static class SchemaTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
        void Reject(Action action, string label) { try { action(); } catch (ToolException) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        var schemas = PacketSchemas.Bundled.Value;
        using var resource = typeof(SchemaTests).Assembly.GetManifestResourceStream("DalamudMCP.opcodes.min.json")!;
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        var mapping = OpcodeDataset.Parse(buffer.ToArray(), "test").ForRegion("CN");
        CapturedPacket Packet(string direction, ushort opcode, byte[] payload, int? captured = null, int? declared = null, string? layout = null)
        {
            var header = direction == "send" ? 32 : 16;
            var data = new byte[header + payload.Length]; payload.CopyTo(data, header);
            if (captured.HasValue) data = data[..captured.Value];
            return new CapturedPacket(1, DateTimeOffset.UtcNow, direction, opcode, null, declared ?? header + payload.Length,
                data, layout ?? (direction == "send" ? NativePacketReader.SendLayout : NativePacketReader.ReceiveLayout), mapping);
        }
        JObject Parse(CapturedPacket p, string? name = null, int cursor = 0, int count = 128)
        {
            var args = new JObject { ["cursor"] = cursor, ["count"] = count };
            if (name != null) args["schemaName"] = name;
            return schemas.Parse(p, args);
        }
        JObject Field(JObject r, string name) => ((JArray)r["fields"]!).OfType<JObject>().Single(f => f.Value<string>("name") == name);
        var summary = schemas.Summary();
        Check(summary.Value<int>("packetSchemas") == 177 && summary.Value<int>("structures") == 218 && summary.Value<int>("zoneMappings") == 170, "pinned packet catalog has 177 schemas, 218 reachable structures, 170 directional mappings");
        Check(((JArray)summary["metadataUnavailable"]!).Count == 7 && summary.Value<string>("versionMatch") == "unverified", "seven missing catalog entries stay unavailable, compatibility unverified");
        var payload = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, ulong.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 4567);
        var yell = Parse(Packet("receive", 101, payload));
        Check(yell.Value<string>("status") == "decoded_unverified" && yell.Value<int>("payloadOffset") == 16, "automatic receive selects NpcYell and skips 16-byte header");
        Check(Field(yell,"sourceActorId").Value<string>("value") == "18446744073709551615" && Field(yell,"messageId").Value<int>("value") == 4567, "NpcYell uint64 and messageId use independently placed wire offsets");
        Check(Field(yell,"messageId").Value<int>("offset") == 28 && !yell.Value<bool>("semanticsVerified"), "absolute and payload offsets remain explicit without semantics claims");
        var send = new byte[32]; BinaryPrimitives.WriteUInt16LittleEndian(send, 701);
        BinaryPrimitives.WriteUInt64LittleEndian(send.AsSpan(24), ulong.MaxValue - 1);
        var trigger = Parse(Packet("send",872,send));
        Check(trigger["schema"]!.Value<string>("name") == "ClientTrigger" && trigger.Value<int>("payloadOffset") == 32, "same opcode send selects ClientTrigger with 32-byte header");
        Check(Field(trigger,"param6").Value<string>("value") == "18446744073709551614" && ((JArray)Field(trigger,"commandId")["enumNames"]!).Values<string>().Contains("FishingBaitChange"), "send uint64 and command enum retain exact values");
        Check(Field(trigger,"param1")["condition"] != null && !trigger.Value<bool>("conditionalDisplayApplied"), "conditional display metadata is preserved without pretending to evaluate it");
        Check(Parse(Packet("receive",872,new byte[32]))["schema"]!.Value<string>("name") != "ClientTrigger", "same numeric receive opcode never uses send schema");
        var shortYell = Parse(Packet("receive",101,payload, captured:30));
        Check(shortYell.Value<string>("status") == "truncated_capture" && !Field(shortYell,"unknown3").Value<bool>("available") && Field(shortYell,"messageId").Value<int>("value") == 4567, "truncated tail is unavailable while complete fields remain readable");
        Check(Parse(Packet("receive",101,new byte[4])).Value<string>("status") == "short_payload", "too-short declared payload reports layout mismatch");
        Check(Parse(Packet("receive",65535,payload)).Value<string>("status") == "opcode_name_unavailable", "unknown numeric opcode does not guess a schema");
        Check(Parse(Packet("receive",65535,payload),"NpcYell").Value<string>("selection") == "caller_selected", "explicit schema override is labeled as caller interpretation");
        Reject(()=>Parse(Packet("send",872,send),"NpcYell"), "explicit opposite-direction schema refused");
        Reject(()=>Parse(Packet("receive",101,payload),"FellowshipList"), "missing direction metadata is never guessed");
        Check(Parse(Packet("receive",101,payload,layout:"other")).Value<string>("status") == "unsupported_capture_layout", "unknown capture header is refused");
        Reject(()=>schemas.Lookup(new JObject{["name"]="NpcYell",["structureId"]="s0"}), "schema lookup cannot follow foreign child id");
        var nested = new byte[64]; BinaryPrimitives.WriteUInt64LittleEndian(nested,12345678901234567890UL);
        BinaryPrimitives.WriteUInt32LittleEndian(nested.AsSpan(28+3*4),0xDEADBEEF);
        var eventPlay = Parse(Packet("receive",65535,nested),"EventPlay8");
        Check(Field(eventPlay,"header.actorId").Value<string>("value") == "12345678901234567890" && Field(eventPlay,"entities[3]").Value<uint>("value") == 0xDEADBEEF, "nested object and primitive array offsets are relative to parent");
        var rsv = new byte[56]; Encoding.UTF8.GetBytes("key\0").CopyTo(rsv,4); Encoding.UTF8.GetBytes("值\0").CopyTo(rsv,52);
        var text = Parse(Packet("receive",65535,rsv),"RSV");
        Check(Field(text,"key").Value<string>("value") == "key" && Field(text,"value").Value<string>("value") == "值", "fixed and remaining-length UTF-8 honor NUL");
        rsv[52]=0xff;
        Check(Field(Parse(Packet("receive",65535,rsv),"RSV"),"value").Value<string>("reason") == "invalid_utf8", "invalid UTF-8 is explicit");
        var levels = new byte[32]; BinaryPrimitives.WriteUInt32LittleEndian(levels, 12345);
        var level = Field(Parse(Packet("receive",65535,levels),"DesynthesisLevels"),"carpenter");
        Check(level.Value<int>("value") == 12345 && level.Value<double>("displayValue") == 123.45, "fixed-point display preserves independent raw integer");
        var position = new byte[20]; BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(8), -12.5f);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(12), float.PositiveInfinity);
        var pos = Parse(Packet("receive",65535,position),"ActorSetPos");
        Check(Field(pos,"x").Value<double>("value") == -12.5 && Field(pos,"y").Value<string>("value") == "Infinity", "schema floats preserve negative fractions and serialize infinity safely");
        var marker = new byte[16]; BinaryPrimitives.WriteInt32LittleEndian(marker.AsSpan(4), -123456);
        Check(Field(Parse(Packet("receive",65535,marker),"PlaceFieldMarker"),"x").Value<int>("value") == -123456, "schema signed integers sign-extend correctly");
        var achievements = new byte[552]; achievements[0] = 0x81; achievements[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(achievements.AsSpan(510), 65000);
        var achievement = Parse(Packet("receive",65535,achievements),"Achievement");
        Check(JToken.DeepEquals(Field(achievement,"completedAchievementIds")["value"], new JArray(0,7,9)) &&
            Field(achievement,"history[0]").Value<int>("value") == 65000, "bitset indices and adjacent primitive arrays use exact byte boundaries");
        // Every import must traverse safely, including arrays, oversized layouts and conditional structures.
        var allPackets = new List<JObject>(); int? next = 0;
        while(next.HasValue) { var page=schemas.Lookup(new JObject{["cursor"]=next,["count"]=100}); allPackets.AddRange(((JArray)page["packets"]!).Cast<JObject>()); next=page.Value<int?>("nextCursor"); }
        foreach(var p in allPackets.Where(p=>p.Value<bool>("mappingAvailable")))
        {
            var name=p.Value<string>("name")!; var direction=p.Value<string>("direction")!;
            var declared=Math.Max(p.Value<int>("minimumPayloadLength"),1);
            var parsed=Parse(Packet(direction,65535,new byte[Math.Min(declared,4000)],declared:declared+(direction=="send"?32:16)),name);
            var first=((JArray)parsed["fields"]!).Select(f=>f.Value<string>("name")).ToArray();
            Check(first.Length<=128 && parsed.Value<int>("totalFields")<=8192 && parsed["schema"]!.Value<string>("name")==name, "bounded traversal " + name);
            if(parsed.Value<int?>("nextCursor") is {} cursor)
            {
                var page=Parse(Packet(direction,65535,new byte[Math.Min(declared,4000)],declared:declared+(direction=="send"?32:16)),name,cursor);
                Check(((JArray)page["fields"]!).All(f=>!first.Contains(f.Value<string>("name"))), "field paging advances " + name);
            }
        }
        Reject(()=>schemas.Parse(Packet("receive",101,payload),new JObject{["count"]=129}), "parse page ceiling enforced");
        Reject(()=>schemas.Parse(Packet("receive",101,payload),new JObject{["cursor"]="1"}), "parse strict integer cursor enforced");
        return checks;
    }
}
