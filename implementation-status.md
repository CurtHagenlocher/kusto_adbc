# Implementation Status vs. Design Proposal

Comparison of what `substrait-adbc-design.md` proposes against what is implemented in this repository.

---

## ✅ Implemented (Core PoC — Section 10)

| Feature | Status | Evidence |
|---------|--------|----------|
| KQL execution via `SqlQuery` | ✅ Done | `KustoStatement.ExecuteQuery()` sends KQL via `HttpClient.ExecuteQueryAsync` |
| Substrait → KQL translation | ✅ Done | `SubstraitPlanDecoder` + `KqlEmitter`: Read, Filter, Project, Fetch, Sort, TopN, Aggregate, Join, Cross, Set (union) |
| Wire-format compatibility | ✅ Done | Field numbers match the Substrait v0.107 protos; legacy fields (extension URIs, int64 fetch offset/count, inline grouping expressions) are also accepted |
| Real-producer coverage | ✅ Done | 35 DataFusion-generated plans in `test/KustoAdbc.Tests/Fixtures/DataFusion`, with reviewed KQL snapshots |
| Extension function resolution | ✅ Done | `KqlFunctionCatalog` resolves by extension URN, name and signature, and honors function options |
| Schema-aware field references | ✅ Done | Every relation's output columns are tracked (including Project emit, joins, aggregates) |
| UTF-8 KQL generation | ✅ Done | `Utf8KqlWriter` builds KQL as UTF-8 bytes |
| Partial pushdown | ✅ Done | `SubstraitPartialPushdown.Pushdown()` replaces translatable subtrees with KQL reads that declare their output schema |
| Capability publication | ✅ Done | `KustoCapabilities.GetDialectYaml()` returns a Substrait dialect file, validated against the dialect schema and checked against the function catalog by tests |
| `GetObjects` with nested Arrow schema | ✅ Done | `KustoConnection.GetObjects()` with full ADBC-compliant nested schema |
| `GetTableSchema` | ✅ Done | `KustoConnection.GetTableSchema()` parses `.show table cslschema` |
| Streaming JSON→Arrow parser | ✅ Done | `KustoResponseParser` uses `PipeReader` + `Utf8JsonReader` |
| Semantic KQL validation in tests | ✅ Done | Translations are analyzed with `Microsoft.Azure.Kusto.Language` against table schemas |

---

## ◐ Addressed Upstream by the Substrait Dialect Format

The Substrait project has since added a dialect format (`text/dialect_schema.yaml`) that covers
much of what the design proposed as new capability declarations. The driver's dialect uses it.
What it still cannot express is described in [substrait-dialect-gaps.md](substrait-dialect-gaps.md).

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Substrait capability YAML schema | §4.3, §8 Gap 1 | ◐ | Covered by the dialect format |
| Relation-level capability declaration | §8 Gap 2 | ◐ | `supported_relations`; sub-features (grouping sets, sort directions, ...) not expressible |
| Join type granularity | §8 Gap 3 | ◐ | `join_types`; join condition shape and null-aware anti joins not expressible |
| Expression capability declaration | §8 Gap 6 | ◐ | `supported_expressions`, cast failure options; cast pairs not expressible |
| Write capability declaration | §8 Gap 7 | ◐ | `WRITE`/`DDL` relation entries exist; not relevant to this driver yet |

---

## ❌ Not Implemented (Proposed Spec Extensions — Sections 4–9)

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `GetSubstraitCapabilities()` ADBC API | §5.1, §9 Gap A | ❌ | The dialect is available from `KustoCapabilities` but not through an ADBC API |
| `ValidateSubstraitPlan()` API | §5.1, §9 Gap B | ❌ | No plan validation mechanism beyond attempting translation |
| `OptimizeSubstraitPlan()` API | §5.1, §9 Gap C | ❌ | No standalone optimizer or rewriter |
| `PushdownSubstraitPlan()` API | §5.1, §9 Gap D | ❌ | Pushdown logic exists internally but is not surfaced as an ADBC API |
| Formal `NativeQueryRel` protobuf message | §8 Gap 5 | ❌ | Uses ad-hoc `NamedTable` encoding (`["kql_query", "<kql>"]`) instead |
| Plan validation message | §8 Gap 4 | ❌ | Requires upstream Substrait spec change |
| Plan result format for pushdown | §9 Gap E | ❌ | No extended result type |
| Auth beyond bearer tokens | §10 | ❌ | No Azure AD / Managed Identity support |
| Connection pooling and retry logic | §10 | ❌ | Not implemented |
| Type variations | §10 | ❌ | Declared type variations are rejected; undeclared references are ignored |
| Window functions, subqueries, VALUES | — | ❌ | Rejected by the translator (left for client-side execution by partial pushdown) |

---

## Summary

The **core proof-of-concept** described in Section 10 is built: Substrait-to-KQL translation
that is tested against plans from a real producer, partial pushdown, a published Substrait
dialect, ADBC metadata and streaming Arrow results. Several of the **proposed Substrait
extensions** have since been addressed upstream by the dialect format. The ADBC API extensions
and the remaining Substrait gaps still need upstream changes.
