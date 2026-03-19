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
            return GetObjectsImpl(depth, tableNamePattern, columnNamePattern);
        }

        IArrowArrayStream GetObjectsImpl(
            GetObjectsDepth depth,
            string? tableNamePattern,
            string? columnNamePattern)
        {
            // For now, return a minimal implementation using .show tables
            // Full implementation with nested catalog/schema structure is a follow-up
            var batches = new List<RecordBatch>();

            // Execute ".show tables" to get table list
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
            return new SingleBatchStream(batch.Schema, batch);
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

        static Schema ParseTableSchema(RecordBatch batch)
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
