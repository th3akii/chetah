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
var users = await db.Select<User>().ToListAsync();

Console.WriteLine($"Fetched {users.Count} User record(s):");
foreach (var user in users)
    Console.WriteLine($"  {user.Id}  {user.Name}  ({user.Age?.ToString() ?? "no age"})");

var manualUsers = await db.Select<ManualUsers>().ToListAsync();

Console.WriteLine($"Fetched {manualUsers.Count} ManualUsers record(s):");
foreach (var u in manualUsers)
    Console.WriteLine($"  {u.Id}  {u.Name}  ({u.Adress ?? "no address"})");
