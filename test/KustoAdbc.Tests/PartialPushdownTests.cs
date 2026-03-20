using System;
using System.Text;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class PartialPushdownTests
    {
        // Reuse the PB helper from SubstraitToKqlTranslatorTests
        // (We need to duplicate the essential builders here since they're nested in another class)
        static class PB
        {
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

            public static byte[] Tag(int fn, int wt) => Varint((fn << 3) | wt);

            public static byte[] LenDel(int fn, byte[] content)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fn, 2)); r.AddRange(Varint(content.Length)); r.AddRange(content);
                return r.ToArray();
            }

            public static byte[] VarField(int fn, int v)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fn, 0)); r.AddRange(Varint(v));
                return r.ToArray();
            }

            public static byte[] VarField64(int fn, long v)
            {
                var r = new System.Collections.Generic.List<byte>();
                r.AddRange(Tag(fn, 0)); r.AddRange(Varint64(v));
                return r.ToArray();
            }

            public static byte[] StrField(int fn, string v)
                => LenDel(fn, Encoding.UTF8.GetBytes(v));

            public static byte[] Cat(params byte[][] arrays)
            {
                int total = 0;
                foreach (var a in arrays) total += a.Length;
                var result = new byte[total];
                int pos = 0;
                foreach (var a in arrays) { Buffer.BlockCopy(a, 0, result, pos, a.Length); pos += a.Length; }
                return result;
            }

            public static byte[] NamedTable(string name) => StrField(1, name);

            public static byte[] ReadRelWithSchema(string table, string[] cols)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                // base_schema (field 2): NamedStruct with names
                if (cols.Length > 0)
                {
                    var nsContent = new System.Collections.Generic.List<byte>();
                    foreach (var c in cols) nsContent.AddRange(StrField(1, c));
                    parts.Add(LenDel(2, nsContent.ToArray()));
                }
                parts.Add(LenDel(7, NamedTable(table)));
                return Cat(parts.ToArray());
            }

            public static byte[] ReadRel(string table) => LenDel(7, NamedTable(table));
            public static byte[] Rel(int field, byte[] content) => LenDel(field, content);

            public static byte[] PlanRel(byte[] rel)
                => LenDel(2, LenDel(1, rel));

            public static byte[] Plan(byte[] planRel)
                => LenDel(3, planRel);

            public static byte[] LitBool(bool v) => LenDel(1, VarField(1, v ? 1 : 0));
            public static byte[] LitI32(int v) => LenDel(1, VarField(5, v));

            public static byte[] FieldRef(int index)
            {
                var sf = VarField(1, index);
                var rs = LenDel(3, sf);
                var fr = LenDel(1, rs);
                return LenDel(2, fr);
            }

            public static byte[] FilterRel(byte[] input, byte[] condition)
                => Cat(LenDel(2, input), LenDel(3, condition));

            public static byte[] FetchRel(byte[] input, long offset, long count)
                => Cat(LenDel(2, input), VarField64(3, offset), VarField64(4, count));

            public static byte[] ExtFunc(int urnRef, int anchor, string name)
                => LenDel(3, Cat(VarField(4, urnRef), VarField(2, anchor), StrField(3, name)));

            public static byte[] ExtUri(int anchor, string uri)
                => Cat(VarField(1, anchor), StrField(2, uri));

            public static byte[] PlanWithExtensions(byte[][] extUris, byte[][] extDecls, byte[] planRel)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                foreach (var uri in extUris) parts.Add(LenDel(8, uri));
                foreach (var decl in extDecls) parts.Add(LenDel(2, decl));
                parts.Add(LenDel(3, planRel));
                return Cat(parts.ToArray());
            }

            public static byte[] ScalarFunc(int funcRef, params byte[][] args)
            {
                var parts = new System.Collections.Generic.List<byte[]> { VarField(1, funcRef) };
                foreach (var arg in args)
                    parts.Add(LenDel(4, LenDel(2, arg)));
                return LenDel(3, Cat(parts.ToArray()));
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

        [Fact]
        public void FullyTranslatable_ReturnsFullyPushed()
        {
            // Simple: T | take 10 — fully translatable
            var read = PB.Rel(1, PB.ReadRel("MyTable"));
            var fetch = PB.FetchRel(read, 0, 10);
            var plan = PB.Plan(PB.PlanRel(PB.Rel(3, fetch)));

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.True(result.FullyPushed);
            Assert.NotNull(result.Kql);
            Assert.Contains("MyTable", result.Kql!.ToString());
            Assert.Contains("take 10", result.Kql!.ToString());
            Assert.True(result.Plan.Length > 0);
        }

        [Fact]
        public void FullyTranslatable_OutputContainsKqlQueryNode()
        {
            var read = PB.Rel(1, PB.ReadRel("Events"));
            var plan = PB.Plan(PB.PlanRel(read));

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.True(result.FullyPushed);

            // The output plan should contain our KQL extension marker
            string planStr = Encoding.UTF8.GetString(result.Plan.ToArray());
            Assert.Contains(SubstraitPartialPushdown.KqlFunctionName, planStr);
            Assert.Contains("Events", planStr);
        }

        [Fact]
        public void UnsupportedFilter_PartialPushdown()
        {
            // Filter uses unsupported function, but ReadRel is translatable
            // Plan: Filter(unsupported_func) → Read("T")
            var unsupportedExpr = PB.ScalarFunc(42, PB.FieldRef(0));
            var read = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(read, unsupportedExpr);
            var rel = PB.Rel(2, filter);

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:custom") },
                new[] { PB.ExtFunc(1, 42, "unsupported_exotic_func:i32") },
                PB.PlanRel(rel));

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.False(result.FullyPushed);
            Assert.Null(result.Kql);
            Assert.True(result.Plan.Length > 0);

            // The output should contain the KQL query marker (for the pushed-down ReadRel)
            string planStr = Encoding.UTF8.GetString(result.Plan.ToArray());
            Assert.Contains(SubstraitPartialPushdown.KqlFunctionName, planStr);
            Assert.Contains("T", planStr); // The table name should appear in the KQL
        }

        [Fact]
        public void FullyTranslatableWithFilter_ReturnsFullyPushed()
        {
            // Filter with supported condition: T | where true
            var read = PB.Rel(1, PB.ReadRel("T"));
            var filter = PB.FilterRel(read, PB.LitBool(true));
            var plan = PB.Plan(PB.PlanRel(PB.Rel(2, filter)));

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.True(result.FullyPushed);
            Assert.Contains("where", result.Kql!.ToString());
        }

        [Fact]
        public void JoinWithUnsupportedFilters_BothSidesPushedDown()
        {
            // Join where both sides have unsupported filters:
            //   Join(inner)
            //     Filter(unsupported) → Read("A")
            //     Filter(unsupported) → Read("B")
            // Expected: Join preserved, both Reads pushed to KQL

            var unsupportedLeft = PB.ScalarFunc(10, PB.FieldRef(0));
            var unsupportedRight = PB.ScalarFunc(11, PB.FieldRef(0));

            var leftRead = PB.Rel(1, PB.ReadRel("A"));
            var leftFilter = PB.Rel(2, PB.FilterRel(leftRead, unsupportedLeft));

            var rightRead = PB.Rel(1, PB.ReadRel("B"));
            var rightFilter = PB.Rel(2, PB.FilterRel(rightRead, unsupportedRight));

            var join = PB.JoinRel(leftFilter, rightFilter, null, 1);

            var plan = PB.PlanWithExtensions(
                new[] { PB.ExtUri(1, "extension:io.substrait:custom") },
                new[] {
                    PB.ExtFunc(1, 10, "left_exotic:i32"),
                    PB.ExtFunc(1, 11, "right_exotic:i32"),
                },
                PB.PlanRel(PB.Rel(6, join)));

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.False(result.FullyPushed);

            // Output should contain KQL markers for the pushed-down Read nodes
            string planStr = Encoding.UTF8.GetString(result.Plan.ToArray());
            Assert.Contains(SubstraitPartialPushdown.KqlFunctionName, planStr);
        }

        [Fact]
        public void EmptyPlan_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SubstraitPartialPushdown.Pushdown(Array.Empty<byte>()));
        }
    }
}
