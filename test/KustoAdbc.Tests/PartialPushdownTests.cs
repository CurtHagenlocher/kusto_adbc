// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class PartialPushdownTests
    {
        static byte[] ReadU() => P.Read("U", ("i", P.TI32), ("l", P.TI64), ("s", P.TString));
        static byte[] ReadCustomers() => P.Read("customers", ("id", P.TI64), ("cname", P.TString), ("region", P.TString));

        /// <summary>A function the driver doesn't support, so the relation using it stays in the plan.</summary>
        static int Unsupported(Functions f, string name = "levenshtein:str_str") => f.Add("functions_string", name);

        [Fact]
        public void FullyTranslatable_ReturnsFullyPushed()
        {
            var plan = P.Plan(P.Fetch(ReadU(), null, 10), new[] { "i", "l", "s" });

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.True(result.FullyPushed);
            KqlAssert.Equal("U | project i, l, s = tostring(s) | take 10", result.Kql!.ToString());

            var output = PlanShape.Parse(result.Plan.ToArray());
            Assert.Contains(SubstraitPartialPushdown.KqlExtensionUrn, output.Urns);
            Assert.Equal(new[] { "i", "l", "s" }, output.RootNames);
            var node = Assert.Single(output.KqlNodes);
            KqlAssert.Equal("U | project i, l, s = tostring(s) | take 10", node.Kql);
            Assert.Equal(new[] { "i", "l", "s" }, node.ColumnNames);
            Assert.Equal(new[] { 5, 7, 12 }, node.ColumnTypeFields); // i32, i64, string
            Assert.Equal("read", output.RootRelKinds);
        }

        [Fact]
        public void UnsupportedFilter_ReadIsPushedDownAndFilterKept()
        {
            var f = new Functions();
            int levenshtein = Unsupported(f);
            var plan = P.Plan(P.Filter(ReadU(), P.Call(levenshtein, P.Field(2), P.Str("x"))), new[] { "i", "l", "s" }, f);

            var result = SubstraitPartialPushdown.Pushdown(plan);

            Assert.False(result.FullyPushed);
            Assert.Null(result.Kql);
            var output = PlanShape.Parse(result.Plan.ToArray());
            Assert.Equal("filter(read)", output.RootRelKinds);
            Assert.Contains("levenshtein:str_str", output.FunctionNames);
            Assert.Contains(SubstraitPartialPushdown.KqlExtensionUrn, output.Urns);
            var node = Assert.Single(output.KqlNodes);
            KqlAssert.Equal("U | project i, l, s = tostring(s)", node.Kql);
            Assert.Equal(new[] { "i", "l", "s" }, node.ColumnNames);
        }

        [Fact]
        public void TranslatableSubtreeBelowUnsupportedRelation_IsPushedAsAWhole()
        {
            var f = new Functions();
            int levenshtein = Unsupported(f);
            int gt = f.Add("functions_comparison", "gt:any_any");
            var filtered = P.Filter(ReadU(), P.Call(gt, P.Field(1), P.I64(5)));
            var plan = P.Plan(P.ProjectEmit(filtered, new[] { 3 }, P.Call(levenshtein, P.Field(2), P.Str("x"))), new[] { "d" }, f);

            var output = PlanShape.Parse(SubstraitPartialPushdown.Pushdown(plan).Plan.ToArray());

            Assert.Equal("project(read)", output.RootRelKinds);
            var node = Assert.Single(output.KqlNodes);
            KqlAssert.Equal("U | project i, l, s = tostring(s) | where (l > 5)", node.Kql);
        }

        [Fact]
        public void JoinWithUnsupportedFilters_BothSidesPushedDown()
        {
            var f = new Functions();
            int levenshtein = Unsupported(f);
            var left = P.Filter(ReadU(), P.Call(levenshtein, P.Field(2), P.Str("a")));
            var right = P.Filter(ReadCustomers(), P.Call(levenshtein, P.Field(1), P.Str("b")));
            var plan = P.Plan(P.Join(left, right, null, P.Inner), null, f);

            var output = PlanShape.Parse(SubstraitPartialPushdown.Pushdown(plan).Plan.ToArray());

            Assert.Equal("join(filter(read),filter(read))", output.RootRelKinds);
            Assert.Equal(new[] { "U | project i, l, s = tostring(s)", "customers | project id, cname = tostring(cname), region = tostring(region)" }, output.KqlNodes.Select(n => KqlAssert.Canonical(n.Kql)));
            Assert.Equal(new[] { "id", "cname", "region" }, output.KqlNodes[1].ColumnNames);
        }

        [Fact]
        public void SubtreeWithComputedColumns_DeclaresTheirTypes()
        {
            var f = new Functions();
            int levenshtein = Unsupported(f);
            int add = f.Add("functions_arithmetic", "add:i64_i64");
            var projected = P.ProjectEmit(ReadU(), new[] { 2, 3 }, P.Call(add, P.Field(1), P.I64(1)));
            var plan = P.Plan(P.Filter(projected, P.Call(levenshtein, P.Field(0), P.Str("x"))), null, f);

            var node = Assert.Single(PlanShape.Parse(SubstraitPartialPushdown.Pushdown(plan).Plan.ToArray()).KqlNodes);

            KqlAssert.Equal("U | project i, l, s = tostring(s) | extend _e0 = (l + 1) | project s, _e0", node.Kql);
            Assert.Equal(new[] { 12, 7 }, node.ColumnTypeFields);
        }

        [Fact]
        public void EmptyPlan_Throws()
        {
            Assert.Throws<ArgumentException>(() => SubstraitPartialPushdown.Pushdown(Array.Empty<byte>()));
        }

        /// <summary>The parts of an output plan the tests check, read with field numbers from the .proto files.</summary>
        sealed class PlanShape
        {
            public List<string> Urns { get; } = new();
            public List<string> FunctionNames { get; } = new();
            public List<string> RootNames { get; } = new();
            public string RootRelKinds { get; private set; } = "";
            public List<(string Kql, List<string> ColumnNames, List<int> ColumnTypeFields)> KqlNodes { get; } = new();

            byte[] _data = Array.Empty<byte>();

            public static PlanShape Parse(byte[] plan)
            {
                var shape = new PlanShape { _data = plan };
                var r = new ProtoReader(plan, 0, plan.Length);
                while (r.Next(out int field, out int wire))
                {
                    switch (field)
                    {
                        case 8: // extension_urns
                        {
                            var u = r.ReadMessage(wire);
                            while (u.Next(out int uf, out int uw)) { if (uf == 2) shape.Urns.Add(u.ReadString(uw)); else u.Skip(uw); }
                            break;
                        }
                        case 2: // extensions
                        {
                            var d = r.ReadMessage(wire);
                            while (d.Next(out int df, out int dw))
                            {
                                if (df != 3) { d.Skip(dw); continue; }
                                var fn = d.ReadMessage(dw);
                                while (fn.Next(out int ff, out int fw)) { if (ff == 3) shape.FunctionNames.Add(fn.ReadString(fw)); else fn.Skip(fw); }
                            }
                            break;
                        }
                        case 3: // relations
                        {
                            var pr = r.ReadMessage(wire);
                            while (pr.Next(out int pf, out int pw))
                            {
                                if (pf != 2) { pr.Skip(pw); continue; }
                                var root = pr.ReadMessage(pw);
                                while (root.Next(out int rf, out int rw))
                                {
                                    if (rf == 1) shape.RootRelKinds = shape.Rel(root.ReadMessage(rw));
                                    else if (rf == 2) shape.RootNames.Add(root.ReadString(rw));
                                    else root.Skip(rw);
                                }
                            }
                            break;
                        }
                        default:
                            r.Skip(wire);
                            break;
                    }
                }
                return shape;
            }

            string Rel(ProtoReader rel)
            {
                rel.Next(out int kind, out int wire);
                var content = rel.ReadMessage(wire);
                string name = kind switch { 1 => "read", 2 => "filter", 3 => "fetch", 4 => "aggregate", 5 => "sort", 6 => "join", 7 => "project", _ => kind.ToString() };
                if (kind == 1)
                {
                    ReadNode(content);
                    return name;
                }
                int[] children = kind == 6 ? new[] { 2, 3 } : new[] { 2 };
                var parts = new List<string>();
                while (content.Next(out int f, out int w))
                {
                    if (Array.IndexOf(children, f) >= 0) parts.Add(Rel(content.ReadMessage(w)));
                    else content.Skip(w);
                }
                return $"{name}({string.Join(",", parts)})";
            }

            void ReadNode(ProtoReader read)
            {
                var names = new List<string>();
                var columns = new List<string>();
                var types = new List<int>();
                while (read.Next(out int f, out int w))
                {
                    if (f == 7)
                    {
                        var t = read.ReadMessage(w);
                        while (t.Next(out int tf, out int tw)) { if (tf == 1) names.Add(t.ReadString(tw)); else t.Skip(tw); }
                    }
                    else if (f == 2)
                    {
                        var ns = read.ReadMessage(w);
                        while (ns.Next(out int nf, out int nw))
                        {
                            if (nf == 1) columns.Add(ns.ReadString(nw));
                            else if (nf == 2)
                            {
                                var st = ns.ReadMessage(nw);
                                while (st.Next(out int sf, out int sw))
                                {
                                    if (sf != 1) { st.Skip(sw); continue; }
                                    var type = st.ReadMessage(sw);
                                    type.Next(out int typeField, out int typeWire);
                                    type.Skip(typeWire);
                                    types.Add(typeField);
                                }
                            }
                            else ns.Skip(nw);
                        }
                    }
                    else read.Skip(w);
                }
                if (names.Count == 2 && names[0] == SubstraitPartialPushdown.KqlFunctionName)
                    KqlNodes.Add((names[1], columns, types));
            }
        }
    }
}
