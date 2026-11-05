# Schema tooling and migrations (Stage 7)

> **Status:** Implemented 2026-11-05 in `src/MyOrm.Db` (`myorm-db`) and verified
> against live PostgreSQL. The decisions below are recorded in
> [`DECISIONS.md`](DECISIONS.md) D005. Companion documents:
> [`ROADMAP.md`](ROADMAP.md) (Stage 7), [`SPEC.md`](SPEC.md).

## Why

`myorm-schema.json` is the source of truth for generated code, but nothing uses
it for the database itself: the live schema is created/evolved by hand. Stage
2's live fixtures already seed PostgreSQL with raw SQL scripts.

The known failure mode — recorded in the roadmap — is silent drift: the schema
file says one thing, the database another, and the mismatch only appears when a
query runs (e.g. a renamed column ending in a runtime `42703` error).

## What gets built

A diff/push tool in the drizzle-kit mental model, with cheap inspection added:

- It never diffs file-to-file. `apply` decides by introspecting the live
  PostgreSQL catalog (`pg_class`, `pg_attribute`, `pg_type`, …) and comparing
  against `myorm-schema.json`.
- Every executed migration is also recorded to disk as plain SQL, with journal
  metadata, so the bare statements that hit the database are always visible and
  reviewable.

## Decisions (locked with the author, 2026-11-05)

1. **Own CLI project:** `src/MyOrm.Db` (`myorm-db`), reusing the schema
   parser/types from `MyOrm.CodeGen`. The generator stays single-purpose (D002).
2. **One apply path:** fresh and existing databases go through the same
   diff→journal→execute flow. No dedicated `create` command. First entry on an
   empty database is the full CREATE script; first entry on an existing database
   is an introspective baseline (schema snapshot, `-- baseline` SQL file, no DDL).
3. **Destructive changes are prompted:** DROPs, type narrowing, and
   nullable→NOT NULL on populated columns require interactive confirmation.
   Never silently applied.
4. **Applied-state table:** `myorm_migrations` — `id` bigserial, `hash` text,
   `name` text, `applied_at` timestamptz — created in the schema the connection
   resolves (search path, fallback `public`). It owns "what has been applied";
   the journal owns "what has been generated".
5. **Transaction per `apply`:** the whole migration runs in one transaction. A
   failed statement rolls the run back; the recorded-but-unapplied migration is
   retried by the next `apply`. Per-statement execution was considered and
   rejected: its executor loop is simpler, but a failure would leave a
   half-migrated database needing per-statement progress bookkeeping, and an
   already-confirmed destructive statement could not be undone.
6. **Drizzle-style record layout:**

   ```text
   migrations/
     <UTC-stamp>_<slug>.sql                      # plain SQL that was applied
     meta/
       _journal.json                             # ordered generation record
       <UTC-stamp>_<slug>_snapshot.json          # full schema state after it
   ```

   Statements inside a file are separated by `--> statement-breakpoint`.
   Each journal entry stores the UTC stamp, slug, sha256 of the SQL payload,
   statement count, and per-statement operation metadata (kind, table, column,
   destructive flag). `apply` verifies each file's hash against the journal
   before executing, so hand-edits cannot silently diverge from what was
   recorded. Snapshots exist for auditability; diffing always uses the catalog.

   A pending entry is replayed only after re-validation: its recorded drops are
   checked against the *current* catalog (a drop would now remove an index or
   constraint → refused), and any recorded destructive statement is confirmed
   again before it runs. A hash match proves the file is unchanged, not that the
   database is; replaying against a different database (*e.g.* with redirected
   stdin) therefore cannot slip a destructive change through. Entries without
   metadata cannot be checked and require confirmation.

## Commands

```sh
dotnet run --project src/MyOrm.Db -- diff  --schema <schema.json> [--connection <conn>] [--migrations <dir>]
dotnet run --project src/MyOrm.Db -- apply --schema <schema.json> [--connection <conn>] [--migrations <dir>]
```

- `diff` — prints the DDL the catalog comparison produces, flags destructive
  items, writes nothing.
- `apply` — runs the destructive confirmation gate, writes the record files,
  executes the migration in one transaction, records it in
  `myorm_migrations`.

The connection string defaults to `MYORM_CONNECTION_STRING`; migrations default
to `migrations/` beside the schema file. Both commands resolve the managed
schema with `current_schema()` (the connection's search path) and fail loudly
when the search path names no existing schema.

Day-to-day flow: edit `myorm-schema.json` → regenerate code (existing flow when
typed models change) → `myorm-db diff` to review → `myorm-db apply` to adopt.

## Scope details

- **Supported operations (v1):** create/drop table, add column, drop column,
  alter nullability, alter type among the supported scalar types (widening is
  free; narrowing is gated). Column/tables added and removed in schema-file
  order.
- **Renames are not paired:** a rename comes out as drop+add and passes the
  same destructive gate. Automatic rename pairing stays open until real
  friction appears.
- **Foreign objects are left alone:** constraints and standalone indexes the
  schema file does not express are read from `pg_constraint`/`pg_index`, and a
  plan that would remove one is refused before anything is written. `DROP
  COLUMN` takes the column's indexes and constraints with it silently, and
  `DROP TABLE` takes every object on the table, so both are blocked with the
  object names reported by `diff` and `apply`. Removing them stays a manual
  step until Stage 7.5 can express them.
- **Pending migrations are re-validated, not trusted:** the same dependency
  check and the destructive confirmation run again on every replay, against the
  catalog as it is now. A recorded `DROP` that has since gained a dependent
  index is refused; a recorded destructive change is confirmed again, and a
  non-interactive replay refuses rather than assuming consent.
- **Not in scope:** down/rollback migrations, seeds/data migrations,
  non-interactive/CI mode, indexes/constraints (Stage 7.5), schema-JSON
  ergonomics (Stage 7.6), other dialects (Stage 6), concurrent applies.

Implementation notes as built:

- Types compare on PostgreSQL type names (`int4`, `int8`, `text`, `bool`,
  `uuid`, `timestamp`), so a column that differs in spelling (e.g.
  `timestamptz`) is reported as a gated type change rather than silently
  accepted. Type modifiers (`timestamp(3)`) are not compared.
- Adding a `NOT NULL` column to an already-populated table has no default to
  draw on; the statement fails, the transaction rolls back, and the journal
  entry is retried after the schema is corrected. Defaults are not expressed by
  the schema file yet.
- Managed tables are those in the resolved schema; `myorm_migrations` itself is
  never diffed. Tables in other schemas are untouched.
- A constraint with no column list (a table-level object) blocks any column
  drop on that table: the schema file cannot say whether it references the
  column, so the refusal is deliberately conservative. A foreign key owned by
  another table is not read — PostgreSQL rejects the dependent drop itself and
  the transaction rolls back.

## Verification

Disposable PostgreSQL fixtures, the same pattern as Stage 2 acceptance: unique
schema per run, never touching shared tables, configured runs (a set
`MYORM_CONNECTION_STRING`) distinguished from skipped ones.

`tests/MyOrm.Db.Tests` covers:

- **Offline diff** — fresh-create column order, add/drop column, drop table,
  rename as gated drop+add, widening free / narrowing gated, `SET NOT NULL`
  gated only when the column holds NULLs, `DROP NOT NULL` free, no-op on a
  matching catalog, and drops of a column or table that carries a constraint or
  index blocked (a retained column's index is not a dependency).
- **Record layout** — SQL file, snapshot, journal entry and hash; hand-edit
  changes the hash; baseline record has no statements; same stamp and slug get
  distinct ids.
- **Live apply** — fresh database created via `apply`, journal/snapshot/SQL
  matching what ran, `myorm_migrations` rows with the right hash and name,
  `diff` reporting drift without writing, declined destructive gate writing
  nothing, failed statement rolling back with the journal entry present and no
  migration row (rerun succeeds after the fix), hand-edited SQL refused, and an
  existing database recorded as a baseline.
- **Live dependency refusal** — a table with a real index and unique
  constraint, and a real index on a second table: neither column drop nor table
  drop is applied, the objects survive, and no journal entry or
  `myorm_migrations` row is added.
- **Live replay validation** — a pending destructive entry replayed after an
  index was added to its target column is refused before confirmation; a pending
  destructive entry is refused when the confirmation gate says no and applied
  when it says yes; a pending entry without metadata is refused unless
  confirmed; a safe pending entry is replayed without consulting the gate.

The CLI itself was exercised against a live database for the same flow,
including both destructive-gate outcomes (non-interactive refusal, interactive
`yes`).

## Decisions log handoff

Transferred to [`DECISIONS.md`](DECISIONS.md) D005 when the code landed.
