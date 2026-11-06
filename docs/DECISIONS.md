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
- `chetah_migrations` (`id bigserial`, `hash`, `name`, `applied_at
  timestamptz`) records what has been applied in the schema the connection
  resolves (`current_schema()`, i.e. the search path). The journal records what
  has been generated; only the table decides what ran.
- Each migration runs in one transaction together with its `chetah_migrations`
  row. A failed statement rolls everything back, the entry stays in the journal,
  and the next `apply` retries it. A retry is not a blind replay: the entry's
  recorded operations are re-checked against the current catalog (a drop that
  now has dependents is refused) and any destructive statement is confirmed
  again. Entries without recorded metadata require confirmation.
- Destructive statements — DROP TABLE, DROP COLUMN, non-widening type changes,
  and `nullable → NOT NULL` on a column that already holds NULLs — require an
  interactive `yes`. Non-interactive runs refuse instead of assuming consent.
- Renames are not paired; they surface as drop + add under the same gate.
  Foreign objects (constraints and standalone indexes) are read from the
  catalog and never removed: a statement that would take one down — dropping
  its column or its table — is refused with the object names before anything is
  written.
- v1 is deliberately narrow: no rollback, seeds, indexes/constraints (Stage
  7.5), CI mode, or other dialects. Types compare on PostgreSQL type names, so
  type modifiers are not diffed.

**Consequences:**

- Chetah gains a deployment tool, not a runtime feature: `Chetah.Postgres` is
  unchanged and takes no dependency on it.
- The schema file is now the source of truth for three things — generated
  models, query metadata, and the database itself. Drift is detected by `diff`
  before a query ever fails at runtime.
- Known ceiling: a `NOT NULL` column added to a populated table has no default
  to draw on, so the statement fails and the run rolls back until the schema is
  corrected.
- Revisit trigger: extend `SchemaDiff` when real friction appears (rename
  pairing, defaults, constraint diffing) rather than growing a migration DSL.
