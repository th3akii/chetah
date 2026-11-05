using MyOrm.Generated;
using MyOrm.Postgres;

var connectionString = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
if (connectionString is null)
{
    Console.WriteLine("Set MYORM_CONNECTION_STRING to run against PostgreSQL, e.g.:");
    Console.WriteLine("  Host=localhost;Username=postgres;Password=myorm;Database=myorm");
    return;
}

var db = new PostgresDb(connectionString);
var users = await db.QueryAsync(UsersTable.Table.SelectAll(), UsersTable.Map);

Console.WriteLine($"Fetched {users.Count} User record(s):");
foreach (var user in users)
    Console.WriteLine($"  {user.Id}  {user.Name}  ({user.Age?.ToString() ?? "no age"})");

var manualUsers = await db.QueryAsync(ManualUserTestTable.Table.SelectAll(), ManualUserTestTable.Map);

Console.WriteLine($"Fetched {manualUsers.Count} ManualUsers record(s):");
foreach (var u in manualUsers)
    Console.WriteLine($"  {u.Id}  {u.Name}  ({u.Adress ?? "no address"})");
