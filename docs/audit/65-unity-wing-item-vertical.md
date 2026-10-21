# Wings of Unity 12/245 — local paired-client acceptance (2026-10-21)

Status: **ACCEPTED for the local extended MuMain/OpenMU pair**. This is an
isolated item-acquisition example, not final drop balance, original Season 6
client fidelity, or public-production readiness.

## Scope and source

The server adds one Baski-specific reward group for Wings of Unity (12/245),
with a single guaranteed local prototype drop and a one-item cap. The group is
not in the global monster drop pool. A separate optional Season 6 update
depends on the installed Baski v4 and experimental wings v2 updates. Existing
Baski reward groups and operator drop caps are preserved. Fresh configuration
initialization includes the same reward.

The client tracks item 12/245, its model mapping, level-400 requirement and
third-tier physical wing additional-option calculations. The JSON profile is
scoped to this item. Original wing mappings and restrictions remain covered by
regression tests. The local model and texture are supplied separately and
ignored by Git; their redistribution rights have not been established.

Source worktrees and exact committed SHAs are in the package manifest at
`E:/MU-S6/ferea-unified-20261021/manifest.json`. Client commit:
`a44e9987cb78ef2f9ce665d54f4a5b9a35667cbb`.

## Verification

- Server: 30 focused initialization and 29 gameplay tests passed. Fresh,
  missing-update, repeated-update and operator-config cases were included.
  `git diff --check` and resource XML checks passed. Logs are at
  `C:/Users/blach/.codex/tmp/unity-server-tests-20261021/`.
- Client: capped native Release build and post-commit consistency build passed.
  `test_item_database`: 87 tests / 472 assertions; `test_item_database_files`:
  62 tests / 7,061 assertions. Focused profile tests: 7 / 227; schema: 1 / 9;
  absent-private-assets: 8 / 5,372. Tests include level 399 rejection and
  level 400 acceptance across seven classes at +0/+15, option calculations,
  legacy import and existing wing behavior. Logs are under the client worktree
  `out/unity-*.log`. An independent read-only reviewer found no material defect.
- Retained PostgreSQL was backed up before deployment:
  `evidence/unity-wing/database-before.dump`, SHA-256
  `D1D9678595C1E9EE55EFAE570BECAA38253D3EB84F4C4AE5E5D6BA93E0678BD8`;
  `pg_restore --list` passed. Existing binaries were copied to rollback files.
  The update helper first failed before any write because its bootstrap omitted
  the server's hex terrain JSON converter. After registering both startup JSON
  converters, a full read-only configuration load passed; targeted update v1
  applied. Reapplication produced no second reward group. SQL readback shows
  Baski 900 cap 1, group `00000200-0384-0001-0000-000000000000`, chance 1,
  only 12/245, `DropsFromMonsters=false`; prior Baski v4/wings v2 remain.
- Normal player `test400Dw` (Grand Master, level 400) used Warp List Ferea,
  walked to Baski at about `(215,165)`, defeated it without GM commands,
  picked up Wings of Unity from the ground, and saw a tooltip with defense 60,
  damage/absorb 39%, attack-speed bonus and required level 400. Equipping
  succeeded; the model rendered before and after relog. A second client logged
  in separately and saw the equipped character/model in Ferea. Screenshots,
  states and commands are under `evidence/unity-wing/` in the package.
- Independent DB readback after relog confirms the same item UUID
  `1960a101-0000-7163-03e1-000020298515` in wing slot 7, group 12/number 245.
  The prior Wing of Eternal, UUID
  `8d54a101-0000-7885-767a-1f44526057bf`, remains in the account vault at
  slot 40. It was moved there with a guarded one-row SQL fixture adjustment
  after logout because the test inventory had no free 4×3 rectangle. The
  character was subsequently loaded after a server restart and the new wing
  equipped through the client. No existing item was deleted.
- The final post-commit client Main SHA-256 is
  `3AE2DC635FC6B612A6E9A7B564B8D28F799DF9FD81D940E45FD1CC524FED0E98`.
  Its control ping reports the client commit and `commit_changed=false`. A
  further login showed Wings of Unity still equipped; the character was moved
  to the Lorencia safe zone. The secondary observer client was closed. One
  package Main, one server and one healthy database remain running.

## Limits and next work

The 100% Baski wing drop exists solely to make this technical acceptance
reproducible. Set a deliberate economy rule before using it for regular
gameplay. There is no custom particle/shadow recipe for 245; its model uses the
generic wing attachment/animation path. The supplied private art remains local.
Test clients had overlapping mounts/effects in screenshots, so detailed visual
balance and animation polish remain a separate gate. This item slice does not
prove crafting, multiple new item groups, original-client compatibility or
long-term production economy.

Continue with another bounded item or acquisition task using the same paired
manifest and backups; avoid reopening the completed Ferea boss stage.
