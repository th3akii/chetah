# Schema tooling and migrations — plan (Stage 7)

> **Status:** Approved plan, not yet implemented.
> Approved by the author 2026-11-05; execution deliberately deferred to a later
> session. Companion documents: [`ROADMAP.md`](ROADMAP.md) (Stage 7),
> [`DECISIONS.md`](DECISIONS.md), [`SPEC.md`](SPEC.md).

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
   and statement count. `apply` verifies each file's hash against the journal
   before executing, so hand-edits cannot silently diverge from what was
   recorded. Snapshots exist for auditability; diffing always uses the catalog.

## Commands

- `diff` — prints the DDL the catalog comparison produces, flags destructive
  items, writes nothing.
- `apply` — runs the destructive confirmation gate, writes the record files,
  executes the migration in one transaction, records it in
  `myorm_migrations`.

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
- **Foreign objects are left alone:** constraints, indexes, or other objects
  the schema file does not express are reported and untouched, never dropped.
- **Not in scope:** down/rollback migrations, seeds/data migrations,
  non-interactive/CI mode, indexes/constraints (Stage 7.5), schema-JSON
  ergonomics (Stage 7.6), other dialects (Stage 6), concurrent applies.

## Verification approach (for implementing session)

Disposable PostgreSQL fixtures, same pattern as Stage 2 acceptance: unique
schema per run, never touching shared tables, configured runs distinguished
from skipped ones.

Scenarios: fresh database created via `apply`; journal/snapshot/.sql contents
match what ran; `myorm_migrations` rows appear with the right hash/name;
column add after interactive confirm; destructive gate honored (decline → no
writes); rename reported as drop+add under the same gate; drift detected by
`diff` before any action; failed apply leaves database unchanged with journal
present and migration row absent, then a rerun succeeds; hash-mismatch
refusal after a hand edit.

## Decisions log handoff

The locked decisions above transfer into `DECISIONS.md` as a new entry when
code lands (the log records decisions as they are made, not in advance).
