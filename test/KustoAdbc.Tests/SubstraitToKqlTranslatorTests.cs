// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

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

            public static byte[] ReadRelWithSchema(string table, string[] columnNames)
            {
                // NamedStruct: field 1 = repeated string (names)
                var nameParts = new System.Collections.Generic.List<byte[]>();
                foreach (var name in columnNames)
                    nameParts.Add(StrField(1, name));
                var namedStruct = nameParts.Count > 0 ? Cat(nameParts.ToArray()) : Array.Empty<byte>();
                // ReadRel: field 2 = NamedStruct, field 7 = NamedTable
                return Cat(LenDel(2, namedStruct), LenDel(7, NamedTable(table)));
            }

            // Rel oneof field numbers: read=1, filter=2, fetch=3, aggregate=4, sort=5, join=6, project=7
            public static byte[] Rel(int field, byte[] content) => LenDel(field, content);

            public static byte[] PlanRel(byte[] rel)
                => LenDel(2, LenDel(1, rel)); // PlanRel.root(2) -> RelRoot.input(1)

            public static byte[] Plan(byte[] planRel)
                => LenDel(3, planRel); // Plan.relations(3)

            // Plan with extension declarations
            // SimpleExtensionDeclaration.extension_function: urn_ref(4), anchor(2), name(3)
            public static byte[] ExtFunc(int urnRef, int anchor, string name)
            {
                var inner = Cat(VarField(4, urnRef), VarField(2, anchor), StrField(3, name));
                return LenDel(3, inner); // ExtensionFunction is oneof field 3
            }

            // SimpleExtensionURN: anchor(1), uri(2)
            public static byte[] ExtUri(int anchor, string uri)
                => Cat(VarField(1, anchor), StrField(2, uri));

            // Build a plan with extensions and relations
            public static byte[] PlanWithExtensions(byte[][] extUris, byte[][] extDecls, byte[] planRel)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                foreach (var uri in extUris) parts.Add(LenDel(8, uri));     // Plan.extension_urns = field 8
                foreach (var decl in extDecls) parts.Add(LenDel(2, decl));  // Plan.extensions = field 2
                parts.Add(LenDel(3, planRel));                              // Plan.relations = field 3
                return Cat(parts.ToArray());
            }

            // ScalarFunction expression: function_reference(1), args(4)
            public static byte[] ScalarFunc(int funcRef, params byte[][] args)
            {
                var parts = new System.Collections.Generic.List<byte[]> { VarField(1, funcRef) };
                foreach (var arg in args)
                {
                    // Wrap each arg as FunctionArgument.value (field 2) = Expression
                    parts.Add(LenDel(4, LenDel(2, arg)));
                }
                var inner = Cat(parts.ToArray());
                return LenDel(3, inner); // Expression.scalar_function = field 3
            }

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
            var input = PB.Rel(1, PB.ReadRelWithSchema("Users", new[] { "col_a", "col_b" }));
            var project = PB.ProjectRel(input, PB.FieldRef(0), PB.FieldRef(1));
            var plan = BuildPlan(7, project);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Users | project col_a, col_b", kql);
        }

        [Fact]
        public void Project_WithoutSchema_FallsBackToFieldN()
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
            var input = PB.Rel(1, PB.ReadRelWithSchema("Orders", new[] { "price" }));
            var sf = PB.SortField(PB.FieldRef(0), 3); // desc_nulls_first
            var sort = PB.SortRel(input, sf);
            var plan = BuildPlan(5, sort);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Orders | sort by price desc", kql);
        }

        [Fact]
        public void Sort_Asc_TranslatesToSortBy()
        {
            var input = PB.Rel(1, PB.ReadRelWithSchema("Orders", new[] { "price" }));
            var sf = PB.SortField(PB.FieldRef(0), 1); // asc_nulls_first
            var sort = PB.SortRel(input, sf);
            var plan = BuildPlan(5, sort);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Orders | sort by price asc", kql);
        }

        #endregion

        #region Aggregate

        [Fact]
        public void Aggregate_TranslatesToSummarize()
        {
            var input = PB.Rel(1, PB.ReadRelWithSchema("Sales", new[] { "region", "amount" }));
            var grouping = PB.Grouping(PB.FieldRef(0));
            var measure = PB.Measure(PB.FieldRef(1));
            var agg = PB.AggregateRel(input, new[] { grouping }, new[] { measure });
            var plan = BuildPlan(4, agg);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("Sales | summarize amount by region", kql);
        }

        #endregion

        #region Join

        [Fact]
        public void InnerJoin_TranslatesToJoinKindInner()
        {
            var left = PB.Rel(1, PB.ReadRelWithSchema("Employees", new[] { "emp_id", "dept_id" }));
            var right = PB.Rel(1, PB.ReadRelWithSchema("Departments", new[] { "id", "name" }));
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
            // Build: Employees | where true | sort by name desc | take 5
            var read = PB.Rel(1, PB.ReadRelWithSchema("Employees", new[] { "name" }));
            var filter = PB.Rel(2, PB.FilterRel(read, PB.LitBool(true)));
            var sort = PB.Rel(5, PB.SortRel(filter,
                PB.SortField(PB.FieldRef(0), 3))); // desc
            var fetch = PB.FetchRel(sort, 0, 5);
            var plan = BuildPlan(3, fetch);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual(
                "Employees | where true | sort by name desc | take 5",
                kql);
        }

        [Fact]
        public void FilterProject_ProducesCorrectPipelineOrder()
        {
            // Build: Users | where true | project col_a, col_b
            var read = PB.Rel(1, PB.ReadRelWithSchema("Users", new[] { "col_a", "col_b" }));
            var filter = PB.Rel(2, PB.FilterRel(read, PB.LitBool(true)));
            var project = PB.ProjectRel(filter, PB.FieldRef(0), PB.FieldRef(1));
            var plan = BuildPlan(7, project);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual(
                "Users | where true | project col_a, col_b",
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

        [Fact]
        public void MalformedPlan_NoRelations_ThrowsWithMessage()
        {
            // A plan with only extension URNs but no relations
            var plan = PB.Cat(PB.LenDel(8, PB.ExtUri(1, "extension:io.substrait:test")));

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("no relations", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void UnsupportedFunction_DeclaredButNotMapped_ThrowsWithFunctionName()
        {
            // Function is declared in extensions but has no KQL mapping
            var expr = PB.ScalarFunc(42, PB.FieldRef(0));
            var read = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(read, expr);
            var rel = PB.Rel(2, filter);
            var planRel = PB.PlanRel(rel);

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_custom") },
                new[] { PB.ExtFunc(1, 42, "some_exotic_function:fp64") },
                planRel);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("some_exotic_function", ex.Message);
            Assert.Contains("no KQL equivalent", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("KustoCapabilities", ex.Message);
        }

        [Fact]
        public void AllTranslationErrors_AreAdbcExceptions()
        {
            // Verify that SubstraitTranslationException is an AdbcException
            var ex = SubstraitTranslationException.UnsupportedFunction("test:i32");
            Assert.IsAssignableFrom<Apache.Arrow.Adbc.AdbcException>(ex);
        }

        #endregion

        #region Extension Function Resolution

        /// <summary>
        /// Helper: builds a complete plan with extensions, where the filter condition
        /// uses a scalar function resolved via the extension declarations.
        /// </summary>
        static byte[] BuildPlanWithScalarFilter(string table, string funcSignature, int funcAnchor, byte[] scalarExpr)
            => BuildPlanWithScalarFilter(table, null, funcSignature, funcAnchor, scalarExpr);

        static byte[] BuildPlanWithScalarFilter(string table, string[]? columnNames, string funcSignature, int funcAnchor, byte[] scalarExpr)
        {
            var readContent = columnNames != null
                ? PB.ReadRelWithSchema(table, columnNames)
                : PB.ReadRel(table);
            var read = PB.Rel(1, readContent);
            var filter = PB.FilterRel(read, scalarExpr);
            var rel = PB.Rel(2, filter);
            var planRel = PB.PlanRel(rel);

            return PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_arithmetic") },
                new[] { PB.ExtFunc(1, funcAnchor, funcSignature) },
                planRel);
        }

        [Fact]
        public void Add_TranslatesToInfixPlus()
        {
            // add(x, 10) → x + 10
            var expr = PB.ScalarFunc(100, PB.FieldRef(0), PB.LitI32(10));
            var plan = BuildPlanWithScalarFilter("T", new[] { "x" }, "add:i32_i32", 100, expr);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where x + 10", kql);
        }

        [Fact]
        public void Subtract_TranslatesToInfixMinus()
        {
            var expr = PB.ScalarFunc(101, PB.FieldRef(0), PB.LitI32(5));
            var plan = BuildPlanWithScalarFilter("T", new[] { "val" }, "subtract:i32_i32", 101, expr);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where val - 5", kql);
        }

        [Fact]
        public void Equal_TranslatesToDoubleEquals()
        {
            var expr = PB.ScalarFunc(200, PB.FieldRef(0), PB.LitStr("hello"));
            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_comparison") },
                new[] { PB.ExtFunc(1, 200, "equal:any_any") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "name" })), expr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where name == 'hello'", kql);
        }

        [Fact]
        public void LessThan_TranslatesToLtOperator()
        {
            var expr = PB.ScalarFunc(201, PB.FieldRef(0), PB.LitI32(100));
            var plan = BuildPlanWithScalarFilter("T", new[] { "age" }, "lt:i32_i32", 201, expr);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where age < 100", kql);
        }

        [Fact]
        public void And_TranslatesToInfixAnd()
        {
            // and(lt(x, 100), gt(y, 0))
            var lt = PB.ScalarFunc(300, PB.FieldRef(0), PB.LitI32(100));
            var gt = PB.ScalarFunc(301, PB.FieldRef(1), PB.LitI32(0));
            var andExpr = PB.ScalarFunc(302, lt, gt);

            var read = PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "x", "y" }));
            var filter = PB.FilterRel(read, andExpr);
            var rel = PB.Rel(2, filter);
            var planRel = PB.PlanRel(rel);

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_comparison"),
                        PB.ExtUri(2, "extension:io.substrait:functions_boolean") },
                new[] { PB.ExtFunc(1, 300, "lt:i32_i32"),
                        PB.ExtFunc(1, 301, "gt:i32_i32"),
                        PB.ExtFunc(2, 302, "and:bool_bool") },
                planRel);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where x < 100 and y > 0", kql);
        }

        [Fact]
        public void Not_TranslatesToPrefixNot()
        {
            var inner = PB.ScalarFunc(400, PB.FieldRef(0), PB.LitI32(0));
            var notExpr = PB.ScalarFunc(401, inner);

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_comparison"),
                        PB.ExtUri(2, "extension:io.substrait:functions_boolean") },
                new[] { PB.ExtFunc(1, 400, "equal:i32_i32"),
                        PB.ExtFunc(2, 401, "not:bool") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "active" })), notExpr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where not (active == 0)", kql);
        }

        [Fact]
        public void Strlen_TranslatesToFunctionCall()
        {
            var expr = PB.ScalarFunc(500, PB.FieldRef(0));

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_string") },
                new[] { PB.ExtFunc(1, 500, "char_length:str") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "msg" })), expr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where strlen(msg)", kql);
        }

        [Fact]
        public void IsNull_TranslatesToIsnull()
        {
            var expr = PB.ScalarFunc(600, PB.FieldRef(0));

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_comparison") },
                new[] { PB.ExtFunc(1, 600, "is_null:any") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "col" })), expr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where isnull(col)", kql);
        }

        [Fact]
        public void UndeclaredFunction_ThrowsWithAnchorInfo()
        {
            // A scalar function with no extension declaration should throw
            var expr = PB.ScalarFunc(999, PB.FieldRef(0));
            var plan = BuildPlan(2, PB.FilterRel(PB.Rel(1, PB.ReadRel("T")), expr));

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("anchor=999", ex.Message);
            Assert.Contains("undeclared", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Strcat_TranslatesToFunctionCall()
        {
            var expr = PB.ScalarFunc(700, PB.FieldRef(0), PB.LitStr("_suffix"));

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_string") },
                new[] { PB.ExtFunc(1, 700, "concat:str_str") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "tag" })), expr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertValidKql(kql);
            AssertKqlEqual("T | where strcat(tag, '_suffix')", kql);
        }

        [Fact]
        public void Multiply_TranslatesToInfixStar()
        {
            var expr = PB.ScalarFunc(800, PB.FieldRef(0), PB.FieldRef(1));
            var plan = BuildPlanWithScalarFilter("T", new[] { "a", "b" }, "multiply:fp64_fp64", 800, expr);

            string kql = SubstraitToKqlTranslator.Translate(plan);
            AssertKqlEqual("T | where a * b", kql);
        }

        [Fact]
        public void CountStar_TranslatesToCount()
        {
            var expr = PB.ScalarFunc(900);
            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_aggregate_generic") },
                new[] { PB.ExtFunc(1, 900, "count_star:") },
                PB.PlanRel(PB.Rel(2, PB.FilterRel(PB.Rel(1, PB.ReadRel("T")), expr))));

            string kql = SubstraitToKqlTranslator.Translate(plan);
            Assert.Contains("count()", kql);
        }

        #endregion

        #region Unexpected Field Rejection

        [Fact]
        public void ReadRel_WithFilter_ThrowsUnexpectedField()
        {
            // ReadRel with a filter field (3) should be rejected — silently dropping
            // a filter on the read would produce incorrect results.
            var readContent = PB.Cat(
                PB.LenDel(7, PB.NamedTable("T")),              // named_table
                PB.LenDel(3, PB.LitBool(true))                 // filter (unsupported)
            );
            var plan = BuildPlan(1, readContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("ReadRel", ex.Message);
        }

        [Fact]
        public void ReadRel_WithCommon_ThrowsUnexpectedField()
        {
            // ReadRel with a common field (1) containing emit remapping
            var commonContent = PB.Cat(PB.VarField(1, 0)); // dummy emit
            var readContent = PB.Cat(
                PB.LenDel(1, commonContent),                    // common
                PB.LenDel(7, PB.NamedTable("T"))                // named_table
            );
            var plan = BuildPlan(1, readContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("ReadRel", ex.Message);
        }

        [Fact]
        public void ReadRel_WithProjection_ThrowsUnexpectedField()
        {
            // ReadRel with a projection field (5) should be rejected
            var readContent = PB.Cat(
                PB.LenDel(7, PB.NamedTable("T")),              // named_table
                PB.LenDel(5, PB.VarField(1, 0))                // projection (unsupported)
            );
            var plan = BuildPlan(1, readContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("ReadRel", ex.Message);
        }

        [Fact]
        public void FilterRel_WithCommon_ThrowsUnexpectedField()
        {
            // FilterRel with a common field (1) — emit remapping could change output
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filterContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, input),                            // input
                PB.LenDel(3, PB.LitBool(true))                  // condition
            );
            var plan = BuildPlan(2, filterContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("FilterRel", ex.Message);
        }

        [Fact]
        public void JoinRel_WithPostJoinFilter_ThrowsUnexpectedField()
        {
            // JoinRel with a post_join_filter (field 6) — dropping it is a correctness bug
            var left = PB.Rel(1, PB.ReadRel("A"));
            var right = PB.Rel(1, PB.ReadRel("B"));
            var joinContent = PB.Cat(
                PB.LenDel(2, left),                             // left
                PB.LenDel(3, right),                            // right
                PB.VarField(5, 1),                              // type = inner
                PB.LenDel(6, PB.LitBool(true))                 // post_join_filter (unsupported)
            );
            var plan = BuildPlan(6, joinContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("JoinRel", ex.Message);
        }

        [Fact]
        public void JoinRel_WithCommon_ThrowsUnexpectedField()
        {
            var left = PB.Rel(1, PB.ReadRel("A"));
            var right = PB.Rel(1, PB.ReadRel("B"));
            var joinContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, left),
                PB.LenDel(3, right),
                PB.VarField(5, 1)
            );
            var plan = BuildPlan(6, joinContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("JoinRel", ex.Message);
        }

        [Fact]
        public void ProjectRel_WithCommon_ThrowsUnexpectedField()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var projectContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, input),                            // input
                PB.LenDel(3, PB.FieldRef(0))                    // expression
            );
            var plan = BuildPlan(7, projectContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("ProjectRel", ex.Message);
        }

        [Fact]
        public void FetchRel_WithCommon_ThrowsUnexpectedField()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var fetchContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, input),                            // input
                PB.VarField64(4, 10)                            // count
            );
            var plan = BuildPlan(3, fetchContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("FetchRel", ex.Message);
        }

        [Fact]
        public void SortRel_WithCommon_ThrowsUnexpectedField()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var sortContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, input),                            // input
                PB.LenDel(3, PB.SortField(PB.FieldRef(0), 1))  // sort field
            );
            var plan = BuildPlan(5, sortContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("SortRel", ex.Message);
        }

        [Fact]
        public void AggregateRel_WithCommon_ThrowsUnexpectedField()
        {
            var input = PB.Rel(1, PB.ReadRel("T"));
            var aggContent = PB.Cat(
                PB.LenDel(1, PB.VarField(1, 0)),               // common
                PB.LenDel(2, input)                             // input
            );
            var plan = BuildPlan(4, aggContent);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("AggregateRel", ex.Message);
        }

        [Fact]
        public void Expression_CastVariant_ThrowsUnexpectedField()
        {
            // Expression with a cast (field 6) instead of a recognized variant
            var castContent = PB.Cat(
                PB.LenDel(1, PB.FieldRef(0)),                  // expression to cast
                PB.LenDel(2, PB.VarField(1, 0))                // target type
            );
            var castExpr = PB.LenDel(6, castContent);           // Expression.cast
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, castExpr));
            var plan = BuildPlan(2, filter);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("Expression", ex.Message);
        }

        [Fact]
        public void Expression_WindowFunction_ThrowsUnexpectedField()
        {
            // Expression with a window_function (field 4)
            var windowContent = PB.VarField(1, 0);              // dummy function_reference
            var windowExpr = PB.LenDel(4, windowContent);       // Expression.window_function
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, windowExpr));
            var plan = BuildPlan(2, filter);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("Expression", ex.Message);
        }

        [Fact]
        public void Literal_Timestamp_ThrowsUnsupportedLiteral()
        {
            // Literal with a timestamp (field 14) — not a supported literal type
            var timestampLiteral = PB.LenDel(1, PB.VarField64(14, 1234567890));
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, timestampLiteral));
            var plan = BuildPlan(2, filter);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("literal", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Literal_Date_ThrowsUnsupportedLiteral()
        {
            // Literal with a date (field 16)
            var dateLiteral = PB.LenDel(1, PB.VarField(16, 19000));
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, dateLiteral));
            var plan = BuildPlan(2, filter);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("literal", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Literal_Binary_ThrowsUnsupportedLiteral()
        {
            // Literal with binary data (field 13)
            var binaryLiteral = PB.LenDel(1, PB.LenDel(13, new byte[] { 0x01, 0x02 }));
            var input = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, binaryLiteral));
            var plan = BuildPlan(2, filter);

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("literal", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Measure_WithFilter_ThrowsUnexpectedField()
        {
            // Measure with a filter (field 2) — filtered aggregation not supported
            var measureContent = PB.Cat(
                PB.LenDel(1, PB.ScalarFunc(100)),               // measure expression
                PB.LenDel(2, PB.LitBool(true))                  // filter (unsupported)
            );
            var input = PB.Rel(1, PB.ReadRel("T"));
            var aggContent = PB.Cat(
                PB.LenDel(2, input),
                PB.LenDel(4, measureContent)
            );

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_aggregate_generic") },
                new[] { PB.ExtFunc(1, 100, "count_star:") },
                PB.PlanRel(PB.Rel(4, aggContent)));

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("Measure", ex.Message);
        }

        [Fact]
        public void UnsupportedRelation_SetOperation_ThrowsUnsupportedRelation()
        {
            // Rel with field 8 (set operation) — not a supported relation type
            var setContent = PB.VarField(1, 0);
            var plan = BuildPlan(8, setContent);

            Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
        }

        [Fact]
        public void FunctionArgument_EnumVariant_ThrowsUnexpectedField()
        {
            // FunctionArgument with enum (field 1) instead of value (field 2)
            var enumArg = PB.LenDel(4, PB.StrField(1, "ROWS")); // FunctionArgument.enum
            var valueArg = PB.LenDel(4, PB.LenDel(2, PB.FieldRef(0))); // valid FunctionArgument.value
            var scalarContent = PB.Cat(PB.VarField(1, 100), valueArg, enumArg);
            var scalarExpr = PB.LenDel(3, scalarContent);
            var input = PB.Rel(1, PB.ReadRelWithSchema("T", new[] { "x" }));
            var filter = PB.Cat(PB.LenDel(2, input), PB.LenDel(3, scalarExpr));

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:functions_arithmetic") },
                new[] { PB.ExtFunc(1, 100, "add:i32_i32") },
                PB.PlanRel(PB.Rel(2, filter)));

            var ex = Assert.Throws<SubstraitTranslationException>(
                () => SubstraitToKqlTranslator.Translate(plan));
            Assert.Contains("FunctionArgument", ex.Message);
        }

        #endregion
    }
}
