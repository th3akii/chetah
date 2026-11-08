# Reading data with SELECT — a guide

> Status: delivered · covers `db.Select<T>()` and its terminal operators
> Reference detail: [`typed-models.md`](../reference/typed-models.md),
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
row at `LIMIT 1`, and `Count` runs `SELECT COUNT(*)` over the query's joins and
filters. A `Limit`/`Offset` already on the chain still applies (a smaller
`Limit` wins; the count is clamped to it). Missing rows throw
`InvalidOperationException` in the non-`OrDefault` methods; `SingleOrDefault`
still throws when more than one row matches.

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
  null member explicitly;
- aggregates (`Sql.Sum(...)` and friends) inside a grouped query only.

Everything else throws `NotSupportedException` naming the expression — for
example `u.Name.ToLower()`, `Expression.Invoke`, or an arbitrary property getter.
There is no client-side filtering fallback. Note that `Where` takes an
expression tree, so C# patterns do not compile there: use `== null` / `!= null`
(`is null` / `is not null` are rejected by the compiler with CS8122).

Captured values and collections are snapshotted at the fluent call, so mutating
the captured variable afterwards cannot change a query you already built.

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
| `CountAsync` on a `Distinct()` query | a plain `COUNT(*)` would count rows read, not distinct values |
| `CountAsync` on a grouped query | counting groups needs a wrapped subquery; use `Select(… Sql.Count())` per group |
| aggregates outside grouped `Select`/`Having`/`OrderBy` | `SELECT COUNT(*)` from a non-grouped chain has no group to count |
| `ThenBy` without `OrderBy`, second `GroupBy`, joins after grouping/`DISTINCT`/paging | the emitted SQL would be ambiguous or invalid |
| C# not in the translator surface (`ToLower`, `Invoke`, arbitrary getters) | no client-side evaluation exists, so it fails loudly instead |

Also not implemented: writes (`INSERT`/`UPDATE`/`DELETE`), transactions,
RIGHT/FULL/CROSS joins, automatic relationship loading, and dialects other than
PostgreSQL. See [`roadmap.md`](../project/roadmap.md) for the stage each belongs
to, and [`decisions.md`](../project/decisions.md) D004/D009 for the rationale.

## Checklist for a new query

1. Start from `db.Select<T>()` and add the clauses you actually need.
2. Prefer a terminal that reads fewer rows (`AnyAsync`, `CountAsync`,
   `FirstAsync`) over `ToListAsync().First()`.
3. Inspect `Compile()` when the SQL matters, before blaming the database.
4. If something throws `NotSupportedException`, the expression is outside the
   surface — restructure the lambda rather than moving the filter into memory.
