// Copyright (c) Microsoft Corporation.  All rights reserved.

using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Translates a Substrait binary plan into KQL.
    /// Output is UTF-8 bytes (ready for HTTP) via <see cref="Utf8KqlWriter"/>.
    /// </summary>
    public static class SubstraitToKqlTranslator
    {
        /// <summary>
        /// Translates a Substrait binary plan to a KQL query string.
        /// </summary>
        public static string Translate(byte[] planBytes)
        {
            return TranslateToUtf8(planBytes).ToString();
        }

        /// <summary>
        /// Translates a Substrait binary plan to UTF-8 encoded KQL bytes.
        /// The returned writer's <see cref="Utf8KqlWriter.WrittenMemory"/> is
        /// ready for direct use as an HTTP request body.
        /// </summary>
        public static Utf8KqlWriter TranslateToUtf8(byte[] planBytes)
        {
            if (planBytes == null || planBytes.Length == 0)
                throw new ArgumentException("Substrait plan is empty.", nameof(planBytes));

            var writer = new Utf8KqlWriter();
            var reader = new SubstraitPlanReader(planBytes);
            reader.WriteTo(writer);
            return writer;
        }
    }

    /// <summary>
    /// Reads a Substrait plan from its protobuf wire format and writes KQL
    /// directly to a <see cref="Utf8KqlWriter"/>.
    /// All KQL output is UTF-8 with zero intermediate string allocations.
    /// </summary>
    /// <summary>
    /// A zero-allocation reference to a UTF-8 string within the protobuf input buffer.
    /// Avoids the UTF-8 → .NET string → UTF-8 round-trip for column names.
    /// </summary>
    readonly struct Utf8Span
    {
        public readonly int Offset;
        public readonly int Length;

        public Utf8Span(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        /// <summary>Extracts the UTF-8 bytes as a ReadOnlySpan from the source buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> AsSpan(byte[] source) => source.AsSpan(Offset, Length);

        /// <summary>Converts to a .NET string (for debugging/tests only).</summary>
        public string ToString(byte[] source)
        {
#if NETSTANDARD2_0
            return Encoding.UTF8.GetString(source, Offset, Length);
#else
            return Encoding.UTF8.GetString(source.AsSpan(Offset, Length));
#endif
        }
    }

    sealed class SubstraitPlanReader
    {
        readonly byte[] _data;
        // Populated during Plan-level parsing: function_anchor → function_name
        readonly Dictionary<int, string> _functionAnchors = new();
        // extension_urn_anchor → uri (currently informational)
        readonly Dictionary<int, string> _extensionUris = new();

        public SubstraitPlanReader(byte[] data)
        {
            _data = data;
        }

        public void WriteTo(Utf8KqlWriter w)
        {
            var span = _data.AsSpan();
            int pos = 0;
            bool found = false;

            // Two-pass: first collect all extension URNs and declarations,
            // then process relations. Since protobuf fields may appear in any
            // order, we record positions of relations and process after extensions.
            var relationPositions = new List<(int start, int len)>();

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
                        int end = pos + len;
                        ParseExtensionDeclaration(span, ref pos, end);
                        pos = end;
                        break;
                    }
                    case 3: // relations (PlanRel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        relationPositions.Add((pos, len));
                        pos += len;
                        found = true;
                        break;
                    }
                    case 8: // extension_urns (SimpleExtensionURN) — in some plan versions
                    {
                        int len = ReadVarint32(span, ref pos);
                        int end = pos + len;
                        ParseExtensionUri(span, ref pos, end);
                        pos = end;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (!found) throw SubstraitTranslationException.MalformedPlan("Plan contains no relations.");

            // Now process the first relation with extension context available
            int p = relationPositions[0].start;
            WritePlanRel(span, ref p, relationPositions[0].start + relationPositions[0].len, w);
        }

        void ParseExtensionUri(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            int anchor = 0;
            string? uri = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // extension_urn_anchor
                        anchor = ReadVarint32(span, ref pos);
                        break;
                    case 2: // uri
                    {
                        int len = ReadVarint32(span, ref pos);
#if NETSTANDARD2_0
                        uri = System.Text.Encoding.UTF8.GetString(span.Slice(pos, len).ToArray());
#else
                        uri = System.Text.Encoding.UTF8.GetString(span.Slice(pos, len));
#endif
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (uri != null)
                _extensionUris[anchor] = uri;
        }

        void ParseExtensionDeclaration(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            // SimpleExtensionDeclaration is a oneof: extension_type(1), extension_type_variation(2), extension_function(3)
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 3: // extension_function
                    {
                        int len = ReadVarint32(span, ref pos);
                        int fEnd = pos + len;
                        ParseExtensionFunction(span, ref pos, fEnd);
                        pos = fEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
        }

        void ParseExtensionFunction(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            int anchor = 0;
            string? name = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // function_anchor
                        anchor = ReadVarint32(span, ref pos);
                        break;
                    case 3: // name (function signature, e.g., "add:i32_i32")
                    {
                        int len = ReadVarint32(span, ref pos);
#if NETSTANDARD2_0
                        name = System.Text.Encoding.UTF8.GetString(span.Slice(pos, len).ToArray());
#else
                        name = System.Text.Encoding.UTF8.GetString(span.Slice(pos, len));
#endif
                        pos += len;
                        break;
                    }
                    case 4: // extension_urn_reference
                        ReadVarint32(span, ref pos); // Consumed but not needed for name resolution
                        break;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (name != null)
                _functionAnchors[anchor] = name;
        }

        List<Utf8Span>? WritePlanRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // rel
                    {
                        int len = ReadVarint32(span, ref pos);
                        int relEnd = pos + len;
                        var schema = WriteRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return schema;
                    }
                    case 2: // root (RelRoot)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int rootEnd = pos + len;
                        var schema = WriteRelRoot(span, ref pos, rootEnd, w);
                        pos = rootEnd;
                        return schema;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.MalformedPlan("PlanRel has no relation.");
        }

        List<Utf8Span>? WriteRelRoot(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
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
                        var schema = WriteRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return schema;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.MalformedPlan("RelRoot has no input.");
        }

        List<Utf8Span>? WriteRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            int lastSeenRelField = 0;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                int len, relEnd;

                switch (fieldNumber)
                {
                    case 1: // read
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var readSchema = WriteReadRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return readSchema;
                    case 2: // filter
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var filterSchema = WriteFilterRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return filterSchema;
                    case 3: // fetch
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var fetchSchema = WriteFetchRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return fetchSchema;
                    case 4: // aggregate
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var aggSchema = WriteAggregateRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return aggSchema;
                    case 5: // sort
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var sortSchema = WriteSortRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return sortSchema;
                    case 6: // join
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var joinSchema = WriteJoinRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return joinSchema;
                    case 7: // project
                        len = ReadVarint32(span, ref pos);
                        relEnd = pos + len;
                        var projSchema = WriteProjectRel(span, ref pos, relEnd, w);
                        pos = relEnd;
                        return projSchema;
                    default:
                        if (fieldNumber > 7 && fieldNumber <= 20)
                            lastSeenRelField = fieldNumber; // likely an unsupported relation type
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.UnsupportedRelation(lastSeenRelField);
        }

        #region Relation Writers

        List<Utf8Span>? WriteReadRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            List<Utf8Span>? schema = null;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // base_schema (NamedStruct)
                    {
                        int len = ReadVarint32(span, ref pos);
                        int nsEnd = pos + len;
                        schema = ParseNamedStructNames(span, ref pos, nsEnd);
                        pos = nsEnd;
                        break;
                    }
                    case 7: // named_table
                    {
                        int len = ReadVarint32(span, ref pos);
                        int ntEnd = pos + len;
                        WriteNamedTable(span, ref pos, ntEnd, w);
                        pos = ntEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            // If we never found a named_table, that's an error — but the table name
            // was already written by WriteNamedTable if encountered.
            // We check after parsing all fields because protobuf field order is not guaranteed.
            return schema;
        }

        List<Utf8Span> ParseNamedStructNames(ReadOnlySpan<byte> span, ref int pos, int end)
        {
            var names = new List<Utf8Span>();
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // names (repeated string) — store as range into _data
                    {
                        int len = ReadVarint32(span, ref pos);
                        names.Add(new Utf8Span(pos, len));
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            return names;
        }

        void WriteNamedTable(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // names (string) — already UTF-8 in the protobuf
                    {
                        int len = ReadVarint32(span, ref pos);
                        w.WriteUtf8(span.Slice(pos, len));
                        pos += len;
                        return;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.MalformedPlan("NamedTable has no name.");
        }

        List<Utf8Span>? WriteFilterRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            // We must parse both input and condition before writing,
            // because protobuf fields can appear in any order.
            // However, in practice, input (field 2) comes before condition (field 3).
            // We use a two-pass approach: save positions, then write in order.

            int inputStart = -1, inputLen = 0;
            int condStart = -1, condLen = 0;

            int saved = pos;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // input
                    {
                        int len = ReadVarint32(span, ref pos);
                        inputStart = pos; inputLen = len;
                        pos += len;
                        break;
                    }
                    case 3: // condition
                    {
                        int len = ReadVarint32(span, ref pos);
                        condStart = pos; condLen = len;
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputStart < 0) throw SubstraitTranslationException.MalformedPlan("FilterRel missing input.");
            if (condStart < 0) throw SubstraitTranslationException.MalformedPlan("FilterRel missing condition.");

            int p = inputStart;
            var schema = WriteRel(span, ref p, inputStart + inputLen, w);
            w.Write(Utf8KqlWriter.PipeWhere);
            p = condStart;
            WriteExpression(span, ref p, condStart + condLen, w, schema);
            return schema;
        }

        List<Utf8Span>? WriteProjectRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            // Collect expression positions, then write in order.
            int inputStart = -1, inputLen = 0;
            var exprPositions = new System.Collections.Generic.List<(int start, int len)>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2:
                    {
                        int len = ReadVarint32(span, ref pos);
                        inputStart = pos; inputLen = len;
                        pos += len;
                        break;
                    }
                    case 3:
                    {
                        int len = ReadVarint32(span, ref pos);
                        exprPositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }

            if (inputStart < 0) throw SubstraitTranslationException.MalformedPlan("ProjectRel missing input.");

            int p = inputStart;
            var schema = WriteRel(span, ref p, inputStart + inputLen, w);

            if (exprPositions.Count > 0)
            {
                w.Write(Utf8KqlWriter.PipeProject);
                for (int i = 0; i < exprPositions.Count; i++)
                {
                    if (i > 0) w.Write(Utf8KqlWriter.Comma);
                    p = exprPositions[i].start;
                    WriteExpression(span, ref p, exprPositions[i].start + exprPositions[i].len, w, schema);
                }
            }

            return null; // ProjectRel output schema depends on expressions; complex to track
        }

        List<Utf8Span>? WriteFetchRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            int inputStart = -1, inputLen = 0;
            long offset = 0, count = -1;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2:
                    {
                        int len = ReadVarint32(span, ref pos);
                        inputStart = pos; inputLen = len;
                        pos += len;
                        break;
                    }
                    case 3: offset = ReadVarint64(span, ref pos); break;
                    case 4: count = ReadVarint64(span, ref pos); break;
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (inputStart < 0) throw SubstraitTranslationException.MalformedPlan("FetchRel missing input.");

            int p = inputStart;
            var schema = WriteRel(span, ref p, inputStart + inputLen, w);

            if (offset > 0)
            {
                w.Write(Utf8KqlWriter.PipeSerialize);
                w.Write(Utf8KqlWriter.WhereRowNumber);
                w.WriteInt64(offset);
            }
            if (count >= 0)
            {
                w.Write(Utf8KqlWriter.PipeTake);
                w.WriteInt64(count);
            }
            return schema;
        }

        List<Utf8Span>? WriteSortRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            int inputStart = -1, inputLen = 0;
            var sortPositions = new System.Collections.Generic.List<(int start, int len)>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2:
                    {
                        int len = ReadVarint32(span, ref pos);
                        inputStart = pos; inputLen = len;
                        pos += len;
                        break;
                    }
                    case 3:
                    {
                        int len = ReadVarint32(span, ref pos);
                        sortPositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (inputStart < 0) throw SubstraitTranslationException.MalformedPlan("SortRel missing input.");

            int p = inputStart;
            var schema = WriteRel(span, ref p, inputStart + inputLen, w);

            if (sortPositions.Count > 0)
            {
                w.Write(Utf8KqlWriter.PipeSortBy);
                for (int i = 0; i < sortPositions.Count; i++)
                {
                    if (i > 0) w.Write(Utf8KqlWriter.Comma);
                    p = sortPositions[i].start;
                    WriteSortField(span, ref p, sortPositions[i].start + sortPositions[i].len, w, schema);
                }
            }
            return schema;
        }

        void WriteSortField(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            int exprStart = -1, exprLen = 0;
            int direction = 0;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1:
                    {
                        int len = ReadVarint32(span, ref pos);
                        exprStart = pos; exprLen = len;
                        pos += len;
                        break;
                    }
                    case 2: direction = ReadVarint32(span, ref pos); break;
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (exprStart < 0) throw SubstraitTranslationException.MalformedPlan("SortField missing expression.");

            int p = exprStart;
            WriteExpression(span, ref p, exprStart + exprLen, w, schema);

            if (direction is 1 or 2) w.Write(Utf8KqlWriter.Asc);
            else if (direction is 3 or 4) w.Write(Utf8KqlWriter.Desc);
        }

        List<Utf8Span>? WriteAggregateRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            int inputStart = -1, inputLen = 0;
            var groupPositions = new System.Collections.Generic.List<(int start, int len)>();
            var measurePositions = new System.Collections.Generic.List<(int start, int len)>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2:
                    {
                        int len = ReadVarint32(span, ref pos);
                        inputStart = pos; inputLen = len;
                        pos += len;
                        break;
                    }
                    case 3:
                    {
                        int len = ReadVarint32(span, ref pos);
                        groupPositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    case 4:
                    {
                        int len = ReadVarint32(span, ref pos);
                        measurePositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (inputStart < 0) throw SubstraitTranslationException.MalformedPlan("AggregateRel missing input.");

            int p = inputStart;
            var schema = WriteRel(span, ref p, inputStart + inputLen, w);

            w.Write(Utf8KqlWriter.PipeSummarize);

            // Measures
            for (int i = 0; i < measurePositions.Count; i++)
            {
                if (i > 0) w.Write(Utf8KqlWriter.Comma);
                p = measurePositions[i].start;
                WriteMeasure(span, ref p, measurePositions[i].start + measurePositions[i].len, w, schema);
            }

            // Groupings
            bool firstGrouping = true;
            for (int gi = 0; gi < groupPositions.Count; gi++)
            {
                p = groupPositions[gi].start;
                int gEnd = groupPositions[gi].start + groupPositions[gi].len;
                WriteGroupingExprs(span, ref p, gEnd, w, ref firstGrouping, schema);
            }

            return null; // AggregateRel output schema depends on groupings + measures
        }

        void WriteGroupingExprs(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, ref bool first, List<Utf8Span>? schema)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // grouping_expressions
                    {
                        int len = ReadVarint32(span, ref pos);
                        if (first) { w.Write(Utf8KqlWriter.SummarizeBy); first = false; }
                        else { w.Write(Utf8KqlWriter.Comma); }
                        int exprEnd = pos + len;
                        WriteExpression(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
        }

        void WriteMeasure(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            bool found = false;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // measure expression
                    {
                        int len = ReadVarint32(span, ref pos);
                        int exprEnd = pos + len;
                        WriteExpression(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        found = true;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            if (!found) w.Write(Utf8KqlWriter.CountFunc);
        }

        List<Utf8Span>? WriteJoinRel(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            int leftStart = -1, leftLen = 0;
            int rightStart = -1, rightLen = 0;
            int condStart = -1, condLen = 0;
            int joinType = 0;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: { int len = ReadVarint32(span, ref pos); leftStart = pos; leftLen = len; pos += len; break; }
                    case 3: { int len = ReadVarint32(span, ref pos); rightStart = pos; rightLen = len; pos += len; break; }
                    case 4: { int len = ReadVarint32(span, ref pos); condStart = pos; condLen = len; pos += len; break; }
                    case 5: joinType = ReadVarint32(span, ref pos); break;
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (leftStart < 0 || rightStart < 0)
                throw SubstraitTranslationException.MalformedPlan("JoinRel missing left or right input.");

            int p = leftStart;
            var leftSchema = WriteRel(span, ref p, leftStart + leftLen, w);

            w.Write(Utf8KqlWriter.PipeJoinKind);
            w.Write(joinType switch
            {
                1 => Utf8KqlWriter.JoinInner,
                2 => Utf8KqlWriter.JoinFullOuter,
                3 => Utf8KqlWriter.JoinLeftOuter,
                4 => Utf8KqlWriter.JoinRightOuter,
                5 => Utf8KqlWriter.JoinLeftSemi,
                6 => Utf8KqlWriter.JoinLeftAnti,
                _ => Utf8KqlWriter.JoinInner,
            });
            w.Write((byte)' ');
            w.Write((byte)'(');
            p = rightStart;
            var rightSchema = WriteRel(span, ref p, rightStart + rightLen, w);
            w.Write((byte)')');

            // Join output schema is left + right columns concatenated
            List<Utf8Span>? joinSchema = null;
            if (leftSchema != null && rightSchema != null)
            {
                joinSchema = new List<Utf8Span>(leftSchema.Count + rightSchema.Count);
                joinSchema.AddRange(leftSchema);
                joinSchema.AddRange(rightSchema);
            }
            else if (leftSchema != null)
            {
                joinSchema = new List<Utf8Span>(leftSchema);
            }
            else if (rightSchema != null)
            {
                joinSchema = new List<Utf8Span>(rightSchema);
            }

            if (condStart >= 0)
            {
                w.Write(Utf8KqlWriter.JoinOn);
                p = condStart;
                WriteExpression(span, ref p, condStart + condLen, w, joinSchema);
            }

            return joinSchema;
        }

        #endregion

        #region Expression Writers

        void WriteExpression(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema = null)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                int len, exprEnd;

                switch (fieldNumber)
                {
                    case 1: // literal
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        WriteLiteral(span, ref pos, exprEnd, w);
                        pos = exprEnd;
                        return;
                    case 2: // selection (FieldReference)
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        WriteFieldReference(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        return;
                    case 3: // scalar_function
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        WriteScalarFunction(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        return;
                    case 5: // if_then
                        len = ReadVarint32(span, ref pos);
                        exprEnd = pos + len;
                        WriteIfThen(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        return;
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.UnsupportedExpression("Expression contains no recognized variant (literal, field reference, scalar function, or if-then).");
        }

        void WriteLiteral(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // boolean
                        w.Write(ReadVarint32(span, ref pos) != 0 ? Utf8KqlWriter.True : Utf8KqlWriter.False);
                        return;
                    case 2: // i8
                    case 3: // i16
                    case 5: // i32
                        w.WriteInt32(ReadVarint32(span, ref pos));
                        return;
                    case 7: // i64
                        w.WriteInt64(ReadVarint64(span, ref pos));
                        return;
                    case 10: // fp32
                    {
                        int bits = ReadFixed32(span, ref pos);
#if NETSTANDARD2_0
                        float f; unsafe { f = *(float*)&bits; }
#else
                        float f = BitConverter.Int32BitsToSingle(bits);
#endif
                        w.WriteFloat(f);
                        return;
                    }
                    case 11: // fp64
                    {
                        long bits = ReadFixed64(span, ref pos);
                        w.WriteDouble(BitConverter.Int64BitsToDouble(bits));
                        return;
                    }
                    case 12: // string — the protobuf bytes are already UTF-8
                    {
                        int len = ReadVarint32(span, ref pos);
                        w.WriteKqlStringLiteral(span.Slice(pos, len));
                        pos += len;
                        return;
                    }
                    case 26: // null
                    {
                        int len = ReadVarint32(span, ref pos);
                        pos += len;
                        w.Write(Utf8KqlWriter.DynamicNull);
                        return;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            w.Write(Utf8KqlWriter.DynamicNull);
        }

        void WriteFieldReference(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            int fieldIndex = -1;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // direct_reference
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
            {
                if (schema != null && fieldIndex < schema.Count)
                    w.WriteUtf8(schema[fieldIndex].AsSpan(_data));
                else
                    w.WriteFieldRef(fieldIndex);
            }
            else
                throw SubstraitTranslationException.UnsupportedExpression("FieldReference has no direct struct field reference.");
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
                    case 1:
                    case 2:
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
                    case 1: fieldIndex = ReadVarint32(span, ref pos); break;
                    default: SkipField(span, wireType, ref pos); break;
                }
            }
            return fieldIndex;
        }

        void WriteScalarFunction(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            int functionRef = 0;
            var argPositions = new List<(int start, int len)>();

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: functionRef = ReadVarint32(span, ref pos); break;
                    case 4:
                    {
                        int len = ReadVarint32(span, ref pos);
                        argPositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            // Resolve function name from plan extensions
            string? funcSignature;
            if (!_functionAnchors.TryGetValue(functionRef, out funcSignature))
                throw SubstraitTranslationException.UndeclaredFunction(functionRef);

            if (!KqlFunctionMap.TryGet(funcSignature, out var mapping))
                throw SubstraitTranslationException.UnsupportedFunction(funcSignature);

            WriteResolvedFunction(span, w, mapping, argPositions, schema);
        }

        void WriteResolvedFunction(ReadOnlySpan<byte> span, Utf8KqlWriter w,
            KqlFunctionMapping mapping, List<(int start, int len)> argPositions, List<Utf8Span>? schema)
        {
            switch (mapping.Kind)
            {
                case KqlFunctionKind.InfixOperator:
                    // arg0 OP arg1 — KqlName includes surrounding spaces
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(mapping.KqlName);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case KqlFunctionKind.PrefixOperator:
                    // OP (arg0) — parenthesize to ensure correct precedence
                    w.Write(mapping.KqlName);
                    w.Write((byte)'(');
                    if (argPositions.Count >= 1)
                        WriteSingleArg(span, w, argPositions[0], schema);
                    w.Write((byte)')');
                    break;

                case KqlFunctionKind.Function:
                case KqlFunctionKind.AggregateFunction:
                    // func(args)
                    w.Write(mapping.KqlName);
                    w.Write((byte)'(');
                    WriteArgList(span, w, argPositions, schema);
                    w.Write((byte)')');
                    break;

                case KqlFunctionKind.Special:
                    WriteSpecialFunction(span, w, mapping.KqlName, argPositions, schema);
                    break;
            }
        }

        void WriteSpecialFunction(ReadOnlySpan<byte> span, Utf8KqlWriter w,
            byte[] specialKey, List<(int start, int len)> argPositions, List<Utf8Span>? schema)
        {
            // Match on the special key to determine output format.
            // specialKey is a UTF-8 encoded string like "is_null", "between", etc.
            string key = Encoding.UTF8.GetString(specialKey);

            switch (key)
            {
                case "is_null":
                    // isnull(arg0)
                    w.Write(IsNull);
                    w.Write((byte)'(');
                    if (argPositions.Count >= 1) WriteSingleArg(span, w, argPositions[0], schema);
                    w.Write((byte)')');
                    break;

                case "is_not_null":
                    // isnotnull(arg0)
                    w.Write(IsNotNull);
                    w.Write((byte)'(');
                    if (argPositions.Count >= 1) WriteSingleArg(span, w, argPositions[0], schema);
                    w.Write((byte)')');
                    break;

                case "between":
                    // arg0 between (arg1 .. arg2)
                    if (argPositions.Count >= 3)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(BetweenOp);
                        w.Write((byte)'(');
                        WriteSingleArg(span, w, argPositions[1], schema);
                        w.Write(DotDot);
                        WriteSingleArg(span, w, argPositions[2], schema);
                        w.Write((byte)')');
                    }
                    break;

                case "count_star":
                    w.Write(Utf8KqlWriter.CountFunc);
                    break;

                case "like":
                    // arg0 matches regex arg1  (KQL uses matches regex for LIKE patterns)
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(MatchesRegex);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case "starts_with":
                    // arg0 startswith arg1
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(StartsWith);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case "ends_with":
                    // arg0 endswith arg1
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(EndsWith);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case "contains":
                    // arg0 contains arg1
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(ContainsOp);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case "xor":
                    // (arg0 and not arg1) or (not arg0 and arg1)
                    // Simplified: arg0 != arg1 for booleans
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(NotEqualOp);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                case "is_not_nan":
                    // not isnan(arg0)
                    w.Write(NotPrefix);
                    w.Write(IsNan);
                    w.Write((byte)'(');
                    if (argPositions.Count >= 1) WriteSingleArg(span, w, argPositions[0], schema);
                    w.Write((byte)')');
                    break;

                case "nullif":
                    // iif(arg0 == arg1, dynamic(null), arg0)
                    if (argPositions.Count >= 2)
                    {
                        w.Write(Utf8KqlWriter.Iif);
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(EqualOp);
                        WriteSingleArg(span, w, argPositions[1], schema);
                        w.Write(Utf8KqlWriter.Comma);
                        w.Write((byte)' ');
                        w.Write(Utf8KqlWriter.DynamicNull);
                        w.Write(Utf8KqlWriter.Comma);
                        w.Write((byte)' ');
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write((byte)')');
                    }
                    break;

                case "regexp_match":
                    // arg0 matches regex arg1
                    if (argPositions.Count >= 2)
                    {
                        WriteSingleArg(span, w, argPositions[0], schema);
                        w.Write(MatchesRegex);
                        WriteSingleArg(span, w, argPositions[1], schema);
                    }
                    break;

                default:
                    // Unknown special: emit as function call
                    w.Write(specialKey);
                    w.Write((byte)'(');
                    WriteArgList(span, w, argPositions, schema);
                    w.Write((byte)')');
                    break;
            }
        }

        void WriteSingleArg(ReadOnlySpan<byte> span, Utf8KqlWriter w, (int start, int len) arg, List<Utf8Span>? schema)
        {
            int p = arg.start;
            WriteFunctionArgument(span, ref p, arg.start + arg.len, w, schema);
        }

        void WriteArgList(ReadOnlySpan<byte> span, Utf8KqlWriter w, List<(int start, int len)> argPositions, List<Utf8Span>? schema)
        {
            for (int i = 0; i < argPositions.Count; i++)
            {
                if (i > 0) w.Write(Utf8KqlWriter.Comma);
                WriteSingleArg(span, w, argPositions[i], schema);
            }
        }

        // UTF-8 constants for special function output
        static readonly byte[] IsNull = "isnull"u8.ToArray();
        static readonly byte[] IsNotNull = "isnotnull"u8.ToArray();
        static readonly byte[] IsNan = "isnan"u8.ToArray();
        static readonly byte[] BetweenOp = " between "u8.ToArray();
        static readonly byte[] DotDot = " .. "u8.ToArray();
        static readonly byte[] MatchesRegex = " matches regex "u8.ToArray();
        static readonly byte[] StartsWith = " startswith "u8.ToArray();
        static readonly byte[] EndsWith = " endswith "u8.ToArray();
        static readonly byte[] ContainsOp = " contains "u8.ToArray();
        static readonly byte[] NotEqualOp = " != "u8.ToArray();
        static readonly byte[] EqualOp = " == "u8.ToArray();
        static readonly byte[] NotPrefix = "not "u8.ToArray();

        void WriteFunctionArgument(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
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
                        WriteExpression(span, ref pos, exprEnd, w, schema);
                        pos = exprEnd;
                        return;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        break;
                }
            }
            throw SubstraitTranslationException.UnsupportedExpression("FunctionArgument has no value expression.");
        }

        void WriteIfThen(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            var clausePositions = new System.Collections.Generic.List<(int start, int len)>();
            int elseStart = -1, elseLen = 0;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1:
                    {
                        int len = ReadVarint32(span, ref pos);
                        clausePositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    case 2:
                    {
                        int len = ReadVarint32(span, ref pos);
                        elseStart = pos; elseLen = len;
                        pos += len;
                        break;
                    }
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            // Emit nested iif()
            for (int i = 0; i < clausePositions.Count; i++)
            {
                if (i > 0) { w.Write(Utf8KqlWriter.Comma); w.Write((byte)' '); }
                w.Write(Utf8KqlWriter.Iif);
                int p = clausePositions[i].start;
                WriteIfClause(span, ref p, clausePositions[i].start + clausePositions[i].len, w, schema);
            }

            w.Write(Utf8KqlWriter.Comma);
            w.Write((byte)' ');
            if (elseStart >= 0) { int p = elseStart; WriteExpression(span, ref p, elseStart + elseLen, w, schema); }
            else { w.Write(Utf8KqlWriter.DynamicNull); }

            for (int i = 0; i < clausePositions.Count; i++) w.Write((byte)')');
        }

        void WriteIfClause(ReadOnlySpan<byte> span, ref int pos, int end, Utf8KqlWriter w, List<Utf8Span>? schema)
        {
            int condStart = -1, condLen = 0;
            int thenStart = -1, thenLen = 0;

            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: { int len = ReadVarint32(span, ref pos); condStart = pos; condLen = len; pos += len; break; }
                    case 2: { int len = ReadVarint32(span, ref pos); thenStart = pos; thenLen = len; pos += len; break; }
                    default: SkipField(span, wireType, ref pos); break;
                }
            }

            if (condStart >= 0) { int p = condStart; WriteExpression(span, ref p, condStart + condLen, w, schema); }
            else { w.Write(Utf8KqlWriter.True); }

            w.Write(Utf8KqlWriter.Comma);
            w.Write((byte)' ');

            if (thenStart >= 0) { int p = thenStart; WriteExpression(span, ref p, thenStart + thenLen, w, schema); }
            else { w.Write(Utf8KqlWriter.DynamicNull); }
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
                case 0: while ((span[pos++] & 0x80) != 0) { } break;
                case 1: pos += 8; break;
                case 2: int len = ReadVarint32(span, ref pos); pos += len; break;
                case 5: pos += 4; break;
                default: throw new InvalidOperationException($"Unknown wire type: {wireType}");
            }
        }

        #endregion
    }
}
