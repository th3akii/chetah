# Writing a schema for Chetah

`chetah-schema.json` is the single source of truth for the shape of the database.
One file feeds two consumers:

```text
chetah-schema.json ──► Chetah.CodeGen ──► C# models and table bindings
                   └─► chetah-db diff / apply ──► SQL migrations
```

The file is JSON with comments allowed (`//` and `/* */`). Unknown properties are
rejected, so a typo fails loudly instead of being ignored. [`chetah.schema.json`](../../chetah.schema.json)
at the repository root is the machine-readable contract: point `"$schema"` at it and
VS Code gives completion, hover descriptions and structural validation with no extension.

Structural validation covers shape, required properties and enum values. References
between objects — does this table exist, is that column unique — are checked by the
semantic validator and reported with the path of the declaration that caused them.

> **Format version.** This is schema format v1. Files written for the older format must be
> converted; see [Converting an older file](#converting-an-older-file) and
> [`decisions.md`](../project/decisions.md) D008.

---

## Quick start

```jsonc
{
  "$schema": "./chetah.schema.json",
  "tables": {
    "users": {
      "model": "User",
      "columns": {
        "id": { "type": "integer", "primaryKey": true, "identity": true },
        "name": { "type": "text", "nullable": false },
        "age": { "type": "integer" }
      }
    }
  }
}
```

Three commands, from the repository root:

```sh
# 1. Look at what the database would change (also records missing "$id" members)
dotnet run --project src/Chetah.Db -- diff --schema chetah-schema.json

# 2. Apply it (records the migration, executes it in one transaction)
dotnet run --project src/Chetah.Db -- apply --schema chetah-schema.json

# 3. In CI: is anything pending, or is the history unsafe? 0 = no, 1 = yes
dotnet run --project src/Chetah.Db -- diff --check --schema chetah-schema.json
```

Regenerate the C# models after changing the file (the default `split` layout
writes `Generated/Tables/<Model>.g.cs` per table, `Generated/Enums/<EnumType>.g.cs`
per enum, and a shared registry; obsolete generated files are removed):

```sh
dotnet run --project src/Chetah.CodeGen -- \
  --schema chetah-schema.json --out Generated --namespace Chetah.Generated
```

`--layout single` keeps the previous one-file output for consumers that expect
it. `Generated/.chetah-codegen.json` records the files the generator owns; other
files in the directory are left alone. See
[`typed-models.md`](../reference/typed-models.md#step-2--run-the-generator) for the
full option list and regeneration guarantees.

A complete, migration-clean example lives in
[`examples/BasicExample/`](../../examples/BasicExample/); the university example in
[`examples/NewExample/`](../../examples/NewExample/) shows composite keys, composite
foreign keys, checks and indexes.

---

## Root

```json
{
  "$schema": "./chetah.schema.json",   // optional; editor contract and v1 marker
  "tables": { ... },                   // optional; at least one object across all collections
  "enums": { ... },                    // optional
  "sequences": { ... },                // optional
  "views": { ... },                    // optional
  "materializedViews": { ... }         // optional
}
```

Every collection is optional; the file must declare at least one object across
them. An empty schema is rejected rather than read as "drop everything".

`$schema` is ignored for validation but is not decorative: it also marks the file
as schema v1. A **string** default requires it, because a pre-v1 file used raw SQL
in that position and no pattern can tell `"1 + 1"` from a text value. Numbers,
booleans, `null` and `{ "sql": ... }` need no marker. Both tools read `$schema`;
editors use it for completion and structural validation.

PostgreSQL's `public` is the default namespace; any object may set `"schema"` to
another namespace. Only `public` is supported today: migrations refuse any other
namespace, and code generation refuses it for tables, because generated table
metadata is unqualified and would target whatever `search_path` resolves.

---

## Tables

```jsonc
"users": {
  "schema": "public",              // optional; the default
  "model": "User",                 // optional; default = PascalCase("users") = "Users"
  "$id": 2,                        // written by the tool; see "Identities"
  "columns": { ... },              // required, at least one
  "primaryKey": { ... },           // optional; composite or named key
  "unique": [ ... ],               // optional; table-level UNIQUE constraints
  "foreignKeys": [ ... ],          // optional; one array of objects
  "checks": [ ... ],               // optional; table-level CHECK constraints
  "indexes": [ ... ]               // optional; standalone indexes
}
```

| Rule | What happens |
|---|---|
| `columns` required, at least one column | rejected |
| Table names are unique | rejected |
| Table name must produce a valid C# accessor (`PascalCase + "Table"`) | rejected |
| `model` and accessor names are unique across tables | rejected (set an explicit `"model"`) |
| `model` must not be a C# keyword (`class`, `event`, `params`) | rejected |
| Table, sequence, view and materialized view names share PostgreSQL's relation namespace | duplicate names rejected |
| `"schema"` other than `public` | rejected by code generation (and by migrations) |

`model` also decides the generated class name, so set it when the table name is
plural: `"model": "User"` for `users`.

---

## Columns

```jsonc
"columns": {
  "id": {
    "type": "integer",
    "primaryKey": true,
    "identity": true
  },
  "username": {
    "type": "varchar(100)",
    "nullable": false,
    "unique": true
  },
  "status": {
    "type": "text",
    "default": "pending"
  },
  "created_at": {
    "type": "timestamptz",
    "default": { "sql": "now()" }
  }
}
```

| Property | Meaning |
|---|---|
| `type` | Required. Any PostgreSQL type expression (see [Types](#types)). |
| `nullable` | Omitted means nullable, PostgreSQL's default. `false` produces `NOT NULL`. A primary key or identity column is never nullable. |
| `default` | Literal scalar, or `{ "sql": "..." }` for an expression. See [Defaults](#defaults). |
| `identity` | `true`, or `{ "generation", "start", "increment" }`. Integer and bigint only. Always `NOT NULL`. |
| `generated` | `{ "expression": "...", "stored": true }`. |
| `primaryKey` | `true` for a single-column primary key. |
| `unique` | `true` for a single-column UNIQUE constraint. |
| `$id` | Tool-maintained identity. |

| Rule | What happens |
|---|---|
| Column names must be valid C# identifiers | rejected |
| Identifiers are ASCII only (`[A-Za-z_][A-Za-z0-9_]*`), as the JSON Schema contract states | rejected |
| Generated C# property names must be unique per table (`user_id` vs `userId`) | rejected |
| A property that would shadow something the generator emits — `Table`, `PropertyColumns`, `Map`, `MapAt`, the model name, or a record member such as `ToString` — is renamed with a `Column` suffix (`table` → `TableColumn`) | generated |
| `identity` requires `integer` or `bigint` | rejected |
| `identity` and `default` on one column | rejected |
| `generated` and `default`, or `generated` and `identity` | rejected |
| `"primaryKey": true` with `"nullable": true` | rejected |
| `"identity"` with `"nullable": true` | rejected |

The property name is PascalCase(`column`), so `created_at` becomes `CreatedAt`
and `users` in table `users` (model `Users`) becomes `UsersColumn`; every other
name is emitted unchanged. The same name is used by the column accessor, the
table's column list, the property→column map and the record parameter, so no
consumer sees the difference. See
[`typed-models.md`](../reference/typed-models.md#column-property-names).

### Defaults

One rule, everywhere: a JSON scalar is a literal, an object is an explicit SQL
expression.

```jsonc
"default": "pending"                        // 'pending'
"default": "it's"                           // 'it''s'  (quoted and escaped)
"default": 0                                // 0
"default": true                             // true
"default": null                             // NULL
"default": { "sql": "gen_random_uuid()" }   // gen_random_uuid()
```

Strings are never interpreted as SQL. If you mean `now()`, write
`{ "sql": "now()" }`; writing `"now()"` stores the text `now()`.

Because a pre-v1 file used raw SQL here, the parser cannot tell a text literal
from an old expression — `"1 + 1"` is the documented example that no pattern can
classify. Any **string** default therefore requires the file to declare
`"$schema"`, the schema-v1 opt-in. Numbers, booleans, `null` and
`{ "sql": ... }` are unambiguous and need no marker.

A literal is emitted with its JSON kind: strings as quoted SQL literals,
numbers and booleans verbatim, `null` as `NULL`. PostgreSQL then coerces it to the
column type, so `"default": "pending"` works for a text, enum or uuid column.

### Identity

```jsonc
"id": { "type": "integer", "identity": true }
```

is shorthand for PostgreSQL's documented default:

```jsonc
"id": {
  "type": "integer",
  "identity": { "generation": "BY DEFAULT", "start": 1, "increment": 1 }
}
```

`"generation": "ALWAYS"` is accepted and validated. The migration engine currently
executes only the shorthand (`BY DEFAULT`); `ALWAYS`, `start` and `increment` are
refused explicitly rather than approximated.

An identity column is `NOT NULL` in PostgreSQL, so the declaration implies it:
the generated model uses `int`/`long` (never `int?`), and an explicit
`"nullable": true` is rejected with the column's path, exactly like a nullable
primary key.

### Generated columns

```jsonc
"total": {
  "type": "integer",
  "generated": { "expression": "price * quantity", "stored": true }
}
```

`stored` defaults to `true`. PostgreSQL 16 (this project's validation baseline)
supports stored generated columns only; `"stored": false` is rejected until the
baseline moves to PostgreSQL 18. Generated columns are validated and generated as
model properties, but cannot be created or altered by the migration engine yet.

---

## Types

`type` accepts any PostgreSQL type expression:

- built-in names and their aliases: `integer`/`int4`, `bigint`/`int8`,
  `boolean`/`bool`, `varchar`/`character varying`, `timestamp`, `timestamptz`,
  `numeric`/`decimal`, `jsonb`, `bytea`, …
- modifiers where PostgreSQL allows one: `varchar(100)`, `timestamp(3)`,
  `numeric(12, 2)`
- arrays: `text[]`, `integer[][]`
- schema-qualified names: `app.money`
- an enum declared in the same file: `user_status` or `app.user_status`

The parser canonicalizes built-in names (`integer` → `int4`) so the schema and the
live catalog compare equal.

| Declared `type` | Generated C# |
|---|---|
| `integer`, `int4` | `int` |
| `bigint`, `int8` | `long` |
| `smallint`, `int2` | `short` |
| `real`, `float4` | `float` |
| `double precision`, `float8` | `double` |
| `numeric`, `decimal` | `decimal` |
| `text`, `varchar(n)`, `char(n)` | `string` |
| `boolean`, `bool` | `bool` |
| `uuid` | `Guid` |
| `date` | `DateOnly` |
| `time` | `TimeOnly` |
| `timetz` | `DateTimeOffset` |
| `timestamp`, `timestamptz` | `DateTime` |
| `interval` | `TimeSpan` |
| `json`, `jsonb` | `string` |
| `bytea` | `byte[]` |
| an enum declared in this file | `string` |
| `T[]` | `T[]` (of a mapped `T`) |

A type with no CLR mapping — a domain, a composite, an unknown user-defined type —
is still a valid declaration for the schema and validator. Code generation refuses
it with the column named, because inventing a CLR type would silently read the
wrong data.

The migration engine executes a smaller set: `int4`, `int8`, `text`, `varchar`,
`bool`, `uuid`, `timestamp`, with modifiers. Anything else is refused by
`diff`/`apply` with its path (see [What the migration engine executes](#what-the-migration-engine-executes)).

---

## Primary keys

Single column:

```jsonc
"id": { "type": "integer", "primaryKey": true }
```

Composite or explicitly named:

```jsonc
"primaryKey": {
  "name": "pk_enrollments",
  "columns": ["student_id", "course_id"]
}
```

`name` defaults to `<table>_pkey`. Rules, all rejected when violated:

- one primary key per table;
- column-level and table-level declarations cannot coexist;
- multiple column-level `primaryKey: true` declarations are invalid;
- primary key columns cannot be nullable;
- every listed column must be declared.

---

## Unique constraints

Single column:

```jsonc
"email": { "type": "text", "unique": true }
```

Table level, one object per key:

```jsonc
"unique": [
  { "name": "uq_student_course", "columns": ["student_id", "course_id"] },
  { "columns": ["email", "role"], "nullsNotDistinct": true }
]
```

`name` defaults to `<table>_<columns joined by _>_key`. `nullsNotDistinct` requires
PostgreSQL 15 and is validated; the migration engine refuses it rather than
pretending.

Declaring the same key twice (column flag and list entry, or two identical entries)
is rejected. A unique constraint is not a unique index: the constraint is part of
the table's definition, an index is a physical access path ([Indexes](#indexes)).

---

## Foreign keys

One structure, always an array of objects:

```jsonc
"foreignKeys": [
  {
    "columns": ["department_id"],
    "references": { "table": "departments", "columns": ["id"] },
    "onDelete": "SET NULL",
    "onUpdate": "CASCADE"
  }
]
```

Composite keys use exactly the same shape:

```jsonc
"foreignKeys": [
  {
    "columns": ["course_id", "semester_id"],
    "references": { "table": "course_offerings", "columns": ["course_id", "semester_id"] },
    "onDelete": "CASCADE"
  }
]
```

Optional properties: `name`, `onDelete`, `onUpdate`, `match`
(`SIMPLE`/`FULL`/`PARTIAL`), `deferrable`, `initiallyDeferred`.

| Rule | What happens |
|---|---|
| `columns` must be declared in this table | rejected |
| `references.table` is `table` or `schema.table`; `references.columns` is required | rejected |
| Local and referenced counts must match | rejected |
| An unqualified target must be declared in this file | rejected |
| Referenced columns must be unique there (primary key, unique constraint or unique index) | rejected |
| Column types must be compatible (same type, or integer/bigint) | rejected |
| `initiallyDeferred` without `deferrable` | rejected |
| `match: "PARTIAL"` | rejected (PostgreSQL accepts the syntax but does not implement it) |

`onDelete`/`onUpdate` are uppercase SQL actions: `NO ACTION` (the default),
`RESTRICT`, `CASCADE`, `SET NULL`, `SET DEFAULT`. Actions are part of the
definition: changing one drops and re-creates the constraint.

A qualified reference such as `other_schema.roles` describes a table outside this
file; the server validates it at apply time.

---

## Check constraints

```jsonc
"checks": [
  { "name": "chk_valid_grade", "expression": "grade BETWEEN 5 AND 10" }
]
```

`name` and `expression` are required; the expression is emitted verbatim. The
parser does not parse SQL. Check constraints are validated, but the
migration engine cannot create or remove them yet — a schema that declares one is
refused by `diff`/`apply` with the constraint named. Existing checks in the
database stay protected from removal.

---

## Indexes

```jsonc
"indexes": [
  {
    "name": "idx_students_name",
    "keys": [{ "column": "last_name" }, { "column": "first_name" }]
  },
  {
    "name": "idx_students_active",
    "keys": [{ "column": "created_at" }],
    "where": "deleted_at IS NULL"
  },
  {
    "name": "idx_students_email_lower",
    "keys": [{ "expression": "lower(email)" }],
    "method": "btree"
  }
]
```

`keys` is the single ordered representation, and mixed column/expression keys are
allowed. Each key is an object with exactly one of `column` or `expression`, and
optionally `direction` (`asc`/`desc`), `nulls` (`first`/`last`) and `opclass`.

Optional index properties: `unique`, `method` (default `btree`), `where`,
`include`, `nullsNotDistinct`.

| Rule | What happens |
|---|---|
| `name` required, a plain identifier, unique across the schema and never the name of a constraint | rejected |
| `keys` non-empty; column keys must name declared columns | rejected |
| `include` entries must be declared columns, listed once | rejected |
| `direction`/`nulls`/`method`/`opclass` validated against PostgreSQL's vocabulary | rejected |

Indexes are matched by **definition** (keys + uniqueness), not by name, so a
hand-named equivalent is left alone. Renaming an index in the file does not rename
it in the database. The migration engine executes plain btree column indexes only;
expressions, ordering, operator classes, other access methods, `where`, `include`
and `nullsNotDistinct` are validated and refused explicitly.

Existing expression, partial and covering indexes in the database are detected and
protected from removal.

---

## Enums

A label array, or an object when the type needs a namespace:

```jsonc
"enums": {
  "user_status": ["active", "inactive", "banned"],
  "kind": { "values": ["a", "b"], "schema": "app" }
}
```

Labels must be non-empty and unique. A column whose `type` matches a declared enum
generates a `string` property and is validated as a reference. Each enum also gets
a generated label-list type (`Enums/UserStatus.g.cs` for `user_status` in the default layout) exposing
its labels as `Values`. Enum DDL is not
executed yet: a schema that declares one is refused by `diff`/`apply` with the enum
named, never silently ignored.

## Sequences

```jsonc
"sequences": {
  "invoice_numbers": { "start": 1000, "increment": 1 }
}
```

Optional: `schema`, `start`, `increment`, `minValue`, `maxValue`, `cycle`, `cache`.
`cache` must be positive. Sequences are validated; creation is not executed yet.

## Views and materialized views

```jsonc
"views": {
  "active_students": { "query": "SELECT * FROM students WHERE active = true" }
},
"materializedViews": {
  "student_counts": { "query": "SELECT count(*) FROM students", "withData": true }
}
```

Both take a required `query` and optional `schema`; a materialized view also takes
`withData` (default `true`). Queries are emitted verbatim — Chetah does not parse
SQL. Views and materialized views are validated; creation is not executed yet.

---

## Identities and renames

Every table and column carries a tool-maintained `"$id"`. It is the only reliable
signal that separates a rename (same id, new name) from a drop plus an add (the
value would be lost). Keep the id when renaming:

```jsonc
"users": {
  "$id": 3,
  "columns": {
    "id": { "$id": 1, "type": "integer" },
    "display_name": { "$id": 7, "type": "text" }   // renamed from "name"
  }
}
```

- `diff` records missing `"$id"` members; you never assign them. A declaration
  without an `"$id"` adopts the id the database holds for that name, unless
  another declaration in the same scope already carries it: renaming an object
  and declaring a new one under the old name gives the new object a fresh id.
- Ids are never reused; a dropped object's id stays tombstoned in the database.
- A brand-new id next to a tracked object that vanished is refused, not guessed:
  for a real replacement run with `--drop-add users.display_name`, which still
  passes the destructive gate.
- Ids are scoped: table ids are unique among tables, column ids are numbered per
  table, so branches touching different tables merge without id conflicts.

Enums, sequences and views do not carry ids yet: nothing migrates them, so no
identity is needed.

---

## Commands

| Command | What it does |
|---|---|
| `chetah-db diff` | Prints the DDL the catalog comparison produced. Writes no DDL, records missing ids. |
| `chetah-db diff --check` | Strict read-only: no file write, no DDL, no lock. Exit 1 when a migration is pending. |
| `chetah-db apply` | Replays pending migrations, then generates and applies new ones. A failed migration is offered as Retry, Repair or Cancel. |
| `chetah-db repair` | Replaces the single pending migration with a fresh plan from the current schema and applies it. |
| `chetah-db apply --drop-add <object>` | Declares an object new so a vanished one becomes a real drop plus add. |

Destructive statements require an interactive `yes`; non-interactive runs refuse.
Migrations, recovery and repair are described in
[`schema-tooling.md`](../reference/schema-tooling.md) and
[`migration-limitations.md`](../notes/migration-limitations.md).

---

## What the migration engine executes

The format describes more than the engine can apply. That gap is always reported,
never silently ignored. `diff`, `apply` and `repair` refuse the whole run when the
file contains any of:

- a non-`public` table `schema`;
- a column type outside `int4`, `int8`, `text`, `varchar`, `bool`, `uuid`,
  `timestamp` (modifiers allowed) — arrays, enums, domains, `numeric`,
  `timestamptz`, `jsonb`, … included;
- generated columns;
- identity options beyond `"identity": true`;
- check constraints;
- `nullsNotDistinct` on a unique constraint;
- index expressions, ordering, operator classes, non-btree methods, `where`,
  `include`, `nullsNotDistinct`;
- any enum, sequence, view or materialized view.

The refusal names every offending declaration and its path. Everything else —
tables, columns, literal and SQL defaults, nullability, primary keys, unique
constraints, foreign keys (including composite keys, actions, `match` and
deferrability) and plain indexes — is executed and compared against the live
catalog.

---

## Converting an older file

The format was redesigned before v1 shipped. Convert once, deliberately; the tool
does not reinterpret old files. Old shapes (`primaryKey` arrays, object-keyed
`indexes`/`foreignKeys`, column `references`) are rejected with a conversion
message, and a string default requires `"$schema"` so a raw-SQL default cannot
change meaning silently.

| Old | New |
|---|---|
| `"primaryKey": ["a", "b"]` | `"primaryKey": { "columns": ["a", "b"] }`, or `"primaryKey": true` on a single column |
| `"unique": ["email", ["a", "b"]]` | `"unique": [{ "columns": ["a", "b"] }]`; keep `"unique": true` on a single column |
| `"indexes": { "name": { "columns": [...] } }` | `"indexes": [{ "name": "name", "keys": [{ "column": "..." }] }]` |
| `"foreignKeys": { "name": { "columns", "references": "t", "referenceColumns": [...] } }` | `"foreignKeys": [{ "name", "columns", "references": { "table": "t", "columns": [...] } }]` |
| column `"references": "t.c"` | a table-level `foreignKeys` entry |
| `"default": "now()"` | `"default": { "sql": "now()" }` |
| `"default": "'pending'"` | `"default": "pending"` |
| `"default": "true"` / `"5"` | `"default": true` / `"default": 5` |
| referential actions `"cascade"` | `"CASCADE"` (case-sensitive, uppercase) |
| table `"checks"` / new objects | read the sections above |

Constraint names that used to be implicit keep their generated names, so an
existing database does not see churn. Only the default booleans changed spelling:
the tool emits `true`/`false`, which is what the catalog stores.

---

## Related reading

- [Schema tooling and migrations](../reference/schema-tooling.md)
- [Generated typed models](../reference/typed-models.md)
- [Migration limitations](../notes/migration-limitations.md)
- [Decision log](../project/decisions.md) — D002, D005–D008
- [Roadmap](../project/roadmap.md)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               