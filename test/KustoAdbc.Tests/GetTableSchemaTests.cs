using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;
using Xunit;

namespace KustoAdbc.Tests
{
    public class GetTableSchemaTests
    {
        /// <summary>
        /// Builds a mock RecordBatch matching the output of `.show table T cslschema`.
        /// The command returns columns: TableName, Schema, DatabaseName, Folder, DocString.
        /// We only care about the Schema column.
        /// </summary>
        static RecordBatch BuildCslSchemaResponse(string tableName, string schemaString)
        {
            var tableNameBuilder = new StringArray.Builder();
            var schemaBuilder = new StringArray.Builder();
            var dbNameBuilder = new StringArray.Builder();
            var folderBuilder = new StringArray.Builder();
            var docStringBuilder = new StringArray.Builder();

            tableNameBuilder.Append(tableName);
            schemaBuilder.Append(schemaString);
            dbNameBuilder.Append("testdb");
            folderBuilder.Append("");
            docStringBuilder.Append("");

            var schema = new Schema(new[]
            {
                new Field("TableName", StringType.Default, false),
                new Field("Schema", StringType.Default, false),
                new Field("DatabaseName", StringType.Default, false),
                new Field("Folder", StringType.Default, true),
                new Field("DocString", StringType.Default, true),
            }, null);

            return new RecordBatch(schema, new IArrowArray[]
            {
                tableNameBuilder.Build(),
                schemaBuilder.Build(),
                dbNameBuilder.Build(),
                folderBuilder.Build(),
                docStringBuilder.Build(),
            }, 1);
        }

        [Fact]
        public void ParsesAllKustoTypes()
        {
            string cslSchema = string.Join(",",
                "Name:string",
                "Id:long",
                "Count:int",
                "Score:real",
                "IsActive:bool",
                "Created:datetime",
                "Duration:timespan",
                "UniqueId:guid",
                "Amount:decimal",
                "Payload:dynamic");

            var batch = BuildCslSchemaResponse("MyTable", cslSchema);
            var result = KustoConnection.ParseTableSchema(batch);

            Assert.Equal(10, result.FieldsList.Count);

            Assert.Equal("Name", result.FieldsList[0].Name);
            Assert.IsType<StringType>(result.FieldsList[0].DataType);

            Assert.Equal("Id", result.FieldsList[1].Name);
            Assert.IsType<Int64Type>(result.FieldsList[1].DataType);

            Assert.Equal("Count", result.FieldsList[2].Name);
            Assert.IsType<Int32Type>(result.FieldsList[2].DataType);

            Assert.Equal("Score", result.FieldsList[3].Name);
            Assert.IsType<DoubleType>(result.FieldsList[3].DataType);

            Assert.Equal("IsActive", result.FieldsList[4].Name);
            Assert.IsType<BooleanType>(result.FieldsList[4].DataType);

            Assert.Equal("Created", result.FieldsList[5].Name);
            Assert.IsType<TimestampType>(result.FieldsList[5].DataType);

            Assert.Equal("Duration", result.FieldsList[6].Name);
            Assert.IsType<DurationType>(result.FieldsList[6].DataType);

            Assert.Equal("UniqueId", result.FieldsList[7].Name);
            Assert.IsType<StringType>(result.FieldsList[7].DataType); // guid → string

            Assert.Equal("Amount", result.FieldsList[8].Name);
            Assert.IsType<DoubleType>(result.FieldsList[8].DataType); // decimal → double

            Assert.Equal("Payload", result.FieldsList[9].Name);
            Assert.IsType<StringType>(result.FieldsList[9].DataType); // dynamic → string
        }

        [Fact]
        public void ParsesSingleColumn()
        {
            var batch = BuildCslSchemaResponse("T", "Value:long");
            var result = KustoConnection.ParseTableSchema(batch);

            Assert.Single(result.FieldsList);
            Assert.Equal("Value", result.FieldsList[0].Name);
            Assert.IsType<Int64Type>(result.FieldsList[0].DataType);
        }

        [Fact]
        public void AllFieldsAreNullable()
        {
            var batch = BuildCslSchemaResponse("T", "A:string,B:int,C:bool");
            var result = KustoConnection.ParseTableSchema(batch);

            foreach (var field in result.FieldsList)
                Assert.True(field.IsNullable, $"Field '{field.Name}' should be nullable");
        }

        [Fact]
        public void EmptySchema_ReturnsEmptyFields()
        {
            var batch = BuildCslSchemaResponse("T", "");
            var result = KustoConnection.ParseTableSchema(batch);

            Assert.Empty(result.FieldsList);
        }

        [Fact]
        public void EmptyBatch_ThrowsNotFound()
        {
            var schema = new Schema(new[]
            {
                new Field("TableName", StringType.Default, false),
                new Field("Schema", StringType.Default, false),
            }, null);

            var batch = new RecordBatch(schema, new IArrowArray[]
            {
                new StringArray.Builder().Build(),
                new StringArray.Builder().Build(),
            }, 0);

            var ex = Assert.Throws<Apache.Arrow.Adbc.AdbcException>(
                () => KustoConnection.ParseTableSchema(batch));
            Assert.Contains("not found", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void HandlesWhitespaceInSchema()
        {
            var batch = BuildCslSchemaResponse("T", " Name : string , Age : int ");
            var result = KustoConnection.ParseTableSchema(batch);

            Assert.Equal(2, result.FieldsList.Count);
            Assert.Equal("Name", result.FieldsList[0].Name);
            Assert.Equal("Age", result.FieldsList[1].Name);
        }

        [Fact]
        public void UnknownType_FallsBackToString()
        {
            var batch = BuildCslSchemaResponse("T", "Col:somefuturetype");
            var result = KustoConnection.ParseTableSchema(batch);

            Assert.Single(result.FieldsList);
            Assert.Equal("Col", result.FieldsList[0].Name);
            Assert.IsType<StringType>(result.FieldsList[0].DataType);
        }
    }
}
