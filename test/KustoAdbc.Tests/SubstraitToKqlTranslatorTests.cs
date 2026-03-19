using System;
using System.Runtime.CompilerServices;
using System.Text;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class SubstraitToKqlTranslatorTests
    {
        /// <summary>
        /// Helper to build simple Substrait protobuf messages for testing.
        /// Constructs wire-format bytes directly.
        /// </summary>
        static class ProtobufBuilder
        {
            public static byte[] Tag(int fieldNumber, int wireType) => Varint((fieldNumber << 3) | wireType);

            public static byte[] Varint(int value)
            {
                var bytes = new System.Collections.Generic.List<byte>();
                uint v = (uint)value;
                while (v >= 0x80)
                {
                    bytes.Add((byte)(v | 0x80));
                    v >>= 7;
                }
                bytes.Add((byte)v);
                return bytes.ToArray();
            }

            public static byte[] Varint64(long value)
            {
                var bytes = new System.Collections.Generic.List<byte>();
                ulong v = (ulong)value;
                while (v >= 0x80)
                {
                    bytes.Add((byte)(v | 0x80));
                    v >>= 7;
                }
                bytes.Add((byte)v);
                return bytes.ToArray();
            }

            public static byte[] LengthDelimited(int fieldNumber, byte[] content)
            {
                var result = new System.Collections.Generic.List<byte>();
                result.AddRange(Tag(fieldNumber, 2)); // wire type 2 = length-delimited
                result.AddRange(Varint(content.Length));
                result.AddRange(content);
                return result.ToArray();
            }

            public static byte[] VarintField(int fieldNumber, int value)
            {
                var result = new System.Collections.Generic.List<byte>();
                result.AddRange(Tag(fieldNumber, 0)); // wire type 0 = varint
                result.AddRange(Varint(value));
                return result.ToArray();
            }

            public static byte[] VarintField64(int fieldNumber, long value)
            {
                var result = new System.Collections.Generic.List<byte>();
                result.AddRange(Tag(fieldNumber, 0)); // wire type 0 = varint
                result.AddRange(Varint64(value));
                return result.ToArray();
            }

            public static byte[] StringField(int fieldNumber, string value)
            {
                return LengthDelimited(fieldNumber, Encoding.UTF8.GetBytes(value));
            }

            public static byte[] Concat(params byte[][] arrays)
            {
                int total = 0;
                foreach (var a in arrays) total += a.Length;
                var result = new byte[total];
                int pos = 0;
                foreach (var a in arrays)
                {
                    Buffer.BlockCopy(a, 0, result, pos, a.Length);
                    pos += a.Length;
                }
                return result;
            }

            // Build a NamedTable message: field 1 = repeated string (names)
            public static byte[] NamedTable(string tableName)
            {
                return StringField(1, tableName);
            }

            // Build a ReadRel: field 7 = named_table (NamedTable)
            public static byte[] ReadRel(string tableName)
            {
                return LengthDelimited(7, NamedTable(tableName));
            }

            // Wrap a Rel variant in a Rel message
            // ReadRel = field 1, FilterRel = field 2, FetchRel = field 3,
            // AggregateRel = field 4, SortRel = field 5, JoinRel = field 6, ProjectRel = field 7
            public static byte[] Rel(int fieldNumber, byte[] relContent)
            {
                return LengthDelimited(fieldNumber, relContent);
            }

            // Build a PlanRel with a root: field 2 = RelRoot
            // RelRoot: field 1 = input (Rel)
            public static byte[] PlanRel(byte[] relContent)
            {
                var relRoot = LengthDelimited(1, relContent); // RelRoot.input
                return LengthDelimited(2, relRoot); // PlanRel.root
            }

            // Build a Plan: field 3 = relations (repeated PlanRel)
            public static byte[] Plan(byte[] planRelContent)
            {
                return LengthDelimited(3, planRelContent);
            }

            // Build a FieldReference expression
            // Expression.selection = field 2
            // FieldReference.direct_reference = field 1 (ReferenceSegment)
            // ReferenceSegment.struct_field = field 3
            // StructField.field = field 1 (int32)
            public static byte[] FieldRefExpression(int fieldIndex)
            {
                var structField = VarintField(1, fieldIndex);
                var refSegment = LengthDelimited(3, structField);
                var fieldRef = LengthDelimited(1, refSegment);
                return LengthDelimited(2, fieldRef); // Expression.selection
            }

            // Build a Literal expression
            // Expression.literal = field 1
            public static byte[] LiteralInt32Expression(int value)
            {
                var literal = VarintField(5, value); // i32 = field 5
                return LengthDelimited(1, literal);
            }

            public static byte[] LiteralInt64Expression(long value)
            {
                var literal = VarintField64(7, value); // i64 = field 7
                return LengthDelimited(1, literal);
            }

            public static byte[] LiteralStringExpression(string value)
            {
                var literal = StringField(12, value); // string = field 12
                return LengthDelimited(1, literal);
            }

            public static byte[] LiteralBoolExpression(bool value)
            {
                var literal = VarintField(1, value ? 1 : 0); // boolean = field 1
                return LengthDelimited(1, literal);
            }

            // Build a FilterRel: field 2 = input (Rel), field 3 = condition (Expression)
            public static byte[] FilterRel(byte[] inputRel, byte[] conditionExpr)
            {
                return Concat(
                    LengthDelimited(2, inputRel),
                    LengthDelimited(3, conditionExpr)
                );
            }

            // Build a ProjectRel: field 2 = input (Rel), field 3 = repeated Expression
            public static byte[] ProjectRel(byte[] inputRel, params byte[][] expressions)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                parts.Add(LengthDelimited(2, inputRel));
                foreach (var expr in expressions)
                    parts.Add(LengthDelimited(3, expr));
                return Concat(parts.ToArray());
            }

            // Build a FetchRel: field 2 = input, field 3 = offset (varint), field 4 = count (varint)
            public static byte[] FetchRel(byte[] inputRel, long offset, long count)
            {
                return Concat(
                    LengthDelimited(2, inputRel),
                    VarintField64(3, offset),
                    VarintField64(4, count)
                );
            }

            // Build a SortField: field 1 = expr (Expression), field 2 = direction (enum)
            public static byte[] SortField(byte[] expression, int direction)
            {
                return Concat(
                    LengthDelimited(1, expression),
                    VarintField(2, direction)
                );
            }

            // Build a SortRel: field 2 = input, field 3 = repeated SortField
            public static byte[] SortRel(byte[] inputRel, params byte[][] sortFields)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                parts.Add(LengthDelimited(2, inputRel));
                foreach (var sf in sortFields)
                    parts.Add(LengthDelimited(3, sf));
                return Concat(parts.ToArray());
            }

            // Build AggregateRel: field 2 = input, field 3 = groupings, field 4 = measures
            // Grouping: field 1 = repeated Expression (grouping_expressions)
            public static byte[] Grouping(params byte[][] expressions)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                foreach (var expr in expressions)
                    parts.Add(LengthDelimited(1, expr));
                return Concat(parts.ToArray());
            }

            // Measure: field 1 = measure (Expression)
            public static byte[] Measure(byte[] expression)
            {
                return LengthDelimited(1, expression);
            }

            public static byte[] AggregateRel(byte[] inputRel, byte[][] groupings, byte[][] measures)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                parts.Add(LengthDelimited(2, inputRel));
                foreach (var g in groupings)
                    parts.Add(LengthDelimited(3, g));
                foreach (var m in measures)
                    parts.Add(LengthDelimited(4, m));
                return Concat(parts.ToArray());
            }

            // Build JoinRel: field 2 = left, field 3 = right, field 4 = expression, field 5 = type
            public static byte[] JoinRel(byte[] left, byte[] right, byte[]? condition, int joinType)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                parts.Add(LengthDelimited(2, left));
                parts.Add(LengthDelimited(3, right));
                if (condition != null)
                    parts.Add(LengthDelimited(4, condition));
                parts.Add(VarintField(5, joinType));
                return Concat(parts.ToArray());
            }
        }

        [Fact]
        public void SimpleRead_TranslatesToTableName()
        {
            // Plan { relations: [PlanRel { root: RelRoot { input: Rel { read: ReadRel { named_table: { names: ["MyTable"] } } } } }] }
            var readRel = ProtobufBuilder.ReadRel("MyTable");
            var rel = ProtobufBuilder.Rel(1, readRel); // Rel.read = field 1
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Equal("MyTable", kql);
        }

        [Fact]
        public void FilterRel_TranslatesToWhere()
        {
            var readRel = ProtobufBuilder.ReadRel("Events");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            // Condition: literal true (simplified)
            var condition = ProtobufBuilder.LiteralBoolExpression(true);

            var filterRel = ProtobufBuilder.FilterRel(inputRel, condition);
            var rel = ProtobufBuilder.Rel(2, filterRel); // Rel.filter = field 2
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Events", kql);
            Assert.Contains("| where", kql);
            Assert.Contains("true", kql);
        }

        [Fact]
        public void ProjectRel_TranslatesToProject()
        {
            var readRel = ProtobufBuilder.ReadRel("Users");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var expr1 = ProtobufBuilder.FieldRefExpression(0);
            var expr2 = ProtobufBuilder.FieldRefExpression(1);

            var projectRel = ProtobufBuilder.ProjectRel(inputRel, expr1, expr2);
            var rel = ProtobufBuilder.Rel(7, projectRel); // Rel.project = field 7
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Users", kql);
            Assert.Contains("| project", kql);
        }

        [Fact]
        public void FetchRel_TranslatesToTake()
        {
            var readRel = ProtobufBuilder.ReadRel("Logs");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var fetchRel = ProtobufBuilder.FetchRel(inputRel, 0, 10);
            var rel = ProtobufBuilder.Rel(3, fetchRel); // Rel.fetch = field 3
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Logs", kql);
            Assert.Contains("| take 10", kql);
        }

        [Fact]
        public void SortRel_TranslatesToSortBy()
        {
            var readRel = ProtobufBuilder.ReadRel("Orders");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var sortField = ProtobufBuilder.SortField(
                ProtobufBuilder.FieldRefExpression(0),
                3 // desc_nulls_first
            );

            var sortRel = ProtobufBuilder.SortRel(inputRel, sortField);
            var rel = ProtobufBuilder.Rel(5, sortRel); // Rel.sort = field 5
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Orders", kql);
            Assert.Contains("| sort by", kql);
            Assert.Contains("desc", kql);
        }

        [Fact]
        public void AggregateRel_TranslatesToSummarize()
        {
            var readRel = ProtobufBuilder.ReadRel("Sales");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var grouping = ProtobufBuilder.Grouping(ProtobufBuilder.FieldRefExpression(0));
            var measure = ProtobufBuilder.Measure(ProtobufBuilder.FieldRefExpression(1));

            var aggRel = ProtobufBuilder.AggregateRel(
                inputRel,
                new[] { grouping },
                new[] { measure }
            );
            var rel = ProtobufBuilder.Rel(4, aggRel); // Rel.aggregate = field 4
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Sales", kql);
            Assert.Contains("| summarize", kql);
            Assert.Contains("by", kql);
        }

        [Fact]
        public void JoinRel_TranslatesToJoin()
        {
            var leftRead = ProtobufBuilder.ReadRel("Employees");
            var leftRel = ProtobufBuilder.Rel(1, leftRead);

            var rightRead = ProtobufBuilder.ReadRel("Departments");
            var rightRel = ProtobufBuilder.Rel(1, rightRead);

            var condition = ProtobufBuilder.FieldRefExpression(0);

            var joinRel = ProtobufBuilder.JoinRel(leftRel, rightRel, condition, 1); // inner join
            var rel = ProtobufBuilder.Rel(6, joinRel); // Rel.join = field 6
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Employees", kql);
            Assert.Contains("| join kind=inner", kql);
            Assert.Contains("Departments", kql);
        }

        [Fact]
        public void ComplexPlan_SortFilterFetch()
        {
            // Employees | where true | sort by $field0 desc | take 5
            var readRel = ProtobufBuilder.ReadRel("Employees");
            var readRelWrapped = ProtobufBuilder.Rel(1, readRel);

            // Filter
            var filterRel = ProtobufBuilder.FilterRel(readRelWrapped, ProtobufBuilder.LiteralBoolExpression(true));
            var filterRelWrapped = ProtobufBuilder.Rel(2, filterRel);

            // Sort
            var sortField = ProtobufBuilder.SortField(ProtobufBuilder.FieldRefExpression(0), 3); // desc
            var sortRel = ProtobufBuilder.SortRel(filterRelWrapped, sortField);
            var sortRelWrapped = ProtobufBuilder.Rel(5, sortRel);

            // Fetch (limit 5)
            var fetchRel = ProtobufBuilder.FetchRel(sortRelWrapped, 0, 5);
            var rel = ProtobufBuilder.Rel(3, fetchRel);

            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("Employees", kql);
            Assert.Contains("| where true", kql);
            Assert.Contains("| sort by", kql);
            Assert.Contains("desc", kql);
            Assert.Contains("| take 5", kql);

            // Verify order
            int whereIdx = kql.IndexOf("| where");
            int sortIdx = kql.IndexOf("| sort by");
            int takeIdx = kql.IndexOf("| take");
            Assert.True(whereIdx < sortIdx, "where should come before sort");
            Assert.True(sortIdx < takeIdx, "sort should come before take");
        }

        [Fact]
        public void LiteralExpressions_TranslateCorrectly()
        {
            // Test literal string in a filter
            var readRel = ProtobufBuilder.ReadRel("T");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var condition = ProtobufBuilder.LiteralStringExpression("hello");
            var filterRel = ProtobufBuilder.FilterRel(inputRel, condition);
            var rel = ProtobufBuilder.Rel(2, filterRel);
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("'hello'", kql);
        }

        [Fact]
        public void EmptyPlan_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SubstraitToKqlTranslator.Translate(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => SubstraitToKqlTranslator.Translate(null!));
        }

        [Fact]
        public void LeftJoinRel_TranslatesToLeftOuterJoin()
        {
            var leftRead = ProtobufBuilder.ReadRel("A");
            var leftRel = ProtobufBuilder.Rel(1, leftRead);
            var rightRead = ProtobufBuilder.ReadRel("B");
            var rightRel = ProtobufBuilder.Rel(1, rightRead);

            var joinRel = ProtobufBuilder.JoinRel(leftRel, rightRel, null, 3); // left outer
            var rel = ProtobufBuilder.Rel(6, joinRel);
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("| join kind=leftouter", kql);
        }

        [Fact]
        public void FetchWithOffset_TranslatesToRowNumber()
        {
            var readRel = ProtobufBuilder.ReadRel("T");
            var inputRel = ProtobufBuilder.Rel(1, readRel);

            var fetchRel = ProtobufBuilder.FetchRel(inputRel, 100, 10);
            var rel = ProtobufBuilder.Rel(3, fetchRel);
            var planRel = ProtobufBuilder.PlanRel(rel);
            var plan = ProtobufBuilder.Plan(planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);

            Assert.Contains("row_number() > 100", kql);
            Assert.Contains("| take 10", kql);
        }
    }
}
