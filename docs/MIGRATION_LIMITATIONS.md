# Migration tooling — open issues

Audited 2026-11-06 against a live PostgreSQL 17, after the stabilisation pass.
Everything listed here was reproduced, not inferred. Behaviour that is
deliberate (out of scope by task) is marked **by design**; the rest is work that
still needs doing.

Companion documents: [`SCHEMA_TOOLING.md`](SCHEMA_TOOLING.md) (how it works),
[`DECISIONS.md`](DECISIONS.md) D005–D007 (why), [`ROADMAP.md`](ROADMAP.md).

## What is already fixed

Do not re-report these: read-only `diff --check`; identity resolution before any
write; `diff` on a never-migrated database; default convergence
(`1 + 1`, `pg_catalog.now()`); verbatim default emission (`'a  b'`, `::date`);
cast-only default edits; type modifiers (`varchar(100)`, `timestamp(3)`);
composite UNIQUE / FOREIGN KEY and `ON DELETE` / `ON UPDATE` (including
`SET NULL` / `SET DEFAULT` on both directions, removal, and renames of the
tables or columns they cover); expression, partial, covering (`INCLUDE`) and
non-btree indexes, non-default opclasses and non-default sort orders protected
instead of matched or dropped; foreign keys from another schema detected;
a declared unique index is not duplicated on top of a live unique constraint
(the skip is silent — see item 9); statement-level failure reporting;
renaming a table or column carries its constraints and indexes with it; a
baseline no longer records the schema file's declarations as applied.

Also fixed by the ordering pass:

- drops are ordered by dependency, not by name: a foreign key before the key it
  references (composite keys included, and the referenced columns are matched in
  any order), a referencing table before the table it references, and mutual
  references resolved by dropping the foreign keys explicitly — never `CASCADE`.
  A foreign key that lives on a table being dropped is dropped before the
  surviving table's key or column changes. A key that a declared foreign key
  still relies on cannot be dropped (the file would contradict itself), so that
  drop is refused with the constraint named — as is a declared foreign key onto
  a table being dropped.
- constraints are dropped before indexes, and indexes are created before foreign
  keys: a unique index can be what a foreign key relies on, so it must outlive
  the drop and exist before the add. An index that a foreign key references is
  now visible to the diff (a foreign key records the referenced index in
  `conindid`, which used to hide it as constraint-owned and re-create it). An
  index whose name a new constraint takes is dropped before that constraint is
  added, because both live in one namespace.
- duplicate constraint declarations are rejected: a column `"unique": true`
  repeated as a table `"unique"` entry, and a declared foreign-key name that
  collides with a generated primary-key, unique or reference constraint name.
- divergent migration histories are refused and reported with the migration ids
  (edits also name the two conflicting hashes): a journal edited after a
  migration ran, a SQL file that no longer matches what ran, or a database that
  has migrations this checkout does not have while the checkout has migrations
  the database has not run. A database with migrations this checkout does not
  have blocks *generating* one too, even when this checkout has nothing pending:
  that would graft a new migration onto a history it cannot see. A no-op
  inspection is still allowed (it writes nothing), an empty journal is adoption
  rather than divergence, and nothing is ever rewritten or merged automatically.
  `--check` reports it and exits non-zero. Only the real adopted-database entry
  is exempt — a migration whose slug merely ends in `baseline` stays visible.

## Open issues by severity

| # | Severity | Area | Issue |
|---|---|---|---|
| 1 | Medium | Types | Types outside the vocabulary (`jsonb`, `numeric`, `timestamptz`, enums, domains) cannot be declared; an adopted column of such a type is reported as a gated type change, and omitting the table from the file means `DROP TABLE` |
| 2 | Medium | Constraints | CHECK and EXCLUSION constraints cannot be declared (their dependent drops are refused) |
| 3 | Medium | Indexes | Index features cannot be *declared* (expression, partial, `INCLUDE`, non-btree, opclass, sort order — all detected and protected); `COLLATE`, `NULLS NOT DISTINCT` and storage options are not even detected |
| 4 | Medium | Operations | `CREATE INDEX` is not `CONCURRENTLY`: a migration holds `ACCESS EXCLUSIVE` on the table |
| 5 | Low | Defaults | `default` is not validated: a `;` produces a recorded statement that fails at apply with a raw syntax error |
| 6 | Low | Defaults | A hand edit that only changes casts *in the database* is invisible (the applied side is compared normalized) |
| 7 | Low | Defaults | Records written by an older build store the normalized declaration, so a cast edit made before upgrading is invisible until the default changes again |
| 8 | Low | Types | `varchar(n)` → `varchar` (unlimited) is gated although it is widening; so is `text` → `varchar(n)` |
| 9 | Low | Constraints | A declared unique index satisfied by a live unique constraint is skipped silently (no line in `diff` output) |
| 10 | Low | Constraints | `DEFERRABLE`, `INITIALLY DEFERRED`, `MATCH`, `NOT VALID` are not expressible; constraint renames are drop+create |
| 11 | Low | Identity | No `adopt`/renumber command: an id conflict is fixed by editing the schema file |
| 12 | Low | Identity | Ids are per file, not coordinated across branches; two independently built databases can hold different id sets for the same logical schema |
| 13 | Low | CLI | `diff` (without `--check`) exits 0 even when a plan is pending or blocked |
| 14 | Low | CLI | `--check` takes no lock, so its several catalog queries can straddle a concurrent commit |
| 15 | Low | CLI | A migration longer than the 30 s lock wait makes concurrent runs fail (retry manually) |
| 16 | Low | History | Journals are per checkout; only `"chetah"."migrations"` says what ran on the database |
| 17 | Low | History | Divergence is detected only when both sides have something the other lacks; a checkout with an *empty* journal (deleted migrations directory) against a database with history is treated as a fresh checkout and allowed to append |
| 18 | Low | Dependencies | Objects that are neither constraints nor indexes are not read: views on a dropped table/column make PostgreSQL refuse (safe rollback); triggers and policies **attached to a dropped table are removed silently** (the gate lists the table only); a policy on a dropped column is refused; a trigger on a dropped column is left broken; function bodies break at runtime |
| 19 | Low | History | A replayed migration executes the recorded statement order verbatim (history is never re-planned), so an order that was valid when written and is not valid now fails at apply |
| 20 | Low | History | Two histories that applied the same ids in a different order are not detected; the journal records order, the database records `id` values |
| 21 | Low | Constraints | Constraint and index *names* in the schema file are used when an object is created; renaming one where an equivalent definition already exists does not rename the database object, so a rename is a drop plus a create |
| 22 | Low | Scope | No down/rollback migrations (**by design**) |
| 23 | Low | Scope | No RLS, no INSERT/UPDATE/DELETE/UPSERT, no other dialect (**by design**) |

## Details

### 1. Types outside the vocabulary — Medium

The vocabulary is `integer`, `bigint`, `text`, `boolean`, `uuid`, `timestamp`,
`varchar` with modifiers. An adopted database that uses anything else cannot be
described truthfully.

**Reproduced:**

```sql
CREATE TABLE t (at timestamptz, payload jsonb);
```

with a schema file declaring `at` as `timestamp` produces:

```text
-- destructive: timestamptz → timestamp can lose data
ALTER TABLE "t" ALTER COLUMN "at" TYPE timestamp;
-- destructive: removes the column and its values
ALTER TABLE "t" DROP COLUMN "payload";
```

Nothing is silent — both are gated — but the user cannot express the truth.

**Impact:** a database that uses `jsonb`/`numeric`/enum columns cannot be
described truthfully, and **omitting such a table from the file does not leave it
alone** — the diff reads every live table in the schema and treats the omitted
one as `DROP TABLE` (destructive-gated, but it removes the table, its rows, and
every object owned by it). The safe options are to convert the column, or to
keep the tool off that database until the type exists in the vocabulary. Do not
"exclude" a table by deleting it from `chetah-schema.json`.

**Fix:** widen `SchemaParser.Types` and `Generator` mappings (`numeric`,
`jsonb`, `timestamptz`, `int2`, `date`, `time`) and map the unmatched ones to a
permissive CLR type, or add an escape hatch (`"type": "jsonb"` → `string`) that
the generator passes through.

### 2. CHECK / EXCLUSION constraints — Medium, by design

Read from `pg_constraint` as protected objects: never created, matched or
dropped, and a statement that would remove one is refused with its name.

**Fix (when wanted):** table-level `"checks": { name: expression }` plus catalog
read of `contype = 'c'` and `pg_get_constraintdef`.

### 3. Index features beyond a btree column list — Medium, partly by design

**Detected and protected** (never matched, removal refused): expression
indexes, partial indexes (`WHERE`), covering indexes (`INCLUDE`), non-btree
access methods, non-default operator classes (`text_pattern_ops`) and
non-default sort order (`DESC`, `NULLS FIRST`).

**Not even detected** — an index that differs only by one of these is read as a
plain btree index on its columns, so it can satisfy a declaration it does not
match (the declared index is never created) or be replaced by a declared index
of the same name:

- `COLLATE` on an indexed column (`CREATE INDEX ... (code COLLATE "C")`)
- `NULLS NOT DISTINCT` on a unique index (PostgreSQL 15+)
- storage parameters (`fillfactor`), `TABLESPACE`, `INCLUDE` ordering details
- opclass *families* that happen to be default but differ per type

Consequence is a missed index, not data loss: a declared index can be
considered present when the physical one is different. Fix: compare
`indcollation` against the column's collation, read `indnullsnotdistinct` on
PostgreSQL 15+ (version-gated), and treat any `pg_class.reloptions` /
`reltablespace` difference as unsupported.

### 4. `CREATE INDEX` is blocking — Medium, operational

Every statement runs inside the migration transaction, so `CREATE INDEX` takes
`ACCESS EXCLUSIVE` for the duration. On a large table in production this blocks
reads and writes.

**Fix:** emit `CREATE INDEX CONCURRENTLY` as a separately committed statement
(outside the transaction) with a recorded marker, or document a maintenance
window expectation.

### 5. An unvalidated `default` can break the recorded statement — Low

`default` accepts any non-empty string. A semicolon makes the recorded statement
a multi-statement batch:

```json
"default": "0; SELECT 1"
```

records `... DEFAULT 0; SELECT 1` and fails at apply with `42601: syntax error at
or near ";"`. It is safe (the transaction rolls back, the failing statement is
printed, the entry is retried) but the diagnostic talks about syntax, not about
the cause. The `--> statement-breakpoint` marker inside a default would split the
statement more subtly.

**Fix:** reject `;` outside quoted literals and the breakpoint marker in
`SchemaParser`, or make statement splitting quote-aware.

### 6. Cast-only edits made directly in the database — Low

The applied side is compared in normalized form (that is what makes
PostgreSQL's rewrites converge), so manually changing only the cast of a stored
default (`SET DEFAULT '2020-01-01 10:00'::date` → `'2020-01-01 10:00'`) is not
detected. A change to the *declaration* is detected verbatim.

**Fix:** store the raw catalog text beside the normalized one and compare the
raw pair too.

### 7. Records written before the verbatim-declaration change — Low, one-time

A registry populated by an older build stores the normalized declaration, so an
edit that only changed a cast made *before* upgrading stays invisible until the
default changes again. The next change re-records the verbatim form.

**Fix:** one-shot backfill (guess the raw form is not possible; a "re-record
defaults" command is the honest option).

### 8. Conservative widening for `varchar` — Low

`IsWidening` requires both sides to carry a modifier. `varchar(100)` → `varchar`
(no limit) is treated as destructive, and so is `text` → `varchar(n)`, although
neither loses data. Safe changes occasionally need the destructive prompt.

### 9. A declared unique index absorbed by a live constraint — Low

If a live unique constraint covers the columns of a declared unique index, the
index is not created (no duplicate) and the constraint is not dropped — but
`diff` prints nothing about it, so the declaration looks like it did nothing.

**Fix:** one line in the plan output: `-- skipped: unique index "x" is already
guaranteed by constraint "y"`.

### 10. Unsupported constraint options and renames — Low

`DEFERRABLE`, `INITIALLY DEFERRED`, `MATCH FULL/PARTIAL`, `NOT VALID` are not
read or written. A live constraint carrying them still matches a declared one by
kind/columns/target/actions and is left alone, so they are preserved, not
managed. Constraint and index *renames* are not operations: a renamed object is
dropped and re-created (gated).

### 11–12. Identity operations at scale — Low

There is no command to adopt a database's ids into a file, or to renumber. An id
conflict is resolved by editing `chetah-schema.json` (delete the `"$id"` to adopt
the tracked id, or set it to the tracked id). Ids are deliberately per file:
two databases built from independently-edited files can hold different id sets
for the same logical schema; the file is the record that travels.

### 13–16. Operational notes — Low

- `diff` returns 0 for a pending or blocked plan; use `diff --check` (1 when a
  migration is pending, 0 when in sync) in CI.
- `--check` deliberately takes no lock, so its catalog queries can interleave
  with a concurrent apply. Each query is consistent; the set is a snapshot of
  the moment.
- Concurrent runs wait up to 30 s for the advisory lock, then fail with a clear
  message. There is no queue or backoff.
- The journal lives in the checkout; only `"chetah"."migrations"` describes the
  database. Two checkouts sharing a database keep separate journals by design.

### 17. Divergence needs both sides to be non-empty — Low

The check compares the checkout's journal with `"chetah"."migrations"` and
refuses when *each* has migrations the other lacks, or when a migration that ran
no longer matches its journal entry or SQL file.

A checkout whose migrations directory was deleted therefore has an empty journal:
it is treated like a fresh clone sharing someone else's history (which must keep
working — two checkouts on one database is a normal setup), and it is allowed to
append a new migration to a history it cannot see. If that matters, restore the
migrations directory from version control instead of deleting it; the applied
state, the ids and the hashes are all that detect it, and an empty journal
carries none of them.

### 18. Dependencies that are not constraints or indexes — Low, but read the split

Objects the catalog reader does not look at behave in two different ways — the
difference matters for safety:

**Refused by PostgreSQL, rolled back (safe):** a view or materialised view that
depends on a dropped table or column. The statement fails with `2BP01` and the
migration rolls back; the diagnostic comes from the server rather than from the
plan.

**Removed implicitly, no error (destructive-gate risk):** a trigger or RLS policy
attached to a dropped table is owned by that table, so `DROP TABLE` removes it
silently. Verified against PostgreSQL 17:

```text
DROP TABLE t;   -- t has trigger trg: succeeds, trg is gone
DROP TABLE p;   -- p has RLS policy pol: succeeds, pol is gone
```

Those objects are invisible to the diff, so the confirmation lists only the
table drop, not what disappears with it. A policy that references a *dropped
column* is the exception: PostgreSQL refuses that `DROP COLUMN` with `2BP01`
(safe). A trigger that references a dropped column is not refused and is left
pointing at a column that no longer exists.

**Broken at runtime, not at migration time:** SQL function bodies that read a
dropped table are not dependencies, so the drop succeeds and the function fails
when it is next called.

Fix: read `pg_depend`/`pg_rewrite` and report views, plus triggers and policies
owned by objects the plan removes, the way check constraints and inbound foreign
keys are reported.

### 19–20. Recorded order and applied order — Low

A migration that is replayed executes its recorded statements in the order they
were written, because history is never re-planned. If the catalog changed shape
since (a dependency appeared that the recorded order does not satisfy), the
migration fails at apply and rolls back instead of being re-ordered.

Two histories that applied the same migration ids in a different order are not
detected: the journal has an order, the database has its own `id` sequence, and
neither is compared. Ids are timestamps, so this requires editing a journal.

### 21–22. Out of scope by design

Down/rollback migrations, seeds/data migrations, an automatic CI apply mode,
RLS, INSERT/UPDATE/DELETE/UPSERT, and any database other than PostgreSQL.
