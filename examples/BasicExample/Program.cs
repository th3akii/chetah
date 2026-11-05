using MyOrm;
using MyOrm.Postgres;

var users = new Table("users", "id", "name", "age");
var query = users.SelectAll();

Console.WriteLine("Generated SQL:");
Console.WriteLine("  " + PostgresCompiler.ToSql(query));

var connectionString = Environment.GetEnvironmentVariable("MYORM_CONNECTION_STRING");
if (connectionString is null)
{
    Console.WriteLine();
    Console.WriteLine("Set MYORM_CONNECTION_STRING to run against PostgreSQL, e.g.:");
    Console.WriteLine("  Host=localhost;Username=postgres;Password=myorm;Database=myorm");
    return;
}

var db = new PostgresDb(connectionString);
var rows = await db.QueryAsync(query, r => new User(
    r.GetInt32(r.GetOrdinal("id")),
    r.GetString(r.GetOrdinal("name")),
    r.GetInt32(r.GetOrdinal("age"))));

Console.WriteLine($"Fetched {rows.Count} row(s):");
foreach (var user in rows)
    Console.WriteLine($"  {user.Id}  {user.Name}  ({user.Age})");

public record User(int Id, string Name, int Age);
