# Constraint metadata — implementation plan (Stage 7.5)

> **Status:** Approved direction, not implemented. Agreed 2026-11-05 with the
> author: "start small and work towards full", delivered in phases below.
> Companion documents: [`ROADMAP.md`](ROADMAP.md) (Stage 7.5),
> [`SCHEMA_TOOLING.md`](SCHEMA_TOOLING.md), [`TYPED_MODELS.md`](TYPED_MODELS.md),
> [`DECISIONS.md`](DECISIONS.md).

## Why

`myorm-schema.json` describes columns only. Nothing declares which column
identifies a row, and nothing declares that `orders.user_id` points at
`users.id`. Two consequences, both already visible:

- `myorm-db apply` creates tables with **no constraints at all** — no primary
  key, no foreign key, no unique, no check. Integrity is whatever the caller
  happens to do.
- The migration tool refuses to drop any column or table that carries a
  constraint or index (a deliberate safety rule, D005). Because the schema file
  cannot express those objects, a hand-added key makes a column permanently
  undroppable until it is removed by hand.

Stage 3 (writes) also needs a key notion before `Insert`/`Update` can be safe,
which is the gating question this plan answers.

## Delivery policy

Start small, add breadth only when a consumer needs it. Each phase ships
end-to-end (schema → generator → migrations → tests → docs) before the next
starts. Nothing here is built in advance of a phase.

## Schema additions (final target shape)

```json
{
  "tables": {
    "users": {
      "primaryKey": ["id"],
      "columns": {
        "id": { "type": "integer", "nullable": false },
        "email": { "type": "text", "nullable": false }
      },
      "unique": [["email"]]
    },
    "orders": {
      "columns": {
        "id": { "type": "integer", "nullable": false },
        "user_id": { "type": "integer", "nullable": false },
        "amount": { "type": "integer", "nullable": false }
      },
      "foreignKeys": [
        {
          "columns": ["user_id"],
          "references": { "table": "users", "columns": ["id"] },
          "onDelete": "cascade"
        }
      ],
      "checks": [{ "name": "amount_positive", "expression": "amount > 0" }]
    }
  }
}
```

Rules to enforce in the parser, all reported as schema errors naming the table
and column:

- Every referenced column exists in its table; `primaryKey`, `unique` and
  `foreignKeys[].columns` name declared columns, never duplicates.
- `primaryKey` columns must be `"nullable": false` (PostgreSQL would force it
  silently otherwise).
- `foreignKeys[].columns` and `references.columns` have equal, non-zero length;
  the referenced table must be declared in the same schema file.
- `checks[].name` is a plain identifier; the expression is passed through
  verbatim (it is DDL, like the schema file itself — not user data).
- Constraint names are **derived deterministically** so diffs stay stable:
  `<table>_pkey`, `<table>_<columns>_fkey`, `<table>_<columns>_key`,
  `<table>_<name>_check`, `<table>_<columns>_idx`. Never user-chosen, except
  for checks (where the name is part of the expression's meaning).

## Generated metadata

Each table accessor gains a key/constraint record, emitted only when declared:

```csharp
public static readonly MyOrm.Key PrimaryKey = new("users_pkey", Id.Name);
public static readonly MyOrm.ForeignKey OrdersUserFk = new(
    "orders_user_id_fkey", "users", "id", "user_id");
```

`MyOrm.Table` and `Column<T>` stay unchanged in these phases: no runtime
feature consumes keys yet, and D001's registry contract should not grow ahead
of `Insert`. Wiring `Table`/`Insert` to the key happens in Stage 3, when a
caller actually needs it. (Deliberate non-goal here: relationship navigation,
lazy loading, `Include`.)

## Migration and diff changes

- `CREATE TABLE` stops being the only creation path for keys. Constraints are
  emitted as **separate statements after all `CREATE TABLE`s**
  (`ALTER TABLE … ADD CONSTRAINT …`), because inline `REFERENCES` cannot
  express a cycle and would make table order matter. Table creation keeps
  column order; constraint statements follow the schema file order.
- New operations: `add_constraint`, `drop_constraint`, with the same journal
  metadata (`kind`, `table`, `column`, `destructive`, `statement`) the replay
  validator already reads. Dropping a key, unique, check or FK removes an
  invariant → destructive, gated.
- `SchemaDiff` gains a comparison over `pg_constraint` (already read for the
  dependency rule): ours vs. declared, by derived name and definition.
- **Dependency rule refinement (a hard prerequisite):** today *any* constraint
  or index blocks a `DROP COLUMN`/`DROP TABLE`. After this stage, objects the
  schema file declares are "ours": they are dropped by the same plan, in the
  right order, and do not block. Foreign objects (hand-added, or kinds not yet
  expressible) keep blocking. This is what makes a declared key stop being a
  permanent drop veto.
- Adding a constraint to populated data can fail (duplicates, NULLs, orphaned
  rows) without being destructive. Such a statement is not gated up front; it
  fails inside the transaction, rolls back, stays pending, and the existing
  replay validation applies. Failure messages must name the constraint.

## Phases

Every phase is a vertical slice: schema grammar → generation → migration DDL →
live verification → doc update (`SCHEMA_TOOLING.md`, `TYPED_MODELS.md`,
`DECISIONS.md` entry with the phase's decisions).

1. **7.5a — Primary keys.** `primaryKey` in the schema; generated `Key`;
   `CREATE TABLE` + `ADD/DROP CONSTRAINT … PRIMARY KEY`; dependency-rule
   refinement limited to primary keys.
2. **7.5b — Foreign keys.** `foreignKeys`; ordering after all tables; cycle
   handling comes free from separate statements; `ON DELETE`/`ON UPDATE` stay
   out unless a consumer needs them (default: PostgreSQL's `NO ACTION`).
3. **7.5c — Unique and check.** `unique` column groups; `checks` with
   pass-through expressions; violation of a check on existing rows behaves like
   a failed add (rollback, pending, retry).
4. **7.5d — Indexes and FK actions.** `indexes` (columns, optional unique) and
   `onDelete`/`onUpdate`; index diffing compares `pg_index` including its
   uniqueness, ignoring physical options (`fillfactor`, tablespaces).

## Verification per phase

Same fixture pattern as Stage 2/7: unique disposable schema per run, configured
runs distinguished from skipped ones. Each phase adds live cases for:

- fresh database created from the schema has exactly the declared constraints
  (`pg_constraint`/`pg_index` inspected, not just the generated SQL text);
- `diff` reports a hand-removed or hand-altered key as drift, and adopts it;
- dropping a declared key/column/table succeeds (object counts before/after),
  while a hand-added key still blocks;
- adding a key to dirty data fails, leaves the database unchanged, keeps the
  journal entry, and succeeds after the data is fixed;
- replay of a recorded `add_constraint`/`drop_constraint` re-validates against
  the current catalog and re-runs the destructive gate (existing rule, new
  kinds).

## Out of scope

Composite expressions beyond plain columns for indexes (functional indexes),
partial indexes, exclusion constraints, deferrable constraints, constraint
options (`NOT VALID`, `MATCH`), cross-schema references, and anything that
turns keys into runtime navigation. Naming collision with an existing database
object is reported as drift and refused, never renamed automatically.

## Open questions (decide per phase, not now)

- Whether the generated `Key`/`ForeignKey` records migrate into
  `MyOrm.Table` once `Insert` lands, or stay a separate registry.
- Whether the snapshot format should record constraints (it currently records
  columns only) — auditability argues yes, but nothing reads it.
- Whether an FK may point at a table outside the schema file once Stage 6
  introduces another dialect.
