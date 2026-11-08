# Decision Log

Decisions arising during development, with rationale. Deliberately unresolved
topics stay in `docs/project/spec.md` §7; an entry here is a directional commitment that
may be revisited — entries carry their date so later reversals stay honest.

## D001 — Database-centric query API `db.Select<T>()`

**Date:** 2026-11-05 (post basic-SELECT milestone)

**Decision:** The primary public query API is centered on the database handle,
not on individual table objects:

```csharp
var users = await db.Select<User>().ToListAsync();
```

Not table-centric forms like `users.SelectAll()`, which are scaffolding of the
basic-SELECT milestone only.

**Details:**

- `T` is the generated C# model type; the ORM resolves the corresponding table
  metadata automatically — consumers never pass table names manually.
- The chain must remain extensible for future `Where`, `Join`, `Insert`, and
  `Update` (e.g. `db.Select<User>().Where(...).Limit(n)`).
- No additional abstractions are introduced just to support this syntax; it
  composes over the existing Core query objects / dialect compiler / executor
  boundaries.

**Consequences:**

> **Delivered 2026-11-05** — the typed-model prerequisites exist:
> `db.Select<T>().ToListAsync()` resolves T through the generated
> `ChetahSchema.Bindings` registry (`TableBinding<T>` in `Chetah.Postgres`);
> consumers construct `new PostgresDb(connectionString, ChetahSchema.Bindings)`.
> With Stage 2 the chain goes further: `Where`, `Select` projections, ordering,
> pagination, `Distinct`, explicit joins and `GroupBy`/`Having` all compose
> over this resolution (see D003 for the query snapshot contract).

- Underneath, `PostgresCompiler.Compile` and `PostgresDb` keep their roles:
  `Select<T>()` is a registry lookup; execution compiles the query into a
  `CompiledQuery` (SQL text plus ordered parameters). Parameter types are
  validated before a connection opens, values are bound after opening and
  immediately before execution.
- Resolution is registry-based (generated file), not reflection discovery —
  revisit trigger: if multiple tables per model type are ever needed, extend
  the registry schema.

## D002 — JSON schema file + CLI generator, explicit generated mappers

**Date:** 2026-11-05 (milestone 002, schema-first typed models)

**Decision:** The schema source of truth is a small JSON file
(`chetah-schema.json`), models are produced by a plain CLI tool
(`src/Chetah.CodeGen`, `dotnet run`), and row mapping stays an explicit
`Func<NpgsqlDataReader, T>` — now generated per table instead of hand-written.

**Rationale:**

- JSON parses with `System.Text.Json`; a custom parser/DSL was rejected as
  unclear value for the current stage (`spec.md` explicitly warns against one).
- A CLI generator (not a Roslyn source generator) keeps generation debuggable
  and gradually adoptable; a source generator remains the likely later upgrade
  once the generated shape stabilises.
- Generated mappers hand-code reader calls and explicit `IsDBNull` handling
  rather than reflecting over properties; per-row reflection cost and
  unreadable stack traces avoided. Nullability defaults to *nullable*
  (PostgreSQL's own default) so an omission cannot produce a spurious NRE at
  map time.
- Generated naming: accessor class `UsersTable` carries typed `Column<T>`
  fields and the `Table` metadata; model record defaults to PascalCase of the
  table name (`User` set explicitly in the example). Generated names are
  validated globally unique because future `db.Select<User>()` (D001) needs an
  unambiguous `T`→table resolution.
- Output is deterministic in a configurable directory and namespace; the file
  layout is revisited in D010 (split by default, single still available).

**Consequences:**

- `Column` in `Chetah.Core` is no longer sealed and gained `Column<T>` — typed
  column metadata; with Stage 2 the generated `Table`/`PropertyColumns`
  metadata itself feeds the fluent chains, so `Where(...)` now consumes it
  directly.
- `examples`, `tests`, and generated files live beside their schema:
  `examples/BasicExample/{chetah-schema.json,Generated/}`;
  `tests/Chetah.CodeGen.Tests/{schema.json,Generated/}`.
- Revisit trigger: if hand-maintained alternatives or richer type needs appear
  (timestamptz, arrays), expect the type map and possibly the format to grow.

## D003 — Provider-neutral query snapshots

**Date:** 2026-11-05 (Stage 2, Core contract)

`SelectQuery` is a sealed record describing sources, joins, projection, filters,
grouping, ordering and pagination. `new SelectQuery(table)` and `table.SelectAll()`
still describe a full-table read. Clause collections and membership values are
snapshotted into read-only lists; deriving a query with `with` cannot change an
existing query's collections.

`SqlExpression` records carry CLR type and SQL nullability separately. Columns
identify a source ID and raw schema name; values are data, never SQL fragments.
The PostgreSQL layer remains responsible for validation, rendering and binding
parameters. This Core contract alone does not enable fluent query refinement;
the compiler and typed builder integration are separate implementation steps.

## D004 — Lambda translation over a finite expression surface

**Date:** 2026-11-05 (Stage 2, query refinement)

**Decision:** Stage 2 read queries are translated from C# lambdas into the
Core expression nodes by a finite, explicit translator. The surface it accepts
is decided: comparisons (null-safe via `IS [NOT] DISTINCT FROM`, with lifted
C# comparisons never leaking SQL UNKNOWN), boolean logic, nullable
`HasValue`/`Value`/`??`/`?:`, numeric casts (rendered as SQL `CAST`, never
silently discarded), `string.Contains/StartsWith/EndsWith` single-string
overloads (pattern parameters, wildcards escaped), membership `Contains` via a
parameterized `IN` snapshot, and the `Sql.*` aggregate markers
(`Count/Sum/Average/Min/Max`). Everything else fails with
`NotSupportedException` naming the expression — no client-side evaluation
fallback, no arbitrary getter invocation, no dynamic SQL.

**Rationale:**

- The frozen Core nodes stay concrete (no visitor framework, no plugin
  function registry); extending later means editing the translator + compiler
  jointly, which keeps validation honest.
- Values captured in a lambda are folded at the fluent call (constants and
  captured closure locals/field chains only — no arbitrary getters, no static
  field traversal) and snapshotted, so later mutations cannot change an
  already-built query. Numeric casts on scalars translate to SQL `CAST`
  semantics; membership items convert with the capturing code's own CLR cast
  semantics.
- Aggregates are expression-only markers that throw if invoked outside a query
  translation; aggregates are legal in SELECT/HAVING/ORDER BY of a grouped
  query and nowhere else.
- Explicit INNER/LEFT joins carry correct outer-row semantics through a hidden
  presence marker appended to the right side's derived source, so absence (and
  all-NULL matched rows) is distinguishable without primary-key assumptions.
- `RIGHT`/`FULL`/`CROSS` joins and automatic relationship loading stay
  unimplemented; joins must precede grouping, `DISTINCT` and pagination.

**Consequences:**

- `db.Select<T>()` is the primary typed entry point; `Compile()` returns the
  inspectable compiled query (SQL text + ordered parameters) without touching a
  database, and `ToListAsync()` executes it. Explicit `QueryAsync` with a
  hand-written query and mapper remains supported alongside the typed chains.
- `ProjectionMapper` plans output slots once and compiles a reader delegate
  once per execution; full single-table reads keep using the generated
  name-based `Map`, everything else uses generated `MapAt` with ordinal
  offsets.
- Revisit trigger: when a real query needs an expression outside the surface,
  extend the translator surface explicitly rather than adding evaluation.

## D005 — Diff/push schema tool, recorded as SQL migrations

**Date:** 2026-11-05 (Stage 7)

**Decision:** Schema changes are applied by a separate CLI project,
`src/Chetah.Db` (`chetah-db`), which never diffs file-to-file: it introspects the
live PostgreSQL catalog and compares that against `chetah-schema.json`. Each
applied change is also written to disk as plain SQL with journal metadata, so
the statements that hit the database are always reviewable.

**Details** (locked with the author before implementation; full plan in
[`schema-tooling.md`](../reference/schema-tooling.md)):

- The generator stays single-purpose (D002): `Chetah.CodeGen` owns the schema
  parser and typed-model output, `Chetah.Db` owns catalog work. The three commands
  are `diff` (print, write nothing), `apply`, and `repair` (replace a failed
  migration).
- One apply path for fresh and existing databases. No `create` command. On an
  empty database the first entry is the full CREATE script; on an existing
  database it is an introspective baseline (snapshot only, no DDL).
- `migrations/` holds one folder per migration, `<id>/<id>.sql` plus
  `<id>/meta/<id>_snapshot.json`, and an ordered `_journal.json` at its root.
  Statements are separated by
  `--> statement-breakpoint`; journal entries carry the id, slug, sha256 of the
  SQL payload and statement count. `apply` re-verifies each pending file's hash
  and refuses to run a hand-edited migration.
- `"chetah"."migrations"` (`id bigserial`, `hash`, `name`, `applied_at
  timestamptz`) records what has been applied, in Chetah's own `chetah` schema.
  The journal records what has been generated; only the table decides what ran.
- Each migration runs in one transaction together with its `"chetah"."migrations`
  row. A failed statement rolls everything back, the entry stays in the journal,
  and the runner records why in `meta/<id>_failed.json`. The next interactive
  `apply` offers Retry, Repair, or Cancel; non-interactive apply refuses instead
  of replaying a migration it knows failed. `chetah-db repair` is the explicit
  form. Repair re-plans the single pending entry from the current schema and the
  live catalog, verifies the database still matches the previous migration's
  snapshot, prints the replacement, and requires confirmation. It replaces the
  SQL in place (preserving the id and journal order) and archives every
  superseded attempt under `meta/`, so the original failure stays readable.
  Applied migrations are never touched, and repair refuses when more than one
  migration is pending. Retry remains a replay, not a blind one: the recorded
  operations are re-checked against the current catalog (a drop that now has
  dependents is refused) and any destructive statement is confirmed again.
  Entries without recorded metadata require confirmation.
- Destructive statements — DROP TABLE, DROP COLUMN, DROP INDEX, DROP CONSTRAINT,
  non-widening type changes, and `nullable → NOT NULL` on a column that already
  holds NULLs — require an interactive `yes`. Non-interactive runs refuse
  instead of assuming consent.
- Renames are paired through object identity (D006): keeping an object's `"$id"`
  while renaming its key produces `ALTER TABLE … RENAME`, and a genuine
  replacement has to be declared with `--drop-add` before it becomes drop + add
  under the destructive gate. Objects the schema file cannot express (check and
  exclusion constraints, expression and partial indexes, foreign keys from
  another schema) are read from the catalog and never removed: a statement that
  would take one down is refused with the object names before anything is
  written.
- `diff` records missing `"$id"` members but writes no DDL, and identity is
  resolved before anything is written, so a refusal leaves the file untouched.
  `diff --check` is the strict read-only form (no file write, no DDL, no lock,
  no internal table, exit code 1 when a migration is pending) and is what a CI
  job gates on.
- v1 was deliberately narrow: no rollback, seeds, CI mode, or other dialects.
  Constraints, indexes, defaults, identity columns, composite keys, referential
  actions and type modifiers are expressible now (D006/D007).

**Consequences:**

- Chetah gains a deployment tool, not a runtime feature: `Chetah.Postgres` is
  unchanged and takes no dependency on it.
- The schema file is now the source of truth for three things — generated
  models, query metadata, and the database itself. Drift is detected by `diff`
  before a query ever fails at runtime.
- Known ceiling: a `NOT NULL` column added to a populated table has no default
  to draw on, so the statement fails and the run rolls back until the schema is
  corrected.
- Revisit trigger: extend `SchemaDiff` when real friction appears rather than
  growing a migration DSL.

## D006 — Stable object identities, in the schema file and in the database

**Date:** 2026-11-06

**Decision:** Every table and column carries a persistent numeric identity, and
the schema file is where it lives: the tool writes `"$id": N` into
`chetah-schema.json` for objects that lack one and never asks the user to
assign one. `"chetah"."objects"` records the same identities in the database,
including tombstones for objects that were dropped.

**Why:**

- Matching by name cannot distinguish a rename from a drop plus an add, and the
  two have opposite consequences: one moves values, the other deletes them. An
  identity that survives the rename is the only reliable signal.
- Keeping the id *in the schema entry* means it travels with the declaration
  through the rename itself, through code review, and through git history. A
  sidecar map keyed by name would lose the trail at exactly the moment it is
  needed, and a suggestion engine ("this looks like a rename") is a guess about
  a destructive operation.
- The database copy is not redundant: it makes "never reuse an id" enforceable
  (a dropped object stays a row with `dropped_at` set, so allocation can start
  above the highest id ever issued), it lets an existing declaration adopt the
  ids it already has instead of looking like a wholesale replacement, and it
  catches merge accidents — a `"$id"` duplicated by two branches, or one claimed
  by a different object, is refused with the pair named.

**Details as built:**

- Allocation is `max(ids in the file, ids in the registry) + 1`, in declaration
  order, so it is deterministic and never reissues a tombstoned id. No sequence
  is needed, and `diff` — which may be the run that records the ids — writes
  only the schema file.
- **Two scopes.** Table ids are unique among tables (one counter per schema).
  Column ids are numbered per table, so `users`' columns are 1, 2, 3 and
  `orders`' are 1, 2, …: the same number can name a column in two tables. The
  registry key is therefore `(kind, coalesce(table_id, 0), id)`, and older
  registries with a global primary key on `id` are migrated by the bootstrap.
  The reason is merge behaviour: with one global counter, two branches each
  adding a column — to *different* tables — allocate the same next number and
  the merge is refused as a duplicate id, which is a false conflict. Scoping
  makes those two allocations independent.
- The price of scoping is that an id alone no longer names a column: identity is
  `(table, id)`, and copying a `$id` from one table to another is legal rather
  than caught.
- A `"$id"` this database never issued for an object it does not know is refused
  ("was never issued by Chetah"); a genuinely portable id from another
  environment is above what the scope has issued, so it is accepted. A
  hand-written id that coincides with an existing object is indistinguishable
  from a rename — that is inherent to keeping the id in the file, and the
  documented rule is that ids are tool-owned.
- The file is edited textually by a small JSONC reader that locates the table
  and column objects, because a re-serialize would delete the comments users
  keep in the file.
- A rename keeps the object's id and the diff emits `ALTER TABLE … RENAME`
  (table or column), reading the catalog as if the rename had already happened.
- An object with a new identity beside a tracked object that disappeared is
  refused, not inferred. The message names both resolutions: keep the `"$id"`
  for a rename, or pass `--drop-add table[.column]` to record a real drop plus
  add, which still passes the destructive gate.
- The registry is synchronized inside the migration transaction, so a rollback
  cannot leave identities describing a migration that never ran.
- **Portability:** the schema file is the portable half of identity — another
  database applies the same file and ends up with the same ids, because ids come
  from the file and allocation only happens for objects that have none. The
  registry is the applied half. A file id this database has never seen, for a
  name it already tracks under another id, and a file id that names a dropped
  object, are both refused with both ids named: never remapped, and never
  reinterpreted as a rename.

**Consequences:**

- `chetah-schema.json` gains machine-maintained members; review diffs show them
  when an object is genuinely new.
- Objects the tool did not create yet are adopted, not re-created: the first
  apply on an existing database records a baseline and identities for what is
  already there.
- Revisit trigger: if a real need appears to carry identities across databases
  (staging → production), the ids are already portable — revisit the conflict
  rules then, not the storage.

## D007 — Defaults are compared against what was applied, not against PostgreSQL's text

**Date:** 2026-11-06

**Decision:** The identity registry (`"chetah"."objects"`) records, per column,
two normalized texts: the default the schema file declared, and the default the
column held once the migration committed. A default has changed when either the
declaration or the stored value no longer matches its record.

**Why:** PostgreSQL rewrites default expressions when it stores them —
`pg_catalog.now()` becomes `now()`, `1 + 1` becomes `(1 + 1)`, a literal gains
its cast. Comparing the declared text with the catalog text therefore never
converged: an unchanged schema generated a new migration on every apply (four in
a row during the audit that produced this decision). A text canonicalizer cannot
fix that in general, because the rewrite rules belong to PostgreSQL; recording
what was applied can, for any expression.

**Consequences:**

- One extra column pair in the internal registry, written inside the migration
  transaction from the catalog as it is after the DDL, so it cannot describe a
  migration that rolled back.
- The normalized form exists **only** for comparison. DDL emits the declaration
  verbatim, because normalization strips casts and collapses whitespace outside
  literals — `'a  b'` and `'2020-01-01 10:00'::date` must reach the database
  unchanged.
- A default edited by hand in the database is still detected (the stored value
  drifts from the record), and so is an edited declaration.
- A registry written before this decision is read without the new columns; the
  next apply adds them. On that first run, a rewritten default costs at most one
  corrective `SET DEFAULT`.
- Revisit trigger: a change made outside Chetah and reverted between two runs is
  invisible to the record. The resulting state is identical, so this is accepted
  rather than solved.

## D008 — Schema format v1: one canonical shape, execution reported honestly

**Date:** 2026-11-08

**Decision:** `chetah-schema.json` has one canonical representation per object,
published as a JSON Schema contract (`chetah.schema.json`). The internal model
(`Schema`, `TableSchema`, `ColumnSchema`, …) is JSON-independent, and a
dedicated semantic validator reports cross-object problems with the declaration's
JSON path. The migration engine executes the same feature set as before, plus
foreign-key `MATCH`/deferrability and explicit constraint names; everything else
in the format is refused explicitly with its path instead of being ignored.

**Format decisions:**

- Root collections are `tables`, `enums`, `sequences`, `views` and
  `materializedViews`; all are optional, but at least one object must be
  declared so an empty file cannot read as "drop everything". `public` is the
  default namespace, and any table or other object may set `"schema"`.
- `nullable` omitted means nullable (PostgreSQL's default). Primary key columns
  are implicitly `NOT NULL`; declaring one nullable is an error.
- Defaults follow one rule: a JSON scalar is a SQL literal, `{ "sql": "..." }` is
  an expression. Strings are quoted and escaped, never parsed as SQL — the old
  "everything is raw SQL" convention silently changed meaning when copied.
  `"$schema"` doubles as the v1 opt-in: a string default requires it (a pre-v1
  file used raw SQL there, and `"1 + 1"` cannot be told apart from text), so a
  pre-v1 file cannot be reinterpreted silently. Numbers, booleans, null and
  `{ "sql": ... }` need no marker.
- Primary keys: `true` on a single column, or a table-level object with `name`
  and `columns`. Column-level and table-level declarations cannot coexist.
- Unique constraints: `true` on a single column, or table-level objects with
  `name`, `columns` and `nullsNotDistinct`.
- Foreign keys: exactly one structure — an array of objects with `columns` and a
  nested `references` object — with optional `name`, actions, `match`,
  `deferrable` and `initiallyDeferred`. There is no column-level string
  shorthand and no object-keyed alternative.
- Indexes: an array of objects with a single ordered `keys` representation
  (column or expression, direction, nulls, operator class) plus `unique`,
  `method`, `where`, `include` and `nullsNotDistinct`. Separate `columns` and
  `expressions` arrays were rejected as two ways to say one thing.
- Checks, generated columns, identity options and the PostgreSQL-only objects
  are part of the contract from the start. A schema that uses what the engine
  cannot apply is refused by `diff`/`apply` with the offending paths, rather than
  silently approximated. This is the explicit ceiling the redesigned task allowed:
  the schema foundation is complete, execution is incremental.

**Why:**

- v1 grew piecemeal (object-keyed indexes and foreign keys, array primary keys,
  raw-SQL defaults, a 7-type whitelist). Every new feature had to invent a second
  shape, and authors had to remember which collection used which.
- Keeping the id system, catalog introspection, diff, journal, retry/repair and
  replay untouched was a hard constraint. Format work is reversible; migration
  history is not. Extending the internal model and translating it into the same
  `CatalogState` achieves the redesign without rewriting the engine.
- A published JSON Schema keeps the documentation, the editor and the parser
  honest. Unknown properties are rejected in all three, and a contract test fails
  when a parser property is missing from the schema.
- Literal defaults close a real footgun: `"default": "now()"` used to work and
  now means the text. The conversion table in the guide makes the change
  explicit; no file is reinterpreted silently.

**Consequences:**

- Old schema files must be converted (guide: "Converting an older file"). The
  tool does not accept both formats: two competing syntaxes for the same feature
  was the problem being solved.
- Default booleans are emitted as `true`/`false`, which is what the catalog
  prints; conversion of a file that used `"true"` therefore produces the same
  stored default.
- The migration engine refuses generated columns, checks, enums, sequences,
  views, non-public schemas, arrays, types outside the executable set, and the
  advanced index/identity options. The refusal names every declaration. Reports
  and tests cover these paths so the limitation is visible, not folklore.
- Foreign keys gained `match`, `deferrable` and `initiallyDeferred`, and
  constraint names became explicit in the file (still defaulting to the old
  synthesized names), so existing databases see no churn.
- Revisit trigger: when one of the refused features is needed end to end, extend
  `SchemaCatalog`/`SchemaDiff` for that feature and remove only its refusal.

## D009 — Terminal operators read only the rows they need

**Date:** 2026-11-08 (read API follow-up to Stage 2)

**Decision:** `TypedSelectQuery<T>` gained `FirstAsync`, `FirstOrDefaultAsync`,
`SingleAsync`, `SingleOrDefaultAsync`, `AnyAsync` and `CountAsync`. Each is a
thin layer over the existing compiler and executor: no new query objects, no new
expression nodes, no client-side evaluation.

**Details:**

- `First*` adds `LIMIT 1`, `Single*` adds `LIMIT 2` (two rows are enough to
  prove "more than one" without reading the rest). A smaller `Limit` already on
  the query wins, because pagination is part of the query: `Limit(0)` still
  yields no row and `Limit(1)` still bounds `Single`. `Offset`, filters,
  projections and ordering are untouched.
- `Any` compiles to one constant output row capped at one row, keeping every
  clause that decides whether a row exists — predicate, joins, `DISTINCT`,
  grouping, `HAVING`, paging — and dropping `ORDER BY`, which cannot create or
  remove a row.
- `Count` compiles to a direct `SELECT COUNT(*)` over the same `FROM`/`JOIN`/
  `WHERE` when those clauses already decide the row count. When the query yields
  fewer rows than it reads — `DISTINCT`, `GROUP BY`/`HAVING`, `LIMIT`/`OFFSET` —
  the complete SELECT is compiled as a derived table and counted
  (`SELECT COUNT(*) FROM (…) AS "__chetah_count"`). Ordering is never rendered —
  it can choose which rows a limit keeps, never how many — but it is still
  validated, so a chain `Compile` rejects is rejected by the count too.
- A missing row is `InvalidOperationException` (as in LINQ); `*OrDefault`
  returns `default`. Uniqueness in `Single*` is judged within the query's own
  result, pagination included, so `Limit(1).SingleAsync()` cannot report
  "more than one".

**Why:**

- Reading `ToListAsync().First()` pulls every row and changes behavior whenever
  the caller's limit is smaller than the data.
- Count must answer "how many rows does this SELECT return", and `COUNT(*)`
  cannot observe `DISTINCT`, grouping or paging. The derived table is internal
  compiled SQL, not a public query-builder feature, so it does not widen the API;
  clamping the number in code was the alternative and it silently miscounted
  whenever a projection or `HAVING` reduced the row set first.
- Keeping the terminal methods over the same `CompiledQuery`/`ExecuteAsync`
  path preserves parameterization, compiled mappers and cancellation.

**Consequences:**

- `CountAsync` returns `long`, matching `Sql.Count()`.
- Count statements are one round trip, never a materialized row set. Cost follows
  the query: the direct form needs no derived table; the counted form pays for
  the inner sorted/distinct/grouped work only when the query asked for it.
- Revision (2026-11-08): an earlier cut refused `DISTINCT` and grouped counts
  because subqueries were out of scope. That restriction covers the public query
  API, not compiler-internal SQL, so the derived-table form replaced the refusal.

## D010 — Split generated output by default, with an ownership manifest

**Date:** 2026-11-08 (output organization and CLI usability)

**Decision:** `Chetah.CodeGen` defaults to `--layout split`: one file per table
under `Generated/Tables/`, one per enum under `Generated/Enums/`, and a small
shared `Generated/Chetah.Schema.g.cs` holding the `Bindings` registry. Files are
named after the generated C# type (`Tables/User.g.cs`, `Enums/UserStatus.g.cs`).
Each table file carries its accessor class, record, mappers and registry binding
field. `--layout single` keeps the previous one-file output. Both layouts expose
the same public generated API (`UsersTable`, `User`, `ChetahSchema.UserBinding`,
`ChetahSchema.Bindings`).

**Rationale:**

- A diff on a one-table change touches one generated file instead of the whole
  schema, which makes review and merge conflicts smaller.
- The shared registry is the only cross-table coupling; keeping it small means
  table files are genuinely independent.
- Deterministic filenames and a manifest allow deleting stale output without a
  broad `*.cs` sweep, which could destroy user-authored files.

**Consequences:**

- `Generated/.chetah-codegen.json` is written last and lists every owned file.
  Only manifest entries are deleted, and only when the file still starts with
  the generated marker.
- Switching layouts removes the other layout's files in the same run. Files at
  generated paths that are not marked as generated are refused, not overwritten.
- Generated type and file names must be unique case-insensitively; collisions
  fail before any output is written.
- Enums get a label-list type (`Values`) but columns still map to `string`, so
  model properties and query behavior do not change.

## D011 — INSERT is an immutable Core query, with generated write metadata and shared RETURNING

**Date:** 2026-11-08 (Stage 3, INSERT)

**Decision:** INSERT is modelled in `Chetah.Core` as an immutable `InsertQuery`
(table, rows, conflict, returning), compiled by `PostgresInsertCompiler`, and
executed through the existing `PostgresDb` parameter/reader path. The fluent
typed builder lives in `Chetah.Postgres` and reuses the generated binding.

**Details:**

- A row is a `Dictionary<string, SqlExpression>` snapshot. A column absent from
  the dictionary is omitted from the SQL column list (database default or NULL);
  `SqlValue(null, …)` is an explicit NULL parameter; `SqlDefault` renders
  `DEFAULT`; omitted identity columns use their sequence. Rows in one statement
  must share a column set, enforced before rendering.
- Generated `Column<T>` metadata now carries `IsNullable`, `HasDefault` and
  `ColumnGeneration` (`Always` for identity ALWAYS, `Computed` for generated
  columns). The compiler rejects supplying those columns, explicit NULL on a
  NOT NULL column, and omitting a NOT NULL column that has no default. This is
  metadata-driven, not a PostgreSQL round trip.
- `Values` reads public readable properties and resolves each through the
  generated `PropertyColumns` map (raw column names also work). It snapshots
  values immediately; a second `Values`/`DefaultValues` call on the same query
  is refused rather than silently replacing the first.
- `RETURNING` is `ReturningClause`, a Core record holding `SelectItem`s that
  future UPDATE/DELETE can reuse. INSERT only chooses the source-0 bare rendering
  mode (`"id"` rather than `"t0"."id"`); projection translation, validation,
  parameterization and compiled mapping are the SELECT machinery.
- Parameter rendering is not reimplemented: `PostgresCompiler.Compilation`
  exposes fragment hooks (`ValidateValueFragment`, `RenderValueFragment`,
  `RenderProjectionFragment`) used by the INSERT compiler with one shared slot
  list. Slots follow SQL order: VALUES, conflict assignments, RETURNING.
- Conflict handling is typed: target columns come from generated properties
  (or an anonymous object of them); `DO UPDATE` assignments are `Set`,
  `SetExcluded` (same-column `EXCLUDED`) and `SetDefault`. No SQL fragment API
  exists.

**Unsupported, deliberately:** partial conflict targets (`ON CONFLICT … WHERE`),
expression conflict targets, arbitrary `EXCLUDED` expressions, `OVERRIDING
SYSTEM VALUE`, UPDATE/DELETE, and transaction APIs. Each is a separate feature,
not an inference from this one.

**Consequences:**

- Affected-row counts come from `ExecuteNonQueryAsync`; RETURNING uses the
  existing reader mapping and its terminal operators judge zero/one/many rows.
- Generated metadata became larger by one object initializer per constrained
  column; defaults are omitted when they match `Column`'s initial values.
- The same `InsertQuery`/`ReturningClause` shape is the starting point for
  UPDATE and DELETE; no redesign should be needed to add their WHERE/assignment
  clauses.
- Revisit trigger: if partial-index conflict targets become necessary, extend
  `InsertConflict` with a validated predicate expression rather than adding a
  raw SQL escape hatch.
