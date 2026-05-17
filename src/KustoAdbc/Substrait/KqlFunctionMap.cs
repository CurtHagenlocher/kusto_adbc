// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Describes how a Substrait function maps to KQL output.
    /// </summary>
    enum KqlFunctionKind
    {
        /// <summary>Binary infix operator: arg0 OP arg1 (e.g., +, ==, and)</summary>
        InfixOperator,
        /// <summary>Unary prefix operator: OP arg0 (e.g., not)</summary>
        PrefixOperator,
        /// <summary>Named function call: func(args) (e.g., strcat, strlen)</summary>
        Function,
        /// <summary>Named aggregate function call: func(args) (e.g., sum, count)</summary>
        AggregateFunction,
        /// <summary>Special handling required (e.g., between, coalesce, is_null)</summary>
        Special,
    }

    /// <summary>
    /// Maps a Substrait function to its KQL representation.
    /// </summary>
    readonly struct KqlFunctionMapping
    {
        public readonly KqlFunctionKind Kind;
        /// <summary>
        /// The KQL operator/function name as UTF-8 bytes.
        /// For InfixOperator: the operator text (e.g., " + ", " == ").
        /// For Function: the function name (e.g., "strcat", "strlen").
        /// For Special: a key identifying the special handler.
        /// </summary>
        public readonly byte[] KqlName;

        public KqlFunctionMapping(KqlFunctionKind kind, byte[] kqlName)
        {
            Kind = kind;
            KqlName = kqlName;
        }

        public static KqlFunctionMapping Infix(string op) => new(KqlFunctionKind.InfixOperator, ToUtf8($" {op} "));
        public static KqlFunctionMapping Prefix(string op) => new(KqlFunctionKind.PrefixOperator, ToUtf8(op));
        public static KqlFunctionMapping Func(string name) => new(KqlFunctionKind.Function, ToUtf8(name));
        public static KqlFunctionMapping Agg(string name) => new(KqlFunctionKind.AggregateFunction, ToUtf8(name));
        public static KqlFunctionMapping Spec(string key) => new(KqlFunctionKind.Special, ToUtf8(key));

        static byte[] ToUtf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    }

    /// <summary>
    /// Maps standard Substrait function signatures to KQL equivalents.
    /// The function name (before the colon in the signature) is used as the
    /// lookup key — type-specific overloads map to the same KQL operation.
    /// </summary>
    static class KqlFunctionMap
    {
        static readonly Dictionary<string, KqlFunctionMapping> s_map = BuildMap();

        /// <summary>
        /// Tries to find a KQL mapping for the given Substrait function name.
        /// The name may be a bare name ("add") or a full signature ("add:i32_i32").
        /// </summary>
        public static bool TryGet(string functionName, out KqlFunctionMapping mapping)
        {
            // Try the full signature first, then the bare name (before ':')
            if (s_map.TryGetValue(functionName, out mapping))
                return true;

            int colon = functionName.IndexOf(':');
            if (colon > 0 && s_map.TryGetValue(functionName.Substring(0, colon), out mapping))
                return true;

            mapping = default;
            return false;
        }

        /// <summary>
        /// Tries to find a KQL mapping using a UTF-8 function name span,
        /// avoiding string allocation for the common case.
        /// </summary>
        public static bool TryGet(ReadOnlySpan<byte> utf8Name, out KqlFunctionMapping mapping)
        {
            // Find colon to extract bare name
            int colon = utf8Name.IndexOf((byte)':');
            ReadOnlySpan<byte> bareName = colon > 0 ? utf8Name.Slice(0, colon) : utf8Name;

            // We need to match against string keys, so we'll check known names
            // via a fast path for common short names.
            // For the general case, fall back to string conversion.
#if NETSTANDARD2_0
            string key = System.Text.Encoding.UTF8.GetString(bareName.ToArray());
#else
            string key = System.Text.Encoding.UTF8.GetString(bareName);
#endif
            return s_map.TryGetValue(key, out mapping);
        }

        static Dictionary<string, KqlFunctionMapping> BuildMap()
        {
            var m = new Dictionary<string, KqlFunctionMapping>(StringComparer.Ordinal);

            // ── Arithmetic ────────────────────────────────────────
            m["add"] = KqlFunctionMapping.Infix("+");
            m["subtract"] = KqlFunctionMapping.Infix("-");
            m["multiply"] = KqlFunctionMapping.Infix("*");
            m["divide"] = KqlFunctionMapping.Infix("/");
            m["modulus"] = KqlFunctionMapping.Infix("%");
            m["negate"] = KqlFunctionMapping.Prefix("-");
            m["abs"] = KqlFunctionMapping.Func("abs");
            m["sign"] = KqlFunctionMapping.Func("sign");
            m["power"] = KqlFunctionMapping.Func("pow");
            m["sqrt"] = KqlFunctionMapping.Func("sqrt");
            m["exp"] = KqlFunctionMapping.Func("exp");
            m["ln"] = KqlFunctionMapping.Func("log");
            m["log10"] = KqlFunctionMapping.Func("log10");
            m["log2"] = KqlFunctionMapping.Func("log2");
            m["ceil"] = KqlFunctionMapping.Func("ceiling");
            m["floor"] = KqlFunctionMapping.Func("floor");
            m["round"] = KqlFunctionMapping.Func("round");

            // ── Comparison ────────────────────────────────────────
            m["equal"] = KqlFunctionMapping.Infix("==");
            m["not_equal"] = KqlFunctionMapping.Infix("!=");
            m["lt"] = KqlFunctionMapping.Infix("<");
            m["lte"] = KqlFunctionMapping.Infix("<=");
            m["gt"] = KqlFunctionMapping.Infix(">");
            m["gte"] = KqlFunctionMapping.Infix(">=");
            m["is_null"] = KqlFunctionMapping.Spec("is_null");
            m["is_not_null"] = KqlFunctionMapping.Spec("is_not_null");
            m["between"] = KqlFunctionMapping.Spec("between");
            m["coalesce"] = KqlFunctionMapping.Func("coalesce");
            m["is_nan"] = KqlFunctionMapping.Func("isnan");
            m["is_finite"] = KqlFunctionMapping.Func("isfinite");
            m["is_infinite"] = KqlFunctionMapping.Func("isinf");
            m["is_not_nan"] = KqlFunctionMapping.Spec("is_not_nan");
            m["nullif"] = KqlFunctionMapping.Spec("nullif");

            // ── Boolean ───────────────────────────────────────────
            m["and"] = KqlFunctionMapping.Infix("and");
            m["or"] = KqlFunctionMapping.Infix("or");
            m["not"] = KqlFunctionMapping.Prefix("not ");
            m["xor"] = KqlFunctionMapping.Spec("xor");

            // ── String ────────────────────────────────────────────
            m["concat"] = KqlFunctionMapping.Func("strcat");
            m["like"] = KqlFunctionMapping.Spec("like");
            m["substring"] = KqlFunctionMapping.Func("substring");
            m["char_length"] = KqlFunctionMapping.Func("strlen");
            m["string_length"] = KqlFunctionMapping.Func("strlen");
            m["upper"] = KqlFunctionMapping.Func("toupper");
            m["lower"] = KqlFunctionMapping.Func("tolower");
            m["trim"] = KqlFunctionMapping.Func("trim");
            m["ltrim"] = KqlFunctionMapping.Func("trim_start");
            m["rtrim"] = KqlFunctionMapping.Func("trim_end");
            m["replace"] = KqlFunctionMapping.Func("replace_string");
            m["starts_with"] = KqlFunctionMapping.Spec("starts_with");
            m["ends_with"] = KqlFunctionMapping.Spec("ends_with");
            m["contains"] = KqlFunctionMapping.Spec("contains");
            m["string_concat"] = KqlFunctionMapping.Func("strcat");
            m["regexp_match"] = KqlFunctionMapping.Spec("regexp_match");

            // ── Datetime ──────────────────────────────────────────
            m["extract"] = KqlFunctionMapping.Spec("extract");

            // ── Aggregate ─────────────────────────────────────────
            m["count"] = KqlFunctionMapping.Agg("count");
            m["sum"] = KqlFunctionMapping.Agg("sum");
            m["min"] = KqlFunctionMapping.Agg("min");
            m["max"] = KqlFunctionMapping.Agg("max");
            m["avg"] = KqlFunctionMapping.Agg("avg");
            m["any_value"] = KqlFunctionMapping.Agg("any");
            m["count_star"] = KqlFunctionMapping.Spec("count_star");
            m["approx_count_distinct"] = KqlFunctionMapping.Agg("dcount");
            m["std_dev"] = KqlFunctionMapping.Agg("stdev");
            m["variance"] = KqlFunctionMapping.Agg("variance");

            // ── Cast ──────────────────────────────────────────────
            m["cast"] = KqlFunctionMapping.Spec("cast");

            return m;
        }
    }
}
