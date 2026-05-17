# Kusto ADBC Driver

A C# [ADBC](https://arrow.apache.org/adbc/) (Arrow Database Connectivity) driver for [Azure Data Explorer (Kusto)](https://learn.microsoft.com/en-us/azure/data-explorer/), with built-in support for [Substrait](https://substrait.io/) query plan translation and partial pushdown.

## Features

- **KQL query execution** via the standard ADBC `SqlQuery` interface
- **Substrait → KQL translation** — translates Substrait query plans into native KQL, supporting Read, Filter, Project, Aggregate, Sort, Fetch, and Join relations
- **Partial pushdown** — when a Substrait plan contains unsupported operations, the driver translates the supported subtrees to KQL and returns a residual plan for client-side evaluation
- **Capability declaration** — publishes a YAML capability document describing supported relations, functions, types, and expressions
- **64 function mappings** — maps Substrait extension functions to their KQL equivalents
- **UTF-8 zero-allocation KQL generation** — generates KQL directly in UTF-8 bytes with no intermediate string allocations
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
    ├── SubstraitToKqlTranslator.cs   # Substrait plan → KQL translation
    ├── SubstraitPartialPushdown.cs   # Partial pushdown engine
    ├── KqlFunctionMap.cs             # Substrait ↔ KQL function mappings
    ├── KustoCapabilities.cs          # Capability YAML publication
    ├── Utf8KqlWriter.cs              # UTF-8 KQL string builder
    ├── ProtobufWriter.cs             # Low-level protobuf writing
    ├── SubstraitTranslationException.cs
    └── Extensions/
        └── kusto_functions.yaml      # Substrait function extension catalog

test/KustoAdbc.Tests/               # 69 unit tests
```

## Design Document

See [substrait-adbc-design.md](substrait-adbc-design.md) for the full design proposal describing capability negotiation, plan translation, partial pushdown, and proposed extensions to both ADBC and Substrait. See [implementation-status.md](implementation-status.md) for a summary of what has been implemented.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
