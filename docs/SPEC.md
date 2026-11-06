# Chetah — Project Specification

> **Status:** Initial draft  
> **Project name:** Chetah  
> **Primary language:** C# / .NET  
> **Initial database:** PostgreSQL

## 1. Overview

Chetah is an experimental, lightweight, SQL-first data-access library for .NET, inspired by the developer experience of Drizzle ORM.

The goal is to offer a clear, predictable way to describe database structures, construct queries, and work with strongly typed C# models without hiding SQL behavior behind unnecessary abstraction.

The project begins as a personal learning and school-use project, with the possibility of becoming an open-source library. Its public API, schema definition syntax, and internal architecture are expected to evolve through experimentation.

## 2. Goals

- Provide an ergonomic C# API for common database operations.
- Favor explicitness, readable queries, and predictable SQL output.
- Support strong typing wherever practical, including models derived from schema definitions in a future iteration.
- Keep SQL generation separate from database execution.
- Start with PostgreSQL while leaving room for a Microsoft SQL Server dialect later.
- Make the library reusable from other compatible .NET projects, with eventual NuGet packaging.
- Keep the implementation modular, testable, and approachable for future contributors.

## 3. Design Principles

1. **SQL-first:** Generated SQL should be understandable and inspectable.
2. **Explicit over implicit:** Avoid surprising conventions and hidden behavior.
3. **Type safety where practical:** Catch mistakes at compile time when the design allows it, without claiming guarantees that have not been implemented.
4. **Safe by default:** Parameterize data values; never concatenate untrusted values into SQL. Validate or safely quote identifiers. Avoid accidental unrestricted data modifications.
5. **Separation of concerns:** Query representation, SQL dialect compilation, and database execution should have distinct responsibilities.
6. **Incremental development:** Implement and test the smallest useful capability before expanding scope.
7. **API flexibility:** Do not commit prematurely to a particular schema syntax or query DSL.

## 4. Intended Capabilities (Long-Term Direction)

These are aspirations, **not requirements for the first milestone**:

- Explicit table and column definitions.
- Generated, strongly typed C# models from a single schema source of truth.
- Query building for `SELECT`, `INSERT`, `UPDATE`, and `DELETE`.
- Filtering, ordering, pagination, joins, and relationships.
- Parameterized SQL generation and database execution.
- PostgreSQL support, followed by consideration of SQL Server.
- Optional schema tooling and migrations if they prove useful.
- Reusable class libraries, developer documentation, examples, and potential NuGet distribution.

The schema representation might be C#, JSON, or another format. Model generation might use a CLI or a source generator. **Neither choice is decided yet.**

## 5. Initial Milestone — Basic SELECT

### Objective

Prove the end-to-end path from a minimal C# query description to a real PostgreSQL result set.

### In Scope

- Represent a database table and a simple `SELECT` query with minimal metadata.
- Generate valid PostgreSQL SQL for an unfiltered `SELECT`.
- Execute the generated query against a local PostgreSQL instance using Npgsql.
- Return and inspect the query results in a small example application.
- Include focused unit tests for SQL generation and at least one integration test against PostgreSQL.
- Keep the query API intentionally small and subject to revision.

### Out of Scope

- `WHERE`, joins, sorting, pagination, and aggregations.
- `INSERT`, `UPDATE`, and `DELETE`.
- Automatic model or schema code generation.
- Migrations and relationship management.
- SQL Server support.
- A stable public API or NuGet release.

### Acceptance Criteria

1. The solution builds successfully.
2. A sample project can reference the library and express a basic `SELECT` without hand-writing the complete SQL statement.
3. The PostgreSQL compiler produces valid SQL with correctly handled identifiers.
4. The sample executes the query against a local PostgreSQL database and reads expected rows.
5. Automated tests cover basic compilation behavior and the database round trip.
6. Database credentials are provided through configuration or environment variables, never committed to source control.

## 6. Architectural Boundaries

The initial solution is organized around these responsibilities:

- **Chetah.Core:** Shared query concepts and public abstractions.
- **Chetah.Postgres:** PostgreSQL-specific SQL compilation and database access.
- **Tests:** Unit and integration coverage.
- **BasicExample:** A minimal executable consumer of the library.

Additional projects, such as a generator or CLI, may be introduced only when the relevant feature is being implemented. Project names and boundaries may change if experience justifies it.

## 7. Deferred Decisions

The following are deliberately unresolved:

- Final project and package names.
- Exact syntax for defining tables and columns.
- Whether schema definitions live in C#, JSON, or another format.
- How generated model classes are produced and updated.
- Exact fluent query API and result-mapping conventions.
- Supported .NET target frameworks and compatibility policy.
- Migration strategy, if any.
- SQL Server dialect and provider integration.

Record important decisions and their rationale as they arise; do not decide them merely to complete a roadmap.

## 8. Development Approach

- Build vertically: one small feature working end to end before broadening the API.
- Use AI-assisted implementation, with tests and review of generated changes.
- Prefer observable behavior and executable examples over speculative abstractions.
- Maintain documentation alongside actual implementation.
- Treat breaking changes as acceptable during early experimental versions.
- Consider open-source contribution guidelines and licensing before public distribution.

## 9. Immediate Next Step

Implement **Basic SELECT** against a local PostgreSQL database. Once that works reliably, evaluate the API ergonomics and decide which feature to implement next based on actual needs.
