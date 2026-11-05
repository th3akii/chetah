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
