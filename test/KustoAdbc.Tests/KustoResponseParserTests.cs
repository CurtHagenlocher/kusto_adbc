// Copyright (c) Microsoft Corporation.  All rights reserved.

using System.IO.Pipelines;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using KustoAdbc.Arrow;
using Xunit;

namespace KustoAdbc.Tests
{
    public class KustoResponseParserTests
    {
        static PipeReader CreatePipeReader(string json)
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return PipeReader.Create(stream);
        }

        [Fact]
        public async Task ParseSimpleResponse_ReturnsCorrectRecordBatch()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Name"", ""ColumnType"": ""string"" },
                        { ""ColumnName"": ""Age"", ""ColumnType"": ""int"" },
                        { ""ColumnName"": ""Score"", ""ColumnType"": ""real"" }
                    ],
                    ""Rows"": [
                        [""Alice"", 30, 95.5],
                        [""Bob"", 25, 88.0],
                        [""Charlie"", 35, 72.3]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.NotNull(batch);
            Assert.Equal(3, batch.Length);
            Assert.Equal(3, batch.Schema.FieldsList.Count);

            // Check schema
            Assert.Equal("Name", batch.Schema.FieldsList[0].Name);
            Assert.IsType<StringType>(batch.Schema.FieldsList[0].DataType);
            Assert.Equal("Age", batch.Schema.FieldsList[1].Name);
            Assert.IsType<Int32Type>(batch.Schema.FieldsList[1].DataType);
            Assert.Equal("Score", batch.Schema.FieldsList[2].Name);
            Assert.IsType<DoubleType>(batch.Schema.FieldsList[2].DataType);

            // Check data
            var nameCol = (StringArray)batch.Column(0);
            Assert.Equal("Alice", nameCol.GetString(0));
            Assert.Equal("Bob", nameCol.GetString(1));
            Assert.Equal("Charlie", nameCol.GetString(2));

            var ageCol = (Int32Array)batch.Column(1);
            Assert.Equal(30, ageCol.GetValue(0));
            Assert.Equal(25, ageCol.GetValue(1));
            Assert.Equal(35, ageCol.GetValue(2));

            var scoreCol = (DoubleArray)batch.Column(2);
            Assert.Equal(95.5, scoreCol.GetValue(0));
            Assert.Equal(88.0, scoreCol.GetValue(1));
            Assert.Equal(72.3, scoreCol.GetValue(2));
        }

        [Fact]
        public async Task ParseWithNulls_HandlesNullValues()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Name"", ""ColumnType"": ""string"" },
                        { ""ColumnName"": ""Value"", ""ColumnType"": ""long"" }
                    ],
                    ""Rows"": [
                        [""A"", 100],
                        [null, 200],
                        [""C"", null]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(3, batch.Length);

            var nameCol = (StringArray)batch.Column(0);
            Assert.Equal("A", nameCol.GetString(0));
            Assert.True(nameCol.IsNull(1));
            Assert.Equal("C", nameCol.GetString(2));

            var valueCol = (Int64Array)batch.Column(1);
            Assert.Equal(100, valueCol.GetValue(0));
            Assert.Equal(200, valueCol.GetValue(1));
            Assert.True(valueCol.IsNull(2));
        }

        [Fact]
        public async Task ParseBooleans_HandlesAllBooleanFormats()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Flag"", ""ColumnType"": ""bool"" }
                    ],
                    ""Rows"": [
                        [true],
                        [false],
                        [null]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(3, batch.Length);
            var flagCol = (BooleanArray)batch.Column(0);
            Assert.True(flagCol.GetValue(0));
            Assert.False(flagCol.GetValue(1));
            Assert.True(flagCol.IsNull(2));
        }

        [Fact]
        public async Task ParseDateTime_ParsesTimestamps()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Timestamp"", ""ColumnType"": ""datetime"" }
                    ],
                    ""Rows"": [
                        [""2024-01-15T10:30:00Z""],
                        [null]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(2, batch.Length);
            var tsCol = (TimestampArray)batch.Column(0);
            Assert.False(tsCol.IsNull(0));
            Assert.True(tsCol.IsNull(1));
        }

        [Fact]
        public async Task ParseDynamic_PreservesJsonAsString()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Data"", ""ColumnType"": ""dynamic"" }
                    ],
                    ""Rows"": [
                        [{""key"": ""value""}],
                        [null],
                        [[1, 2, 3]]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(3, batch.Length);
            var dataCol = (StringArray)batch.Column(0);
            Assert.Contains("key", dataCol.GetString(0));
            Assert.True(dataCol.IsNull(1));
            Assert.Contains("[1,2,3]", dataCol.GetString(2)!.Replace(" ", ""));
        }

        [Fact]
        public async Task ParseEmptyRows_ReturnsEmptyBatch()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""Name"", ""ColumnType"": ""string"" }
                    ],
                    ""Rows"": []
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.NotNull(batch);
            Assert.Equal(0, batch.Length);
        }

        [Fact]
        public async Task ParseMultipleColumns_AllKustoTypes()
        {
            string json = @"{
                ""Tables"": [{
                    ""Columns"": [
                        { ""ColumnName"": ""StrCol"", ""ColumnType"": ""string"" },
                        { ""ColumnName"": ""IntCol"", ""ColumnType"": ""int"" },
                        { ""ColumnName"": ""LongCol"", ""ColumnType"": ""long"" },
                        { ""ColumnName"": ""RealCol"", ""ColumnType"": ""real"" },
                        { ""ColumnName"": ""BoolCol"", ""ColumnType"": ""bool"" },
                        { ""ColumnName"": ""DynCol"", ""ColumnType"": ""dynamic"" },
                        { ""ColumnName"": ""GuidCol"", ""ColumnType"": ""guid"" },
                        { ""ColumnName"": ""DecCol"", ""ColumnType"": ""decimal"" }
                    ],
                    ""Rows"": [
                        [""hello"", 42, 9999999999, 3.14, true, {""a"":1}, ""550e8400-e29b-41d4-a716-446655440000"", 123.456]
                    ]
                }]
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(1, batch.Length);
            Assert.Equal(8, batch.Schema.FieldsList.Count);

            Assert.Equal("hello", ((StringArray)batch.Column(0)).GetString(0));
            Assert.Equal(42, ((Int32Array)batch.Column(1)).GetValue(0));
            Assert.Equal(9999999999L, ((Int64Array)batch.Column(2)).GetValue(0));
            Assert.Equal(3.14, ((DoubleArray)batch.Column(3)).GetValue(0));
            Assert.True(((BooleanArray)batch.Column(4)).GetValue(0));
            Assert.Contains("a", ((StringArray)batch.Column(5)).GetString(0));
            Assert.Contains("550e8400", ((StringArray)batch.Column(6)).GetString(0));
            Assert.Equal(123.456, ((DoubleArray)batch.Column(7)).GetValue(0));
        }

        [Fact]
        public async Task ParseWithExtraProperties_IgnoresThem()
        {
            string json = @"{
                ""ExtraProp"": ""ignored"",
                ""Tables"": [{
                    ""TableName"": ""PrimaryResult"",
                    ""Columns"": [
                        { ""ColumnName"": ""X"", ""ColumnType"": ""int"", ""ExtraField"": true }
                    ],
                    ""Rows"": [[1], [2]],
                    ""SomeOtherField"": 42
                }],
                ""AnotherProp"": {}
            }";

            var pipeReader = CreatePipeReader(json);
            var parser = new KustoResponseParser(pipeReader);
            var batch = await parser.ParseAsync();

            Assert.Equal(2, batch.Length);
            Assert.Equal(1, ((Int32Array)batch.Column(0)).GetValue(0));
            Assert.Equal(2, ((Int32Array)batch.Column(0)).GetValue(1));
        }

        [Fact]
        public async Task StreamingParse_ProducesMultipleBatches()
        {
            // Build a JSON response with enough rows to trigger batching
            var sb = new StringBuilder();
            sb.Append(@"{ ""Tables"": [{ ""Columns"": [{ ""ColumnName"": ""Id"", ""ColumnType"": ""int"" }], ""Rows"": [");

            int rowCount = 200;
            for (int i = 0; i < rowCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"[{i}]");
            }
            sb.Append(@"] }] }");

            var pipeReader = CreatePipeReader(sb.ToString());
            var parser = new KustoResponseParser(pipeReader, batchSize: 50);
            var stream = parser.CreateArrowStream();

            int totalRows = 0;
            int batchCount = 0;
            while (true)
            {
                var batch = await stream.ReadNextRecordBatchAsync();
                if (batch == null) break;
                totalRows += batch.Length;
                batchCount++;
            }

            Assert.Equal(rowCount, totalRows);
            Assert.True(batchCount >= 2, $"Expected multiple batches but got {batchCount}");
        }
    }
}
