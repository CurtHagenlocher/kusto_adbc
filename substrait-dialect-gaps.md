# Substrait Dialects: Gaps Found While Writing a Kusto Dialect

This note describes what the Substrait dialect format
([`text/dialect_schema.yaml`](https://github.com/substrait-io/substrait/blob/main/text/dialect_schema.yaml),
as of Substrait v0.107) can and cannot express, based on writing a complete dialect for a
real consumer: an ADBC driver that translates Substrait plans into Kusto Query Language (KQL)
for Azure Data Explorer. It is meant for Substrait and ADBC contributors evaluating whether a
dialect file is enough for a producer to generate plans that a consumer will accept *and*
execute with the intended semantics.

**Short version:** the dialect format is a good fit for *what* a consumer supports (relations,
join types, set operations, expression kinds, types with precision bounds, functions by
extension, signature and option). It cannot describe *how* a consumer restricts those
features. Examples: "equality join conditions only", "this argument must be a literal", "only
these values of this enumeration", "one grouping set". It also cannot describe the semantic
differences a producer needs in order to get identical results. In practice, the dialect check
is necessary but not sufficient. The Kusto dialect records the missing information as free-form
`metadata`, which no producer can act on automatically.

Separately from the format, the ecosystem has two problems: producers that omit extension URNs,
and producers that emit non-standard functions. These limit what any dialect can achieve today.

---

## Background

The driver publishes [`kusto_dialect.yaml`](src/KustoAdbc/Substrait/Dialect/kusto_dialect.yaml),
which validates against the dialect schema and is the contract for the translator:

* every function, implementation and option behavior it lists translates to KQL, and anything
  else is rejected;
* a unit test checks the dialect's function lists against the translator's function catalog,
  so the file cannot drift from the code;
* the translator is tested against plans produced by DataFusion 55 from SQL
  (`test/KustoAdbc.Tests/Fixtures/DataFusion`), and every generated query is semantically
  analyzed with the Kusto language service;
* a differential test (`scripts/differential_test.py`) runs each SQL query in DataFusion and
  its translation on a Kusto cluster over the same data, including null keys and values. 26 of
  27 comparable queries returned identical results. The exception is the null-aware anti join
  described in gap 1.

The gaps below are things we needed to say about the Kusto consumer and could not.

| # | Gap | Effect on producers | Severity |
|---|-----|---------------------|----------|
| 1 | Join condition shape can't be declared | Plans with non-equi joins pass the dialect check and are rejected | High |
| 2 | Argument constraints (literal-only arguments) can't be declared | `like`, `trim`, `extract` time zones, fetch counts fail at runtime | High |
| 3 | Supported enumeration values can't be declared | `extract` components, `median` EXACT/APPROXIMATE | High |
| 4 | Relation sub-features can't be declared | Grouping sets, measure filters, ordered aggregates, sort directions, WITH TIES | High |
| 5 | `required_options` is ambiguous and unvalidated | Producers can't tell "default behavior" from "must request" | Medium |
| 6 | Semantic properties have no place in the format | String nulls, NaN ordering, collation, result-type differences | Medium |
| 7 | Type value ranges and type mappings | Timestamp range; columns surfaced under another type | Medium |
| 8 | Cast support is all-or-nothing | Which casts work, and which can fail | Medium |
| 9 | Variadic `min: 0` is not allowed | `and()`/`or()` with no arguments can't be declared | Low |
| 10 | Consumer limits | Result truncation, IN-list size, argument counts | Low |
| 11 | Named-table naming rules | Number and meaning of name parts, quoting | Low |
| 12 | No Substrait version in the dialect | Can't tell which proto/extension version is meant | Low |

---

## Gaps in the dialect format

### 1. Join conditions: join types yes, condition shape no

`join_relation.join_types` lets a dialect list `[INNER, OUTER, LEFT, RIGHT, LEFT_SEMI, ...]`,
which is exactly right for Kusto. What can't be said is that **Kusto joins only match on
equality between a left column and a right column** (`$left.a == $right.b`, combined with
`and`). A plan with `ON a.x < b.y`, `ON a.x = b.y + 1` or `ON a.x = b.y OR ...` passes the
dialect check, but the consumer cannot run it.

The Kusto translator recovers some cases by moving conjuncts where they keep their meaning:

* after an INNER join, as a filter;
* into the right input of a LEFT join, when the conjunct references only the right side;
* and so on for the other join types.

It rejects the rest. A producer has no way to know which shapes will work. Related semantics
that are also not declarable:

* whether null keys can match. Kusto's own join keys *do* match nulls to each other (measured on
  a cluster; the documentation doesn't say), the opposite of Substrait's `equal`. The translator
  drops null-keyed rows, or adds a marker key for full outer joins, and in exchange supports
  `is_not_distinct_from` keys natively;
* whether `post_join_filter` is supported, and for which join types;
* whether an anti join is null-aware, i.e. `NOT IN` versus `NOT EXISTS`. The plan itself can't
  express this either (substrait-io/substrait#325). DataFusion emits `LEFT_ANTI` for `NOT IN`,
  so a consumer with `NOT EXISTS` semantics gets different results when the subquery contains
  nulls. Our differential test (see Background) hit exactly this case: on data where SQL's
  `NOT IN` returns no rows, the translated query returns four.

*Possible extension:*

```yaml
- relation: JOIN
  join_types: [INNER, LEFT, ...]
  condition_kinds: [COLUMN_EQUALITY_CONJUNCTION]   # vs. ARBITRARY_EXPRESSION
  null_safe_keys: false
  post_join_filter: [INNER, OUTER, LEFT, RIGHT]
```

### 2. Argument constraints

Several KQL operators require literal arguments. A dialect can list `like` with impl `str_str`
but cannot say that the pattern must be a literal. The same applies to:

* the character set of `trim`/`ltrim`/`rtrim` (KQL trims by regular expression, so the set has
  to be compiled into one);
* the time zone argument of `extract` on `precision_timestamp_tz` (only UTC is supported);
* the offset and count of `FetchRel`/`TopNRel` (KQL `take` needs a constant).

Extension YAML has `constant: true` for value arguments, but that describes the *function*, not
a consumer's restriction of it, and none of the standard functions above set it.

*Possible extension:* per-implementation argument constraints in `dialect_function`, e.g.
`argument_constraints: [{index: 1, constant: true}]`. For relations, the same idea:
`fetch: {constant_offset: true, constant_count: true}`.

### 3. Enumeration argument values

Enumeration arguments are part of the signature (`req`), so `supported_impls: [req_ptstz_str]`
says that `extract` with a timestamp and a time zone is supported, but not *which* components.
Kusto supports YEAR, QUARTER, MONTH, DAY, ..., UNIX_TIME. It doesn't support ISO_YEAR, US_YEAR,
MONDAY_WEEK, SUNDAY_WEEK, US_WEEK, PICOSECOND or TIMEZONE_OFFSET. Likewise:

* `median(req, fp64)`: Kusto can deliver APPROXIMATE (`percentile`) but not EXACT;
* `std_dev(req, fp64)`: both SAMPLE and POPULATION happen to be supported, but a consumer
  supporting only one couldn't say so.

*Possible extension:* `enum_values: {0: [YEAR, MONTH, ...]}` per implementation, or
treating enumeration values like option values in `required_options`.

### 4. Relation sub-features

Most relation entries carry only `relation` and `metadata`. Features inside a relation that a
consumer may or may not support can't be declared:

| Relation | Undeclarable feature | Kusto |
|----------|---------------------|-------|
| AGGREGATE | number of grouping sets (GROUPING SETS / ROLLUP / CUBE) | one only |
| AGGREGATE | measure `filter` | supported (via `sumif`, `countif`, ...) except for some functions |
| AGGREGATE | `AggregationInvocation.DISTINCT` per function | `count` only |
| AGGREGATE | aggregate `sorts` (ordered aggregates) | no |
| AGGREGATE | `AggregationPhase` | INITIAL_TO_RESULT only |
| SORT | `SortDirection` values (incl. CLUSTERED), custom comparison functions | all four directions; no comparison functions |
| TOP_N | `FetchMode.WITH_TIES` | no |
| FETCH | expression offset/count | literals only |
| READ | `filter`, `best_effort_filter`, `projection` (and nested projection) | supported; top-level projection only |

The DISTINCT-per-function case belongs on `dialect_function` (`supported_invocations: [ALL]`)
rather than on the relation.

### 5. `required_options` is ambiguous and unvalidated

The schema describes `required_options` as "Function-specific options that must be supported by
the dialect. These are the exact list of options that must be used. If no options are listed
here, no options must be present." The type is an unconstrained `object`. Several things are
unclear:

* **Direction.** Does `overflow: ERROR` mean "this dialect behaves as ERROR" (a capability), or
  "plans must request ERROR" (a requirement)? Substrait's own option rule is that a plan may omit
  an option and let the consumer choose. If it specifies preferences, the consumer must deliver
  the first one it can or reject the plan. That rule fits the capability reading. The Kusto
  dialect uses that reading and states it in a comment.
* **Multiple behaviors.** Values are single strings in the upstream examples. A consumer that
  can deliver both CASE_SENSITIVE and CASE_INSENSITIVE has to list the function twice, once per
  behavior. That works, but isn't specified.
* **"No options must be present"** reads as forbidding options a consumer could simply ignore,
  e.g. `rounding` on an exact operation.
* **No validation.** The schema accepts any keys and values. The upstream test file
  `dialects/tests/functions_test.yaml` places `notation: INFIX` inside `required_options` and
  still validates.

*Suggestion:* rename to `supported_options: {option: [values...]}` with an explicit statement of
how it interacts with plan-level option preferences. Validate option names and values against
the referenced extension.

Also missing: a way to say where a declared behavior comes from. Kusto doesn't document integer
overflow, integer division by zero or how `round()` breaks ties. We measured them on a cluster:
overflow wraps, division by zero gives null, ties round away from zero. In two places the
measurement contradicts the documentation: `sqrt(-1)` is NaN, not null, and `strrep` doesn't
cap its count at 1024. Some edge cases raise errors instead (`long.MinValue / -1`, `abs` and
negation of `long.MinValue`), so `divide`, `negate` and `abs` declare no overflow behavior. A
`required_options` entry can't say "observed, not guaranteed", so a producer can't tell
measured behavior from contractual behavior.

### 6. Semantic properties

These Kusto behaviors change query results, and the format has nowhere to put them:

* **Strings can't be null.** A missing string is `''`. The translator maps `is_null(string)` to
  `isempty()` and string null literals to `''`. Empty strings and missing values can't be told
  apart.
* **Null comparisons.** With one null operand, Kusto's `==` is false and `!=` is true, where
  Substrait gives null. `!=` changes which rows a filter keeps: `WHERE price <> 3` would keep
  rows with a null price. The translator adds null guards, but a dialect can't warn a producer
  that a consumer needs them.
* **Ordering.** Strings sort ordinally: `A`, `B`, `a`, `b`, `e`, `é` (measured). KQL doesn't
  even define `<` on strings, so comparisons use `strcmp`. NaN sorts after all other numbers, as
  in PostgreSQL and DataFusion.
* **Aggregates over no values.** Kusto returns 0 (`sum`) or NaN (`avg`) where Substrait returns
  null. The translator guards these with `iif(countif(...) == 0, null, ...)`. The behavior is
  fixable, but the difference isn't declarable.
* **Result types.** KQL evaluates `int` arithmetic as `long`, and `avg` of integers is `real`.
  The translator converts back where Substrait expects i32. A declared type mapping would let
  producers anticipate this instead.
* **`now()`** is constant per query. `supported_execution_behavior` covers this one
  (`PER_PLAN`), which is a good model for the others.

*Suggestion:* a `semantics` section with standardized keys, such as `empty_string_is_null`,
`null_comparison_result`, `nan_ordering`, `string_collation`, `aggregate_empty_result`.
Producers could check these the same way they check `supported_execution_behavior`.

### 7. Type ranges and type mappings

* `PRECISION_TIMESTAMP(_TZ)` takes `max_precision` (7 for Kusto's 100 ns ticks) but not a value
  range. Kusto datetimes cover years 1 to 9999.
* `supported_as_column` says a type can't be a column, but not *how* the system's own column
  types appear to the producer. The driver reports Kusto `decimal` as fp64 and `guid` and
  `dynamic` as string, because its Arrow mapping does. A producer reading the table schema
  therefore never sees DECIMAL or UUID columns, even though both are supported as literals and
  cast targets.

*Suggestion:* allow `min_value`/`max_value` on temporal types, and an optional
`column_type_mapping` from system type names to the Substrait type a producer will see.

### 8. Casts

`cast_expression.failure_options` declares RETURN_NULL vs THROW_EXCEPTION, but not which
source→target pairs are supported. The Kusto translator supports casts to boolean, i32, i64,
fp64, string, decimal, timestamp, date, interval_day and uuid. It accepts THROW_EXCEPTION only
for casts that cannot fail, because KQL conversions return null on failure. DataFusion marks
even widening casts (i32→i64) THROW_EXCEPTION, so rejecting that failure option outright would
reject most real plans.

Target *parameters* aren't covered either. KQL's `todecimal()` neither rounds to a scale nor
checks precision, and datetimes always keep 100 ns precision. So casts to `decimal(10,2)` from
a real, or to `precision_timestamp<3>`, can't be honored, while casts from integers can.
Temporal-to-string casts are also rejected, because KQL formats them differently from SQL.

*Suggestion:* an optional cast matrix, e.g. `casts: [{from: [i32, i64], to: [fp64], failure_options: [...]}]`.

### 9. Variadic functions with zero arguments

`functions_boolean.yaml` declares `and`/`or` with `variadic: {min: 0}`, but the dialect schema
requires `variadic.min >= 1`. The Kusto dialect writes `variadic: {}`.

### 10. Limits

Kusto truncates results at 500,000 rows or 64 MB by default. This is reported as a partial
failure that clients can miss. Other limits: an `in` list can hold up to 1,000,000 values, and
functions like `strcat` and `coalesce` take at most 64 arguments. Only the argument limit is
expressible (`variadic.max`). A `limits` section, or at least a standard metadata key, would
let producers split IN lists or warn about truncation.

### 11. Named tables

`read_types: [NAMED_TABLE]` doesn't say how many name parts a table name has or what they mean.
For Kusto that is `[table]`, `[database, table]` or `[cluster, database, table]`. Nor does it say
whether names are case-sensitive or need quoting.

### 12. Versioning

The top level allows only `name`, `metadata`, `dependencies` and the `supported_*` lists.
Nothing records which Substrait release the dialect was written against. That matters because
proto fields and extension signatures change between releases: fields have been reserved and
renumbered, and impls deprecated. The Kusto dialect puts `substrait_version` in `metadata`.

---

## Gaps around the format

These aren't problems with the schema itself, but they decide whether a dialect helps in
practice.

### Producers that omit extension URNs

DataFusion 55 declares functions by bare name with no extension URN: the
`extension_urn_reference` is `0xFFFFFFFF` and no URN is declared. A dialect identifies
functions by `(source URN, name, signature)`, so such plans can't be matched against it
exactly. The Kusto translator falls back to matching by name and inferred argument types.
That works, but is a guess.

### Non-standard functions and signatures

Of 35 SQL queries run through DataFusion's producer, 6 were rejected because they used
functions or signatures that are not in the standard extensions, with no extension declared
for them. (The only other rejection was a ROLLUP, i.e. multiple grouping sets.)

| SQL | Emitted | Standard equivalent |
|-----|---------|---------------------|
| `extract(year FROM ts)` | `date_part` | `extract` (functions_datetime) |
| `stddev(x)` | `stddev` | `std_dev` (functions_arithmetic) |
| `length(s)` | `character_length` | `char_length` (functions_string) |
| `substr(s, 2, 3)` | `substring` with i64 positions | `substring:str_i32_i32` |
| `s LIKE 'a%'`, `starts_with(s, 'a')` | `like` with a third argument (the escape character; a null literal when there is none) | `like:str_str` |

A dialect can't fix this. It only helps if the producer consults it and limits itself to what
it lists.

### Under-specified standard functions

Writing exact translations exposed functions whose meaning a consumer has to guess:

* **`like`** doesn't define its wildcard syntax or an escape character. The translator assumes
  SQL `%`/`_` and rejects patterns containing a backslash.
* **`atan2(x, y)`** is described as "arctangent of values given as x/y pairs", which doesn't say
  which argument is the numerator. The Kusto dialect leaves it out rather than guess.
* **`extract`**: MICROSECOND counts from the last full millisecond, while KQL's `datetime_part`
  counts from the last full second. This is well defined, just easy to get wrong; it's noted
  here because it's the kind of detail a dialect can't flag.

### Undeclared type variations

DataFusion sets `type_variation_reference` (e.g. 2 on strings, its "view" layout marker) without
declaring the variation in the plan. Declared variations may change function semantics. The
Kusto translator rejects declared variations and ignores undeclared references; strictly, such
plans are malformed.

### Discovery

The format describes a dialect, but there is no standard way for a consumer to *publish* one,
or for a producer to request it. ADBC has no API for this (see
[substrait-adbc-design.md](substrait-adbc-design.md), §5 and §9). The Kusto driver exposes its
dialect through `KustoCapabilities.GetDialectYaml()`.

---

## What works well

For balance: several gaps listed in this project's earlier design document are now covered by
the dialect format, and the Kusto dialect uses them as intended.

* Relation-level support (`supported_relations`), including `join_types`, set `operations` and
  `read_types`.
* Expression-level support, including cast `failure_options` and execution-context variables.
* Types with `max_precision` and `max_scale`.
* Functions by extension and exact signature, with option behaviors (using `required_options`
  as a capability, as described in gap 5).
* `system_metadata.name`/`notation`, which maps naturally to an operator-or-function code
  generator.
* Execution behavior (`PER_PLAN`).

The dialect file validates with an off-the-shelf JSON Schema validator.
