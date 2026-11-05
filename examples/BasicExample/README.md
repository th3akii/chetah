# BasicExample

Selects all rows from a `users` table via MyOrm and prints them as `User` records.

## Database setup

Requires PostgreSQL and a `users` table:

```sql
CREATE TABLE users (id integer PRIMARY KEY, name text NOT NULL, age integer NOT NULL);
INSERT INTO users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45);
```

## Run

Connection details come from the environment — never committed to source control:

```sh
export MYORM_CONNECTION_STRING="Host=localhost;Username=postgres;Password=<secret>;Database=myorm"
dotnet run --project examples/BasicExample
```

Without `MYORM_CONNECTION_STRING` the example prints the generated SQL and setup help, then exits.
