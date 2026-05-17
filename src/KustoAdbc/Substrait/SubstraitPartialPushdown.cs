// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Result of partial pushdown: a Substrait plan with maximal KQL substitution.
    /// </summary>
    public sealed class PushdownResult
    {
        /// <summary>The output Substrait plan (protobuf bytes). Always a valid plan.</summary>
        public ReadOnlyMemory<byte> Plan { get; }

        /// <summary>True if the entire plan was translatable to a single KQL query.</summary>
        public bool FullyPushed { get; }

        /// <summary>
        /// If fully pushed, the KQL query. Otherwise null.
        /// Callers can use this to skip re-parsing the output plan.
        /// </summary>
        public Utf8KqlWriter? Kql { get; }

        internal PushdownResult(ReadOnlyMemory<byte> plan, bool fullyPushed, Utf8KqlWriter? kql)
        {
            Plan = plan;
            FullyPushed = fullyPushed;
            Kql = kql;
        }
    }

    /// <summary>
    /// Walks a Substrait plan bottom-up and replaces maximal translatable subtrees
    /// with custom extension ReadRel nodes containing the equivalent KQL query.
    ///
    /// The output is always a valid Substrait plan. Unsupported nodes are preserved
    /// verbatim. The custom extension uses the URI "extension:kusto:kql_query".
    /// KQL query nodes are encoded as ReadRel with NamedTable names
    /// ["kql_query", "&lt;actual KQL string&gt;"].
    /// </summary>
    public static class SubstraitPartialPushdown
    {
        public const string KqlExtensionUri = "extension:kusto:kql_query";
        public const string KqlFunctionName = "kql_query";
        internal const int KqlUriAnchor = 9999;

        /// <summary>
        /// Performs partial pushdown on a Substrait plan.
        /// </summary>
        public static PushdownResult Pushdown(byte[] planBytes)
        {
            if (planBytes == null || planBytes.Length == 0)
                throw new ArgumentException("Substrait plan is empty.", nameof(planBytes));

            var reader = new PushdownEngine(planBytes);
            return reader.Execute();
        }
    }

    sealed class PushdownEngine
    {
        readonly byte[] _data;
        readonly Dictionary<int, string> _functionAnchors = new();
        readonly List<(int start, int end)> _extensionPositions = new();
        readonly List<(int start, int end)> _uriPositions = new();

        public PushdownEngine(byte[] data) { _data = data; }

        public PushdownResult Execute()
        {
            var span = _data.AsSpan();
            int pos = 0;

            var relationPositions = new List<(int start, int len)>();
            var otherFieldPositions = new List<(int start, int end)>();

            // Phase 1: Scan top-level fields
            while (pos < span.Length)
            {
                int tagStart = pos;
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 2: // extensions
                    {
                        int len = ReadVarint32(span, ref pos);
                        int end = pos + len;
                        ParseExtensionDeclaration(span, pos, end);
                        _extensionPositions.Add((tagStart, end));
                        pos = end;
                        break;
                    }
                    case 3: // relations
                    {
                        int len = ReadVarint32(span, ref pos);
                        relationPositions.Add((pos, len));
                        pos += len;
                        break;
                    }
                    case 8: // extension_urns
                    {
                        int len = ReadVarint32(span, ref pos);
                        int end = pos + len;
                        ParseExtensionUri(span, pos, end);
                        _uriPositions.Add((tagStart, end));
                        pos = end;
                        break;
                    }
                    default:
                    {
                        SkipField(span, wireType, ref pos);
                        otherFieldPositions.Add((tagStart, pos));
                        break;
                    }
                }
            }

            if (relationPositions.Count == 0)
                throw SubstraitTranslationException.MalformedPlan("Plan contains no relations.");

            // Phase 2: Fast path — try full pushdown
            try
            {
                var kqlWriter = new Utf8KqlWriter();
                var fullReader = new SubstraitPlanReader(_data);
                fullReader.WriteTo(kqlWriter);

                var outputPlan = BuildFullyPushedPlan(kqlWriter);
                return new PushdownResult(outputPlan, true, kqlWriter);
            }
            catch (SubstraitTranslationException)
            {
                // Full pushdown failed — continue to partial
            }

            // Phase 3: Partial pushdown
            var output = new ProtobufWriter(Math.Max(_data.Length, 256));

            // Copy extension URIs and declarations
            foreach (var (start, end) in _uriPositions)
                output.WriteRawBytes(span.Slice(start, end - start));
            foreach (var (start, end) in _extensionPositions)
                output.WriteRawBytes(span.Slice(start, end - start));

            // Add our KQL extension URI
            WriteKqlExtUri(output);

            // Copy other fields (version, etc.)
            foreach (var (start, end) in otherFieldPositions)
                output.WriteRawBytes(span.Slice(start, end - start));

            // Process relations with partial pushdown
            foreach (var (rStart, rLen) in relationPositions)
            {
                var planRelContent = new ProtobufWriter(rLen + 64);
                int p = rStart;
                WritePartialPlanRel(_data, ref p, rStart + rLen, planRelContent);
                output.WriteLengthDelimited(3, planRelContent);
            }

            return new PushdownResult(output.WrittenMemory, false, null);
        }

        #region Partial Plan Writing

        void WritePartialPlanRel(byte[] data, ref int pos, int end, ProtobufWriter output)
        {
            var span = data.AsSpan();
            while (pos < end)
            {
                int tagStart = pos;
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // rel
                    {
                        int len = ReadVarint32(span, ref pos);
                        var relWriter = new ProtobufWriter(len + 32);
                        int p = pos;
                        WritePartialRel(data, ref p, pos + len, relWriter);
                        output.WriteLengthDelimited(1, relWriter);
                        pos += len;
                        return;
                    }
                    case 2: // root
                    {
                        int len = ReadVarint32(span, ref pos);
                        var rootWriter = new ProtobufWriter(len + 32);
                        int p = pos;
                        WritePartialRelRoot(data, ref p, pos + len, rootWriter);
                        output.WriteLengthDelimited(2, rootWriter);
                        pos += len;
                        return;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        output.WriteRawBytes(span.Slice(tagStart, pos - tagStart));
                        break;
                }
            }
        }

        void WritePartialRelRoot(byte[] data, ref int pos, int end, ProtobufWriter output)
        {
            var span = data.AsSpan();
            while (pos < end)
            {
                int tagStart = pos;
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // input (Rel)
                    {
                        int len = ReadVarint32(span, ref pos);
                        var relWriter = new ProtobufWriter(len + 32);
                        int p = pos;
                        WritePartialRel(data, ref p, pos + len, relWriter);
                        output.WriteLengthDelimited(1, relWriter);
                        pos += len;
                        break;
                    }
                    default:
                        SkipField(span, wireType, ref pos);
                        output.WriteRawBytes(span.Slice(tagStart, pos - tagStart));
                        break;
                }
            }
        }

        void WritePartialRel(byte[] data, ref int pos, int end, ProtobufWriter output)
        {
            var span = data.AsSpan();
            int relStart = pos;
            int relLen = end - pos;

            // Speculative: try full subtree translation
            string? kql = TryTranslateRel(relStart, relLen);
            if (kql != null)
            {
                WriteKqlReadRel(output, kql);
                pos = end;
                return;
            }

            // Partial: find relation type and recurse into children
            int innerPos = pos;
            while (innerPos < end)
            {
                int tag = ReadTag(span, ref innerPos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                if (fieldNumber >= 1 && fieldNumber <= 20 && wireType == 2)
                {
                    int len = ReadVarint32(span, ref innerPos);
                    int relContentEnd = innerPos + len;

                    var innerWriter = new ProtobufWriter(len + 32);
                    int p = innerPos;
                    WritePartialRelContent(data, ref p, relContentEnd, fieldNumber, innerWriter);
                    output.WriteLengthDelimited(fieldNumber, innerWriter);
                    pos = end;
                    return;
                }
                else
                {
                    SkipField(span, wireType, ref innerPos);
                }
            }

            // Couldn't parse — copy verbatim
            output.WriteRawBytes(span.Slice(relStart, relLen));
            pos = end;
        }

        void WritePartialRelContent(byte[] data, ref int pos, int end, int relType, ProtobufWriter output)
        {
            var span = data.AsSpan();
            var childRelFields = GetChildRelFields(relType);

            while (pos < end)
            {
                int tagStart = pos;
                int tag = ReadTag(span, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                if (childRelFields.Contains(fieldNumber) && wireType == 2)
                {
                    int len = ReadVarint32(span, ref pos);
                    int childEnd = pos + len;
                    var childWriter = new ProtobufWriter(len + 32);
                    int p = pos;
                    WritePartialRel(data, ref p, childEnd, childWriter);
                    output.WriteLengthDelimited(fieldNumber, childWriter);
                    pos = childEnd;
                }
                else
                {
                    SkipField(span, wireType, ref pos);
                    output.WriteRawBytes(span.Slice(tagStart, pos - tagStart));
                }
            }
        }

        static HashSet<int> GetChildRelFields(int relType) => relType switch
        {
            1 => s_noChildren,
            2 => s_singleInput,    // FilterRel: input=2
            3 => s_singleInput,    // FetchRel: input=2
            4 => s_singleInput,    // AggregateRel: input=2
            5 => s_singleInput,    // SortRel: input=2
            6 => s_joinInputs,     // JoinRel: left=2, right=3
            7 => s_singleInput,    // ProjectRel: input=2
            _ => s_noChildren,
        };

        static readonly HashSet<int> s_noChildren = new();
        static readonly HashSet<int> s_singleInput = new() { 2 };
        static readonly HashSet<int> s_joinInputs = new() { 2, 3 };

        #endregion

        #region KQL Node Emission

        string? TryTranslateRel(int relStart, int relLen)
        {
            try
            {
                var tempPlan = BuildTempPlan(relStart, relLen);
                return SubstraitToKqlTranslator.Translate(tempPlan);
            }
            catch (SubstraitTranslationException) { return null; }
        }

        byte[] BuildTempPlan(int relStart, int relLen)
        {
            var plan = new ProtobufWriter(relLen + 256);

            // Copy extensions for function resolution
            var span = _data.AsSpan();
            foreach (var (start, end) in _uriPositions)
                plan.WriteRawBytes(span.Slice(start, end - start));
            foreach (var (start, end) in _extensionPositions)
                plan.WriteRawBytes(span.Slice(start, end - start));

            // Wrap rel in PlanRel { root { input: rel } }
            var rootContent = new ProtobufWriter(relLen + 16);
            rootContent.WriteBytesField(1, span.Slice(relStart, relLen));
            var planRelContent = new ProtobufWriter(rootContent.Length + 8);
            planRelContent.WriteLengthDelimited(2, rootContent);
            plan.WriteLengthDelimited(3, planRelContent);

            return plan.ToArray();
        }

        void WriteKqlReadRel(ProtobufWriter output, string kql)
        {
            // Rel.read = field 1
            var namedTableContent = new ProtobufWriter(kql.Length + 32);
            namedTableContent.WriteStringField(1, SubstraitPartialPushdown.KqlFunctionName);
            namedTableContent.WriteStringField(1, kql);

            var readRelContent = new ProtobufWriter(namedTableContent.Length + 8);
            readRelContent.WriteLengthDelimited(7, namedTableContent); // named_table

            output.WriteLengthDelimited(1, readRelContent); // Rel.read
        }

        void WriteKqlExtUri(ProtobufWriter output)
        {
            var uriContent = new ProtobufWriter(64);
            uriContent.WriteVarintField(1, SubstraitPartialPushdown.KqlUriAnchor);
            uriContent.WriteStringField(2, SubstraitPartialPushdown.KqlExtensionUri);
            output.WriteLengthDelimited(8, uriContent);
        }

        byte[] BuildFullyPushedPlan(Utf8KqlWriter kql)
        {
            var output = new ProtobufWriter(_data.Length);
            WriteKqlExtUri(output);

            var relWriter = new ProtobufWriter(kql.Length + 64);
            WriteKqlReadRel(relWriter, kql.ToString());

            var rootWriter = new ProtobufWriter(relWriter.Length + 8);
            rootWriter.WriteLengthDelimited(1, relWriter); // RelRoot.input

            var planRelWriter = new ProtobufWriter(rootWriter.Length + 8);
            planRelWriter.WriteLengthDelimited(2, rootWriter); // PlanRel.root

            output.WriteLengthDelimited(3, planRelWriter); // Plan.relations

            return output.ToArray();
        }

        #endregion

        #region Extension Parsing

        void ParseExtensionUri(ReadOnlySpan<byte> span, int pos, int end)
        {
            int anchor = 0; string? uri = null;
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fn = tag >> 3; int wt = tag & 0x7;
                switch (fn)
                {
                    case 1: anchor = ReadVarint32(span, ref pos); break;
                    case 2:
                        int len = ReadVarint32(span, ref pos);
#if NETSTANDARD2_0
                        uri = Encoding.UTF8.GetString(span.Slice(pos, len).ToArray());
#else
                        uri = Encoding.UTF8.GetString(span.Slice(pos, len));
#endif
                        pos += len; break;
                    default: SkipField(span, wt, ref pos); break;
                }
            }
            if (uri != null && !_functionAnchors.ContainsKey(anchor))
                _functionAnchors[anchor] = uri;
        }

        void ParseExtensionDeclaration(ReadOnlySpan<byte> span, int pos, int end)
        {
            while (pos < end)
            {
                int tag = ReadTag(span, ref pos);
                int fn = tag >> 3; int wt = tag & 0x7;
                if (fn == 3 && wt == 2) // extension_function
                {
                    int len = ReadVarint32(span, ref pos);
                    int fEnd = pos + len;
                    int anchor = 0; string? name = null;
                    while (pos < fEnd)
                    {
                        int t2 = ReadTag(span, ref pos);
                        int fn2 = t2 >> 3; int wt2 = t2 & 0x7;
                        switch (fn2)
                        {
                            case 2: anchor = ReadVarint32(span, ref pos); break;
                            case 3:
                                int slen = ReadVarint32(span, ref pos);
#if NETSTANDARD2_0
                                name = Encoding.UTF8.GetString(span.Slice(pos, slen).ToArray());
#else
                                name = Encoding.UTF8.GetString(span.Slice(pos, slen));
#endif
                                pos += slen; break;
                            default: SkipField(span, wt2, ref pos); break;
                        }
                    }
                    if (name != null) _functionAnchors[anchor] = name;
                    pos = fEnd;
                }
                else SkipField(span, wt, ref pos);
            }
        }

        #endregion

        #region Protobuf Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadTag(ReadOnlySpan<byte> span, ref int pos) => ReadVarint32(span, ref pos);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadVarint32(ReadOnlySpan<byte> span, ref int pos)
        {
            int result = 0; int shift = 0; byte b;
            do { b = span[pos++]; result |= (b & 0x7F) << shift; shift += 7; }
            while ((b & 0x80) != 0);
            return result;
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
