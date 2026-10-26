using System.Reflection;
using DalamudMCP.Logging;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Parsing;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
var type = typeof(Dalamud.Plugin.IDalamudPlugin).Assembly.GetType("Dalamud.Logging.Internal.SerilogEventSink", true)!;
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
var sink = type.GetProperty("Instance", flags)!.GetValue(null);
if (sink is null)
    sink = Activator.CreateInstance(type, flags, null, new object?[] { null }, null)!;
void Emit(string message, LogEventLevel level = LogEventLevel.Information, Exception? exception = null)
{
    var evt = new LogEvent(DateTimeOffset.UtcNow, level, exception, new MessageTemplateParser().Parse("{Message}"),
        new[] { new LogEventProperty("Message", new ScalarValue(message)), new LogEventProperty("SourceContext", new ScalarValue("LogTest")) });
    type.GetMethod("Emit", flags)!.Invoke(sink, new object[] { evt });
}
using var hub = new DalamudLogHub();
var registry = new ToolRegistry();
DalamudLogTools.Register(registry, hub);
var tool = registry.Find("get_dalamud_log")!;
Check(!tool.Mutating && registry.Visible().Contains(tool), "log query remains available with mutation gate closed");
foreach (var invalid in new[] { "{\"count\":0}", "{\"count\":201}", "{\"afterId\":-1}", "{\"minLevel\":\"1\"}", "{\"minLevel\":\"unknown\"}" })
{
    try { tool.Handler!(JObject.Parse(invalid)); Check(false, "invalid input"); }
    catch (ToolException) { Check(true, "invalid log query rejected " + invalid); }
}
JObject Read(int count = 100, long after = 0, string? contains = null, LogEventLevel min = LogEventLevel.Verbose, string? stream = null) =>
    hub.Query(count, min, null, contains, after, stream);
Check(Read().Value<bool>("available"), "installed SDK sink subscription succeeds");
Emit("first"); Emit("second", LogEventLevel.Error, new InvalidOperationException("test exception")); Emit("third");
var first = Read(1);
Check(first["lines"]![0]!["message"]!.Value<string>()!.Contains("first") && first.Value<bool>("hasMore"), "oldest-first bounded page");
var next = Read(1, first.Value<long>("nextAfterId"), stream: first.Value<string>("streamId"));
Check(next["lines"]![0]!["message"]!.Value<string>()!.Contains("second"), "pagination does not skip newer lines");
Check(Read(min: LogEventLevel.Error).Value<int>("count") == 1, "severity filter");
Check(Read(contains: "TEST EXCEPTION").Value<int>("count") == 1, "exception search is case-insensitive");
var none = Read(contains: "absent");
Check(none.Value<long>("nextAfterId") == none.Value<long>("lastId"), "filtered scan advances cursor");
Check(hub.Query(100, LogEventLevel.Verbose, "logtest", null, 0, null).Value<int>("count") == 3, "SourceContext filter");
Emit(new string('x', 10000));
Check(Read(contains: "xxx")["lines"]![0]!.Value<bool>("truncated"), "oversize message flagged");
Parallel.For(0, 1200, i => Emit("parallel " + i));
var overflow = Read(200, 1);
Check(overflow.Value<bool>("historyLost") && overflow.Value<int>("bufferSize") == 1000, "bounded retention reports dropped history");
long cursor = overflow.Value<long>("oldestId") - 1;
var ids = new List<long>();
do
{
    var page = Read(200, cursor);
    ids.AddRange(page["lines"]!.Select(x => x.Value<long>("id")));
    cursor = page.Value<long>("nextAfterId");
    if (!page.Value<bool>("hasMore")) break;
} while (true);
Check(ids.Count == 1000 && ids.Distinct().Count() == 1000 && ids.SequenceEqual(ids.Order()), "concurrent entries have ordered gap-free retained pagination");
for (var i = 0; i < 20; i++) Emit(new string('b', 4096), exception: new Exception(new string('e', 4096)));
var budget = Read(200, cursor);
Check(budget.Value<bool>("hasMore") && budget["lines"]!.ToString(Newtonsoft.Json.Formatting.None).Length < 64500, "response character budget paginates");
try { Read(stream: "obsolete"); Check(false, "stream guard"); } catch (ToolException) { Check(true, "reload cursor rejected"); }
try { Read(after: long.MaxValue); Check(false, "future cursor"); } catch (ToolException) { Check(true, "future cursor rejected"); }
var beforeDispose = Read().Value<long>("lastId");
hub.Dispose(); Emit("after dispose");
Check(!Read().Value<bool>("available") && Read().Value<long>("lastId") == beforeDispose, "dispose unsubscribes and stops capture");
hub.Dispose();
Console.WriteLine($"{checks} checks passed");
