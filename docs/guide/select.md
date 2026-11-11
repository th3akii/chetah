# Reading data with SELECT — a guide

> Status: delivered · covers `db.Select<T>()` and its terminal operators
> Related: [`insert.md`](insert.md) · [`update.md`](update.md) ·
> [`delete.md`](delete.md) · reference [`typed-models.md`](../reference/typed-models.md),
> [`basic-select.md`](../reference/basic-select.md)

How to query PostgreSQL through the generated models. Everything here compiles
to one parameterized statement — user values never become SQL text — and
unsupported C# expressions fail before a connection opens.

## Start a query

```csharp
using Chetah.Postgres;

var db = new PostgresDb(connectionString, ChetahSchema.Bindings);
var users = await db.Select<User>().ToListAsync();
```

`T` is the generated model. `PostgresDb` needs the generated `Bindings`
registry; without it `Select<T>()` throws with guidance. A `Select<T>()` chain
is a description of SQL clauses, so call order does not matter — `Limit(5)`
before `Where(...)` still emits `WHERE … LIMIT 5`.

## Read the shape you need

| I want | Call |
|---|---|
| every row | `ToListAsync()` |
| one row, error when absent | `FirstAsync()` |
| one row or `default` | `FirstOrDefaultAsync()` |
| exactly one row | `SingleAsync()` |
| zero or one row | `SingleOrDefaultAsync()` |
| does anything match | `AnyAsync()` |
| how many match | `CountAsync()` |

`First*` reads `LIMIT 1`, `Single*` reads `LIMIT 2`, `Any` reads one constant
row at `LIMIT 1`, and `Count` counts the complete query — `SELECT COUNT(*)`
directly when filters and joins decide the row count, otherwise over a derived
table so `DISTINCT`, `GROUP BY`/`HAVING` and `LIMIT`/`OFFSET` are counted
instead of ignored. A `Limit`/`Offset` already on the chain still applies: a
smaller `Limit` wins, and `SingleAsync` therefore checks uniqueness within the
query's resulting rows, including its pagination, rather than among all matching
table rows. Missing rows throw `InvalidOperationException` in the non-`OrDefault`
methods; `SingleOrDefault` still throws when more than one row matches.

## Filter with a lambda

```csharp
var grownUps = await db.Select<User>()
    .Where(u => u.Age >= 18 && u.Name != null)
    .ToListAsync();

var adults = await db.Select<User>().Where(u => u.Age != null).ToListAsync();
```

Accepted in `Where`:

- comparisons `==`, `!=`, `<`, `<=`, `>`, `>=` (null-safe: `u.Age == null`
  becomes `IS NULL`, and nullable comparisons never leak SQL `UNKNOWN` through
  a surrounding `!`);
- `&&`, `||`, `!`;
- nullable helpers: `HasValue`, `Value`, `??`, `?:`;
- numeric casts (rendered as SQL `CAST`, never silently dropped);
- `string.Contains`/`StartsWith`/`EndsWith` with the single-string overload
  (the pattern is a parameter, wildcards escaped);
- membership: a captured or inline collection's `Contains`, compiled to a
  parameterized `IN` — `new int?[] { 18, null }.Contains(u.Age)` handles the
  null member explicitly. `IN` compares with SQL equality, so a collection that
  carries a comparer (`HashSet<T>`, `SortedSet<T>`, the immutable sets) is
  accepted only with the default comparer for its element type: a
  `new HashSet<string>(StringComparer.OrdinalIgnoreCase)` throws
  `NotSupportedException` instead of silently matching case-sensitively, exactly
  as `Enumerable.Contains(…, comparer)` does;
- aggregates (`Sql.Sum(...)` and friends) inside a grouped query only.

Everything else throws `NotSupportedException` naming the expression — for
example `u.Name.ToLower()`, `Expression.Invoke`, or an arbitrary property getter.
There is no client-side filtering fallback. Note that `Where` takes an
expression tree, so C# patterns do not compile there: use `== null` / `!= null`
(`is null` / `is not null` are rejected by the compiler with CS8122).

Captured values and collections are snapshotted at the fluent call, so mutating
the captured variable afterwards cannot change a query you already built.

A captured `DateTime` is bound by its `Kind` and by the column it is compared with
([`writing-a-schema.md`](writing-a-schema.md#datetime-values)): a UTC instant filters
`timestamp` and `timestamptz` columns alike, and an `Unspecified` value compared with
a `timestamptz` column is refused instead of being shifted by the session zone.

## Project, order and page

```csharp
var page = await db.Select<User>()
    .Where(u => u.Age >= 18)
    .OrderByDescending(u => u.Age).ThenBy(u => u.Id)
    .Offset(20).Limit(10)
    .Select(u => new { u.Id, u.Name })
    .ToListAsync();
```

- `Select` accepts a scalar, an anonymous object, `new UserLabel(u.Id, u.Name)`
  or an object initializer, nested freely. Projected members can be used by a
  later `Where`/`OrderBy`, because they resolve back to the underlying SQL
  expressions.
- `OrderBy`/`OrderByDescending` start a new ordering; `ThenBy`/
  `ThenByDescending` append to it and throw without a preceding `OrderBy`.
- `Limit`/`Offset` reject negative counts and accept `0`; the latest call wins.
  Offset without a limit is valid.
- `Distinct()` is idempotent, and a `DISTINCT` query may only order by
  expressions it already selects.

## Join explicitly

```csharp
var joined = await db.Select<User>()
    .LeftJoin<Order>((user, order) => user.Id == order.UserId)
    .Select(row => new { UserId = row.Left.Id, OrderId = row.Right == null ? (int?)null : row.Right.Id })
    .ToListAsync();
```

The result is `Joined<T, TJoin>` with `Left`/`Right` members. `LeftJoin` makes
the right side nullable (a real `null` when no row matched); `InnerJoin` does
not, and comparing its right side to `null` is rejected. Self joins and repeated
joins are supported — source aliases are qualified for you. Joins must come
before `GroupBy`, `Distinct` and pagination.

## Group and aggregate

```csharp
var totals = await db.Select<Order>()
    .Where(o => o.Amount > 0)
    .GroupBy(o => o.UserId)
    .Having(o => Sql.Sum(o.Amount) >= 20)
    .Select(o => new { o.UserId, Count = Sql.Count(), Total = Sql.Sum(o.Amount) })
    .OrderBy(row => row.UserId)
    .ToListAsync();
```

`Sql.Count/Sum/Average/Min/Max` are expression markers, legal only in the
`Select`, `Having` and `OrderBy` of a grouped query. Result kinds: `Count` →
`long`; `Sum(int)` → `long?`; `Sum(bigint)`/`Average` → `decimal?`;
`Min`/`Max` keep the argument's type, including nullability. Group keys must
reference source columns or compositions of them — constants and whole entities
are rejected.

## Inspect the SQL without executing

```csharp
var compiled = db.Select<User>().Where(u => u.Name.StartsWith("A")).Compile();
Console.WriteLine(compiled.Sql);        // SELECT … WHERE "t0"."name" LIKE $1 ESCAPE '\'
foreach (var parameter in compiled.Parameters)
    Console.WriteLine($"{parameter.ClrType.Name} {parameter.Value}");
```

`Compile()` is pure and never opens a connection, which is why tests assert
exact SQL and parameter order. `QueryAsync(query, mapper)` remains available for
ad-hoc queries that do not come from the typed chain.

## What is refused, and why

| Refused | Reason / use instead |
|---|---|
| aggregates outside grouped `Select`/`Having`/`OrderBy` | a non-grouped chain has no group to aggregate |
| `ThenBy` without `OrderBy`, second `GroupBy`, joins after grouping/`DISTINCT`/paging | the emitted SQL would be ambiguous or invalid |
| C# not in the translator surface (`ToLower`, `Invoke`, arbitrary getters) | no client-side evaluation exists, so it fails loudly instead |

`CountAsync` refuses nothing: it counts whatever the chain yields, including
`DISTINCT`, groups surviving `HAVING`, and a page. Ordering never reaches the
count statement — it cannot change how many rows a query returns — but it is
still validated, so an invalid chain fails here exactly as it would on `ToListAsync`.

Also not implemented: transactions, RIGHT/FULL/CROSS joins,
automatic relationship loading, and dialects other than PostgreSQL. Writes have
their own guides — [`insert.md`](insert.md), [`update.md`](update.md) and
[`delete.md`](delete.md); UPDATE and DELETE reuse this guide's `Where` and join
rules. See [`roadmap.md`](../project/roadmap.md) for the stage each belongs to,
and [`decisions.md`](../project/decisions.md) D004/D009/D011/D012/D013 for the
rationale.

## Checklist for a new query

1. Start from `db.Select<T>()` and add the clauses you actually need.
2. Prefer a terminal that reads fewer rows (`AnyAsync`, `CountAsync`,
   `FirstAsync`) over `ToListAsync().First()`.
3. Inspect `Compile()` when the SQL matters, before blaming the database.
4. If something throws `NotSupportedException`, the expression is outside the
   surface — restructure the lambda rather than moving the filter into memory.
