# Run a live test as a sequence

At the start, establish the player's current territory, job and condition with `get_client_state` and `get_local_player`. Decide the next checkpoint: an opened addon, an exact prompt, an arena, or a completed encounter. Collect the guards for that checkpoint together. Once an authorized step has an observed target, an exact action and a known postcondition, run it as soon as its prerequisite is true. Do not take a model turn to reconfirm each successful mechanical step.

Prefer a typed tool that waits for the result. If several leaf tools are needed and the host can compose MCP calls, run dependent calls sequentially in one block, checking each result before continuing. Batch independent reads in parallel. Poll readiness only until a short deadline; a fixed sleep or a successful dispatch is not readiness. End the block at a new menu, conflicting state, or an unverified action. Do not repeat an interaction, confirmation or opener while its result is unknown.

## Walk to an NPC and open a known menu

1. Read `get_game_objects` for an exact, targetable `EventNpc` in the current territory. Use the observed `gameObjectId` as a string and the observed `baseId`. If several NPCs match, disambiguate from the task and live position before acting.
2. With `interact_with_npc`, pass `expectedBaseId`, `expectedTerritoryId`, and the exact expected addon. Set `approach: true` for a short walk; it uses vnavmesh if the NPC is outside interaction range. If the addon is known but its entries need discovery, omit `menuLabel` and inspect the resulting `dialogue`. If a menu choice and its next addon are known, pass the exact `menuLabel`, a different `expectedResultAddon`, and, when useful, `expectedResultText`. For example, the Stone, Sky, Sea researcher on a Simplified Chinese client can open `SelectIconString`; its observed “开战！” entry opens `DpsChallengeList`.
3. Continue from `dialogue_opened` or `completed` with `outcomeVerified: true`. This verifies the addon checkpoint. It does not verify a quest, purchase, teleport, duty start or other game result. If the workflow stops, inspect `lastStage`, `interactionAttempted`, `menuAttempted`, and `menuDispatched`. Read the current addon or state before trying again. If the actual initial addon is unknown, use the leaf movement/interaction tools and discover it; don't guess an addon just to use the workflow.

On an older server, use `move_to_entity` only when movement is needed, check distance and condition, stop the walk, set and verify the exact target, then call `interact_with_target` once. Wait for a fresh addon and use `read_addon_text`; for a known `SelectString` or `SelectIconString`, pass the exact label and observed addon ID to `select_addon_menu_item` and verify the next state.

## Confirm a known native prompt

Read the new prompt with `read_addon_text`. Confirm only if its exact content matches the authorized action. Find the visible, enabled button using `list_addon_elements`. Pass its current addon ID, node ID, component path and verified registered event to the native adapter. Follow it immediately with a read of the actual result (e.g. a new territory and loaded player, not just the closed prompt). Treat changed wording or multiple matching controls as a new checkpoint.

## Repeat a combat scenario

- Record the intended difficulty, level/item sync and starting position from the live UI. For Stone, Sky, Sea, the researcher's menu only opens the challenge list. Confirm the selected difficulty, then verify the exact start prompt before accepting it. A list row index, node ID, or menu ordinal from another run is not evidence for this one.
- Verify the arena and the unique live target before setting it. If the test calls for a manual opener, guard the territory, job, range, and target and check that combat has not already started. Issue at most the planned opener; then let the routine under test act.
- Take a pre-run chat/log cursor and recorder state. Observe the active encounter, scoped log messages and any relevant gauge/frame samples with bounded polling, rather than taking a model turn for each tick. Use the game outcome and the matching fresh encounter/archive for the verdict. A queued action or server effect alone is not a duty clear. Report packet gaps, partial starts and other limits that affect the conclusion.
- If returning to the start was part of the test, discover the current return NPC and prompt, then verify the post-return position. Leave an unfamiliar or already-open dialogue for inspection.

When a repeatable test is verified, keep its scenario, stable NPC base IDs when helpful, exact labels per client language, and outcome checks in a repository reference. Rediscover runtime object IDs, addon IDs and node paths each run. A successful run improves the recipe; it does not change the agent model or authorize future game actions.
