using Chetah;
using Chetah.Postgres;
using Npgsql;

namespace Chetah.CodeGen.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")))
            Skip = "Requires CHETAH_CONNECTION_STRING targeting a disposable PostgreSQL database.";
    }
}

public class Stage2LiveTests
{
    [PostgresFact]
    public async Task FiltersPagingProjectionAndDistinct_ReturnExpectedValues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var users = fixture.Db.Select<Stage2User>();
        var page = await users.Where(user => user.Age >= 18).OrderByDescending(user => user.Age).ThenBy(user => user.Id)
            .Offset(1).Limit(1).Select(user => new { user.Id, user.Name }).ToListAsync();
        Assert.Equal(1, Assert.Single(page).Id);
        Assert.Equal("Ada", page[0].Name);
        Assert.Empty(await users.Limit(0).ToListAsync());
        Assert.Equal(new[] { 2, 3, 4 }, (await users.OrderBy(user => user.Id).Offset(1).Select(user => user.Id).ToListAsync()));
        Assert.Equal(new[] { "Ada", "Grace", "Linus" }, await users.Select(user => user.Name).Distinct().OrderBy(name => name).ToListAsync());
        Assert.Empty(await users.Where(user => user.Name == "' OR TRUE --").ToListAsync());
        Assert.Equal(new[] { 1, 2, 3, 4 }, await users.OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        var records = await users.Where(user => user.Id == 1).Select(user => new Stage2QueryTests.UserLabel(user.Id, user.Name)).ToListAsync();
        Assert.Equal(new Stage2QueryTests.UserLabel(1, "Ada"), Assert.Single(records));
        var dto = await users.Where(user => user.Id == 2).Select(user => new Stage2QueryTests.LabelDto { Name = user.Name }).ToListAsync();
        Assert.Equal("Grace", Assert.Single(dto).Name);
    }

    [PostgresFact]
    public async Task NullComparisonsLiteralMatchingMembershipAndCasts_MatchClrBoundaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var users = fixture.Db.Select<Stage2User>();
        Assert.Equal(new[] { 3, 4 }, await users.Where(user => !(user.Age >= 18)).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        Assert.Equal(new[] { 1, 2, 4 }, await users.Where(user => user.Age != null).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        int?[] ages = [null, 36];
        Assert.Equal(new[] { 1, 3 }, await users.Where(user => ages.Contains(user.Age)).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        Assert.Equal(new[] { 2, 4 }, await users.Where(user => !ages.Contains(user.Age)).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        int?[] empty = [];
        Assert.Empty(await users.Where(user => empty.Contains(user.Age)).ToListAsync());
        Assert.Equal(new[] { 1, 2, 3, 4 }, await users.Where(user => !empty.Contains(user.Age)).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        Assert.Equal(new long?[] { 38, 47, null, 19 }, await users.OrderBy(user => user.Id).Select(user => (long?)user.Age + 2L).ToListAsync());
        Assert.Equal(new[] { 18, 22, 0, 8 }, await users.OrderBy(user => user.Id).Select(user => (user.Age ?? 0) / 2).ToListAsync());
        // Inline membership (no captured collection) parameterizes like captured collections.
        Assert.Equal(new[] { 1, 3 }, await users.Where(user => new int?[] { 36, null, 18 }.Contains(user.Age)).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync());
        // C# null==null stays equal under null-safe rendering; null==value stays FALSE.
        Assert.Equal(2, (await fixture.Db.Select<TestWidgets>().Where(widget => widget.Label == widget.Label).ToListAsync()).Count);
        Assert.Empty(await fixture.Db.Select<TestWidgets>().Where(widget => widget.Label == "x").ToListAsync());
        await fixture.CommandAsync("INSERT INTO users VALUES (5, E'a%_\\\\b', NULL), (6, 'axb', NULL)");
        Assert.Equal(new[] { 5 }, await users.Where(user => user.Name.Contains("%_\\")).Select(user => user.Id).ToListAsync());
        Assert.Equal(new[] { 5 }, await users.Where(user => user.Name.StartsWith("a%_")).Select(user => user.Id).ToListAsync());
        Assert.Equal(new[] { 5 }, await users.Where(user => user.Name.EndsWith("\\b")).Select(user => user.Id).ToListAsync());
        Assert.Equal(new[] { 11 }, await fixture.Db.Select<Stage2Order>().Where(order => !order.Note!.Contains("a"))
            .Where(order => order.Id == 11).Select(order => order.Id).ToListAsync());
    }

    [PostgresFact]
    public async Task InnerLeftSelfAndRepeatedJoins_PreserveSourceAndOuterNulls()
    {
        await using var fixture = await Fixture.CreateAsync();
        var joined = fixture.Db.Select<Stage2User>().LeftJoin<Stage2Order>((user, order) => user.Id == order.UserId);
        var pairs = await joined.OrderBy(row => row.Left.Id).ThenBy(row => row.Right == null ? (int?)null : row.Right.Id)
            .Select(row => new { UserId = row.Left.Id, OrderId = row.Right == null ? (int?)null : row.Right.Id }).ToListAsync();
        Assert.Equal(new[] { (1, (int?)10), (1, 11), (2, 12), (2, 13), (3, null), (4, null) }, pairs.Select(row => (row.UserId, row.OrderId)));
        var inner = await fixture.Db.Select<Stage2User>().InnerJoin<Stage2Order>((user, order) => user.Id == order.UserId)
            .OrderBy(row => row.Right.Id).Select(row => new { UserId = row.Left.Id, OrderId = row.Right.Id }).ToListAsync();
        Assert.Equal(new[] { (1, 10), (1, 11), (2, 12), (2, 13) }, inner.Select(row => (row.UserId, row.OrderId)));
        var absent = await fixture.Db.Select<Stage2User>().LeftJoin<Stage2Order>((user, order) => false).OrderBy(row => row.Left.Id).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3, 4 }, absent.Select(row => row.Left.Id));
        Assert.All(absent, row => Assert.Null(row.Right));
        var nullable = await fixture.Db.Select<Stage2User>().Where(user => user.Id == 1)
            .LeftJoin<NullableRow>((user, right) => true).ToListAsync();
        var rightEntity = Assert.Single(nullable).Right;
        Assert.NotNull(rightEntity);
        Assert.Null(rightEntity.Value);
        // Suppression exposes the deliberate SQL NULL-to-nonnullable projection error, not a CLR dereference.
        var invalid = joined.Where(row => row.Left.Id == 3).Select(row => row.Right!.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ToListAsync());
        Assert.Contains("c0", error.Message);
        var self = await fixture.Db.Select<Stage2User>().InnerJoin<Stage2User>((left, right) => left.Id == right.Id)
            .InnerJoin<Stage2Order>((row, order) => row.Right.Id == order.UserId)
            .OrderBy(row => row.Right.Id).Select(row => new { row.Left.Left.Name, Id = row.Left.Right.Id, OrderId = row.Right.Id }).ToListAsync();
        Assert.Equal(new[] { ("Ada", 1, 10), ("Ada", 1, 11), ("Grace", 2, 12), ("Grace", 2, 13) }, self.Select(row => (row.Name, row.Id, row.OrderId)));
    }

    [PostgresFact]
    public async Task GroupingHavingAndAggregateTypes_ReturnExpectedValues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var orders = fixture.Db.Select<Stage2Order>();
        var users = fixture.Db.Select<Stage2User>();
        var totals = await orders.GroupBy(order => order.UserId).Having(order => Sql.Sum(order.Amount) >= 20)
            .Select(order => new { order.UserId, Count = Sql.Count(), Notes = Sql.Count(order.Note), Total = Sql.Sum(order.Amount) })
            .OrderBy(row => row.UserId).ToListAsync();
        Assert.Equal(new[] { (1, 2L, 1L, (long?)30), (2, 2L, 2L, (long?)20) }, totals.Select(row => (row.UserId, row.Count, row.Notes, row.Total)));
        var filtered = await orders.Where(order => order.Amount >= 10).GroupBy(order => order.UserId)
            .Having(order => Sql.Sum(order.Amount) >= 20).Select(order => new { order.UserId, Total = Sql.Sum(order.Amount) }).ToListAsync();
        Assert.Equal(1, Assert.Single(filtered).UserId);
        Assert.Equal(30L, filtered[0].Total);
        // Composed group key (coalesce + arithmetic with parameters) must SELECT and GROUP BY
        // with identical parameterization; decade 0 is the NULL-age row.
        var composed = await users.GroupBy(user => (user.Age ?? 0) / 10)
            .Select(user => new { Decade = (user.Age ?? 0) / 10, Count = Sql.Count() })
            .OrderBy(row => row.Decade).ToListAsync();
        Assert.Equal(new[] { (0, 1L), (1, 1L), (3, 1L), (4, 1L) }, composed.Select(row => (row.Decade, row.Count)));
        var nonZeroDecades = await users.GroupBy(user => (user.Age ?? 0) / 10)
            .Having(user => (user.Age ?? 0) / 10 >= 1)
            .Select(user => new { Decade = (user.Age ?? 0) / 10, Count = Sql.Count() })
            .OrderBy(row => row.Decade).ToListAsync();
        Assert.Equal(new[] { (1, 1L), (3, 1L), (4, 1L) }, nonZeroDecades.Select(row => (row.Decade, row.Count)));
        // Membership and equality keys group on shared SQL across SELECT/GROUP BY/ORDER BY.
        var orderIds = new[] { 10, 12 };
        var hits = await orders.GroupBy(order => orderIds.Contains(order.Id))
            .Select(order => new { Hit = orderIds.Contains(order.Id), Count = Sql.Count() })
            .OrderBy(row => row.Hit).ToListAsync();
        Assert.Equal(new[] { (false, 2L), (true, 2L) }, hits.Select(row => (row.Hit, row.Count)));
        var grown = await users.GroupBy(user => user.Age != null)
            .Select(user => new { Grown = user.Age != null, Count = Sql.Count() })
            .OrderBy(row => row.Grown).ToListAsync();
        Assert.Equal(new[] { (false, 1L), (true, 3L) }, grown.Select(row => (row.Grown, row.Count)));
        var multi = await orders.GroupBy(order => new { order.UserId, Present = order.Note != null })
            .Select(order => new { order.UserId, Present = order.Note != null, Count = Sql.Count() })
            .OrderBy(row => row.UserId).ThenBy(row => row.Present).ToListAsync();
        Assert.Equal(new[] { (1, false, 1L), (1, true, 1L), (2, true, 2L) }, multi.Select(row => (row.UserId, row.Present, row.Count)));
        var nullSum = await fixture.Db.Select<Stage2User>().Where(user => user.Id == 3).GroupBy(user => user.Name)
            .Select(user => new { user.Name, Total = Sql.Sum(user.Age) }).ToListAsync();
        Assert.Null(Assert.Single(nullSum).Total);
        var wide = await fixture.Db.Select<TestWidgets>().GroupBy(widget => widget.Active)
            .Select(widget => new { Total = Sql.Sum(widget.Quantity), Average = Sql.Average(widget.Quantity), Count = Sql.Count(widget.Label) }).ToListAsync();
        Assert.Equal(9000000000m, Assert.Single(wide).Total);
        Assert.Equal(4500000000m, wide[0].Average);
        Assert.Equal(0L, wide[0].Count);
        // Provider parameter mappings: timestamp equality, bool member filter.
        var expectedCreated = new DateTime(2020, 1, 2);
        Assert.Equal(new[] { 1, 2 }, (await fixture.Db.Select<TestWidgets>().Where(widget => widget.Created == expectedCreated)
            .OrderBy(widget => widget.Id).Select(widget => widget.Id).ToListAsync()));
        Assert.Equal(new[] { 1, 2 }, (await fixture.Db.Select<TestWidgets>().Where(widget => widget.Active)
            .OrderBy(widget => widget.Id).Select(widget => widget.Id).ToListAsync()));
        // Null-safe inequality: a non-null captured Guid distincts from NULL-token rows.
        var token = Guid.NewGuid();
        Assert.Equal(2, (await fixture.Db.Select<TestWidgets>().Where(widget => widget.Token != token).ToListAsync()).Count);
        // Equality-derived boolean key groups live: SELECT and GROUP BY render the key identically.
        var tokenGrouped = await fixture.Db.Select<TestWidgets>().GroupBy(widget => widget.Token == token)
            .Select(widget => new { Known = widget.Token == token, Count = Sql.Count() }).ToListAsync();
        var keyed = Assert.Single(tokenGrouped);
        Assert.False(keyed.Known);
        Assert.Equal(2L, keyed.Count);
        // Nullable .Value unwrap projects conditionally (no CS8629); static fields are not
        // foldable by design, so the else branch uses a captured local Guid instead.
        var emptyGuid = Guid.Empty;
        Assert.Empty(await fixture.Db.Select<TestWidgets>().Where(widget => widget.Token.HasValue)
            .Select(widget => widget.Token.HasValue ? widget.Token.Value : emptyGuid).ToListAsync());
        // MIN/MAX preserve the argument's nullability: age is int? (group 3 maps NULL),
        // id/amount are non-nullable ints and max() keeps their bare kind.
        var extremes = await users.GroupBy(user => user.Id)
            .Select(user => new { user.Id, Oldest = Sql.Min(user.Age), Biggest = Sql.Max(user.Id) })
            .OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(new[] { (1, (int?)36, 1), (2, (int?)45, 2), (3, (int?)null, 3), (4, (int?)17, 4) },
            extremes.Select(row => (row.Id, row.Oldest, row.Biggest)));
        var kinded = await orders.GroupBy(order => order.UserId)
            .Select(order => new { order.UserId, Smallest = Sql.Min(order.Id), Largest = Sql.Max(order.Amount) })
            .OrderBy(row => row.UserId).ToListAsync();
        Assert.Equal(new[] { (1, 10, 20), (2, 12, 15) }, kinded.Select(row => (row.UserId, row.Smallest, row.Largest)));
        var aggregateOrder = await orders.GroupBy(order => order.UserId).Select(order => new { order.UserId, Total = Sql.Sum(order.Amount) })
            .OrderByDescending(row => row.Total).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, aggregateOrder.Select(row => row.UserId));
    }

    [PostgresFact]
    public async Task GeneratedMapAt_UsesOrdinalOffsetAndProviderTypes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 999 AS ignored, id, label, quantity, active, token, created FROM test_widgets ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var widget = TestWidgetsTable.MapAt(reader, 1);
        Assert.Equal(new TestWidgets(1, null, 4000000000L, true, null, new DateTime(2020, 1, 2)), widget);
    }

    public sealed record NullableRow(string? Value);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _schema = "chetah_stage2_" + Guid.NewGuid().ToString("N");
        internal string ConnectionString { get; }
        internal PostgresDb Db { get; }

        private Fixture(string root)
        {
            _root = root;
            ConnectionString = new NpgsqlConnectionStringBuilder(root) { SearchPath = _schema }.ConnectionString;
            var bindings = new Dictionary<Type, ITableBinding>(ChetahSchema.Bindings)
            {
                [typeof(NullableRow)] = new TableBinding<NullableRow>(new Table("nullable_rows", "value"),
                    reader => new(reader.IsDBNull(reader.GetOrdinal("value")) ? null : reader.GetString(reader.GetOrdinal("value"))),
                    (reader, offset) => new(reader.IsDBNull(offset) ? null : reader.GetString(offset)),
                    new Dictionary<string, string> { [nameof(NullableRow.Value)] = "value" })
            };
            Db = new PostgresDb(ConnectionString, bindings);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("CHETAH_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Disposable PostgreSQL connection is required.");
            var fixture = new Fixture(root);
            await using var connection = new NpgsqlConnection(root);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{fixture._schema}\"", connection);
            await create.ExecuteNonQueryAsync();
            try
            {
                await fixture.CommandAsync("""
                    CREATE TABLE users (id int NOT NULL, name text NOT NULL, age int);
                    INSERT INTO users VALUES (1,'Ada',36),(2,'Grace',45),(3,'Linus',NULL),(4,'Ada',17);
                    CREATE TABLE orders (id int NOT NULL, user_id int NOT NULL, amount int NOT NULL, note text);
                    INSERT INTO orders VALUES (10,1,10,'a'),(11,1,20,NULL),(12,2,15,'b'),(13,2,5,'c');
                    CREATE TABLE nullable_rows (value text);
                    INSERT INTO nullable_rows VALUES (NULL);
                    CREATE TABLE test_widgets (id int NOT NULL, label text, quantity bigint NOT NULL, active bool NOT NULL, token uuid, created timestamp NOT NULL);
                    INSERT INTO test_widgets VALUES (1,NULL,4000000000,TRUE,NULL,'2020-01-02'),(2,NULL,5000000000,TRUE,NULL,'2020-01-02');
                    """);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async Task CommandAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_root);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
