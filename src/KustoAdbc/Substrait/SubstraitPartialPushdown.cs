// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using F = KustoAdbc.Substrait.SubstraitFields;

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
    /// Walks a Substrait plan top-down and replaces maximal translatable subtrees
    /// with ReadRel nodes containing the equivalent KQL query.
    ///
    /// The output is always a valid Substrait plan. Unsupported nodes are preserved
    /// verbatim. KQL query nodes are encoded as a ReadRel whose NamedTable names are
    /// ["kql_query", "&lt;KQL&gt;"] and whose base_schema describes the query's
    /// output columns, in the same order as the subtree they replace, so references
    /// from the rest of the plan stay valid. The plan declares the extension URN
    /// "extension:kusto:kql_query" to mark that it uses this convention.
    /// </summary>
    public static class SubstraitPartialPushdown
    {
        public const string KqlExtensionUrn = "extension:kusto:kql_query";
        public const string KqlFunctionName = "kql_query";

        /// <summary>
        /// Performs partial pushdown on a Substrait plan.
        /// </summary>
        public static PushdownResult Pushdown(byte[] planBytes)
        {
            if (planBytes == null || planBytes.Length == 0)
                throw new ArgumentException("Substrait plan is empty.", nameof(planBytes));

            return new PushdownEngine(planBytes).Execute();
        }
    }

    sealed class PushdownEngine
    {
        readonly byte[] _data;
        // Top-level plan fields that give translated subtrees their context
        // (extension URNs/URIs and declarations, execution behavior), copied verbatim.
        readonly List<ProtoSlice> _headerFields = new();
        // An extension URN anchor not used by the input plan, for the KQL query URN.
        uint _kqlUrnAnchor = 1;

        public PushdownEngine(byte[] data) { _data = data; }

        public PushdownResult Execute()
        {
            var relations = new List<ProtoSlice>();
            var otherFields = new List<ProtoSlice>();

            // Phase 1: scan top-level fields.
            var r = new ProtoReader(_data, 0, _data.Length);
            int fieldStart = r.Position;
            while (r.Next(out int field, out int wire))
            {
                if (field == F.Plan_Relations)
                {
                    relations.Add(r.ReadSlice(wire));
                }
                else
                {
                    if (field is F.Plan_ExtensionUrns or F.Plan_ExtensionUris)
                    {
                        var urn = r.ReadMessage(wire);
                        while (urn.Next(out int uf, out int uw))
                        {
                            if (uf == F.ExtensionUrn_Anchor) _kqlUrnAnchor = Math.Max(_kqlUrnAnchor, urn.ReadUInt32(uw) + 1);
                            else urn.Skip(uw);
                        }
                    }
                    else
                    {
                        r.Skip(wire);
                    }
                    var whole = new ProtoSlice(fieldStart, r.Position - fieldStart);
                    if (field is F.Plan_ExtensionUrns or F.Plan_ExtensionUris or F.Plan_ExtensionDeclarations or F.Plan_ExecutionBehavior)
                        _headerFields.Add(whole);
                    else
                        otherFields.Add(whole);
                }
                fieldStart = r.Position;
            }

            if (relations.Count == 0)
                throw SubstraitTranslationException.MalformedPlan("Plan contains no relations.");

            // Phase 2: fast path, the whole plan translates.
            try
            {
                var (kql, columns) = SubstraitToKqlTranslator.TranslateWithSchema(_data);
                if (TryBuildFullyPushedPlan(kql, columns, out var plan))
                    return new PushdownResult(plan, true, kql);
            }
            catch (SubstraitTranslationException)
            {
                // Fall through to partial pushdown.
            }

            // Phase 3: partial pushdown.
            var output = new ProtobufWriter(Math.Max(_data.Length, 256));
            foreach (var f in _headerFields)
                output.WriteRawBytes(_data.AsSpan(f.Start, f.Length));
            WriteKqlUrn(output);
            foreach (var f in otherFields)
                output.WriteRawBytes(_data.AsSpan(f.Start, f.Length));

            foreach (var rel in relations)
            {
                var planRel = new ProtobufWriter(rel.Length + 64);
                WritePartialPlanRel(new ProtoReader(_data, rel), planRel);
                output.WriteLengthDelimited(F.Plan_Relations, planRel);
            }

            return new PushdownResult(output.WrittenMemory, false, null);
        }

        #region Partial Plan Writing

        void WritePartialPlanRel(ProtoReader r, ProtobufWriter output)
        {
            int fieldStart = r.Position;
            while (r.Next(out int field, out int wire))
            {
                if (field == F.PlanRel_Rel)
                {
                    var rel = r.ReadSlice(wire);
                    output.WriteLengthDelimited(F.PlanRel_Rel, w => WritePartialRel(rel, w));
                }
                else if (field == F.PlanRel_Root)
                {
                    var root = r.ReadMessage(wire);
                    output.WriteLengthDelimited(F.PlanRel_Root, w => WritePartialRelRoot(root, w));
                }
                else
                {
                    r.Skip(wire);
                    output.WriteRawBytes(_data.AsSpan(fieldStart, r.Position - fieldStart));
                }
                fieldStart = r.Position;
            }
        }

        void WritePartialRelRoot(ProtoReader r, ProtobufWriter output)
        {
            int fieldStart = r.Position;
            while (r.Next(out int field, out int wire))
            {
                if (field == F.RelRoot_Input)
                {
                    var rel = r.ReadSlice(wire);
                    output.WriteLengthDelimited(F.RelRoot_Input, w => WritePartialRel(rel, w));
                }
                else
                {
                    r.Skip(wire);
                    output.WriteRawBytes(_data.AsSpan(fieldStart, r.Position - fieldStart));
                }
                fieldStart = r.Position;
            }
        }

        /// <summary>Writes the content of a Rel message: a KQL node if the subtree translates, else the original with rewritten children.</summary>
        void WritePartialRel(ProtoSlice rel, ProtobufWriter output)
        {
            if (TryTranslateRel(rel, out var kql, out var columns))
            {
                WriteKqlReadRel(output, kql, columns);
                return;
            }

            var r = new ProtoReader(_data, rel);
            int fieldStart = r.Position;
            while (r.Next(out int relType, out int wire))
            {
                var childFields = ChildRelFields(relType);
                if (childFields.Length == 0 || wire != ProtoReader.WireLengthDelimited)
                {
                    r.Skip(wire);
                    output.WriteRawBytes(_data.AsSpan(fieldStart, r.Position - fieldStart));
                }
                else
                {
                    var content = r.ReadMessage(wire);
                    output.WriteLengthDelimited(relType, w => WritePartialRelContent(content, childFields, w));
                }
                fieldStart = r.Position;
            }
        }

        void WritePartialRelContent(ProtoReader r, int[] childFields, ProtobufWriter output)
        {
            int fieldStart = r.Position;
            while (r.Next(out int field, out int wire))
            {
                if (Array.IndexOf(childFields, field) >= 0 && wire == ProtoReader.WireLengthDelimited)
                {
                    var child = r.ReadSlice(wire);
                    output.WriteLengthDelimited(field, w => WritePartialRel(child, w));
                }
                else
                {
                    r.Skip(wire);
                    output.WriteRawBytes(_data.AsSpan(fieldStart, r.Position - fieldStart));
                }
                fieldStart = r.Position;
            }
        }

        static readonly int[] s_noChildren = Array.Empty<int>();
        static readonly int[] s_singleInput = { F.SingleInputRel_Input };
        static readonly int[] s_biInputs = { F.BiRel_Left, F.BiRel_Right };
        static readonly int[] s_multiInputs = { F.MultiInputRel_Inputs };

        /// <summary>The fields of each relation message that hold child relations.</summary>
        static int[] ChildRelFields(int relType) => relType switch
        {
            F.Rel_Filter or F.Rel_Fetch or F.Rel_Aggregate or F.Rel_Sort or F.Rel_Project
                or F.Rel_ExtensionSingle or F.Rel_Window or F.Rel_Exchange or F.Rel_Expand or F.Rel_TopN => s_singleInput,
            F.Rel_Join or F.Rel_Cross or F.Rel_HashJoin or F.Rel_MergeJoin or F.Rel_NestedLoopJoin or F.Rel_LateralJoin => s_biInputs,
            F.Rel_Set or F.Rel_ExtensionMulti => s_multiInputs,
            _ => s_noChildren,
        };

        #endregion

        #region KQL Node Emission

        bool TryTranslateRel(ProtoSlice rel, out Utf8KqlWriter kql, out List<KqlColumn> columns)
        {
            try
            {
                (kql, columns) = SubstraitToKqlTranslator.TranslateWithSchema(BuildTempPlan(rel));
                // The replacement node must declare its output types; skip subtrees where some are unknown.
                if (columns.All(c => TypeEncoder.CanEncode(c.Type)))
                    return true;
            }
            catch (SubstraitTranslationException)
            {
            }
            kql = null!;
            columns = null!;
            return false;
        }

        byte[] BuildTempPlan(ProtoSlice rel)
        {
            var plan = new ProtobufWriter(rel.Length + 256);
            foreach (var f in _headerFields)
                plan.WriteRawBytes(_data.AsSpan(f.Start, f.Length));
            plan.WriteLengthDelimited(F.Plan_Relations, planRel =>
                planRel.WriteLengthDelimited(F.PlanRel_Root, root =>
                    root.WriteBytesField(F.RelRoot_Input, _data.AsSpan(rel.Start, rel.Length))));
            return plan.ToArray();
        }

        static void WriteKqlReadRel(ProtobufWriter output, Utf8KqlWriter kql, List<KqlColumn> columns)
        {
            output.WriteLengthDelimited(F.Rel_Read, read =>
            {
                read.WriteLengthDelimited(F.ReadRel_BaseSchema, schema =>
                {
                    foreach (var c in columns)
                        schema.WriteStringField(F.NamedStruct_Names, c.Name);
                    schema.WriteLengthDelimited(F.NamedStruct_Struct, st =>
                    {
                        foreach (var c in columns)
                            st.WriteLengthDelimited(F.StructType_Types, t => TypeEncoder.Write(t, c.Type));
                        st.WriteVarintField(F.StructType_Nullability, F.Nullability_Required);
                    });
                });
                read.WriteLengthDelimited(F.ReadRel_NamedTable, table =>
                {
                    table.WriteStringField(F.NamedTable_Names, SubstraitPartialPushdown.KqlFunctionName);
                    table.WriteStringField(F.NamedTable_Names, kql.ToString());
                });
            });
        }

        void WriteKqlUrn(ProtobufWriter output)
        {
            output.WriteLengthDelimited(F.Plan_ExtensionUrns, urn =>
            {
                urn.WriteVarintField(F.ExtensionUrn_Anchor, (int)_kqlUrnAnchor);
                urn.WriteStringField(F.ExtensionUrn_Urn, SubstraitPartialPushdown.KqlExtensionUrn);
            });
        }

        bool TryBuildFullyPushedPlan(Utf8KqlWriter kql, List<KqlColumn> columns, out ReadOnlyMemory<byte> plan)
        {
            plan = default;
            if (!columns.All(c => TypeEncoder.CanEncode(c.Type)))
                return false;

            var output = new ProtobufWriter(kql.Length + 256);
            WriteKqlUrn(output);
            output.WriteLengthDelimited(F.Plan_Relations, planRel =>
                planRel.WriteLengthDelimited(F.PlanRel_Root, root =>
                {
                    root.WriteLengthDelimited(F.RelRoot_Input, rel => WriteKqlReadRel(rel, kql, columns));
                    foreach (var c in columns)
                        root.WriteStringField(F.RelRoot_Names, c.Name);
                }));
            plan = output.WrittenMemory;
            return true;
        }

        #endregion
    }

    /// <summary>
    /// Encodes the type of a KQL query's output column as a Substrait Type message. The declared
    /// type is the one the driver returns for the corresponding Kusto type (see Arrow/Property.cs):
    /// decimal as fp64, guid as string, datetime as timestamp[us, UTC], timespan as duration[us].
    /// Every column is declared nullable, since Kusto doesn't track nullability.
    /// </summary>
    static class TypeEncoder
    {
        public static bool CanEncode(SType type) => FieldFor(AsReturned(type).Kind) != 0;

        static SType AsReturned(SType type) => type.Kind switch
        {
            TypeKind.Decimal => SType.Fp64,
            TypeKind.Uuid or TypeKind.VarChar or TypeKind.FixedChar => SType.String,
            TypeKind.Date or TypeKind.PrecisionTimestamp or TypeKind.PrecisionTimestampTz => new SType(TypeKind.PrecisionTimestampTz, precision: 6),
            TypeKind.IntervalDay => new SType(TypeKind.IntervalDay, precision: 6),
            _ => type,
        };

        public static void Write(ProtobufWriter output, SType declared)
        {
            var type = AsReturned(declared);
            int nullability = F.Nullability_Nullable;
            output.WriteLengthDelimited(FieldFor(type.Kind), t =>
            {
                switch (type.Kind)
                {
                    case TypeKind.Decimal:
                        t.WriteVarintField(F.DecimalType_Scale, type.Scale);
                        t.WriteVarintField(F.DecimalType_Precision, type.Precision);
                        t.WriteVarintField(F.DecimalType_Nullability, nullability);
                        break;
                    case TypeKind.PrecisionTimestamp:
                    case TypeKind.PrecisionTimestampTz:
                    case TypeKind.FixedChar:
                    case TypeKind.VarChar:
                        t.WriteVarintField(F.PrecisionType_Precision, type.Precision);
                        t.WriteVarintField(F.ParameterizedType_Nullability, nullability);
                        break;
                    case TypeKind.IntervalDay:
                        t.WriteVarintField(F.SimpleType_Nullability, nullability);
                        t.WriteVarintField(F.IntervalDayType_Precision, type.Precision);
                        break;
                    default:
                        t.WriteVarintField(F.SimpleType_Nullability, nullability);
                        break;
                }
            });
        }

        static int FieldFor(TypeKind kind) => kind switch
        {
            TypeKind.Bool => F.Type_Bool,
            TypeKind.I32 => F.Type_I32,
            TypeKind.I64 => F.Type_I64,
            TypeKind.Fp64 => F.Type_Fp64,
            TypeKind.String => F.Type_String,
            TypeKind.VarChar => F.Type_VarChar,
            TypeKind.FixedChar => F.Type_FixedChar,
            TypeKind.Date => F.Type_Date,
            TypeKind.IntervalDay => F.Type_IntervalDay,
            TypeKind.Uuid => F.Type_Uuid,
            TypeKind.Decimal => F.Type_Decimal,
            TypeKind.PrecisionTimestamp => F.Type_PrecisionTimestamp,
            TypeKind.PrecisionTimestampTz => F.Type_PrecisionTimestampTz,
            _ => 0,
        };
    }
}
