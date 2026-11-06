# Decision Log

Decisions arising during development, with rationale. Deliberately unresolved
topics stay in `docs/SPEC.md` §7; an entry here is a directional commitment that
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
  unclear value for the current stage (`SPEC.md` explicitly warns against one).
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
- Output is one deterministic `Chetah.Schema.g.cs` in a configurable directory
  and namespace; only that file is ever written.

**Consequences:**

- `Column` in `Chetah.Core` is no longer sealed and gained `Column<T>` — typed
  column metadata; with Stage 2 the generated `Table`/`PropertyColumns`
  metadata itself feeds the fluent chains, so `Where(...)` now consumes it
  directly.
- `examples`, `tests`, and generated files: `examples/BasicExample/{chetah-schema.json,Generated/Chetah.Schema.g.cs}`;
  `tests/Chetah.CodeGen.Tests/{schema.json,Generated/Chetah.Schema.g.cs}`.
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
[`SCHEMA_TOOLING.md`](SCHEMA_TOOLING.md)):

- The generator stays single-purpose (D002): `Chetah.CodeGen` owns the schema
  parser and typed-model output, `Chetah.Db` owns catalog work. The two commands
  are `diff` (print, write nothing) and `apply`.
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
  and the next `apply` retries it. A retry is not a blind replay: the entry's
  recorded operations are re-checked against the current catalog (a drop that
  now has dependents is refused) and any destructive statement is confirmed
  again. Entries without recorded metadata require confirmation.
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
- A default edited by hand in the database is still detected (the stored value
  drifts from the record), and so is an edited declaration.
- A registry written before this decision is read without the new columns; the
  next apply adds them. On that first run, a rewritten default costs at most one
  corrective `SET DEFAULT`.
- Revisit trigger: a change made outside Chetah and reverted between two runs is
  invisible to the record. The resulting state is identical, so this is accepted
  rather than solved.
