# University example

A complete schema for a small university database, written in
[`chetah-schema.json`](chetah-schema.json). It is meant to be read, copied and
adapted — every table exists to show a shape the format supports.

```text
departments ──< instructors
     │               │
     ├──< students   │
     │               │
     └──< courses    │
            │        │
        semesters    │
            │        │
      course_offerings >──┘
            │
      enrollments >── students
            │
         grades
```

| Table | What it demonstrates |
|---|---|
| `departments` | single-column primary key, column-level unique, identity |
| `instructors` | composite unique, index, foreign key with actions |
| `students` | identity on `bigint`, several indexes, two foreign keys |
| `courses` | composite unique, numeric literal default |
| `semesters` | composite unique, `timestamp` columns |
| `course_offerings` | three foreign keys, string literal default |
| `enrollments` | composite primary key object, composite foreign key |
| `grades` | composite primary key and foreign key, check constraint, index |

Defaults show both forms: `"default": 5`, `"default": "enrolled"` are literals;
`"default": { "sql": "now()" }` is an explicit SQL expression.
Referential actions are part of each foreign key and are written the way
PostgreSQL names them (`CASCADE`, `RESTRICT`, `SET NULL`).

## What runs today

The schema validates, and the generator produces models for it (the default
`split` layout: one file per table plus the shared registry):

```sh
dotnet run --project src/Chetah.CodeGen -- \
  --schema examples/NewExample/chetah-schema.json --out examples/NewExample/Generated --namespace Chetah.Generated
```

`chetah-db diff` will refuse this particular file because `grades` declares a
check constraint. That is deliberate: checks are part of the schema contract but
the migration engine cannot create them yet, and it reports the limitation
instead of ignoring it (see
[`writing-a-schema.md`](../../docs/guide/writing-a-schema.md#what-the-migration-engine-executes)).
Remove the `checks` entry to exercise the migration path end to end.

The `migrations/` folder is the history produced while the example was being
built; it predates the `grades` table, so applying it after adding the table
generates one more migration.
