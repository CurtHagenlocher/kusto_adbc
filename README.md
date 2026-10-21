# Kusto ADBC Driver

A C# [ADBC](https://arrow.apache.org/adbc/) (Arrow Database Connectivity) driver for [Azure Data Explorer (Kusto)](https://learn.microsoft.com/en-us/azure/data-explorer/), with built-in support for [Substrait](https://substrait.io/) query plan translation and partial pushdown.

## Features

- **KQL query execution** via the standard ADBC `SqlQuery` interface
- **Substrait → KQL translation** — translates Substrait plans (v0.107 protos; older producers' legacy fields are accepted) into native KQL, supporting Read, Filter, Project, Aggregate, Sort, Fetch, TopN, Join, Cross and Set (union) relations. Constructs that can't be translated with the same semantics are rejected rather than approximated
- **Partial pushdown** — when a Substrait plan contains unsupported operations, the driver translates the supported subtrees to KQL and returns a residual plan for client-side evaluation
- **Substrait dialect** — publishes a [Substrait dialect](src/KustoAdbc/Substrait/Dialect/kusto_dialect.yaml) describing exactly the types, relations, expressions, functions and option behaviors the translator supports (`KustoCapabilities.GetDialectYaml()`)
- **UTF-8 KQL generation** — generates KQL directly as UTF-8 bytes, ready to send
- **Streaming JSON→Arrow parser** — parses Kusto JSON responses into Arrow record batches using `PipeReader` and `Utf8JsonReader`
- **ADBC metadata** — full `GetObjects` (with ADBC-compliant nested schema) and `GetTableSchema` support

## Requirements

- .NET 8.0+ (also targets netstandard2.0 and net10.0)
- An Azure Data Explorer cluster and a bearer token for authentication

## Getting Started

```csharp
using Apache.Arrow.Adbc;
using KustoAdbc;

// Create the driver and open a connection
var driver = new KustoDriver();
var database = driver.Open(new Dictionary<string, string>
{
    ["uri"] = "https://your-cluster.kusto.windows.net",
    ["database"] = "your-database",
    ["access_token"] = "your-bearer-token"
});
var connection = database.Connect(null);

// Execute a KQL query
var statement = connection.CreateStatement();
statement.SqlQuery = "StormEvents | take 10";
var result = statement.ExecuteQuery();
```

## Project Structure

```
src/KustoAdbc/
├── KustoDriver.cs              # ADBC driver entry point
├── KustoConnection.cs          # Connection, GetObjects, GetTableSchema
├── KustoDatabase.cs            # Database handle
├── KustoStatement.cs           # Statement execution (KQL and Substrait)
├── KustoParameters.cs          # Connection parameter constants
├── Arrow/                      # Arrow helpers and response parsing
│   ├── KustoResponseParser.cs  # Streaming JSON→Arrow parser
│   ├── ArrowListArrayHelper.cs # Nested list/struct array builder
│   ├── NativeAllocator.cs      # Native memory allocator
│   └── Property.cs             # Property helpers
├── Http/
│   └── KustoHttpClient.cs      # HTTP client for Kusto REST API
└── Substrait/
    ├── SubstraitToKqlTranslator.cs   # Public entry point: Substrait plan → KQL
    ├── SubstraitPlanDecoder.cs       # Protobuf → plan model (rejects what it can't represent)
    ├── SubstraitModel.cs             # Plan model: relations, expressions, types
    ├── KqlEmitter.cs                 # Plan model → KQL, tracking each relation's output columns
    ├── KqlFunctionCatalog.cs         # Supported functions, signatures, options and their KQL
    ├── SubstraitPartialPushdown.cs   # Partial pushdown engine
    ├── KustoCapabilities.cs          # Dialect publication
    ├── ProtoReader.cs                # Bounds-checked protobuf reading
    ├── ProtobufWriter.cs             # Low-level protobuf writing
    ├── SubstraitFields.cs            # Substrait protobuf field numbers
    ├── Utf8KqlWriter.cs              # UTF-8 KQL builder (identifiers, literals)
    ├── SubstraitTranslationException.cs
    └── Dialect/
        └── kusto_dialect.yaml        # Substrait dialect for this driver

test/KustoAdbc.Tests/
├── Fixtures/DataFusion/            # Plans produced by DataFusion from SQL, with reviewed KQL snapshots
└── Fixtures/Substrait/             # Substrait dialect schema (for validating the dialect)
scripts/gen_datafusion_fixtures.py  # Regenerates the DataFusion plan fixtures
scripts/differential_test.py        # Compares DataFusion results with the translated KQL run on a Kusto cluster
```

## Design Documents

See [substrait-adbc-design.md](substrait-adbc-design.md) for the full design proposal describing capability negotiation, plan translation, partial pushdown, and proposed extensions to both ADBC and Substrait. See [implementation-status.md](implementation-status.md) for a summary of what has been implemented, and [substrait-dialect-gaps.md](substrait-dialect-gaps.md) for what the Substrait dialect format can't yet express about this driver.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
