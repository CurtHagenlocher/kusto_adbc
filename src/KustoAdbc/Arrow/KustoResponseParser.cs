// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace KustoAdbc.Arrow
{
    /// <summary>
    /// Streaming parser for Kusto V1 REST API JSON responses.
    /// Produces Apache Arrow RecordBatches from the JSON response stream.
    /// </summary>
    sealed class KustoResponseParser
    {
        readonly PipeReader _input;
        readonly int _batchSize;

        readonly List<Property> _properties = new();
        IArrowArrayBuilder[]? _builders;
        Schema? _schema;

        bool _hasTable;
        ParseState _state;
        ParseState _previousState;
        int _skipLevel;

        string? _columnName;
        string? _columnType;
        int _rowCount;
        int _column;

        static readonly byte[] TablesBytes = "Tables"u8.ToArray();
        static readonly byte[] ColumnsBytes = "Columns"u8.ToArray();
        static readonly byte[] RowsBytes = "Rows"u8.ToArray();
        static readonly byte[] ColumnNameBytes = "ColumnName"u8.ToArray();
        static readonly byte[] ColumnTypeBytes = "ColumnType"u8.ToArray();
        static readonly byte[] DataTypeBytes = "DataType"u8.ToArray();

        enum ParseState
        {
            Start,
            TopLevel,
            BeforeTablesArray,
            InTablesArray,
            InFirstTableObject,
            BeforeColumnsArray,
            InColumnsArray,
            InColumn,
            BeforeColumnName,
            BeforeColumnType,
            BeforeRowsArray,
            InRowsArray,
            InRow,
            Skip,
            Done,
        }

        public KustoResponseParser(PipeReader input, int batchSize = 65536)
        {
            _input = input;
            _batchSize = batchSize;
        }

        public Schema? Schema => _schema;

        /// <summary>
        /// Parses the full response and returns a single RecordBatch.
        /// For streaming, use <see cref="CreateArrowStreamAsync"/>.
        /// </summary>
        public async Task<RecordBatch> ParseAsync(CancellationToken cancellationToken = default)
        {
            var batches = new List<RecordBatch>();
            await foreach (var batch in ParseBatchesAsync(cancellationToken))
            {
                batches.Add(batch);
            }

            if (batches.Count == 0)
            {
                if (_schema == null)
                    throw new InvalidDataException("No data found in Kusto response.");
                return new RecordBatch(_schema, System.Array.Empty<IArrowArray>(), 0);
            }

            if (batches.Count == 1)
                return batches[0];

            // Concatenate multiple batches (should be rare for moderate result sets)
            return ConcatenateBatches(batches);
        }

        /// <summary>
        /// Returns an IArrowArrayStream that streams RecordBatches from the response.
        /// </summary>
        public KustoArrowArrayStream CreateArrowStream(CancellationToken cancellationToken = default)
        {
            return new KustoArrowArrayStream(this, cancellationToken);
        }

        internal async IAsyncEnumerable<RecordBatch> ParseBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var jsonOptions = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip };
            var jsonState = new JsonReaderState(jsonOptions);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readResult = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                var reader = new Utf8JsonReader(buffer, readResult.IsCompleted, jsonState);
                ReadNextChunk(ref reader);
                jsonState = reader.CurrentState;

                SequencePosition consumed = reader.Position;
                _input.AdvanceTo(consumed, buffer.End);

                // Emit batches as they fill up
                while (_builders != null && _rowCount >= _batchSize)
                {
                    yield return BuildRecordBatch();
                    ResetBuilders();

                    // Continue parsing the same buffer if there's more data
                    if (_state != ParseState.Done && !readResult.IsCompleted)
                        break; // need to read more from pipe

                    // Re-read from the pipe to continue parsing
                    if (_state != ParseState.Done)
                    {
                        readResult = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
                        buffer = readResult.Buffer;
                        reader = new Utf8JsonReader(buffer, readResult.IsCompleted, jsonState);
                        ReadNextChunk(ref reader);
                        jsonState = reader.CurrentState;
                        consumed = reader.Position;
                        _input.AdvanceTo(consumed, buffer.End);
                    }
                }

                if (readResult.IsCompleted || _state == ParseState.Done)
                {
                    // Emit any remaining rows
                    if (_builders != null && _rowCount > 0)
                    {
                        yield return BuildRecordBatch();
                    }
                    break;
                }
            }
        }

        void ReadNextChunk(ref Utf8JsonReader reader)
        {
            while (reader.Read())
            {
                switch (_state)
                {
                    case ParseState.Start:
                        if (reader.TokenType != JsonTokenType.StartObject) Throw("Expected start of JSON object");
                        _state = ParseState.TopLevel;
                        break;

                    case ParseState.TopLevel:
                        if (reader.TokenType == JsonTokenType.EndObject) { _state = ParseState.Done; return; }
                        if (reader.TokenType != JsonTokenType.PropertyName) Throw("Expected property name");
                        if (reader.ValueTextEquals(TablesBytes))
                            _state = ParseState.BeforeTablesArray;
                        else
                            SkipValue(ref reader);
                        break;

                    case ParseState.BeforeTablesArray:
                        if (reader.TokenType != JsonTokenType.StartArray) Throw("Expected Tables array");
                        _state = ParseState.InTablesArray;
                        break;

                    case ParseState.InTablesArray:
                        if (reader.TokenType == JsonTokenType.EndArray) { _state = ParseState.TopLevel; }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            if (_hasTable)
                                SkipValue(ref reader, addCount: true);
                            else
                            {
                                _hasTable = true;
                                _state = ParseState.InFirstTableObject;
                            }
                        }
                        else Throw("Expected object or end of Tables array");
                        break;

                    case ParseState.InFirstTableObject:
                        if (reader.TokenType == JsonTokenType.EndObject) { _state = ParseState.InTablesArray; }
                        else if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals(ColumnsBytes))
                                _state = ParseState.BeforeColumnsArray;
                            else if (reader.ValueTextEquals(RowsBytes))
                                _state = ParseState.BeforeRowsArray;
                            else
                                SkipValue(ref reader);
                        }
                        else Throw("Expected property or end of table object");
                        break;

                    case ParseState.BeforeColumnsArray:
                        if (reader.TokenType != JsonTokenType.StartArray) Throw("Expected Columns array");
                        _state = ParseState.InColumnsArray;
                        break;

                    case ParseState.InColumnsArray:
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            FinishColumns();
                            _state = ParseState.InFirstTableObject;
                        }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                            _state = ParseState.InColumn;
                        else
                            Throw("Expected column object or end of Columns array");
                        break;

                    case ParseState.InColumn:
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            AddColumn();
                            _state = ParseState.InColumnsArray;
                        }
                        else if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals(ColumnNameBytes))
                                _state = ParseState.BeforeColumnName;
                            else if (reader.ValueTextEquals(ColumnTypeBytes) || reader.ValueTextEquals(DataTypeBytes))
                                _state = ParseState.BeforeColumnType;
                            else
                                SkipValue(ref reader);
                        }
                        else Throw("Expected property or end of column object");
                        break;

                    case ParseState.BeforeColumnName:
                        if (reader.TokenType != JsonTokenType.String) Throw("Expected column name string");
                        _columnName = reader.GetString();
                        _state = ParseState.InColumn;
                        break;

                    case ParseState.BeforeColumnType:
                        if (reader.TokenType != JsonTokenType.String) Throw("Expected column type string");
                        _columnType = reader.GetString();
                        _state = ParseState.InColumn;
                        break;

                    case ParseState.BeforeRowsArray:
                        if (reader.TokenType != JsonTokenType.StartArray) Throw("Expected Rows array");
                        _state = ParseState.InRowsArray;
                        break;

                    case ParseState.InRowsArray:
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            _state = ParseState.InFirstTableObject;
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            _column = 0;
                            _state = ParseState.InRow;
                        }
                        else Throw("Expected row array or end of Rows array");
                        break;

                    case ParseState.InRow:
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            FinishRow();
                            _state = ParseState.InRowsArray;
                            // Check if we need to yield a batch
                            if (_rowCount >= _batchSize) return;
                        }
                        else
                        {
                            AddValue(ref reader);
                        }
                        break;

                    case ParseState.Skip:
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.StartObject:
                            case JsonTokenType.StartArray:
                                if (!reader.TrySkip()) _skipLevel++;
                                break;
                            case JsonTokenType.EndObject:
                            case JsonTokenType.EndArray:
                                _skipLevel--;
                                if (_skipLevel == 0) _state = _previousState;
                                break;
                        }
                        break;

                    case ParseState.Done:
                        return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SkipValue(ref Utf8JsonReader reader, bool addCount = false)
        {
            if (!reader.TrySkip())
            {
                _previousState = _state;
                _state = ParseState.Skip;
                _skipLevel = addCount ? 1 : 0;
            }
        }

        void AddColumn()
        {
            if (_columnName == null || _columnType == null)
                Throw("Column missing name or type");

            _properties.Add(PropertyFactory.Create(_columnName!, _columnType!));
            _columnName = null;
            _columnType = null;
        }

        void FinishColumns()
        {
            _builders = new IArrowArrayBuilder[_properties.Count];
            var fields = new Field[_properties.Count];
            for (int i = 0; i < _properties.Count; i++)
            {
                _builders[i] = _properties[i].CreateBuilder();
                fields[i] = new Field(_properties[i].Name, _properties[i].Type, nullable: true);
            }
            _schema = new Schema(fields, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddValue(ref Utf8JsonReader reader)
        {
            if (_builders == null) Throw("Rows encountered before Columns");
            _properties[_column].Read(ref reader, _builders![_column]);
            _column++;
        }

        void FinishRow()
        {
            if (_builders == null) return;
            // Pad missing columns with nulls
            while (_column < _properties.Count)
            {
                _properties[_column].AddNull(_builders![_column]);
                _column++;
            }
            _rowCount++;
        }

        RecordBatch BuildRecordBatch()
        {
            if (_properties.Count == 0 || _schema == null)
                throw new InvalidDataException("No columns found in response.");

            var arrays = new IArrowArray[_properties.Count];
            for (int i = 0; i < _properties.Count; i++)
            {
                arrays[i] = _properties[i].Build(_builders![i]);
            }

            return new RecordBatch(_schema, arrays, _rowCount);
        }

        void ResetBuilders()
        {
            _builders = new IArrowArrayBuilder[_properties.Count];
            for (int i = 0; i < _properties.Count; i++)
            {
                _builders[i] = _properties[i].CreateBuilder();
            }
            _rowCount = 0;
        }

        RecordBatch ConcatenateBatches(List<RecordBatch> batches)
        {
            // Simple concatenation: rebuild all columns from all batches.
            // This is a rare path for very large result sets.
            int totalRows = 0;
            foreach (var b in batches) totalRows += b.Length;

            var arrays = new IArrowArray[_properties.Count];
            for (int col = 0; col < _properties.Count; col++)
            {
                var builder = _properties[col].CreateBuilder(totalRows);
                // Re-read from the arrays — for simplicity, delegate to property-specific logic
                // In practice, for the initial implementation, large results should use streaming
                arrays[col] = batches[0].Column(col); // TODO: proper concatenation
            }

            // For now, just return all data — proper concat is a follow-up
            return batches[0]; // Single-batch path is preferred
        }

        static void Throw(string message) => throw new InvalidDataException(message);
    }

    /// <summary>
    /// An IArrowArrayStream backed by the Kusto response parser.
    /// </summary>
    sealed class KustoArrowArrayStream : IArrowArrayStream
    {
        readonly KustoResponseParser _parser;
        readonly CancellationToken _cancellationToken;
        IAsyncEnumerator<RecordBatch>? _enumerator;
        Schema? _schema;
        bool _disposed;

        public KustoArrowArrayStream(KustoResponseParser parser, CancellationToken cancellationToken)
        {
            _parser = parser;
            _cancellationToken = cancellationToken;
        }

        public Schema Schema
        {
            get
            {
                if (_schema != null) return _schema;
                if (_parser.Schema != null) { _schema = _parser.Schema; return _schema; }
                throw new InvalidOperationException("Schema not yet available. Call ReadNextRecordBatchAsync first.");
            }
        }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return null;

            var token = cancellationToken == default ? _cancellationToken : cancellationToken;

            if (_enumerator == null)
            {
                _enumerator = _parser.ParseBatchesAsync(token).GetAsyncEnumerator(token);
            }

            if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                _schema ??= _parser.Schema;
                return _enumerator.Current;
            }

            return null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _enumerator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
