# 1.0.0.27 — cooldown and gearset correctness

- Include inherited class and shared role actions through ClassJobCategory, using
  effective level. Identify GCD-only actions by the sheet group before converting
  to runtime recast indices.
- Distinguish charge availability, next-charge recovery and full recovery.
  Partially available charges report cooldownReady=true and remainingSeconds=0;
  totalRecastSeconds is the per-charge interval. See [field semantics](ability-gearset-state.md).
- Scan all 100 gearset slots and filter Exists, including sparse high IDs, for both
  listing and exact/substring name lookup. Ignore stale names and reject ambiguity.
- Always reapply the saved gearset, including an already-active index after manual
  gear changes. Confirmation requires matching index, job and item IDs/HQ; unavailable
  equipment cannot confirm success. The deprecated belt slot is ignored. Confirmation
  does not establish matching materia, dyes or glamour.
- Reject fractional, boolean, string and overflowing gearset IDs before equipping.

Debug and Release builds: zero warnings/errors. AbilityGearsetTest: 32/32 against
both built assemblies, including actual game-data inheritance and isolated SDK
memory with sparse/stale/ambiguous entries. PluginLoadTest: 160/160 against the
Release assembly. Package manifest and packed DLL match the tested Release build.

Live verification used the matching Debug MVID at territory 641 as a level-100 WAR.
A fractional ID was refused, the inherited Thrill of Battle (Action 40) appeared
with a 90-second cooldown after use, and reapplying active gearset 1 returned
confirmed=true/equipmentMatches=true. Equipped item IDs, glamour IDs and materia
counts matched the pre-test snapshot. No item was consumed or purchased.
Sparse gearset edits, manual item replacement, multi-charge recovery and level sync
were checked offline rather than by altering the character for those cases.
