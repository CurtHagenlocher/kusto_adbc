# Implementation Status vs. Design Proposal

Comparison of what `substrait-adbc-design.md` proposes against what is implemented in this repository.

---

## ✅ Implemented (Core PoC — Section 10)

| Feature | Status | Evidence |
|---------|--------|----------|
| KQL execution via `SqlQuery` | ✅ Done | `KustoStatement.ExecuteQuery()` sends KQL via `HttpClient.ExecuteQueryAsync` |
| Substrait → KQL translation | ✅ Done | `SubstraitToKqlTranslator.Translate()` handles 7 relation types: Read, Filter, Fetch, Sort, Aggregate, Project, Join |
| Extension function resolution | ✅ Done | `KqlFunctionMap` contains 64 function mappings |
| Schema-aware field references | ✅ Mostly | `ParseNamedStructNames()` captures base schema; ProjectRel schema tracking incomplete |
| UTF-8 zero-alloc KQL generation | ✅ Done | `Utf8KqlWriter` builds KQL in UTF-8 bytes with no intermediate strings |
| Partial pushdown | ✅ Done | `SubstraitPartialPushdown.Pushdown()` rewrites subtrees, preserves unsupported nodes |
| Capability YAML publication | ✅ Done | `KustoCapabilities.GetCapabilityYaml()` exposes embedded YAML |
| `GetObjects` with nested Arrow schema | ✅ Done | `KustoConnection.GetObjects()` with full ADBC-compliant nested schema |
| `GetTableSchema` | ✅ Done | `KustoConnection.GetTableSchema()` parses `.show table cslschema` |
| Streaming JSON→Arrow parser | ✅ Done | `KustoResponseParser` uses `PipeReader` + `Utf8JsonReader` |
| Tests | ✅ 69 tests | Design doc claims 85; actual count across 6 test files is 69 |

---

## ❌ Not Implemented (Proposed Spec Extensions — Sections 4–9)

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `GetSubstraitCapabilities()` ADBC API | §5.1, §9 Gap A | ❌ | Capability YAML exists internally but is not exposed via an ADBC API |
| `ValidateSubstraitPlan()` API | §5.1, §9 Gap B | ❌ | No plan validation mechanism |
| `OptimizeSubstraitPlan()` API | §5.1, §9 Gap C | ❌ | No standalone optimizer or rewriter |
| `PushdownSubstraitPlan()` API | §5.1, §9 Gap D | ❌ | Pushdown logic exists internally but is not surfaced as an ADBC API |
| Formal `NativeQueryRel` protobuf message | §8 Gap 5 | ❌ | Uses ad-hoc `NamedTable` encoding (`["kql_query", "<kql>"]`) instead |
| Substrait Capability YAML schema | §4.3, §8 Gap 1 | ❌ | Requires upstream Substrait spec change |
| Relation-level capability declaration | §8 Gap 2 | ❌ | Requires upstream Substrait spec change |
| Join type granularity in capabilities | §8 Gap 3 | ❌ | Requires upstream Substrait spec change |
| Plan validation message | §8 Gap 4 | ❌ | Requires upstream Substrait spec change |
| Expression capability declaration | §8 Gap 6 | ❌ | Requires upstream Substrait spec change |
| Write capability declaration | §8 Gap 7 | ❌ | Requires upstream Substrait spec change |
| Plan result format for pushdown | §9 Gap E | ❌ | No extended result type |
| Auth beyond bearer tokens | §10 | ❌ | No Azure AD / Managed Identity support |
| Connection pooling and retry logic | §10 | ❌ | Not implemented |
| Full type variation support | §10 | ❌ | Not implemented |

---

## Summary

The **core proof-of-concept** described in Section 10 is fully built — Substrait-to-KQL translation, partial pushdown, capability YAML, ADBC metadata, and streaming Arrow results all work. The **proposed spec extensions** to both ADBC (new APIs) and Substrait (capability schema, native query relation, validation messages) remain unimplemented, as the design doc itself acknowledges these require upstream specification changes.
