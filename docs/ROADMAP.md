# MyOrm — Development Roadmap

> **Status:** Initial draft
> **Companion document:** [`SPEC.md`](SPEC.md)

## 1. Purpose and How to Read This

This roadmap sketches a direction, not a commitment. It exists to keep the next small step visible and to separate what has been agreed from what is merely a candidate.

- Milestones are **checkpoints**, not schedules. No dates, no version numbers, no promised delivery order.
- Only **Milestone 1 (MVP)** is currently in scope. Everything after it is an unapproved candidate and must not be treated as a plan.
- Ordering after the MVP is a default suggestion. Real usage of the MVP decides what comes next.
- Each milestone is only "done" when it works end to end and is covered by the verification approach described in the specification.
- Implementation shape — APIs, schema syntax, project layout, tooling — is deliberately left open. Choosing it belongs to the milestone that needs it, and significant choices should be recorded in [`DECISIONS.md`](DECISIONS.md).

## 2. Status Legend

| Status | Meaning |
| --- | --- |
| **In progress** | Being implemented now. |
| **Next** | Agreed as the immediate follow-up once the current milestone exits. |
| **Candidate** | Worth considering, not approved, not started. |
| **Undecided** | Depends on a decision that has not been made. |
| **Parked** | Deliberately set aside; revisit only with a concrete reason. |

## 3. Milestone 1 — MVP: Basic SELECT

**Status:** In progress
**Goal:** Prove the full path from a minimal C# query description to a real PostgreSQL result set.

**Deliverables**

- A query description limited to a table and an unfiltered `SELECT`.
- PostgreSQL SQL generation with correctly handled identifiers.
- Execution against a local PostgreSQL instance.
- A small example application that consumes the library and inspects real rows.
- Unit coverage for SQL generation plus at least one integration test against a real database.
- Credentials supplied by configuration or environment, never committed.

**Exit criteria:** the acceptance criteria in [`SPEC.md` §5](SPEC.md#5-initial-milestone--basic-select) all hold, and the query API has been exercised by the example rather than only by tests.

**Explicit non-goals:** filtering, joins, ordering, pagination, aggregation, writes, code generation, migrations, SQL Server, stable public API, packaging.

**Follow-up to this milestone:** review API ergonomics and decide the next milestone from observed friction, not from this document.

## 4. Post-MVP Candidates

Nothing in this section is approved. Each stage lists the question that must be answered before it starts.

### Stage 2 — Query Refinement

**Status:** Candidate
**Goal:** Make reads useful for realistic queries.

Roughly: filtering conditions, ordering, pagination, and column projection. Value parameters are already a design principle; this stage is where their handling is proven at scale.

**Gating question:** does the MVP query description still feel right when conditions are added, or does it need reshaping first?

### Stage 3 — Write Operations

**Status:** Candidate
**Goal:** Support `INSERT`, `UPDATE`, and `DELETE` with predictable, safe behavior.

Roughly: single-row and multi-row inserts, updates and deletes with mandatory scoping so an unrestricted modification cannot happen by accident, and returning results where the database supports it.

**Gating question:** how are writes expressed without the "safe by default" principle becoming cumbersome?

### Stage 4 — Schema Source of Truth and Typing

**Status:** Delivered for reads (Milestones 002–003: JSON schema → CLI generator → typed models → `db.Select<T>()`; see [`TYPED_MODELS.md`](TYPED_MODELS.md), [`DECISIONS.md`](DECISIONS.md) D002)
**Goal:** Describe tables and columns once, and derive both query metadata and strongly typed models from it.

This stage's open questions from the original draft were answered in Milestone 002:
the format is JSON, the mechanism is the `MyOrm.CodeGen` CLI, and the sync
discipline is "regenerate the single `MyOrm.Schema.g.cs`" (rationale in
[`DECISIONS.md`](DECISIONS.md) D002). What remains open: whether the generated
shape still fits once `Where`/`Insert` chains consume the typed columns.

**Gating question:** consumed for D001 — does `T` → generated table metadata
resolution hold up as the query chain grows?

### Stage 5 — Relationships and Joins

**Status:** Candidate
**Goal:** Express joins and related data without hiding the underlying SQL.

**Gating question:** how much relationship modeling is warranted before it starts dictating the schema design?

### Stage 6 — Additional Database Dialect

**Status:** Candidate
**Goal:** Prove that query representation, compilation, and execution are genuinely separated by adding a second dialect.

**Gating question:** is the current boundary between shared concepts and dialect-specific compilation actually clean, or only nominally so?

### Stage 7 — Schema Tooling and Migrations

**Status:** Undecided
**Goal:** Optional tooling for applying schema changes.

Deliberately conditional: it is included only if the project reaches a point where manual schema management is a real cost. Migrations may never be built, and that is an acceptable outcome.

### Stage 7.5 — Constraint Metadata (noted by author, not started)

**Status:** Candidate
**Goal:** Carry keys and constraints — primary keys, foreign keys, unique, and other column constraints — in the schema so generated metadata can express them (needed before `Where`/`Insert` can reason about keys).

**Gating question:** which constraint set is actually needed by the next consuming milestone rather than ambitious completeness?

### Stage 7.6 — Schema Format Ergonomics (noted by author, not started)

**Status:** Candidate
**Goal:** Reduce the verbosity of the JSON schema (repeated `"name": …` keys per column). Possible shapes: a column shorthand (`"columns": ["id:integer!", "name:text!"]`), or a different format — decided when actual friction demands it.

**Gating question:** does the verbose form actually pain-worth-shipping the next N schemas, or is a shorthand justified by real usage?

### Stage 8 — Distribution and Documentation

**Status:** Candidate
**Goal:** Make the library usable by others.

Roughly: documentation, examples, packaging and release mechanics, contribution guidance, and licensing. Some of this is cheap to start early; none of it should block the technical milestones.

## 5. Parked

- Performance work beyond avoiding obviously wasteful behavior.
- Caching layers.
- Advanced PostgreSQL-specific features that are not needed by current users.
- Anything that exists only to make the library look complete.

Revisit a parked item only when a concrete requirement or observed problem justifies it.

## 6. Working Rules Across Milestones

- One vertical slice at a time: working, tested, demonstrated end to end before broadening.
- Prefer deleting an experiment over keeping an abstraction that has one caller.
- Breaking changes are acceptable while the project is experimental; keep them documented.
- Record significant decisions with their rationale in [`DECISIONS.md`](DECISIONS.md) when they are made — not in advance.
- Re-read this roadmap after each milestone and rewrite the next section rather than defending it.
