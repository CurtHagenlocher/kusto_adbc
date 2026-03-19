using System;
using System.Linq;
using System.Text;
using Kusto.Language;
using Kusto.Language.Syntax;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class SubstraitToKqlTranslatorTests
    {
        #region KQL Comparison Helpers

        /// <summary>
        /// Asserts that the actual KQL parses without syntax errors and is structurally
        /// equivalent to the expected KQL, using the Kusto parser for normalization.
        /// </summary>
        static void AssertKqlEqual(string expectedKql, string actualKql)
        {
            // Both must parse without syntax errors
            var expectedCode = KustoCode.Parse(expectedKql);
            var actualCode = KustoCode.Parse(actualKql);

            var expectedDiags = expectedCode.GetSyntaxDiagnostics();
            var actualDiags = actualCode.GetSyntaxDiagnostics();

            Assert.True(expectedDiags.Count == 0,
                $"Expected KQL has syntax errors: {FormatDiagnostics(expectedDiags)}\nKQL: {expectedKql}");
            Assert.True(actualDiags.Count == 0,
                $"Translated KQL has syntax errors: {FormatDiagnostics(actualDiags)}\nKQL: {actualKql}");

            // Compare normalized syntax tree text (whitespace-independent)
            string expectedNorm = NormalizeKql(expectedCode);
            string actualNorm = NormalizeKql(actualCode);

            Assert.Equal(expectedNorm, actualNorm);
        }

        /// <summary>
        /// Asserts that the actual KQL parses without syntax errors.
        /// Used for cases where the exact output may vary but must be valid KQL.
        /// </summary>
        static void AssertValidKql(string kql)
        {
            var code = KustoCode.Parse(kql);
            var diags = code.GetSyntaxDiagnostics();
            Assert.True(diags.Count == 0,
                $"KQL has syntax errors: {FormatDiagnostics(diags)}\nKQL: {kql}");
        }

        /// <summary>
        /// Normalizes KQL by re-printing the syntax tree with minimal trivia,
        /// then collapsing whitespace to produce a canonical single-line form.
        /// </summary>
        static string NormalizeKql(KustoCode code)
        {
            // IncludeTrivia.Minimal strips comments but keeps structural whitespace.
            // We further normalize by collapsing all runs of whitespace (including newlines)
            // into single spaces, so "T\n| where x" and "T | where x" compare equal.
            string text = code.Syntax.ToString(IncludeTrivia.Minimal);
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        static string FormatDiagnostics(IReadOnlyList<Diagnostic> diags)
        {
            return string.Join("; ", diags.Select(d => $"[{d.Start}..{d.Start + d.Length}] {d.Message}"));
        }

        #endregion

        #region Protobuf Builder

        /// <summary>
        /// Helper to build Substrait protobuf messages for testing.
        /// Constructs wire-format bytes directly (no protobuf dependency).
        /// </summary>
        static class PB
        {
            public static byte[] Tag(int fieldNumber, int wireType) => Varint((fieldNumber << 3) | wireType);

            public static byte[] Varint(int value)
            {
                var bytes = new System.Collections.Generic.List<byte>();
                uint v = (uint)value;
                while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
                bytes.Add((byte)v);
                return bytes.ToArray();
            }

            public static byte[] Varint64(long value)
            {
                var bytes = new System.Collections.Generic.List<byte>();
                ulong v = (ulong)value;
                while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
                bytes.Add((byte)v);
                return bytes.ToArray();
            }

            public static byte[] LenDel(int fieldNumber, byte[] content)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fieldNumber, 2));
                r.AddRange(Varint(content.Length));
                r.AddRange(content);
                return r.ToArray();
            }

            public static byte[] VarField(int fieldNumber, int value)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fieldNumber, 0));
                r.AddRange(Varint(value));
                return r.ToArray();
            }

            public static byte[] VarField64(int fieldNumber, long value)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fieldNumber, 0));
                r.AddRange(Varint64(value));
                return r.ToArray();
            }

            public static byte[] StrField(int fieldNumber, string value)
                => LenDel(fieldNumber, Encoding.UTF8.GetBytes(value));

            public static byte[] Cat(params byte[][] arrays)
            {
                int total = 0;
                foreach (var a in arrays) total += a.Length;
                var result = new byte[total];
                int pos = 0;
                foreach (var a in arrays) { Buffer.BlockCopy(a, 0, result, pos, a.Length); pos += a.Length; }
                return result;
            }

            // Substrait structure builders

            public static byte[] NamedTable(string name) => StrField(1, name);
            public static byte[] ReadRel(string table) => LenDel(7, NamedTable(table));

            // Rel oneof field numbers: read=1, filter=2, fetch=3, aggregate=4, sort=5, join=6, project=7
            public static byte[] Rel(int field, byte[] content) => LenDel(field, content);

            public static byte[] PlanRel(byte[] rel)
                => LenDel(2, LenDel(1, rel)); // PlanRel.root(2) -> RelRoot.input(1)

            public static byte[] Plan(byte[] planRel)
                => LenDel(3, planRel); // Plan.relations(3)

            // Expressions

            public static byte[] FieldRef(int index)
            {
                var structField = VarField(1, index);           // StructField.field
                var refSegment = LenDel(3, structField);        // ReferenceSegment.struct_field
                var fieldRef = LenDel(1, refSegment);           // FieldReference.direct_reference
                return LenDel(2, fieldRef);                     // Expression.selection
            }

            public static byte[] LitBool(bool v) => LenDel(1, VarField(1, v ? 1 : 0));
            public static byte[] LitI32(int v) => LenDel(1, VarField(5, v));
            public static byte[] LitI64(long v) => LenDel(1, VarField64(7, v));
            public static byte[] LitStr(string v) => LenDel(1, StrField(12, v));

            // Relations

            public static byte[] FilterRel(byte[] input, byte[] condition)
                => Cat(LenDel(2, input), LenDel(3, condition));

            public static byte[] ProjectRel(byte[] input, params byte[][] exprs)
            {
                var parts = new System.Collections.Generic.List<byte[]> { LenDel(2, input) };
                foreach (var e in exprs) parts.Add(LenDel(3, e));
                return Cat(parts.ToArray());
            }

            public static byte[] FetchRel(byte[] input, long offset, long count)
                => Cat(LenDel(2, input), VarField64(3, offset), VarField64(4, count));

            public static byte[] SortField(byte[] expr, int direction)
                => Cat(LenDel(1, expr), VarField(2, direction));

            public static byte[] SortRel(byte[] input, params byte[][] fields)
            {
                var parts = new System.Collections.Generic.List<byte[]> { LenDel(2, input) };
                foreach (var f in fields) parts.Add(LenDel(3, f));
                return Cat(parts.ToArray());
            }

            public static byte[] Grouping(params byte[][] exprs)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                foreach (var e in exprs) parts.Add(LenDel(1, e));
                return Cat(parts.ToArray());
            }

            public static byte[] Measure(byte[] expr) => LenDel(1, expr);

            public static byte[] AggregateRel(byte[] input, byte[][] groupings, byte[][] measures)
            {
                var parts = new System.Collections.Generic.List<byte[]> { LenDel(2, input) };
                foreach (var g in groupings) parts.Add(LenDel(3, g));
                foreach (var m in measures) parts.Add(LenDel(4, m));
                return Cat(parts.ToArray());
            }

            public static byte[] JoinRel(byte[] left, byte[] right, byte[]? cond, int joinType)
            {
                var parts = new System.Collections.Generic.List<byte[]>
                    { LenDel(2, left), LenDel(3, right) };
                if (cond != null) parts.Add(LenDel(4, cond));
                parts.Add(VarField(5, joinType));
                return Cat(parts.ToArray());
            }
        }

        /// <summary>
        /// Builds a complete Substrait plan from a Rel content and its Rel field number.
        /// </summary>
        static byte[] BuildPlan(int relField, byte[] relContent)
        {
            var rel = PB.Rel(relField, relContent);
            return PB.Plan(PB.PlanRel(rel));
        }

        #endregion

        #region Read

        [Fact]
        public void Read_TranslatesToTableReference()
        {
            var plan = BuildPlan(1, PB.ReadRel("MyTable"));
            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("MyTable", kql);
        }

        #endregion

        #region Filter

        [Fact]
        public void Filter_TranslatesToWhere()
        {
            var input = PB.Rel(1, PB.ReadRel("Events"));
            var filter = PB.FilterRel(input, PB.LitBool(true));
            var plan = BuildPlan(2, filter);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Events | where true", kql);
        }

        [Fact]
        public void Filter_WithStringLiteral()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(input, PB.LitStr("hello"));
            var plan = BuildPlan(2, filter);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where 'hello'", kql);
        }

        #endregion

        #region Project

        [Fact]
        public void Project_TranslatesToProject()
        {
            var input = PB.Rel(1, PB.ReadRel("Users"));
            var project = PB.ProjectRel(input, PB.FieldRef(0), PB.FieldRef(1));
            var plan = BuildPlan(7, project);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Users | project $field0, $field1", kql);
        }

        #endregion

        #region Fetch (Limit/Offset)

        [Fact]
        public void Fetch_TranslatesToTake()
        {
            var input = PB.Rel(1, PB.ReadRel("Logs"));
            var fetch = PB.FetchRel(input, 0, 10);
            var plan = BuildPlan(3, fetch);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Logs | take 10", kql);
        }

        [Fact]
        public void FetchWithOffset_TranslatesToSerializeAndTake()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var fetch = PB.FetchRel(input, 100, 10);
            var plan = BuildPlan(3, fetch);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual(
                "T | serialize | where row_number() > 100 | take 10",
                kql);
        }

        #endregion

        #region Sort

        [Fact]
        public void Sort_Desc_TranslatesToSortBy()
        {
            var input = PB.Rel(1, PB.ReadRel("Orders"));
            var sf = PB.SortField(PB.FieldRef(0), 3); // desc_nulls_first
            var sort = PB.SortRel(input, sf);
            var plan = BuildPlan(5, sort);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Orders | sort by $field0 desc", kql);
        }

        [Fact]
        public void Sort_Asc_TranslatesToSortBy()
        {
            var input = PB.Rel(1, PB.ReadRel("Orders"));
            var sf = PB.SortField(PB.FieldRef(0), 1); // asc_nulls_first
            var sort = PB.SortRel(input, sf);
            var plan = BuildPlan(5, sort);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Orders | sort by $field0 asc", kql);
        }

        #endregion

        #region Aggregate

        [Fact]
        public void Aggregate_TranslatesToSummarize()
        {
            var input = PB.Rel(1, PB.ReadRel("Sales"));
            var grouping = PB.Grouping(PB.FieldRef(0));
            var measure = PB.Measure(PB.FieldRef(1));
            var agg = PB.AggregateRel(input, new[] { grouping }, new[] { measure });
            var plan = BuildPlan(4, agg);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Sales | summarize $field1 by $field0", kql);
        }

        #endregion

        #region Join

        [Fact]
        public void InnerJoin_TranslatesToJoinKindInner()
        {
            var left = PB.Rel(1, PB.ReadRel("Employees"));
            var right = PB.Rel(1, PB.ReadRel("Departments"));
            var join = PB.JoinRel(left, right, PB.FieldRef(0), 1); // inner
            var plan = BuildPlan(6, join);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            Assert.Contains("join kind=inner", kql);
            Assert.Contains("Employees", kql);
            Assert.Contains("Departments", kql);
        }

        [Fact]
        public void LeftOuterJoin_TranslatesToJoinKindLeftouter()
        {
            var left = PB.Rel(1, PB.ReadRel("A"));
            var right = PB.Rel(1, PB.ReadRel("B"));
            var join = PB.JoinRel(left, right, null, 3); // left outer
            var plan = BuildPlan(6, join);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            Assert.Contains("join kind=leftouter", kql);
        }

        [Fact]
        public void FullOuterJoin_TranslatesToJoinKindFullouter()
        {
            var left = PB.Rel(1, PB.ReadRel("A"));
            var right = PB.Rel(1, PB.ReadRel("B"));
            var join = PB.JoinRel(left, right, null, 2); // full outer
            var plan = BuildPlan(6, join);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            Assert.Contains("join kind=fullouter", kql);
        }

        #endregion

        #region Complex Plans

        [Fact]
        public void FilterSortFetch_ProducesCorrectPipelineOrder()
        {
            // Build: Employees | where true | sort by $field0 desc | take 5
            var read = PB.Rel(1, PB.ReadRel("Employees"));
            var filter = PB.Rel(2, PB.FilterRel(read, PB.LitBool(true)));
            var sort = PB.Rel(5, PB.SortRel(filter,
                PB.SortField(PB.FieldRef(0), 3))); // desc
            var fetch = PB.FetchRel(sort, 0, 5);
            var plan = BuildPlan(3, fetch);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual(
                "Employees | where true | sort by $field0 desc | take 5",
                kql);
        }

        [Fact]
        public void FilterProject_ProducesCorrectPipelineOrder()
        {
            // Build: Users | where true | project $field0, $field1
            var read = PB.Rel(1, PB.ReadRel("Users"));
            var filter = PB.Rel(2, PB.FilterRel(read, PB.LitBool(true)));
            var project = PB.ProjectRel(filter, PB.FieldRef(0), PB.FieldRef(1));
            var plan = BuildPlan(7, project);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual(
                "Users | where true | project $field0, $field1",
                kql);
        }

        #endregion

        #region Literals

        [Fact]
        public void IntegerLiteral_TranslatesCorrectly()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(input, PB.LitI32(42));
            var plan = BuildPlan(2, filter);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where 42", kql);
        }

        [Fact]
        public void Int64Literal_TranslatesCorrectly()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(input, PB.LitI64(9999999999L));
            var plan = BuildPlan(2, filter);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where 9999999999", kql);
        }

        #endregion

        #region Error Handling

        [Fact]
        public void EmptyPlan_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SubstraitToKqlTranslator.Translate(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => SubstraitToKqlTranslator.Translate(null!));
        }

        #endregion
    }
}
