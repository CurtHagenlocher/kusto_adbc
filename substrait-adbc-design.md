# Substrait-Native Query Planning for ADBC

## A Proposal for Capability Negotiation, Plan Translation, and Optimization

---

## Executive Summary

Today, applications that query databases through ADBC pass SQL strings and get Arrow-formatted results back. This works, but SQL dialects vary across engines, making cross-engine query generation fragile. Substrait — an open standard for representing query plans as engine-independent relational algebra — could solve this, but significant gaps prevent its practical use in ADBC today.

This document proposes extensions to both ADBC and Substrait that would enable a new workflow:

1. **An application asks a driver**: "What can you do?" — and receives a structured capability declaration covering supported types, functions, relation operators, join flavors, and more.
2. **The application builds a Substrait plan** tailored to those capabilities.
3. **The driver translates the plan** to its native dialect (KQL, SQL, Cypher, etc.) or executes it directly.
4. **For plans that partially exceed capabilities**, the driver performs *partial pushdown* — translating what it can and returning a residual plan for the application to handle.

We have built a working proof-of-concept for Kusto (Azure Data Explorer) that demonstrates the core translation and partial pushdown mechanics. This document describes the full vision, identifies the gaps in Substrait and ADBC that need to be addressed, and proposes specific extensions to close them.

---

## Table of Contents

1. [Motivation](#1-motivation)
2. [Current State of the Art](#2-current-state-of-the-art)
3. [Proposed Architecture](#3-proposed-architecture)
4. [Capability Declaration](#4-capability-declaration-the-biggest-gap)
5. [ADBC API Extensions](#5-adbc-api-extensions)
6. [Plan Translation and Partial Pushdown](#6-plan-translation-and-partial-pushdown)
7. [Plan Optimization and Rewriting](#7-plan-optimization-and-rewriting)
8. [Substrait Gaps and Proposed Extensions](#8-substrait-gaps-and-proposed-extensions)
9. [ADBC Gaps and Proposed Extensions](#9-adbc-gaps-and-proposed-extensions)
10. [Proof of Concept: Kusto ADBC Driver](#10-proof-of-concept-kusto-adbc-driver)
11. [Open Questions](#11-open-questions)
12. [Appendix: Gap Summary Matrix](#12-appendix-gap-summary-matrix)

---

## 1. Motivation

We build data integration tools that work with dozens of data sources — relational databases, cloud analytics engines, graph databases, and more. Each has its own query dialect with different syntax, function names, type systems, and capabilities.

Today, generating queries for these engines requires per-engine SQL generation logic. This is fragile, hard to test, and results in lowest-common-denominator queries that don't exploit engine-specific optimizations.

**Substrait** offers a promising alternative: represent query plans as engine-independent relational algebra, and let each engine's driver translate to its native dialect. Combined with **ADBC** (Arrow Database Connectivity) as the transport layer, this creates a clean separation:

```
Application → Substrait Plan → ADBC Driver → Native Dialect → Engine
                                     ↑
                          Capability negotiation
```

But this workflow has critical gaps today. Neither Substrait nor ADBC provides the mechanisms needed for an application to discover what a driver supports, build conforming plans, or handle partial support gracefully.

---

## 2. Current State of the Art

### What Substrait provides today

| Feature | Status |
|---------|--------|
| Relation types (Read, Filter, Project, Join, Aggregate, Sort, Fetch, Set, Window, etc.) | ✅ Well-defined |
| Type system (i32, i64, fp64, string, timestamp, etc.) | ✅ Well-defined |
| Type variations (vendor-specific physical representations) | ✅ Defined but rarely used |
| Function extensions (YAML catalogs with signatures) | ✅ Well-defined |
| Capability declaration ("what relations/joins/features do I support?") | ❌ **Missing entirely** |
| Plan validation ("is this plan executable by consumer X?") | ❌ **Missing** |
| Capability negotiation protocol | ❌ **Missing** |

### What ADBC provides today

| Feature | Status |
|---------|--------|
| Execute SQL query | ✅ `SqlQuery` property |
| Execute Substrait plan | ✅ `SubstraitPlan` property |
| Get database metadata | ✅ `GetInfo`, `GetObjects`, `GetTableSchema` |
| Discover Substrait capabilities | ❌ **Missing** |
| Partial pushdown / plan splitting | ❌ **Missing** |
| Plan validation before execution | ❌ **Missing** |
| Plan optimization hints | ❌ **Missing** |

### The gap in pictures

Today's Substrait workflow is "send and pray":

```
Application: Here's a Substrait plan.
Driver:      [tries to execute]
Driver:      Error: unsupported window function.
Application: Which part failed? What do you support?
Driver:      ¯\_(ツ)_/¯
```

The proposed workflow is "negotiate, plan, execute":

```
Application: What Substrait features do you support?
Driver:      Here's my capability document.
Application: [builds a conforming plan]
Application: Execute this plan.
Driver:      [translates to native dialect, returns Arrow results]
```

With a fallback for partial support:

```
Application: Execute this plan (it might exceed your capabilities).
Driver:      I could translate 80% of it. Here's a residual plan
             with KQL nodes for the parts I handled.
Application: [executes residual plan, evaluating the unsupported
              parts client-side against the KQL result sets]
```

---

## 3. Proposed Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                        Application                                │
│                                                                    │
│  1. GetSubstraitCapabilities() ←──── Capability Document          │
│  2. Build Substrait plan using capabilities                       │
│  3. OptimizeSubstraitPlan(plan) ←── Optimized plan                │
│  4. ValidateSubstraitPlan(plan) ←── Validation result             │
│  5. ExecuteQuery(plan) ←──────────── Arrow result stream          │
│     OR                                                             │
│  5. PushdownSubstraitPlan(plan) ←── Residual plan + KQL nodes     │
└──────────────────────────────────────────────────────────────────┘
                              │
                    ADBC Connection API
                              │
┌──────────────────────────────────────────────────────────────────┐
│                     ADBC Driver (e.g., Kusto)                     │
│                                                                    │
│  Capability Document ───── describes what the driver supports     │
│  Plan Translator ────────── Substrait → native dialect (KQL)      │
│  Plan Optimizer ─────────── rewrite for engine-specific perf      │
│  Partial Pushdown ───────── split plan at capability boundary     │
│  Native Executor ────────── execute via engine API                │
└──────────────────────────────────────────────────────────────────┘
```

---

## 4. Capability Declaration (The Biggest Gap)

This is the most significant gap in both Substrait and ADBC today. There is no standard way for a data source to declare: "I support inner joins and left outer joins, but not lateral joins. I support `sum`, `count`, and `avg` aggregates, but not `percentile`. I can filter and project, but I can't do window functions."

### 4.1 What needs to be declared

We propose a **Substrait Capability Document** that covers these dimensions:

#### Relation capabilities

```yaml
relations:
  read:
    supported: true
    named_table: true
    virtual_table: false
    local_files: false
  filter:
    supported: true
  project:
    supported: true
  aggregate:
    supported: true
    grouping_sets: false          # CUBE, ROLLUP
    distinct_aggregates: true
  sort:
    supported: true
    multi_key: true
    null_ordering: true
  fetch:
    supported: true
    offset: true
  join:
    supported: true
    types:
      inner: true
      left: true
      right: true
      full_outer: true
      left_semi: true
      left_anti: true
      cross: false
      lateral: false
  set:
    supported: false              # UNION, INTERSECT, EXCEPT
  window:
    supported: false
  exchange:
    supported: false
```

#### Function capabilities

Already partially addressed by Substrait's extension YAML files — a consumer publishes which functions it supports. Our Kusto driver already does this via `kusto_functions.yaml`.

#### Type capabilities

```yaml
types:
  supported:
    - i32
    - i64
    - fp32
    - fp64
    - string
    - boolean
    - timestamp
    - date
    - interval_day
  type_variations:
    - parent: string
      name: "kusto_dynamic"
      description: "JSON-typed dynamic values"
  max_varchar_length: null        # unlimited
  max_decimal_precision: null
```

#### Expression capabilities

```yaml
expressions:
  literals: true
  field_references: true
  scalar_functions: true
  window_functions: false
  subqueries: false
  correlated_subqueries: false
  if_then: true
  switch: false
  cast: true
  nested_types: false             # struct/list/map construction
```

#### Write capabilities

```yaml
write:
  insert: true
  update: false
  delete: false
  bulk_ingest: true
  create_table: false
  truncate: false
```

### 4.2 Why YAML is the right format

- Consistent with Substrait's existing extension system
- Human-readable and auditable
- Can be embedded as a resource in driver packages
- Machine-parseable for automated plan generation
- Versionable — capabilities can evolve across driver versions

### 4.3 What Substrait would need to add

Substrait's existing extension system is YAML-based — functions, types, and type variations are all declared in YAML. Capability declaration should follow the same pattern: **a YAML schema for capability documents**.

The YAML capability document would extend the existing `simple_extensions` schema with new top-level sections for relation capabilities, expression capabilities, and write capabilities. Function and type capabilities are already covered by the existing YAML schema.

```yaml
%YAML 1.2
---
# Capability document for Kusto ADBC driver

substrait_version: {major: 0, minor: 55}

relations:
  read: { named_table: true, virtual_table: false }
  filter: true
  project: true
  fetch: { offset: true }
  sort: { multi_key: true }
  aggregate: { grouping_sets: false, distinct: true }
  join:
    inner: true
    left: true
    right: true
    full_outer: true
    left_semi: true
    left_anti: true
    cross: false
    lateral: false
  set: false
  window: false

expressions:
  literals: true
  field_references: true
  scalar_functions: true
  if_then: true
  cast: true
  subqueries: false
  window_functions: false

write:
  insert: true
  bulk_ingest: true
  update: false
  delete: false

# Function and type capabilities use the existing
# simple_extensions YAML schema (scalar_functions,
# aggregate_functions, types, type_variations).
```

A formal Substrait addition would be a **capability YAML schema** (like the existing `simple_extensions_schema.yaml`) that defines the allowed structure. This is consistent with the ecosystem and requires no protobuf changes.

For scenarios that require machine-to-machine exchange over a wire protocol (e.g., a remote query optimizer negotiating with backends), a protobuf `Capabilities` message could be defined as an optional binary equivalent, but YAML should be the primary format.

---

## 5. ADBC API Extensions

### 5.1 Ideal: New first-class APIs

We propose adding these methods to `AdbcConnection`:

#### `GetSubstraitCapabilities()`

```csharp
/// Returns the driver's Substrait capability document.
/// The format is a Substrait Capabilities protobuf message.
public virtual byte[] GetSubstraitCapabilities()
```

This is the primary discovery mechanism. The application calls this once per connection (or caches it) and uses it to guide plan construction.

#### `ValidateSubstraitPlan(byte[] plan)`

```csharp
/// Validates a Substrait plan against the driver's capabilities
/// without executing it. Returns validation diagnostics.
public virtual SubstraitValidationResult ValidateSubstraitPlan(byte[] plan)
```

Returns a structured result indicating which parts of the plan are supported, which aren't, and why. This is cheaper than attempting execution and provides actionable feedback.

```csharp
class SubstraitValidationResult {
    bool IsFullySupported { get; }
    IReadOnlyList<SubstraitDiagnostic> Diagnostics { get; }
}

class SubstraitDiagnostic {
    SubstraitDiagnosticSeverity Severity { get; }  // Error, Warning, Info
    string Message { get; }
    string RelationPath { get; }  // e.g., "relations[0].root.input.filter.condition"
}
```

#### `OptimizeSubstraitPlan(byte[] plan)`

```csharp
/// Rewrites a Substrait plan for optimal execution on this engine.
/// Returns the optimized plan (which may be structurally different
/// but semantically equivalent).
public virtual byte[] OptimizeSubstraitPlan(byte[] plan)
```

This allows the driver to apply engine-specific optimizations: predicate pushdown into joins, projection pruning, join reordering, etc.

#### `PushdownSubstraitPlan(byte[] plan)`

```csharp
/// Performs partial pushdown: translates as much of the plan as
/// possible to native execution, returning a residual plan where
/// translated subtrees are replaced with native execution nodes.
public virtual SubstraitPushdownResult PushdownSubstraitPlan(byte[] plan)
```

This is the partial pushdown mechanism we've already prototyped for Kusto. The result contains the modified plan and metadata about what was pushed down.

### 5.2 Fallback: Using existing ADBC APIs

For drivers that can't implement new methods, we can use conventions on existing APIs:

| Ideal API | Fallback via existing ADBC |
|-----------|--------------------------|
| `GetSubstraitCapabilities()` | `GetInfo(SUBSTRAIT_CAPABILITIES)` — new info code, returns bytes |
| `ValidateSubstraitPlan()` | Set `SubstraitPlan` + call `ExecuteSchema()` — schema means valid, exception means invalid |
| `OptimizeSubstraitPlan()` | No good fallback — requires new API |
| `PushdownSubstraitPlan()` | `SetOption("adbc.substrait.pushdown", "true")` + set `SubstraitPlan` + `ExecuteQuery()` returns the residual plan via a special info column |

The fallback approaches are awkward but workable for initial adoption.

---

## 6. Plan Translation and Partial Pushdown

### 6.1 Full translation

The simplest case: the entire Substrait plan maps to the engine's native dialect. The driver translates and executes.

```
Substrait Plan:   Fetch(10, Sort(salary DESC, Filter(dept='eng', Read(employees))))
KQL:              employees | where dept == 'eng' | sort by salary desc | take 10
```

### 6.2 Partial pushdown

When a plan contains unsupported operations, the driver translates maximal supported subtrees and wraps them as native execution nodes:

```
Input plan:
  WindowRel(row_number)                    ← unsupported
    JoinRel(inner)                         ← unsupported (depends on window above)
      FilterRel(dept == 'eng')             ← supported
        ReadRel(employees)                 ← supported
      ReadRel(departments)                 ← supported

Output plan:
  WindowRel(row_number)                    ← preserved
    JoinRel(inner)                         ← preserved
      KqlQuery("employees | where ...")    ← pushed down
      KqlQuery("departments")             ← pushed down
```

### 6.3 The "native execution node" convention

We propose a convention for representing pushed-down native queries in Substrait plans:

- **Extension URI**: `extension:<vendor>:native_query` (e.g., `extension:kusto:kql_query`)
- **Representation**: A `ReadRel` with a `NamedTable` whose names encode the query type and query string
- **Schema**: The `base_schema` of the `ReadRel` declares the output columns and types

This is a pragmatic approach that works within Substrait's current type system. A more formal approach (a new `NativeQueryRel` message type) would require a Substrait spec change.

---

## 7. Plan Optimization and Rewriting

Beyond translation, drivers can optimize plans for their specific engine:

### Predicate pushdown

```
Before: Filter(a > 10, Join(inner, Read(T1), Read(T2), on: T1.id = T2.id))
After:  Join(inner, Filter(a > 10, Read(T1)), Read(T2), on: T1.id = T2.id)
```

### Projection pruning

```
Before: Project([a, b], Read(T, schema: [a, b, c, d, e]))
After:  Project([a, b], Read(T, schema: [a, b]))  // only fetch needed columns
```

### Engine-specific rewrites

```
Before: Fetch(10, Sort(score DESC, Read(T)))
Kusto:  Read(T) → "T | top 10 by score desc"   // KQL has a combined top operator
```

These optimizations are engine-specific and cannot be standardized in Substrait. The `OptimizeSubstraitPlan` API gives drivers the opportunity to apply them while keeping the plan in Substrait form.

---

## 8. Substrait Gaps and Proposed Extensions

### Gap 1: No capability declaration mechanism

**Current state**: Substrait has no `Capabilities` message or negotiation protocol. Consumers are assumed to support everything.

**Impact**: Applications cannot discover what a driver supports before building a plan. This is the single biggest gap.

**Proposed extension**: Add a capability YAML schema (consistent with the existing `simple_extensions_schema.yaml`) that defines the structure for relation, expression, and write capabilities. Function and type capabilities are already covered. Optionally, define a protobuf equivalent for wire-protocol scenarios.

**Effort**: Medium. Requires a new YAML schema, community consensus on the capability dimensions, and a reference parser.

### Gap 2: No relation-level capability granularity

**Current state**: The extension system supports function-level and type-level declarations, but not relation-level. There's no way to say "I support FilterRel but not WindowRel."

**Impact**: A producer cannot tailor plans to avoid unsupported relation types without out-of-band knowledge.

**Proposed extension**: Include relation capabilities in the `Capabilities` message (as described in Section 4.1).

### Gap 3: Join type granularity

**Current state**: `JoinRel` defines join types as an enum, but there's no standard way to declare which join types a consumer supports. Furthermore, Substrait Issue #325 identifies ambiguities in anti-join semantics (null-aware vs regular) that cannot be distinguished.

**Impact**: Applications cannot know if a specific join flavor will work. Even among "supported" joins, semantic variations may produce incorrect results.

**Proposed extension**: (1) Add join type support to `Capabilities`. (2) Address Issue #325 by adding join semantic qualifiers.

### Gap 4: No plan validation message

**Current state**: Substrait Issue #131 identified the need for plan validation but no standard mechanism was created.

**Impact**: The only way to check if a plan is valid for a consumer is to attempt execution.

**Proposed extension**: Define a `ValidationResult` protobuf message with structured diagnostics, plus a convention for consumers to expose a validation endpoint.

### Gap 5: No "native query" relation type

**Current state**: There's no standard Substrait relation type for "execute this native query string." The partial pushdown mechanism must encode native queries in ad-hoc ways (e.g., in `NamedTable` names).

**Impact**: Partial pushdown results are not interoperable — each vendor uses a different encoding.

**Proposed extension**: Add an `ExtensionLeafRel` or `NativeQueryRel` message:
```protobuf
message NativeQueryRel {
  RelCommon common = 1;
  NamedStruct output_schema = 2;
  string dialect = 3;            // e.g., "kql", "sql", "cypher"
  bytes query = 4;               // the native query (usually UTF-8)
}
```

### Gap 6: Expression capability declaration

**Current state**: Functions are declarable via YAML, but expression-level features (subqueries, correlated subqueries, CASE expressions, casts between specific types) have no capability declaration mechanism.

**Impact**: A producer might emit a subquery in an expression, not knowing the consumer can't handle it.

**Proposed extension**: Add expression capabilities to the `Capabilities` message.

### Gap 7: Write operation capabilities

**Current state**: Substrait focuses on read-path relational algebra. Write operations (INSERT, UPDATE, DELETE) have limited representation and no capability declaration.

**Impact**: Applications cannot discover whether a data source supports bulk ingest, upserts, etc.

**Proposed extension**: Add write capabilities to the `Capabilities` message. This could also include Substrait write relation types (which are less mature than the read path).

---

## 9. ADBC Gaps and Proposed Extensions

### Gap A: No Substrait capability discovery

**Current state**: ADBC has `GetInfo()` for general metadata but no mechanism to retrieve Substrait-specific capabilities.

**Proposed extension**: `GetSubstraitCapabilities()` method returning a `Capabilities` protobuf, plus a new `AdbcInfoCode` for the fallback path.

### Gap B: No plan validation API

**Current state**: The only way to validate a plan is to execute it and see if it fails.

**Proposed extension**: `ValidateSubstraitPlan()` method returning structured diagnostics.

### Gap C: No plan optimization API

**Current state**: Applications have no way to ask a driver to optimize a plan before execution.

**Proposed extension**: `OptimizeSubstraitPlan()` method returning an optimized plan.

### Gap D: No partial pushdown API

**Current state**: ADBC's `SubstraitPlan` property is all-or-nothing — the entire plan executes or fails.

**Proposed extension**: `PushdownSubstraitPlan()` method returning a residual plan with native execution nodes.

### Gap E: Substrait plan result format

**Current state**: When a Substrait plan is executed via ADBC, the result is an `IArrowArrayStream` — but there's no way to return a *modified plan* as part of the result (needed for pushdown).

**Proposed extension**: Define a result type that can carry both data streams and plan metadata.

---

## 10. Proof of Concept: Kusto ADBC Driver

We have built a working proof-of-concept that demonstrates the core mechanics:

### What works today

| Feature | Status |
|---------|--------|
| KQL query execution via `SqlQuery` | ✅ |
| Substrait plan translation to KQL | ✅ |
| Extension function resolution (50+ functions) | ✅ |
| Schema-aware field reference resolution | ✅ |
| UTF-8 native KQL generation (zero intermediate strings) | ✅ |
| Partial pushdown (`SubstraitPartialPushdown.Pushdown()`) | ✅ |
| Capability YAML publication (`KustoCapabilities`) | ✅ |
| Full `GetObjects` with ADBC-compliant nested schema | ✅ |
| `GetTableSchema` via Kusto management commands | ✅ |
| Streaming JSON→Arrow response parser | ✅ |
| 85 unit tests | ✅ |

### What this demonstrates

1. **Translation is feasible**: Substrait's relational algebra maps naturally to KQL's pipe-based syntax, despite their very different structures (tree vs. pipeline).
2. **Function mapping scales**: The YAML catalog approach works well for advertising which Substrait functions have KQL equivalents.
3. **Partial pushdown works**: When a plan contains unsupported operations, we can cleanly split it at the capability boundary.
4. **Zero-copy optimizations are practical**: The protobuf wire format can be read and KQL generated in UTF-8 with minimal allocation.

### What's missing for production use

- Authentication beyond bearer tokens (Azure AD, Managed Identity)
- Connection pooling and retry logic
- Full type variation support
- The proposed ADBC API extensions (no spec changes exist yet)
- The proposed Substrait Capabilities message (no spec changes exist yet)

---

## 11. Open Questions

1. **Should capability documents be static or dynamic?** A driver's capabilities might change based on the connected database version, user permissions, or configuration. Should `GetSubstraitCapabilities()` accept parameters?

2. **How do we handle capability versioning?** If a driver updates its capabilities (e.g., adds window function support in v2.0), how does an application detect this?

3. **Should plan optimization be idempotent?** Can an application call `OptimizeSubstraitPlan()` multiple times? Should the result converge?

4. **How granular should function capabilities be?** Should a driver declare support for `add:i32_i32` and `add:i64_i64` separately, or just `add` (with all overloads implied)?

5. **What's the right home for the Capabilities spec?** Should it live in the Substrait repo (as a new proto file), in ADBC (as a spec extension), or in a new bridging spec?

6. **How should partial pushdown interact with transactions?** If a plan is split across native execution and client-side evaluation, are the native parts executed within a single transaction?

7. **Should there be a standard "plan complexity" metric?** This would let applications estimate whether a plan is too complex for a given consumer before attempting pushdown.

---

## 12. Appendix: Gap Summary Matrix

| # | Gap | Where | Severity | Proposed Solution |
|---|-----|-------|----------|-------------------|
| 1 | No capability declaration | Substrait | **Critical** | New capability YAML schema (extending simple_extensions) |
| 2 | No relation-level capabilities | Substrait | **Critical** | `RelationCapabilities` in Capabilities message |
| 3 | Join type granularity | Substrait | **High** | `JoinCapabilities` + address Issue #325 |
| 4 | No plan validation | Substrait | **High** | `ValidationResult` message |
| 5 | No native query relation | Substrait | **Medium** | `NativeQueryRel` message type |
| 6 | No expression capabilities | Substrait | **Medium** | `ExpressionCapabilities` in Capabilities |
| 7 | No write capabilities | Substrait | **Medium** | `WriteCapabilities` in Capabilities |
| A | No capability discovery API | ADBC | **Critical** | `GetSubstraitCapabilities()` method |
| B | No plan validation API | ADBC | **High** | `ValidateSubstraitPlan()` method |
| C | No plan optimization API | ADBC | **Medium** | `OptimizeSubstraitPlan()` method |
| D | No partial pushdown API | ADBC | **High** | `PushdownSubstraitPlan()` method |
| E | No plan result format | ADBC | **Medium** | Extended result type for pushdown |

---

*This document was prepared based on analysis of the Substrait specification (substrait-io/substrait), the ADBC specification (apache/arrow-adbc), and a working proof-of-concept Kusto ADBC driver implementing plan translation and partial pushdown.*
