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
composite UNIQUE / FOREIGN KEY and `ON DELETE` / `ON UPDATE`; expression,
partial, covering (`INCLUDE`) and non-btree indexes, non-default opclasses and
non-default sort orders protected instead of matched or dropped; foreign keys
from another schema detected; duplicate unique index over a live constraint;
statement-level failure reporting; renaming a table or column carries its
constraints and indexes with it (they were discarded, so a rename re-added all
of them and failed on the duplicate); a baseline no longer records the schema
file's declarations as applied, so a declined or blocked migration cannot
suppress the default it was going to set.

## Open issues by severity

| # | Severity | Area | Issue |
|---|---|---|---|
| 1 | Medium | Types | Types outside the vocabulary (`jsonb`, `numeric`, `timestamptz`, enums, domains) cannot be declared; an adopted column of such a type is reported as a gated type change |
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
| 17 | Low | Scope | No down/rollback migrations (**by design**) |
| 18 | Low | Scope | No RLS, no INSERT/UPDATE/DELETE/UPSERT, no other dialect (**by design**) |

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

**Impact:** an adopted database with `jsonb`/`numeric`/enum columns can only be
managed by converting those columns or by leaving the table out of the file.

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

### 17–18. Out of scope by design

Down/rollback migrations, seeds/data migrations, an automatic CI apply mode,
RLS, INSERT/UPDATE/DELETE/UPSERT, and any database other than PostgreSQL.
