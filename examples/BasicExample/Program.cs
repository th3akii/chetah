using MyOrm.Generated;
using MyOrm.Postgres;

var connectionString = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
if (connectionString is null)
{
    Console.WriteLine("Set MYORM_CONNECTION_STRING to run against PostgreSQL, e.g.:");
    Console.WriteLine("  Host=localhost;Username=postgres;Password=myorm;Database=myorm");
    return;
}

var db = new PostgresDb(connectionString, MyOrmSchema.Bindings);
if (args.Contains("--stage2-demo", StringComparer.Ordinal))
{
    Console.WriteLine("Filtered users");
    var filtered = await db.Select<User>().Where(user => user.Age >= 18).OrderBy(user => user.Id)
        .Select(user => new { user.Id, user.Name }).ToListAsync();
    foreach (var user in filtered)
        Console.WriteLine($"{user.Id} {user.Name}");

    Console.WriteLine("User orders");
    var joined = await db.Select<User>().LeftJoin<Order>((user, order) => user.Id == order.UserId)
        .OrderBy(row => row.Left.Id).ThenBy(row => (int?)row.Right!.Id)
        .Select(row => new { UserId = row.Left.Id, OrderId = (int?)row.Right!.Id }).ToListAsync();
    foreach (var row in joined)
        Console.WriteLine($"{row.UserId} {row.OrderId?.ToString() ?? "NULL"}");

    Console.WriteLine("Order totals");
    var totals = await db.Select<Order>().GroupBy(order => order.UserId)
        .Having(order => Sql.Sum(order.Amount) >= 20)
        .Select(order => new { order.UserId, Count = Sql.Count(), Total = Sql.Sum(order.Amount) })
        .OrderBy(row => row.UserId).ToListAsync();
    foreach (var row in totals)
        Console.WriteLine($"{row.UserId} {row.Count} {row.Total}");
    return;
}

var users = await db.Select<User>().ToListAsync();

Console.WriteLine($"Fetched {users.Count} User record(s):");
foreach (var user in users)
    Console.WriteLine($"  {user.Id}  {user.Name}  ({user.Age?.ToString() ?? "no age"})");

var manualUsers = await db.Select<ManualUsers>().ToListAsync();

Console.WriteLine($"Fetched {manualUsers.Count} ManualUsers record(s):");
foreach (var u in manualUsers)
    Console.WriteLine($"  {u.Id}  {u.Name}  ({u.Adress ?? "no address"})");
