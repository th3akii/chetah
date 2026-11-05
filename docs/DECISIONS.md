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

- Underneath, `PostgresCompiler.ToSql` and `PostgresDb` keep their roles:
  this changes the API surface on top of the query representation, not the
  compile→execute pipeline.
- Prerequisite: typed model generation (column types + `Table`↔model
  resolution) from the planned next milestones; until then the
  `users.SelectAll()` entry point remains the only working syntax, and
  `examples/BasicExample` + `docs/BASIC_SELECT.md` describe it as MVP
  scaffolding, superseded by this decision once typed models exist.
- Revisit trigger: if auto-resolution of table metadata from `T` proves
  awkward (metadata-independent discovery, multiple tables per model), this
  entry is expected to be revised — the preference is directional, not final.

## D002 — JSON schema file + CLI generator, explicit generated mappers

**Date:** 2026-11-05 (milestone 002, schema-first typed models)

**Decision:** The schema source of truth is a small JSON file
(`myorm-schema.json`), models are produced by a plain CLI tool
(`src/MyOrm.CodeGen`, `dotnet run`), and row mapping stays an explicit
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
- Output is one deterministic `MyOrm.Schema.g.cs` in a configurable directory
  and namespace; only that file is ever written.

**Consequences:**

- `Column` in `MyOrm.Core` is no longer sealed and gained `Column<T>` — typed
  column metadata for upcoming `Where`/`Select` chains.
- `examples`, `tests`, and generated files: `examples/BasicExample/{myorm-schema.json,Generated/MyOrm.Schema.g.cs}`;
  `tests/MyOrm.CodeGen.Tests/{schema.json,Generated/MyOrm.Schema.g.cs}`.
- Revisit trigger: if hand-maintained alternatives or richer type needs appear
  (timestamptz, arrays), expect the type map and possibly the format to grow.
