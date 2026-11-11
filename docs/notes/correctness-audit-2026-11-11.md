# Correctness audit — 2026-11-11

> Status: **resolved** (2026-11-11). All 16 confirmed bugs are fixed on `test/correctness-audit`;
> see [Resolution](#resolution). Every confirmed bug has a regression test that **failed on the
> audited code** (`ba29c45`) and passes after the fix. The tests assert the correct behavior and
> are tagged `[Trait("Category", "Audit")]`.
> Branch: `test/correctness-audit`.

PostgreSQL used: **17.11** (x86_64-windows) in a throwaway cluster with trust auth on port 55432.
Each live test creates and drops its own database or schema.

Run the regression set (one project at a time; never the full suite):

```sh
export CHETAH_CONNECTION_STRING="Host=127.0.0.1;Port=55432;Username=postgres;Database=postgres"
dotnet test tests/Chetah.Db.Tests       --filter "Category=Audit"
dotnet test tests/Chetah.Postgres.Tests --filter "Category=Audit"
dotnet test tests/Chetah.CodeGen.Tests  --filter "Category=Audit"
```

Last full RED run on the audited code: Db.Tests 11 failed / 0 passed, Postgres.Tests 4 failed /
0 passed, CodeGen.Tests 16 failed / 0 passed. That is 31 regression tests, all failing for the
reasons listed below. After the fixes all 31 pass.

## Resolution

| ID | Resolution | Commit |
|---|---|---|
| AUD-01 | Replay validation ignores objects the entry's own earlier drops remove and keys/indexes owned by a table the entry drops; journal operations record the dropped object's name. Foreign dependents still refuse. | `107f0ae` |
| AUD-02 | `Column.StoreType` (generated for every column) and the compared/assigned column travel with each parameter. `timestamptz`: Utc/Local bind as instants, Unspecified is refused before I/O. `timestamp`: Utc/Local keep their UTC wall clock, Unspecified binds as is. Column of unknown type: instants bind as `timestamptz`, Unspecified is refused. Rules in `writing-a-schema.md#datetime-values`. | `1d4dc8d`, `bf920b3` |
| AUD-03 | Generation refuses a table whose `schema` is not `public` (`tables.<t>.schema`), matching migrations. Deliberate restriction, no qualified metadata. | `eb34d83` |
| AUD-04 | Under DISTINCT, an ORDER BY key equal to a selected expression reuses its rendered SQL and parameter slot. | `a0688f4` |
| AUD-05 | A registry id is adopted by name only when no declaration in the scope claims it explicitly; `Identity.Resolve` refuses duplicate claims before anything is persisted. | `8ef7f16` |
| AUD-06 | `ADD GENERATED … AS IDENTITY` is planned after `DROP DEFAULT` and `SET NOT NULL`; `DROP IDENTITY` still precedes `SET DEFAULT`. | `9445029` |
| AUD-07 | Partitioned parents and partitions are read as unexpressible relations: never planned as tables to drop, and a declaration using their name is refused. | `761b624` |
| AUD-08 | Identity implies NOT NULL in the parser; an explicit `"nullable": true` on an identity column is rejected. | `a161d21` |
| AUD-09 | Membership over a collection whose comparer is not the default is refused with `NotSupportedException`. | `044edb2` |
| AUD-10, AUD-11 | Property names that collide with generated members, the model/accessor name or record-synthesized members get a `Column` suffix; two columns that land on one property are refused. | `1609d40` |
| AUD-12 | Generated code `global::`-qualifies every namespace and non-keyword BCL type reference. | `1609d40`, `c410a0e` |
| AUD-13 | A C# keyword as an explicit `"model"` name is rejected (the keyword list is shared with `--namespace`). | `19255c7`, `15a36d6` |
| AUD-14 | Index and constraint names are validated in one namespace. | `c686998` |
| AUD-15 | The parser accepts ASCII identifiers only, matching `$defs.identifier`. | `32fb27e` |
| AUD-16 | The UPDATE nullability proof credits every operand of a conjunction in the true branch and of a disjunction in the false branch. | `cc38022` |

Checked-in generated models were regenerated once afterwards (`cca3812`).
`examples/NewExample` `Grade.Grade` became `GradeColumn`; the old output did not compile (an AUD-11 instance).

Known limits left by the fixes:

- A dropped table's own check/exclusion constraints and unexpressible indexes still refuse a replay,
  because the catalog does not record their owner. Tool-generated journals cannot contain such a
  drop, so only hand-written journals hit this.
- DateTime operands wrapped in `CASE` or casts are not tied to a column, so they use the
  rule for untied values.
- Enums in non-public schemas are still generated, while tables in them are refused.
- Comparer checks compare by reference: `HashSet<string>(StringComparer.Ordinal)` is refused even
  though it behaves like the default.

## Executive summary

**Readiness:** the query layer is in good shape. The audit compared these against live
PostgreSQL and found no divergence:

- 32 WHERE predicates covering three-valued logic.
- Join and presence-marker semantics.
- Terminal operators over 16 chain shapes.
- Parameter ordering for every statement.
- UPDATE/DELETE scope guards and the target-moving join rewrite.

No path was found that updates or deletes more rows than the chain asks for. The weak spots are
in three places:

- **Migration history replay:** a fresh database cannot be built from a journal that ever dropped
  a table.
- **DateTime parameter binding:** shared by every statement.
- **Code-generation naming:** valid schemas can produce C# that does not compile.

| Severity | Count | IDs |
|---|---|---|
| P0 | 0 | — |
| P1 | 3 | AUD-01, AUD-02, AUD-03 |
| P2 | 9 | AUD-04 … AUD-12 |
| P3 | 4 | AUD-13 … AUD-16 |

**Highest-risk subsystems:**

- `MigrationRunner` replay validation.
- `PostgresDb` parameter typing.
- `SchemaFile`/`Identity` id adoption.
- `GeneratedSource` naming.

**Blocks CLI/NuGet work:**

- AUD-01: a fresh clone, CI or staging database cannot be built.
- AUD-02: `DateTime.UtcNow` cannot be used, and timestamptz values shift silently.
- AUD-03: a table in another schema is silently read or written in the wrong relation.
- AUD-05: the CLI corrupts the schema file it owns.
- AUD-07 should also be fixed before the CLI exposes identity edits.

Details are in [Recommended next steps](#recommended-next-steps).

## Confirmed bugs

### AUD-01 — A fresh database cannot replay tool-generated drops (P1)

- **Subsystem:** migration history replay. Original ids: RUN03 and RUN04.
- **Source:**
  - `src/Chetah.Db/MigrationRunner.cs` `ValidatePendingAsync` (≈853–886)
  - `src/Chetah.Db/SchemaDiff.cs` `ReplayDependents` (≈666–677)
  - The plan never drops a dropped table's own keys explicitly (`SchemaDiff.cs` ≈118–131).
- **Preconditions:** a journal written by Chetah contains either of these entries, and a second
  checkout (fresh clone, CI or staging) applies the journal to an empty database:
  - `DROP TABLE` of a table that has a primary key.
  - `drop_index` followed by `drop_column`.
- **Actual:** replay validation compares each recorded drop with the live catalog. It counts
  objects that the entry itself removes, or that die with the dropped table, as unexplained
  dependents:
  - `Refusing to replay …_drop_legacy: DROP TABLE "legacy" would remove constraint "legacy_pkey".`
  - `Refusing to replay …: ALTER TABLE "probe" DROP COLUMN "note" would remove index "probe_note_idx".`

  The database stops one migration short of the journal. When two or more entries are pending,
  `repair` refuses too (it accepts only one pending entry), so there is no way forward.
- **Expected:** decision 2 in `docs/reference/schema-tooling.md` says "One apply path: fresh and
  existing databases go through the same diff→journal→execute flow". The replay should succeed,
  because PostgreSQL drops a table's own constraints and indexes along with it.
- **Impact:** almost every project that ever drops a table can no longer bootstrap a new
  environment from its committed history.
- **Tests:**
  - `AuditMigrationRunnerTests.RUN03_FreshDatabase_ReplaysAPendingMigrationThatDropsItsOwnIndex`
  - `AuditMigrationRunnerTests.RUN04_FreshDatabase_ReplaysADroppedTable`
- **Observed:** `apply exited 1: Applied …create_keep_plus_1. Refusing to replay …drop_legacy:
  DROP TABLE "legacy" would remove constraint "legacy_pkey".` RUN03 fails the same way with
  `… would remove index "probe_note_idx"`.
- **Commit:** `b9ea1bc`.
- **Fix direction:**
  - Validate against the catalog as it will be after the entry's own earlier drops.
  - Ignore objects owned by a recorded `drop_table` target.
  - Keep refusing genuinely foreign dependents.

### AUD-02 — `DateTime` parameters are always bound as `timestamp` (P1)

- **Subsystem:** shared parameter binding. It affects SELECT, INSERT, UPDATE, DELETE, conflict
  assignments and RETURNING. Original ids: LEAD01–03 and SEL02.
- **Source:** `src/Chetah.Postgres/PostgresDb.cs` `ParameterType` (≈115–126) maps every
  `DateTime` to `NpgsqlDbType.Timestamp`, whatever the `Kind` and whatever the column type.
  `BindParameters` (≈102–113) passes the value through unchanged.
- **Preconditions:** a captured or assigned `DateTime`. The generator maps both `timestamp` and
  `timestamptz` to `DateTime` (see the types table in `writing-a-schema.md`).
- **Actual:**
  - Any `Kind=Utc` value throws inside Npgsql: `Cannot write DateTime with Kind=UTC to PostgreSQL
    type 'timestamp without time zone'`. This breaks `Where(r => r.Created < DateTime.UtcNow)` on
    both column types and every UTC write into `timestamptz`.
  - An `Unspecified` value written into `timestamptz` is **silently converted through the session
    TimeZone**. Under `America/New_York`, 12:00 is stored as 17:00Z, so the stored instant depends
    on server configuration. This was verified with plain SQL:
    `SET timezone='America/New_York'; SELECT ('2026-01-01 12:00'::timestamp)::timestamptz AT TIME ZONE 'UTC'`
    returns `2026-01-01 17:00:00`.
- **Expected:**
  - A UTC instant written to `timestamptz` is stored as that instant.
  - A UTC capture filters `timestamptz` and `timestamp` columns without throwing.
  - An ambiguous `Unspecified` value is never shifted by the session zone: either refuse it or
    store it unshifted.
- **Tests:**
  - `AuditLeadTests.LEAD01_InsertUtcDateTimeIntoTimestamptzColumn_StoresTheInstant`
  - `AuditLeadTests.LEAD02_FilterTimestamptzColumnByUtcDateTime_MatchesTheInstant`
  - `AuditLeadTests.LEAD03_InsertUnspecifiedDateTimeIntoTimestamptzColumn_IsNotShiftedBySessionTimeZone`
  - `AuditSelectLiveTests.SEL02_CapturedUtcDateTime_IsBoundAndFilters`
- **Observed:**
  - LEAD01, LEAD02 and SEL02: the `ArgumentException` quoted above.
  - LEAD03: `Assert.Equal() Failure: Expected: True Actual: False` (the stored instant was shifted).
- **Commits:** `7c06504` (LEAD01/02), `4fdb66a` (LEAD03), `971b905` (SEL02).
- **Fix direction:**
  - Carry the target column's PostgreSQL type into the compiled parameter, so a `timestamptz`
    column binds as `TimestampTz`.
  - For `timestamp`, normalize or refuse non-`Unspecified` kinds.
  - Document the Kind rule.

### AUD-03 — A table's non-public `"schema"` is silently dropped by the generator (P1)

- **Subsystem:** code generation, then every compiler. Original id: GEN05.
- **Source:**
  - `src/Chetah.CodeGen/GeneratedSource.cs` (≈182) emits `new Chetah.Table("items", …)` with the
    name only.
  - `PostgresCompiler.cs` (≈780) and the insert, update and delete compilers quote
    `Table.Name` alone.
- **Preconditions:** `"schema": "app"` on a table. The parser accepts this, the JSON contract
    documents it, and only migrations refuse it.
- **Actual:** the generated metadata is unqualified, so every statement targets whatever
  `search_path` resolves `"items"` to. The result is either `42P01`, or reads and writes against
  a **different table** of the same name, such as `public.items`.
- **Expected:** D008 says a declaration is honored or refused with its path, never ignored.
- **Test:** `AuditCodeGenTests.GEN05_TableDeclaredInANonPublicSchemaIsNotSilentlyUnqualified`
- **Observed:** `Assert.Contains() Failure: Sub-string not found … Not found: ""app"`
- **Commit:** `ae7c4d8`.
- **Fix direction:** either refuse non-public `schema` in generation, or carry the schema in
  `Chetah.Table` and quote it in every compiler.
- **Note on severity:** the trigger is uncommon, because migrations refuse non-public schemas
  and only adopted databases hit it. The consequence is silent writes to the wrong relation.

### AUD-04 — DISTINCT ordered by a parameterized projection fails with 42P10 (P2)

- **Subsystem:** SELECT compiler. Original id: SEL01.
- **Source:** `src/Chetah.Postgres/Compilation/PostgresCompiler.cs`. The render-reuse cache
  (≈886–902) applies to group keys only, and ORDER BY re-renders the expression (≈769–819) with a
  new parameter slot.
- **Repro:** `Select(r => r.Age ?? 18).Distinct().OrderBy(x => x)` compiles to
  `SELECT DISTINCT COALESCE("t0"."age", $1) … ORDER BY COALESCE("t0"."age", $2)`.
- **Actual:** `42P10: for SELECT DISTINCT, ORDER BY expressions must appear in select list`.
  `Count` and `Any` on the same chain work, because they drop ORDER BY, which hides the bug.
- **Expected:** select.md explicitly allows this chain. Verified with `PREPARE`: reusing `$1`
  works.
- **Tests:**
  - `AuditSelectTests.SEL01_DistinctOrderedParameterizedScalarProjection_RendersOneParameterSlot`
  - `AuditSelectTests.SEL01_…ObjectProjection_…`
  - `AuditSelectLiveTests.SEL01_DistinctOrderedParameterizedProjection_ExecutesAndReturnsDistinctValues`
- **Observed:**
  - Offline tests: `Expected: [18] Actual: [18, 18]`.
  - Live test: the 42P10 error above.
- **Commit:** `971b905`.
- **Fix direction:** under `IsDistinct`, reuse the projection's rendered SQL for ORDER BY, or
  order by the select alias.

### AUD-05 — A rename plus a new object under the old name writes duplicate `"$id"` into the schema file (P2)

- **Subsystem:** identities, then the schema file, then execution. Original ids: RUN01 and RUN02.
- **Source:**
  - `src/Chetah.Db/SchemaFile.cs` ≈78 (columns) and ≈53 (tables) adopt the registry id by name
    without checking whether another declaration already claims that id.
  - `src/Chetah.Db/Identity.cs` ≈410 and ≈354 ignore the return value of `claimed*.Add`.
  - `PersistIdsAsync` writes the duplicates **before** the migration runs.
- **Repro:** rename `email` to `email_old` (keeping `"$id"`) and declare a new `email` in the same
  edit. The table-scope equivalent behaves the same way.
- **Actual:**
  - The migration fails with `21000: ON CONFLICT DO UPDATE command cannot affect row a second
    time` and rolls back cleanly.
  - `chetah-schema.json` is left with `"$id": 2` on both columns.
  - From then on `diff`, `apply` and `repair` all stop with `"$id" 2 is already used by another
    column`. The user has to hand-edit tool-owned ids to recover.
- **Expected:** the rename keeps its id, the new object gets a fresh id, and one migration
  applies. This follows schema-tooling.md, which says ids are never reused.
- **Tests:**
  - `AuditMigrationRunnerTests.RUN01_RenameColumnAndRecreateIt_KeepsTheFileParseable`
  - `AuditMigrationRunnerTests.RUN02_RenameTableAndRecreateIt_AllocatesAFreshId`
- **Observed:** `apply exited 1: Assigned 3 missing "$id" member(s) … 21000: ON CONFLICT DO UPDATE
  command cannot affect row a second time`.
- **Commit:** `b9ea1bc`.
- **Fix direction:**
  - Adopt an id by name only when no declaration in that scope claims it.
  - Make `Identity.Resolve` refuse duplicate claims before anything is persisted.

### AUD-06 — Adding identity to an existing column is planned in the wrong order (P2)

- **Subsystem:** migration planning. Original id: PLAN01.
- **Source:** `src/Chetah.Db/SchemaDiff.cs` ≈240–291. The per-column order is identity, then
  default, then nullability.
- **Actual:** `ADD GENERATED BY DEFAULT AS IDENTITY` is emitted before `SET NOT NULL` and before
  `DROP DEFAULT`. Apply then fails with one of:
  - `55000: column "id" … must be declared NOT NULL before identity can be added`. This happens
    even on an empty table.
  - `55000: … already has a default value`.

  The transaction rolls back safely.
- **Expected:** prerequisites come first (verified with plain SQL). schema-tooling.md lists
  "add identity" as a supported operation.
- **Tests:**
  - `AuditMigrationPlanTests.PLAN01_IdentityIsAddedOnlyAfterNotNullAndDefaultAreCleared` (offline)
  - `AuditMigrationPlanTests.PLAN01_IdentityOnANullableColumnApplies`
  - `AuditMigrationPlanTests.PLAN01_IdentityReplacingADefaultApplies`
- **Observed:**
  - Offline: `SET NOT NULL must precede ADD GENERATED ... AS IDENTITY`.
  - Live: the runner output with the 55000 errors above.
- **Commit:** `0f9e65f`.
- **Fix direction:** emit the identity statement after `DROP DEFAULT` and `SET NOT NULL`.

### AUD-07 — Partitioned tables are invisible, and a partition is planned as `DROP TABLE` (P2)

- **Subsystem:** catalog reading. Original id: PLAN02.
- **Source:** `src/Chetah.Db/Catalog.cs` ≈292 and ≈356 (`relkind = 'r'`). This skips `'p'`
  parents but keeps `relispartition` children.
- **Actual:**
  - A partition is planned as `-- destructive: removes the table and its rows / DROP TABLE
    "events_p1"`. It is gated, but confirming it deletes data.
  - Declaring the parent instead produces `CREATE TABLE "events"`, which fails with `42P07`.
- **Expected:** partitioning is not expressible, so treat it like other unexpressible objects:
  refuse or protect it, and never plan a partition as a removable table.
- **Tests:**
  - `AuditMigrationPlanTests.PLAN02_APartitionIsNotPlannedAsAnUnmanagedTableToDrop`
  - `AuditMigrationPlanTests.PLAN02_APartitionedParentIsNotPlannedAsCreate`
- **Observed:** `Assert.DoesNotContain() Failure: Filter matched in collection` (`DROP TABLE
  "events_p1"`, `Destructive = True`).
- **Commit:** `0f9e65f`.
- **Fix direction:** read `relkind='p'` and `relispartition`, and surface them as protected or
  refused objects.

### AUD-08 — Identity columns are NOT NULL in PostgreSQL but nullable in generated models (P2)

- **Subsystem:** parser and generator metadata. Original ids: GEN06 and E2E01.
- **Source:**
  - `src/Chetah.CodeGen/SchemaParser.cs` ≈261–284 forces NOT NULL only for primary keys.
  - `GeneratedSource.ColumnMetadata` (≈319–332) emits `IsNullable` from the parsed flag.
  - The migration side already coerces identity columns to NOT NULL (`Catalog.cs` ≈153–162).
- **Actual:**
  - The documented shorthand `"identity": true` (nullable omitted) generates
    `Chetah.Column<int?> Id … { HasDefault = true }` and `record …(int? Id, …)`, while the live
    column is NOT NULL.
  - An explicit `"nullable": true` on an identity column is accepted.
  - An INSERT with an explicit NULL, or an UPDATE from a nullable column, passes Chetah's
    pre-execution validation (D011/D012) and fails at the server with `23502`.
- **Expected:** identity implies NOT NULL in every representation. PostgreSQL rejects
  `IDENTITY NULL`, and an identity column always has `attnotnull`.
- **Tests:**
  - `AuditCodeGenTests.GEN06_IdentityColumnDeclaredNullableIsRejected`
  - `AuditEndToEndTests.E2E01_IdentityColumnIsNotNullInEveryRepresentation` (Db.Tests)
  - `AuditEndToEndTests.E2E01_ExplicitNullForIdentityColumnIsRefusedBeforeExecution` (CodeGen.Tests)
- **Observed:**
  - GEN06: `Assert.NotNull() Failure: Value is null`.
  - E2E01 (Db.Tests): `Not found: "Chetah.Column<int> Id"`.
  - E2E01 (CodeGen.Tests): `Expected: InvalidOperationException Actual: Npgsql.PostgresException
    23502`.
- **Commits:** `ae7c4d8`, `0c75170`.
- **Fix direction:** apply the primary-key rule to identity columns (coerce them to NOT NULL and
  reject an explicit nullable), then regenerate the fixtures.

### AUD-09 — `Contains` on a collection with a custom comparer silently drops the comparer (P2)

- **Subsystem:** lambda translator. It is shared by SELECT, UPDATE and DELETE predicates.
  Original id: SEL03.
- **Source:** `src/Chetah.Postgres/Translation/QueryExpressionTranslator.cs` ≈217–223.
- **Repro:**
  `var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ada" }; Where(r => names.Contains(r.Label!))`
- **Actual:** the predicate becomes plain `IN`, so the query returns 0 rows where C# semantics
  return 2. A negated set in a DELETE `Where` therefore deletes rows the caller meant to keep.
- **Expected:** `NotSupportedException`. The translator already refuses
  `Enumerable.Contains(…, comparer)`, and D004 rules out client-side semantics.
- **Test:** `AuditSelectTests.SEL03_NonDefaultComparerMembership_IsRefusedInsteadOfDroppingComparerSemantics`
- **Observed:** `Assert.Throws() Failure: No exception was thrown`
- **Commit:** `971b905`.
- **Fix direction:** reject receivers whose comparer is not the default equality comparer.

### AUD-10 — A column name that matches a generated accessor member does not compile (P2)

- **Subsystem:** code generation. Original id: GEN01.
- **Source:** `src/Chetah.CodeGen/GeneratedSource.cs` ≈169–225 emits the members, and
  `ValidateGeneratedNames` (≈267) does not reserve them.
- **Repro:** columns `map`, `map_at`, `table` or `property_columns` produce `CS0102` (duplicate
  `Map`, `MapAt`, `Table` or `PropertyColumns`). `table` is a plausible column name.
- **Test:** `AuditCodeGenTests.GEN01_ColumnNameCollidingWithAGeneratedMemberCompiles`
- **Observed:** `…Point.g.cs(32,25): error CS0102: The type 'PointsTable' already contains a
  definition for 'Map'`
- **Commit:** `ae7c4d8`.
- **Fix direction:** reserve the generated member names, and refuse them with the column named,
  or disambiguate them.

### AUD-11 — A column named after its own model does not compile (P2)

- **Subsystem:** code generation. Original id: GEN02.
- **Repro:** table `users` with a column `users` produces
  `record Users(…, string? Users)`, which fails with `CS0542`.
- **Related:** the record's synthesized members (`to_string`, `equals`, `deconstruct`,
  `print_members`) fail the same way: `CS0102` and `CS8866`, verified by the auditing agent.
- **Test:** `AuditCodeGenTests.GEN02_ColumnNameEqualToTheModelNameCompiles`
- **Observed:** `Users.g.cs(46,44): error CS0542: 'Users': member names cannot be the same as their
  enclosing type`
- **Commit:** `ae7c4d8`.
- **Fix direction:** add the model name and the record's synthesized member names to the reserved
  set.

### AUD-12 — A table named `system` or `chetah` breaks every generated file (P2)

- **Subsystem:** code generation. Original id: GEN04.
- **Source:** the generated code uses unqualified `System.…` and `Chetah.…` references, and the
  model type lands in the same namespace.
- **Actual:** with the default model naming, a `system` or `chetah` table yields
  `CS0426 The type name 'Collections' does not exist in the type 'System'`. The equivalent errors
  for `Chetah` (`Postgres`, `Column<>`) appear in every generated file.
- **Test:** `AuditCodeGenTests.GEN04_ModelNameShadowingAGeneratedNamespaceCompilesOrIsRejected`
  (cases `system` and `chetah`).
- **Commit:** `ae7c4d8`.
- **Fix direction:** emit `global::` qualified references.

### AUD-13 — A C# keyword is accepted as a `"model"` name (P3)

- **Original id:** GEN03.
- **Source:** `SchemaParser.cs` ≈133 and ≈1208.
- **Actual:** `"model": "class"`, `"event"` or `"params"` produces syntax errors such as
  `CS1031 Type expected`.
- **Test:** `AuditCodeGenTests.GEN03_ModelNameThatIsACSharpKeywordCompilesOrIsRejected`
- **Commit:** `ae7c4d8`.
- **Fix direction:** reject keywords, as the CLI already does for `--namespace`.

### AUD-14 — An index name may collide with a constraint name (P3)

- **Original id:** PLAN03.
- **Source:** `SchemaParser.cs` keeps separate name sets (≈1310–1336 for constraints, ≈1419–1423
  for indexes). PostgreSQL uses one namespace for both, which `SchemaDiff.cs` ≈96–99 states.
- **Actual:** validation accepts the collision, and apply fails with `42P07`.
- **Test:** `AuditMigrationPlanTests.PLAN03_DeclaredIndexNameCollidingWithAConstraintNameIsRejected`
- **Observed:** `Assert.NotNull() Failure: Value is null`
- **Commit:** `0f9e65f`.

### AUD-15 — The JSON Schema contract and the parser disagree on identifier characters (P3)

- **Original id:** GEN08.
- **Actual:** `chetah.schema.json` `$defs.identifier` is ASCII-only, while `IsValidIdentifier`
  uses `char.IsLetter` (Unicode). The parser accepts names that an editor flags as invalid.
- **Test:** `AuditCodeGenTests.GEN08_ThePublishedContractAndTheParserAgreeOnIdentifiers`
- **Observed:** `Expected: True Actual: False`
- **Commit:** `ae7c4d8`.

### AUD-16 — An UPDATE nullability proof rejects a compound-guarded CASE (P3)

- **Original id:** WR01.
- **Source:** `src/Chetah.Postgres/Compilation/PostgresUpdateCompiler.cs` ≈157–262. The proof
  recognizes only a single null check, so a guard combined with AND proves nothing.
- **Repro:**
  `Set(p => p.FirstName, p => p.Nickname != null && p.Nickname != "" ? p.Nickname : "fallback")`
  on a NOT NULL column.
- **Actual:** `InvalidOperationException: Column 'first_name' is NOT NULL, but the assignment can
  produce NULL.` The expression can never yield NULL, so this is an over-strict refusal. It does
  not corrupt data.
- **Test:** `AuditWriteTests.WR01_UpdateCompoundGuardedCaseIntoNotNullColumnCompiles`
- **Commit:** `6395f86`.

## Coverage

| Area | Depth | Notes |
|---|---|---|
| Schema parsing / validation | full | Contradictory declarations are already rejected: nullable PK, FK target/type/uniqueness, default+identity, duplicate names |
| Migration planning | full | Convergence checked for defaults, modifiers and FK actions; widening, renames, ordering, quoting |
| Migration execution / recovery | full | Transaction boundaries verified correct; Retry/Repair/Cancel; divergence |
| Identities / renames | full | Rename table+column in one migration works; AUD-05 is the gap |
| Code generation | full | Type map verified live, including arrays and enums; determinism; manifest safety |
| SELECT | full | Differential testing against PostgreSQL |
| INSERT | full | Batch, conflict, RETURNING, parameter order |
| UPDATE | full | FROM/joins, multi-match, guards, parameter order |
| DELETE | full | USING chains, guards, counts, RETURNING |
| Shared infrastructure | full | Translator, mapper, binder; AUD-02 and AUD-09 |
| End-to-end | partial | File → apply → catalog → generated metadata → CRUD for every executable type; rename and regenerate; nullability/default changes. Generated code was built through the CLI or a consumer project, not loaded at runtime in-process |

**Not investigated sufficiently:**

- Concurrency beyond the existing advisory-lock test.
- `CREATE INDEX` locking (documented).
- Multi-schema `search_path` execution paths.
- Very large schemas.
- Arithmetic overflow divergence between C# and PostgreSQL (reasoned, not reproduced).
- `CodeGenCli` exit codes beyond the existing tests.

## Non-blocking findings

**Documented limitations that were hit and not re-reported:**

- migration-limitations #1: non-executable types.
- #8: conservative widening. In practice it is wider than documented: `timestamp(3)→timestamp`,
  `int4→text` and `uuid→text` are also gated.
- #9, #17, #19–#22.
- `Average` precision (`numeric` vs `decimal`).
- Scalar casts use SQL `CAST` semantics (D004): `(int)decimal` rounds.
- ON CONFLICT targets are validated as columns only. Constraint metadata is not delivered yet.

**Design suggestions:**

- ORDER BY NULL placement is the opposite of LINQ and is undocumented.
- `COUNT(*)` over a LEFT JOIN counts the null-extended row. This deserves a line in the guide.
- `AnyAsync` skips the ordering validation that `Count` performs.
- Inline `new List<T>{…}.Contains` is refused, while the guide says "inline collection".
- `MigrationFailure.Hash` is never compared.
- A replayed baseline syncs with `recordDeclarations: true`.
- `ApplyMigrationAsync` catches only `NpgsqlException`, so a non-Npgsql failure writes no failure
  marker.
- There is no `.gitattributes` for `migrations/**`. With CRLF checkout the migration hashes would
  change. This was not reproduced through git.
- No `USING` clause is emitted for `text→uuid` or `text→integer`. The change is gated, then fails
  at apply.
- `ADD COLUMN … NOT NULL` without a default on a populated table fails at apply (known ceiling,
  D005).
- `numeric` values beyond the `decimal` range throw on read.
- `typed-models.md` has a stale line saying `timestamptz` is not in the type map.
- A mixed per-row `DEFAULT` marker in an insert batch is unreachable, although `insert.md`
  describes it.

**False positives ruled out:**

- Identity sequence type after `int4→int8` (PostgreSQL retypes the sequence).
- Identifier quoting with mixed case and reserved words.
- Default convergence.
- JSONC `$id` insertion with comments, escapes, BOM and CRLF.
- Transaction atomicity of DDL, history and registry.
- Enum and array reads.
- The CROSS JOIN rewrite for target-referencing inner joins.
- Every `AllRows`/`Where` scope guard.

## Recommended next steps

1. **AUD-01 (replay):** without it, no second environment can be built.
2. **AUD-02 (DateTime binding):** the first real application will use `DateTime.UtcNow`, and
   timestamptz shifts are silent corruption.
3. **AUD-05 (duplicate `$id`):** the CLI must never corrupt the file it owns.
4. **AUD-03 (non-public schema):** a one-line refusal is enough for now.
5. **AUD-08 (identity nullability)** and **AUD-06 (identity add order):** both are small parser
   and plan-order fixes.
6. **AUD-10, AUD-11, AUD-12 (generated-name robustness):** fix these before NuGet, since users'
   arbitrary schemas meet the generator there.
7. **AUD-04 (DISTINCT ORDER BY)** and **AUD-09 (comparer membership):** fix these before the
   first real application.
8. **AUD-07 and AUD-13 … AUD-16:** these can wait for real usage. AUD-07 only matters for adopted
   partitioned databases.

## Files and commits (branch `test/correctness-audit`)

| Commit | Files |
|---|---|
| `7c06504`, `4fdb66a` | `tests/Chetah.CodeGen.Tests/AuditLeadTests.cs` |
| `0f9e65f` | `tests/Chetah.Db.Tests/AuditMigrationPlanTests.cs` |
| `b9ea1bc` | `tests/Chetah.Db.Tests/AuditMigrationRunnerTests.cs` |
| `971b905` | `tests/Chetah.Postgres.Tests/AuditSelectTests.cs`, `tests/Chetah.CodeGen.Tests/AuditSelectLiveTests.cs` |
| `ae7c4d8` | `tests/Chetah.CodeGen.Tests/AuditCodeGenTests.cs` |
| `0c75170` | `tests/Chetah.Db.Tests/AuditEndToEndTests.cs`, `tests/Chetah.CodeGen.Tests/AuditEndToEndTests.cs` |
| `6395f86` | `tests/Chetah.Postgres.Tests/AuditWriteTests.cs` |
