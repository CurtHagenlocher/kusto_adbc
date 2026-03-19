using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using KustoAdbc.Arrow;
using KustoAdbc.Http;

namespace KustoAdbc
{
    /// <summary>
    /// ADBC connection to a Kusto cluster/database.
    /// </summary>
    public sealed class KustoConnection : AdbcConnection
    {
        readonly string _endpoint;
        readonly string _database;
        string _accessToken;
        readonly int _batchSize;
        KustoHttpClient? _httpClient;
        bool _disposed;

        internal KustoConnection(string endpoint, string database, string accessToken, int batchSize)
        {
            _endpoint = endpoint;
            _database = database;
            _accessToken = accessToken;
            _batchSize = batchSize;
            _httpClient = new KustoHttpClient(endpoint, database, accessToken);
        }

        internal KustoHttpClient HttpClient => _httpClient ?? throw new ObjectDisposedException(nameof(KustoConnection));
        internal int BatchSize => _batchSize;

        public override void SetOption(string key, string value)
        {
            if (key == KustoParameters.AccessToken)
            {
                _accessToken = value;
                _httpClient?.SetAccessToken(value);
            }
            else
            {
                throw AdbcException.NotImplemented($"Option '{key}' is not supported after connection is opened.");
            }
        }

        public override AdbcStatement CreateStatement()
        {
            ThrowIfDisposed();
            return new KustoStatement(this);
        }

        public override IArrowArrayStream GetObjects(
            GetObjectsDepth depth,
            string? catalogPattern,
            string? dbSchemaPattern,
            string? tableNamePattern,
            IReadOnlyList<string>? tableTypes,
            string? columnNamePattern)
        {
            ThrowIfDisposed();
            return GetObjectsImpl(depth, catalogPattern, dbSchemaPattern, tableNamePattern, tableTypes, columnNamePattern);
        }

        IArrowArrayStream GetObjectsImpl(
            GetObjectsDepth depth,
            string? catalogPattern,
            string? dbSchemaPattern,
            string? tableNamePattern,
            IReadOnlyList<string>? tableTypes,
            string? columnNamePattern)
        {
            // Kusto mapping:
            //   Catalog  = cluster endpoint
            //   DbSchema = database name (the one we're connected to)
            //   Table    = tables from .show tables
            //   Column   = columns from .show table T cslschema

            string catalogName = _endpoint;

            // Check catalog filter
            if (catalogPattern != null && !MatchesPattern(catalogName, catalogPattern))
            {
                // No matching catalogs — return empty result
                return BuildEmptyGetObjects();
            }

            // Check db schema filter
            if (dbSchemaPattern != null && !MatchesPattern(_database, dbSchemaPattern))
            {
                return BuildGetObjectsResult(catalogName, depth, null);
            }

            // Build the nested structure based on requested depth
            var catalogNameBuilder = new StringArray.Builder();
            var catalogDbSchemas = new List<IArrowArray?>();
            catalogNameBuilder.Append(catalogName);

            if (depth == GetObjectsDepth.Catalogs)
            {
                catalogDbSchemas.Add(null);
            }
            else
            {
                catalogDbSchemas.Add(BuildDbSchemas(depth, tableNamePattern, tableTypes, columnNamePattern));
            }

            var dataArrays = new IArrowArray[]
            {
                catalogNameBuilder.Build(),
                ArrowListArrayHelper.BuildListArray(catalogDbSchemas, new StructType(StandardSchemas.DbSchemaSchema)),
            };

            var batch = new RecordBatch(StandardSchemas.GetObjectsSchema, dataArrays, 1);
            return new SingleBatchStream(StandardSchemas.GetObjectsSchema, batch);
        }

        StructArray BuildDbSchemas(
            GetObjectsDepth depth,
            string? tableNamePattern,
            IReadOnlyList<string>? tableTypes,
            string? columnNamePattern)
        {
            var dbSchemaNameBuilder = new StringArray.Builder();
            var dbSchemaTables = new List<IArrowArray?>();
            var validityBuilder = new ArrowBuffer.BitmapBuilder();

            dbSchemaNameBuilder.Append(_database);
            validityBuilder.Append(true);

            if (depth == GetObjectsDepth.DbSchemas)
            {
                dbSchemaTables.Add(null);
            }
            else
            {
                dbSchemaTables.Add(BuildTables(depth, tableNamePattern, tableTypes, columnNamePattern));
            }

            var dataArrays = new IArrowArray[]
            {
                dbSchemaNameBuilder.Build(),
                ArrowListArrayHelper.BuildListArray(dbSchemaTables, new StructType(StandardSchemas.TableSchema)),
            };

            return new StructArray(new StructType(StandardSchemas.DbSchemaSchema), 1, dataArrays, validityBuilder.Build());
        }

        StructArray BuildTables(
            GetObjectsDepth depth,
            string? tableNamePattern,
            IReadOnlyList<string>? tableTypes,
            string? columnNamePattern)
        {
            // Fetch table list from Kusto
            var tables = FetchTableList();

            var tableNameBuilder = new StringArray.Builder();
            var tableTypeBuilder = new StringArray.Builder();
            var tableColumns = new List<IArrowArray?>();
            var tableConstraints = new List<IArrowArray?>();
            var validityBuilder = new ArrowBuffer.BitmapBuilder();
            int length = 0;

            foreach (var (name, type) in tables)
            {
                // Apply table name filter
                if (tableNamePattern != null && !MatchesPattern(name, tableNamePattern))
                    continue;

                // Apply table type filter
                if (tableTypes != null && !ContainsIgnoreCase(tableTypes, type))
                    continue;

                tableNameBuilder.Append(name);
                tableTypeBuilder.Append(type);
                validityBuilder.Append(true);
                length++;

                // Columns
                if (depth == GetObjectsDepth.Tables)
                {
                    tableColumns.Add(null);
                }
                else
                {
                    tableColumns.Add(BuildColumns(name, columnNamePattern));
                }

                // Constraints — Kusto doesn't have traditional constraints
                tableConstraints.Add(null);
            }

            var dataArrays = new IArrowArray[]
            {
                tableNameBuilder.Build(),
                tableTypeBuilder.Build(),
                ArrowListArrayHelper.BuildListArray(tableColumns, new StructType(StandardSchemas.ColumnSchema)),
                ArrowListArrayHelper.BuildListArray(tableConstraints, new StructType(StandardSchemas.ConstraintSchema)),
            };

            return new StructArray(new StructType(StandardSchemas.TableSchema), length, dataArrays, validityBuilder.Build());
        }

        StructArray BuildColumns(string tableName, string? columnNamePattern)
        {
            // Reuse our existing schema parsing
            Schema tableSchema;
            try
            {
                tableSchema = GetTableSchema(null, null, tableName);
            }
            catch
            {
                // If we can't get the schema, return empty columns
                return BuildEmptyColumns();
            }

            var columnNameBuilder = new StringArray.Builder();
            var ordinalBuilder = new Int32Array.Builder();
            var remarksBuilder = new StringArray.Builder();
            var xdbcDataTypeBuilder = new Int16Array.Builder();
            var xdbcTypeNameBuilder = new StringArray.Builder();
            var xdbcColumnSizeBuilder = new Int32Array.Builder();
            var xdbcDecimalDigitsBuilder = new Int16Array.Builder();
            var xdbcNumPrecRadixBuilder = new Int16Array.Builder();
            var xdbcNullableBuilder = new Int16Array.Builder();
            var xdbcColumnDefBuilder = new StringArray.Builder();
            var xdbcSqlDataTypeBuilder = new Int16Array.Builder();
            var xdbcDatetimeSubBuilder = new Int16Array.Builder();
            var xdbcCharOctetLengthBuilder = new Int32Array.Builder();
            var xdbcIsNullableBuilder = new StringArray.Builder();
            var xdbcScopeCatalogBuilder = new StringArray.Builder();
            var xdbcScopeSchemaBuilder = new StringArray.Builder();
            var xdbcScopeTableBuilder = new StringArray.Builder();
            var xdbcIsAutoIncrementBuilder = new BooleanArray.Builder();
            var xdbcIsGeneratedColumnBuilder = new BooleanArray.Builder();
            var validityBuilder = new ArrowBuffer.BitmapBuilder();
            int length = 0;

            for (int i = 0; i < tableSchema.FieldsList.Count; i++)
            {
                var field = tableSchema.FieldsList[i];

                if (columnNamePattern != null && !MatchesPattern(field.Name, columnNamePattern))
                    continue;

                columnNameBuilder.Append(field.Name);
                ordinalBuilder.Append(i + 1); // 1-based ordinal
                remarksBuilder.AppendNull();
                xdbcDataTypeBuilder.AppendNull();
                xdbcTypeNameBuilder.Append(ArrowTypeToKustoTypeName(field.DataType));
                xdbcColumnSizeBuilder.AppendNull();
                xdbcDecimalDigitsBuilder.AppendNull();
                xdbcNumPrecRadixBuilder.AppendNull();
                xdbcNullableBuilder.Append((short)(field.IsNullable ? 1 : 0));
                xdbcColumnDefBuilder.AppendNull();
                xdbcSqlDataTypeBuilder.AppendNull();
                xdbcDatetimeSubBuilder.AppendNull();
                xdbcCharOctetLengthBuilder.AppendNull();
                xdbcIsNullableBuilder.Append(field.IsNullable ? "YES" : "NO");
                xdbcScopeCatalogBuilder.AppendNull();
                xdbcScopeSchemaBuilder.AppendNull();
                xdbcScopeTableBuilder.AppendNull();
                xdbcIsAutoIncrementBuilder.Append(false);
                xdbcIsGeneratedColumnBuilder.Append(false);
                validityBuilder.Append(true);
                length++;
            }

            var dataArrays = new IArrowArray[]
            {
                columnNameBuilder.Build(),
                ordinalBuilder.Build(),
                remarksBuilder.Build(),
                xdbcDataTypeBuilder.Build(),
                xdbcTypeNameBuilder.Build(),
                xdbcColumnSizeBuilder.Build(),
                xdbcDecimalDigitsBuilder.Build(),
                xdbcNumPrecRadixBuilder.Build(),
                xdbcNullableBuilder.Build(),
                xdbcColumnDefBuilder.Build(),
                xdbcSqlDataTypeBuilder.Build(),
                xdbcDatetimeSubBuilder.Build(),
                xdbcCharOctetLengthBuilder.Build(),
                xdbcIsNullableBuilder.Build(),
                xdbcScopeCatalogBuilder.Build(),
                xdbcScopeSchemaBuilder.Build(),
                xdbcScopeTableBuilder.Build(),
                xdbcIsAutoIncrementBuilder.Build(),
                xdbcIsGeneratedColumnBuilder.Build(),
            };

            return new StructArray(new StructType(StandardSchemas.ColumnSchema), length, dataArrays, validityBuilder.Build());
        }

        StructArray BuildEmptyColumns()
        {
            return BuildColumns("__nonexistent__", "____nomatch____");
        }

        List<(string name, string type)> FetchTableList()
        {
            var task = Task.Run(async () =>
            {
                var (reader, lifetime) = await HttpClient.ExecuteManagementAsync(".show tables").ConfigureAwait(false);
                using (lifetime)
                {
                    var parser = new KustoResponseParser(reader);
                    return await parser.ParseAsync().ConfigureAwait(false);
                }
            });

            var batch = task.GetAwaiter().GetResult();
            var result = new List<(string name, string type)>();

            // .show tables returns: TableName, DatabaseName, Folder, DocString
            int nameCol = -1;
            for (int i = 0; i < batch.Schema.FieldsList.Count; i++)
            {
                if (batch.Schema.FieldsList[i].Name == "TableName")
                    nameCol = i;
            }

            if (nameCol >= 0)
            {
                var nameArray = (StringArray)batch.Column(nameCol);
                for (int i = 0; i < batch.Length; i++)
                {
                    string? name = nameArray.GetString(i);
                    if (name != null)
                        result.Add((name, "Table"));
                }
            }

            return result;
        }

        IArrowArrayStream BuildEmptyGetObjects()
        {
            var catalogNameBuilder = new StringArray.Builder();
            var catalogDbSchemas = new List<IArrowArray?>();
            var dataArrays = new IArrowArray[]
            {
                catalogNameBuilder.Build(),
                ArrowListArrayHelper.BuildListArray(catalogDbSchemas, new StructType(StandardSchemas.DbSchemaSchema)),
            };
            var batch = new RecordBatch(StandardSchemas.GetObjectsSchema, dataArrays, 0);
            return new SingleBatchStream(StandardSchemas.GetObjectsSchema, batch);
        }

        IArrowArrayStream BuildGetObjectsResult(string catalogName, GetObjectsDepth depth, StructArray? dbSchemas)
        {
            var catalogNameBuilder = new StringArray.Builder();
            var catalogDbSchemasList = new List<IArrowArray?>();
            catalogNameBuilder.Append(catalogName);
            catalogDbSchemasList.Add(dbSchemas);

            var dataArrays = new IArrowArray[]
            {
                catalogNameBuilder.Build(),
                ArrowListArrayHelper.BuildListArray(catalogDbSchemasList, new StructType(StandardSchemas.DbSchemaSchema)),
            };

            var batch = new RecordBatch(StandardSchemas.GetObjectsSchema, dataArrays, 1);
            return new SingleBatchStream(StandardSchemas.GetObjectsSchema, batch);
        }

        static string ArrowTypeToKustoTypeName(IArrowType arrowType)
        {
            return arrowType switch
            {
                StringType => "string",
                Int32Type => "int",
                Int64Type => "long",
                DoubleType => "real",
                BooleanType => "bool",
                TimestampType => "datetime",
                DurationType => "timespan",
                _ => "dynamic",
            };
        }

        internal static bool MatchesPattern(string value, string pattern)
        {
            // Simple pattern matching: supports SQL LIKE-style % and _ wildcards
            if (pattern == "%") return true;
            if (!pattern.Contains('%') && !pattern.Contains('_'))
                return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);

            // Convert to simple regex-like matching
            int pi = 0, vi = 0;
            while (pi < pattern.Length && vi < value.Length)
            {
                char pc = pattern[pi];
                if (pc == '%')
                {
                    pi++;
                    if (pi == pattern.Length) return true; // trailing %
                    while (vi < value.Length)
                    {
                        if (MatchesPattern(value.Substring(vi), pattern.Substring(pi)))
                            return true;
                        vi++;
                    }
                    return false;
                }
                else if (pc == '_')
                {
                    pi++;
                    vi++;
                }
                else
                {
                    if (char.ToLowerInvariant(pc) != char.ToLowerInvariant(value[vi]))
                        return false;
                    pi++;
                    vi++;
                }
            }
            // Consume trailing %
            while (pi < pattern.Length && pattern[pi] == '%') pi++;
            return pi == pattern.Length && vi == value.Length;
        }

        static bool ContainsIgnoreCase(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public override Schema GetTableSchema(string? catalog, string? dbSchema, string tableName)
        {
            ThrowIfDisposed();

            var task = Task.Run(async () =>
            {
                string command = $".show table [{tableName}] cslschema";
                var (reader, lifetime) = await HttpClient.ExecuteManagementAsync(command).ConfigureAwait(false);
                using (lifetime)
                {
                    var parser = new KustoResponseParser(reader);
                    var batch = await parser.ParseAsync().ConfigureAwait(false);
                    return ParseTableSchema(batch);
                }
            });

            return task.GetAwaiter().GetResult();
        }

        internal static Schema ParseTableSchema(RecordBatch batch)
        {
            // The ".show table T cslschema" command returns a table with a "Schema" column
            // containing a comma-separated list of "ColumnName:ColumnType" pairs.
            if (batch.Length == 0)
                throw new AdbcException("Table not found.", AdbcStatusCode.NotFound);

            // Find the Schema column
            int schemaCol = -1;
            for (int i = 0; i < batch.Schema.FieldsList.Count; i++)
            {
                if (batch.Schema.FieldsList[i].Name == "Schema")
                {
                    schemaCol = i;
                    break;
                }
            }

            if (schemaCol < 0)
                throw new AdbcException("Unexpected schema response format.", AdbcStatusCode.InternalError);

            var schemaArray = (StringArray)batch.Column(schemaCol);
            string? schemaStr = schemaArray.GetString(0);
            if (string.IsNullOrEmpty(schemaStr))
                return new Schema(new List<Field>(), null);

            var fields = new List<Field>();
            foreach (var pair in schemaStr!.Split(','))
            {
                var parts = pair.Trim().Split(':');
                if (parts.Length >= 2)
                {
                    string colName = parts[0].Trim();
                    string colType = parts[1].Trim();
                    var property = PropertyFactory.Create(colName, colType);
                    fields.Add(new Field(colName, property.Type, nullable: true));
                }
            }

            return new Schema(fields, null);
        }

        public override IArrowArrayStream GetTableTypes()
        {
            ThrowIfDisposed();

            // Kusto has "Table" as the primary table type
            var field = new Field("table_type", StringType.Default, false);
            var schema = new Schema(new[] { field }, null);

            var builder = new StringArray.Builder();
            builder.Append("Table");
            builder.Append("MaterializedView");
            builder.Append("ExternalTable");

            var array = builder.Build();
            var batch = new RecordBatch(schema, new IArrowArray[] { array }, 3);

            return new SingleBatchStream(schema, batch);
        }

        public override IArrowArrayStream GetInfo(IReadOnlyList<AdbcInfoCode> codes)
        {
            ThrowIfDisposed();

            var infoNameBuilder = new UInt32Array.Builder();
            var infoValueBuilder = new StringArray.Builder();

            foreach (var code in codes)
            {
                switch (code)
                {
                    case AdbcInfoCode.VendorName:
                        infoNameBuilder.Append((uint)code);
                        infoValueBuilder.Append("Azure Data Explorer (Kusto)");
                        break;
                    case AdbcInfoCode.DriverName:
                        infoNameBuilder.Append((uint)code);
                        infoValueBuilder.Append("KustoAdbc");
                        break;
                    case AdbcInfoCode.DriverVersion:
                        infoNameBuilder.Append((uint)code);
                        infoValueBuilder.Append("0.1.0");
                        break;
                }
            }

            var infoNameArray = infoNameBuilder.Build();
            var infoValueArray = infoValueBuilder.Build();

            var schema = new Schema(new[]
            {
                new Field("info_name", UInt32Type.Default, false),
                new Field("info_value", StringType.Default, true),
            }, null);

            var batch = new RecordBatch(schema, new IArrowArray[] { infoNameArray, infoValueArray }, infoNameArray.Length);
            return new SingleBatchStream(schema, batch);
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KustoConnection));
        }

        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _httpClient?.Dispose();
                _httpClient = null;
            }
        }
    }

    /// <summary>
    /// A simple IArrowArrayStream that returns a single RecordBatch.
    /// </summary>
    sealed class SingleBatchStream : IArrowArrayStream
    {
        readonly Schema _schema;
        RecordBatch? _batch;

        public SingleBatchStream(Schema schema, RecordBatch batch)
        {
            _schema = schema;
            _batch = batch;
        }

        public Schema Schema => _schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = _batch;
            _batch = null;
            return new ValueTask<RecordBatch?>(batch);
        }

        public void Dispose() { _batch = null; }
    }
}
