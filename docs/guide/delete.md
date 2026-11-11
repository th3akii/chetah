# Removing rows with DELETE — a guide

> Status: delivered · covers `db.Delete<T>()`, scoping, `DELETE … USING` joins
> and `RETURNING`
> Related: [`select.md`](select.md) (the `Where` and join rules used here) ·
> [`insert.md`](insert.md) · [`update.md`](update.md)

DELETE uses the same generated model binding as SELECT: `T` resolves to its
table, values are parameterized, and the statement is compiled before a
connection opens. There is nothing to assign — a DELETE is a target table, a
predicate, optional joined sources and an optional `RETURNING`.

## Delete rows

```csharp
var deleted = await db.Delete<Student>()
    .Where(s => s.Id == 1)
    .ExecuteAsync();                            // deleted row count

var removed = await db.Delete<Student>()
    .Where(s => s.Age < 18)
    .Returning(s => new { s.Id, s.FirstName })
    .ToListAsync();                             // one row per deleted row
```

```sql
DELETE FROM "students" WHERE "id" = $1
```

## Choose which rows: `Where` or `AllRows`

A DELETE with no `WHERE` removes every row, so it has to be requested
explicitly:

```csharp
var purged = await db.Delete<Student>()
    .AllRows()
    .ExecuteAsync();                            // every row removed
```

`AllRows()` and `Where()` are mutually exclusive, and `Compile()` (or any
terminal, `Returning` included) throws when neither was chosen. `Where` takes
the same lambda as `Select` (see [`select.md`](select.md#filter-with-a-lambda)):
null-safe comparisons, membership snapshots, and captured values folded at the
call. Repeated `Where` calls combine with `AND`.

## Delete using other tables (`DELETE … USING`)

```csharp
var purged = await db.Delete<Student>()
    .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
    .Where(row => row.Right.Value < 50)
    .Returning(row => new { row.Left.Id, row.Right.Label })
    .ToListAsync();
```

```sql
DELETE FROM "students" AS "t0"
USING "scores" AS "t1"
WHERE ("t1"."student_id" = "t0"."id") AND ("t1"."value" < $1)
RETURNING "t0"."id" AS "c0", "t1"."label" AS "c1"
```

Joins are the SELECT joins: `InnerJoin`/`LeftJoin` take `(left, right) => …`, the
row type becomes `Joined<T, TJoin>`, chained joins nest the same way
(`row.Left.Left`), and every source gets a compiler alias, so self joins work.
Joined columns are usable in `Where` and `Returning`; only target rows
(`row.Left…` at the root) are deleted, never joined rows.

Delete rows that have **no** match by chaining a `LeftJoin` and testing absence:

```csharp
var orphans = await db.Delete<Student>()
    .InnerJoin<Score>((student, score) => score.StudentId == student.Id)
    .LeftJoin<Label>((row, label) => label.StudentId == row.Right.StudentId)
    .Where(row => row.Right == null)            // no label for this score
    .ExecuteAsync();
```

PostgreSQL rules this API follows:

- The first join links the target to the first source and must be `InnerJoin`.
  `DELETE … USING` has no outer target: a target row without a source match is
  never deleted, so a target-side LEFT join cannot exist.
- A later join condition that mentions the target cannot appear in `USING`
  (PostgreSQL rejects target references there). Chetah moves an `INNER JOIN`
  condition into `WHERE` — equivalent for inner joins — and refuses the same
  shape for a `LEFT JOIN`, where it is not equivalent. Join through the previous
  source instead, e.g. `label.StudentId == score.StudentId`.
- Each target row is deleted **once**, even when several source rows match it.
  The affected count is deleted target rows, and `RETURNING` yields one row per
  deleted target row, carrying one arbitrary matching source row.
- `AllRows()` still confirms an unfiltered delete; with a join it means every
  target row that has a match.
- A left-joined row is nullable, in `Where` and in `Returning` alike:
  `row.Right == null` tests that no source row matched.

## Return the deleted rows

`Returning`/`ReturningAll` work exactly as for INSERT (see
[`insert.md`](insert.md#return-the-inserted-row)) and read the rows as they were
before deletion. The terminals judge the returned row count **after** the
statement ran: `SingleAsync` on a delete that removed two rows throws, but both
rows are already gone. Scope the `Where` first when you rely on a terminal's
count check.

## Inspect the SQL

```csharp
var compiled = db.Delete<Student>()
    .Where(s => s.Id == 1)
    .Returning(s => new { s.Id })
    .Compile();

// DELETE FROM "students" WHERE "id" = $1 RETURNING "id" AS "c0"
```

`Compile()` is pure and never opens a connection. Parameter order is the `USING`
join conditions, then WHERE (the first join's condition, any moved conditions,
then the predicate), then RETURNING.

## Row counts

`ExecuteAsync` returns the number of deleted rows — each target row once, even
when several joined sources match it — and `0` when nothing matched.

## What is refused

| Refused | Reason / use instead |
|---|---|
| an unscoped DELETE (`Compile` throws) | call `AllRows()` to confirm a full-table delete |
| `LeftJoin` as the first source | `DELETE … USING` cannot outer-join the target; use `InnerJoin` |
| a `LeftJoin` condition that references the target | join through the previous source instead |
| referencing a table without joining it (no implicit CROSS JOIN sources) | add the source explicitly with `InnerJoin` |
| aggregates in `Where` or `Returning` | a DELETE has no groups |
| expressions outside the SELECT translator surface (`Trim()`, method calls, client evaluation) | no client-side evaluation exists, so they fail loudly before I/O |
| `TRUNCATE`, row-lock clauses, transaction management | not part of this API |

Foreign-key violations (deleting a row other rows still reference without
`ON DELETE CASCADE`) are PostgreSQL's to decide and surface as
`PostgresException`; nothing is deleted.
