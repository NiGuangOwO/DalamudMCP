using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
JObject Node(int id, string label, bool visible = true, bool enabled = true, string kind = "Button") => new()
{
    ["nodeId"] = id, ["label"] = label, ["componentType"] = kind,
    ["type"] = "1004", ["depth"] = 2, ["x"] = 10, ["y"] = 20,
    ["width"] = 100, ["height"] = 30, ["visible"] = visible,
    ["enabled"] = enabled, ["clickable"] = kind == "Button",
};
var exact = new AddonElementQuery(JObject.Parse("""{"label":"  改价（全部雇员）  ","detail":"compact"}"""), 1);
for (var i = 0; i < 300; i++) exact.Add(Node(i, "其他按钮"), Array.Empty<uint>());
exact.Add(Node(99, "改价（全部雇员）"), new uint[] { 10, 20 });
Check(exact.Elements.Count == 1 && !exact.Truncated, "filter runs before maxNodes even after 300 nonmatching controls");
Check(exact.Elements[0]["componentPath"]!.Values<uint>().SequenceEqual(new uint[] { 10, 20 }), "compact projection preserves full component path");
Check(exact.Elements[0]["x"] == null && exact.Elements[0]["label"] != null, "compact omits geometry while retaining labels");
exact.Add(Node(100, "改价（全部雇员）"), new uint[] { 11 });
Check(exact.Elements.Count == 1 && exact.ResultLimit && exact.Truncated, "one extra matching result marks possible ambiguity/truncation");
var substring = new AddonElementQuery(JObject.Parse("""{"label":"price","containsMatch":true}"""), 10);
substring.Add(Node(1, "Price all"), Array.Empty<uint>());
substring.Add(Node(2, "Reprice"), new uint[] { 5 });
Check(substring.Elements.Count == 2, "case-insensitive substring search preserves every candidate");
var literal = new AddonElementQuery(JObject.Parse("""{"label":"a.*"}"""), 10);
literal.Add(Node(1, "anything"), Array.Empty<uint>());
literal.Add(Node(2, "A.*"), Array.Empty<uint>());
Check(literal.Elements.Count == 1, "label matching is literal and does not execute regex");
var filters = new AddonElementQuery(JObject.Parse("""{"visibleOnly":true,"enabledOnly":true,"clickableOnly":true,"componentType":"Button"}"""), 10);
filters.Add(Node(1, "hidden", false), Array.Empty<uint>());
filters.Add(Node(2, "disabled", true, false), Array.Empty<uint>());
filters.Add(Node(3, "text", kind: "TextInput"), Array.Empty<uint>());
filters.Add(Node(4, "ready"), Array.Empty<uint>());
Check(filters.Elements.Count == 1 && filters.Elements[0].Value<int>("nodeId") == 4, "widget and state filters combine without choosing hidden or disabled controls");
var unknown = new AddonElementQuery(JObject.Parse("""{"visibleOnly":true}"""), 10);
var unknownNode = Node(5, "unknown"); unknownNode["visible"] = null;
unknown.Add(unknownNode, Array.Empty<uint>());
Check(unknown.Elements.Count == 0 && unknown.ScanIncomplete && unknown.Truncated, "unknown visibility is excluded and reports incomplete coverage");
var full = new AddonElementQuery(new JObject(), 10);
full.Add(Node(1, "full"), Array.Empty<uint>());
Check(full.Elements[0]["x"] != null && !full.Truncated, "default full projection preserves legacy fields");
Check(full.Elements[0]["componentPath"] is JArray { Count: 0 }, "top-level path is explicit rather than ambiguous absence");
full.ScanIncomplete = true;
Check(full.Truncated, "incomplete traversal is never represented as exhaustive search");
var textOnly = new AddonElementQuery(JObject.Parse("""{"detail":"compact","textOnly":true}"""), 2);
textOnly.Add(Node(1, "", kind: "Button"), Array.Empty<uint>());
textOnly.Add(Node(2, "   "), Array.Empty<uint>());
textOnly.Add(Node(3, "first"), Array.Empty<uint>());
textOnly.Add(Node(4, "second"), Array.Empty<uint>());
Check(textOnly.Elements.Count == 2 && !textOnly.Truncated,
    "text-only mode ignores blank and unlabeled controls before consuming its result budget");
textOnly.Add(Node(5, "third"), Array.Empty<uint>());
Check(textOnly.Elements.Count == 2 && textOnly.ResultLimit,
    "text-only mode preserves one-match lookahead and marks truncation");
var textFiltered = new AddonElementQuery(
    JObject.Parse("""{"detail":"compact","textOnly":true,"label":"quest","containsMatch":true}"""), 10);
textFiltered.Add(Node(10, "Main Quest"), Array.Empty<uint>());
textFiltered.Add(Node(11, "Settings"), Array.Empty<uint>());
Check(textFiltered.Elements.Count == 1 && textFiltered.Elements[0].Value<int>("nodeId") == 10,
    "text-only mode can search node text without returning non-text controls");
foreach (var input in new[] {
    """{"detail":"tiny"}""", """{"visibleOnly":"false"}""", """{"enabledOnly":1}""", """{"textOnly":"true"}""",
    """{"label":2}""", """{"containsMatch":true}""", """{"containsMatch":true,"label":" "}""",
    """{"componentType":null}""", """{"label":null}""", """{"detail":null}""",
    new JObject { ["label"] = new string('a', 257) }.ToString(),
})
{
    var refused = false;
    try { _ = new AddonElementQuery(JObject.Parse(input), 10); }
    catch (ToolException) { refused = true; }
    Check(refused, "malformed/coerced query refused: " + input[..Math.Min(65, input.Length)]);
}
Console.WriteLine($"{checks}/{checks} checks passed");
