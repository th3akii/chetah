# Typed models from a schema file — how everything works

> Status: updated for Stage 2 · applies to Milestone 002 (schema-first typed
> models) plus the query-refinement chains built on it

How to write that file: [`writing-a-schema.md`](../guide/writing-a-schema.md).

This document explains the milestone-002 flow: one JSON schema file is
the single source of truth that produces C# models **and** table metadata, a CLI
generator emits deterministic source files, and the Postgres
executor returns strongly typed records. Since Stage 2 those typed models also
drive `Where`/`Select`/join/group chains (see the "Typed queries" step and
[`decisions.md`](../project/decisions.md) D004). For a task-oriented walkthrough
of the query API, see [`../guide/select.md`](../guide/select.md).

```
chetah-schema.json → Chetah.CodeGen → Generated/Tables/*.g.cs → db.Select<T>()… → List<User>
```

See [`basic-select.md`](basic-select.md) for the underlying describe → compile →
execute → map layering; nothing below changes those boundaries.

## Step 1 — the schema file

The schema is JSONC ("JSON with comments"), parsed with `System.Text.Json`
(no custom parser). Example
([`examples/BasicExample/chetah-schema.json`](../../examples/BasicExample/chetah-schema.json)):

```json
{
  // Tables are keyed by their PostgreSQL name; columns by their column name.
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

Rules:

- A key in `tables` — the real PostgreSQL table name (stored raw; the dialect
  compiler quotes it at SQL generation time, exactly as in milestone 001).
  Letters, digits, underscores and hyphens are supported; names that cannot
  produce a valid C# accessor class (everything else, e.g. spaces or quotes)
  are rejected with that reason in the error.
- `model` — optional C# model name. Default: PascalCase of the table name.
  Model and accessor names must be unique across the whole schema, and a keyword
  (`class`, `event`, `params`) is rejected.
- A key in `columns` — a PostgreSQL type expression: `integer`, `bigint`,
  `text`, `boolean`, `uuid`, `timestamp`, `timestamptz`, `date`, `time`,
  `numeric`, `jsonb`, `bytea`, `T[]`, a declared enum, and so on. The CLR
  mapping is listed in
  [`writing-a-schema.md`](../guide/writing-a-schema.md#types). A type with no
  CLR mapping (a domain, a composite) is a valid declaration but code generation
  refuses it with the column named.
- `nullable` — optional. **Omitted means nullable**, matching PostgreSQL's own
  default. `"nullable": false` marks a NOT NULL column. A primary key or identity
  column is always NOT NULL, so it generates `int`/`long` and an explicit
  `"nullable": true` on one is rejected.
- `$id` — maintained by `chetah-db`, not by hand: the tool writes a persistent
  identity onto every table and column and uses it to tell a rename from a drop
  plus an add ([`schema-tooling.md`](schema-tooling.md), D006).
- Constraints — `primaryKey` (column flag or table object), `unique` (column
  flag or table objects), `foreignKeys`, `checks`, `indexes`, `default`,
  `identity`, `generated` — are accepted and validated. The generator maps every
  column to a CLR type regardless; SQL semantics live in the migration tool.
  Vocabulary: [`writing-a-schema.md`](../guide/writing-a-schema.md).
- `default` accepts a JSON literal (`"pending"`, `0`, `true`) or an explicit SQL
  expression (`{ "sql": "now()" }`). A string is never interpreted as SQL, so
  `"default": "now()"` would default to the text `now()`.
- `type` accepts a PostgreSQL modifier where one exists: `varchar(100)` maps to
  `string` like `text`, `timestamp(3)` to `DateTime`. The modifier is part of the
  migrated type.
- Column names must be plain identifiers (`[A-Za-z_][A-Za-z0-9_]*`) because they
  become C# property names (`first_name` → `FirstName`) and database rows are
  read by name. PostgreSQL columns that require quoting (spaces, `@`…) are
  rejected rather than half-supported. The parser applies exactly this ASCII
  pattern, the same one the JSON Schema contract publishes.
- Unknown properties are rejected rather than ignored, so a typo cannot silently
  change nullability or drop a setting.
- Validation errors: empty/duplicate names, unsupported types, invalid or
  clashing C# names, columns whose PascalCase forms collide (`user_id` and
  `user__id` both map to `UserId`), zero columns/tables. The parser never
  partially emits code.

## Step 2 — run the generator

```sh
dotnet run --project src/Chetah.CodeGen -- \
  --schema <schema.json> [--out <dir>] [--namespace <ns>] [--layout split|single]
dotnet run --project src/Chetah.CodeGen -- --help
```

- `--schema` (required): the schema file to read.
- `--out` (default `.`): directory that receives the generated files.
- `--namespace` (default `Chetah.Generated`): namespace of generated code.
- `--layout` (default `split`): output organization.
  - `split` — one file per table (`Tables/<Model>.g.cs`), one per enum
    (`Enums/<EnumType>.g.cs`), and a small shared `Chetah.Schema.g.cs` registry.
    Files are named after the generated C# type.
  - `single` — the original layout: every generated type in
    `Chetah.Schema.g.cs`.
- `--help`: usage, option defaults and examples.

Invalid paths, namespaces (including C# keywords), layouts and missing option
values fail with a message naming the argument; no output is written.

Guarantees:

- Output is deterministic — same schema, same layout, same bytes.
- Every written file is clearly marked `<auto-generated>` and starts with
  `#nullable enable`.
- `Generated/.chetah-codegen.json` records the files this tool owns. Regeneration
  overwrites only those files and removes generated files whose table or enum no
  longer exists (including after a rename or a layout switch).
- Other files in the output directory are never touched. A file at a generated
  path that lacks the generated marker is refused rather than overwritten.
- Rendering and validation finish before anything is written; a schema error
  leaves the previous output intact. Filename collisions — including
  case-insensitive collisions on Windows — fail with the colliding declarations
  named.

## Step 3 — what gets generated

One accessor class, one record and the table binding per table. In the default
`split` layout, each table file is self-contained
([example](../../examples/BasicExample/Generated/Tables/User.g.cs)):

```csharp
public static class UsersTable
{
    // Typed column metadata. Non-default write rules are emitted only when set:
    // IsNullable = false, HasDefault = true, Generation = …; the canonical
    // PostgreSQL type (StoreType) is always emitted.
    public static readonly global::Chetah.Column<int> Id = new("id") { IsNullable = false, HasDefault = true, StoreType = "int4" };
    public static readonly global::Chetah.Column<string> Name = new("name") { IsNullable = false, StoreType = "text" };
    public static readonly global::Chetah.Column<int?> Age = new("age") { StoreType = "int4" };
    public static readonly global::Chetah.Column<string> Display =
        new("display") { IsNullable = false, Generation = global::Chetah.ColumnGeneration.Computed, StoreType = "text" };

    // Table metadata handed to the query objects (schema order = output order).
    public static readonly global::Chetah.Table Table =
        new("users", new global::Chetah.Column[] { Id, Name, Age, Display });

    // Explicit property-name → raw column-name snapshot, Ordinal comparison.
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> PropertyColumns =
        new global::System.Collections.Generic.Dictionary<string, string>(global::System.StringComparer.Ordinal)
        {
            [nameof(User.Id)] = "id",
            [nameof(User.Name)] = "name",
            [nameof(User.Age)] = "age",
            [nameof(User.Display)] = "display",
        };

    // Hand-written (generated) row mappers — no reflection.
    public static User Map(global::Npgsql.NpgsqlDataReader reader) { … }            // name-based; full-row reads
    public static User MapAt(global::Npgsql.NpgsqlDataReader reader, int offset) { … } // ordinal offset; joined/partial slots
}

public sealed record User(int Id, string Name, int? Age, string Display);

// The same file also contributes its binding to the shared registry type.
public static partial class ChetahSchema
{
    public static readonly global::Chetah.Postgres.TableBinding<User> UserBinding =
        new(UsersTable.Table, UsersTable.Map, UsersTable.MapAt, UsersTable.PropertyColumns);
}
```

`Column` metadata is also what INSERT and UPDATE validate against: nullability,
database defaults, identity generation and computed columns. `StoreType` carries
the column's canonical PostgreSQL type; parameter binding reads it to decide the
provider type a value is written as — which matters for `DateTime`, where
`timestamp` and `timestamptz` share one CLR type but not one semantics
([`writing-a-schema.md`](../guide/writing-a-schema.md#datetime-values)).
Descriptions of the
write surface are in [`../guide/insert.md`](../guide/insert.md) and [`../guide/update.md`](../guide/update.md).

`Chetah.Schema.g.cs` holds the rest of the registry, so the public API is
unchanged whether you use `split` or `single`:

```csharp
public static partial class ChetahSchema
{
    static ChetahSchema()
    {
        Bindings = new global::System.Collections.Generic.Dictionary<global::System.Type, global::Chetah.Postgres.ITableBinding>
        { [typeof(User)] = UserBinding, … };
    }
    public static readonly global::System.Collections.Generic.IReadOnlyDictionary<global::System.Type, global::Chetah.Postgres.ITableBinding> Bindings;
}
```

The `split` directory for two tables and an enum looks like:

```text
Generated/
  Chetah.Schema.g.cs        registry + Bindings
  Tables/
    Order.g.cs              OrdersTable, Order record, OrderBinding
    User.g.cs               UsersTable, User record, UserBinding
  Enums/
    UserStatus.g.cs         UserStatus.Values (labels in declaration order)
  .chetah-codegen.json      ownership manifest
```

Enum files expose the declared labels as `Values`; enum columns still map to
`string`, exactly as in the `single` layout. An enum outside the default
schema is qualified in both its type and file name (`app.kind` →
`Enums/AppKind.g.cs`, class `AppKind`) so two schemas can declare the same
enum name. `single` emits the same types in one `Chetah.Schema.g.cs`.

Naming: accessor class = PascalCase(table name) + `"Table"` (`users` →
`UsersTable`); record = `model` or PascalCase(table name) (`User`).
Non-nullable columns keep their bare CLR type (`int`, `string`); nullable
columns become `int?` / `string?`.

### Column property names

A column's C# property is PascalCase(column): `created_at` → `CreatedAt`,
`map_at` → `MapAt`. That name is used in all four places the property appears —
the `Column` accessor, the `Table` column list, the `PropertyColumns` map and the
record parameter — so they never disagree.

When the plain name is already taken it is suffixed with `Column`, and the suffix
applies to all four places at once. Taken means: the model record's name (`users`
in model `Users` → `UsersColumn`), the accessor class's name, one of the
accessor's own members (`Table`, `PropertyColumns`, `Map`, `MapAt`), or a member
the record compiler synthesizes (`Equals`, `GetHashCode`, `ToString`,
`PrintMembers`, `Deconstruct`, `Clone`, `EqualityContract`). Names that do not
collide are emitted unchanged, so existing schemas keep their exact generated
API. Two columns of one table that would end up with the same property (say
`map` and `map_column`, both `MapColumn`) fail generation with the table and both
column names.

Every namespace reference in generated code is `global::`-qualified
(`global::Chetah.Column`, `global::System.Collections.Generic…`,
`global::Npgsql.NpgsqlDataReader`), so a table whose model is named `System`,
`Chetah` or `Npgsql` does not shadow the namespace its own file needs. The same
holds for the CLR types C# does not spell as keywords: property types, nullable
variants and reader expressions use `global::System.` (`global::System.Guid?`,
`reader.GetFieldValue<global::System.DateOnly>`), so a model named `Guid` or
`DateTime` is safe too. Keyword types (`int`, `string`, `bool`, `byte[]`, …)
stay bare — they cannot be shadowed.

Type map: `integer`→`int`, `bigint`→`long`, `text`→`string`,
`boolean`→`bool`, `uuid`→`global::System.Guid`,
`timestamp`→`global::System.DateTime`, `timestamptz`→`global::System.DateTime`,
`date`→`global::System.DateOnly`, `time`→`global::System.TimeOnly`,
`numeric`→`decimal`. The complete list, with aliases and modifiers, is the
types table in [`writing-a-schema.md`](../guide/writing-a-schema.md#types). A
type outside it is refused by the generator with the column named.

The binding validates its mapping at construction: every key must name a
readable public instance property of the model, every table column must be
mapped exactly once, and unknown columns are rejected with `ArgumentException`.
Unmapped extra model properties are allowed but fail if a query uses them;
there is no runtime name inference anywhere.

## Step 4 — typed queries

The generated metadata feeds the compile→execute pipeline. The
database-centric API from [`decisions.md`](../project/decisions.md) D001 resolves `T`
through the generated registry:

```csharp
var db = new PostgresDb(connectionString, ChetahSchema.Bindings);
var users = await db.Select<User>().ToListAsync();
foreach (var user in users)
    Console.WriteLine($"{user.Id} {user.Name} ({user.Age})"); // IntelliSense on all three
```

`db.Select<T>()` resolves `T` against the `Bindings` dictionary and throws an
`InvalidOperationException` with guidance if `T` is unknown or the registry was
not passed to the database. SQL identifiers come only from the binding's
`Table` metadata; quoting remains the compiler's job. No user string ever
reaches the SQL text.

A `TypedSelectQuery<T>` chain is a description of SQL clauses — not an
order-sensitive LINQ pipeline (calling `Limit` before `Where` still emits the
clauses in SQL order):

```csharp
// Filter, shape, order, page — every call snapshots; the base query never changes.
var page = await db.Select<User>()
    .Where(u => u.Age >= 18)
    .OrderByDescending(u => u.Age).ThenBy(u => u.Id)
    .Offset(1).Limit(1)
    .Select(u => new { u.Id, u.Name })     // projection reshapes to the underlying SQL expressions
    .ToListAsync();

// Explicit joins: LEFT-join nulls are real nulls on the right side.
var joined = await db.Select<User>()
    .LeftJoin<Order>((user, order) => user.Id == order.UserId)
    .Select(row => new { UserId = row.Left.Id, OrderId = row.Right == null ? (int?)null : row.Right.Id })
    .ToListAsync();

// Grouped reads: WHERE filters rows, HAVING filters groups, Sql.* are expression markers.
var totals = await db.Select<Order>()
    .GroupBy(o => o.UserId)
    .Having(o => Sql.Sum(o.Amount) >= 20)
    .Select(o => new { o.UserId, Count = Sql.Count(), Total = Sql.Sum(o.Amount) })
    .OrderBy(row => row.UserId)
    .ToListAsync();

// Or compile without executing (same contract as Compile on the compiler layer):
var compiled = db.Select<User>().Where(u => u.Name.StartsWith("A")).Compile();
```

Terminal operators read what they need instead of the whole result set. They take
the same chain — filters, projections, ordering and paging all still apply:

```csharp
// Asks the database for one row (LIMIT 1); throws InvalidOperationException when nothing matches.
var first = await db.Select<User>().Where(u => u.Age >= 18).OrderBy(u => u.Age).FirstAsync();
var maybe = await db.Select<User>().Where(u => u.Id == 99).FirstOrDefaultAsync();   // null

// Asks for at most two rows (LIMIT 2) so "more than one" is detectable without loading the rest.
var only = await db.Select<User>().Where(u => u.Id == 1).SingleAsync();             // throws on 0 or 2+
var absent = await db.Select<User>().Where(u => u.Id == 99).SingleOrDefaultAsync(); // null; throws on 2+

// One constant output row, capped at one row: existence without reading any table column.
var any = await db.Select<User>().Where(u => u.Age >= 18).AnyAsync();

// SELECT COUNT(*) over the complete query — a derived table when DISTINCT, grouping or paging apply.
var count = await db.Select<User>().Where(u => u.Age >= 18).CountAsync();           // long
```

- `First`/`Single` add `LIMIT 1`/`LIMIT 2`, but a smaller `Limit` already on the
  query wins: pagination is part of the query, so `Limit(0)` still reads nothing
  and `Limit(1)` still bounds what `Single` can see. `Offset` is preserved.
- `FirstAsync` throws when no row matches. `SingleAsync` throws on zero or more
  than one. `SingleOrDefaultAsync` throws only on more than one, otherwise
  returns `default`. `AnyAsync` never materializes rows.
- Because pagination is part of the query, `SingleAsync` checks uniqueness
  within the query's resulting rows, not among all matching table rows:
  `Limit(1).SingleAsync()` can never report "more than one". Drop the limit when
  uniqueness across every match is what matters.
- `AnyAsync` keeps every clause that decides whether a row exists (filter, joins,
  `Distinct`, grouping, `HAVING`, paging) and drops `ORDER BY`, which cannot
  create or remove a row.
- `CountAsync` is the number of rows the complete query yields. Filters and joins
  alone decide that, so it compiles to a direct `SELECT COUNT(*)`; when
  `DISTINCT`, `GROUP BY`/`HAVING` or `LIMIT`/`OFFSET` change how many rows come
  back, the whole SELECT is compiled as a derived table and counted. Ordering is
  never rendered (it cannot change a count) but is still validated, so a chain
  `Compile` would reject is rejected by the count too.

Behavior contract (enforced before any connection opens):

- The accepted lambda surface is finite; unsupported methods, getters,
  `Expression.Invoke` and non-scalar ordering keys throw
  `NotSupportedException` naming the expression — there is no client-side
  filtering/projection fallback. `Null` arguments throw
  `ArgumentNullException`.
- Captured values and collections are snapshotted at the fluent call; changing
  them afterwards cannot change a query already built.
- `ThenBy` requires an existing `OrderBy`; `Limit`/`Offset` reject negative
  counts, allow zero, and replace earlier values; `Distinct()` is idempotent;
  joins must come before `GroupBy`/`Distinct`/pagination; a second `GroupBy`
  and aggregates outside grouped SELECT/HAVING/ORDER BY are rejected.
- Aggregate result kinds: `COUNT` → `long`; `SUM(int)` → `long?`;
  `SUM(bigint)`/`AVG` → `decimal?`; `MIN`/`MAX` keep the argument's type,
  including nullability.

Explicit `QueryAsync` remains available for ad-hoc queries:

```csharp
// Same full-table query, same generated mapper, spelled out:
var users = await db.QueryAsync(UsersTable.Table.SelectAll(), UsersTable.Map);
```

- Full single-table reads use the generated name-based `Map` (one `GetOrdinal`
  per nullable column, typed reader calls, explicit `IsDBNull` handling).
  Projected, joined and grouped reads plan output slots per query and use the
  ordinal-based generated `MapAt`, so a schema column's index travels with its
  slot; the reader delegate is compiled once per execution, with no per-row
  reflection. Selecting SQL `NULL` into a non-nullable output slot fails with
  an exception naming the output slot rather than substituting a default.

## Tests

`tests/Chetah.CodeGen.Tests`:

- **Schema parsing/validation** — valid parse, defaults, unsupported types,
  duplicate/invalid names, empty tables/columns.
- **Code generation** — generated-consumer behavior: property-name resolution,
  the ordinal `MapAt` path and typed records; the checked-in split fixture is
  regenerated and compared byte-for-byte, so it cannot drift.
- **CLI and regeneration** — both layouts, custom namespaces, deterministic
  reruns, deleted/renamed tables, layout switches, preservation of unrelated
  files, refused user-file overwrites, case-insensitive collisions, invalid
  arguments, and a real `dotnet build` of generated output in a consuming
  project.
- **Type mapping** — every PostgreSQL type name → CLR type.
- **Typed SELECT** — live-database round trip (same `CHETAH_CONNECTION_STRING`
  variable) creating/dropping only `test_widgets`, asserting typed records and
  `NULL` mapping.
- **Stage 2 chain behavior** — `Stage2QueryTests` (offline: value/collection
  snapshots, null-lift comparisons and negation, membership/`IN`/`LIKE`
  boundaries, numeric cast handling, join qualification and rejection rules,
  grouping/`HAVING`/aggregate boundaries) and `Stage2LiveTests` (five
  explicit-skipped facts against a disposable `chetah_stage2_<guid>` schema:
  pagination/projection/DISTINCT, null-literality, joins, grouping, generated
  `MapAt` against a live reader).

Backward compatibility of milestone 001 is covered by the unchanged
`tests/Chetah.Core.Tests` and `tests/Chetah.Postgres.Tests`.

## Deliberate limits

Not implemented, not hidden: SQL Server, Roslyn source generators, automatic
relationship management, RIGHT/FULL/CROSS joins. INSERT, UPDATE and DELETE are
documented in [`../guide/insert.md`](../guide/insert.md), [`../guide/update.md`](../guide/update.md) and [`../guide/delete.md`](../guide/delete.md).
Schema-to-database tooling and catalog introspection live in `src/Chetah.Db`
instead — see [`schema-tooling.md`](schema-tooling.md).
Query lambdas outside the
frozen translator surface fail explicitly (no evaluation fallback).
Mapping trusts the schema's
nullability claim — a DB that
returns `NULL` for a `"nullable": false` column will surface as a runtime
error from the reader, not silent mis-mapping.
