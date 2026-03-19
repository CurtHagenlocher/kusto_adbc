using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Translates a Substrait binary plan into a KQL query string.
    /// Phase 1: Uses Google.Protobuf for deserialization.
    /// Phase 2 (future): Custom wire-format visitor for zero-allocation parsing.
    /// </summary>
    public static class SubstraitToKqlTranslator
    {
        /// <summary>
        /// Translates a Substrait binary plan to a KQL query string.
        /// </summary>
        /// <param name="planBytes">Serialized Substrait Plan protobuf.</param>
        /// <returns>A KQL query string.</returns>
        public static string Translate(byte[] planBytes)
        {
            if (planBytes == null || planBytes.Length == 0)
                throw new ArgumentException("Substrait plan is empty.", nameof(planBytes));

            // Parse the plan using the Substrait protobuf schema.
            // The Substrait plan is a tree of relations that we walk depth-first
            // and translate to KQL's pipe-based syntax.
            var reader = new SubstraitPlanReader(planBytes);
            return reader.Translate();
        }
    }

    /// <summary>
    /// Reads a Substrait plan from its protobuf wire format and translates to KQL.
    ///
    /// Substrait plan structure (simplified):
    ///   Plan { relations: [PlanRel { root: RelRoot { input: Rel } }] }
    ///
    /// Rel is a oneof with variants:
    ///   read, filter, project, aggregate, sort, join, fetch, set, ...
    ///
    /// We walk the tree bottom-up (the deepest ReadRel is the table source)
    /// and emit KQL operators top-down (left to right in pipe syntax).
    /// </summary>
    sealed class SubstraitPlanReader
    {
        // Protobuf wire type constants
        const int WireTypeVarint = 0;
        const int WireTypeLengthDelimited = 2;

        readonly byte[] _data;
        readonly string[] _extensionFunctions;

        public SubstraitPlanReader(byte[] data)
        {
            _data = data;
            _extensionFunctions = Array.Empty<string>();
        }

        public string Translate()
        {
            // Parse the top-level Plan message
            var span = _data.AsSpan();
            int pos = 0;

            string[] functions = Array.Empty<string>();
            string? result = null;

            while (pos < span.Length)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // extensions (SimpleExtensionDeclaration)
                    {
                        int len = ReadVarint32(span, ref pos);
                        // Parse extension declarations for function name resolution
                        // (Needed to map function references to their names)
                        pos += len; // Skip for now; function resolution is Phase 2
                        break;
                    }
                    case 3: // relations (PlanRel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int end = pos + len;
                        result = ReadPlanRel(span, ref pos, end);
                        pos = end;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            return result ?? throw new InvalidOperationException("Substrait plan contains no relations.");
        }

        string ReadPlanRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // rel (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        string kql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return kql;
                    }
                    case 2: // root (RelRoot)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int rootEnd = pos + len;
                        string kql = ReadRelRoot(span, ref pos, rootEnd);
                        pos = rootEnd;
                        return kql;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw new InvalidOperationException("PlanRel has no relation.");
        }

        string ReadRelRoot(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? relKql = null;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        relKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return relKql ?? throw new InvalidOperationException("RelRoot has no input.");
        }

        /// <summary>
        /// Reads a Rel message and dispatches to the appropriate handler.
        /// Rel is a oneof; the field number tells us the variant.
        /// </summary>
        string ReadRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                int len;
                int relEnd;

                switch (fieldNumber)
                {
                    case 1: // read
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var readKql = ReadReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return readKql;

                    case 2: // filter
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var filterKql = ReadFilterRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return filterKql;

                    case 3: // fetch
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var fetchKql = ReadFetchRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return fetchKql;

                    case 4: // aggregate
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var aggKql = ReadAggregateRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return aggKql;

                    case 5: // sort
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var sortKql = ReadSortRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return sortKql;

                    case 6: // join
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var joinKql = ReadJoinRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return joinKql;

                    case 7: // project
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var projectKql = ReadProjectRel(span, ref pos, relEnd);
                        pos = relEnd;
                        return projectKql;

                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw new InvalidOperationException("Rel message has no recognized relation type.");
        }

        #region Relation Readers

        string ReadReadRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? tableName = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 7: // named_table (NamedStruct → ReadRel.NamedTable)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int ntEnd = pos + len;
                        tableName = ReadNamedTable(span, ref pos, ntEnd);
                        pos = ntEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            return tableName ?? throw new InvalidOperationException("ReadRel has no named table.");
        }

        string ReadNamedTable(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? name = null;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // names (repeated string)
                    {
                        int len = ReadVarint32(span, ref pos);
                        name = Encoding.UTF8.GetString(span.Slice(pos, len)
#if NETSTANDARD2_0
                            .ToArray()
#endif
                        );
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return name ?? throw new InvalidOperationException("NamedTable has no name.");
        }

        string ReadFilterRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? inputKql = null;
            string? conditionKql = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        inputKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // condition (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        conditionKql = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputKql == null) throw new InvalidOperationException("FilterRel missing input.");
            if (conditionKql == null) throw new InvalidOperationException("FilterRel missing condition.");

            return $"{inputKql}\n| where {conditionKql}";
        }

        string ReadProjectRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? inputKql = null;
            var expressions = new System.Collections.Generic.List<string>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        inputKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // expressions (repeated Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        expressions.Add(ReadExpression(span, ref pos, exprEnd));
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputKql == null) throw new InvalidOperationException("ProjectRel missing input.");
            if (expressions.Count == 0) return inputKql;

            return $"{inputKql}\n| project {string.Join(", ", expressions)}";
        }

        string ReadFetchRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? inputKql = null;
            long offset = 0;
            long count = -1;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        inputKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // offset (int64)
                        offset = ReadVarint64(span, ref pos);
                        break;
                    case 4: // count (int64)
                        count = ReadVarint64(span, ref pos);
                        break;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputKql == null) throw new InvalidOperationException("FetchRel missing input.");

            var sb = new StringBuilder(inputKql);
            if (offset > 0)
                sb.Append($"\n| serialize\n| where row_number() > {offset}");
            if (count >= 0)
                sb.Append($"\n| take {count}");

            return sb.ToString();
        }

        string ReadSortRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? inputKql = null;
            var sortFields = new System.Collections.Generic.List<string>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        inputKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // sorts (repeated SortField)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int sfEnd = pos + len;
                        sortFields.Add(ReadSortField(span, ref pos, sfEnd));
                        pos = sfEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputKql == null) throw new InvalidOperationException("SortRel missing input.");
            if (sortFields.Count == 0) return inputKql;

            return $"{inputKql}\n| sort by {string.Join(", ", sortFields)}";
        }

        string ReadSortField(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? expr = null;
            int direction = 0; // 0=unspecified, 1=asc_nulls_first, 2=asc_nulls_last, 3=desc_nulls_first, 4=desc_nulls_last, 5=clustered

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // expr (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        expr = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    case 2: // direction (enum SortDirection)
                        direction = ReadVarint32(span, ref pos);
                        break;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (expr == null) throw new InvalidOperationException("SortField missing expression.");

            string dir = direction switch
            {
                1 or 2 => " asc",
                3 or 4 => " desc",
                _ => ""
            };

            return $"{expr}{dir}";
        }

        string ReadAggregateRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? inputKql = null;
            var groupings = new System.Collections.Generic.List<string>();
            var measures = new System.Collections.Generic.List<string>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        inputKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // groupings (repeated Grouping)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int gEnd = pos + len;
                        ReadGrouping(span, ref pos, gEnd, groupings);
                        pos = gEnd;
                        break;
                    }
                    case 4: // measures (repeated Measure)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int mEnd = pos + len;
                        measures.Add(ReadMeasure(span, ref pos, mEnd));
                        pos = mEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputKql == null) throw new InvalidOperationException("AggregateRel missing input.");

            var sb = new StringBuilder(inputKql);
            sb.Append("\n| summarize ");
            sb.Append(string.Join(", ", measures));
            if (groupings.Count > 0)
            {
                sb.Append(" by ");
                sb.Append(string.Join(", ", groupings));
            }

            return sb.ToString();
        }

        void ReadGrouping(ReadOnlySpan<byte> span, ref int pos, int end, System.Collections.Generic.List<string> groupings)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // grouping_expressions (repeated Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        groupings.Add(ReadExpression(span, ref pos, exprEnd));
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
        }

        string ReadMeasure(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? measureExpr = null;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // measure (AggregateFunction wrapped in Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        measureExpr = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return measureExpr ?? "count()";
        }

        string ReadJoinRel(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? leftKql = null;
            string? rightKql = null;
            string? condition = null;
            int joinType = 0; // 0=unspecified, 1=inner, 2=outer, 3=left, 4=right, ...

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // left (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        leftKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 3: // right (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        rightKql = ReadRel(span, ref pos, relEnd);
                        pos = relEnd;
                        break;
                    }
                    case 4: // expression (Expression - join condition)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        condition = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    case 5: // type (JoinType enum)
                        joinType = ReadVarint32(span, ref pos);
                        break;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (leftKql == null || rightKql == null)
                throw new InvalidOperationException("JoinRel missing left or right input.");

            string kind = joinType switch
            {
                1 => "inner",
                2 => "fullouter",
                3 => "leftouter",
                4 => "rightouter",
                5 => "leftsemi",
                6 => "leftanti",
                _ => "inner"
            };

            var sb = new StringBuilder(leftKql);
            sb.Append($"\n| join kind={kind} ({rightKql})");
            if (condition != null)
                sb.Append($" on {condition}");

            return sb.ToString();
        }

        #endregion

        #region Expression Reader

        /// <summary>
        /// Reads a Substrait Expression and returns a KQL expression string.
        /// Expression is a oneof with many variants.
        /// </summary>
        string ReadExpression(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                int len;
                int exprEnd;

                switch (fieldNumber)
                {
                    case 1: // literal (Expression.Literal)
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        var lit = ReadLiteral(span, ref pos, exprEnd);
                        pos = exprEnd;
                        return lit;

                    case 2: // selection (FieldReference)
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        var field = ReadFieldReference(span, ref pos, exprEnd);
                        pos = exprEnd;
                        return field;

                    case 3: // scalar_function (ScalarFunction)
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        var func = ReadScalarFunction(span, ref pos, exprEnd);
                        pos = exprEnd;
                        return func;

                    case 5: // if_then (IfThen)
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        var ifThen = ReadIfThen(span, ref pos, exprEnd);
                        pos = exprEnd;
                        return ifThen;

                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return "/* unknown expression */";
        }

        string ReadLiteral(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // boolean
                        return ReadVarint32(span, ref pos) != 0 ? "true" : "false";
                    case 2: // i8
                    case 3: // i16
                    case 5: // i32
                        return ReadVarint32(span, ref pos).ToString();
                    case 7: // i64
                        return ReadVarint64(span, ref pos).ToString();
                    case 10: // fp32
                    {
                        int bits = ReadFixed32(span, ref pos);
#if NETSTANDARD2_0
                        unsafe { float f = *(float*)&bits; return f.ToString("G"); }
#else
                        return BitConverter.Int32BitsToSingle(bits).ToString("G");
#endif
                    }
                    case 11: // fp64
                    {
                        long bits = ReadFixed64(span, ref pos);
                        return BitConverter.Int64BitsToDouble(bits).ToString("G");
                    }
                    case 12: // string
                    {
                        int len = ReadVarint32(span, ref pos);
                        string s = Encoding.UTF8.GetString(span.Slice(pos, len)
#if NETSTANDARD2_0
                            .ToArray()
#endif
                        );
                        pos += len;
                        return $"'{EscapeKqlString(s)}'";
                    }
                    case 26: // null (Type)
                    {
                        int len = ReadVarint32(span, ref pos);
                        pos += len;
                        return "dynamic(null)";
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return "dynamic(null)";
        }

        string ReadFieldReference(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            // FieldReference contains a ReferenceSegment chain.
            // The simplest case is a direct reference to a field by index.
            // We return the field index as $N — the caller maps to column names.
            int fieldIndex = -1;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // direct_reference (ReferenceSegment)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int refEnd = pos + len;
                        fieldIndex = ReadReferenceSegment(span, ref pos, refEnd);
                        pos = refEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (fieldIndex >= 0)
                return $"$field{fieldIndex}";

            return "/* unknown field */";
        }

        int ReadReferenceSegment(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // map_key — skip
                    case 2: // list_element — skip
                    {
                        int len = ReadVarint32(span, ref pos);
                        pos += len;
                        break;
                    }
                    case 3: // struct_field
                    {
                        int len = ReadVarint32(span, ref pos);
                        int sfEnd = pos + len;
                        int index = ReadStructField(span, ref pos, sfEnd);
                        pos = sfEnd;
                        return index;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return -1;
        }

        int ReadStructField(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            int fieldIndex = 0;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // field (int32)
                        fieldIndex = ReadVarint32(span, ref pos);
                        break;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return fieldIndex;
        }

        string ReadScalarFunction(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            int functionRef = 0;
            var args = new System.Collections.Generic.List<string>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // function_reference (uint32)
                        functionRef = ReadVarint32(span, ref pos);
                        break;
                    case 4: // arguments (repeated FunctionArgument)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int argEnd = pos + len;
                        args.Add(ReadFunctionArgument(span, ref pos, argEnd));
                        pos = argEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            // Map well-known function references to KQL.
            // In a full implementation, we'd resolve functionRef via the extension declarations.
            // For now, emit a generic function call.
            string funcName = MapFunctionRef(functionRef);
            return $"{funcName}({string.Join(", ", args)})";
        }

        string ReadFunctionArgument(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // value (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        var expr = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        return expr;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return "/* unknown arg */";
        }

        string ReadIfThen(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            // Translate to KQL iif() for simple cases
            var conditions = new System.Collections.Generic.List<(string cond, string then)>();
            string? elseExpr = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // ifs (repeated IfClause)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int clauseEnd = pos + len;
                        var clause = ReadIfClause(span, ref pos, clauseEnd);
                        conditions.Add(clause);
                        pos = clauseEnd;
                        break;
                    }
                    case 2: // else (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        elseExpr = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (conditions.Count == 1)
                return $"iif({conditions[0].cond}, {conditions[0].then}, {elseExpr ?? "dynamic(null)"})";

            // For multiple conditions, use nested case/iif
            var sb = new StringBuilder();
            for (int i = 0; i < conditions.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"iif({conditions[i].cond}, {conditions[i].then}");
            }
            sb.Append($", {elseExpr ?? "dynamic(null)"}");
            for (int i = 0; i < conditions.Count; i++) sb.Append(')');
            return sb.ToString();
        }

        (string cond, string then) ReadIfClause(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            string? cond = null;
            string? then = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // if (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        cond = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    case 2: // then (Expression)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        then = ReadExpression(span, ref pos, exprEnd);
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            return (cond ?? "true", then ?? "dynamic(null)");
        }

        #endregion

        #region Function Mapping

        static string MapFunctionRef(int functionRef)
        {
            // Default mapping — in a full implementation this would be resolved
            // from the plan's extension declarations.
            return $"func_{functionRef}";
        }

        #endregion

        #region Protobuf Wire Format Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadTag(ReadOnlySpan<byte> span, ref int pos) => ReadVarint32(span, ref pos);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadVarint32(ReadOnlySpan<byte> span, ref int pos)
        {
            int result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = span[pos++];
                result |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long ReadVarint64(ReadOnlySpan<byte> span, ref int pos)
        {
            long result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = span[pos++];
                result |= (long)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadFixed32(ReadOnlySpan<byte> span, ref int pos)
        {
            int val = span[pos] | (span[pos + 1] << 8) | (span[pos + 2] << 16) | (span[pos + 3] << 24);
            pos += 4;
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long ReadFixed64(ReadOnlySpan<byte> span, ref int pos)
        {
            long lo = (uint)(span[pos] | (span[pos + 1] << 8) | (span[pos + 2] << 16) | (span[pos + 3] << 24));
            long hi = (uint)(span[pos + 4] | (span[pos + 5] << 8) | (span[pos + 6] << 16) | (span[pos + 7] << 24));
            pos += 8;
            return lo | (hi << 32);
        }

        static void SkipField(ReadOnlySpan<byte> span, int wireType, ref int pos)
        {
            switch (wireType)
            {
                case 0: // Varint
                    while ((span[pos++] & 0x80) != 0) { }
                    break;
                case 1: // 64-bit
                    pos += 8;
                    break;
                case 2: // Length-delimited
                    int len = ReadVarint32(span, ref pos);
                    pos += len;
                    break;
                case 5: // 32-bit
                    pos += 4;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown wire type: {wireType}");
            }
        }

        static string EscapeKqlString(string s) => s.Replace("'", "\\'");

        #endregion
    }
}
