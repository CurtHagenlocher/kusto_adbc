// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using KustoAdbc.Arrow;

namespace KustoAdbc
{
    /// <summary>
    /// ADBC statement for executing KQL queries and Substrait plans against Kusto.
    /// </summary>
    public sealed class KustoStatement : AdbcStatement
    {
        readonly KustoConnection _connection;
        byte[]? _substraitPlan;
        bool _disposed;

        internal KustoStatement(KustoConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public override byte[]? SubstraitPlan
        {
            get => _substraitPlan;
            set => _substraitPlan = value;
        }

        public override QueryResult ExecuteQuery()
        {
            ThrowIfDisposed();

            string kql = ResolveKql();

            var task = Task.Run(async () =>
            {
                var (reader, lifetime) = await _connection.HttpClient.ExecuteQueryAsync(kql).ConfigureAwait(false);
                var parser = new KustoResponseParser(reader, _connection.BatchSize);
                var stream = parser.CreateArrowStream();

                // Wrap so the HTTP lifetime is disposed when the stream is done
                return new LifetimeManagedStream(stream, parser, lifetime);
            });

            var managedStream = task.GetAwaiter().GetResult();
            return new QueryResult(-1, managedStream);
        }

        public override UpdateResult ExecuteUpdate()
        {
            ThrowIfDisposed();

            string kql = ResolveKql();

            var task = Task.Run(async () =>
            {
                var (reader, lifetime) = await _connection.HttpClient.ExecuteManagementAsync(kql).ConfigureAwait(false);
                using (lifetime)
                {
                    var parser = new KustoResponseParser(reader);
                    var batch = await parser.ParseAsync().ConfigureAwait(false);
                    return new UpdateResult(batch.Length);
                }
            });

            return task.GetAwaiter().GetResult();
        }

        string ResolveKql()
        {
            if (SqlQuery != null && _substraitPlan != null)
                throw new AdbcException("Cannot set both SqlQuery and SubstraitPlan.", AdbcStatusCode.InvalidState);

            if (SqlQuery != null)
                return SqlQuery;

            if (_substraitPlan != null)
                return TranslateSubstrait(_substraitPlan);

            throw new AdbcException("No query or plan has been set.", AdbcStatusCode.InvalidState);
        }

        static string TranslateSubstrait(byte[] plan)
        {
            try
            {
                return Substrait.SubstraitToKqlTranslator.Translate(plan);
            }
            catch (Substrait.SubstraitTranslationException)
            {
                throw; // Already an AdbcException — propagate as-is
            }
            catch (Exception ex)
            {
                throw new AdbcException(
                    $"Failed to translate Substrait plan to KQL: {ex.Message}",
                    AdbcStatusCode.InvalidArgument,
                    ex);
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KustoStatement));
        }

        public override void Dispose()
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// Wraps an IArrowArrayStream and disposes additional resources when the stream is exhausted.
    /// </summary>
    sealed class LifetimeManagedStream : IArrowArrayStream
    {
        readonly KustoArrowArrayStream _inner;
        readonly KustoResponseParser _parser;
        readonly IDisposable _lifetime;
        bool _disposed;

        public LifetimeManagedStream(KustoArrowArrayStream inner, KustoResponseParser parser, IDisposable lifetime)
        {
            _inner = inner;
            _parser = parser;
            _lifetime = lifetime;
        }

        public Schema Schema => _inner.Schema;

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch == null)
            {
                // Stream exhausted — clean up HTTP resources
                DisposeResources();
            }
            return batch;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                DisposeResources();
            }
        }

        void DisposeResources()
        {
            if (!_disposed)
            {
                _disposed = true;
                _inner.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
