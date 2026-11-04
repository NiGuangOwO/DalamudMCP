using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Runtime;

/// <summary>Bounded, explicit-range value scans. No unbounded process-wide scans.</summary>
internal sealed class MemoryScan
{
    private sealed record Scan(Type Type, Dictionary<ulong,byte[]> Values, bool Complete);
    private readonly Dictionary<string,Scan> scans = new();
    private long next;
    internal static Type ValueType(string name) => name.ToLowerInvariant() switch {
        "byte" or "u8" => typeof(byte), "sbyte" or "i8" => typeof(sbyte), "short" or "i16" => typeof(short), "ushort" or "u16" => typeof(ushort),
        "int" or "i32" => typeof(int), "uint" or "u32" => typeof(uint), "long" or "i64" => typeof(long), "ulong" or "u64" => typeof(ulong),
        "float" or "f32" => typeof(float), "double" or "f64" => typeof(double), "bool" => typeof(bool), _ => throw new ToolException("Unsupported value type") };
    private static decimal Number(byte[] value, Type type) => Convert.ToDecimal(NativeAccess.Decode(value,type),CultureInfo.InvariantCulture);
    internal object Run(JObject args)
    {
        var action = args.Value<string>("action");
        if (action == "List") return new { sessions = scans.Select(p => new { id=p.Key, count=p.Value.Values.Count, complete=p.Value.Complete }).ToArray() };
        if (action == "Drop") return new { removed = scans.Remove(args.Value<string>("session") ?? "") };
        var limit = RuntimeCatalog.Int(args,"limit",1,256,16);
        if (action == "Filter")
        {
            if (!scans.TryGetValue(args.Value<string>("session") ?? "",out var scan)) throw new ToolException("Unknown scan session");
            var mode = args.Value<string>("filter") ?? "changed";
            if (!new[]{"changed","unchanged","increased","decreased","equals","between"}.Contains(mode)) throw new ToolException("Unknown filter");
            var low = args["value"] is null ? 0 : decimal.Parse(args["value"]!.ToString(),CultureInfo.InvariantCulture);
            var upper = args["upper"] is null ? low : decimal.Parse(args["upper"]!.ToString(),CultureInfo.InvariantCulture);
            var keep = new Dictionary<ulong,byte[]>(); var lost=0;
            foreach(var entry in scan.Values)
            {
                byte[] current; try { current=NativeAccess.Read(entry.Key,entry.Value.Length); } catch(ToolException){lost++;continue;}
                var equal=current.SequenceEqual(entry.Value);
                bool accept;
                try { accept=mode switch { "changed"=>!equal,"unchanged"=>equal,"increased"=>Number(current,scan.Type)>Number(entry.Value,scan.Type),"decreased"=>Number(current,scan.Type)<Number(entry.Value,scan.Type),"equals"=>Number(current,scan.Type)==low,_=>Number(current,scan.Type)>=low&&Number(current,scan.Type)<=upper }; }
                catch(OverflowException){accept=false;}
                if(accept)keep.Add(entry.Key,current);
            }
            scans[args.Value<string>("session")!]=scan with { Values=keep, Complete=scan.Complete&&lost==0 };
            return new { session=args.Value<string>("session"),count=keep.Count, lost, complete=scan.Complete&&lost==0, addresses=keep.Keys.Take(limit).Select(a=>"0x"+a.ToString("X")).ToArray() };
        }
        if (action is not ("Value" or "Pattern")) throw new ToolException("action must be Value, Pattern, Filter, List or Drop");
        var start=NativeAccess.Address(args["rangeStart"]);var end=NativeAccess.Address(args["rangeEnd"]);
        if(end<=start||end-start>16*1024*1024)throw new ToolException("Explicit readable scan range must be 1..16 MiB");
        var type=action=="Value"?ValueType(args.Value<string>("valueType")??"int"):typeof(byte);
        var size=NativeAccess.Size(type); byte?[]? pattern=null;
        if(action=="Pattern")
        {
            var text=args.Value<string>("pattern")??throw new ToolException("pattern required");
            if(text.Length>2048)throw new ToolException("Pattern too long");
            try{pattern=text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(s=>s is "?" or "??"?(byte?)null:byte.Parse(s,NumberStyles.HexNumber)).ToArray();}catch(FormatException){throw new ToolException("Invalid byte pattern");}
            if(pattern.Length is <1 or >256)throw new ToolException("Pattern must contain 1..256 bytes"); size=pattern.Length;
        }
        var valueText=args["value"]?.ToString();
        var target=action=="Value"?(type==typeof(bool)&&bool.TryParse(valueText,out var boolean)?boolean?1m:0m:decimal.Parse(valueText??throw new ToolException("value required"),CultureInfo.InvariantCulture)):0;
        var alignment=RuntimeCatalog.Int(args,"alignment",1,64,action=="Value"?size:1);
        var values=new Dictionary<ulong,byte[]>();var complete=true;var skipped=0;var watch=Stopwatch.StartNew();
        for(var cursor=start;cursor+(ulong)size<=end;)
        {
            if(watch.ElapsedMilliseconds>1000){complete=false;break;}
            var count=(int)Math.Min(65536ul,end-cursor);byte[] bytes;
            try{bytes=NativeAccess.Read(cursor,count);}catch(ToolException){skipped+=count;cursor+=(ulong)count;complete=false;continue;}
            var advance=Math.Max(alignment,((count-size+1)/alignment)*alignment);
            for(var offset=0;offset+size<=count;offset+=alignment)
            {
                bool match;
                if(pattern is not null){match=true;for(var i=0;i<size;i++)if(pattern[i] is {} b&&b!=bytes[offset+i]){match=false;break;}}
                else try{match=Number(bytes.AsSpan(offset,size).ToArray(),type)==target;}catch(OverflowException){match=false;}
                if(!match)continue;
                if(values.Count==16384){complete=false;break;}
                values[cursor+(ulong)offset]=bytes.AsSpan(offset,size).ToArray();
            }
            if(values.Count==16384)break;cursor+=(ulong)advance;
        }
        string? id=null;
        if(action=="Value") { if(scans.Count>=8)throw new ToolException("Eight scan sessions reached; drop one first");id="s"+ ++next;scans.Add(id,new Scan(type,values,complete)); }
        return new { session=id,count=values.Count,complete,skippedBytes=skipped,addresses=values.Keys.Take(limit).Select(a=>"0x"+a.ToString("X")).ToArray() };
    }
    internal void Clear()=>scans.Clear();
}
