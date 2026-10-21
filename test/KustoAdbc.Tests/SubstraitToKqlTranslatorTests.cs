// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class SubstraitToKqlTranslatorTests
    {
        #region Fixtures

        const string Arithmetic = "functions_arithmetic";
        const string Comparison = "functions_comparison";
        const string Boolean = "functions_boolean";
        const string Strings = "functions_string";
        const string Datetime = "functions_datetime";
        const string Logarithmic = "functions_logarithmic";
        const string Generic = "functions_aggregate_generic";

        // U(i:int, l:long, s:string): fields 0, 1, 2
        static byte[] ReadU() => P.Read("U", ("i", P.TI32), ("l", P.TI64), ("s", P.TString));

        // T(i:int, l:long, r:real, s:string, b:bool, dt:datetime, ts:timespan): fields 0-6
        static byte[] ReadT() => P.Read("T", ("i", P.TI32), ("l", P.TI64), ("r", P.TFp64), ("s", P.TString),
            ("b", P.TBool), ("dt", P.TTimestampTz(6)), ("ts", P.TIntervalDay()));

        // orders: fields 0-6; customers: fields 0-2
        static byte[] ReadOrders() => P.Read("orders", ("id", P.TI64), ("customer_id", P.TI64), ("name", P.TString),
            ("price", P.TFp64), ("qty", P.TI32), ("ts", P.TTimestampTz(6)), ("flag", P.TBool));
        static byte[] ReadCustomers() => P.Read("customers", ("id", P.TI64), ("cname", P.TString), ("region", P.TString));

        static readonly string[] UNames = { "i", "l", "s" };

        static string Translate(byte[] plan) => SubstraitToKqlTranslator.Translate(plan);

        static SubstraitTranslationException Rejects(byte[] plan, string messageFragment)
        {
            var ex = Assert.Throws<SubstraitTranslationException>(() => Translate(plan));
            Assert.Contains(messageFragment, ex.Message);
            return ex;
        }

        /// <summary>Translates a projection of the given expressions (named x0, x1, ...) over U.</summary>
        static string ProjectU(Functions f, params byte[][] expressions)
        {
            var emit = Enumerable.Range(3, expressions.Length).ToArray();
            var names = Enumerable.Range(0, expressions.Length).Select(i => "x" + i).ToArray();
            return Translate(P.Plan(P.ProjectEmit(ReadU(), emit, expressions), names, f));
        }

        static string ProjectT(Functions f, params byte[][] expressions)
        {
            var emit = Enumerable.Range(7, expressions.Length).ToArray();
            var names = Enumerable.Range(0, expressions.Length).Select(i => "x" + i).ToArray();
            return Translate(P.Plan(P.ProjectEmit(ReadT(), emit, expressions), names, f));
        }

        /// <summary>The KQL for a single projected expression, e.g. "toint((i + int(1)))".</summary>
        static string ExpressionKql(string kql)
        {
            const string marker = "| extend _e0 = ";
            int start = kql.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, kql);
            start += marker.Length;
            int end = kql.IndexOf("\n", start, StringComparison.Ordinal);
            // Names from the plan are bracketed; unquote simple ones for readable expectations.
            return System.Text.RegularExpressions.Regex.Replace(kql.Substring(start, end - start), @"\['([A-Za-z_][A-Za-z0-9_]*)'\]", "$1");
        }

        #endregion

        #region Read

        [Fact]
        public void Read_WithMatchingNames_IsJustTheTable()
        {
            string kql = Translate(P.Plan(ReadU(), UNames));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s)", kql);
        }

        [Fact]
        public void Read_BareRelWithoutRoot()
        {
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s)", Translate(P.PlanBareRel(ReadU())));
        }

        [Fact]
        public void Read_DatabaseAndClusterQualifiedNames()
        {
            var cols = new[] { ("i", P.TI32), ("l", P.TI64), ("s", P.TString) };
            KqlAssert.EqualAndAnalyzes("database('db').U | project i, l, s = tostring(s)", Translate(P.Plan(P.Read(new[] { "db", "U" }, cols))));
            KqlAssert.Equal("cluster('help').database('db').U | project i, l, s = tostring(s)", Translate(P.Plan(P.Read(new[] { "help", "db", "U" }, cols))));
        }

        [Fact]
        public void Read_QuotesNamesThatAreNotPlainIdentifiers()
        {
            var f = new Functions();
            int add = f.Add(Arithmetic, "add:i64_i64");
            var read = P.Read("odd name", ("a b", P.TI64), ("where", P.TString));
            string kql = Translate(P.Plan(P.ProjectEmit(read, new[] { 2, 1 }, P.Call(add, P.Field(0), P.I64(1))), new[] { "it's", "where" }, f));
            KqlAssert.EqualAndAnalyzes(
                "['odd name'] | project ['a b'], ['where'] = tostring(['where']) | extend _e0 = (['a b'] + 1) | project ['it\\'s'] = _e0, ['where']",
                kql);
        }

        [Fact]
        public void Read_FilterAppliesBeforeProjection()
        {
            var f = new Functions();
            int gt = f.Add(Comparison, "gt:any_any");
            var read = P.Read(new[] { "U" }, new[] { ("i", P.TI32), ("l", P.TI64), ("s", P.TString) },
                P.Cat(P.ReadFilter(P.Call(gt, P.Field(0), P.I32(5))), P.ReadProjection(2, 0)));
            string kql = Translate(P.Plan(read, new[] { "s", "i" }, f));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | where (i > int(5)) | project s, i", kql);
        }

        [Fact]
        public void Read_NonNamedTable_IsRejected()
        {
            // ReadRel.virtual_table = 5
            var rel = P.LenDel(1, P.Cat(P.LenDel(2, P.StrField(1, "x")), P.LenDel(5, P.Empty)));
            Rejects(P.Plan(rel), "virtual_table");
        }

        [Fact]
        public void Read_MissingBaseSchema_IsRejected()
        {
            var rel = P.LenDel(1, P.LenDel(7, P.StrField(1, "U")));
            Rejects(P.Plan(rel), "base_schema");
        }

        #endregion

        #region Root

        [Fact]
        public void Root_RenamesOutputColumns()
        {
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | project x = i, l, y = s", Translate(P.Plan(ReadU(), new[] { "x", "l", "y" })));
        }

        [Fact]
        public void Root_DuplicateNamesAreMadeUnique()
        {
            var columns = KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | project a = i, a_1 = l, a_2 = s",
                Translate(P.Plan(ReadU(), new[] { "a", "a", "a" })));
            Assert.Equal(new[] { "a:int", "a_1:long", "a_2:string" }, columns);
        }

        [Fact]
        public void Root_NameCountMismatch_IsRejected()
        {
            Rejects(P.Plan(ReadU(), new[] { "a" }), "names");
        }

        #endregion

        #region Project

        [Fact]
        public void Project_AppendsExpressionsToInputColumns()
        {
            var f = new Functions();
            int add = f.Add(Arithmetic, "add:i64_i64");
            string kql = Translate(P.Plan(P.Project(ReadU(), P.Call(add, P.Field(1), P.I64(1))), new[] { "i", "l", "s", "x" }, f));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | extend _e0 = (l + 1) | project i, l, s, x = _e0", kql);
        }

        [Fact]
        public void Project_EmitSelectsAndReordersColumns()
        {
            var f = new Functions();
            int add = f.Add(Arithmetic, "add:i64_i64");
            string kql = Translate(P.Plan(P.ProjectEmit(ReadU(), new[] { 3, 0 }, P.Call(add, P.Field(1), P.I64(1))), new[] { "x", "i" }, f));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | extend _e0 = (l + 1) | project x = _e0, i", kql);
        }

        [Fact]
        public void Project_RepeatedColumnReferences()
        {
            string kql = Translate(P.Plan(P.ProjectEmit(ReadU(), new[] { 0, 3 }, P.Field(0)), new[] { "a", "b" }));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | project a = i, b = i", kql);
        }

        [Fact]
        public void Project_FieldReferenceOutOfRange_IsRejected()
        {
            Rejects(P.Plan(P.Project(ReadU(), P.Field(7))), "out of range");
        }

        #endregion

        #region Fetch, Sort, TopN

        [Fact]
        public void Fetch_CountOnly()
        {
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | take 10", Translate(P.Plan(P.Fetch(ReadU(), null, 10), UNames)));
        }

        [Fact]
        public void Fetch_OffsetAfterSort()
        {
            var plan = P.Plan(P.Fetch(P.Sort(ReadU(), P.SortField(P.Field(0), P.AscNullsFirst)), 5, 10), UNames);
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | sort by i asc nulls first | serialize _rn0 = row_number() | where _rn0 > 5 | take 10 | project i, l, s",
                Translate(plan));
        }

        [Fact]
        public void Fetch_LegacyIntegerFields()
        {
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | take 7", Translate(P.Plan(P.FetchLegacy(ReadU(), 0, 7), UNames)));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s)", Translate(P.Plan(P.FetchLegacy(ReadU(), 0, -1), UNames)));
        }

        [Fact]
        public void Fetch_NonLiteralCount_IsRejected()
        {
            var fetch = P.LenDel(3, P.Cat(P.LenDel(2, ReadU()), P.LenDel(6, P.Field(1))));
            Rejects(P.Plan(fetch), "integer literal");
        }

        [Fact]
        public void Sort_WritesDirectionAndNullOrderingExplicitly()
        {
            var plan = P.Plan(P.Sort(ReadU(),
                P.SortField(P.Field(0), P.AscNullsFirst),
                P.SortField(P.Field(1), P.AscNullsLast),
                P.SortField(P.Field(2), P.DescNullsFirst)), UNames);
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | sort by i asc nulls first, l asc nulls last, s desc nulls first", Translate(plan));

            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | sort by l desc nulls last",
                Translate(P.Plan(P.Sort(ReadU(), P.SortField(P.Field(1), P.DescNullsLast)), UNames)));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | sort by l asc nulls first",
                Translate(P.Plan(P.Sort(ReadU(), P.SortField(P.Field(1), P.Clustered)), UNames)));
        }

        [Fact]
        public void Sort_UnspecifiedDirection_IsRejected()
        {
            Rejects(P.Plan(P.Sort(ReadU(), P.SortField(P.Field(1), 0))), "direction");
        }

        [Fact]
        public void Sort_ByExpressionMaterializesAColumn()
        {
            var f = new Functions();
            int add = f.Add(Arithmetic, "add:i64_i64");
            var plan = P.Plan(P.Sort(ReadU(), P.SortField(P.Call(add, P.Field(1), P.I64(1)), P.DescNullsLast)), UNames, f);
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | extend _s0 = (l + 1) | sort by _s0 desc nulls last | project i, l, s", Translate(plan));
        }

        [Fact]
        public void TopN_TranslatesToSortAndTake()
        {
            var plan = P.Plan(P.TopN(ReadU(), 5, P.SortField(P.Field(1), P.DescNullsLast)), UNames);
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | sort by l desc nulls last | take 5", Translate(plan));
        }

        #endregion

        #region Aggregate

        [Fact]
        public void Aggregate_SumByKey_ReturnsNullOverNoValues()
        {
            var f = new Functions();
            int sum = f.Add(Arithmetic, "sum:fp64");
            var plan = P.Plan(P.Aggregate(ReadT(), new[] { P.Field(3) }, P.Measure(sum, new[] { P.Field(2) })), new[] { "s", "total" }, f);
            var columns = KqlAssert.EqualAndAnalyzes(
                "T | project i, l, r = toreal(r), s = tostring(s), b, dt, ts | summarize _a0 = iif(countif(isnotnull(r)) < 1, real(null), sum(r)) by s | project s, total = _a0",
                Translate(plan));
            Assert.Equal(new[] { "s:string", "total:real" }, columns);
        }

        [Fact]
        public void Aggregate_Counts()
        {
            var f = new Functions();
            int countStar = f.Add(Generic, "count:");
            int count = f.Add(Generic, "count:any");
            var plan = P.Plan(P.Aggregate(ReadU(), Array.Empty<byte[]>(),
                P.Measure(countStar, Array.Empty<byte[]>()),
                P.Measure(count, new[] { P.Field(2) }),
                P.Measure(count, new[] { P.Field(2) }, invocation: P.InvocationDistinct),
                P.Measure(count, new[] { P.Field(1) })), new[] { "n", "ns", "nd", "nl" }, f);
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | summarize _a0 = count(), _a1 = countif(isnotempty(s)), _a2 = count_distinctif(s, isnotempty(s)), _a3 = countif(isnotnull(l))"
                + " | project n = _a0, ns = _a1, nd = _a2, nl = _a3",
                Translate(plan));
        }

        [Fact]
        public void Aggregate_MeasureFilterUsesConditionalAggregates()
        {
            var f = new Functions();
            int sum = f.Add(Arithmetic, "sum:i64");
            int count = f.Add(Generic, "count:");
            int gt = f.Add(Comparison, "gt:any_any");
            var filter = P.Call(gt, P.Field(0), P.I32(1));
            var plan = P.Plan(P.Aggregate(ReadU(), Array.Empty<byte[]>(),
                P.Measure(sum, new[] { P.Field(1) }, filter),
                P.Measure(count, Array.Empty<byte[]>(), filter)), new[] { "total", "n" }, f);
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | summarize _a0 = iif(countif(isnotnull(l) and ((i > int(1)))) < 1, long(null), sumif(l, (i > int(1)))), _a1 = countif((i > int(1)))"
                + " | project total = _a0, n = _a1",
                Translate(plan));
        }

        [Fact]
        public void Aggregate_WithoutMeasuresIsDistinct()
        {
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | summarize by s", Translate(P.Plan(P.Aggregate(ReadU(), new[] { P.Field(2) }), new[] { "s" })));
        }

        [Fact]
        public void Aggregate_ExpressionKeysAreNamed()
        {
            var f = new Functions();
            int mod = f.Add(Arithmetic, "modulus:i32_i32");
            int count = f.Add(Generic, "count:");
            var plan = P.Plan(P.Aggregate(ReadU(), new[] { P.Call(mod, P.Field(0), P.I32(2)) }, P.Measure(count, Array.Empty<byte[]>())),
                new[] { "parity", "n" }, f);
            var columns = KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | summarize _a0 = count() by _g1 = toint((i - int(2) * (i / int(2)))) | project parity = _g1, n = _a0",
                Translate(plan));
            Assert.Equal(new[] { "parity:int", "n:long" }, columns);
        }

        [Fact]
        public void Aggregate_LegacyInlineGroupingExpressions()
        {
            var f = new Functions();
            int count = f.Add(Generic, "count:");
            var plan = P.Plan(P.AggregateLegacy(ReadU(), new[] { P.Field(2) }, P.Measure(count, Array.Empty<byte[]>())), new[] { "s", "n" }, f);
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | summarize _a0 = count() by s | project s, n = _a0", Translate(plan));
        }

        [Fact]
        public void Aggregate_StdDevAndVariance()
        {
            var f = new Functions();
            int stddev = f.Add(Arithmetic, "std_dev:req_fp64");
            int variance = f.Add(Arithmetic, "variance:req_fp64");
            var plan = P.Plan(P.Aggregate(ReadT(), Array.Empty<byte[]>(),
                P.MeasureWithEnum(stddev, "SAMPLE", P.Field(2)),
                P.MeasureWithEnum(variance, "POPULATION", P.Field(2))), new[] { "sd", "var" }, f);
            KqlAssert.EqualAndAnalyzes(
                "T | project i, l, r = toreal(r), s = tostring(s), b, dt, ts | summarize _a0 = iif(countif(isnotnull(r)) < 2, real(null), stdev(r)), _a1 = iif(countif(isnotnull(r)) < 1, real(null), variancep(r))"
                + " | project sd = _a0, var = _a1",
                Translate(plan));
        }

        [Fact]
        public void Aggregate_MultipleGroupingSets_AreRejected()
        {
            var f = new Functions();
            int count = f.Add(Generic, "count:");
            var plan = P.Plan(P.AggregateSets(ReadU(), new[] { P.Field(2) }, new[] { new[] { 0 }, Array.Empty<int>() },
                P.Measure(count, Array.Empty<byte[]>())), null, f);
            Rejects(plan, "grouping sets");
        }

        [Fact]
        public void Aggregate_OrderedAggregates_AreRejected()
        {
            var f = new Functions();
            int count = f.Add(Generic, "count:");
            var sorts = P.LenDel(3, P.SortField(P.Field(0), P.AscNullsFirst)); // AggregateFunction.sorts = 3
            Rejects(P.Plan(P.Aggregate(ReadU(), Array.Empty<byte[]>(), P.Measure(count, Array.Empty<byte[]>(), extra: sorts)), null, f), "sorts");
        }

        [Fact]
        public void Aggregate_IntermediatePhase_IsRejected()
        {
            var f = new Functions();
            int count = f.Add(Generic, "count:");
            var phase = P.VarField(4, 1); // AggregateFunction.phase = INITIAL_TO_INTERMEDIATE
            Rejects(P.Plan(P.Aggregate(ReadU(), Array.Empty<byte[]>(), P.Measure(count, Array.Empty<byte[]>(), extra: phase)), null, f), "phase");
        }

        [Fact]
        public void Aggregate_DistinctSum_IsRejected()
        {
            var f = new Functions();
            int sum = f.Add(Arithmetic, "sum:i64");
            Rejects(P.Plan(P.Aggregate(ReadU(), Array.Empty<byte[]>(), P.Measure(sum, new[] { P.Field(1) }, invocation: P.InvocationDistinct)), null, f),
                "DISTINCT");
        }

        #endregion

        #region Join, Cross, Set

        static byte[] OrdersCustomerKey(Functions f) => P.Call(f.Add(Comparison, "equal:any_any"), P.Field(1), P.Field(7));

        [Fact]
        public void Join_Inner_RenamesCollidingRightColumns()
        {
            var f = new Functions();
            var join = P.Join(ReadOrders(), ReadCustomers(), OrdersCustomerKey(f), P.Inner);
            var plan = P.Plan(P.ProjectEmit(join, new[] { 0, 7, 8 }), new[] { "id", "cid", "cname" }, f);
            var columns = KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0"
                + " | project id, cid = id_0, cname",
                Translate(plan));
            Assert.Equal(new[] { "id:long", "cid:long", "cname:string" }, columns);
        }

        [Fact]
        public void Join_Left_RightOnlyConditionFiltersRightInput()
        {
            var f = new Functions();
            int and = f.Add(Boolean, "and:bool");
            int eq = f.Add(Comparison, "equal:any_any");
            var cond = P.Call(and, P.Call(eq, P.Field(1), P.Field(7)), P.Call(eq, P.Field(9), P.Str("r")));
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), cond, P.Left), new[] { 0, 8 }), new[] { "id", "cname" }, f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=leftouter (customers | project id, cname = tostring(cname), region = tostring(region) | where (region == 'r') | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0"
                + " | project id, cname",
                Translate(plan));
        }

        [Fact]
        public void Join_Inner_NonEquiConditionBecomesPostFilter()
        {
            var f = new Functions();
            int and = f.Add(Boolean, "and:bool");
            int eq = f.Add(Comparison, "equal:any_any");
            int lt = f.Add(Comparison, "lt:any_any");
            var cond = P.Call(and, P.Call(eq, P.Field(1), P.Field(7)), P.Call(lt, P.Field(0), P.Field(7)));
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), cond, P.Inner), new[] { 0 }), new[] { "id" }, f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0"
                + " | where (id < id_0) | project id",
                Translate(plan));
        }

        [Fact]
        public void Join_SemiAndAntiKinds()
        {
            var f = new Functions();
            var key = OrdersCustomerKey(f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=leftsemi (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0 | project id",
                Translate(P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), key, P.LeftSemi), new[] { 0 }), new[] { "id" }, f)));
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=leftanti (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0 | project id",
                Translate(P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), key, P.LeftAnti), new[] { 0 }), new[] { "id" }, f)));
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | where isnotnull(customer_id) | join kind=rightsemi (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region) on $left.customer_id == $right.id_0 | project id_0",
                Translate(P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), key, P.RightSemi), new[] { 0 }), new[] { "id_0" }, f)));
        }

        [Fact]
        public void Join_AntiWithLeftOnlyCondition_IsRejected()
        {
            var f = new Functions();
            int and = f.Add(Boolean, "and:bool");
            int eq = f.Add(Comparison, "equal:any_any");
            var cond = P.Call(and, P.Call(eq, P.Field(1), P.Field(7)), P.Call(eq, P.Field(2), P.Str("x")));
            Rejects(P.Plan(P.Join(ReadOrders(), ReadCustomers(), cond, P.LeftAnti), null, f), "LEFT ANTI");
        }

        [Fact]
        public void Join_FullOuterWithNonEquiCondition_IsRejected()
        {
            var f = new Functions();
            int lt = f.Add(Comparison, "lt:any_any");
            Rejects(P.Plan(P.Join(ReadOrders(), ReadCustomers(), P.Call(lt, P.Field(1), P.Field(7)), P.Outer), null, f), "FULL OUTER");
        }

        [Fact]
        public void Join_WithoutEqualityKeysUsesConstantKey()
        {
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadU(), ReadCustomers(), null, P.Inner), new[] { 0, 4 }), new[] { "i", "cname" });
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _k0 = 1 | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | extend _k1 = 1) on $left._k0 == $right._k1 | project i, cname",
                Translate(plan));
        }

        [Fact]
        public void Join_PostJoinFilter()
        {
            var f = new Functions();
            int gt = f.Add(Comparison, "gt:any_any");
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), OrdersCustomerKey(f), P.Inner, P.Call(gt, P.Field(3), P.Fp64(10))),
                new[] { 0 }), new[] { "id" }, f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region | where isnotnull(id_0)) on $left.customer_id == $right.id_0"
                + " | where (price > real(10.0)) | project id",
                Translate(plan));
        }

        [Fact]
        public void Join_SingleJoin_IsRejected()
        {
            var f = new Functions();
            Rejects(P.Plan(P.Join(ReadOrders(), ReadCustomers(), OrdersCustomerKey(f), P.LeftSingle), null, f), "Join type");
        }

        [Fact]
        public void Cross_UsesConstantKey()
        {
            var plan = P.Plan(P.ProjectEmit(P.Cross(ReadU(), ReadCustomers()), new[] { 2, 4 }), new[] { "s", "cname" });
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _k0 = 1 | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | extend _k1 = 1) on $left._k0 == $right._k1 | project s, cname",
                Translate(plan));
        }

        [Fact]
        public void Set_UnionAllAlignsColumnsByPosition()
        {
            var plan = P.Plan(P.Set(P.UnionAll, P.ProjectEmit(ReadU(), new[] { 2 }), P.ProjectEmit(ReadCustomers(), new[] { 1 })), new[] { "name" });
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | project s | union (customers | project id, cname = tostring(cname), region = tostring(region) | project s = cname) | project name = s", Translate(plan));
        }

        [Fact]
        public void Set_UnionDistinct()
        {
            var plan = P.Plan(P.Set(P.UnionDistinct, P.ProjectEmit(ReadU(), new[] { 2 }), P.ProjectEmit(ReadCustomers(), new[] { 1 })), new[] { "s" });
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | project s | union (customers | project id, cname = tostring(cname), region = tostring(region) | project s = cname) | distinct *", Translate(plan));
        }

        [Fact]
        public void Set_Minus_IsRejected()
        {
            Rejects(P.Plan(P.Set(P.MinusPrimary, ReadU(), ReadU())), "Set operation");
        }

        #endregion

        #region Literals

        [Fact]
        public void Literals_ScalarTypes()
        {
            string kql = ProjectU(new Functions(),
                P.Bool(true), P.I32(-3), P.I64(long.MinValue), P.Fp64(1.5), P.Fp64(2), P.Fp64(double.NaN), P.Fp64(double.NegativeInfinity),
                P.Str("it's a \\ \"test\"\n"));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = true, _e1 = int(-3), _e2 = (-9223372036854775807 - 1), _e3 = real(1.5), _e4 = real(2.0), _e5 = real(nan), _e6 = real(-inf),"
                + " _e7 = 'it\\'s a \\\\ \"test\"\\n'"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3, x4 = _e4, x5 = _e5, x6 = _e6, x7 = _e7",
                kql);
        }

        [Fact]
        public void Literals_TemporalTypes()
        {
            string kql = ProjectU(new Functions(),
                P.Date(19723),
                P.TimestampTz(6, 1704067200123456),
                P.Timestamp(9, 1704067200123456700),
                P.IntervalDay(1, 3600, 500000, 6));
            var columns = KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = datetime(2024-01-01T00:00:00.0000000Z), _e1 = datetime(2024-01-01T00:00:00.1234560Z),"
                + " _e2 = datetime(2024-01-01T00:00:00.1234567Z), _e3 = (900005000000tick)"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3",
                kql);
            Assert.Equal(new[] { "x0:datetime", "x1:datetime", "x2:datetime", "x3:timespan" }, columns);
        }

        [Fact]
        public void Literals_DecimalAndUuid()
        {
            string kql = ProjectU(new Functions(),
                P.Decimal(12345, 10, 2), P.Decimal(-5, 3, 3),
                P.Uuid(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef")));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = decimal(123.45), _e1 = decimal(-0.005), _e2 = guid(12345678-90ab-cdef-1234-567890abcdef)"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2",
                kql);
        }

        [Fact]
        public void Literals_TypedNulls()
        {
            string kql = ProjectU(new Functions(), P.Null(P.TI64), P.Null(P.TI32), P.Null(P.TFp64), P.Null(P.TBool), P.Null(P.TTimestampTz()), P.Null(P.TString));
            var columns = KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = long(null), _e1 = int(null), _e2 = real(null), _e3 = bool(null), _e4 = datetime(null), _e5 = ''"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3, x4 = _e4, x5 = _e5",
                kql);
            Assert.Equal(new[] { "x0:long", "x1:int", "x2:real", "x3:bool", "x4:datetime", "x5:string" }, columns);
        }

        [Fact]
        public void Literals_UnrepresentableValues_AreRejected()
        {
            Rejects(P.Plan(P.Project(ReadU(), P.I8(1))), "i8");
            Rejects(P.Plan(P.Project(ReadU(), P.Timestamp(9, 1704067200123456789))), "100ns");
            Rejects(P.Plan(P.Project(ReadU(), P.Decimal(System.Numerics.BigInteger.Pow(10, 30), 38, 0))), "significant digits");
        }

        #endregion

        #region Expressions

        [Fact]
        public void Cast_UsesConversionFunctions()
        {
            string kql = ProjectU(new Functions(),
                P.Cast(P.TI64, P.Field(0)), P.Cast(P.TString, P.Field(1), P.FailureReturnNull), P.Cast(P.TDate, P.Field(2)), P.Cast(P.TFp64, P.Field(0)));
            var columns = KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = tolong(i), _e1 = tostring(l), _e2 = startofday(todatetime(s)), _e3 = toreal(i)"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3",
                kql);
            Assert.Equal(new[] { "x0:long", "x1:string", "x2:datetime", "x3:real" }, columns);
        }

        [Fact]
        public void Cast_ThatThrows_IsRejected()
        {
            Rejects(P.Plan(P.Project(ReadU(), P.Cast(P.TI64, P.Field(2), P.FailureThrow))), "throw");
        }

        [Fact]
        public void IfThen_TranslatesToCase()
        {
            var f = new Functions();
            int gt = f.Add(Comparison, "gt:any_any");
            string withElse = ProjectU(f, P.IfThen(new[] { (P.Call(gt, P.Field(0), P.I32(1)), P.Str("many")) }, P.Str("one")));
            Assert.Equal("case((i > int(1)), 'many', 'one')", ExpressionKql(withElse));
            KqlAssert.Analyzes(withElse);

            string withoutElse = ProjectU(f, P.IfThen(new[] { (P.Call(gt, P.Field(0), P.I32(1)), P.Field(1)) }, null));
            Assert.Equal("case((i > int(1)), l, long(null))", ExpressionKql(withoutElse));
            KqlAssert.Analyzes(withoutElse);
        }

        [Fact]
        public void Switch_TranslatesToCase()
        {
            string kql = ProjectU(new Functions(), P.Switch(P.Field(0), new[] { (P.I32(1), P.Str("one")), (P.I32(2), P.Str("two")) }, null));
            Assert.Equal("case((i == int(1)), 'one', (i == int(2)), 'two', '')", ExpressionKql(kql));
            KqlAssert.Analyzes(kql);
        }

        [Fact]
        public void InLists()
        {
            string kql = ProjectU(new Functions(),
                P.InList(P.Field(2), P.Str("a"), P.Str("b")),
                P.InList(P.Field(2)),
                P.MultiInList(new[] { P.Field(0), P.Field(2) }, new[] { P.I32(1), P.Str("a") }, new[] { P.I32(2), P.Str("b") }));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = (s in ('a', 'b')), _e1 = false,"
                + " _e2 = ((iif(isnull(i), bool(null), (i == int(1))) and (s == 'a')) or (iif(isnull(i), bool(null), (i == int(2))) and (s == 'b')))"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2",
                kql);
        }

        [Fact]
        public void ContextVariables()
        {
            string kql = ProjectU(new Functions(), P.ContextVariable(1), P.ContextVariable(3), P.ContextVariable(2));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = now(), _e1 = startofday(now()), _e2 = 'UTC' | project x0 = _e0, x1 = _e1, x2 = _e2", kql);
        }

        [Fact]
        public void ContextVariables_PerRecordEvaluation_IsRejected()
        {
            // Plan.execution_behavior = 10 { variable_eval_mode = 1: PER_RECORD (2) }
            var perRecord = P.LenDel(10, P.VarField(1, 2));
            Rejects(P.Plan(P.Project(ReadU(), P.ContextVariable(1)), extra: perRecord), "Per-record");
        }

        [Theory]
        [InlineData("window function", 5)]
        [InlineData("subqueries", 12)]
        [InlineData("dynamic parameters", 14)]
        public void UnsupportedExpressionKinds_AreRejected(string message, int expressionField)
        {
            Rejects(P.Plan(P.Project(ReadU(), P.LenDel(expressionField, P.Empty))), message);
        }

        #endregion

        #region Functions

        [Fact]
        public void Arithmetic_Int32ResultsStayInt32()
        {
            var f = new Functions();
            int add = f.Add(Arithmetic, "add:i32_i32");
            int neg = f.Add(Arithmetic, "negate:i32");
            int div = f.Add(Arithmetic, "divide:i64_i64");
            string kql = ProjectU(f, P.Call(add, P.Field(0), P.I32(1)), P.Call(neg, P.Field(0)), P.Call(div, P.Field(1), P.I64(2)));
            var columns = KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = toint((i + int(1))), _e1 = toint(-(i)), _e2 = (l / 2) | project x0 = _e0, x1 = _e1, x2 = _e2",
                kql);
            Assert.Equal(new[] { "x0:int", "x1:int", "x2:long" }, columns);
        }

        [Fact]
        public void Comparison_OnStringsUsesStrcmp()
        {
            var f = new Functions();
            int lt = f.Add(Comparison, "lt:any_any");
            int between = f.Add(Comparison, "between:any_any_any");
            string kql = ProjectU(f, P.Call(lt, P.Field(2), P.Str("m")), P.Call(between, P.Field(2), P.Str("a"), P.Str("m")), P.Call(between, P.Field(1), P.I64(1), P.I64(5)));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = (strcmp(s, 'm') < 0), _e1 = ((strcmp(s, 'a') >= 0) and (strcmp(s, 'm') <= 0)), _e2 = ((l >= 1) and (l <= 5))"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2",
                kql);
        }

        [Fact]
        public void IsNull_OnStringsTestsForEmpty()
        {
            var f = new Functions();
            int isNull = f.Add(Comparison, "is_null:any");
            int isNotNull = f.Add(Comparison, "is_not_null:any");
            string kql = ProjectU(f, P.Call(isNull, P.Field(2)), P.Call(isNull, P.Field(0)), P.Call(isNotNull, P.Field(2)));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | extend _e0 = isempty(s), _e1 = isnull(i), _e2 = isnotempty(s) | project x0 = _e0, x1 = _e1, x2 = _e2", kql);
        }

        [Fact]
        public void StringPredicates_CaseSensitivityOption()
        {
            var f = new Functions();
            int contains = f.Add(Strings, "contains:str_str");
            var ci = new[] { ("case_sensitivity", new[] { "CASE_INSENSITIVE" }) };
            var asciiThenSensitive = new[] { ("case_sensitivity", new[] { "CASE_INSENSITIVE_ASCII", "CASE_SENSITIVE" }) };
            string kql = ProjectU(f,
                P.Call(contains, P.Field(2), P.Str("x")),
                P.CallWith(contains, ci, P.Field(2), P.Str("x")),
                P.CallWith(contains, asciiThenSensitive, P.Field(2), P.Str("x")));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = (s contains_cs 'x'), _e1 = (s contains 'x'), _e2 = (s contains_cs 'x') | project x0 = _e0, x1 = _e1, x2 = _e2",
                kql);
        }

        [Fact]
        public void Options_WithNoDeliverablePreference_AreRejected()
        {
            var f = new Functions();
            int contains = f.Add(Strings, "contains:str_str");
            int add = f.Add(Arithmetic, "add:i64_i64");
            Rejects(P.Plan(P.Project(ReadU(), P.CallWith(contains, new[] { ("case_sensitivity", new[] { "CASE_INSENSITIVE_ASCII" }) }, P.Field(2), P.Str("x"))), null, f),
                "case_sensitivity");
            // Integer overflow behavior is not documented by Kusto, so it can't be promised.
            Rejects(P.Plan(P.Project(ReadU(), P.CallWith(add, new[] { ("overflow", new[] { "ERROR" }) }, P.Field(1), P.I64(1))), null, f), "overflow");
        }

        [Fact]
        public void Options_FirstDeliverablePreferenceIsUsed()
        {
            var f = new Functions();
            int ln = f.Add(Logarithmic, "ln:fp64");
            string kql = ProjectT(f, P.CallWith(ln, new[] { ("on_domain_error", new[] { "ERROR", "NULL" }) }, P.Field(2)));
            Assert.Equal("log(r)", ExpressionKql(kql));
        }

        [Fact]
        public void Like_TranslatesToAnchoredRegex()
        {
            var f = new Functions();
            int like = f.Add(Strings, "like:str_str");
            string kql = ProjectU(f,
                P.Call(like, P.Field(2), P.Str("a%b_.")),
                P.CallWith(like, new[] { ("case_sensitivity", new[] { "CASE_INSENSITIVE" }) }, P.Field(2), P.Str("x%")));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = (s matches regex '(?s)^a.*b.\\\\.$'), _e1 = (s matches regex '(?s)(?i)^x.*$') | project x0 = _e0, x1 = _e1",
                kql);
        }

        [Fact]
        public void Like_NonLiteralPattern_IsRejected()
        {
            var f = new Functions();
            int like = f.Add(Strings, "like:str_str");
            Rejects(P.Plan(P.Project(ReadU(), P.Call(like, P.Field(2), P.Field(2))), null, f), "string literal");
        }

        [Fact]
        public void Substring_ConvertsOneBasedPositions()
        {
            var f = new Functions();
            int substring = f.Add(Strings, "substring:str_i32_i32");
            string kql = ProjectU(f, P.Call(substring, P.Field(2), P.I32(2), P.I32(3)), P.Call(substring, P.Field(2), P.Field(0), P.I32(3)));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = substring(s, 1, int(3)), _e1 = substring(s, max_of(i - 1, 0), max_of(int(3) + min_of(i - 1, 0), 0))"
                + " | project x0 = _e0, x1 = _e1",
                kql);
        }

        [Fact]
        public void StringFunctions()
        {
            var f = new Functions();
            string kql = ProjectU(f,
                P.Call(f.Add(Strings, "concat:str"), P.Field(2), P.Str("-"), P.Field(2)),
                P.Call(f.Add(Strings, "upper:str"), P.Field(2)),
                P.Call(f.Add(Strings, "char_length:str"), P.Field(2)),
                P.Call(f.Add(Strings, "strpos:str_str"), P.Field(2), P.Str("q")),
                P.Call(f.Add(Strings, "trim:str_str"), P.Field(2), P.Str(" -]")),
                P.Call(f.Add(Strings, "replace:str_str_str"), P.Field(2), P.Str("a"), P.Str("b")));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | extend _e0 = strcat(s, '-', s), _e1 = toupper(s), _e2 = strlen(s), _e3 = (indexof(s, 'q') + 1),"
                + " _e4 = trim('[ \\\\-\\\\]]+', s), _e5 = replace_string(s, 'a', 'b')"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3, x4 = _e4, x5 = _e5",
                kql);
        }

        [Fact]
        public void Logical_AndOrNotXor()
        {
            var f = new Functions();
            int and = f.Add(Boolean, "and:bool");
            int or = f.Add(Boolean, "or:bool");
            int not = f.Add(Boolean, "not:bool");
            int xor = f.Add(Boolean, "xor:bool_bool");
            string kql = ProjectT(f,
                P.Call(and, P.Field(4), P.Field(4), P.Bool(true)),
                P.Call(or),
                P.Call(not, P.Field(4)),
                P.Call(xor, P.Field(4), P.Bool(false)));
            KqlAssert.EqualAndAnalyzes(
                "T | project i, l, r = toreal(r), s = tostring(s), b, dt, ts | extend _e0 = (b and b and true), _e1 = false, _e2 = not(b), _e3 = iif(isnull(b) or isnull(false), bool(null), (b != false))"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3",
                kql);
        }

        [Fact]
        public void Modulus_IsTruncatingRemainder()
        {
            var f = new Functions();
            string kql = ProjectU(f, P.Call(f.Add(Arithmetic, "modulus:i64_i64"), P.Field(1), P.I64(3)));
            Assert.Equal("(l - 3 * (l / 3))", ExpressionKql(kql));
            KqlAssert.Analyzes(kql);
        }

        [Fact]
        public void Extract_DateParts()
        {
            var f = new Functions();
            int extract = f.Add(Datetime, "extract:req_ptstz_str");
            int extract2 = f.Add(Datetime, "extract:req_req_ptstz_str");
            string kql = ProjectT(f,
                P.Call(extract, P.Enum("YEAR"), P.Field(5), P.Str("UTC")),
                P.Call(extract2, P.Enum("MONTH"), P.Enum("ZERO"), P.Field(5), P.Str("UTC")),
                P.Call(extract2, P.Enum("MONDAY_DAY_OF_WEEK"), P.Enum("ONE"), P.Field(5), P.Str("UTC")),
                P.Call(extract, P.Enum("MICROSECOND"), P.Field(5), P.Str("Etc/UTC")));
            var columns = KqlAssert.EqualAndAnalyzes(
                "T | project i, l, r = toreal(r), s = tostring(s), b, dt, ts | extend _e0 = tolong(datetime_part('year', dt)), _e1 = tolong(datetime_part('month', dt) - 1),"
                + " _e2 = ((tolong(dayofweek(dt) / 1d) + 6) % 7 + 1), _e3 = (tolong(datetime_part('microsecond', dt)) % 1000)"
                + " | project x0 = _e0, x1 = _e1, x2 = _e2, x3 = _e3",
                kql);
            Assert.All(columns, c => Assert.EndsWith(":long", c));
        }

        [Fact]
        public void Extract_NonUtcTimeZone_IsRejected()
        {
            var f = new Functions();
            int extract = f.Add(Datetime, "extract:req_ptstz_str");
            Rejects(P.Plan(P.Project(ReadT(), P.Call(extract, P.Enum("YEAR"), P.Field(5), P.Str("Europe/Paris"))), null, f), "UTC");
        }

        [Fact]
        public void DatetimeArithmeticAndComparison()
        {
            var f = new Functions();
            int add = f.Add(Datetime, "add:ptstz_iday");
            int gt = f.Add(Datetime, "gt:ptstz_ptstz");
            string kql = ProjectT(f,
                P.Call(add, P.Field(5), P.Field(6)),
                P.Call(gt, P.Field(5), P.TimestampTz(6, 0)));
            KqlAssert.EqualAndAnalyzes(
                "T | project i, l, r = toreal(r), s = tostring(s), b, dt, ts | extend _e0 = (dt + ts), _e1 = (dt > datetime(1970-01-01T00:00:00.0000000Z)) | project x0 = _e0, x1 = _e1",
                kql);
        }

        [Fact]
        public void BareFunctionNames_ResolveByNameAndArgumentTypes()
        {
            // Some producers (e.g. DataFusion) declare functions without an extension URN.
            var f = new Functions();
            int add = f.AddBare("add");
            int gt = f.AddBare("gt");
            string kql = ProjectU(f, P.Call(add, P.Field(1), P.I64(1)), P.Call(gt, P.Field(2), P.Str("a")));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | extend _e0 = (l + 1), _e1 = (strcmp(s, 'a') > 0) | project x0 = _e0, x1 = _e1", kql);
        }

        [Fact]
        public void LegacyExtensionUris_AreAccepted()
        {
            var f = new Functions().UseLegacyUris();
            int add = f.Add(Arithmetic, "add:i64_i64");
            Assert.Equal("(l + 1)", ExpressionKql(ProjectU(f, P.Call(add, P.Field(1), P.I64(1)))));
        }

        [Fact]
        public void UnknownFunctions_AreRejected()
        {
            var f = new Functions();
            int unknown = f.Add(Strings, "levenshtein:str_str");
            int wrongSignature = f.Add(Arithmetic, "add:i8_i8");
            int wrongExtension = f.Add(Strings, "add:i64_i64");
            Rejects(P.Plan(P.Project(ReadU(), P.Call(unknown, P.Field(2), P.Field(2))), null, f), "levenshtein");
            Rejects(P.Plan(P.Project(ReadU(), P.Call(wrongSignature, P.Field(0), P.Field(0))), null, f), "add:i8_i8");
            Rejects(P.Plan(P.Project(ReadU(), P.Call(wrongExtension, P.Field(1), P.Field(1))), null, f), "functions_string");
        }

        [Fact]
        public void UndeclaredFunctionAnchor_IsRejected()
        {
            Rejects(P.Plan(P.Project(ReadU(), P.Call(42, P.Field(0)))), "anchor=42");
        }

        #endregion

        #region Null semantics, schemas and casts

        [Fact]
        public void NotEqual_DropsNullsEvenInFilters()
        {
            // Kusto: null != 5 is true; Substrait: null, so the row must not pass the filter.
            var f = new Functions();
            int ne = f.Add(Comparison, "not_equal:any_any");
            string kql = Translate(P.Plan(P.Filter(ReadU(), P.Call(ne, P.Field(0), P.I32(5))), UNames, f));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | where iif(isnull(i), bool(null), (i != int(5)))", kql);
        }

        [Fact]
        public void Equal_IsGuardedOnlyOutsidePredicates()
        {
            var f = new Functions();
            int eq = f.Add(Comparison, "equal:any_any");
            int not = f.Add(Boolean, "not:bool");
            // In a filter, Kusto's false for a null operand drops the row, as null would.
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | where (i == int(5))",
                Translate(P.Plan(P.Filter(ReadU(), P.Call(eq, P.Field(0), P.I32(5))), UNames, f)));
            // Under not(), false would become true; the guard keeps it null.
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s) | where not(iif(isnull(i), bool(null), (i == int(5))))",
                Translate(P.Plan(P.Filter(ReadU(), P.Call(not, P.Call(eq, P.Field(0), P.I32(5)))), UNames, f)));
            // Strings are never null in Kusto, so no guard is needed.
            Assert.Equal("(s == 'a')", ExpressionKql(ProjectU(f, P.Call(eq, P.Field(2), P.Str("a")))));
        }

        [Fact]
        public void NotIn_PropagatesNulls()
        {
            var f = new Functions();
            int not = f.Add(Boolean, "not:bool");
            string kql = Translate(P.Plan(P.Filter(ReadU(), P.Call(not, P.InList(P.Field(0), P.I32(1), P.Null(P.TI32)))), UNames, f));
            KqlAssert.EqualAndAnalyzes(
                "U | project i, l, s = tostring(s) | where not((iif(isnull(i), bool(null), (i == int(1))) or iif(isnull(i) or isnull(int(null)), bool(null), (i == int(null)))))",
                kql);
        }

        [Fact]
        public void Read_ProjectsDeclaredColumnsInDeclaredOrder()
        {
            // base_schema may list a subset of the table's columns in another order.
            var read = P.Read("U", ("s", P.TString), ("l", P.TI64));
            var columns = KqlAssert.EqualAndAnalyzes("U | project s = tostring(s), l", Translate(P.Plan(read, new[] { "s", "l" })));
            Assert.Equal(new[] { "s:string", "l:long" }, columns);
        }

        [Fact]
        public void Read_ConvertsColumnsToTheTypesTheDriverReports()
        {
            // The driver reports decimal as fp64 and guid as string; plans see values of those types.
            var read = P.Read("T", ("d", P.TFp64), ("g", P.TString));
            var columns = KqlAssert.EqualAndAnalyzes("T | project d = toreal(d), g = tostring(g)", Translate(P.Plan(read, new[] { "d", "g" })));
            Assert.Equal(new[] { "d:real", "g:string" }, columns);
        }

        [Fact]
        public void Identifiers_AreAlwaysQuoted()
        {
            var f = new Functions();
            int isNotNull = f.Add(Comparison, "is_not_null:any");
            var read = P.Read("V", ("title", P.TString), ("flags", P.TI64));
            string kql = Translate(P.Plan(P.Filter(read, P.Call(isNotNull, P.Field(1))), new[] { "title", "flags" }, f));
            Assert.Contains("isnotnull(['flags'])", kql);
            Assert.StartsWith("['V']", kql);
        }

        [Fact]
        public void Casts_ThatKqlCannotHonor_AreRejected()
        {
            Rejects(P.Plan(P.Project(ReadT(), P.Cast(P.TDecimal(10, 2), P.Field(2)))), "decimal(10,2)");
            Rejects(P.Plan(P.Project(ReadT(), P.Cast(P.TTimestampTz(3), P.Field(5)))), "precision 3");
            Rejects(P.Plan(P.Project(ReadT(), P.Cast(P.TString, P.Field(5)))), "to string");
            Assert.Equal("todecimal(l)", ExpressionKql(ProjectU(new Functions(), P.Cast(P.TDecimal(20, 0), P.Field(1)))));
        }

        [Fact]
        public void IntervalLiteral_WithZeroPrecisionOmitted()
        {
            // proto3 drops precision = 0 from the wire; subseconds are then whole seconds' fractions of 10^0.
            var literal = P.LenDel(1, P.LenDel(20, P.Cat(P.VarField(1, 1), P.VarField(2, 30))));
            Assert.Equal("(864300000000tick)", ExpressionKql(ProjectU(new Functions(), literal)));
        }

        [Fact]
        public void Fetch_NullCountMeansAllRows()
        {
            var fetch = P.LenDel(3, P.Cat(P.LenDel(2, ReadU()), P.LenDel(6, P.Null(P.TI64))));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s)", Translate(P.Plan(fetch, UNames)));
        }

        [Fact]
        public void OrderingComparisons_OnUuid_AreRejected()
        {
            var f = new Functions();
            int lt = f.Add(Comparison, "lt:any_any");
            Rejects(P.Plan(P.Project(ReadU(), P.Call(lt, P.Uuid(Guid.Empty), P.Uuid(Guid.Empty))), null, f), "uuid");
        }

        [Fact]
        public void FullOuterJoin_KeepsNullKeysApart()
        {
            // Kusto matches null join keys; a full outer join can't drop rows, so a marker key
            // that differs between the sides for null keys is added.
            var f = new Functions();
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), OrdersCustomerKey(f), P.Outer), new[] { 0, 8 }), new[] { "id", "cname" }, f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag"
                + " | extend _m0 = iif(isnull(customer_id), 1, 0)"
                + " | join kind=fullouter (customers | project id, cname = tostring(cname), region = tostring(region)"
                + " | project id_1 = id, cname, region | extend _m2 = iif(isnull(id_1), 2, 0))"
                + " on $left.customer_id == $right.id_1 and $left._m0 == $right._m2 | project id, cname",
                Translate(plan));
        }

        [Fact]
        public void IsNotDistinctFrom_IsANativeJoinKey()
        {
            var f = new Functions();
            int key = f.Add(Comparison, "is_not_distinct_from:any_any");
            var plan = P.Plan(P.ProjectEmit(P.Join(ReadOrders(), ReadCustomers(), P.Call(key, P.Field(1), P.Field(7)), P.Inner), new[] { 0 }), new[] { "id" }, f);
            KqlAssert.EqualAndAnalyzes(
                "orders | project id, customer_id, name = tostring(name), price = toreal(price), qty, ts, flag"
                + " | join kind=inner (customers | project id, cname = tostring(cname), region = tostring(region) | project id_0 = id, cname, region)"
                + " on $left.customer_id == $right.id_0 | project id",
                Translate(plan));
        }

        [Fact]
        public void NarrowingCasts_ReturnNullOutOfRange()
        {
            // toint()/tolong() wrap or saturate out-of-range values; Substrait expects a failure (null).
            string kql = ProjectT(new Functions(), P.Cast(P.TI32, P.Field(1)), P.Cast(P.TI64, P.Field(2)), P.Cast(P.TI64, P.Field(0)));
            Assert.Contains("_e0 = iif(['l'] >= -2147483648.0 and ['l'] <= 2147483647.0, toint(['l']), int(null))", kql);
            Assert.Contains("_e1 = iif(['r'] >= -9223372036854775808.0 and ['r'] <= 9223372036854774784.0, tolong(['r']), long(null))", kql);
            Assert.Contains("_e2 = tolong(['i'])", kql);
            KqlAssert.Analyzes(kql);
        }

        #endregion

        #region Decoding

        [Fact]
        public void EmptyPlan_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => Translate(Array.Empty<byte>()));
        }

        [Fact]
        public void PlanWithoutRelations_IsRejected()
        {
            Rejects(P.LenDel(6, P.VarField(2, 50)), "no relations");
        }

        [Fact]
        public void TruncatedPlans_AreRejectedAsMalformed()
        {
            var plan = P.Plan(P.Fetch(ReadU(), 1, 2), UNames);
            for (int length = 1; length < plan.Length; length++)
            {
                var truncated = plan.Take(length).ToArray();
                try
                {
                    Translate(truncated);
                }
                catch (SubstraitTranslationException)
                {
                    // Expected; anything else (IndexOutOfRangeException, ...) fails the test.
                }
            }
        }

        [Fact]
        public void UnknownRelationField_IsRejected()
        {
            // FilterRel field 9 is not defined
            var filter = P.LenDel(2, P.Cat(P.LenDel(2, ReadU()), P.LenDel(3, P.Bool(true)), P.VarField(9, 1)));
            Rejects(P.Plan(filter), "Unexpected field 9 in Substrait FilterRel");
        }

        [Fact]
        public void AdvancedExtensionEnhancement_IsRejected()
        {
            // ProjectRel.common = 1 { advanced_extension = 4 { enhancement = 2 } }
            var project = P.LenDel(7, P.Cat(P.LenDel(1, P.LenDel(4, P.LenDel(2, P.Empty))), P.LenDel(2, ReadU())));
            Rejects(P.Plan(project), "enhancement");
        }

        [Fact]
        public void AdvancedExtensionOptimization_IsIgnored()
        {
            // ProjectRel.common = 1 { hint = 3 {}, advanced_extension = 4 { optimization = 1 } }
            var project = P.LenDel(7, P.Cat(P.LenDel(1, P.Cat(P.LenDel(3, P.Empty), P.LenDel(4, P.LenDel(1, P.Empty)))), P.LenDel(2, ReadU())));
            KqlAssert.EqualAndAnalyzes("U | project i, l, s = tostring(s)", Translate(P.Plan(project, UNames)));
        }

        [Fact]
        public void UnsupportedRelation_IsRejected()
        {
            // Rel.window = 17
            Rejects(P.Plan(P.LenDel(17, P.LenDel(2, ReadU()))), "Rel field 17");
        }

        [Fact]
        public void AllTranslationErrors_AreAdbcExceptions()
        {
            var ex = Assert.Throws<SubstraitTranslationException>(() => Translate(P.Plan(P.Set(P.MinusPrimary, ReadU(), ReadU()))));
            Assert.IsAssignableFrom<AdbcException>(ex);
            Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
        }

        #endregion
    }
}
