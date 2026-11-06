# Schema tooling and migrations (Stage 7)

> **Status:** Implemented 2026-11-05 in `src/Chetah.Db` (`chetah-db`) and verified
> against live PostgreSQL; extended 2026-11-06 with persistent object identities,
> constraint/default/identity/index diffing, the `chetah` internal schema and
> run serialisation. The decisions below are recorded in
> [`decisions.md`](../project/decisions.md) D005 and D006. Companion documents:
> [`roadmap.md`](../project/roadmap.md) (Stage 7), [`spec.md`](../project/spec.md).
> How to write the schema file itself:
> [`writing-a-schema.md`](../guide/writing-a-schema.md).
> Known gaps with reproductions and suggested fixes:
> [`migration-limitations.md`](../notes/migration-limitations.md).

## Why

`chetah-schema.json` is the source of truth for generated code, but nothing uses
it for the database itself: the live schema is created/evolved by hand. Stage
2's live fixtures already seed PostgreSQL with raw SQL scripts.

The known failure mode — recorded in the roadmap — is silent drift: the schema
file says one thing, the database another, and the mismatch only appears when a
query runs (e.g. a renamed column ending in a runtime `42703` error).

## What gets built

A diff/push tool in the drizzle-kit mental model, with cheap inspection added:

- It never diffs file-to-file. `apply` decides by introspecting the live
  PostgreSQL catalog (`pg_class`, `pg_attribute`, `pg_type`, …) and comparing
  against `chetah-schema.json`.
- Every executed migration is also recorded to disk as plain SQL, with journal
  metadata, so the bare statements that hit the database are always visible and
  reviewable.

## Decisions (locked with the author, 2026-11-05)

1. **Own CLI project:** `src/Chetah.Db` (`chetah-db`), reusing the schema
   parser/types from `Chetah.CodeGen`. The generator stays single-purpose (D002).
2. **One apply path:** fresh and existing databases go through the same
   diff→journal→execute flow. No dedicated `create` command. First entry on an
   empty database is the full CREATE script; first entry on an existing database
   is an introspective baseline (schema snapshot, `-- baseline` SQL file, no DDL).
3. **Destructive changes are prompted:** DROPs, type narrowing, and
   nullable→NOT NULL on populated columns require interactive confirmation.
   Never silently applied.
4. **Applied-state table:** `"chetah"."migrations"` — `id` bigserial, `hash`
   text, `name` text, `applied_at` timestamptz — in Chetah's own `chetah`
   schema, created on demand and never diffed (introspection reads the
   application schema only). An older `chetah_migrations` in the application
   schema is moved into `chetah` and renamed in one transaction, so its history
   (and the ids it implies) survives the move; a table stranded between those
   two steps by an interrupted manual move is picked up instead of being
   shadowed by a fresh empty one. The table owns "what has been applied"; the
   journal owns "what has been generated".
5. **Transaction per `apply`:** the whole migration runs in one transaction. A
   failed statement rolls the run back; the recorded-but-unapplied migration is
   retried by the next `apply`. Per-statement execution was considered and
   rejected: its executor loop is simpler, but a failure would leave a
   half-migrated database needing per-statement progress bookkeeping, and an
   already-confirmed destructive statement could not be undone.
6. **Drizzle-style record layout, one folder per migration:**

   ```text
   migrations/
     _journal.json                               # ordered generation record
     <UTC-stamp>_<slug>/
       <UTC-stamp>_<slug>.sql                    # plain SQL that was applied
       meta/
         <UTC-stamp>_<slug>_snapshot.json        # full schema state after it
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
dotnet run --project src/Chetah.Db -- diff  --schema <schema.json> [--connection <conn>] [--migrations <dir>] [--drop-add <object> ...] [--check]
dotnet run --project src/Chetah.Db -- apply --schema <schema.json> [--connection <conn>] [--migrations <dir>] [--drop-add <object> ...]
```

- `diff` — prints the DDL the catalog comparison produces, flags destructive
  items, writes no DDL. It does persist missing `"$id"` members into the schema
  file: identity has to be recorded where it is decided, or the next run would
  decide different ones. Identity is resolved and checked *first*; a run that
  ends in a refusal has not touched the file.
- `diff --check` — read-only: no `"$id"` write, no DDL, no advisory lock, no
  internal table creation, and it works on a database that has never been
  migrated. It names the members it would write and exits **1** when a
  migration is pending, **0** when the database matches the schema, which is
  what a CI job gates on.
- `apply` — runs the destructive confirmation gate, writes the record files,
  executes the migration in one transaction, records it in
  `"chetah"."migrations"`. `--check` is rejected here rather than silently
  behaving like `diff`.
- `--drop-add <table>` / `--drop-add <table>.<column>` — resolves an otherwise
  ambiguous replacement; see *Identities*.

The connection string defaults to `CHETAH_CONNECTION_STRING`; migrations default
to `migrations/` beside the schema file. Both commands resolve the application
schema once (the first existing schema on the search path that is not `chetah`),
pin it with `SET search_path`, and — except in read-only mode — hold a session
advisory lock for the run, so two runs cannot plan from the same state or
allocate the same identity.

## Identities

Matching by name cannot tell a rename from a drop plus an add, and the two have
opposite consequences: one moves values, the other deletes them. Every table and
column therefore carries a persistent `"$id"` in `chetah-schema.json`:

```json
"users": {
  "$id": 1,
  "columns": {
    "username": { "$id": 3, "type": "text" }
  }
}
```

- **Generated, not authored.** The tool writes the members it is missing, by
  editing the file textually (a small JSONC reader locates the objects), so
  comments and layout survive. Users never assign ids by hand.
- **Two scopes.** Table ids are unique among tables; **column ids are numbered
  per table** (`users`' columns 1, 2, 3, `orders`' columns 1, 2, …). Adding a
  column to one table never moves another table's numbers, so two branches that
  touch different tables merge without an id conflict. A column id only means
  something together with its table.
- **A rename keeps the id.** Renaming the key while keeping `"$id": 3` declares
  the rename; the plan emits `ALTER TABLE … RENAME COLUMN` and reads the catalog
  as if the rename had already happened, so the column is neither dropped nor
  re-added.
- **A replacement does not.** An object with a new identity next to a tracked
  object that vanished is ambiguous, and the plan is refused with both
  resolutions named: keep the `"$id"` for a rename, or pass
  `--drop-add table.column` to record a real drop plus add (still gated).
- **Ids are never reused.** `"chetah"."objects"` records every id with its
  scope; dropped objects stay as tombstones, and allocation starts above the
  highest id that scope has ever issued.
- **Ids are issued by the tool.** A `"$id"` that this database never issued, for
  an object it does not know, is refused ("was never issued by Chetah") instead
  of being honoured — the one exception is a genuinely portable id from another
  environment, which is above what this scope has issued. A hand-written id that
  happens to match an existing object is indistinguishable from a rename, which
  is why editing ids by hand is unsupported.
- **Conflicts are refused, not guessed.** A `"$id"` duplicated by two branches
  in the same scope, a `"$id"` claimed by a different object, or one that names
  a dropped object stops the run with the offending pair.
- **Existing files adopt.** A schema file without `"$id"`s keeps the ids the
  registry already holds for its names, so a declaration written before
  identities existed is not mistaken for a rewrite.
- **The file is the portable half.** Development resolves and records the ids;
  staging and production apply the same file and end up with the same
  identities. The registry is the applied half: it knows which ids this database
  has seen, which were dropped, and what each column's default actually became.
- **A contradiction is a refusal.** A `"$id"` this database never saw, for a
  name it already tracks under a different id, means the file and the database
  were adopted separately. The message names both ids and both resolutions —
  delete the `"$id"` to adopt the tracked id, or align it — and nothing is
  remapped silently. Reusing a dropped id is refused the same way.
- The registry is synchronized inside the migration transaction, from the
  catalog as it is after the DDL, so a rollback cannot leave identities
  describing a migration that never ran.

## Schema vocabulary

```json
"users": {
  "primaryKey": ["tenant_id", "email"],
  "unique": ["email", ["tenant_id", "email"]],
  "indexes": { "users_state_idx": { "columns": ["state"], "unique": false } },
  "foreignKeys": {
    "users_tenant_fkey": {
      "columns": ["tenant_id"],
      "references": "tenants",
      "referenceColumns": ["id"],
      "onDelete": "cascade",
      "onUpdate": "no action"
    }
  },
  "columns": {
    "tenant_id": { "type": "integer", "nullable": false },
    "email": { "type": "text", "nullable": false, "unique": true },
    "role_id": { "type": "integer", "references": "roles.id" },
    "created_at": { "type": "timestamp", "nullable": false, "default": "now()" },
    "id": { "type": "bigint", "nullable": false, "identity": true },
    "code": { "type": "varchar(100)" }
  }
}
```

- `primaryKey` — column list, in constraint order; composite keys are just a
  longer list.
- `unique` — array of column names or column-name arrays: single or composite
  UNIQUE constraints. The column-level `"unique": true` is the single-column
  shorthand.
- `indexes` — one entry per standalone index, with `columns` and an optional
  `unique`.
- `references` (column) — `table.column` or `schema.table.column`, the
  single-column shortcut.
- `foreignKeys` (table) — keyed by constraint name: `columns`,
  `references` (`table` or `schema.table`), `referenceColumns`, optional
  `onDelete`/`onUpdate` (`no action`, `restrict`, `cascade`, `set null`,
  `set default`). Actions are part of the definition: changing one replaces the
  constraint.
- `default` — raw SQL, applied verbatim. It is compared against the recorded
  declaration and the column's recorded result in a normalized form (casts
  stripped, layout collapsed outside literals), but that form is never emitted:
  `'a  b'` keeps its two spaces and `'2020-01-01 10:00'::date` keeps its cast.
- `identity` — `GENERATED BY DEFAULT AS IDENTITY`, integer/bigint only, implies
  `NOT NULL` and cannot be combined with `default`.
- `type` — `integer`, `bigint`, `text`, `boolean`, `uuid`, `timestamp`,
  `varchar`, with a modifier where PostgreSQL allows one: `varchar(100)`,
  `timestamp(3)`. Type modifiers are compared: a live `timestamp(3)` is not a
  declared `timestamp`, and a larger modifier is treated as widening.
- Check and exclusion constraints, and expression, partial, covering (`INCLUDE`),
  non-btree, non-default-opclass or non-default-sort-order indexes, are not
  expressible yet: they are read from the catalog, never created or dropped, and
  a statement that would remove one is refused. So is a foreign key from another
  schema that points at a managed table. An index differing only by `COLLATE`,
  `NULLS NOT DISTINCT` or a storage option is not distinguished — see
  [`migration-limitations.md`](../notes/migration-limitations.md).

## Scope details

- **Supported operations:** create/drop table, add/drop column, rename
  table/column (by identity), alter type among the supported scalar types and
  modifiers (widening is free; narrowing is gated), alter nullability, set/drop
  default, add/drop identity, add/drop primary key, unique constraint (single or
  composite) and foreign key (single or composite, with actions), create/drop
  indexes.
- **Ordering is fixed:** renames, then index drops, then constraint drops, then
  column drops, then table drops, then table creates, then column adds and
  alters, then primary keys and unique constraints, then foreign keys, then
  index creates. Nothing is removed implicitly through the object that carried
  it, and a foreign key can point at a table created in the same migration.
- **Definitions, not names:** constraints and indexes are matched on kind,
  columns, reference target, referential actions and uniqueness, so a hand-named
  equivalent does not churn on every apply.
- **Defaults are compared against what was applied:** the registry records the
  declared text and the column's resulting default, so a PostgreSQL rewrite
  (`pg_catalog.now()` → `now()`, `1 + 1` → `(1 + 1)`) is not a change while
  editing the file or the database still is. See D007.
- **Destructive gate:** DROP TABLE, DROP COLUMN, DROP INDEX, DROP CONSTRAINT,
  non-widening type changes, and `nullable → NOT NULL` on a column that already
  holds NULLs require an interactive `yes`. Non-interactive runs refuse instead
  of assuming consent.
- **Unexpressible objects block:** check/exclusion constraints, expression,
  partial, covering and non-btree indexes, and foreign keys from another schema
  are read from `pg_constraint`/`pg_index` and never matched against a declared
  object or removed; a statement that would take one down is refused with the
  object names before anything is written.
- **A second unique index is not built:** a declared unique index whose columns a
  live unique constraint already covers is considered satisfied, and that
  constraint is not dropped for being "undeclared" under a different spelling.
- **Pending migrations are re-validated, not trusted:** the dependency check
  runs again on every replay, against the catalog as it is now, which counts
  every attached constraint and index — including ones the schema file does
  express — because the recorded drop predates them. A recorded destructive
  change is confirmed again, and a non-interactive replay refuses.
- **One run at a time:** a session advisory lock guards planning and execution;
  a competing run waits up to 30s and then fails with a clear message rather
  than interleaving.
- **Not in scope:** down/rollback migrations, seeds/data migrations,
  non-interactive/CI mode, check constraints, schema-JSON ergonomics
  (Stage 7.6), other dialects (Stage 6).

Implementation notes as built:

- Types compare on PostgreSQL type names (`int4`, `int8`, `text`, `bool`,
  `uuid`, `timestamp`), and a modifier is part of the type (`varchar(100)`,
  `timestamp(3)`), so a different spelling the catalog normalizes away (e.g.
  `int4` vs `integer`) is not a change while a different modifier is.
- Adding a `NOT NULL` column to an already-populated table without a `default`
  fails; the transaction rolls back and the journal entry is retried after the
  schema is corrected.
- Managed tables are those in the resolved schema; the `chetah` schema is never
  diffed. Tables in other schemas are untouched.
- A table-level object that PostgreSQL reports without a column list blocks any
  column drop on that table: the schema file cannot say whether it references
  the column, so the refusal is deliberately conservative.

## Verification

Disposable PostgreSQL fixtures, the same pattern as Stage 2 acceptance: each run
owns a throwaway database (internal state lives in a fixed `chetah` schema, so
schemas inside one database would share it), never touching shared tables;
configured runs (a set `CHETAH_CONNECTION_STRING`) are distinguished from
skipped ones.

`tests/Chetah.Db.Tests` covers:

- **Read-only diff** — `--check` on a database that has never been migrated
  writes no file, creates no schema, no internal table and no DDL; it names the
  `"$id"` members it would assign, exits 1 when a migration is pending and 0 when
  in sync; a refused identity leaves the file byte-identical; `diff` without
  `--check` still records the ids and no DDL.
- **Cross-environment identity** — one schema file applied to two databases
  produces the same identity set; a `"$id"` that contradicts the registry is
  refused with both ids named; reusing a dropped id is refused; a file from
  before identities adopts the recorded ones.
- **Defaults** — a PostgreSQL-rewritten default (`1 + 1`, `pg_catalog.now()`)
  converges to one migration and stays in sync; a default changed by hand in the
  database is corrected and then settles; the offline diff reacts to a changed
  declaration or to drift and ignores a rewrite.
- **Types** — `varchar(100)`/`timestamp(3)` are introspected and are a no-op on
  re-apply; a larger modifier is not gated, a smaller one and a dropped modifier
  are; the parser rejects modifiers PostgreSQL would not accept.
- **Composite keys and actions** — composite UNIQUE and composite FOREIGN KEY
  with `ON DELETE cascade`/`ON UPDATE restrict` are created, are a no-op on
  re-apply, and are replaced when an action changes; the parser rejects a
  missing `referenceColumns`, an arity mismatch, an unknown action and an
  undeclared column.
- **Unsupported objects** — an expression index and a partial index survive,
  are never proposed for removal and block the column drop; a foreign key from
  another schema blocks the table drop with its name; a declared unique index
  over a live composite unique constraint neither duplicates the index nor drops
  the constraint.
- **Offline diff** — fresh-create column order, add/drop column, drop table,
  widening free / narrowing gated, `SET NOT NULL` gated only when the column
  holds NULLs, `DROP NOT NULL` free, no-op on a matching catalog, primary key
  and unique constraints inline in `CREATE TABLE`, foreign keys and indexes
  added for an existing table, definition matching (a hand-named equivalent is
  left alone), default/identity changes, a default that only differs by casts,
  and drops blocked only by objects the schema cannot express.
- **Identities** — `"$id"` members are written without disturbing comments or
  layout and a second run writes nothing; a rename keeps the id and the values;
  a table rename keeps its rows and its identity; an unqualified replacement is
  refused with the exact `--drop-add` resolution; a deleted id is tombstoned and
  never reissued; a schema file without ids adopts the recorded ones; duplicate
  and non-positive ids are rejected by the parser.
- **Record layout** — SQL file, snapshot, journal entry and hash; hand-edit
  changes the hash; baseline record has no statements; same stamp and slug get
  distinct ids.
- **Live apply** — fresh database created via `apply`, journal/snapshot/SQL
  matching what ran, `"chetah"."migrations"` rows with the right hash and name,
  `diff` reporting drift without writing, declined destructive gate writing
  nothing, failed statement rolling back with the journal entry present and no
  migration row (rerun succeeds after the fix), hand-edited SQL refused, an
  existing database recorded as a baseline, and a legacy `chetah_migrations`
  table moved into `chetah` with its history (including a table stranded under
  the old name by an interrupted move).
- **Live constraints** — a table created with a primary key, identity column,
  single-column unique, foreign key, default and unique index; a second apply
  finds nothing; a later revision replaces the key with a composite one, drops
  the unique and foreign key, swaps the index and changes the default.
- **Live dependency handling** — a column drop takes its expressible index and
  unique constraint with it through explicit, ordered drops; a check constraint
  on the column or table still refuses the drop.
- **Live replay validation** — a pending destructive entry replayed after an
  index was added to its target column is refused before confirmation; a pending
  destructive entry is refused when the confirmation gate says no and applied
  when it says yes; a pending entry without metadata is refused unless
  confirmed; a safe pending entry is replayed without consulting the gate.
- **Live concurrency** — two runners with separate checkouts race on one
  database: both succeed, the migration row exists once, and only one checkout
  records the create.

The CLI itself was exercised against a live database for the same flow,
including identity allocation, a column rename through the file, the ambiguous
replacement refusal, `--drop-add` resolving it, and constraint creation.

## Decisions log handoff

Transferred to [`decisions.md`](../project/decisions.md) D005 when the code landed.
