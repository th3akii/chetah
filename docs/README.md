# Chetah documentation

Chetah is a schema-first .NET ORM for PostgreSQL: one declaration file defines the
tables, the generator produces typed models from it, and the migration tool
(`chetah-db`) keeps the database in step with it.

This folder is organised by what you are trying to do.

```text
docs/
  guide/       how to write a schema, and how the workflow runs
  reference/   how the tooling and the query API behave, in detail
  project/     what is planned (roadmap) and why things are the way they are (decisions)
  notes/       what is broken or missing, and designs not yet implemented
```

## Start here

| I want to… | Read |
|---|---|
| write or change `chetah-schema.json` | [guide/writing-a-schema.md](guide/writing-a-schema.md) |
| read rows (filter, project, order, page, join, group, count) | [guide/querying.md](guide/querying.md) |
| insert, update or delete rows, use defaults, upsert and return written rows | [guide/writing-data.md](guide/writing-data.md) |
| understand the diff/apply workflow, ids, gates and history | [reference/schema-tooling.md](reference/schema-tooling.md) |
| use the generated models and the query API | [reference/typed-models.md](reference/typed-models.md), [reference/basic-select.md](reference/basic-select.md) |
| know what the project is for | [project/spec.md](project/spec.md) |
| know why something is the way it is | [project/decisions.md](project/decisions.md) |
| see what is planned or delivered | [project/roadmap.md](project/roadmap.md) |
| know what is unsupported, and whether it is safe to ignore | [notes/migration-limitations.md](notes/migration-limitations.md) |
| understand a design that is not implemented yet | [notes/constraint-metadata.md](notes/constraint-metadata.md) |

## The folders

**`guide/`** — task-oriented, for people writing the project. Everything a schema
author needs (the complete property reference, rules, cookbook, common mistakes)
is in [guide/writing-a-schema.md](guide/writing-a-schema.md), which is also the
file to hand an AI assistant: it ends with an explicit rule set and a template.
Reading and querying rows (terminals, filters, projections, ordering, paging,
joins, grouping) is [guide/querying.md](guide/querying.md); writing rows
(inserts, updates, deletes, defaults, conflicts, `RETURNING`) is
[guide/writing-data.md](guide/writing-data.md).

**`reference/`** — behaviour that is already implemented, described precisely:
the migration tool (diff, identities, history, safety gates), the generated
models, and the query API.

**`project/`** — the why and the when: the original specification, the roadmap
with its delivery status, and the decision log (D001…D008) with rationale and
revisit triggers.

**`notes/`** — working documents: the register of known limitations and bugs
(with reproductions, severity and suggested fixes), and designs that are planned
but not built.

Conventions: file names are lowercase with hyphens; a document states its status
(delivered / planned / open) near the top; cross-links are relative so the folder
can be moved as a unit.
