# BasicExample

Selects all rows from a `users` table via Chetah and prints them as strongly
typed `User` records — generated from [`chetah-schema.json`](chetah-schema.json).

## Generating the model

The generated file [`Generated/Chetah.Schema.g.cs`](Generated/Chetah.Schema.g.cs)
is committed; regenerate after changing the schema (overwrites only that file).
Run from this directory (`examples/BasicExample/`):

```sh
dotnet run --project ../../src/Chetah.CodeGen -- --schema chetah-schema.json --out Generated --namespace Chetah.Generated
```

## Database setup

Requires PostgreSQL and two tables matching the schema:

```sql
CREATE TABLE users (id integer PRIMARY KEY, name text NOT NULL, age integer);
INSERT INTO users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45);

CREATE TABLE orders (id integer PRIMARY KEY, user_id integer NOT NULL, amount integer NOT NULL, note text);
```

(`age` and `note` may be `NULL`; they map to `int?` and `string?`.)

## Run

Connection details come from the environment — never committed to source control:

```sh
export CHETAH_CONNECTION_STRING="Host=localhost;Username=postgres;Password=<secret>;Database=chetah"
dotnet run --project examples/BasicExample
```

Without `CHETAH_CONNECTION_STRING` the example prints setup help, then exits.
How the pieces fit together: [`docs/guide/writing-a-schema.md`](../../docs/guide/writing-a-schema.md)
and [`docs/reference/typed-models.md`](../../docs/reference/typed-models.md).
