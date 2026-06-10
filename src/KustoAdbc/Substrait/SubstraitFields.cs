// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Named constants for Substrait protobuf field numbers.
    /// These correspond to the field numbers defined in the Substrait .proto schema
    /// and are used in place of raw numeric literals when parsing protobuf wire format.
    /// </summary>
    internal static class SubstraitFields
    {
        // ── Plan ───────────────────────────────────────────────────────
        public const int Plan_Version = 1;
        public const int Plan_ExtensionDeclarations = 2;
        public const int Plan_Relations = 3;
        public const int Plan_ExtensionUris = 8;

        // ── PlanRel ────────────────────────────────────────────────────
        public const int PlanRel_Rel = 1;
        public const int PlanRel_Root = 2;

        // ── RelRoot ────────────────────────────────────────────────────
        public const int RelRoot_Input = 1;
        public const int RelRoot_Names = 2;

        // ── Rel (oneof rel_type) ───────────────────────────────────────
        public const int Rel_Read = 1;
        public const int Rel_Filter = 2;
        public const int Rel_Fetch = 3;
        public const int Rel_Aggregate = 4;
        public const int Rel_Sort = 5;
        public const int Rel_Join = 6;
        public const int Rel_Project = 7;

        // ── RelCommon ──────────────────────────────────────────────────
        public const int RelCommon = 1;

        // ── ReadRel ────────────────────────────────────────────────────
        public const int ReadRel_Common = 1;
        public const int ReadRel_BaseSchema = 2;
        public const int ReadRel_Filter = 3;
        public const int ReadRel_BestEffortFilter = 4;
        public const int ReadRel_Projection = 5;
        public const int ReadRel_NamedTable = 7;

        // ── FilterRel ──────────────────────────────────────────────────
        public const int FilterRel_Common = 1;
        public const int FilterRel_Input = 2;
        public const int FilterRel_Condition = 3;

        // ── FetchRel ───────────────────────────────────────────────────
        public const int FetchRel_Common = 1;
        public const int FetchRel_Input = 2;
        public const int FetchRel_Offset = 3;
        public const int FetchRel_Count = 4;

        // ── ProjectRel ─────────────────────────────────────────────────
        public const int ProjectRel_Common = 1;
        public const int ProjectRel_Input = 2;
        public const int ProjectRel_Expressions = 3;

        // ── SortRel ────────────────────────────────────────────────────
        public const int SortRel_Common = 1;
        public const int SortRel_Input = 2;
        public const int SortRel_Sorts = 3;

        // ── AggregateRel ───────────────────────────────────────────────
        public const int AggregateRel_Common = 1;
        public const int AggregateRel_Input = 2;
        public const int AggregateRel_Groupings = 3;
        public const int AggregateRel_Measures = 4;

        // ── JoinRel ────────────────────────────────────────────────────
        public const int JoinRel_Common = 1;
        public const int JoinRel_Left = 2;
        public const int JoinRel_Right = 3;
        public const int JoinRel_Expression = 4;
        public const int JoinRel_Type = 5;
        public const int JoinRel_PostJoinFilter = 6;

        // ── SortField ──────────────────────────────────────────────────
        public const int SortField_Expr = 1;
        public const int SortField_Direction = 2;

        // ── Grouping ───────────────────────────────────────────────────
        public const int Grouping_GroupingExpressions = 1;

        // ── Measure ────────────────────────────────────────────────────
        public const int Measure_Measure = 1;
        public const int Measure_Filter = 2;

        // ── Expression (oneof rex_type) ────────────────────────────────
        public const int Expression_Literal = 1;
        public const int Expression_Selection = 2;
        public const int Expression_ScalarFunction = 3;
        public const int Expression_WindowFunction = 4;
        public const int Expression_IfThen = 5;
        public const int Expression_Cast = 6;
        public const int Expression_Subquery = 7;
        public const int Expression_SingularOrList = 8;
        public const int Expression_MultiOrList = 9;
        public const int Expression_Nested = 11;
        public const int Expression_Enum = 10;

        // ── Expression.Literal (oneof literal_type) ────────────────────
        public const int Literal_Boolean = 1;
        public const int Literal_I8 = 2;
        public const int Literal_I16 = 3;
        public const int Literal_I32 = 5;
        public const int Literal_I64 = 7;
        public const int Literal_Fp32 = 10;
        public const int Literal_Fp64 = 11;
        public const int Literal_String = 12;
        public const int Literal_Binary = 13;
        public const int Literal_Timestamp = 14;
        public const int Literal_Date = 16;
        public const int Literal_Time = 17;
        public const int Literal_TimestampTz = 27;
        public const int Literal_Null = 26;

        // ── FieldReference ─────────────────────────────────────────────
        public const int FieldReference_DirectReference = 1;
        public const int FieldReference_MaskedReference = 2;
        public const int FieldReference_RootReference = 3;

        // ── ReferenceSegment (oneof reference_type) ────────────────────
        public const int ReferenceSegment_MapKey = 1;
        public const int ReferenceSegment_ListElement = 2;
        public const int ReferenceSegment_StructField = 3;

        // ── ReferenceSegment.StructField ───────────────────────────────
        public const int StructField_Field = 1;
        public const int StructField_Child = 2;

        // ── ScalarFunction ─────────────────────────────────────────────
        public const int ScalarFunction_FunctionReference = 1;
        public const int ScalarFunction_OutputType = 3;
        public const int ScalarFunction_Arguments = 4;
        public const int ScalarFunction_Options = 5;

        // ── FunctionArgument ───────────────────────────────────────────
        public const int FunctionArgument_Enum = 1;
        public const int FunctionArgument_Value = 2;
        public const int FunctionArgument_Type = 3;

        // ── IfThen ─────────────────────────────────────────────────────
        public const int IfThen_Ifs = 1;
        public const int IfThen_Else = 2;

        // ── IfClause ───────────────────────────────────────────────────
        public const int IfClause_If = 1;
        public const int IfClause_Then = 2;

        // ── NamedStruct ────────────────────────────────────────────────
        public const int NamedStruct_Names = 1;
        public const int NamedStruct_Struct = 2;

        // ── NamedTable ─────────────────────────────────────────────────
        public const int NamedTable_Names = 1;
        public const int NamedTable_AdvancedExtension = 2;

        // ── SimpleExtensionURI ─────────────────────────────────────────
        public const int ExtensionUri_Anchor = 1;
        public const int ExtensionUri_Uri = 2;

        // ── SimpleExtensionDeclaration (oneof mapping_type) ────────────
        public const int ExtensionDecl_ExtensionType = 1;
        public const int ExtensionDecl_ExtensionTypeVariation = 2;
        public const int ExtensionDecl_ExtensionFunction = 3;

        // ── SimpleExtensionDeclaration.ExtensionFunction ───────────────
        public const int ExtensionFunction_UriReference = 1;
        public const int ExtensionFunction_Anchor = 2;
        public const int ExtensionFunction_Name = 3;
    }
}
