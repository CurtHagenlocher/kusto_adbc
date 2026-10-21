// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Numerics;
using F = KustoAdbc.Substrait.SubstraitFields;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// A column of a relation's output, as visible to Substrait field references.
    /// </summary>
    sealed class KqlColumn
    {
        public KqlColumn(string name, SType type)
        {
            Name = name;
            Type = type;
        }

        /// <summary>The name of the KQL column holding this field's value.</summary>
        public string Name { get; }
        public SType Type { get; }
    }

    /// <summary>
    /// The state of a KQL pipeline after a relation has been written:
    /// <see cref="Schema"/> maps Substrait field ordinals to KQL columns, and
    /// <see cref="Physical"/> lists the columns the KQL tabular expression
    /// actually has at this point, in order. The physical set can be a superset
    /// of the schema (e.g. after <c>extend</c>), and a schema can refer to the
    /// same KQL column more than once.
    /// </summary>
    sealed class KqlPipeline
    {
        public KqlPipeline(List<KqlColumn> schema, List<string> physical)
        {
            Schema = schema;
            Physical = physical;
        }

        public List<KqlColumn> Schema { get; set; }
        public List<string> Physical { get; set; }

        /// <summary>True when the KQL columns are exactly the schema's, in order and without duplicates.</summary>
        public bool IsExact
        {
            get
            {
                if (Schema.Count != Physical.Count)
                    return false;
                for (int i = 0; i < Schema.Count; i++)
                {
                    if (!string.Equals(Schema[i].Name, Physical[i], StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// Writes KQL for a decoded Substrait plan.
    ///
    /// Each relation is written as a KQL tabular pipeline. Substrait refers to
    /// columns by ordinal and KQL by name, so the emitter tracks which KQL column
    /// holds each output field of every relation (see <see cref="KqlPipeline"/>).
    /// The final result always has exactly the Substrait output columns, in order.
    /// </summary>
    sealed class KqlEmitter
    {
        readonly Utf8KqlWriter _w;
        readonly bool _perRecordEvaluation;
        int _nameCounter;

        public KqlEmitter(Utf8KqlWriter writer, bool perRecordEvaluation)
        {
            _w = writer;
            _perRecordEvaluation = perRecordEvaluation;
        }

        internal Utf8KqlWriter Writer => _w;

        /// <summary>Writes the whole query and returns the output columns.</summary>
        public List<KqlColumn> EmitRoot(RootRel root)
        {
            var p = EmitRel(root.Input);
            var names = root.Names;
            if (names == null || names.Count == 0)
            {
                EnsureExact(p, null);
                return p.Schema;
            }

            if (names.Count != p.Schema.Count)
                throw SubstraitTranslationException.MalformedPlan($"RelRoot has {names.Count} names but its input has {p.Schema.Count} columns.");

            // KQL requires distinct output column names.
            var used = new HashSet<string>(StringComparer.Ordinal);
            var finalNames = new List<string>(names.Count);
            foreach (var n in names)
            {
                string name = n;
                for (int suffix = 1; !used.Add(name); suffix++)
                    name = n + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                finalNames.Add(name);
            }

            bool needsProject = !p.IsExact;
            for (int i = 0; i < finalNames.Count && !needsProject; i++)
                needsProject = !string.Equals(finalNames[i], p.Schema[i].Name, StringComparison.Ordinal);

            var output = new List<KqlColumn>(finalNames.Count);
            if (needsProject)
            {
                _w.Write("\n| project ");
                for (int i = 0; i < finalNames.Count; i++)
                {
                    if (i > 0) _w.Write(", ");
                    WriteAlias(finalNames[i], p.Schema[i].Name);
                }
            }
            for (int i = 0; i < finalNames.Count; i++)
                output.Add(new KqlColumn(finalNames[i], p.Schema[i].Type));
            return output;
        }

        #region Relations

        KqlPipeline EmitRel(Rel rel)
        {
            var p = rel switch
            {
                ReadRel r => EmitRead(r),
                FilterRel r => EmitFilter(r),
                ProjectRel r => EmitProject(r),
                FetchRel r => EmitFetch(r),
                SortRel r => EmitSort(r),
                AggregateRel r => EmitAggregate(r),
                JoinRel r => EmitJoin(r),
                CrossRel r => EmitCross(r),
                SetRel r => EmitSet(r),
                TopNRel r => EmitTopN(r),
                _ => throw SubstraitTranslationException.Unsupported($"relation {rel.GetType().Name}"),
            };

            if (rel.Emit != null)
            {
                var emitted = new List<KqlColumn>(rel.Emit.Count);
                foreach (int index in rel.Emit)
                {
                    if (index < 0 || index >= p.Schema.Count)
                        throw SubstraitTranslationException.MalformedPlan($"Emit output mapping references field {index}, but the relation has {p.Schema.Count} fields.");
                    emitted.Add(p.Schema[index]);
                }
                p.Schema = emitted;
            }
            return p;
        }

        KqlPipeline EmitRead(ReadRel rel)
        {
            var names = rel.TableNames;
            if (names.Count == 3)
            {
                _w.Write("cluster(");
                _w.WriteStringLiteral(names[0]);
                _w.Write(").");
            }
            if (names.Count >= 2)
            {
                _w.Write("database(");
                _w.WriteStringLiteral(names[names.Count - 2]);
                _w.Write(").");
            }
            _w.WriteIdentifier(names[names.Count - 1]);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var schema = new List<KqlColumn>(rel.ColumnNames.Count);
            for (int i = 0; i < rel.ColumnNames.Count; i++)
            {
                if (!seen.Add(rel.ColumnNames[i]))
                    throw SubstraitTranslationException.MalformedPlan($"base_schema has duplicate column name '{rel.ColumnNames[i]}'.");
                schema.Add(new KqlColumn(rel.ColumnNames[i], rel.ColumnTypes[i]));
            }
            // The table may have other columns, or a different order, than base_schema declares, so
            // the declared columns are always projected. Columns declared fp64 or string are converted:
            // the driver reports Kusto decimal columns as fp64 and guid/dynamic columns as string, and
            // a plan written against those types must see values of those types.
            List<KqlColumn>? projected = null;
            if (rel.Projection != null)
            {
                projected = new List<KqlColumn>(rel.Projection.Count);
                foreach (int index in rel.Projection)
                {
                    if (index < 0 || index >= schema.Count)
                        throw SubstraitTranslationException.MalformedPlan($"ReadRel projection references field {index}, but base_schema has {schema.Count} fields.");
                    projected.Add(schema[index]);
                }
            }

            // The filters apply to the base schema, before the projection; without filters the
            // projection can be applied by the first project.
            bool hasFilters = rel.Filter != null || rel.BestEffortFilter != null;
            var first = !hasFilters && projected != null && projected.Distinct().Count() == projected.Count ? projected : schema;
            _w.Write("\n| project ");
            for (int i = 0; i < first.Count; i++)
            {
                if (i > 0) _w.Write(", ");
                string? conversion = first[i].Type.Kind switch
                {
                    TypeKind.Fp64 => "toreal(",
                    TypeKind.String => "tostring(",
                    _ => null,
                };
                _w.WriteIdentifier(first[i].Name);
                if (conversion != null)
                {
                    _w.Write(" = ");
                    _w.Write(conversion);
                    _w.WriteIdentifier(first[i].Name);
                    _w.Write(")");
                }
            }
            var p = new KqlPipeline(new List<KqlColumn>(first), first.Select(c => c.Name).ToList());

            if (rel.Filter != null)
                WriteWhere(rel.Filter, p.Schema);
            if (rel.BestEffortFilter != null)
                WriteWhere(rel.BestEffortFilter, p.Schema);

            if (projected != null && !ReferenceEquals(first, projected))
            {
                p.Schema = projected;
                EnsureExact(p, null);
            }
            return p;
        }

        KqlPipeline EmitFilter(FilterRel rel)
        {
            var p = EmitRel(rel.Input);
            WriteWhere(rel.Condition, p.Schema);
            return p;
        }

        void WriteWhere(Expr condition, IReadOnlyList<KqlColumn> scope)
        {
            _w.Write("\n| where ");
            WriteExpr(condition, scope, predicate: true);
        }

        KqlPipeline EmitProject(ProjectRel rel)
        {
            var p = EmitRel(rel.Input);
            var input = p.Schema;
            var output = new List<KqlColumn>(input);
            bool first = true;
            foreach (var expr in rel.Expressions)
            {
                if (expr is FieldRefExpr fr)
                {
                    // A plain column reference doesn't need a new KQL column.
                    output.Add(Column(input, fr.Index));
                    continue;
                }

                string name = NewName("_e", p.Physical);
                var type = InferType(expr, input);
                _w.Write(first ? "\n| extend " : ", ");
                first = false;
                _w.WriteIdentifier(name);
                _w.Write(" = ");
                WriteExpr(expr, input);
                p.Physical.Add(name);
                output.Add(new KqlColumn(name, type));
            }
            p.Schema = output;
            return p;
        }

        KqlPipeline EmitFetch(FetchRel rel)
        {
            var p = EmitRel(rel.Input);
            WriteOffsetAndCount(p, rel.Offset, rel.Count);
            return p;
        }

        void WriteOffsetAndCount(KqlPipeline p, Expr? offsetExpr, Expr? countExpr)
        {
            long offset = offsetExpr == null ? 0 : ConstantRowCount(offsetExpr, "offset") ?? 0;
            long? count = countExpr == null ? null : ConstantRowCount(countExpr, "count");
            if (offset > 0)
            {
                // Rows keep the order of a preceding sort; serialize numbers them in that order.
                string rn = NewName("_rn", p.Physical);
                _w.Write("\n| serialize ");
                _w.WriteIdentifier(rn);
                _w.Write(" = row_number()\n| where ");
                _w.WriteIdentifier(rn);
                _w.Write(" > ");
                _w.WriteInt64(offset);
                p.Physical.Add(rn);
            }
            if (count != null)
            {
                _w.Write("\n| take ");
                _w.WriteInt64(count.Value);
            }
        }

        static long? ConstantRowCount(Expr expr, string what)
        {
            // A null offset means 0 and a null count means all rows.
            if (expr is LiteralExpr { IsNull: true })
                return null;
            if (expr is LiteralExpr { IsNull: false } lit && lit.Type.Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64)
            {
                if (lit.IntValue < 0)
                    throw SubstraitTranslationException.MalformedPlan($"Fetch {what} must not be negative.");
                return lit.IntValue;
            }
            throw SubstraitTranslationException.Unsupported($"Fetch {what} must be an integer literal.");
        }

        KqlPipeline EmitSort(SortRel rel)
        {
            var p = EmitRel(rel.Input);
            WriteSort(p, rel.Sorts);
            return p;
        }

        void WriteSort(KqlPipeline p, List<SortField> sorts)
        {
            if (sorts.Count == 0)
                return;

            // Sort keys that are expressions are materialized first, so that the sort clause only names columns.
            var keys = new List<string>(sorts.Count);
            bool first = true;
            foreach (var sort in sorts)
            {
                if (sort.Expr is FieldRefExpr fr)
                {
                    keys.Add(Column(p.Schema, fr.Index).Name);
                    continue;
                }
                string name = NewName("_s", p.Physical);
                _w.Write(first ? "\n| extend " : ", ");
                first = false;
                _w.WriteIdentifier(name);
                _w.Write(" = ");
                WriteExpr(sort.Expr, p.Schema);
                p.Physical.Add(name);
                keys.Add(name);
            }

            _w.Write("\n| sort by ");
            for (int i = 0; i < sorts.Count; i++)
            {
                if (i > 0) _w.Write(", ");
                _w.WriteIdentifier(keys[i]);
                // KQL defaults to descending order, so the direction is always written.
                _w.Write(sorts[i].Direction switch
                {
                    F.SortDirection_AscNullsFirst => " asc nulls first",
                    F.SortDirection_AscNullsLast => " asc nulls last",
                    F.SortDirection_DescNullsFirst => " desc nulls first",
                    F.SortDirection_DescNullsLast => " desc nulls last",
                    // Clustered only requires equal values to be adjacent; any total order satisfies it.
                    F.SortDirection_Clustered => " asc nulls first",
                    _ => throw SubstraitTranslationException.MalformedPlan($"Sort direction {sorts[i].Direction} is not valid."),
                });
            }
        }

        KqlPipeline EmitTopN(TopNRel rel)
        {
            var p = EmitRel(rel.Input);
            WriteSort(p, rel.Sorts);
            WriteOffsetAndCount(p, rel.Offset, rel.Count);
            return p;
        }

        KqlPipeline EmitAggregate(AggregateRel rel)
        {
            var p = EmitRel(rel.Input);
            var input = p.Schema;
            var output = new List<KqlColumn>(rel.Keys.Count + rel.Measures.Count);
            var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _w.Write("\n| summarize ");
            for (int i = 0; i < rel.Measures.Count; i++)
            {
                if (i > 0) _w.Write(", ");
                string name = NewName("_a", p.Physical);
                outputNames.Add(name);
                _w.WriteIdentifier(name);
                _w.Write(" = ");
                var type = WriteAggregate(rel.Measures[i], input);
                output.Add(new KqlColumn(name, type));
            }

            var keyColumns = new List<KqlColumn>(rel.Keys.Count);
            if (rel.Keys.Count > 0)
            {
                _w.Write(rel.Measures.Count > 0 ? " by " : "by ");
                for (int i = 0; i < rel.Keys.Count; i++)
                {
                    if (i > 0) _w.Write(", ");
                    var key = rel.Keys[i];
                    if (key is FieldRefExpr fr && outputNames.Add(Column(input, fr.Index).Name))
                    {
                        var col = Column(input, fr.Index);
                        _w.WriteIdentifier(col.Name);
                        keyColumns.Add(col);
                        continue;
                    }
                    string name = NewName("_g", p.Physical, outputNames);
                    outputNames.Add(name);
                    _w.WriteIdentifier(name);
                    _w.Write(" = ");
                    WriteExpr(key, input);
                    keyColumns.Add(new KqlColumn(name, InferType(key, input)));
                }
            }

            // summarize outputs the grouping keys first, then the aggregates, as Substrait does.
            output.InsertRange(0, keyColumns);
            return new KqlPipeline(output, output.Select(c => c.Name).ToList());
        }

        KqlPipeline EmitJoin(JoinRel rel)
        {
            string kind = rel.Type switch
            {
                F.JoinType_Inner => "inner",
                F.JoinType_Outer => "fullouter",
                F.JoinType_Left => "leftouter",
                F.JoinType_Right => "rightouter",
                F.JoinType_LeftSemi => "leftsemi",
                F.JoinType_LeftAnti => "leftanti",
                F.JoinType_RightSemi => "rightsemi",
                F.JoinType_RightAnti => "rightanti",
                _ => throw SubstraitTranslationException.Unsupported($"Join type {rel.Type} has no KQL equivalent."),
            };

            var left = EmitRel(rel.Left);
            EnsureExact(left, null);
            int leftCount = left.Schema.Count;

            // Classify the conjuncts of the join condition. KQL joins accept only
            // equalities between a left and a right column; any other conjunct has
            // to be moved somewhere it means the same thing, or the plan is rejected.
            var keys = new List<(int Left, int Right, bool NullSafe)>();
            var leftOnly = new List<Expr>();
            var rightOnly = new List<Expr>();
            var mixed = new List<Expr>();
            if (rel.Expression != null)
            {
                foreach (var conjunct in Conjuncts(rel.Expression))
                {
                    if (TryGetJoinKey(conjunct, leftCount, out var key))
                    {
                        keys.Add(key);
                        continue;
                    }
                    var (usesLeft, usesRight) = Sides(conjunct, leftCount);
                    if (usesLeft && usesRight) mixed.Add(conjunct);
                    else if (usesRight) rightOnly.Add(conjunct);
                    else leftOnly.Add(conjunct);
                }
            }

            var postFilters = new List<Expr>();
            var leftFilters = new List<Expr>();
            var rightFilters = new List<Expr>();
            switch (rel.Type)
            {
                case F.JoinType_Inner:
                    postFilters.AddRange(leftOnly);
                    postFilters.AddRange(rightOnly);
                    postFilters.AddRange(mixed);
                    break;
                case F.JoinType_Left:
                    RequireEmpty(leftOnly, mixed, "LEFT");
                    rightFilters.AddRange(rightOnly);
                    break;
                case F.JoinType_Right:
                    RequireEmpty(rightOnly, mixed, "RIGHT");
                    leftFilters.AddRange(leftOnly);
                    break;
                case F.JoinType_LeftSemi:
                    RequireEmpty(mixed, null, "LEFT SEMI");
                    leftFilters.AddRange(leftOnly);
                    rightFilters.AddRange(rightOnly);
                    break;
                case F.JoinType_RightSemi:
                    RequireEmpty(mixed, null, "RIGHT SEMI");
                    leftFilters.AddRange(leftOnly);
                    rightFilters.AddRange(rightOnly);
                    break;
                case F.JoinType_LeftAnti:
                    // A left row failing a left-only condition has no match, so it is kept: such
                    // a condition can't become a filter on the left input.
                    RequireEmpty(leftOnly, mixed, "LEFT ANTI");
                    rightFilters.AddRange(rightOnly);
                    break;
                case F.JoinType_RightAnti:
                    RequireEmpty(rightOnly, mixed, "RIGHT ANTI");
                    leftFilters.AddRange(leftOnly);
                    break;
                default:
                    RequireEmpty(leftOnly, rightOnly, "FULL OUTER");
                    RequireEmpty(mixed, null, "FULL OUTER");
                    break;
            }

            if (rel.PostJoinFilter != null && rel.Type is not (F.JoinType_Inner or F.JoinType_Outer or F.JoinType_Left or F.JoinType_Right))
                throw SubstraitTranslationException.Unsupported("A post-join filter on a semi or anti join is not supported.");

            foreach (var f in leftFilters)
                WriteWhere(f, left.Schema);

            // Null keys: see WriteNullKeyFilter. Right-preserving kinds drop them on the left,
            // the others on the right; a full outer join uses markers instead.
            var strictKeys = keys.Where(k => !k.NullSafe).ToList();
            bool filterLeftNulls = rel.Type is F.JoinType_Right or F.JoinType_RightSemi or F.JoinType_RightAnti;
            bool filterRightNulls = rel.Type is F.JoinType_Inner or F.JoinType_Left or F.JoinType_LeftSemi or F.JoinType_LeftAnti;
            if (filterLeftNulls)
                WriteNullKeyFilter(left, strictKeys.Select(k => k.Left));
            var leftMarkers = new List<string?>();
            if (rel.Type == F.JoinType_Outer)
            {
                foreach (var k in strictKeys)
                    leftMarkers.Add(WriteNullKeyMarker(left, k.Left, 1, null));
            }

            // Joins without an equality key become a join on a constant dummy key.
            string? leftDummy = null, rightDummy = null;
            if (keys.Count == 0)
            {
                leftDummy = NewName("_k", left.Physical);
                _w.Write("\n| extend ");
                _w.WriteIdentifier(leftDummy);
                _w.Write(" = 1");
                left.Physical.Add(leftDummy);
            }

            _w.Write("\n| join kind=");
            _w.Write(kind);
            _w.Write(" (");
            var right = EmitRel(rel.Right);
            foreach (var f in rightFilters)
                WriteWhere(ShiftFields(f, -leftCount), right.Schema);
            // Right columns are renamed so that none collides with a left column;
            // otherwise KQL would rename them itself.
            EnsureExact(right, left.Physical);
            if (filterRightNulls)
                WriteNullKeyFilter(right, strictKeys.Select(k => k.Right - leftCount));
            var markerKeys = new List<(string Left, string Right)>();
            for (int i = 0; i < leftMarkers.Count; i++)
            {
                // Null keys can only meet when both sides can be null.
                string? rightMarker = leftMarkers[i] == null ? null : WriteNullKeyMarker(right, strictKeys[i].Right - leftCount, 2, left.Physical);
                if (rightMarker != null)
                    markerKeys.Add((leftMarkers[i]!, rightMarker));
            }
            if (keys.Count == 0)
            {
                rightDummy = NewName("_k", right.Physical, left.Physical);
                _w.Write("\n| extend ");
                _w.WriteIdentifier(rightDummy);
                _w.Write(" = 1");
                right.Physical.Add(rightDummy);
            }
            _w.Write(") on ");

            if (keys.Count == 0)
            {
                WriteJoinKey(leftDummy!, rightDummy!);
            }
            else
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) _w.Write(" and ");
                    WriteJoinKey(left.Schema[keys[i].Left].Name, right.Schema[keys[i].Right - leftCount].Name);
                }
                foreach (var (leftMarker, rightMarker) in markerKeys)
                {
                    _w.Write(" and ");
                    WriteJoinKey(leftMarker, rightMarker);
                }
            }

            List<KqlColumn> schema;
            List<string> physical;
            switch (rel.Type)
            {
                case F.JoinType_LeftSemi:
                case F.JoinType_LeftAnti:
                    schema = left.Schema;
                    physical = left.Physical;
                    break;
                case F.JoinType_RightSemi:
                case F.JoinType_RightAnti:
                    schema = right.Schema;
                    physical = right.Physical;
                    break;
                default:
                    schema = new List<KqlColumn>(left.Schema.Count + right.Schema.Count);
                    schema.AddRange(left.Schema);
                    schema.AddRange(right.Schema.Select(c => OuterNullable(c, rel.Type)));
                    physical = new List<string>(left.Physical);
                    physical.AddRange(right.Physical);
                    break;
            }
            var p = new KqlPipeline(schema, physical);

            foreach (var f in postFilters)
                WriteWhere(f, p.Schema);
            if (rel.PostJoinFilter != null)
                WriteWhere(rel.PostJoinFilter, p.Schema);
            return p;
        }

        static KqlColumn OuterNullable(KqlColumn c, int joinType)
            => joinType is F.JoinType_Left or F.JoinType_Outer ? new KqlColumn(c.Name, c.Type.WithNullable(true)) : c;

        static void RequireEmpty(List<Expr> a, List<Expr>? b, string joinKind)
        {
            if (a.Count > 0 || (b != null && b.Count > 0))
                throw SubstraitTranslationException.Unsupported(
                    $"A {joinKind} join condition with non-equality terms that cannot be applied as filters is not supported; KQL joins only accept equality between left and right columns.");
        }

        void WriteJoinKey(string left, string right)
        {
            _w.Write("$left.");
            _w.WriteIdentifier(left);
            _w.Write(" == $right.");
            _w.WriteIdentifier(right);
        }

        static IEnumerable<Expr> Conjuncts(Expr expr)
        {
            if (expr is ScalarFunctionExpr { Function.Name: "and" } and && KqlFunctionCatalog.IsBooleanFunction(and.Function))
            {
                foreach (var arg in and.Args)
                {
                    if (arg.Value == null)
                        throw SubstraitTranslationException.MalformedPlan("'and' has a non-value argument.");
                    foreach (var c in Conjuncts(arg.Value))
                        yield return c;
                }
            }
            else if (expr is LiteralExpr { IsNull: false, Type.Kind: TypeKind.Bool, BoolValue: true })
            {
                // ON TRUE contributes nothing.
            }
            else
            {
                yield return expr;
            }
        }

        /// <summary>
        /// Recognizes equal(left column, right column), and is_not_distinct_from, whose null-matching
        /// semantics are those of Kusto join keys.
        /// </summary>
        static bool TryGetJoinKey(Expr expr, int leftCount, out (int Left, int Right, bool NullSafe) key)
        {
            key = default;
            if (expr is not ScalarFunctionExpr { Function.Name: "equal" or "is_not_distinct_from" } eq || !KqlFunctionCatalog.IsComparisonFunction(eq.Function))
                return false;
            if (eq.Options.Count > 0 || eq.Args.Count != 2 || eq.Args[0].Value is not FieldRefExpr a || eq.Args[1].Value is not FieldRefExpr b)
                return false;
            bool nullSafe = eq.Function.Name == "is_not_distinct_from";
            if (a.Index < leftCount && b.Index >= leftCount)
                key = (a.Index, b.Index, nullSafe);
            else if (b.Index < leftCount && a.Index >= leftCount)
                key = (b.Index, a.Index, nullSafe);
            else
                return false;
            return true;
        }

        /// <summary>True when a column of this type can hold null in Kusto (strings can't).</summary>
        static bool TypeMayBeNull(SType type) => !type.IsStringLike && (!type.IsKnown || type.Nullable);

        /// <summary>
        /// Kusto join keys match when both are null (observed on a cluster), but Substrait's equal
        /// never matches a null. For each such key, rows with a null key are removed from the side
        /// whose unmatched rows the join discards anyway (they can't match in Substrait).
        /// </summary>
        void WriteNullKeyFilter(KqlPipeline side, IEnumerable<int> keyIndexes)
        {
            var nullable = keyIndexes.Where(i => TypeMayBeNull(side.Schema[i].Type)).ToList();
            if (nullable.Count == 0)
                return;
            _w.Write("\n| where ");
            for (int i = 0; i < nullable.Count; i++)
            {
                if (i > 0) _w.Write(" and ");
                _w.Write("isnotnull(");
                _w.WriteIdentifier(side.Schema[nullable[i]].Name);
                _w.Write(")");
            }
        }

        /// <summary>
        /// For a full outer join, where no side may lose rows: adds a marker column that is 0 for a
        /// non-null key and differs between the sides (1 left, 2 right) for a null key, so that null
        /// keys never match. Returns the marker name, or null when the key can't be null on this side.
        /// </summary>
        string? WriteNullKeyMarker(KqlPipeline side, int keyIndex, int nullValue, IEnumerable<string>? alsoAvoid)
        {
            if (!TypeMayBeNull(side.Schema[keyIndex].Type))
                return null;
            string marker = NewName("_m", side.Physical, alsoAvoid);
            _w.Write("\n| extend ");
            _w.WriteIdentifier(marker);
            _w.Write(" = iif(isnull(");
            _w.WriteIdentifier(side.Schema[keyIndex].Name);
            _w.Write("), ");
            _w.Write(nullValue.ToString(CultureInfo.InvariantCulture));
            _w.Write(", 0)");
            side.Physical.Add(marker);
            return marker;
        }

        static (bool Left, bool Right) Sides(Expr expr, int leftCount)
        {
            bool left = false, right = false;
            Visit(expr);
            return (left, right);

            void Visit(Expr e)
            {
                switch (e)
                {
                    case FieldRefExpr fr:
                        if (fr.Index < leftCount) left = true; else right = true;
                        break;
                    case ScalarFunctionExpr sf:
                        foreach (var a in sf.Args) if (a.Value != null) Visit(a.Value);
                        break;
                    case IfThenExpr it:
                        foreach (var (c, t) in it.Clauses) { Visit(c); Visit(t); }
                        if (it.Else != null) Visit(it.Else);
                        break;
                    case SwitchExpr sw:
                        Visit(sw.Match);
                        foreach (var (_, t) in sw.Cases) Visit(t);
                        if (sw.Else != null) Visit(sw.Else);
                        break;
                    case SingularOrListExpr sl:
                        Visit(sl.Value);
                        foreach (var o in sl.Options) Visit(o);
                        break;
                    case MultiOrListExpr ml:
                        foreach (var v in ml.Values) Visit(v);
                        foreach (var r in ml.Options) foreach (var o in r) Visit(o);
                        break;
                    case CastExpr c:
                        Visit(c.Input);
                        break;
                }
            }
        }

        /// <summary>Returns a copy of <paramref name="expr"/> with every field reference moved by <paramref name="delta"/>.</summary>
        static Expr ShiftFields(Expr expr, int delta)
        {
            Expr S(Expr e) => ShiftFields(e, delta);
            List<FunctionArg> Args(List<FunctionArg> args)
                => args.Select(a => new FunctionArg { Value = a.Value == null ? null : S(a.Value), Enum = a.Enum, TypeArg = a.TypeArg }).ToList();

            switch (expr)
            {
                case FieldRefExpr fr:
                    return new FieldRefExpr { Index = fr.Index + delta };
                case ScalarFunctionExpr sf:
                    return new ScalarFunctionExpr { Function = sf.Function, Args = Args(sf.Args), Options = sf.Options, OutputType = sf.OutputType };
                case IfThenExpr it:
                    return new IfThenExpr { Clauses = it.Clauses.Select(c => (S(c.If), S(c.Then))).ToList(), Else = it.Else == null ? null : S(it.Else) };
                case SwitchExpr sw:
                    return new SwitchExpr { Match = S(sw.Match), Cases = sw.Cases.Select(c => (c.If, S(c.Then))).ToList(), Else = sw.Else == null ? null : S(sw.Else) };
                case SingularOrListExpr sl:
                    return new SingularOrListExpr { Value = S(sl.Value), Options = sl.Options.Select(S).ToList() };
                case MultiOrListExpr ml:
                    return new MultiOrListExpr { Values = ml.Values.Select(S).ToList(), Options = ml.Options.Select(r => r.Select(S).ToList()).ToList() };
                case CastExpr c:
                    return new CastExpr { Type = c.Type, Input = S(c.Input), FailureBehavior = c.FailureBehavior };
                default:
                    return expr;
            }
        }

        KqlPipeline EmitCross(CrossRel rel)
            => EmitJoin(new JoinRel { Left = rel.Left, Right = rel.Right, Type = F.JoinType_Inner });

        KqlPipeline EmitSet(SetRel rel)
        {
            if (rel.Op is not (F.SetOp_UnionAll or F.SetOp_UnionDistinct))
                throw SubstraitTranslationException.Unsupported($"Set operation {rel.Op} is not supported; only UNION ALL and UNION DISTINCT are.");

            var first = EmitRel(rel.Inputs[0]);
            EnsureExact(first, null);
            var names = first.Physical;

            if (rel.Inputs.Count > 1)
            {
                // KQL's union matches columns by name (and type), not position, so every
                // other input is projected onto the first input's column names.
                _w.Write("\n| union ");
                for (int i = 1; i < rel.Inputs.Count; i++)
                {
                    if (i > 1) _w.Write(", ");
                    _w.Write("(");
                    var p = EmitRel(rel.Inputs[i]);
                    if (p.Schema.Count != names.Count)
                        throw SubstraitTranslationException.MalformedPlan("SetRel inputs have different numbers of columns.");
                    _w.Write("\n| project ");
                    for (int c = 0; c < names.Count; c++)
                    {
                        if (c > 0) _w.Write(", ");
                        WriteAlias(names[c], p.Schema[c].Name);
                    }
                    _w.Write(")");
                }
            }

            if (rel.Op == F.SetOp_UnionDistinct)
                _w.Write("\n| distinct *");

            var schema = new List<KqlColumn>(first.Schema);
            return new KqlPipeline(schema, new List<string>(names));
        }

        /// <summary>
        /// Makes the KQL columns exactly the schema's columns, in order, by writing a
        /// project when needed. Duplicate columns, and columns whose names appear in
        /// <paramref name="avoid"/>, are given new names.
        /// </summary>
        void EnsureExact(KqlPipeline p, IReadOnlyCollection<string>? avoid)
        {
            var avoidSet = avoid == null ? null : new HashSet<string>(avoid, StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newNames = new List<string>(p.Schema.Count);
            bool renamed = false;
            foreach (var col in p.Schema)
            {
                string name = col.Name;
                if (!used.Add(name) || (avoidSet != null && avoidSet.Contains(name)))
                {
                    name = NewName(col.Name + "_", used, avoidSet);
                    used.Add(name);
                    renamed = true;
                }
                newNames.Add(name);
            }

            if (!renamed && p.IsExact)
                return;

            _w.Write("\n| project ");
            for (int i = 0; i < newNames.Count; i++)
            {
                if (i > 0) _w.Write(", ");
                WriteAlias(newNames[i], p.Schema[i].Name);
            }
            p.Schema = p.Schema.Select((c, i) => new KqlColumn(newNames[i], c.Type)).ToList();
            p.Physical = new List<string>(newNames);
        }

        void WriteAlias(string name, string source)
        {
            _w.WriteIdentifier(name);
            if (!string.Equals(name, source, StringComparison.Ordinal))
            {
                _w.Write(" = ");
                _w.WriteIdentifier(source);
            }
        }

        string NewName(string prefix, IEnumerable<string> existing, IEnumerable<string>? alsoAvoid = null)
        {
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            if (alsoAvoid != null)
                taken.UnionWith(alsoAvoid);
            string name;
            do
            {
                name = prefix + (_nameCounter++).ToString(CultureInfo.InvariantCulture);
            } while (taken.Contains(name));
            return name;
        }

        static KqlColumn Column(IReadOnlyList<KqlColumn> scope, int index)
        {
            if (index < 0 || index >= scope.Count)
                throw SubstraitTranslationException.MalformedPlan($"Field reference {index} is out of range; the input has {scope.Count} fields.");
            return scope[index];
        }

        #endregion

        #region Expressions

        internal void Write(string text) => _w.Write(text);

        /// <summary>
        /// Writes an expression. <paramref name="predicate"/> is true when a boolean result only decides
        /// whether a row is kept (where clauses, measure filters, case conditions); there null and false
        /// are equivalent, and comparisons need no null-propagation guards.
        /// </summary>
        internal void WriteExpr(Expr expr, IReadOnlyList<KqlColumn> scope, bool predicate = false)
        {
            switch (expr)
            {
                case LiteralExpr lit: WriteLiteral(lit); break;
                case FieldRefExpr fr: _w.WriteIdentifier(Column(scope, fr.Index).Name); break;
                case ScalarFunctionExpr sf: KqlFunctionCatalog.WriteScalar(this, sf, scope, predicate); break;
                case IfThenExpr it: WriteIfThen(it, scope); break;
                case SwitchExpr sw: WriteSwitch(sw, scope); break;
                case SingularOrListExpr sl: WriteSingularOrList(sl, scope, predicate); break;
                case MultiOrListExpr ml: WriteMultiOrList(ml, scope, predicate); break;
                case CastExpr c: WriteCast(c, scope); break;
                case ContextVariableExpr cv: WriteContextVariable(cv); break;
                default: throw SubstraitTranslationException.UnsupportedExpression(expr.GetType().Name);
            }
        }

        /// <summary>
        /// True when the expression may evaluate to null in Kusto. Strings never do (a missing
        /// string is ''), nor do non-null literals or columns typed as required.
        /// </summary>
        internal bool MayBeNull(Expr expr, IReadOnlyList<KqlColumn> scope)
        {
            if (expr is LiteralExpr lit)
                return lit.IsNull && !lit.Type.IsStringLike;
            var type = InferType(expr, scope);
            if (type.IsStringLike)
                return false;
            return !type.IsKnown || type.Nullable;
        }

        internal SType InferType(Expr expr, IReadOnlyList<KqlColumn> scope)
        {
            switch (expr)
            {
                case LiteralExpr lit: return lit.Type;
                case FieldRefExpr fr: return Column(scope, fr.Index).Type;
                case ScalarFunctionExpr sf: return sf.OutputType ?? KqlFunctionCatalog.InferScalarType(this, sf, scope);
                case IfThenExpr it:
                    foreach (var (_, then) in it.Clauses)
                    {
                        var t = InferType(then, scope);
                        if (t.IsKnown) return t;
                    }
                    return it.Else == null ? SType.Unknown : InferType(it.Else, scope);
                case SwitchExpr sw:
                    foreach (var (_, then) in sw.Cases)
                    {
                        var t = InferType(then, scope);
                        if (t.IsKnown) return t;
                    }
                    return sw.Else == null ? SType.Unknown : InferType(sw.Else, scope);
                case SingularOrListExpr:
                case MultiOrListExpr:
                    return SType.Bool;
                case CastExpr c: return c.Type;
                case ContextVariableExpr cv:
                    return cv.Variable switch
                    {
                        F.ContextVariable_CurrentTimestamp => new SType(TypeKind.PrecisionTimestampTz, false, 7),
                        F.ContextVariable_CurrentDate => new SType(TypeKind.Date, false),
                        _ => new SType(TypeKind.String, false),
                    };
                default: return SType.Unknown;
            }
        }

        void WriteIfThen(IfThenExpr expr, IReadOnlyList<KqlColumn> scope)
        {
            _w.Write("case(");
            foreach (var (cond, then) in expr.Clauses)
            {
                // A null condition falls through to the next clause, as a false one does.
                WriteExpr(cond, scope, predicate: true);
                _w.Write(", ");
                WriteExpr(then, scope);
                _w.Write(", ");
            }
            if (expr.Else != null)
                WriteExpr(expr.Else, scope);
            else
                WriteTypedNull(InferType(expr, scope));
            _w.Write(")");
        }

        void WriteSwitch(SwitchExpr expr, IReadOnlyList<KqlColumn> scope)
        {
            _w.Write("case(");
            foreach (var (value, then) in expr.Cases)
            {
                _w.Write("(");
                WriteExpr(expr.Match, scope);
                _w.Write(" == ");
                WriteLiteral(value);
                _w.Write("), ");
                WriteExpr(then, scope);
                _w.Write(", ");
            }
            if (expr.Else != null)
                WriteExpr(expr.Else, scope);
            else
                WriteTypedNull(InferType(expr, scope));
            _w.Write(")");
        }

        void WriteSingularOrList(SingularOrListExpr expr, IReadOnlyList<KqlColumn> scope, bool predicate)
        {
            if (expr.Options.Count == 0)
            {
                _w.Write("false");
                return;
            }

            // Kusto's 'in' is false, not null, when the value or a list entry is null. That only
            // matters outside a predicate; there the list is expanded into null-propagating equalities.
            if (!predicate && (MayBeNull(expr.Value, scope) || expr.Options.Any(o => MayBeNull(o, scope))))
            {
                _w.Write("(");
                for (int i = 0; i < expr.Options.Count; i++)
                {
                    if (i > 0) _w.Write(" or ");
                    WriteNullPropagatingEqual(expr.Value, expr.Options[i], scope);
                }
                _w.Write(")");
                return;
            }

            _w.Write("(");
            WriteExpr(expr.Value, scope);
            _w.Write(" in (");
            for (int i = 0; i < expr.Options.Count; i++)
            {
                if (i > 0) _w.Write(", ");
                WriteExpr(expr.Options[i], scope);
            }
            _w.Write("))");
        }

        void WriteMultiOrList(MultiOrListExpr expr, IReadOnlyList<KqlColumn> scope, bool predicate)
        {
            if (expr.Options.Count == 0)
            {
                _w.Write("false");
                return;
            }
            _w.Write("(");
            for (int r = 0; r < expr.Options.Count; r++)
            {
                if (r > 0) _w.Write(" or ");
                _w.Write("(");
                for (int v = 0; v < expr.Values.Count; v++)
                {
                    if (v > 0) _w.Write(" and ");
                    if (predicate)
                    {
                        _w.Write("(");
                        WriteExpr(expr.Values[v], scope);
                        _w.Write(" == ");
                        WriteExpr(expr.Options[r][v], scope);
                        _w.Write(")");
                    }
                    else
                    {
                        WriteNullPropagatingEqual(expr.Values[v], expr.Options[r][v], scope);
                    }
                }
                _w.Write(")");
            }
            _w.Write(")");
        }

        /// <summary>Writes a == b, null when either side is null (Kusto would give false).</summary>
        void WriteNullPropagatingEqual(Expr a, Expr b, IReadOnlyList<KqlColumn> scope)
        {
            var nullable = new[] { a, b }.Where(e => MayBeNull(e, scope)).ToList();
            if (nullable.Count > 0)
            {
                _w.Write("iif(");
                for (int i = 0; i < nullable.Count; i++)
                {
                    if (i > 0) _w.Write(" or ");
                    _w.Write("isnull(");
                    WriteExpr(nullable[i], scope);
                    _w.Write(")");
                }
                _w.Write(", bool(null), ");
            }
            _w.Write("(");
            WriteExpr(a, scope);
            _w.Write(" == ");
            WriteExpr(b, scope);
            _w.Write(")");
            if (nullable.Count > 0)
                _w.Write(")");
        }

        void WriteCast(CastExpr expr, IReadOnlyList<KqlColumn> scope)
        {
            // KQL's conversion functions return null when the input can't be converted, so a cast
            // that must throw can only be honored when it cannot fail.
            var source = InferType(expr.Input, scope);
            if (expr.FailureBehavior == F.FailureBehavior_ThrowException && CastCanFail(source, expr.Type))
                throw SubstraitTranslationException.Unsupported("Casts that throw on failure are not supported; KQL conversions return null.");
            RejectLossyCast(source, expr.Type);

            string? open = expr.Type.Kind switch
            {
                TypeKind.Bool => "tobool(",
                TypeKind.I32 => "toint(",
                TypeKind.I64 => "tolong(",
                TypeKind.Fp64 => "toreal(",
                TypeKind.String => "tostring(",
                TypeKind.Decimal => "todecimal(",
                TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz => "todatetime(",
                TypeKind.Date => "startofday(todatetime(",
                TypeKind.IntervalDay => "totimespan(",
                TypeKind.Uuid => "toguid(",
                _ => null,
            };
            if (open == null)
                throw SubstraitTranslationException.Unsupported($"Cast to type '{expr.Type}' is not supported.");

            // toint()/tolong() wrap or saturate out-of-range numbers instead of failing (observed:
            // toint(3000000000) = -1294967296, tolong(1e30) = long.MinValue), so the range is checked first.
            string? range = (expr.Type.Kind, source.Kind) switch
            {
                (TypeKind.I32, TypeKind.I64 or TypeKind.Fp64 or TypeKind.Decimal) => "-2147483648.0 .. 2147483647.0",
                (TypeKind.I64, TypeKind.Fp64 or TypeKind.Decimal) => "-9223372036854775808.0 .. 9223372036854774784.0",
                _ => null,
            };
            if (range != null)
            {
                int dots = range.IndexOf(" .. ", StringComparison.Ordinal);
                _w.Write("iif(");
                WriteExpr(expr.Input, scope);
                _w.Write(" >= ");
                _w.Write(range.Substring(0, dots));
                _w.Write(" and ");
                WriteExpr(expr.Input, scope);
                _w.Write(" <= ");
                _w.Write(range.Substring(dots + 4));
                _w.Write(", ");
            }
            _w.Write(open);
            WriteExpr(expr.Input, scope);
            _w.Write(expr.Type.Kind == TypeKind.Date ? "))" : ")");
            if (range != null)
            {
                _w.Write(", ");
                WriteTypedNull(expr.Type);
                _w.Write(")");
            }
        }

        /// <summary>
        /// Rejects casts whose target parameters KQL's conversion functions don't apply: decimal
        /// precision/scale (todecimal neither rounds nor checks overflow), reduced timestamp precision,
        /// char/varchar lengths, and temporal-to-string casts (KQL formats them differently from SQL).
        /// </summary>
        static void RejectLossyCast(SType source, SType target)
        {
            switch (target.Kind)
            {
                case TypeKind.Decimal:
                {
                    int integerDigits = target.Precision - target.Scale;
                    bool exact = source.Kind switch
                    {
                        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 => integerDigits >= 10,
                        TypeKind.I64 => integerDigits >= 19,
                        TypeKind.Decimal => target.Scale >= source.Scale && integerDigits >= source.Precision - source.Scale,
                        _ => false,
                    };
                    if (!exact || target.Precision > KqlFunctionCatalog.MaxDecimalPrecision)
                        throw SubstraitTranslationException.Unsupported(
                            $"Cast from '{source}' to decimal({target.Precision},{target.Scale}) is not supported; KQL's todecimal() does not round to a scale or check precision.");
                    break;
                }
                case TypeKind.PrecisionTimestamp:
                case TypeKind.PrecisionTimestampTz:
                    if (target.Precision < 7 && !(source.Kind == TypeKind.Date || (source.IsTimestamp && source.Precision <= target.Precision)))
                        throw SubstraitTranslationException.Unsupported(
                            $"Cast from '{source}' to a timestamp with precision {target.Precision} is not supported; Kusto datetimes keep 100ns precision.");
                    break;
                case TypeKind.VarChar:
                case TypeKind.FixedChar:
                    throw SubstraitTranslationException.Unsupported("Casts to varchar or fixedchar are not supported; Kusto strings have no length.");
                case TypeKind.String when source.IsTimestamp || source.Kind is TypeKind.IntervalDay or TypeKind.Unknown:
                    throw SubstraitTranslationException.Unsupported(
                        $"Cast from '{source}' to string is not supported; KQL formats dates, times and intervals differently.");
            }
        }

        /// <summary>True unless converting <paramref name="source"/> to <paramref name="target"/> always succeeds.</summary>
        static bool CastCanFail(SType source, SType target)
        {
            if (!source.IsKnown)
                return true;
            if (source.Kind == target.Kind && target.Kind != TypeKind.Decimal)
                return false;
            bool smallInt = source.Kind is TypeKind.Bool or TypeKind.I8 or TypeKind.I16 or TypeKind.I32;
            return target.Kind switch
            {
                TypeKind.String or TypeKind.VarChar => false,
                TypeKind.I32 => !smallInt,
                TypeKind.I64 or TypeKind.Fp64 => !(smallInt || source.Kind == TypeKind.I64),
                TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz => !source.IsTimestamp,
                _ => true,
            };
        }

        void WriteContextVariable(ContextVariableExpr expr)
        {
            // now() is evaluated once per query, which matches only per-plan evaluation.
            if (_perRecordEvaluation && expr.Variable != F.ContextVariable_CurrentTimezone)
                throw SubstraitTranslationException.Unsupported("Per-record evaluation of execution context variables is not supported; KQL evaluates now() once per query.");
            _w.Write(expr.Variable switch
            {
                F.ContextVariable_CurrentTimestamp => "now()",
                F.ContextVariable_CurrentDate => "startofday(now())",
                _ => "'UTC'",
            });
        }

        /// <summary>Writes a null of the KQL type corresponding to <paramref name="type"/>.</summary>
        internal void WriteTypedNull(SType type)
        {
            _w.Write(type.Kind switch
            {
                TypeKind.Bool => "bool(null)",
                TypeKind.I8 or TypeKind.I16 or TypeKind.I32 => "int(null)",
                TypeKind.I64 => "long(null)",
                TypeKind.Fp32 or TypeKind.Fp64 => "real(null)",
                TypeKind.Decimal => "decimal(null)",
                TypeKind.Date or TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz => "datetime(null)",
                TypeKind.IntervalDay => "timespan(null)",
                TypeKind.Uuid => "guid(null)",
                // Kusto strings cannot be null; the empty string stands in for a missing value.
                TypeKind.String or TypeKind.VarChar or TypeKind.FixedChar => "''",
                TypeKind.Unknown => "dynamic(null)",
                _ => throw SubstraitTranslationException.Unsupported($"A null of type '{type}' has no KQL equivalent."),
            });
        }

        internal void WriteLiteral(LiteralExpr lit)
        {
            if (lit.IsNull)
            {
                WriteTypedNull(lit.Type);
                return;
            }

            switch (lit.Type.Kind)
            {
                case TypeKind.Bool:
                    _w.Write(lit.BoolValue ? "true" : "false");
                    break;
                case TypeKind.I32:
                    // A bare integer literal is a long in KQL.
                    _w.Write("int(");
                    _w.WriteInt64(lit.IntValue);
                    _w.Write(")");
                    break;
                case TypeKind.I64:
                    if (lit.IntValue == long.MinValue)
                        _w.Write("(-9223372036854775807 - 1)");
                    else
                        _w.WriteInt64(lit.IntValue);
                    break;
                case TypeKind.Fp64:
                    WriteReal(lit.FloatValue);
                    break;
                case TypeKind.String:
                case TypeKind.VarChar:
                case TypeKind.FixedChar:
                    _w.WriteStringLiteral(lit.StringValue ?? "");
                    break;
                case TypeKind.Date:
                    WriteDateTime(lit.IntValue * TimeSpan.TicksPerDay);
                    break;
                case TypeKind.PrecisionTimestamp:
                case TypeKind.PrecisionTimestampTz:
                    WriteDateTime(ToTicks(lit.IntValue, lit.Type.Precision, "timestamp"));
                    break;
                case TypeKind.IntervalDay:
                    WriteTimespan(lit);
                    break;
                case TypeKind.Decimal:
                    WriteDecimal(lit);
                    break;
                case TypeKind.Uuid:
                    WriteGuid(lit.BytesValue!);
                    break;
                default:
                    // i8, i16 and fp32 have no KQL type; neither do intervals in months, binary, etc.
                    throw SubstraitTranslationException.Unsupported($"Literals of type '{lit.Type}' are not supported.");
            }
        }

        void WriteReal(double value)
        {
            if (double.IsNaN(value)) { _w.Write("real(nan)"); return; }
            if (double.IsPositiveInfinity(value)) { _w.Write("real(+inf)"); return; }
            if (double.IsNegativeInfinity(value)) { _w.Write("real(-inf)"); return; }
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0)
                text += ".0";
            _w.Write("real(");
            _w.Write(text);
            _w.Write(")");
        }

        /// <summary>Converts a value in units of 10^-precision seconds to 100ns ticks.</summary>
        static long ToTicks(long value, int precision, string what)
        {
            if (precision < 0 || precision > 12)
                throw SubstraitTranslationException.MalformedPlan($"Invalid {what} precision {precision}.");
            if (precision <= 7)
            {
                long factor = Pow10(7 - precision);
                if (value > long.MaxValue / factor || value < long.MinValue / factor)
                    throw SubstraitTranslationException.Unsupported($"The {what} literal is outside the range Kusto can represent.");
                return value * factor;
            }
            long divisor = Pow10(precision - 7);
            if (value % divisor != 0)
                throw SubstraitTranslationException.Unsupported($"The {what} literal has sub-100ns precision, which Kusto cannot represent.");
            return value / divisor;
        }

        static long Pow10(int exponent)
        {
            long result = 1;
            for (int i = 0; i < exponent; i++) result *= 10;
            return result;
        }

        static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        void WriteDateTime(long ticksSinceEpoch)
        {
            long ticks = UnixEpochTicks + ticksSinceEpoch;
            if (ticksSinceEpoch < DateTime.MinValue.Ticks - UnixEpochTicks || ticksSinceEpoch > DateTime.MaxValue.Ticks - UnixEpochTicks)
                throw SubstraitTranslationException.Unsupported("The timestamp literal is outside the range Kusto can represent (years 1 to 9999).");
            var dt = new DateTime(ticks, DateTimeKind.Utc);
            _w.Write("datetime(");
            _w.Write(dt.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff", CultureInfo.InvariantCulture));
            _w.Write("Z)");
        }

        void WriteTimespan(LiteralExpr lit)
        {
            long ticks;
            try
            {
                ticks = checked(lit.Days * TimeSpan.TicksPerDay + lit.Seconds * TimeSpan.TicksPerSecond
                    + ToTicks(lit.Subseconds, lit.SubsecondPrecision, "interval"));
            }
            catch (OverflowException)
            {
                throw SubstraitTranslationException.Unsupported("The interval literal is outside the range Kusto can represent.");
            }
            _w.Write("(");
            _w.WriteInt64(ticks);
            _w.Write("tick)");
        }

        void WriteDecimal(LiteralExpr lit)
        {
            // Kusto decimals are 128-bit values like .NET's System.Decimal (28 significant digits).
            var unscaled = new BigInteger(lit.BytesValue!);
            int scale = lit.Type.Scale;
            string digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
            if (digits.TrimStart('0').Length > KqlFunctionCatalog.MaxDecimalPrecision)
                throw SubstraitTranslationException.Unsupported($"Decimal literals with more than {KqlFunctionCatalog.MaxDecimalPrecision} significant digits are not supported.");
            if (scale < 0)
                throw SubstraitTranslationException.MalformedPlan("Decimal scale must not be negative.");
            if (scale > KqlFunctionCatalog.MaxDecimalPrecision)
                throw SubstraitTranslationException.Unsupported($"Decimal literals with a scale above {KqlFunctionCatalog.MaxDecimalPrecision} are not supported.");
            if (digits.Length <= scale)
                digits = new string('0', scale - digits.Length + 1) + digits;
            string text = scale == 0 ? digits : digits.Substring(0, digits.Length - scale) + "." + digits.Substring(digits.Length - scale);
            _w.Write("decimal(");
            if (unscaled.Sign < 0) _w.Write("-");
            _w.Write(text);
            _w.Write(")");
        }

        void WriteGuid(byte[] bytes)
        {
            // Substrait UUIDs are the 16 bytes in RFC 4122 (big-endian) order.
            var hex = new System.Text.StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i is 4 or 6 or 8 or 10) hex.Append('-');
                hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            _w.Write("guid(");
            _w.Write(hex.ToString());
            _w.Write(")");
        }

        #endregion

        #region Aggregates

        SType WriteAggregate(Measure measure, IReadOnlyList<KqlColumn> scope)
            => KqlFunctionCatalog.WriteAggregate(this, measure, scope);

        #endregion
    }
}
