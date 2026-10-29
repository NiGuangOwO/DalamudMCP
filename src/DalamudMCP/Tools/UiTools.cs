using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace DalamudMCP.Tools;

/// <summary>
/// The plugin's first mutating tool set: opening and closing the game's addon (UI window)
/// surfaces through the FFXIVClientStructs agent layer.
///
/// <para>Every tool here is registered with <c>mutating: true</c>, so the whole set is invisible
/// to <c>tools/list</c> and refused by <c>tools/call</c> while the <c>AllowMutatingTools</c>
/// configuration switch is off 鈥?the same two-place gate every future state-changing tool
/// inherits. The actions themselves are bounded to what a player could do with a keyboard
/// shortcut: show or hide a system window, or ask which ones are open. The click surface
/// (click_addon_element) is likewise bounded to dispatching one Atk UI event at a synthetic
/// position inside a window the agent has already enumerated — the moral equivalent of moving
/// the mouse and clicking. TextInputTools provides a separate bounded native text edit;
/// neither surface touches combat state. Crafted AtkValue callback payloads live in <c>CallbackTools</c>, a separate set
/// with its own justification.</para>
///
/// <para>The list/click mechanics go one layer below the agent layer, to the game's own UI
/// tree: RaptureAtkUnitManager resolves a loaded addon (AtkUnitBase), its RootNode tree is
/// walked for element discovery (ids, labels, screen rectangles), and clicking dispatches
/// AtkEventType.MouseClick / ButtonClick (or the component-list item path) through the
/// addon's own ReceiveEvent — the same dispatch the real input pipeline feeds. Every pointer
/// is null-checked rather than trusted, and every failure becomes a structured
/// <c>available=false</c> answer with a reason, never an escaped exception.</para>
/// </summary>
internal static class UiTools
{
    /// <summary>
    /// The agent ids this tool set will open, by name. The full <see cref="AgentId"/> enum has
    /// ~500 members, many of which are context menus or sub-panels that make no sense opened
    /// standalone (or crash when shown out of context); an allowlist keeps the tool's surface
    /// auditable and each entry hand-verifiable in game.
    /// </summary>
    private static readonly Dictionary<string, AgentId> OpenableAgents = new()
    {
        ["inventory"] = AgentId.Inventory,
        ["armoury"] = AgentId.ArmouryBoard,
        ["actionmenu"] = AgentId.ActionMenu,
        ["mirageprism"] = AgentId.MiragePrism,
        ["colorant"] = AgentId.Colorant,
        ["emote"] = AgentId.Emote,
        ["questjournal"] = AgentId.QuestJournal,
        ["journaldetail"] = AgentId.Detail,
        ["fishingnote"] = AgentId.FishingNote,
        ["fishguide"] = AgentId.FishGuide,
        ["monsternote"] = AgentId.MonsterNote,
        ["achievement"] = AgentId.Achievement,
        ["mountnotebook"] = AgentId.MountNotebook,
        ["minionnotebook"] = AgentId.MinionNotebook,
        ["orchestrion"] = AgentId.Orchestrion,
        ["adventurenotebook"] = AgentId.AdventureNotebook,
        ["aethercurrent"] = AgentId.AetherCurrent,
        ["contentsnote"] = AgentId.ContentsNote,
        ["pvpprofile"] = AgentId.PvpProfile,
        ["companions"] = AgentId.Buddy,
        ["chocobobreed"] = AgentId.ChocoboBreed,
        ["chocoborace"] = AgentId.ChocoboRace,
        ["goldsaucer"] = AgentId.GoldSaucer,
        ["tripletriad"] = AgentId.TripleTriad,
        ["currency"] = AgentId.Currency,
        ["retainer"] = AgentId.Retainer,
        ["freecompany"] = AgentId.FreeCompany,
        ["freecompanyprofile"] = AgentId.FreeCompanyProfile,
        ["freecompanychest"] = AgentId.FreeCompanyChest,
        ["gearsets"] = AgentId.GearSet,
        ["social"] = AgentId.Social,
        ["friendlist"] = AgentId.Friendlist,
        ["partysearch"] = AgentId.Search,
        ["lookingforparty"] = AgentId.LookingForGroup,
        ["linkshell"] = AgentId.Linkshell,
        ["blacklist"] = AgentId.Blacklist,
        ["contacts"] = AgentId.ContactList,
        ["teleport"] = AgentId.Teleport,
        ["recipenote"] = AgentId.RecipeNote,
        ["gatheringnote"] = AgentId.GatheringNote,
        ["materialize"] = AgentId.Materialize,
        ["materiaattach"] = AgentId.MateriaAttach,
        ["repair"] = AgentId.Repair,
        ["itemsearch"] = AgentId.ItemSearch,
        ["itemdetail"] = AgentId.ItemDetail,
        ["hudlayout"] = AgentId.HudLayout,
        ["config"] = AgentId.Config,
        ["characterconfig"] = AgentId.ConfigCharacter,
        ["chatconfig"] = AgentId.ChatConfig,
        ["keybindconfig"] = AgentId.Configkey,
        ["playguide"] = AgentId.PlayGuide,
        ["dutyfinder"] = AgentId.ContentsFinder,
        ["raidfinder"] = AgentId.RaidFinder,
        ["screenlog"] = AgentId.ScreenLog,
        ["howto"] = AgentId.HowTo,
        ["cabinet"] = AgentId.Cabinet,
        ["legacyitemstorage"] = AgentId.LegacyItemStorage,
        ["salvage"] = AgentId.Salvage,
        ["aquarium"] = AgentId.Aquarium,
        ["weatherreport"] = AgentId.WeatherReport,
        ["weblauncher"] = AgentId.WebLauncher,
        ["licenseviewer"] = AgentId.LicenseViewer,
        ["letter"] = AgentId.Letter,
        ["relicnotebook"] = AgentId.RelicNotebook,
        ["deepdungeonmap"] = AgentId.DeepDungeonMap,
        ["deepdungeonstatus"] = AgentId.DeepDungeonStatus,
    };

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        var names = OpenableAgents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        registry.Add(
            "open_addon",
            "Open an addon (UI window)",
            "Opens one of the game's addon (UI window) surfaces the way its own keyboard shortcut " +
            "would: through the FFXIVClientStructs agent layer (AgentInterface.Show). Bounded to an " +
            "allowlist of standalone-openable system windows; anything that must be opened from a " +
            "parent context is refused by design. Inventory uses UIModule.OpenInventory and reports its actual " +
            "immediate open state; other agents still report dispatch only. This is a mutating tool: it is hidden from " +
            "tools/list and refused by tools/call while the plugin's allow-mutating-tools switch " +
            "is off.",
            Json.SchemaWithEnum(
                "addon",
                names,
                "Which addon to open.",
                true),
            args => OpenAddon(args),
            mutating: true);

        registry.Add(
            "close_addon",
            "Close an addon (UI window)",
            "Closes one of the game's addon (UI window) surfaces through the agent layer " +
            "(AgentInterface.Hide), the same path the game's own close action takes. Accepts the " +
            "same allowlist names as open_addon. Inventory uses UIModule.CloseInventory and reads back actual state. " +
            "This is a mutating tool: it is hidden from " +
            "tools/list and refused by tools/call while the plugin's allow-mutating-tools switch " +
            "is off.",
            Json.SchemaWithEnum(
                "addon",
                names,
                "Which addon to close.",
                true),
            args => CloseAddon(args),
            mutating: true);

        registry.Add(
            "get_addon_state",
            "Get addon (UI window) state",
            "Reports which of the allowlisted addon surfaces the agent layer currently considers " +
            "active, keyed by the same names open_addon accepts. Inventory uses the actual UIModule bag-window " +
            "state because its persistent agent can remain active with no bag window. Unknown inventory state " +
            "is listed in unknown, never silently treated as inactive. Read-only.",
            Json.Schema(),
            _ => AddonState());

        registry.Add(
            "list_loaded_addons",
            "Discover loaded native UI windows",
            "Lists exact internal names and ids from the native loaded-addon list, including minigames outside the open_addon allowlist. Read-only; use returned names with list_addon_elements or get_addon_node_snapshot. Bounded to the native list capacity.",
            Json.Schema(
                ("nameContains", "string", "Optional case-insensitive internal-name substring.", false),
                ("visibleOnly", "boolean", "Only return visible windows (default true).", false),
                ("max", "integer", "Maximum results, 1-256 (default 100).", false)),
            ListLoadedAddons);

        registry.Add(
            "list_addon_elements",
            "List addon (UI window) elements",
            "Read nodes in any loaded native addon; discover exact names with list_loaded_addons. " +
            "For UI text use read_addon_text; use this tool when controls, labels, visibility or geometry are needed. " +
            "For ordinary lookup use label/componentType filters and detail=compact. Copy nodeId and " +
            "componentPath to click_addon_element or get_addon_node_snapshot; IDs alone can repeat. " +
            "Filters run before the result limit. truncated/scanIncomplete mean absence or uniqueness " +
            "cannot be established. visible includes addon, parent and component-scope visibility; full detail " +
            "also reports selfVisible. Unreadable/cyclic ancestry returns visible=null and scanIncomplete=true. " +
            "visibleOnly skips known-hidden component subtrees; root ULD arrays support up to4096 entries within the same4096-node total scan budget. " +
            "Full detail and unfiltered behavior remain the default. Read-only.",
            ElementQuerySchema(),
            args => ListAddonElements(args));

        registry.Add(
            "read_addon_text",
            "Read text from an addon (UI window)",
            "Preferred read-only text path for native UI. Reads visible Text/Button/List labels from the " +
            "addon node tree and string-valued AtkValue entries in one bounded result. Use this to read " +
            "native UI text. This does not OCR textured text. visibleOnly defaults true. Node text keeps " +
            "nodeId/componentPath for follow-up; values keep their AtkValue index. truncated or scanIncomplete " +
            "means text coverage is incomplete.",
            AddonTextSchema(),
            args => ReadAddonText(args));

        CheckboxTools.Register(registry);
        TextInputTools.Register(registry);
        ContextMenuTools.Register(registry);
        NumericInputTools.Register(registry);
        MapTools.Register(registry);
        DropdownTools.Register(registry);
        ListRowTools.Register(registry);
        registry.Add(
            "click_addon_element",
            "Click an addon (UI window) element",
            "Dispatches one UI event (default: left click) at a node through the loaded addon's own vtable. " +
            "Discover the actual event type and node first; component-owned controls may require typed tools. " +
            "Automatic discovery preserves exact registered parameter zero and refuses conflicting exact parameters. " +
            "Legacy fallback remains only when the requested event type is absent. For list components " +
            "you may pass 'index' to click a list item directly. CheckBox controls require set_addon_checkbox with an explicit checked state. This is a mutating tool: it " +
            "is hidden from tools/list and refused by tools/call while the plugin's " +
            "allow-mutating-tools switch is off.",
            Json.Schema(
                ("addon", "string", "Addon name to click inside (same names open_addon accepts)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to click inside (overrides addon)", false),
                ("nodeId", "integer", "Target node id from list_addon_elements", false),
                ("componentId", "integer", "Optional top-level component node id. Resolve nodeId only inside this component, avoiding duplicate child ids elsewhere in the addon.", false),
                ("componentPath", "array of integer", "Exact component path from list_addon_elements, outermost first (0-40 ids). Empty means top-level only. Do not combine with componentId; ambiguous paths are refused. Re-query after addon replacement.", false),
                ("index", "integer", "For list components: the item index to click instead of a nodeId", false),
                ("event", "string", "Which discovered event to dispatch: click (default), doubleClick, buttonClick, buttonPress, buttonRelease. Advanced 'registered' sends every registered handler; inspect all effects first.", false),
                ("param", "integer", "Override the event callback param (uint32). Advanced: the game normally derives it from the node's registered handler; ClickLib-style close buttons need 0xFFFFFFFF (-1)", false)),
            args => ClickAddonElement(args),
            mutating: true);

        registry.Add(
            "get_addon_strings",
            "Read an addon (UI window)'s value table",
            "Reports the string and scalar values an addon currently carries in its AtkValue " +
            "table — the text a menu, dialog or list is displaying right now. Use read_addon_text " +
            "for a concise combined node/AtkValue text view. Each entry is " +
            "index, type and decoded value; control characters are escaped so the raw payload " +
            "is visible. Pass 'addon' or 'addonId'. Read-only.",
            Json.Schema(
                ("addon", "string", "Addon name to read (same names open_addon accepts)", false),
                ("addonId", "integer", "Raw AtkUnitBase id to read (overrides addon)", false),
                ("maxValues", "integer", "Maximum entries to report (1-500, default 200)", false),
                ("stringsOnly", "boolean", "Only report string-typed values (default false)", false)),
            args => GetAddonStrings(args));

        registry.Add(
            "select_addon_menu_item",
            "Select an item in an addon (UI window) menu",
            "Selects a menu entry by normalized label or index. SelectString reads its guarded native popup " +
            "entries and sends ListItemClick through the list; index is the zero-based menu ordinal, " +
            "NOT the label's AtkValue index. Use dryRun to inspect indexKind and the selected entry. " +
            "Other addons retain legacy AtkValue-index callback behavior and require a known callback contract. " +
            "Ambiguous labels are refused even with containsMatch. A dispatched input is not proof of transition. This is a " +
            "mutating tool: it is hidden from tools/list and refused by tools/call while the " +
            "plugin's allow-mutating-tools switch is off.",
            Json.Schema(
                ("addon", "string", "Addon name whose menu to select from", false),
                ("addonId", "integer", "Raw AtkUnitBase id (overrides addon)", false),
                ("label", "string", "Menu entry label to select (required unless index is given)", false),
                ("index", "integer", "SelectString: zero-based menu ordinal; other addons: legacy AtkValue index", false),
                ("containsMatch", "boolean", "Match labels by substring instead of exact (default false)", false),
                ("dryRun", "boolean", "Report the entry that would be selected without activating it", false)),
            args => SelectAddonMenuItem(args),
            mutating: true);

        // Temporary diagnostic tools for the click-dispatch investigation. Mutating: the
        // enable variant swaps a live vtable slot inside the game process.
        registry.Add(
            "probe_receive_event_enable",
            "Hook one addon's ReceiveEvent (diagnostic)",
            "Temporary diagnostic: swaps the ReceiveEvent vtable slot of one loaded addon so " +
            "every UI event it receives (real mouse clicks and synthetic dispatches alike) is " +
            "captured before being forwarded. Pass 'addon' (any loaded addon name) or 'addonId'. " +
            "Use probe_receive_event_dump to read captures. Mutating.",
            Json.Schema(
                ("addon", "string", "Addon name to hook (raw loaded addon name, e.g. Currency)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to hook (overrides addon)", false)),
            args => ProbeEnable(args),
            mutating: true);

        registry.Add(
            "probe_receive_event_disable",
            "Unhook the ReceiveEvent probe (diagnostic)",
            "Restores the ReceiveEvent vtable slot captured by probe_receive_event_enable. " +
            "Mutating.",
            Json.Schema(),
            _ => AtkEventProbe.Disable(),
            mutating: true);

        registry.Add(
            "probe_receive_event_enable_listener",
            "Hook one node's registered listener ReceiveEvent (diagnostic)",
            "Resolves the addon and node, then hooks the node's FIRST registered listener's " +
            "ReceiveEvent vtable slot. Real component clicks land on node-registered listeners " +
            "rather than the addon's own slot, so this captures what a real click delivers. " +
            "Pass 'addon' (raw loaded addon name) or 'addonId', plus 'nodeId'. Use " +
            "probe_receive_event_dump to read captures. Mutating.",
            Json.Schema(
                ("addon", "string", "Addon name to hook (raw loaded addon name, e.g. Currency)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to hook (overrides addon)", false),
                ("nodeId", "integer", "Node whose first registered listener should be hooked", true)),
            args => ProbeEnableListener(args),
            mutating: true);

        registry.Add(
            "probe_receive_event_dump",
            "Dump captured ReceiveEvent calls (diagnostic)",
            "Returns every ReceiveEvent invocation captured since the hook was enabled: event " +
            "type, param, and the raw event / event-data buffers decoded at their FCS offsets. " +
            "Read-only.",
            Json.Schema(),
            _ => AtkEventProbe.Dump());

        // FireCallback probe (modelled on SimpleTweaks' Addon Logging → Callbacks): a
        // global inline detour recording every addon callback with its decoded AtkValue[]
        // arguments. This captures the semantic layer real UI clicks funnel into.
        registry.Add(
            "probe_callback_enable",
            "Hook the global FireCallback (diagnostic)",
            "Temporary diagnostic: installs a global hook on AtkUnitBase.FireCallback so every " +
            "addon callback the game performs is recorded with its decoded AtkValue[] " +
            "arguments — the same data SimpleTweaks' Addon Logging → Callbacks shows. Real UI " +
            "interactions (button clicks, list picks) funnel into these callbacks, so the " +
            "capture tells an agent exactly which (addon, values) payload reproduces an " +
            "action. Use probe_callback_dump to read captures. Mutating.",
            Json.Schema(),
            _ => CallbackProbe.Enable(),
            mutating: true);

        registry.Add(
            "probe_callback_disable",
            "Unhook the FireCallback probe (diagnostic)",
            "Removes the global FireCallback hook installed by probe_callback_enable. Mutating.",
            Json.Schema(),
            _ => CallbackProbe.Disable(),
            mutating: true);

        registry.Add(
            "probe_callback_dump",
            "Dump captured FireCallback calls (diagnostic)",
            "Returns every addon callback captured since probe_callback_enable: addon name, " +
            "value count, each AtkValue decoded by type (Int/UInt/Bool/String/...), the " +
            "close/update-visibility flag, and the return value. Read-only.",
            Json.Schema(),
            _ => CallbackProbe.Dump());
    }

    private static unsafe object ProbeEnable(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved for probe_receive_event_enable");
        return AtkEventProbe.Enable(unit);
    }

    /// <summary>Hook a specific node's first registered listener. Resolves the addon +
    /// node, walks the node's AtkEventManager event chain, and hooks the FIRST listener
    /// found there (that listener's own vtable slot). Real component clicks land here,
    /// not on the addon's vtable slot.</summary>
    private static unsafe object ProbeEnableListener(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved for probe_receive_event_enable_listener");

        var nodeId = args["nodeId"]?.Value<int?>();
        if (nodeId is null)
            return Json.ToolError("missing required parameter: nodeId");

        var node = FindBestInUld(unit, (uint)nodeId.Value, out var bestScore);
        if (node is null || bestScore < 3)
        {
            var fromRoot = FindBestNode(unit->RootNode, (uint)nodeId.Value, out var rootScore);
            if (rootScore > bestScore)
            {
                node = fromRoot;
                bestScore = rootScore;
            }
        }
        if (node is null)
            return Json.ToolError($"no node with id {nodeId} was found in this addon");

        var em = &node->AtkEventManager;
        if (em->Event is null)
            return Json.ToolError($"node {nodeId} has no registered listeners");

        var listener = em->Event->Listener;
        if (listener is null)
            return Json.ToolError($"node {nodeId}'s first registered event has a null listener");

        return AtkEventProbe.EnableListener(unit, listener);
    }

    // ---------------------------------------------------------------- handlers

    private static unsafe object ListLoadedAddons(JObject args)
    {
        var max = args.Value<int?>("max") ?? 100;
        if (max is < 1 or > 256) throw new ToolException("max must be 1-256");
        RaptureAtkUnitManager* manager;
        try { manager = RaptureAtkUnitManager.Instance(); }
        catch (InvalidOperationException) { return new JObject { ["available"] = false, ["reason"] = "UI manager unresolved" }; }
        if (manager == null) return new JObject { ["available"] = false, ["reason"] = "UI manager unavailable" };
        var filter = args.Value<string>("nameContains") ?? "";
        var visibleOnly = args.Value<bool?>("visibleOnly") ?? true;
        var list = &manager->AllLoadedUnitsList;
        var entries = new JArray();
        var matching = 0;
        for (var i = 0; i < Math.Min((int)list->Count, list->Entries.Length); i++)
        {
            var unit = list->Entries[i].Value;
            if (unit == null || (visibleOnly && !unit->IsVisible)) continue;
            var name = unit->NameString;
            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            matching++;
            if (entries.Count >= max) continue;
            entries.Add(new JObject { ["addon"] = name, ["addonId"] = unit->Id,
                ["visible"] = unit->IsVisible, ["ready"] = unit->IsReady,
                ["nodeCount"] = unit->UldManager.NodeListCount, ["valueCount"] = unit->AtkValuesCount });
        }
        return new JObject { ["available"] = true, ["count"] = entries.Count,
            ["totalMatching"] = matching, ["truncated"] = matching > entries.Count, ["addons"] = entries };
    }

    private static unsafe object OpenAddon(JObject args)
    {
        var addon = args["addon"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(addon))
            return Json.ToolError("missing required parameter: addon");

        if (!OpenableAgents.TryGetValue(addon, out var agentId))
            return Json.ToolError($"unknown addon '{addon}'; see open_addon's enum for the allowlist");

        if (agentId == AgentId.Inventory) return InventoryUiTools.Set(true);

        return WithAgent(agentId, addon, agent =>
        {
            agent->Show();
            return new JObject
            {
                ["addon"] = addon,
                ["agentId"] = (int)agentId,
                ["opened"] = true,
            };
        });
    }

    private static unsafe object CloseAddon(JObject args)
    {
        var addon = args["addon"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(addon))
            return Json.ToolError("missing required parameter: addon");

        if (!OpenableAgents.TryGetValue(addon, out var agentId))
            return Json.ToolError($"unknown addon '{addon}'; see close_addon's enum for the allowlist");

        if (agentId == AgentId.Inventory) return InventoryUiTools.Set(false);

        return WithAgent(agentId, addon, agent =>
        {
            agent->Hide();
            return new JObject
            {
                ["addon"] = addon,
                ["agentId"] = (int)agentId,
                ["closed"] = true,
            };
        });
    }

    private static unsafe object AddonState()
    {
        var module = SafeAgentModule();
        if (module is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = "the agent module's static address has not resolved - the game may " +
                             "not be running, or the client has not finished initializing",
            };
        }

        var active = new JArray();
        var inactive = new JArray();
        var unknown = new JArray();
        foreach (var (name, agentId) in OpenableAgents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (agentId == AgentId.Inventory)
            {
                try { (InventoryUiTools.IsOpen() ? active : inactive).Add(name); }
                catch (ToolException) { unknown.Add(name); }
                continue;
            }
            var agent = SafeAgent(module, agentId);
            if (agent is null)
            {
                inactive.Add(name);
                continue;
            }

            var isActive = agent->IsAgentActive();
            (isActive ? active : inactive).Add(name);
        }

        return new JObject
        {
            ["available"] = true,
            ["active"] = active,
            ["inactive"] = inactive,
            ["unknown"] = unknown,
        };
    }

    // ---------------------------------------------------------------- addon element tools

    private static JObject ElementQuerySchema()
    {
        var schema = Json.Schema(
            ("addon", "string", "Exact loaded addon name from list_loaded_addons; not restricted by open_addon.", false),
            ("addonId", "integer", "Raw loaded-addon id (overrides addon).", false),
            ("maxDepth", "integer", "Maximum component nesting depth, 1-40 (default 12).", false),
            ("maxNodes", "integer", "Maximum matching results, 1-500 (default 100).", false),
            ("allNodes", "boolean", "Include unlabeled/nonclickable nodes (default false).", false),
            ("label", "string", "Trimmed case-insensitive exact label, up to 256 characters.", false),
            ("containsMatch", "boolean", "Use label substring matching (default false); returns all matches, never auto-selects.", false),
            ("componentType", "string", "Filter by native widget kind, e.g. Button or CheckBox.", false),
            ("visibleOnly", "boolean", "Only effectively visible nodes, including addon, ancestors and owning components (default false). Unknown visibility is excluded.", false),
            ("enabledOnly", "boolean", "Only enabled nodes (default false).", false),
            ("clickableOnly", "boolean", "Only nodes classified clickable (default false).", false),
            ("detail", "string", "compact omits geometry/type/depth; full preserves diagnostic fields (default full).", false));
        schema["properties"]!["detail"]!["enum"] = new JArray("compact", "full");
        schema["properties"]!["componentType"]!["enum"] = new JArray(Enum.GetNames<ComponentType>());
        return schema;
    }

    private static JObject AddonTextSchema()
    {
        var schema = Json.Schema(
            ("addon", "string", "Exact loaded addon name from list_loaded_addons; not restricted by open_addon.", false),
            ("addonId", "integer", "Raw loaded-addon id (alternative to addon).", false),
            ("contains", "string", "Optional case-insensitive literal text filter, up to 256 characters; returns all matches.", false),
            ("visibleOnly", "boolean", "Only effectively visible UI text and skip AtkValue text from a hidden addon (default true).", false),
            ("includeNodeText", "boolean", "Read Text/Button/List labels from the addon node tree (default true).", false),
            ("includeAtkValues", "boolean", "Read string-valued AtkValue entries (default true).", false),
            ("maxNodeTexts", "integer", "Maximum node labels returned, 1-500 (default 100).", false),
            ("maxValueTexts", "integer", "Maximum AtkValue strings returned, 1-500 (default 100).", false),
            ("maxTextLength", "integer", "Maximum characters retained per text item, 32-4096 (default 512).", false),
            ("maxDepth", "integer", "Maximum component nesting depth, 1-40 (default 12).", false));
        var props = (JObject)schema["properties"]!;
        props["visibleOnly"]!["default"] = true;
        props["includeNodeText"]!["default"] = true;
        props["includeAtkValues"]!["default"] = true;
        props["maxNodeTexts"]!["default"] = 100;
        props["maxNodeTexts"]!["minimum"] = 1;
        props["maxNodeTexts"]!["maximum"] = 500;
        props["maxValueTexts"]!["default"] = 100;
        props["maxValueTexts"]!["minimum"] = 1;
        props["maxValueTexts"]!["maximum"] = 500;
        props["maxTextLength"]!["default"] = 512;
        props["maxTextLength"]!["minimum"] = 32;
        props["maxTextLength"]!["maximum"] = 4096;
        props["maxDepth"]!["default"] = 12;
        props["maxDepth"]!["minimum"] = 1;
        props["maxDepth"]!["maximum"] = 40;
        return schema;
    }

    private static unsafe object ListAddonElements(JObject args)
    {
        var addonName = args["addon"]?.Value<string>();
        var addonIdArg = args["addonId"];
        var maxDepth = ClampInt(args["maxDepth"], 1, 40, 12);
        var maxNodes = ClampInt(args["maxNodes"], 1, 500, 100);
        var allNodes = args["allNodes"]?.Value<bool>() ?? false;
        var query = new AddonElementQuery(args, maxNodes);
        if (args["componentType"] is { } kind && !Enum.GetNames<ComponentType>().Contains(kind.Value<string>(), StringComparer.OrdinalIgnoreCase))
            throw new ToolException("componentType must be a native widget kind from the tool schema");

        var unit = ResolveAddon(addonName, addonIdArg);
        if (unit is null)
        {
            var loaded = string.Empty;
            var mgr = SafeRaptureAtkUnitManager();
            if (mgr is not null)
                loaded = DescribeLoadedAddons(mgr);
            return addonName is not null
                ? Json.ToolError($"no loaded addon named '{addonName}' was found; discover current names with list_loaded_addons.{loaded}")
                : Json.ToolError("no addon resolved; pass an exact loaded 'addon' name or 'addonId'");
        }

        var root = unit->RootNode;
        if (root is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = $"addon '{unit->NameString}' is loaded but has no root node yet",
            };
        }

        // Top-level nodes live in the addon's UldManager node list (an array), not in the
        // RootNode's child chain — RootNode->ChildCount (e.g. 110) far exceeds the one
        // linked child, so the authoritative enumeration is UldManager.NodeList. The list
        // also contains nested nodes, so a visited set keeps the child/sibling descent from
        // emitting the same node twice.
        WalkAddon((nint)unit, unit->IsVisible, maxDepth, allNodes, query);

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["x"] = (int)unit->X,
            ["y"] = (int)unit->Y,
            ["scale"] = (float)unit->Scale,
            ["truncated"] = query.Truncated,
            ["scanIncomplete"] = query.ScanIncomplete,
            ["scannedNodes"] = query.Visited.Count,
            ["returned"] = query.Elements.Count,
            ["elements"] = query.Elements,
        };
    }

    private static unsafe object ReadAddonText(JObject args)
    {
        string? addonName = null;
        if (args["addon"] is { } addonToken)
        {
            if (addonToken.Type != JTokenType.String)
                throw new ToolException("addon must be a string");
            addonName = addonToken.Value<string>()?.Trim();
            if (addonName?.Length > 256)
                throw new ToolException("addon is limited to 256 characters");
        }
        if (args["addonId"] is not null)
            _ = MapTools.Integer(args, "addonId", 0, 0, ushort.MaxValue);
        if (string.IsNullOrEmpty(addonName) && args["addonId"] is null)
            throw new ToolException("pass 'addon' or 'addonId'");

        string? contains = null;
        if (args["contains"] is { } containsToken)
        {
            if (containsToken.Type != JTokenType.String)
                throw new ToolException("contains must be a string");
            contains = containsToken.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(contains))
                throw new ToolException("contains cannot be empty");
            if (contains.Length > 256)
                throw new ToolException("contains is limited to 256 characters");
        }

        var visibleOnly = CheckboxTools.StrictBoolean(args, "visibleOnly", true);
        var includeNodeText = CheckboxTools.StrictBoolean(args, "includeNodeText", true);
        var includeAtkValues = CheckboxTools.StrictBoolean(args, "includeAtkValues", true);
        if (!includeNodeText && !includeAtkValues)
            throw new ToolException("at least one of includeNodeText or includeAtkValues must be true");
        var maxNodeTexts = MapTools.Integer(args, "maxNodeTexts", 100, 1, 500);
        var maxValueTexts = MapTools.Integer(args, "maxValueTexts", 100, 1, 500);
        var maxTextLength = MapTools.Integer(args, "maxTextLength", 512, 32, 4096);
        var maxDepth = MapTools.Integer(args, "maxDepth", 12, 1, 40);

        var unit = ResolveAddon(addonName, args["addonId"]);
        if (unit is null)
        {
            var loaded = string.Empty;
            var mgr = SafeRaptureAtkUnitManager();
            if (mgr is not null)
                loaded = DescribeLoadedAddons(mgr);
            return Json.ToolError(addonName is not null
                ? $"no loaded addon named '{addonName}' was found; discover current names with list_loaded_addons.{loaded}"
                : "no addon resolved; pass an exact loaded 'addon' name or 'addonId'");
        }
        if (!unit->IsReady || unit->RootNode is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["ready"] = unit->IsReady,
                ["reason"] = "addon is loaded but its native UI tree is not ready yet",
            };
        }

        AddonElementQuery? query = null;
        if (includeNodeText)
        {
            var queryArgs = new JObject
            {
                ["detail"] = "compact",
                ["textOnly"] = true,
                ["visibleOnly"] = visibleOnly,
            };
            if (contains is not null)
            {
                queryArgs["label"] = contains;
                queryArgs["containsMatch"] = true;
            }
            query = new AddonElementQuery(queryArgs, maxNodeTexts);
            WalkAddon((nint)unit, unit->IsVisible, maxDepth, false, query);
        }

        var nodeTexts = ReadNodeTexts(query, contains, maxTextLength, out var suppressedDuplicates, out var nodeTextClipped);
        var retainedNodeTexts = query is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                query.Elements.Select(row => EscapeText(row.Value<string>("label") ?? string.Empty)),
                StringComparer.Ordinal);
        var valuesSkipped = includeAtkValues && visibleOnly && !unit->IsVisible;
        var valueRead = includeAtkValues && !valuesSkipped
            ? ReadAtkValueTexts(unit, contains, maxValueTexts, maxTextLength, retainedNodeTexts)
            : default;
        var valueTexts = valueRead.Texts ?? new JArray();
        var nodeTextsTruncated = query?.Truncated ?? false;
        var textClipped = nodeTextClipped || valueRead.TextTruncated;
        var truncated = nodeTextsTruncated || valueRead.Truncated || valuesSkipped || textClipped;

        var result = new JObject
        {
            ["available"] = true,
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["visible"] = unit->IsVisible,
            ["ready"] = unit->IsReady,
            ["scannedNodes"] = query?.Visited.Count ?? 0,
            ["returned"] = nodeTexts.Count + valueTexts.Count,
            ["nodeTextCount"] = nodeTexts.Count,
            ["valueTextCount"] = valueTexts.Count,
            ["atkValueStringCount"] = valueRead.StringValues,
            ["valueTextMatchCount"] = valueRead.Matches,
            ["nodeTexts"] = nodeTexts,
            ["valueTexts"] = valueTexts,
            ["truncated"] = truncated,
            ["scanIncomplete"] = query?.ScanIncomplete ?? false,
            ["nodeTextsTruncated"] = nodeTextsTruncated,
            ["valueTextsTruncated"] = valueRead.Truncated,
            ["textTruncated"] = textClipped,
            ["suppressedDuplicateNodeTexts"] = suppressedDuplicates,
            ["suppressedDuplicateValueTexts"] = valueRead.Duplicates,
        };
        if (valuesSkipped) result["valueTextsSkipped"] = "addonHidden";
        return result;
    }

    private static JArray ReadNodeTexts(
        AddonElementQuery? query,
        string? contains,
        int maxTextLength,
        out int suppressedDuplicates,
        out bool textTruncated)
    {
        var candidates = new List<(string Raw, string Display, int NodeId, string? ComponentType,
            uint[] Path, uint[]? ComponentFullPath, bool Clipped, bool? Visible)>();
        if (query is not null)
        {
            foreach (var element in query.Elements)
            {
                var raw = element.Value<string>("label");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (contains is not null && !raw.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                var display = ClipText(EscapeText(raw), maxTextLength, out var clipped);
                var path = element["componentPath"]?.Values<uint>().ToArray() ?? Array.Empty<uint>();
                var nodeId = element.Value<int>("nodeId");
                var componentType = element.Value<string>("componentType");
                var componentFullPath = componentType is null
                    ? null
                    : path.Append((uint)nodeId).ToArray();
                candidates.Add((raw, display, nodeId, componentType, path, componentFullPath, clipped,
                    element.Value<bool?>("visible")));
            }
        }

        suppressedDuplicates = 0;
        textTruncated = false;
        var rows = new JArray();
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            // A Button/List component and its text child can expose the same visible label.
            // Keep the component source because it is directly useful for follow-up clicks,
            // and suppress only a same-text descendant with no additional information.
            var childOfMatchingComponent = false;
            for (var parentIndex = 0; parentIndex < candidates.Count; parentIndex++)
            {
                if (parentIndex == candidateIndex) continue;
                var parent = candidates[parentIndex];
                if (parent.ComponentFullPath is not null &&
                    parent.Path.Length < candidate.Path.Length &&
                    string.Equals(parent.Raw, candidate.Raw, StringComparison.Ordinal) &&
                    candidate.Path.AsSpan().StartsWith(parent.ComponentFullPath))
                {
                    childOfMatchingComponent = true;
                    break;
                }
            }
            if (childOfMatchingComponent) { suppressedDuplicates++; continue; }

            var row = new JObject
            {
                ["text"] = candidate.Display,
                ["nodeId"] = candidate.NodeId,
                ["componentPath"] = new JArray(candidate.Path),
                ["visible"] = candidate.Visible,
            };
            if (candidate.ComponentType is not null) row["componentType"] = candidate.ComponentType;
            if (candidate.Clipped)
            {
                row["textTruncated"] = true;
                textTruncated = true;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static unsafe (JArray? Texts, int StringValues, int Matches, int Duplicates, bool Truncated, bool TextTruncated)
        ReadAtkValueTexts(AtkUnitBase* unit, string? contains, int maxTexts, int maxTextLength,
            IReadOnlySet<string> retainedNodeTexts)
    {
        var texts = new JArray();
        var values = unit->AtkValuesSpan;
        var strings = 0;
        var matches = 0;
        var duplicates = 0;
        var truncated = false;
        var textTruncated = false;
        for (var i = 0; i < values.Length; i++)
        {
            try
            {
                AtkValue* value;
                fixed (AtkValue* p = &values[i]) value = p;
                if (!IsStringValue(value->Type)) continue;
                strings++;
                var decoded = DecodeAddonValue(value);
                if (string.IsNullOrWhiteSpace(decoded)) continue;
                if (contains is not null && !decoded.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                if (retainedNodeTexts.Contains(decoded))
                {
                    duplicates++;
                    continue;
                }
                var display = ClipText(decoded, maxTextLength, out var clipped);
                if (texts.Count >= maxTexts) { truncated = true; continue; }
                var row = new JObject
                {
                    ["text"] = display,
                    ["index"] = i,
                    ["type"] = value->Type.ToString(),
                };
                if (clipped)
                {
                    row["textTruncated"] = true;
                    textTruncated = true;
                }
                texts.Add(row);
            }
            catch
            {
                // An unreadable value does not hide the rest of the addon text.
            }
        }
        return (texts, strings, matches, duplicates, truncated, textTruncated);
    }

    private static string ClipText(string text, int maxLength, out bool truncated)
    {
        if (text.Length <= maxLength)
        {
            truncated = false;
            return text;
        }
        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        truncated = true;
        return text[..length];
    }

    internal static unsafe object ClickAddonElement(JObject args)
    {
        var addonName = args["addon"]?.Value<string>();
        var addonIdArg = args["addonId"];
        var nodeIdArg = args["nodeId"];
        var indexArg = args["index"];
        var eventName = args["event"]?.Value<string>() ?? "click";
        var paramArg = args["param"];
        var path = AddonProbeTools.ReadComponentPath(args);
        if (path != null && nodeIdArg == null) throw new ToolException("componentPath requires nodeId");

        AtkEventType? eventType = eventName switch
        {
            null or "" or "click" => AtkEventType.MouseClick,
            "doubleClick" => AtkEventType.MouseDoubleClick,
            "buttonClick" => AtkEventType.ButtonClick,
            "buttonPress" => AtkEventType.ButtonPress,
            "buttonRelease" => AtkEventType.ButtonRelease,
            "registered" => AtkEventType.MouseClick, // event itself ignored; every registered handler fires
            _ => null,
        };
        if (eventType is null)
            return Json.ToolError($"unknown event '{eventName}'; use click, doubleClick, buttonClick, buttonPress or buttonRelease");

        var unit = ResolveAddon(addonName, addonIdArg);
        if (unit is null)
        {
            var loaded = string.Empty;
            var mgr = SafeRaptureAtkUnitManager();
            if (mgr is not null)
                loaded = DescribeLoadedAddons(mgr);
            return addonName is not null
                ? Json.ToolError($"no loaded addon named '{addonName}' was found; open it with " +
                                 $"open_addon first, or pass the addon's raw addonId.{loaded}")
                : Json.ToolError("no addon resolved; pass 'addon' (a name from the open_addon " +
                                 "allowlist) or 'addonId'");
        }

        if (!unit->IsReady || !unit->IsVisible)
            return Json.ToolError("addon is not ready or is hidden");

        // List-item shortcut: index into a list component dispatches the item event directly.
        if (indexArg is not null && nodeIdArg is null)
        {
            var index = indexArg.Value<int>();
            var list = FindListByIndex(unit, index);
            if (list is null)
                return Json.ToolError($"no visible list component with item index {index} was " +
                                      "found in this addon; list it with list_addon_elements");

            var listNodeId = list->OwnerNode is not null ? list->OwnerNode->NodeId : 0;
            list->SelectItem(index, false);
            list->DispatchItemEvent(index, AtkEventType.ListItemClick);

            return new JObject
            {
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["listNodeId"] = (int)listNodeId,
                ["index"] = index,
                ["clicked"] = true,
                ["via"] = "AtkComponentList.DispatchItemEvent(ListItemClick)",
            };
        }

        if (nodeIdArg is null)
            return Json.ToolError("nothing to click: pass 'nodeId' (from list_addon_elements) " +
                                  "or 'index' (for list components)");

        var nodeId = nodeIdArg.Value<uint>();
        // Search the addon's UldManager node list first — it contains every top-level and
        // nested node, whereas the RootNode child chain only exposes one linked child.
        // Duplicate ids are common (per-row components reuse ids 2-7), so among all matches
        // prefer a visible + clickable node; a bare Res/text match is the last resort.
        var scoped = args.Value<uint?>("componentId");
        var bestScore = 0;
        var node = scoped.HasValue || path != null ? AddonProbeTools.FindNode(unit, nodeId, scoped, path) : FindBestInUld(unit, nodeId, out bestScore);
        if (!scoped.HasValue && path == null && (node is null || bestScore < 3))
        {
            var fromRoot = FindBestNode(unit->RootNode, nodeId, out var rootScore);
            if (rootScore > bestScore)
            {
                node = fromRoot;
                bestScore = rootScore;
            }
        }

        if (node is null)
            return Json.ToolError($"no node with id {nodeId} was found in this addon; list it " +
                                  "with list_addon_elements");
        if (NativeUiVisibility.Combine(ScopeVisibility(unit, path ?? (scoped.HasValue ? new[] { scoped.Value } : null)),
                NativeUiVisibility.Read((nint)node)) != true)
            return Json.ToolError($"node {nodeId} is not currently visible");
        if (IsComponentType(node) && ComponentKindOf((AtkComponentNode*)node) == ComponentType.CheckBox)
            return Json.ToolError("Use set_addon_checkbox with an explicit checked boolean; addon event dispatch alone does not update native checkbox state");

        // ClickLib's proven dispatch shape (ClickBase.SendClick): ALWAYS dispatch through
        // the ADDON's own vtable ReceiveEvent (vfunc[2]) with the addon as both receiver
        // and evt.Listener — never the node's registered listener or the component. The
        // addon's override routes the event to the right handler internally. The event's
        // Target@0x8 is the clicked node; for component nodes ClickLib targets the
        // component's OwnerNode (the component node itself), and the callback Param is
        // what the game registered for that node (close buttons typically 0xFFFFFFFF with
        // EventType.Change).
        var sendType = eventType.Value;

        // Explicit param override wins (ClickLib hardcodes 0xFFFFFFFF for close buttons
        // where the auto-derived registered param may differ); otherwise read the param
        // the game registered for this node/event.
        uint eventParam;
        if (paramArg is not null)
        {
            eventParam = unchecked((uint)paramArg.Value<long>());
        }
        else
        {
            eventParam = NativeUiEvents.Parameter((nint)node, eventType.Value);
        }

        // For component nodes prefer the component's OwnerNode as the event target and the
        // param registered on the component node (button callback ids live there).
        var targetNode = (AtkResNode*)node;
        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (compNode is not null && compNode->Component is not null &&
                compNode->Component->OwnerNode is not null)
            {
                targetNode = (AtkResNode*)compNode->Component->OwnerNode;
            }
        }

        var screenX = (short)(node->ScreenX + (node->Width / 2f));
        var screenY = (short)(node->ScreenY + (node->Height / 2f));

        // ClickLib passes a FULLY ZEROED arg5 — synthetic clicks carry no mouse data.

        // ClickLib's SendClick, byte-for-byte: a zeroed 0x40 buffer with the target node
        // at +0x8 and the addon at +0x10 — NOT a full AtkEvent (Param and State stay
        // zero; the callback id travels only in the a3 argument). Buffer sizes larger
        // than the managed AtkEvent struct matter for handlers that read past it.
        byte* evtBuf = stackalloc byte[0x40];
        for (var i = 0; i < 0x40; i++)
            evtBuf[i] = 0;
        // CN client 7.x: the addon's ReceiveEvent dereferences evt->Node (a synthetic event
        // with a null Node crashed the game in live testing), so fill Node@0x0 as well —
        // not just Target@0x8 / Listener@0x10 as ClickLib's older intl-client layout did.
        *(AtkResNode**)&evtBuf[0x0] = targetNode;
        *(AtkEventTarget**)&evtBuf[0x8] = (AtkEventTarget*)targetNode;
        *(AtkEventListener**)&evtBuf[0x10] = (AtkEventListener*)unit;

        // ClickLib passes a FULLY ZEROED 0x40-byte arg5 — synthetic clicks carry no mouse
        // data. AtkEventData is smaller than 0x40, so a handler that reads past the managed
        // struct would touch stack garbage; zero the full native-size buffer instead.
        byte* eventDataBuf = stackalloc byte[0x40];
        for (var i = 0; i < 0x40; i++)
            eventDataBuf[i] = 0;

        if (eventName == "registered")
        {
            // Genuine-mouse-click replication: fire every (type, param, listener) triple the
            // node actually has registered — a real click routes through the node's event
            // manager to ITS listeners (component buttons, lists, ...), which then fire the
            // addon-level callback. Dispatching to the addon vtable only covers addons whose
            // ReceiveEvent override switches on addon-level (type, param) pairs.
            var fired = 0;
            Span<byte> regSpan = stackalloc byte[0x40];
            for (var e = node->AtkEventManager.Event; e is not null; e = e->NextEvent)
            {
                if (e->Listener is null)
                    continue;
                regSpan.Clear();
                fixed (byte* regBuf = regSpan)
                {
                    *(AtkResNode**)&regBuf[0x0] = targetNode;
                    *(AtkEventTarget**)&regBuf[0x8] = (AtkEventTarget*)targetNode;
                    *(AtkEventListener**)&regBuf[0x10] = e->Listener;
                    var listenerVtbl = (AtkEventListener.AtkEventListenerVirtualTable*)e->Listener->VirtualTable;
                    listenerVtbl->ReceiveEvent(e->Listener, e->State.EventType, (int)e->Param, (AtkEvent*)regBuf, (AtkEventData*)eventDataBuf);
                    fired++;
                }
            }

            return new JObject
            {
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["nodeId"] = (int)nodeId,
                ["nodeType"] = node->Type.ToString(),
                ["event"] = "registered",
                ["fired"] = fired,
                ["screenX"] = (int)screenX,
                ["screenY"] = (int)screenY,
                ["clicked"] = fired > 0,
                ["via"] = "registered listeners",
            };
        }

        // Dispatch through the ADDON's vtable ReceiveEvent slot (offset 16, slot 2) —
        // ClickLib's exact path. The addon's virtual override performs its own handler
        // routing; dispatching to nested listeners bypasses it and drops the event.
        var vtbl = (AtkEventListener.AtkEventListenerVirtualTable*)unit->VirtualTable;
        vtbl->ReceiveEvent((AtkEventListener*)unit, sendType, (int)eventParam, (AtkEvent*)evtBuf, (AtkEventData*)eventDataBuf);

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["nodeId"] = (int)nodeId,
            ["nodeType"] = node->Type.ToString(),
            ["event"] = sendType.ToString(),
            ["eventParam"] = (int)eventParam,
            ["screenX"] = (int)screenX,
            ["screenY"] = (int)screenY,
            ["clicked"] = true,
            ["via"] = "addon vtable",
        };
    }

    // ---------------------------------------------------------------- addon value table

    /// <summary>
    /// Reads the addon's AtkValue table: the values the client handed the addon on its last
    /// SetAtkValues call. For menu-style addons these are the option labels, which is what
    /// makes both reading the choices and selecting one possible without an addon-specific
    /// layout.
    /// </summary>
    private static unsafe object GetAddonStrings(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved; pass 'addon' (a name from the open_addon " +
                                  "allowlist) or 'addonId'");

        if (!unit->IsReady)
            return Json.ToolError($"addon '{unit->NameString}' is not ready yet");

        var maxValues = ClampInt(args["maxValues"], 1, 500, 200);
        var stringsOnly = args["stringsOnly"]?.Value<bool>() ?? false;

        var values = unit->AtkValuesSpan;
        var entries = new JArray();
        var strings = 0;
        for (var i = 0; i < values.Length; i++)
        {
            try
            {
                AtkValue* value;
                fixed (AtkValue* p = &values[i]) value = p;
                if (IsStringValue(value->Type)) strings++;

                if (entries.Count >= maxValues) continue;
                if (stringsOnly && !IsStringValue(value->Type)) continue;

                var decoded = DecodeAddonValue(value);
                if (string.IsNullOrEmpty(decoded)) continue;

                entries.Add(new JObject
                {
                    ["index"] = i,
                    ["type"] = value->Type.ToString(),
                    ["value"] = decoded,
                });
            }
            catch
            {
                // A value the decoder cannot reach is skipped rather than failing the read.
            }
        }

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["valueCount"] = values.Length,
            ["stringCount"] = strings,
            ["count"] = entries.Count,
            ["entries"] = entries,
        };
    }

    private static unsafe bool IsStringValue(AtkValueType type) =>
        type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
            or AtkValueType.WideString;

    /// <summary>Decode one addon AtkValue to its display text (or invariant scalar text).</summary>
    private static unsafe string? DecodeAddonValue(AtkValue* value)
    {
        switch (value->Type)
        {
            case AtkValueType.String:
            case AtkValueType.ManagedString:
            case AtkValueType.ConstString:
                return EscapeText(value->String.ToString());
            case AtkValueType.WideString:
                return EscapeText(Marshal.PtrToStringUni((IntPtr)value->WideString) ?? string.Empty);
            case AtkValueType.Bool:
                return value->Byte != 0 ? "true" : "false";
            case AtkValueType.Int:
                return value->Int.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case AtkValueType.Int64:
                return value->Int64.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case AtkValueType.UInt:
                return value->UInt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case AtkValueType.UInt64:
                return value->UInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case AtkValueType.Float:
                return value->Float.ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    /// <summary>
    /// Escapes control characters so a raw UI payload stays readable in JSON, mirroring how
    /// the game's own text payloads separate attributes with 0x02.
    /// </summary>
    private static string EscapeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c)) builder.Append("\\u").Append(((int)c).ToString("X4"));
            else builder.Append(c);
        }

        return builder.ToString();
    }

    private static unsafe object SelectAddonMenuItem(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved; pass 'addon' or 'addonId'");

        if (!unit->IsReady)
            return Json.ToolError($"addon '{unit->NameString}' is not ready yet");

        var values = unit->AtkValuesSpan;
        var candidates = new List<(int Index, string Label)>();
        nint nativeList = 0;
        var nativeMenu = unit->NameString == "SelectString";
        if (nativeMenu)
            candidates = ReadSelectStringEntries(unit, out nativeList);
        for (var i = 0; !nativeMenu && i < values.Length; i++)
        {
            AtkValue* value = null;
            try
            {
                fixed (AtkValue* p = &values[i]) value = p;
                if (!IsStringValue(value->Type)) continue;
                var entryLabel = DecodeAddonValue(value);
                if (string.IsNullOrWhiteSpace(entryLabel)) continue;
                candidates.Add((i, NormalizeMenuLabel(entryLabel)));
            }
            catch
            {
                // Skip unreadable values; the menu may still be selectable by index.
            }
        }

        if (candidates.Count == 0)
        {
            return Json.ToolError($"addon '{unit->NameString}' carries no string entries in its " +
                                  "value table, so no menu could be read from it");
        }

        var indexArg = args["index"];
        var label = args["label"]?.Value<string>();
        var containsMatch = args["containsMatch"]?.Value<bool>() ?? false;
        var dryRun = args["dryRun"]?.Value<bool>() ?? false;

        (int Index, string Label) selected;
        if (indexArg is not null && indexArg.Type != JTokenType.Null)
        {
            var index = indexArg.Value<int>();
            if (index < 0 || index >= (nativeMenu ? candidates.Count : values.Length))
                throw new ToolException(nativeMenu
                    ? $"menu index {index} must be between 0 and {candidates.Count - 1}"
                    : $"index {index} is outside the addon's value table (0-{values.Length - 1})");

            var hit = candidates.FirstOrDefault(c => c.Index == index);
            selected = hit.Label is null ? (index, string.Empty) : hit;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ToolException("provide either label or index");

            var wanted = NormalizeMenuLabel(label);
            var exact = candidates.Where(c => string.Equals(c.Label, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 0 && containsMatch)
                exact = candidates.Where(c => c.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            if (exact.Count == 0)
            {
                var options = string.Join(" | ", candidates.Take(20).Select(c => c.Label));
                throw new ToolException($"no menu entry matches '{label}'. Available: {options}");
            }

            if (exact.Count > 1)
            {
                var options = string.Join(" | ", exact.Select(c => $"{c.Label} (index {c.Index})"));
                throw new ToolException($"'{label}' matches more than one entry: {options}. Pass index to disambiguate.");
            }

            selected = exact[0];
        }

        if (dryRun)
        {
            return new JObject
            {
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["index"] = selected.Index,
                ["indexKind"] = nativeMenu ? "menuOrdinal" : "atkValueIndex",
                ["label"] = selected.Label,
                ["dryRun"] = true,
                ["selected"] = false,
            };
        }

        // SelectString AtkValues include the title and metadata before the labels.
        // Its popup entries, not those value-table indices, define the actual selection.
        if (nativeMenu)
        {
            var list = (AtkComponentList*)nativeList;
            list->SelectItem(selected.Index, false);
            list->DispatchItemEvent(selected.Index, AtkEventType.ListItemClick);
            return new JObject
            {
                ["addon"] = unit->NameString, ["addonId"] = (int)unit->Id,
                ["index"] = selected.Index, ["indexKind"] = "menuOrdinal",
                ["label"] = selected.Label, ["selected"] = true,
                ["inputDispatched"] = true, ["via"] = "AtkComponentList.DispatchItemEvent(ListItemClick)",
            };
        }
        var fired = unit->FireCallbackInt(selected.Index);
        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["index"] = selected.Index,
            ["label"] = selected.Label,
            ["selected"] = true,
            ["callbackAccepted"] = fired,
        };
    }

    private static unsafe List<(int Index, string Label)> ReadSelectStringEntries(AtkUnitBase* unit, out nint list)
    {
        list = 0;
        if (!TryNative((AddonSelectString*)unit, out var addon))
            throw new ToolException("SelectString layout is unreadable");
        var menu = addon.PopupMenu;
        if (menu.Owner != unit || menu.EntryCount is < 1 or > 256 || menu.EntryNames is null ||
            !TryNative(menu.List, out var nativeList) || nativeList.ListLength != menu.EntryCount ||
            !TryNative(nativeList.OwnerNode, out var owner) || owner.Component != (AtkComponentBase*)menu.List ||
            ComponentKindOf(nativeList.OwnerNode) != ComponentType.List || !NodeVisible((AtkResNode*)nativeList.OwnerNode))
            throw new ToolException("SelectString popup/list layout is not ready or inconsistent");
        if ((owner.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("SelectString list is disabled");
        var entries = new List<(int, string)>();
        for (var i = 0; i < menu.EntryCount; i++)
        {
            if (!TryNative(menu.EntryNames + i, out var pointer) || ReadCString(pointer) is not { } label)
                throw new ToolException($"SelectString entry {i} is unreadable");
            entries.Add((i, NormalizeMenuLabel(label)));
        }
        list = (nint)menu.List;
        return entries;
    }

    /// <summary>
    /// Trims a menu label to its display text: control characters removed, surrounding
    /// whitespace collapsed, and the decorative prefixes the client adds to highlighted or
    /// keyboard-hinted entries stripped.
    /// </summary>
    private static string NormalizeMenuLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return string.Empty;

        var cleaned = new System.Text.StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (char.IsControl(c)) continue;
            cleaned.Append(c);
        }

        var text = cleaned.ToString().Trim();
        const string decorations = " \u3000・•★☆▶►◆◇◉○●■□";
        text = text.TrimStart(decorations.ToCharArray());
        text = text.Trim();

        // A single-letter shortcut prefix ("A Continue") is not part of the label.
        if (text.Length > 2 && char.IsLetter(text[0]) && text[1] == ' ')
            text = text[2..].TrimStart();

        return text;
    }

    // ---------------------------------------------------------------- node helpers

    /// <summary>
    /// Resolves an AtkUnitBase either by allowlist name (through the agent layer's addon
    /// names) or by raw addon id, via RaptureAtkUnitManager.
    /// </summary>
    internal static unsafe AtkUnitBase* ResolveAddon(string? addonName, JToken? addonIdArg)
    {
        var manager = SafeRaptureAtkUnitManager();
        if (manager is null)
            return null;

        if (addonIdArg is not null)
        {
            var id = addonIdArg.Value<int>();
            if (id < 0 || id > ushort.MaxValue)
                return null;
            return manager->GetAddonById((ushort)id);
        }

        if (string.IsNullOrWhiteSpace(addonName))
            return null;

        // The client registers addons under their internal names ("CurrencyList",
        // "_AddonExample", ...), which rarely equal the agent-allowlist keys, so resolve by
        // scanning the loaded list for a case-insensitive name match. Direct GetAddonByName
        // is tried first because it is the game's own lookup.
        var byName = manager->GetAddonByName(addonName, 1);
        if (byName is not null)
            return byName;

        var list = manager->AllLoadedUnitsList;
        var entries = list.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var p = entries[i];
            if (p.IsNull)
                continue;
            var unit = p.Value;
            if (unit is null)
                continue;
            var name = unit->NameString;
            if (string.Equals(name, addonName, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(addonName, StringComparison.OrdinalIgnoreCase))
                return unit;
        }

        return null;
    }

    /// <summary>Best-effort snapshot of the loaded addon names for an error message.</summary>
    private static unsafe string DescribeLoadedAddons(RaptureAtkUnitManager* manager)
    {
        var names = new List<string>();
        TryCollectAddonNames(manager->AllLoadedUnitsList, names);
        if (names.Count == 0)
            TryCollectAddonNames(manager->FocusedUnitsList, names);

        return names.Count > 0 ? $" Loaded addons include: {string.Join(", ", names)}." : " (No loaded addons were enumerable.)";
    }

    private static unsafe void TryCollectAddonNames(AtkUnitList list, List<string> names)
    {
        try
        {
            var entries = list.Entries;
            foreach (var p in entries)
            {
                if (p.IsNull)
                    continue;
                try
                {
                    var unit = p.Value;
                    if (unit is null)
                        continue;
                    var name = unit->NameString;
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        names.Add(name);
                    if (names.Count >= 30)
                        return;
                }
                catch (Exception)
                {
                    // unreadable entry — skip it, keep collecting
                }
            }
        }
        catch (Exception)
        {
            // unreadable list — nothing to add
        }
    }

    internal static unsafe void WalkAddon(nint address, bool visible, int maxDepth, bool allNodes, AddonElementQuery query)
    {
        var addon = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)address, out _);
        if (!addon.HasValue) { query.ScanIncomplete = true; return; }
        // Dynamic addons can exceed 512 root entries (DDungeon: 531). This
        // remains inside the existing 4096-node total discovery budget.
        var count = Math.Min((int)addon.Value.UldManager.NodeListCount, 4096);
        if (addon.Value.UldManager.NodeListCount > 4096) query.ScanIncomplete = true;
        for (var i = 0; i < count && !query.ResultLimit; i++)
        {
            if (query.Visited.Count >= 4096) { query.ScanIncomplete = true; break; }
            if (!TryNative((nint*)addon.Value.UldManager.NodeList + i, out var node)) { query.ScanIncomplete = true; break; }
            WalkNode((AtkResNode*)node, (AtkUnitBase*)address, 0, maxDepth, allNodes, query, Array.Empty<uint>(), visible);
        }
        if (!query.ResultLimit) WalkNode(addon.Value.RootNode, (AtkUnitBase*)address, 0, maxDepth, allNodes, query, Array.Empty<uint>(), visible);
    }

    /// <summary>Bounded ULD traversal with filtering and component scope projection.</summary>
    private static unsafe void WalkNode(
        AtkResNode* node,
        AtkUnitBase* unit,
        int depth,
        int maxDepth,
        bool allNodes,
        AddonElementQuery query,
        uint[] componentPath,
        bool? scopeVisibility)
    {
        if (node is null || query.ResultLimit)
            return;
        if (query.Visited.Contains((nuint)node)) return;
        if (depth > maxDepth || query.Visited.Count >= 4096) { query.ScanIncomplete = true; return; }
        if (!query.Visited.Add((nuint)node))
            return;
        if (!TryNative(node, out var nodeData)) { query.ScanIncomplete = true; return; }

        var visible = NativeUiVisibility.Combine(scopeVisibility, NativeUiVisibility.Read((nint)node, query.VisibilityCache));
        if (visible == null) query.ScanIncomplete = true;
        // Effective visibility includes owning component scopes. A known-hidden
        // component cannot contribute a visible descendant, even with detached ULD trees.
        // Keep unknown ancestry conservative and retain full traversal for other queries.
        if (query.VisibleOnly && visible == false) return;

        var label = NodeLabel(node);
        var clickable = NodeClickable(node);
        if (allNodes || label is not null || clickable)
        {
            query.Add(NodeJson(node, unit, label, clickable, depth, visible), componentPath);
        }

        // A component node's contents live on the component's UldManager node list (its
        // AtkResNode* back-reference chains the component's own tree), not among the res-node
        // children — descend so buttons/lists inside components are listed.
        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (TryNative(compNode, out var componentNode) && TryNative(componentNode.Component, out var component))
            {
                // The component's widget tree lives in its own UldManager node list; the
                // AtkResNode back-reference is frequently null on live clients.
                var compUld = component.UldManager;
                var compCount = Math.Min((int)compUld.NodeListCount, 512);
                if (compCount > 0 && compUld.NodeList is not null)
                {
                    var path = componentPath.Append(nodeData.NodeId).ToArray();
                    if (compUld.NodeListCount > 512) query.ScanIncomplete = true;
                    for (var i = 0; i < compCount && !query.ResultLimit; i++)
                        if (TryNative((nint*)compUld.NodeList + i, out var child))
                            WalkNode((AtkResNode*)child, unit, depth + 1, maxDepth, allNodes, query, path, visible);
                        else query.ScanIncomplete = true;
                }
                else if (compCount > 0) query.ScanIncomplete = true;
            }
            else query.ScanIncomplete = true;
        }

        // ULD node arrays are authoritative. Child/sibling chains may contain aliases,
        // cycles or stale links and must not be followed by this discovery tool.
    }


    private static unsafe JObject NodeJson(AtkResNode* node, AtkUnitBase* unit, string? label, bool clickable, int depth, bool? visible)
    {
        return new JObject
        {
            ["nodeId"] = (int)node->NodeId,
            ["type"] = node->Type.ToString(),
            ["componentType"] = IsComponentType(node) ? ComponentKindOf(ComponentNodeOf(node))?.ToString() : null,
            ["depth"] = depth,
            ["label"] = label,
            ["x"] = (int)node->ScreenX,
            ["y"] = (int)node->ScreenY,
            ["width"] = (int)node->Width,
            ["height"] = (int)node->Height,
            ["visible"] = visible,
            ["selfVisible"] = (node->NodeFlags & NodeFlags.Visible) != 0,
            ["enabled"] = (node->NodeFlags & NodeFlags.Enabled) != 0,
            ["clickable"] = clickable,
        };
    }

    /// <summary>
    /// Component nodes report combined runtime type values (observed 1004 on live clients)
    /// rather than the bare 10000 enum constant, so component detection uses a >=1000 test.
    /// </summary>
    private static unsafe bool IsComponentType(AtkResNode* node) => TryNative(node, out var data) && (int)data.Type >= 1000;

    private static unsafe bool TryNative<T>(T* pointer, out T value) where T : unmanaged
    {
        var read = MemoryProbe.TryReadStruct<T>((ulong)pointer, out _);
        value = read.GetValueOrDefault(); return read.HasValue;
    }

    /// <summary>
    /// Casts a component node directly instead of via <c>GetAsAtkComponentNode</c>:
    /// live clients report combined type values (e.g. 1004) that the helper's strict
    /// enum check rejects, while the memory layout (AtkResNode at offset 0) is identical.
    /// </summary>
    private static unsafe AtkComponentNode* ComponentNodeOf(AtkResNode* node) =>
        IsComponentType(node) ? (AtkComponentNode*)node : null;

    /// <summary>
    /// Guarded effective visibility. Replaces the native <c>AtkResNode.IsVisible()</c>
    /// member function: that helper is a reverse P/Invoke into the client and dereferences
    /// <c>this</c>, so a stale pointer raises a native access violation that
    /// <c>catch (Exception)</c> cannot intercept — it terminates ffxiv_dx11.exe.
    /// </summary>
    private static unsafe bool NodeVisible(AtkResNode* node) => NativeUiVisibility.Read((nint)node) == true;

    internal static unsafe bool? ScopeVisibility(AtkUnitBase* unit, uint[]? path,
        Dictionary<nint, bool?>? cache = null)
    {
        var owner = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!owner.HasValue) return null;
        bool? visible = unit->IsVisible;
        var uld = owner.Value.UldManager;
        foreach (var id in path ?? Array.Empty<uint>())
        {
            var node = CheckboxTools.Find(uld, id);
            var data = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)node, out _);
            if (!data.HasValue || (int)data.Value.Type < 1000) return NativeUiVisibility.Combine(visible, null);
            var component = MemoryProbe.TryReadStruct<AtkComponentBase>((ulong)data.Value.Component, out _);
            if (!component.HasValue || component.Value.OwnerNode != (AtkComponentNode*)node)
                return NativeUiVisibility.Combine(visible, null);
            visible = NativeUiVisibility.Combine(visible, NativeUiVisibility.Read((nint)node, cache));
            uld = component.Value.UldManager;
        }
        return visible;
    }

    /// <summary>
    /// Pure field read of the component's kind. Replaces the native
    /// <c>AtkComponentBase.GetComponentType()</c> and every <c>GetAsAtkComponentX()</c> probe:
    /// all of those are reverse P/Invokes that dereference the pointer handed to them.
    /// The kind lives in the ULD component info, which is only populated when the manager's
    /// BaseType is Component.
    /// </summary>
    internal static unsafe ComponentType? ComponentKindOf(AtkComponentNode* compNode)
    {
        if (!TryNative(compNode, out var node) || !TryNative(node.Component, out var comp)) return null;
        if (comp.UldManager.BaseType != AtkUldManagerBaseType.Component)
            return null;

        // Objects points to AtkUldComponentInfo for component ULDs. ComponentData
        // is navigation data (AtkUldComponentDataBase), not a component type header.
        return TryNative((AtkUldComponentInfo*)comp.UldManager.Objects, out var data) ? data.ComponentType : null;
    }

    /// <summary>Reads a game-side Utf8String without any native call.</summary>
    private static unsafe string? ReadUtf8(Utf8String* text)
    {
        if (!TryNative(text, out var value)) return null;
        var s = MemoryProbe.TryReadCString((ulong)(byte*)value.StringPtr, 4096, out _);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static unsafe string? ReadTextNode(AtkTextNode* pointer)
    {
        // Taking &pointer->NodeText emits ldflda, which can fault on the pointer
        // before ReadUtf8 gets a chance to validate it. Copy the entire node first.
        return TryNative(pointer, out var node) ? ReadUtf8(&node.NodeText) : null;
    }

    private static unsafe string? ReadCString(CStringPointer pointer) =>
        pointer.HasValue ? MemoryProbe.TryReadCString((ulong)(byte*)pointer, 4096, out _) : null;

    /// <summary>
    /// Whether the node has a MouseClick handler registered, read straight off the
    /// AtkEventManager chain. Replaces the native <c>AtkResNode.IsEventRegistered</c>.
    /// </summary>
    private static unsafe bool NodeHasMouseClickEvent(AtkResNode* node)
    {
        if (!TryNative(node, out var data)) return false;
        var next = data.AtkEventManager.Event;
        for (var i = 0; i < 64 && TryNative(next, out var e); i++, next = e.NextEvent)
        {
            if (e.State.EventType == AtkEventType.MouseClick)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Label text for text/button/list nodes; null when the node carries no text. Every step
    /// uses guarded complete struct/string copies. Plain pointer field reads and managed
    /// catch blocks alone do not protect the client from access violations.
    /// </summary>
    private static unsafe string? NodeLabel(AtkResNode* node)
    {
        try
        {
            if (!TryNative(node, out var data))
                return null;

            if (data.Type == NodeType.Text)
                return ReadTextNode((AtkTextNode*)node);

            if (!IsComponentType(node))
                return null;

            var compNode = ComponentNodeOf(node);
            if (!TryNative(compNode, out var componentNode))
                return null;

            var kind = ComponentKindOf(compNode);
            if (kind is null)
                return null;

            // Button: its own text node.
            if (kind == ComponentType.Button)
            {
                if (!TryNative((AtkComponentButton*)componentNode.Component, out var button)) return null;
                var textNode = button.ButtonTextNode;
                var text = ReadTextNode(textNode);
                if (text is not null)
                    return text;
            }

            // List: first item labels, so the agent can see what's selectable.
            if (kind == ComponentType.List)
            {
                if (!TryNative((AtkComponentList*)componentNode.Component, out var list) || list.ItemLabels is null)
                    return null;

                var items = new JArray();
                var count = Math.Min(list.ListLength, 8);
                for (var i = 0; i < count; i++)
                {
                    if (!TryNative(list.ItemLabels + i, out var pointer)) continue;
                    var label = ReadCString(pointer);
                    if (label is not null)
                        items.Add(label);
                }

                return items.Count > 0 ? items.ToString(Newtonsoft.Json.Formatting.None) : null;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether a node can plausibly receive click events.</summary>
    private static unsafe bool NodeClickable(AtkResNode* node)
    {
        if (!TryNative(node, out var data))
            return false;

        if (data.Type == NodeType.Collision)
            return (data.NodeFlags & NodeFlags.RespondToMouse) != 0 || NodeHasMouseClickEvent(node);

        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (!TryNative(compNode, out var component) || component.Component is null)
                return false;

            var clickableKind = ComponentKindOf(compNode) is ComponentType.Button
                or ComponentType.CheckBox
                or ComponentType.RadioButton
                or ComponentType.List
                or ComponentType.TreeList
                or ComponentType.DropDownList
                or ComponentType.IconText
                or ComponentType.Slider
                or ComponentType.TextInput
                or ComponentType.NumericInput
                or ComponentType.Tab
                or ComponentType.HoldButton
                or ComponentType.Icon
                or ComponentType.DragDrop
                or ComponentType.TextNineGrid
                or ComponentType.ListItemRenderer;
            return clickableKind || NodeHasMouseClickEvent(node);
        }

        return false;
    }

    /// <summary>Finds the first node with the given id in the addon's node tree.</summary>
    private static unsafe AtkResNode* FindNode(AtkResNode* node, uint nodeId)
    {
        while (node is not null)
        {
            if (node->NodeId == nodeId)
                return node;

            var inChild = FindNode(node->ChildNode, nodeId);
            if (inChild is not null)
                return inChild;

            node = node->NextSiblingNode;
        }

        return null;
    }

    /// <summary>
    /// Scores every node with <paramref name="nodeId"/> across the addon's UldManager node
    /// list (visible +2, clickable +1) and returns the best match.
    /// </summary>
    private static unsafe AtkResNode* FindBestInUld(AtkUnitBase* unit, uint nodeId, out int bestScore)
    {
        AtkResNode* best = null;
        bestScore = -1;
        var uldCount = Math.Min((int)unit->UldManager.NodeListCount, 512);
        for (var i = 0; i < uldCount; i++)
        {
            var candidate = FindBestNode(unit->UldManager.NodeList[i], nodeId, out var score);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }

            if (bestScore == 3)
                break;
        }

        return best;
    }

    /// <summary>
    /// Walks the tree rooted at <paramref name="root"/>, scoring every node with the given
    /// id (visible +2, clickable +1) and keeping the best match. Duplicate ids are common
    /// (per-row components reuse ids 2-7), so the highest-scoring match wins.
    /// </summary>
    private static unsafe AtkResNode* FindBestNode(AtkResNode* root, uint nodeId, out int bestScore)
    {
        AtkResNode* best = null;
        var bestLocal = -1;

        Visit(root);
        bestScore = bestLocal;
        return best;

        // Local function (not lambda): lambdas capturing pointer locals are fine, but a
        // recursive lambda needs an explicit delegate type; a local function needs none.
        // bestScore is copied to bestLocal first: out params cannot be captured.
        void Visit(AtkResNode* node)
        {
            while (node is not null)
            {
                if (node->NodeId == nodeId)
                {
                    var score = 0;
                    if (NodeVisible(node)) score += 2;
                    if (NodeClickable(node)) score += 1;
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        best = node;
                    }
                }

                Visit(node->ChildNode);
                node = node->NextSiblingNode;
            }
        }
    }

    /// <summary>
    /// Finds one unambiguous visible list in the bounded ULD array. Child/sibling
    /// pointers do not reliably expose popup lists and are not followed here.
    /// </summary>
    private static unsafe AtkComponentList* FindListByIndex(AtkUnitBase* unit, int index)
    {
        if (index < 0 || !TryNative(unit, out var addon) || addon.UldManager.NodeListCount > 512)
            return null;
        AtkComponentList* found = null;
        for (var i = 0; i < addon.UldManager.NodeListCount; i++)
        {
            if (!TryNative((nuint*)(addon.UldManager.NodeList + i), out var pointer)) return null;
            var node = (AtkResNode*)pointer;
            var component = ComponentNodeOf(node);
            if (component is null || !NodeVisible(node) || ComponentKindOf(component) != ComponentType.List ||
                !TryNative(component, out var componentData) ||
                !TryNative((AtkComponentList*)componentData.Component, out var data) ||
                data.ListLength is < 1 or > 4096 || index >= data.ListLength)
                continue;
            var candidate = (AtkComponentList*)componentData.Component;
            if (found != null && found != candidate) return null;
            found = candidate;
        }
        return found;
    }

    private static int ClampInt(JToken? token, int min, int max, int fallback)
    {
        if (token is null)
            return fallback;
        try
        {
            var v = token.Value<int>();
            return Math.Clamp(v, min, max);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static unsafe RaptureAtkUnitManager* SafeRaptureAtkUnitManager()
    {
        try
        {
            return RaptureAtkUnitManager.Instance();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Resolves the agent for <paramref name="agentId"/> and runs <paramref name="action"/> on
    /// it, converting every failure mode (unresolved module pointer, missing agent) into a
    /// structured answer rather than an escaped exception.
    /// </summary>
    private static unsafe object WithAgent(AgentId agentId, string addon, AgentAction action)
    {
        var module = SafeAgentModule();
        if (module is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = "the agent module's static address has not resolved - the game may " +
                             "not be running, or the client has not finished initializing",
            };
        }

        var agent = SafeAgent(module, agentId);
        if (agent is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = $"no agent is registered for id {(int)agentId} ('{addon}') on this client",
            };
        }

        return action(agent);
    }

    private unsafe delegate object AgentAction(AgentInterface* agent);

    private static unsafe AgentModule* SafeAgentModule()
    {
        try
        {
            return AgentModule.Instance();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe AgentInterface* SafeAgent(AgentModule* module, AgentId agentId)
    {
        try
        {
            return module->GetAgentByInternalId(agentId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
