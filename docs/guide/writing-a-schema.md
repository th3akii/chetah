# Writing a schema for Chetah

`chetah-schema.json` is the single source of truth for the shape of the database:
the same file generates the C# models and drives the migrations (a diff between
the file and the live catalog). One file, two consumers:

```text
chetah-schema.json ──► Chetah.CodeGen ──► C# models and table bindings
                   └─► chetah-db diff / apply ──► SQL migrations
```

The file is JSON with comments allowed (`//` and `/* */`), so annotate it freely.
Unknown properties are rejected — a typo fails loudly instead of being ignored.

This guide is written for both people and AI assistants. It is complete: the
"reference" sections below list every accepted property and every rule the
parser enforces. For why things work this way, see
[`decisions.md`](../project/decisions.md) D002 and D006.

---

## Quick start

The smallest useful file:

```jsonc
{
  "tables": {
    "users": {
      "model": "User",
      "columns": {
        "id": { "type": "integer", "nullable": false },
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

Regenerate the C# models after changing the file (overwrites only that one file):

```sh
dotnet run --project src/Chetah.CodeGen -- \
  --schema chetah-schema.json --out Generated --namespace Chetah.Generated
```

A working example lives in [`../../examples/BasicExample/`](../../examples/BasicExample/).

---

## A file with everything

Every property is shown below; the sections that follow explain each one.

```jsonc
{
  "tables": {
    "roles": {
      "model": "Role",
      // "id" is written by the tool. Never edit it by hand.
      "$id": 1,
      "primaryKey": ["id"],
      "columns": {
        "id": { "$id": 1, "type": "integer", "nullable": false, "identity": true },
        "code": { "$id": 2, "type": "varchar(32)", "nullable": false, "unique": true }
      }
    },

    "users": {
      "model": "User",
      "$id": 2,
      "primaryKey": ["tenant_id", "id"],          // composite primary key
      "unique": [["tenant_id", "email"]],          // composite UNIQUE constraint
      "indexes": {
        "users_state_idx": { "columns": ["state"] } // CREATE INDEX
      },
      "foreignKeys": {
        "users_role_fkey": {
          "columns": ["role_id"],
          "references": "roles",
          "referenceColumns": ["id"],
          "onDelete": "cascade",
          "onUpdate": "no action"
        }
      },
      "columns": {
        "tenant_id": { "$id": 1, "type": "integer", "nullable": false },
        "id": { "$id": 2, "type": "bigint", "nullable": false, "identity": true },
        "email": { "$id": 3, "type": "text", "nullable": false },
        "role_id": { "$id": 4, "type": "integer", "nullable": false },
        "state": { "$id": 5, "type": "text", "nullable": false, "default": "'new'" },
        "created_at": { "$id": 6, "type": "timestamp(3)", "nullable": false, "default": "now()" }
      }
    }
  }
}
```

---

## Tables

```jsonc
"users": {
  "model": "User",                 // optional; default = PascalCase("users") = "Users"
  "$id": 2,                        // written by the tool; see "Identities"
  "columns": { ... },              // required, at least one
  "primaryKey": ["id"],            // optional; the column list, in constraint order
  "unique": ["email", ["a", "b"]], // optional; one UNIQUE constraint per entry
  "indexes": { ... },              // optional; standalone indexes
  "foreignKeys": { ... }           // optional; foreign keys, including composite ones
}
```

| Rule | What happens |
|---|---|
| At least one table | the file is rejected |
| Table names are unique | rejected (`is defined more than once`) |
| `columns` required, at least one column | rejected |
| Table name must produce a valid C# accessor (`PascalCase + "Table"`) | rejected |
| `model` and accessor names must be unique across tables | rejected (set an explicit `"model"`) |
| Use letters, digits and underscores in table names | hyphens are PascalCased for C#, but underscores are the safe choice |

`model` also decides the generated class name, so set it when the table name is
plural: `"model": "User"` for `users`.

---

## Columns

`type` is required; the rest are optional. This complete example shows the
shapes a column can take:

```jsonc
{
  "tables": {
    "roles": {
      "primaryKey": ["id"],
      "columns": {
        "id": { "type": "integer", "nullable": false, "identity": true }
      }
    },
    "users": {
      "columns": {
        "email": { "type": "text", "nullable": false, "unique": true, "default": "'pending'" },
        "role_id": { "type": "integer", "references": "roles.id" }
      }
    }
  }
}
```

A foreign key needs a matching type on both sides — `text` cannot reference an
`integer` (PostgreSQL refuses at apply), and the referenced column must be
unique. An identity column is integer or bigint and cannot also have a `default`.

| Rule | What happens |
|---|---|
| Column names must be valid C# identifiers (letters, digits, `_`; not starting with a digit) | rejected |
| Generated C# property names must be unique per table (`user_id` vs `userId` collide) | rejected |
| `nullable` omitted means **nullable** | the column is created without `NOT NULL` |
| `identity` requires `integer` or `bigint` | rejected |
| `identity` and `default` on the same column | rejected |
| `default` is raw SQL | emitted verbatim |

---

## Types

| Declared `type` | PostgreSQL | Generated C# | Notes |
|---|---|---|---|
| `integer`, `int4` | `integer` | `int` | |
| `bigint`, `int8` | `bigint` | `long` | |
| `text` | `text` | `string` | |
| `varchar`, `character varying` | `varchar` | `string` | unlimited unless a length is given |
| `varchar(100)` | `character varying(100)` | `string` | length 1–10485760 |
| `boolean`, `bool` | `boolean` | `bool` | |
| `uuid` | `uuid` | `Guid` | |
| `timestamp` | `timestamp` | `DateTime` | |
| `timestamp(3)` | `timestamp(3)` | `DateTime` | precision 0–6 |

Types are compared with their modifier, so `timestamp(3)` and `timestamp` are
different. A larger modifier (`varchar(100)` → `varchar(200)`) is a widening and
applies without a prompt; a smaller one, a dropped modifier or a different base
type is destructive-gated.

Anything else (`jsonb`, `numeric`, `timestamptz`, `date`, `int2`, enums,
domains) cannot be declared yet. Do **not** "exclude" such a table by removing
it from the file: an omitted live table is planned as `DROP TABLE`. See
[`migration-limitations.md`](../notes/migration-limitations.md) item 1.

---

## Constraints

### Primary key

```jsonc
"primaryKey": ["tenant_id", "id"]     // composite; omit for no primary key
```

Generates `CONSTRAINT "users_pkey" PRIMARY KEY ("tenant_id", "id")`. Columns must
be declared in the same table.

### Unique constraints

Two forms, same guarantee:

```jsonc
"email": { "type": "text", "unique": true }        // single column, column-level
"unique": ["email", ["tenant_id", "email"]]        // table-level list
```

A string entry is one column; an array entry is a composite key. Names are
synthesized: `{table}_{column}_key` / `{table}_{columns joined by _}_key`.
Declaring the same key twice (column flag *and* list entry) is rejected.

### Foreign keys

Single column, with the shorthand:

```jsonc
"role_id": { "type": "integer", "references": "roles.id" }
```

Composite keys, or any non-default action, use the table-level object keyed by
constraint name:

```jsonc
"foreignKeys": {
  "users_role_fkey": {
    "columns": ["tenant_id", "role_id"],
    "references": "roles",                    // or "other_schema.roles"
    "referenceColumns": ["tenant_id", "id"],
    "onDelete": "cascade",
    "onUpdate": "restrict"
  }
}
```

| Rule | What happens |
|---|---|
| `columns` must be declared in this table | rejected |
| `referenceColumns` must have the same number of entries as `columns` | rejected |
| Actions: `no action` (default), `restrict`, `cascade`, `set null`, `set default`; case-insensitive, `_`/`-` accepted | anything else is rejected |
| The referenced columns must be unique in the target (primary key, unique constraint or unique index) | PostgreSQL refuses at apply; the migration rolls back |
| `set null` needs nullable columns; `set default` needs a default | PostgreSQL refuses at apply |
| Declared names must not collide with a generated name (`{t}_pkey`, `{t}_{col}_key`, `{t}_{col}_fkey`) | rejected |

Actions are part of the definition: changing one drops and re-creates the
constraint.

### CHECK and EXCLUSION constraints

Not expressible. They are read from the catalog and protected: a plan that would
remove one is refused with its name. See
[`migration-limitations.md`](../notes/migration-limitations.md) item 2.

---

## Indexes

```jsonc
"indexes": {
  "users_state_idx": { "columns": ["state"] },
  "users_email_lower_idx": { "columns": ["email"], "unique": true }
}
```

| Rule | What happens |
|---|---|
| Key = index name, a plain identifier | rejected otherwise |
| Index names are unique across the whole file (PostgreSQL keeps indexes in one namespace) | rejected |
| `columns` non-empty, all declared in this table | rejected |
| `unique` optional, default `false` | |

Indexes are matched by **definition** (columns + uniqueness), not by name, so a
hand-named equivalent is left alone. Renaming an index in the file does not
rename it in the database.

Expression, partial (`WHERE`), covering (`INCLUDE`) and non-btree indexes cannot
be declared; they are detected and protected from removal.

---

## Identity columns

```jsonc
"id": { "type": "bigint", "nullable": false, "identity": true }
```

Generates `"id" bigint NOT NULL GENERATED BY DEFAULT AS IDENTITY`. Integer or
bigint only, cannot have a `default`, and is implicitly `NOT NULL` whether or not
`nullable` is stated. An existing `GENERATED ALWAYS` column is recognised as an
identity and left as it is.

---

## Defaults

```jsonc
"created_at": { "type": "timestamp", "default": "now()" },
"state": { "type": "text", "default": "'active'" },
"payload": { "type": "uuid", "default": "gen_random_uuid()" }
```

- The expression is raw SQL, emitted **verbatim** (`'a  b'` keeps its spaces,
  `'2020-01-01 10:00'::date` keeps its cast).
- It cannot contain `;` or the migration breakpoint marker; such a default
  produces a migration that fails at apply.
- PostgreSQL rewrites some expressions when it stores them (`pg_catalog.now()` →
  `now()`). The tool records both what you declared and what the column holds, so
  an unchanged declaration never regenerates the same migration.
- To remove a default, delete the property.

---

## Identities (`$id`) — read this before editing the file

Every table and column carries a persistent number. It is how the tool tells a
**rename** (same id, new name) from a **replacement** (new id, `DROP` + `ADD`).
You never assign these; the tool writes them.

| Property | Scope |
|---|---|
| Table `$id` | unique among tables |
| Column `$id` | unique **within its table**; the same number can name a column in two tables |

Rules, in practice:

- **Do not edit, invent or copy `$id` values.** They are tool-owned. The tool
  refuses duplicates, ids of deleted objects, ids that contradict the database,
  and ids it never issued. A hand-written id that happens to match an existing
  object is indistinguishable from a rename, so it renames that object.
- **Renaming a table or column is just renaming the key** and keeping the `$id`
  inside it. The tool then emits `ALTER TABLE … RENAME …` and keeps the data.
- **Removing the `$id`** (or using a different one) makes the object look new.
  Deleting a column and adding an unrelated one in the same table in one change
  is ambiguous and is refused: keep the `$id` for a rename, or pass
  `--drop-add table.column` to say "this is a real drop plus add".
- **A deleted object's id is never reused.** The database remembers it, so a new
  object always gets a higher number in its table.
- **Adoption**: a file without `$id`s adopts the ids the database already has for
  those names, so you can start from a hand-written declaration.
- **Portability**: the file carries the ids, so development, staging and
  production apply the same identities. Two branches adding columns to
  *different* tables never collide.

Ids are internal. They are not used for ordering, sorting, display or foreign
keys in your tables.

---

## Validation and errors

The parser is strict; these are the messages you will meet:

| Message | Cause |
|---|---|
| `Unknown top-level property 'x'. The schema contains only "tables".` | typo at the root |
| `Table 'x': unknown property 'y'` | typo inside a table, column, index or foreign key |
| `Table 'x' must contain a "columns" object` | columns missing |
| `Table 'x' must define at least one column` | empty `columns` |
| `Table 'x' is defined more than once` | duplicate key |
| `column 'y' has unsupported type 'z'` | type outside the table above |
| `... "$id" must be a positive integer` | id is not a positive whole number |
| `... "$id" N is already used by another column in table 'x'` | duplicate inside a scope |
| `... "primaryKey" names column 'y', which is not declared` | typo in a key list |
| `... index 'y' names column 'z', which is not declared` | typo in an index |
| `... "unique" lists column 'y', which already declares "unique": true` | the same key twice |
| `... foreign key 'y' maps 2 column(s) onto 1 reference column(s)` | arity mismatch |
| `... foreign key 'y' collides with the name Chetah generates ...` | declared name clashes with `{t}_pkey` / `{t}_{col}_key` |

Runtime refusals (not parse errors) come from `diff`/`apply`: ambiguous
identity, ids the tool never issued, edited applied migrations, divergent
history, and destructive changes without confirmation. All are reported with the
object names and say what to do.

---

## Workflow

1. **Edit** `chetah-schema.json`.
2. **`diff`** — prints the SQL a plan would run. It writes no DDL; it does record
   any missing `$id`s into the file, because identity has to be recorded when it
   is decided. `diff --check` writes nothing at all (no file, no DDL, no lock) and
   exits `1` when a migration is pending.
3. **`apply`** — writes the migration file(s), then executes in one transaction:
   the DDL, the applied-state row, and the identity registry update. A failure
   rolls everything back and records the failure. The next interactive `apply`
   offers Retry, Repair, or Cancel; non-interactive apply refuses. `repair`
   re-plans the failed entry from the corrected schema, verifies the database
   still matches the previous snapshot, archives the failed SQL, and applies
   the replacement.
4. **Regenerate the models** if the shape the code sees changed.
5. **Commit** the schema file and the generated `migrations/` files together.

What `apply` creates on disk, next to the schema file by default:

```text
migrations/
  20261106230743_create_users_plus_1/
    20261106230743_create_users_plus_1.sql     -- the statements, in order
    meta/20261106230743_create_users_plus_1_snapshot.json
  _journal.json                                -- ids, hashes, operation metadata
```

On the database side, in its own `chetah` schema: `chetah.migrations` (what ran,
with hashes) and `chetah.objects` (the identity registry). Never edit a migration
that has run — it is history, and the tool refuses to replay one whose file no
longer matches.

Destructive changes ask for confirmation: `DROP TABLE`, `DROP COLUMN`,
`DROP INDEX`, `DROP CONSTRAINT`, narrowing a type, and `SET NOT NULL` on a column
that already holds NULLs. A non-interactive run refuses instead of assuming
"yes".

---

## Cookbook

Add a table:

```jsonc
"posts": {
  "model": "Post",
  "columns": {
    "id": { "type": "bigint", "nullable": false, "identity": true },
    "user_id": { "type": "bigint", "nullable": false, "references": "users.id" },
    "title": { "type": "text", "nullable": false }
  }
}
```

Add a nullable column: add the entry; nothing else is needed.

Rename a column: rename the key, keep its `$id`:

```jsonc
"username": { "$id": 3, "type": "text" }   →   "display_name": { "$id": 3, "type": "text" }
```

Replace a column (real drop + add), two steps or one explicit flag:

```sh
# remove the old column, apply, then add the new one — or, in one migration:
dotnet run --project src/Chetah.Db -- apply --drop-add users.address --schema chetah-schema.json
```

Change a type: widen freely (`integer` → `bigint`, `varchar(100)` →
`varchar(200)`); narrowing prompts for confirmation.

Make a column `NOT NULL` on a populated table: only if it has no NULLs and, if
rows exist, a `default`; otherwise the statement fails and rolls back.

Add a unique constraint: `"unique": true` on the column, or a table-level list
entry for a composite.

Add an index: an entry under `indexes`.

Drop a column or a table: delete the entry; the plan is destructive-gated. The
confirmation names what disappears — except triggers and policies attached to a
dropped table, which PostgreSQL removes silently (see
[`migration-limitations.md`](../notes/migration-limitations.md) item 18).

---

## Common mistakes

| Mistake | What happens |
|---|---|
| Removing a table from the file to "leave it alone" | it is planned as `DROP TABLE` (gated). Never use omission to exclude a table. |
| Editing `$id` values | refused, or treated as a rename. Treat them as read-only. |
| Editing a migration that already ran | the next run refuses (`no longer matches what the database ran`) |
| Assuming `"nullable": false` is the default | omitted means **nullable** |
| A foreign key to a non-unique column | PostgreSQL refuses at apply; the migration rolls back |
| `identity` plus `default` | rejected |
| Two indexes with the same name in different tables | rejected (PostgreSQL keeps one namespace) |
| A `default` containing `;` | the migration fails at apply and rolls back |
| Expecting `jsonb`/`numeric`/`timestamptz` | not expressible yet; a declared `text` against such a column is a gated type change |

---

## For AI assistants

When generating or editing a `chetah-schema.json`:

1. Emit only the properties listed in this guide. The parser rejects unknown
   ones, so guessing a property name fails the whole file.
2. Never write `$id` unless you are copying the file verbatim; if you are adding
   an object, leave `$id` out and let the tool assign it.
3. A rename is "same `$id`, new key". If you do not have the existing id, do not
   rename — output a removal and an addition as separate steps, or say that
   `--drop-add` is required.
4. State `"nullable"` explicitly. Omitting it means nullable.
5. Foreign keys need the referenced columns to be unique in the target table.
6. Keep column names to `[A-Za-z_][A-Za-z0-9_]*`; table names may use `_`.
7. After editing, the verification command is
   `diff --check` (exit `0` = in sync, `1` = pending or unsafe). Do not claim a
   schema is valid without it.
8. Do not remove a table from the file to "ignore" it.

Template to fill in (it parses as written — rename and extend it):

```jsonc
{
  "tables": {
    "tenants": {
      "model": "Tenant",
      "primaryKey": ["id"],
      "columns": {
        "id": { "type": "bigint", "nullable": false, "identity": true }
      }
    },
    "widgets": {
      "model": "Widget",
      "primaryKey": ["id"],
      "unique": [["tenant_id", "name"]],
      "indexes": { "widgets_name_idx": { "columns": ["name"] } },
      "foreignKeys": {
        "widgets_tenant_fkey": {
          "columns": ["tenant_id"],
          "references": "tenants",
          "referenceColumns": ["id"],
          "onDelete": "no action",
          "onUpdate": "no action"
        }
      },
      "columns": {
        "id": { "type": "bigint", "nullable": false, "identity": true },
        "tenant_id": { "type": "bigint", "nullable": false },
        "name": { "type": "text", "nullable": false }
      }
    }
  }
}
```

---

## Where the rest is

| I want to… | Read |
|---|---|
| understand how the diff, ids, history and gates work | [`schema-tooling.md`](../reference/schema-tooling.md) |
| know why a decision was made | [`decisions.md`](../project/decisions.md) |
| query data with the generated models | [`typed-models.md`](../reference/typed-models.md), [`basic-select.md`](../reference/basic-select.md) |
| see what is not supported, and whether it is safe to ignore | [`migration-limitations.md`](../notes/migration-limitations.md) |
| see the plan for upcoming work | [`roadmap.md`](../project/roadmap.md) |
