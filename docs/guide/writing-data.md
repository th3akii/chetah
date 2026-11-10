# Writing data with INSERT and UPDATE — a guide

> Status: delivered · covers `db.Insert<T>()`, conflict handling, `db.Update<T>()`
> and `RETURNING`
> Reference context: [`typed-models.md`](../reference/typed-models.md),
> [`querying.md`](querying.md)

INSERT and UPDATE use the same generated model binding as SELECT: `T` resolves to
its table, values are parameterized, and the statement is compiled before a
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

Return the inserted row with `RETURNING`:

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

## Update rows

```csharp
var updated = await db.Update<Student>()
    .Set(s => s.Age, 21)                        // direct value → one parameter
    .Set(s => s.Nickname, s => s.FirstName)     // expression over the stored row
    .Where(s => s.Id == 1)
    .ReturningAll()
    .SingleAsync();

var affected = await db.Update<Student>()
    .Set(s => s.Age, s => s.Age + 1)            // arithmetic on the current value
    .Where(s => s.Age != null)
    .ExecuteAsync();                            // affected row count
```

`Set` has three shapes, all validated against the generated columns:

| Call | SQL | Notes |
|---|---|---|
| `Set(s => s.Age, 21)` | `"age" = $1` | the value's CLR type must fit the column |
| `Set(s => s.Age, s => s.Age + 1)` | `"age" = "age" + $1` | translated by the SELECT lambda surface |
| `SetDefault(s => s.Nickname)` | `"nickname" = DEFAULT` | the column's own default expression |

Assignments are emitted in call order and a column may be assigned only once.
Column expressions are SQL: `Age + 1` on a nullable column is `NULL` when the
stored value is `NULL` (use `s => s.Age ?? 0` for a fallback).

### Assignment types are checked by C#

Both `Set` parameters are generic over the assigned type, so a column and a value
of unrelated CLR types have no common type argument and the call does not compile:

```csharp
db.Update<Student>().Set(s => s.Age, DateTime.Now);   // does not compile
db.Update<Student>().Set(s => s.FirstName, 5);        // does not compile
db.Update<Student>().Set(s => s.Age, (int?)null);     // compiles: SQL NULL
```

Implicit C# conversions still apply (`int` into a `long` column). When the value
is the one that would be widened, the selector stays intact and the assignment
is fine; when the column itself would be converted (`Set(s => s.Id, 5L)` on an
`int` column), the fluent call refuses before any I/O and asks for an explicit
cast on the value:

```csharp
db.Update<Student>().Set(s => s.Id, 5L);        // refused: selector would convert int → long
db.Update<Student>().Set(s => s.Id, (int)5L);   // explicit narrowing, accepted
```

The compiler then checks the SQL kind — identical scalar types, or a numeric pair
that PostgreSQL itself converts — and refuses anything else, along with
unsupported casts and expressions. Nothing is converted on the client.

### Scoping an update

An UPDATE with no `WHERE` changes every row, so it has to be requested
explicitly:

```csharp
var archived = await db.Update<Student>()
    .Set(s => s.Status, "archived")
    .AllRows()
    .ExecuteAsync();
```

`AllRows()` and `Where()` are mutually exclusive, and `Compile()` (or any
terminal) throws when neither was chosen. `Where` takes the same lambda as
`Select`: null-safe comparisons, `Contains` membership snapshots and captured
values folded at the call — a local mutated afterwards cannot change the built
query. Repeated `Where` calls combine with `AND`.

### Writes that cannot be executed

| Assignment | Result |
|---|---|
| a generated (computed) column | rejected: PostgreSQL computes it |
| a `GENERATED ALWAYS AS IDENTITY` column with a value | rejected; `SetDefault` is the only form |
| `NULL` into a `NOT NULL` column | rejected before the connection opens |
| an expression that can produce `NULL` into a `NOT NULL` column | rejected; use `COALESCE` or a non-null expression |
| `DEFAULT` on a `NOT NULL` column with no default | rejected; it would write `NULL` |
| the same column twice, or an unknown column | rejected |
| `CHECK`/`UNIQUE`/foreign-key violations | PostgreSQL decides; the error surfaces as `PostgresException` |

`RETURNING` works exactly as for INSERT — any projection shape, the same
terminals, and an empty result when the predicate matched no row.

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

`Compile()` is pure and never opens a connection. Parameter slots follow SQL order:
VALUES first, then conflict assignments, then RETURNING expressions. For UPDATE
the order is SET assignments, then WHERE, then RETURNING:

```csharp
var compiled = db.Update<Student>()
    .Set(s => s.Age, s => s.Age + 1)
    .Where(s => s.Id == 1)
    .Returning(s => new { s.Id, s.Age })
    .Compile();

// UPDATE "students" SET "age" = "age" + $1 WHERE "id" = $2 RETURNING "id" AS "c0", "age" AS "c1"
```

## What is refused

| Refused | Reason / use instead |
|---|---|
| partial conflict targets (`ON CONFLICT … WHERE …`) | advanced predicate support is deferred |
| conflict targets that are expressions rather than columns | only generated column metadata is validated |
| arbitrary SQL fragments in `DO UPDATE` or `SET` | use `Set`, `SetExcluded`, `SetDefault` |
| `OVERRIDING SYSTEM VALUE` for `GENERATED ALWAYS` identity columns | supply a `BY DEFAULT` identity column instead |
| values for generated columns, or non-`DEFAULT` values for `GENERATED ALWAYS` identity columns | generated values are read-only; identity `ALWAYS` only accepts `DEFAULT` |
| rows with different column sets in one statement | split the statement or supply every column |
| an unscoped UPDATE (`Compile` throws) | call `AllRows()` to confirm a full-table update |
| `UPDATE … FROM` and updates through joins | not implemented; scope with `Where` on the target table |
| expressions outside the SELECT translator surface (`Trim()`, method calls, client evaluation) | no client-side evaluation exists, so they fail loudly before I/O |
| unsupported CLR parameter types | only `int`, `long`, `decimal`, `string`, `bool`, `Guid` and `DateTime` are supported today |
| `DELETE` | not implemented yet; the shared `RETURNING` model is ready for it |
| transaction management | kept out of this API; connections and commands are disposed per execution |

Affected-row counts come from `ExecuteNonQueryAsync`. Without `RETURNING`, the
count is the number of inserted or updated rows (an UPDATE counts the rows its
`WHERE` matched, `0` when it matched none); ignored conflicts are not counted.
