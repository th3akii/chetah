# Inserting rows with INSERT — a guide

> Status: delivered · covers `db.Insert<T>()`, defaults, multi-row inserts,
> conflict handling (upsert) and `RETURNING`
> Related: [`select.md`](select.md) · [`update.md`](update.md) ·
> [`delete.md`](delete.md) · reference [`typed-models.md`](../reference/typed-models.md)

INSERT uses the same generated model binding as SELECT: `T` resolves to its
table, values are parameterized, and the statement is compiled before a
connection opens. PostgreSQL identifier quoting and parameter numbering are the
compiler's job; user values never become SQL text.

## Insert one row

```csharp
using Chetah;
using Chetah.Postgres;

var db = new PostgresDb(connectionString, ChetahSchema.Bindings);

await db.Insert<Student>()
    .Values(new { FirstName = "Marko", Age = 20 })
    .ExecuteAsync();               // affected row count
```

`Values` reads the row object's public readable properties and resolves each name
through the generated property map (`FirstName` → `first_name`). Raw column names
work too (`new { first_name = "Marko" }`). An unknown property, a property that does
not map to a column, or a non-readable property throws before any connection opens.

## Return the inserted row

```csharp
var student = await db.Insert<Student>()
    .Values(new { FirstName = "Marko" })
    .ReturningAll()
    .SingleAsync();

var label = await db.Insert<Student>()
    .Values(new { FirstName = "Marko" })
    .Returning(s => new { s.Id, s.FirstName })
    .SingleAsync();
```

`Returning` accepts the same projection shapes as `Select`: a whole entity, a
scalar, an anonymous object or an object initializer. `ReturningAll()` is the whole
entity. Terminals are `ToListAsync`, `FirstAsync`, `FirstOrDefaultAsync`,
`SingleAsync` and `SingleOrDefaultAsync`; they check the rows actually returned, so
a multi-row insert or a conflict that ignores the row can yield zero or many.
UPDATE and DELETE use the same `RETURNING` machinery.

## Omitted, NULL and DEFAULT are different

| What you write | SQL | Meaning |
|---|---|---|
| property absent from the row object | column not in the column list | PostgreSQL uses the column's default, or NULL when it has none |
| `Age = (int?)null` | `$n` bound to NULL | explicit NULL; the default is **not** used |
| `Nickname = DefaultValue.Value` | `DEFAULT` in that value slot | explicit database default |
| `Id` omitted on an identity column | column not in the column list | the sequence generates the value |

An explicit NULL or DEFAULT for a `NOT NULL` column with no default is rejected
before execution. Omitting a `NOT NULL` column that has no default is rejected too;
PostgreSQL would fail the statement, but the compiler names the column instead.

Generated metadata carries the write-side rules: `IsNullable`, `HasDefault`,
`Generation.Always` (a `GENERATED ALWAYS AS IDENTITY` column accepts `DEFAULT`
but not an explicit value) and `Generation.Computed` (a generated column can
never be inserted). Identity `BY DEFAULT` columns may be supplied when you need
a specific id.

## Insert several rows

```csharp
var inserted = await db.Insert<Student>()
    .ValuesMany(new[]
    {
        new { FirstName = "Ada", Age = (int?)36 },
        new { FirstName = "Grace", Age = (int?)45 },
    })
    .ExecuteAsync();               // 2
```

`Values(row1, row2, …)` does the same with separate arguments. Every row in one
statement must supply the **same** column set; differing sets are rejected instead
of silently inserting shifted values. Per-row `DEFAULT` markers stay positional.

Use `DefaultValues()` for a table whose columns can all be produced by defaults:

```csharp
var note = await db.Insert<Note>()
    .DefaultValues()
    .ReturningAll()
    .SingleAsync();
```

## Handle unique conflicts

```csharp
// Any unique violation: ignore the row.
await db.Insert<Student>()
    .Values(new { FirstName = "Ada" })
    .OnConflictDoNothing()
    .ExecuteAsync();               // 0 when the row already exists

// A specific unique target.
await db.Insert<Student>()
    .Values(new { FirstName = "Ada" })
    .OnConflict(s => s.FirstName)
    .DoNothing()
    .ExecuteAsync();

// Upsert: update the existing row from the incoming values.
var updated = await db.Insert<Student>()
    .Values(new { FirstName = "Ada", Age = (int?)37, Nickname = "second" })
    .OnConflict(s => s.FirstName)
    .DoUpdate(update => update
        .SetExcluded(s => s.Nickname)     // nickname = EXCLUDED.nickname
        .Set(s => s.Age, 40)              // parameterized constant
        .SetDefault(s => s.Id))           // id = DEFAULT
    .ReturningAll()
    .SingleAsync();
```

A conflict target is one model property or an anonymous object of properties
(`s => new { s.TenantId, s.FirstName }`); targets and assignments are validated
against the generated columns. `EXCLUDED` is exposed through `SetExcluded` on the
same column, not as an arbitrary SQL fragment. Assigning a generated (computed)
column is rejected; `SetDefault` is the only way to write a default value in `DO
UPDATE`. An ignored conflict affects zero rows and returns no row when `RETURNING`
is used; an updated conflict affects one row per updated row.

## Inspect the SQL

```csharp
var compiled = db.Insert<Student>()
    .Values(new { FirstName = "Marko" })
    .OnConflict(s => s.FirstName)
    .DoNothing()
    .Returning(s => new { s.Id, s.FirstName })
    .Compile();

Console.WriteLine(compiled.Sql);
foreach (var parameter in compiled.Parameters)
    Console.WriteLine($"{parameter.ClrType.Name} {parameter.Value}");
```

`Compile()` is pure and never opens a connection. Parameter slots follow SQL
order: VALUES first, then conflict assignments, then RETURNING expressions.

## Row counts

`ExecuteAsync` returns the number of inserted rows. Ignored conflicts are not
counted; an upsert counts each row it inserted or updated.

## What is refused

| Refused | Reason / use instead |
|---|---|
| partial conflict targets (`ON CONFLICT … WHERE …`) | advanced predicate support is deferred |
| conflict targets that are expressions rather than columns | only generated column metadata is validated |
| arbitrary SQL fragments in `DO UPDATE` | use `Set`, `SetExcluded`, `SetDefault` |
| `OVERRIDING SYSTEM VALUE` for `GENERATED ALWAYS` identity columns | supply a `BY DEFAULT` identity column instead |
| values for generated columns, or non-`DEFAULT` values for `GENERATED ALWAYS` identity columns | generated values are read-only; identity `ALWAYS` only accepts `DEFAULT` |
| rows with different column sets in one statement | split the statement or supply every column |
| explicit or omitted NULL for a `NOT NULL` column without a default | rejected before the connection opens |
| unsupported CLR parameter types | only `int`, `long`, `decimal`, `string`, `bool`, `Guid` and `DateTime` are supported today |
| transaction management | kept out of this API; connections and commands are disposed per execution |

`CHECK`, `UNIQUE` (without a conflict clause) and foreign-key violations are
PostgreSQL's to decide and surface as `PostgresException`.
