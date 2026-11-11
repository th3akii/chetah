# Changing rows with UPDATE — a guide

> Status: delivered · covers `db.Update<T>()`, `Set`/`SetDefault`, scoping,
> `UPDATE … FROM` joins and `RETURNING`
> Related: [`select.md`](select.md) (the `Where` and join rules used here) ·
> [`insert.md`](insert.md) · [`delete.md`](delete.md)

UPDATE uses the same generated model binding as SELECT: `T` resolves to its
table, values are parameterized, and the statement is compiled before a
connection opens. User values never become SQL text.

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

## Assignment types are checked by C#

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

## Choose which rows: `Where` or `AllRows`

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
`Select` (see [`select.md`](select.md#filter-with-a-lambda)): null-safe
comparisons, `Contains` membership snapshots and captured values folded at the
call — a local mutated afterwards cannot change the built query. Repeated
`Where` calls combine with `AND`.

## Update from other tables (`UPDATE … FROM`)

```csharp
var updated = await db.Update<Student>()
    .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
    .Set(row => row.Left.Age, row => row.Right.Value)
    .Where(row => row.Right.Value >= 90)
    .Returning(row => new { row.Left.Id, row.Left.Age, row.Right.Label })
    .ToListAsync();
```

```sql
UPDATE "students" AS "t0"
SET "age" = "t1"."value"
FROM "scores" AS "t1"
WHERE ("t1"."student_id" = "t0"."id") AND ("t1"."value" >= $1)
RETURNING "t0"."id" AS "c0", "t0"."age" AS "c1", "t1"."label" AS "c2"
```

Joins are the SELECT joins: `InnerJoin`/`LeftJoin` take `(left, right) => …`, the
row type becomes `Joined<T, TJoin>`, later calls address `row.Left` / `row.Right`,
and chained joins nest the same way (`row.Left.Left`). Every source gets a
compiler alias, so ambiguous column references cannot occur; the SET target stays
unqualified because PostgreSQL accepts only a bare target column there.

Only the target table can be assigned. `Set(row => row.Right.Value, …)` is
refused — put the joined column on the right of an assignment, in `Where`, or in
`Returning`. `ReturningAll()` returns the whole joined shape.

PostgreSQL rules this API follows:

- The first join links the target to the first source and must be `InnerJoin`.
  `UPDATE … FROM` has no outer target: a target row without a source match is
  never updated, so a target-side LEFT join cannot exist.
- A later join condition that mentions the target cannot appear in `FROM`
  (PostgreSQL rejects target references there). Chetah moves an `INNER JOIN`
  condition into `WHERE` — equivalent for inner joins — and refuses the same
  shape for a `LEFT JOIN`, where it is not equivalent. Join through the previous
  source instead, e.g. `label.StudentId == score.StudentId`.
- A target row that matches several source rows is updated **once**, using one
  arbitrary matching row's values. `AllRows()` still confirms an unfiltered
  update; with a join it means every target row that has a match.
- A left-joined row is nullable, so it cannot feed a `NOT NULL` column.

## Return the updated rows

`Returning`/`ReturningAll` work exactly as for INSERT (see
[`insert.md`](insert.md#return-the-inserted-row)) — any projection shape, the
same terminals, and an empty result when the predicate matched no row.
`RETURNING` reads the row **after** the update.

## Inspect the SQL

```csharp
var compiled = db.Update<Student>()
    .Set(s => s.Age, s => s.Age + 1)
    .Where(s => s.Id == 1)
    .Returning(s => new { s.Id, s.Age })
    .Compile();

// UPDATE "students" SET "age" = "age" + $1 WHERE "id" = $2 RETURNING "id" AS "c0", "age" AS "c1"
```

`Compile()` is pure and never opens a connection. Parameter order is SET
assignments, then join conditions (wherever they render), then WHERE, then
RETURNING.

## Row counts

`ExecuteAsync` returns the number of updated rows: the rows the `WHERE` matched,
each target row once even when several joined sources match it, and `0` when
nothing matched.

## What is refused

| Refused | Reason / use instead |
|---|---|
| an unscoped UPDATE (`Compile` throws) | call `AllRows()` to confirm a full-table update |
| a generated (computed) column, even as `DEFAULT` | Chetah treats it as read-only (PostgreSQL would accept `DEFAULT` there) |
| a `GENERATED ALWAYS AS IDENTITY` column with a value | `SetDefault` is the only form |
| `NULL`, or an expression that can produce `NULL`, into a `NOT NULL` column | use `COALESCE` or a non-null expression |
| `DEFAULT` on a `NOT NULL` column with no default | it would write `NULL` |
| the same column twice, or an unknown column | each column is assigned once |
| assigning a joined column (`Set(row => row.Right.Value, …)`) | only the target table's columns are assignable |
| `LeftJoin` as the first source | `UPDATE … FROM` cannot outer-join the target; use `InnerJoin` |
| a `LeftJoin` condition that references the target | join through the previous source instead |
| referencing a table without joining it (no implicit CROSS JOIN sources) | add the source explicitly with `InnerJoin` |
| arbitrary SQL fragments in `SET` | use `Set` and `SetDefault` |
| expressions outside the SELECT translator surface (`Trim()`, method calls, client evaluation) | no client-side evaluation exists, so they fail loudly before I/O |
| unsupported CLR parameter types | only `int`, `long`, `decimal`, `string`, `bool`, `Guid` and `DateTime` are supported today |
| transaction management | kept out of this API; connections and commands are disposed per execution |

`CHECK`, `UNIQUE` and foreign-key violations are PostgreSQL's to decide and
surface as `PostgresException`.
