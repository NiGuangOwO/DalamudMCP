# Cooldown and gearset state

## Cooldown queries

`get_cooldowns` includes job-specific, inherited class and shared role actions using
the client's `ClassJobCategory` data. It filters by effective level, including level
sync. The primary GCD group is identified in the one-based Action sheet before
resolving the runtime recast index; these two numbering systems are different.

The result contains actions with an active recharge, including partially depleted
multi-charge actions. A listed action can still have a usable charge:

| Field | Meaning |
| --- | --- |
| `remainingSeconds` | Time until at least one charge is available; zero if any remain. |
| `nextChargeInSeconds` | Time until the next charge regenerates. |
| `fullRechargeSeconds` | Time until all charges allowed at the current level are restored. |
| `totalRecastSeconds` | Recharge interval for one charge. |
| `currentCharges` / `maxCharges` | Native current count and current-level maximum. |
| `cooldownReady` | At least one charge remains; does not establish target, resource or other use conditions. |

For a three-charge ability with a 30-second interval, ten seconds after exhausting
all charges, `remainingSeconds=20`, `nextChargeInSeconds=20`, and
`fullRechargeSeconds=80`. With one charge available, `remainingSeconds=0` and
`cooldownReady=true` even while another charge is regenerating. The native elapsed
gauge includes credit for unspent charges; its raw total can reflect a higher-level
cap, so the tool uses the adjusted interval and current charge count instead.

Results are capped at 100 rows, ordered by full recharge time. An empty list means
no matching active non-GCD recharge was found, not that every action is usable.

## Saved gearsets

`get_gearsets` scans the fixed 100-entry capacity, returning only `Exists` entries.
`NumGearsets` is a count, not the last occupied index: deleted or reassigned sets can
leave holes. Exact and substring name matching scan the same occupied entries,
including high IDs, and reject ambiguity. Stale names without `Exists` are ignored.
The active row's `equipmentMatches` compares saved and equipped item IDs, including
HQ encoding; it is null for inactive entries or unavailable inventory.

`equip_gearset` requires a JSON integer for `gearsetId`. Fractional values, booleans,
numeric strings and overflow are rejected before any equipment change. The ID
takes precedence over `name`; an omitted/null ID permits name lookup.

The tool always invokes the client's equip operation, including when the same set
and job are active. This restores saved items after manual gear changes. Its
`alreadyActive` field describes the prior index and does not mean the call was
skipped. Confirmation requires the target index, target job and equipped item IDs
to match the saved snapshot. Missing inventory never counts as confirmation.
The obsolete belt slot is ignored. This identity check does not verify materia,
dyes or glamour; the client can still apply the linked glamour plate as usual.

## Verification

Build the plugin first, then run `dotnet run --project tests/AbilityGearsetTest`.
The test reads the built assembly (an optional first argument selects another DLL),
uses actual installed game data for inheritance/role filtering, and isolated SDK
memory for sparse/stale/ambiguous gearsets. It checks strict ID parsing, charge
timing and equipment identity, including HQ and unavailable inventory. This does
not replace live verification of a native equip operation or level-synced combat.
