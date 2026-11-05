# BasicExample

Selects all rows from a `users` table via MyOrm and prints them as strongly
typed `User` records — generated from [`myorm-schema.json`](myorm-schema.json).

## Generating the model

The generated file [`Generated/MyOrm.Schema.g.cs`](Generated/MyOrm.Schema.g.cs)
is committed; regenerate after changing the schema (overwrites only that file).
Run from this directory (`examples/BasicExample/`):

```sh
dotnet run --project ../../src/MyOrm.CodeGen -- --schema myorm-schema.json --out Generated --namespace MyOrm.Generated
```

## Database setup

Requires PostgreSQL and two tables matching the schema:

```sql
CREATE TABLE users (id integer PRIMARY KEY, name text NOT NULL, age integer);
INSERT INTO users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45);

CREATE TABLE manual_user_test (id uuid PRIMARY KEY, name text NOT NULL, adress text);
INSERT INTO manual_user_test (id, name, adress) VALUES
    ('e6b1c8a0-2f1e-4a1d-9c2f-8a5f4e1d2b3c', 'Jane Manual', NULL),
    ('f8a2d9b1-3e2f-4b2e-8d3a-9b6e5f2c3d4a', 'Bob Sample', 'Unter den Linden 1');
```

(`age` and `adress` may be `NULL`; they map to `int?` and `string?`.)

## Run

Connection details come from the environment — never committed to source control:

```sh
export MYORM_CONNECTION_STRING="Host=localhost;Username=postgres;Password=<secret>;Database=myorm"
dotnet run --project examples/BasicExample
```

Without `MYORM_CONNECTION_STRING` the example prints setup help, then exits.
How the pieces fit together: [`docs/TYPED_MODELS.md`](../../docs/TYPED_MODELS.md).
