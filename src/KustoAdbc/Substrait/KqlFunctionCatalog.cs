// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using F = KustoAdbc.Substrait.SubstraitFields;

namespace KustoAdbc.Substrait
{
    enum KqlFunctionKind
    {
        Scalar,
        Aggregate,
    }

    /// <summary>
    /// A set of implementations of a function that share the same option behavior.
    /// Each variant corresponds to one entry in the dialect file's
    /// supported_scalar_functions / supported_aggregate_functions lists.
    /// </summary>
    sealed class KqlFunctionVariant
    {
        public KqlFunctionVariant(string[] impls, params (string Name, string Value)[] options)
        {
            Impls = impls;
            Options = options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);
        }

        /// <summary>Supported argument signatures, e.g. "i64_i64".</summary>
        public IReadOnlyList<string> Impls { get; }

        /// <summary>For each option, the behavior the translation delivers.</summary>
        public IReadOnlyDictionary<string, string> Options { get; }

        /// <summary>Minimum argument count when the last argument is variadic; null if not variadic.</summary>
        public int? VariadicMin { get; init; }

        /// <summary>Maximum argument count for variadic functions, if limited.</summary>
        public int? VariadicMax { get; init; }
    }

    /// <summary>A Substrait function the driver can translate, and how.</summary>
    sealed class KqlFunction
    {
        public KqlFunction(string urn, string name, KqlFunctionKind kind, string systemName, string notation,
            KqlFunctionVariant[] variants, Func<SType[], SType> returnType, Action<KqlCall> write)
        {
            Urn = urn;
            Name = name;
            Kind = kind;
            SystemName = systemName;
            Notation = notation;
            Variants = variants;
            ReturnType = returnType;
            Write = write;
        }

        public string Urn { get; }
        public string Name { get; }
        public KqlFunctionKind Kind { get; }
        /// <summary>The KQL function or operator this maps to (dialect system_metadata.name).</summary>
        public string SystemName { get; }
        /// <summary>INFIX, PREFIX or FUNCTION (dialect system_metadata.notation).</summary>
        public string Notation { get; }
        public IReadOnlyList<KqlFunctionVariant> Variants { get; }
        public Func<SType[], SType> ReturnType { get; }
        public Action<KqlCall> Write { get; }
    }

    /// <summary>A resolved call being written: arguments, their types and the chosen options.</summary>
    sealed class KqlCall
    {
        readonly KqlEmitter _e;
        readonly IReadOnlyList<KqlColumn> _scope;

        public KqlCall(KqlEmitter emitter, IReadOnlyList<KqlColumn> scope, string displayName, List<FunctionArg> args,
            SType[] argTypes, IReadOnlyDictionary<string, string> options, SType resultType, Expr? filter, bool distinct,
            bool predicate = false)
        {
            _e = emitter;
            _scope = scope;
            DisplayName = displayName;
            Args = args;
            ArgTypes = argTypes;
            Options = options;
            ResultType = resultType;
            Filter = filter;
            Distinct = distinct;
            Predicate = predicate;
        }

        /// <summary>A view of <paramref name="source"/> without its first <paramref name="skip"/> arguments.</summary>
        public KqlCall(KqlCall source, int skip)
            : this(source._e, source._scope, source.DisplayName, source.Args.Skip(skip).ToList(), source.ArgTypes.Skip(skip).ToArray(),
                  source.Options, source.ResultType, source.Filter, source.Distinct, source.Predicate)
        {
        }

        /// <summary>
        /// True when the call's boolean result only decides whether a row is kept (a where clause,
        /// a measure filter, a case condition, or and/or operands of one). There, null and false
        /// have the same effect, so null-propagation guards can be left out.
        /// </summary>
        public bool Predicate { get; }

        /// <summary>Writes argument <paramref name="index"/> in the same (predicate or value) context as the call.</summary>
        public void ArgInContext(int index)
        {
            var value = index < Args.Count ? Args[index].Value : null;
            if (value == null)
                throw SubstraitTranslationException.MalformedPlan($"Argument {index} of '{DisplayName}' must be a value.");
            _e.WriteExpr(value, _scope, Predicate);
        }

        /// <summary>True when argument <paramref name="index"/> may evaluate to null in Kusto.</summary>
        public bool MayBeNull(int index)
            => index < Args.Count && Args[index].Value != null && _e.MayBeNull(Args[index].Value!, _scope);

        /// <summary>
        /// Writes == or != with Substrait null semantics. Null is sticky for other Kusto operators,
        /// but with one null operand == gives false and != gives true, so the result is forced to null
        /// when an operand is null. A false result is harmless in a predicate (<paramref name="falseOnNull"/>),
        /// where the guard is left out; a true one never is.
        /// </summary>
        public void NullPropagating(Action write, bool falseOnNull)
        {
            var nullable = Enumerable.Range(0, Args.Count).Where(MayBeNull).ToList();
            if ((Predicate && falseOnNull) || nullable.Count == 0)
            {
                write();
                return;
            }
            W("iif(");
            for (int i = 0; i < nullable.Count; i++)
            {
                if (i > 0) W(" or ");
                W("isnull(");
                Arg(nullable[i]);
                W(")");
            }
            W(", bool(null), ");
            write();
            W(")");
        }

        public string DisplayName { get; }
        public List<FunctionArg> Args { get; }
        public SType[] ArgTypes { get; }
        public IReadOnlyDictionary<string, string> Options { get; }
        public SType ResultType { get; }
        /// <summary>The measure's filter, for aggregate calls.</summary>
        public Expr? Filter { get; }
        /// <summary>True for DISTINCT aggregate invocations.</summary>
        public bool Distinct { get; }

        public int Count => Args.Count;

        public void W(string text) => _e.Write(text);

        public void Arg(int index)
        {
            var value = index < Args.Count ? Args[index].Value : null;
            if (value == null)
                throw SubstraitTranslationException.MalformedPlan($"Argument {index} of '{DisplayName}' must be a value.");
            _e.WriteExpr(value, _scope);
        }

        public void ArgList(int from = 0)
        {
            for (int i = from; i < Args.Count; i++)
            {
                if (i > from) W(", ");
                Arg(i);
            }
        }

        public string EnumArg(int index)
        {
            var value = index < Args.Count ? Args[index].Enum : null;
            return value ?? throw SubstraitTranslationException.MalformedPlan($"Argument {index} of '{DisplayName}' must be an enumeration.");
        }

        public bool IsStringArg(int index) => index < ArgTypes.Length && ArgTypes[index].IsStringLike;

        public string? StringLiteralArg(int index)
            => index < Args.Count && Args[index].Value is LiteralExpr { IsNull: false } lit && lit.Type.IsStringLike ? lit.StringValue : null;

        public void WriteFilter()
        {
            if (Filter == null)
                throw new InvalidOperationException("Call has no filter.");
            _e.WriteExpr(Filter, _scope, predicate: true);
        }

        public void TypedNull(SType type) => _e.WriteTypedNull(type);

        /// <summary>Writes a test for a non-null value (strings: non-empty, since Kusto strings can't be null).</summary>
        public void NotNull(int index)
        {
            W(IsStringArg(index) ? "isnotempty(" : "isnotnull(");
            Arg(index);
            W(")");
        }

        public void IsNull(int index)
        {
            W(IsStringArg(index) ? "isempty(" : "isnull(");
            Arg(index);
            W(")");
        }

        public void Infix(string op)
        {
            W("(");
            Arg(0);
            W(" ");
            W(op);
            W(" ");
            Arg(1);
            W(")");
        }

        public void Call(string name)
        {
            W(name);
            W("(");
            ArgList();
            W(")");
        }

        /// <summary>
        /// KQL promotes int arithmetic to long, so an i32 result is converted back. KQL functions
        /// that return real for integer input (<paramref name="returnsReal"/>) are converted to i64 too.
        /// </summary>
        public void Typed(Action write, bool returnsReal = false)
        {
            string? conversion = ResultType.Kind switch
            {
                TypeKind.I32 => "toint(",
                TypeKind.I64 when returnsReal => "tolong(",
                _ => null,
            };
            if (conversion != null) W(conversion);
            write();
            if (conversion != null) W(")");
        }

        public SubstraitTranslationException Unsupported(string detail)
            => SubstraitTranslationException.Unsupported($"{DisplayName}: {detail}");
    }

    /// <summary>
    /// The Substrait functions this driver translates to KQL. This catalog and the
    /// dialect file (Dialect/kusto_dialect.yaml) describe the same set of functions,
    /// implementations and options; a unit test keeps them in sync.
    /// </summary>
    static class KqlFunctionCatalog
    {
        public const string ArithmeticUrn = "extension:io.substrait:functions_arithmetic";
        public const string ComparisonUrn = "extension:io.substrait:functions_comparison";
        public const string BooleanUrn = "extension:io.substrait:functions_boolean";
        public const string StringUrn = "extension:io.substrait:functions_string";
        public const string DatetimeUrn = "extension:io.substrait:functions_datetime";
        public const string RoundingUrn = "extension:io.substrait:functions_rounding";
        public const string LogarithmicUrn = "extension:io.substrait:functions_logarithmic";
        public const string AggregateGenericUrn = "extension:io.substrait:functions_aggregate_generic";
        public const string AggregateApproxUrn = "extension:io.substrait:functions_aggregate_approx";

        /// <summary>Kusto decimals behave like .NET System.Decimal: 28 significant digits.</summary>
        public const int MaxDecimalPrecision = 28;

        /// <summary>KQL functions such as strcat and coalesce accept at most 64 arguments.</summary>
        const int MaxKqlArguments = 64;

        public static IReadOnlyList<KqlFunction> All { get; } = Build();

        static readonly Dictionary<string, List<KqlFunction>> s_byName = All
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        public static bool IsBooleanFunction(FunctionDecl decl) => decl.Urn == null || decl.Urn == BooleanUrn;

        public static bool IsComparisonFunction(FunctionDecl decl) => decl.Urn == null || decl.Urn == ComparisonUrn;

        #region Resolution

        public static void WriteScalar(KqlEmitter e, ScalarFunctionExpr sf, IReadOnlyList<KqlColumn> scope, bool predicate)
        {
            var argTypes = ArgTypes(e, sf.Args, scope);
            var (fn, options) = Resolve(sf.Function, KqlFunctionKind.Scalar, sf.Args, argTypes, sf.Options);
            var resultType = sf.OutputType ?? fn.ReturnType(argTypes);
            fn.Write(new KqlCall(e, scope, sf.Function.DisplayName, sf.Args, argTypes, options, resultType, null, false, predicate));
        }

        public static SType InferScalarType(KqlEmitter e, ScalarFunctionExpr sf, IReadOnlyList<KqlColumn> scope)
        {
            var argTypes = ArgTypes(e, sf.Args, scope);
            try
            {
                var (fn, _) = Resolve(sf.Function, KqlFunctionKind.Scalar, sf.Args, argTypes, sf.Options);
                return fn.ReturnType(argTypes);
            }
            catch (SubstraitTranslationException)
            {
                // The error is reported when the expression is written.
                return SType.Unknown;
            }
        }

        /// <summary>Writes an aggregate measure and returns its result type.</summary>
        public static SType WriteAggregate(KqlEmitter e, Measure measure, IReadOnlyList<KqlColumn> scope)
        {
            var call = measure.Call;
            var argTypes = ArgTypes(e, call.Args, scope);
            var (fn, options) = Resolve(call.Function, KqlFunctionKind.Aggregate, call.Args, argTypes, call.Options);
            var resultType = call.OutputType ?? fn.ReturnType(argTypes);
            bool distinct = call.Invocation == F.AggregationInvocation_Distinct;
            fn.Write(new KqlCall(e, scope, call.Function.DisplayName, call.Args, argTypes, options, resultType, measure.Filter, distinct));
            return resultType;
        }

        static SType[] ArgTypes(KqlEmitter e, List<FunctionArg> args, IReadOnlyList<KqlColumn> scope)
        {
            var types = new SType[args.Count];
            for (int i = 0; i < args.Count; i++)
                types[i] = args[i].Value != null ? e.InferType(args[i].Value!, scope) : SType.Unknown;
            return types;
        }

        static (KqlFunction Function, IReadOnlyDictionary<string, string> Options) Resolve(
            FunctionDecl decl, KqlFunctionKind kind, List<FunctionArg> args, SType[] argTypes, List<FunctionOptionValue> options)
        {
            if (!s_byName.TryGetValue(decl.Name, out var named))
                throw SubstraitTranslationException.UnsupportedFunction(decl.DisplayName);

            // Plans that don't name the extension (some producers omit URNs) are matched by name and signature.
            var functions = named.Where(f => f.Kind == kind && (decl.Urn == null || f.Urn == decl.Urn)).ToList();
            if (functions.Count == 0)
                throw SubstraitTranslationException.UnsupportedFunction(decl.Urn == null ? decl.DisplayName : $"{decl.DisplayName} ({decl.Urn})");

            string[]? callTokens = decl.Signature != null ? SplitSignature(decl.Signature) : InferTokens(args, argTypes);
            var candidates = new List<(KqlFunction Fn, KqlFunctionVariant Variant)>();
            foreach (var fn in functions)
            {
                foreach (var variant in fn.Variants)
                {
                    if (callTokens == null || variant.Impls.Any(impl => SignatureMatches(callTokens, SplitSignature(impl), variant.VariadicMin.HasValue)))
                        candidates.Add((fn, variant));
                }
            }
            if (candidates.Count == 0)
            {
                string sig = callTokens == null ? "" : ":" + string.Join("_", callTokens);
                throw SubstraitTranslationException.UnsupportedFunction($"{decl.Name}{sig}");
            }

            // A requested option must be honored with the first preference the driver can deliver.
            foreach (var option in options)
            {
                if (option.Preference.Count == 0)
                    continue;
                string? chosen = option.Preference.FirstOrDefault(p =>
                    candidates.Any(c => c.Variant.Options.TryGetValue(option.Name, out var v) && v == p));
                if (chosen == null)
                    throw SubstraitTranslationException.UnsupportedOption(decl.DisplayName, option.Name, option.Preference);
                candidates = candidates.Where(c => c.Variant.Options.TryGetValue(option.Name, out var v) && v == chosen).ToList();
            }

            var (function, chosenVariant) = candidates[0];
            if (chosenVariant.VariadicMax.HasValue && args.Count > chosenVariant.VariadicMax.Value)
                throw SubstraitTranslationException.Unsupported($"{decl.DisplayName}: at most {chosenVariant.VariadicMax} arguments are supported.");
            return (function, chosenVariant.Options);
        }

        static string[] SplitSignature(string signature)
            => signature.Length == 0 ? Array.Empty<string>() : signature.Split('_');

        static string[]? InferTokens(List<FunctionArg> args, SType[] argTypes)
        {
            var tokens = new string[args.Count];
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Enum != null)
                {
                    tokens[i] = "req";
                    continue;
                }
                var token = argTypes[i].SignatureToken;
                if (token == null)
                    return null;
                tokens[i] = token;
            }
            return tokens;
        }

        static bool SignatureMatches(string[] call, string[] impl, bool variadic)
        {
            if (variadic)
            {
                if (impl.Length == 0 || call.Length < impl.Length - 1)
                    return false;
            }
            else if (call.Length != impl.Length)
            {
                return false;
            }

            for (int i = 0; i < call.Length; i++)
            {
                string expected = i < impl.Length ? impl[i] : impl[impl.Length - 1];
                if (expected != "any" && expected != call[i])
                    return false;
            }
            return true;
        }

        #endregion

        #region Catalog

        static SType SameAsFirst(SType[] t) => t.Length > 0 ? t[0] : SType.Unknown;
        static SType Bool(SType[] _) => SType.Bool;
        static SType Real(SType[] _) => SType.Fp64;
        static SType Long(SType[] _) => SType.I64;
        static SType Str(SType[] _) => SType.String;

        static KqlFunctionVariant V(params string[] impls) => new(impls);

        static List<KqlFunction> Build()
        {
            // Locals rather than static fields: Build() runs during static initialization.
            string[] Ints = { "i32_i32", "i64_i64" };
            string[] IntsAndReal = { "i32_i32", "i64_i64", "fp64_fp64" };
            string[] UnaryNumeric = { "i32", "i64", "fp64" };
            var list = new List<KqlFunction>();

            void Scalar(string urn, string name, string kql, string notation, Func<SType[], SType> ret, Action<KqlCall> write, params KqlFunctionVariant[] variants)
                => list.Add(new KqlFunction(urn, name, KqlFunctionKind.Scalar, kql, notation, variants, ret, write));

            void Aggregate(string urn, string name, string kql, Func<SType[], SType> ret, Action<KqlCall> write, params KqlFunctionVariant[] variants)
                => list.Add(new KqlFunction(urn, name, KqlFunctionKind.Aggregate, kql, "FUNCTION", variants, ret, write));

            // ── functions_arithmetic: scalar ────────────────────────────────
            // Behaviors observed on a cluster (help.kusto.windows.net, 2026-10-20): long arithmetic wraps
            // on overflow, and so does toint() of an out-of-range long (used for i32 results); integer
            // division and remainder by zero return null.
            Scalar(ArithmeticUrn, "add", "+", "INFIX", SameAsFirst, c => c.Typed(() => c.Infix("+")),
                new KqlFunctionVariant(Ints, ("overflow", "SILENT")), V("fp64_fp64"));
            Scalar(ArithmeticUrn, "subtract", "-", "INFIX", SameAsFirst, c => c.Typed(() => c.Infix("-")),
                new KqlFunctionVariant(Ints, ("overflow", "SILENT")), V("fp64_fp64"));
            Scalar(ArithmeticUrn, "multiply", "*", "INFIX", SameAsFirst, c => c.Typed(() => c.Infix("*")),
                new KqlFunctionVariant(Ints, ("overflow", "SILENT")), V("fp64_fp64"));
            Scalar(ArithmeticUrn, "divide", "/", "INFIX", SameAsFirst, c => c.Typed(() => c.Infix("/")),
                new KqlFunctionVariant(Ints, ("on_division_by_zero", "NULL")),
                new KqlFunctionVariant(new[] { "fp64_fp64" }, ("on_division_by_zero", "IEEE"), ("on_domain_error", "NAN")));
            // KQL's % is not the truncating remainder, so it is computed from truncating division.
            Scalar(ArithmeticUrn, "modulus", "%", "INFIX", SameAsFirst, c => c.Typed(() =>
            {
                c.W("(");
                c.Arg(0);
                c.W(" - ");
                c.Arg(1);
                c.W(" * (");
                c.Arg(0);
                c.W(" / ");
                c.Arg(1);
                c.W("))");
            }), new KqlFunctionVariant(Ints, ("division_type", "TRUNCATE"), ("on_domain_error", "NULL")));
            Scalar(ArithmeticUrn, "negate", "-", "PREFIX", SameAsFirst, c => c.Typed(() => { c.W("-("); c.Arg(0); c.W(")"); }), V(UnaryNumeric));
            Scalar(ArithmeticUrn, "abs", "abs", "FUNCTION", SameAsFirst, c => c.Typed(() => c.Call("abs")), V(UnaryNumeric));
            Scalar(ArithmeticUrn, "sign", "sign", "FUNCTION", SameAsFirst, c => c.Typed(() => c.Call("sign"), returnsReal: true), V(UnaryNumeric));
            Scalar(ArithmeticUrn, "power", "pow", "FUNCTION", Real, c => c.Call("pow"), V("fp64_fp64"));
            Scalar(ArithmeticUrn, "sqrt", "sqrt", "FUNCTION", Real, c => c.Call("sqrt"),
                new KqlFunctionVariant(new[] { "i64", "fp64" }, ("on_domain_error", "NAN")));
            Scalar(ArithmeticUrn, "exp", "exp", "FUNCTION", Real, c => c.Call("exp"), V("i64", "fp64"));
            foreach (var trig in new[] { "sin", "cos", "tan", "asin", "acos", "atan", "degrees", "radians" })
                Scalar(ArithmeticUrn, trig, trig, "FUNCTION", Real, c => c.Call(trig), V("fp64"));
            Scalar(ArithmeticUrn, "bitwise_and", "binary_and", "FUNCTION", SameAsFirst, c => c.Call("binary_and"), V("i64_i64"));
            Scalar(ArithmeticUrn, "bitwise_or", "binary_or", "FUNCTION", SameAsFirst, c => c.Call("binary_or"), V("i64_i64"));
            Scalar(ArithmeticUrn, "bitwise_xor", "binary_xor", "FUNCTION", SameAsFirst, c => c.Call("binary_xor"), V("i64_i64"));
            Scalar(ArithmeticUrn, "bitwise_not", "binary_not", "FUNCTION", SameAsFirst, c => c.Call("binary_not"), V("i64"));

            // ── functions_arithmetic: aggregate ─────────────────────────────
            static SType SumType(SType[] t) => t.Length > 0 && t[0].Kind == TypeKind.Fp64 ? SType.Fp64 : SType.I64;
            Aggregate(ArithmeticUrn, "sum", "sum", SumType, c => GuardedAggregate(c, "sum", "sumif"), V(UnaryNumeric));
            Aggregate(ArithmeticUrn, "sum0", "sum", t => SumType(t).WithNullable(false), c => PlainAggregate(c, "sum", "sumif"), V(UnaryNumeric));
            Aggregate(ArithmeticUrn, "avg", "avg", Real, c => GuardedAggregate(c, "avg", "avgif"), V("fp64"));
            Aggregate(ArithmeticUrn, "min", "min", SameAsFirst, c => GuardedAggregate(c, "min", "minif"), V(UnaryNumeric));
            Aggregate(ArithmeticUrn, "max", "max", SameAsFirst, c => GuardedAggregate(c, "max", "maxif"), V(UnaryNumeric));
            Aggregate(ArithmeticUrn, "std_dev", "stdev", Real, c => Dispersion(c, "stdev", "stdevif", "stdevp", null),
                V("req_fp64"),
                new KqlFunctionVariant(new[] { "fp64" }, ("distribution", "SAMPLE")),
                new KqlFunctionVariant(new[] { "fp64" }, ("distribution", "POPULATION")));
            Aggregate(ArithmeticUrn, "variance", "variance", Real, c => Dispersion(c, "variance", "varianceif", "variancep", "variancepif"),
                V("req_fp64"),
                new KqlFunctionVariant(new[] { "fp64" }, ("distribution", "SAMPLE")),
                new KqlFunctionVariant(new[] { "fp64" }, ("distribution", "POPULATION")));
            Aggregate(ArithmeticUrn, "median", "percentile", Real, Median, V("req_fp64"));

            // ── functions_comparison ────────────────────────────────────────
            Scalar(ComparisonUrn, "equal", "==", "INFIX", Bool, c => c.NullPropagating(() => c.Infix("=="), falseOnNull: true), V("any_any"));
            Scalar(ComparisonUrn, "not_equal", "!=", "INFIX", Bool, c => c.NullPropagating(() => c.Infix("!="), falseOnNull: false), V("any_any"));
            Scalar(ComparisonUrn, "lt", "<", "INFIX", Bool, c => Compare(c, "<"), V("any_any"));
            Scalar(ComparisonUrn, "lte", "<=", "INFIX", Bool, c => Compare(c, "<="), V("any_any"));
            Scalar(ComparisonUrn, "gt", ">", "INFIX", Bool, c => Compare(c, ">"), V("any_any"));
            Scalar(ComparisonUrn, "gte", ">=", "INFIX", Bool, c => Compare(c, ">="), V("any_any"));
            Scalar(ComparisonUrn, "between", "between", "INFIX", Bool, Between, V("any_any_any"));
            Scalar(ComparisonUrn, "is_null", "isnull", "FUNCTION", Bool, c => c.IsNull(0), V("any"));
            Scalar(ComparisonUrn, "is_not_null", "isnotnull", "FUNCTION", Bool, c => c.NotNull(0), V("any"));
            Scalar(ComparisonUrn, "is_nan", "isnan", "FUNCTION", Bool, c => c.Call("isnan"), V("fp64"));
            Scalar(ComparisonUrn, "is_finite", "isfinite", "FUNCTION", Bool, c => c.Call("isfinite"), V("fp64"));
            Scalar(ComparisonUrn, "is_infinite", "isinf", "FUNCTION", Bool, c => c.Call("isinf"), V("fp64"));
            Scalar(ComparisonUrn, "is_true", "coalesce", "FUNCTION", Bool, c => { c.W("coalesce("); c.Arg(0); c.W(", false)"); }, V("bool"));
            Scalar(ComparisonUrn, "is_not_true", "coalesce", "FUNCTION", Bool, c => { c.W("not(coalesce("); c.Arg(0); c.W(", false))"); }, V("bool"));
            Scalar(ComparisonUrn, "is_false", "coalesce", "FUNCTION", Bool, c => { c.W("coalesce(not("); c.Arg(0); c.W("), false)"); }, V("bool"));
            Scalar(ComparisonUrn, "is_not_false", "coalesce", "FUNCTION", Bool, c => { c.W("not(coalesce(not("); c.Arg(0); c.W("), false))"); }, V("bool"));
            Scalar(ComparisonUrn, "nullif", "iif", "FUNCTION", SameAsFirst, c =>
            {
                c.W("iif(");
                c.Infix("==");
                c.W(", ");
                c.TypedNull(c.ArgTypes[0]);
                c.W(", ");
                c.Arg(0);
                c.W(")");
            }, V("any_any"));
            Scalar(ComparisonUrn, "coalesce", "coalesce", "FUNCTION", SameAsFirst, c => c.Call("coalesce"),
                new KqlFunctionVariant(new[] { "any" }) { VariadicMin = 2, VariadicMax = MaxKqlArguments });
            Scalar(ComparisonUrn, "is_distinct_from", "iif", "FUNCTION", Bool, c => DistinctFrom(c, negate: false), V("any_any"));
            Scalar(ComparisonUrn, "is_not_distinct_from", "iif", "FUNCTION", Bool, c => DistinctFrom(c, negate: true), V("any_any"));

            // ── functions_boolean ───────────────────────────────────────────
            Scalar(BooleanUrn, "and", "and", "INFIX", Bool, c => Logical(c, "and", "true"), new KqlFunctionVariant(new[] { "bool" }) { VariadicMin = 0 });
            Scalar(BooleanUrn, "or", "or", "INFIX", Bool, c => Logical(c, "or", "false"), new KqlFunctionVariant(new[] { "bool" }) { VariadicMin = 0 });
            Scalar(BooleanUrn, "not", "not", "FUNCTION", Bool, c => c.Call("not"), V("bool"));
            Scalar(BooleanUrn, "and_not", "and", "INFIX", Bool, c => { c.W("("); c.Arg(0); c.W(" and not("); c.Arg(1); c.W("))"); }, V("bool_bool"));
            Scalar(BooleanUrn, "xor", "!=", "INFIX", Bool, c =>
            {
                c.W("iif(isnull(");
                c.Arg(0);
                c.W(") or isnull(");
                c.Arg(1);
                c.W("), bool(null), ");
                c.Infix("!=");
                c.W(")");
            }, V("bool_bool"));

            // ── functions_string ────────────────────────────────────────────
            // Kusto strings cannot be null: a missing value is the empty string, which strcat skips.
            Scalar(StringUrn, "concat", "strcat", "FUNCTION", Str, c => c.Call("strcat"),
                new KqlFunctionVariant(new[] { "str" }, ("null_handling", "IGNORE_NULLS")) { VariadicMin = 1, VariadicMax = MaxKqlArguments });
            Scalar(StringUrn, "like", "matches regex", "INFIX", Bool, Like,
                new KqlFunctionVariant(new[] { "str_str" }, ("case_sensitivity", "CASE_SENSITIVE")),
                new KqlFunctionVariant(new[] { "str_str" }, ("case_sensitivity", "CASE_INSENSITIVE")));
            Scalar(StringUrn, "substring", "substring", "FUNCTION", Str, Substring,
                new KqlFunctionVariant(new[] { "str_i32_i32", "str_i32" }, ("negative_start", "LEFT_OF_BEGINNING")));
            foreach (var (name, kql) in new[] { ("starts_with", "startswith"), ("ends_with", "endswith"), ("contains", "contains") })
            {
                // The plain KQL operators ignore case; the _cs forms are case-sensitive.
                Scalar(StringUrn, name, kql + "_cs", "INFIX", Bool,
                    c => c.Infix(c.Options.TryGetValue("case_sensitivity", out var cs) && cs == "CASE_INSENSITIVE" ? kql : kql + "_cs"),
                    new KqlFunctionVariant(new[] { "str_str" }, ("case_sensitivity", "CASE_SENSITIVE")),
                    new KqlFunctionVariant(new[] { "str_str" }, ("case_sensitivity", "CASE_INSENSITIVE")));
            }
            Scalar(StringUrn, "strpos", "indexof", "FUNCTION", Long, c => { c.W("(indexof("); c.ArgList(); c.W(") + 1)"); },
                new KqlFunctionVariant(new[] { "str_str" }, ("case_sensitivity", "CASE_SENSITIVE")));
            Scalar(StringUrn, "replace", "replace_string", "FUNCTION", Str, c => c.Call("replace_string"),
                new KqlFunctionVariant(new[] { "str_str_str" }, ("case_sensitivity", "CASE_SENSITIVE")));
            Scalar(StringUrn, "repeat", "strrep", "FUNCTION", Str, c => c.Call("strrep"), V("str_i64"));
            Scalar(StringUrn, "reverse", "reverse", "FUNCTION", Str, c => c.Call("reverse"), V("str"));
            Scalar(StringUrn, "lower", "tolower", "FUNCTION", Str, c => c.Call("tolower"), new KqlFunctionVariant(new[] { "str" }, ("char_set", "UTF8")));
            Scalar(StringUrn, "upper", "toupper", "FUNCTION", Str, c => c.Call("toupper"), new KqlFunctionVariant(new[] { "str" }, ("char_set", "UTF8")));
            Scalar(StringUrn, "char_length", "strlen", "FUNCTION", Long, c => c.Call("strlen"), V("str"));
            Scalar(StringUrn, "ltrim", "trim_start", "FUNCTION", Str, c => Trim(c, "trim_start"), V("str_str"));
            Scalar(StringUrn, "rtrim", "trim_end", "FUNCTION", Str, c => Trim(c, "trim_end"), V("str_str"));
            Scalar(StringUrn, "trim", "trim", "FUNCTION", Str, c => Trim(c, "trim"), V("str_str"));

            // ── functions_datetime ──────────────────────────────────────────
            Scalar(DatetimeUrn, "extract", "datetime_part", "FUNCTION", Long, Extract,
                V("req_pts", "req_ptstz_str", "req_req_pts", "req_req_ptstz_str"));
            Scalar(DatetimeUrn, "add", "+", "INFIX", SameAsFirst, c => c.Infix("+"), V("pts_iday", "ptstz_iday"));
            Scalar(DatetimeUrn, "subtract", "-", "INFIX", SameAsFirst, c => c.Infix("-"), V("pts_iday", "ptstz_iday"));
            Scalar(DatetimeUrn, "multiply", "*", "INFIX", t => t.Length > 1 ? t[1] : SType.Unknown, c => c.Infix("*"), V("i32_iday", "i64_iday"));
            Scalar(DatetimeUrn, "add_intervals", "+", "INFIX", SameAsFirst, c => c.Infix("+"), V("iday_iday"));
            foreach (var (name, op) in new[] { ("lt", "<"), ("lte", "<="), ("gt", ">"), ("gte", ">=") })
                Scalar(DatetimeUrn, name, op, "INFIX", Bool, c => c.Infix(op), V("pts_pts", "ptstz_ptstz", "iday_iday"));
            Aggregate(DatetimeUrn, "min", "min", SameAsFirst, c => GuardedAggregate(c, "min", "minif"), V("pts", "ptstz", "iday"));
            Aggregate(DatetimeUrn, "max", "max", SameAsFirst, c => GuardedAggregate(c, "max", "maxif"), V("pts", "ptstz", "iday"));

            // ── functions_rounding ──────────────────────────────────────────
            Scalar(RoundingUrn, "ceil", "ceiling", "FUNCTION", Real, c => c.Call("ceiling"), V("fp64"));
            Scalar(RoundingUrn, "floor", "floor", "FUNCTION", Real, c => { c.W("floor("); c.Arg(0); c.W(", 1.0)"); }, V("fp64"));
            // round() breaks ties away from zero (observed: 2.5 → 3, -2.5 → -3, 0.125 → 0.13) and returns real.
            Scalar(RoundingUrn, "round", "round", "FUNCTION", SameAsFirst, c => c.Typed(() => c.Call("round"), returnsReal: true),
                new KqlFunctionVariant(new[] { "i32_i32", "i64_i32", "fp64_i32" }, ("rounding", "TIE_AWAY_FROM_ZERO")));

            // ── functions_logarithmic ───────────────────────────────────────
            // Observed: log of 0 is -infinity; of a negative number, null.
            foreach (var (name, kql) in new[] { ("ln", "log"), ("log10", "log10"), ("log2", "log2") })
            {
                Scalar(LogarithmicUrn, name, kql, "FUNCTION", Real, c => c.Call(kql),
                    new KqlFunctionVariant(new[] { "i64", "fp64" }, ("on_domain_error", "NULL"), ("on_log_zero", "MINUS_INFINITY")));
            }

            // ── functions_aggregate_generic / functions_aggregate_approx ────
            Aggregate(AggregateGenericUrn, "count", "count", t => SType.I64.WithNullable(false), Count, V("any", ""));
            Aggregate(AggregateGenericUrn, "any_value", "take_any", SameAsFirst, c => PlainAggregate(c, "take_any", "take_anyif"), V("any"));
            Aggregate(AggregateApproxUrn, "approx_count_distinct", "dcount", t => SType.I64.WithNullable(false), c =>
            {
                c.W("dcountif(");
                c.Arg(0);
                c.W(", ");
                WriteNotNullAndFilter(c);
                c.W(")");
            }, V("any"));

            return list;
        }

        #endregion

        #region Writers

        static void RejectUnordered(KqlCall c)
        {
            // KQL defines no ordering on guid or bool values.
            if (c.ArgTypes.Any(t => t.Kind is TypeKind.Uuid or TypeKind.Bool))
                throw c.Unsupported("ordering comparisons on uuid or boolean values are not supported.");
        }

        static void Compare(KqlCall c, string op)
        {
            // Null propagates through Kusto's ordering operators, as in Substrait.
            RejectUnordered(c);
            // KQL doesn't define < and > on strings; strcmp gives an ordinal comparison.
            if (c.IsStringArg(0) || c.IsStringArg(1))
            {
                c.W("(strcmp(");
                c.ArgList();
                c.W(") ");
                c.W(op);
                c.W(" 0)");
                return;
            }
            c.Infix(op);
        }

        static void Between(KqlCall c)
        {
            // Substrait's between is inclusive on both ends and false when low > high.
            RejectUnordered(c);
            bool strings = c.IsStringArg(0);
            c.W("(");
            WriteComparison(c, 0, ">=", 1, strings);
            c.W(" and ");
            WriteComparison(c, 0, "<=", 2, strings);
            c.W(")");
        }

        static void WriteComparison(KqlCall c, int left, string op, int right, bool strings)
        {
            if (strings)
            {
                c.W("(strcmp(");
                c.Arg(left);
                c.W(", ");
                c.Arg(right);
                c.W(") ");
                c.W(op);
                c.W(" 0)");
            }
            else
            {
                c.W("(");
                c.Arg(left);
                c.W(" ");
                c.W(op);
                c.W(" ");
                c.Arg(right);
                c.W(")");
            }
        }

        static void DistinctFrom(KqlCall c, bool negate)
        {
            // Null-safe comparison: two nulls are not distinct; a null and a value are.
            c.W("iif(");
            c.IsNull(0);
            c.W(" or ");
            c.IsNull(1);
            c.W(", ");
            if (!negate) c.W("not(");
            c.IsNull(0);
            c.W(" and ");
            c.IsNull(1);
            if (!negate) c.W(")");
            c.W(", ");
            c.Infix(negate ? "==" : "!=");
            c.W(")");
        }

        static void Logical(KqlCall c, string op, string identity)
        {
            if (c.Count == 0)
            {
                c.W(identity);
                return;
            }
            // Kusto's and/or are three-valued like Substrait's, so a predicate context carries through.
            c.W("(");
            for (int i = 0; i < c.Count; i++)
            {
                if (i > 0) { c.W(" "); c.W(op); c.W(" "); }
                c.ArgInContext(i);
            }
            c.W(")");
        }

        static void Like(KqlCall c)
        {
            string? pattern = c.StringLiteralArg(1);
            if (pattern == null)
                throw c.Unsupported("the pattern must be a string literal.");
            // The standard 'like' defines no escape character; a backslash would be ambiguous.
            if (pattern.IndexOf('\\') >= 0)
                throw c.Unsupported("patterns containing a backslash are not supported (the escape convention is undefined).");

            var regex = new StringBuilder("(?s)");
            if (c.Options.TryGetValue("case_sensitivity", out var cs) && cs == "CASE_INSENSITIVE")
                regex.Append("(?i)");
            regex.Append('^');
            foreach (char ch in pattern)
            {
                if (ch == '%') regex.Append(".*");
                else if (ch == '_') regex.Append('.');
                else AppendRegexLiteral(regex, ch);
            }
            regex.Append('$');

            c.W("(");
            c.Arg(0);
            c.W(" matches regex ");
            c.W(QuoteString(regex.ToString()));
            c.W(")");
        }

        static void AppendRegexLiteral(StringBuilder regex, char ch)
        {
            if ("\\.^$|?*+()[]{}".IndexOf(ch) >= 0)
                regex.Append('\\');
            regex.Append(ch);
        }

        static string QuoteString(string value)
        {
            var w = new Utf8KqlWriter();
            w.WriteStringLiteral(value);
            return w.ToString();
        }

        static void Substring(KqlCall c)
        {
            // Substrait positions are 1-based; a start before the first character
            // (LEFT_OF_BEGINNING) shortens the result rather than wrapping.
            bool hasLength = c.Count >= 3;
            if (c.Args[1].Value is LiteralExpr { IsNull: false } start && start.IntValue >= 1)
            {
                c.W("substring(");
                c.Arg(0);
                c.W(", ");
                c.W((start.IntValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (hasLength)
                {
                    c.W(", ");
                    c.Arg(2);
                }
                c.W(")");
                return;
            }

            c.W("substring(");
            c.Arg(0);
            c.W(", max_of(");
            c.Arg(1);
            c.W(" - 1, 0)");
            if (hasLength)
            {
                c.W(", max_of(");
                c.Arg(2);
                c.W(" + min_of(");
                c.Arg(1);
                c.W(" - 1, 0), 0)");
            }
            c.W(")");
        }

        static void Trim(KqlCall c, string kql)
        {
            // KQL's trim functions take a regular expression; the character set becomes a class.
            string? characters = c.StringLiteralArg(1);
            if (characters == null)
                throw c.Unsupported("the characters to trim must be a string literal.");
            if (characters.Length == 0)
            {
                c.Arg(0);
                return;
            }
            var regex = new StringBuilder("[");
            foreach (char ch in characters)
            {
                // Escape all ASCII punctuation: besides \ ] [ ^ -, some regex engines give && ~~ -- meaning in a class.
                if (ch < 128 && (char.IsPunctuation(ch) || char.IsSymbol(ch)))
                    regex.Append('\\');
                regex.Append(ch);
            }
            regex.Append("]+");
            c.W(kql);
            c.W("(");
            c.W(QuoteString(regex.ToString()));
            c.W(", ");
            c.Arg(0);
            c.W(")");
        }

        static readonly HashSet<string> s_utcNames = new(StringComparer.OrdinalIgnoreCase) { "UTC", "Etc/UTC", "GMT", "Z", "+00:00", "+0000" };

        static void Extract(KqlCall c)
        {
            string component = c.EnumArg(0);
            int valueIndex = 1;
            int indexing = 1;
            if (c.Args.Count > 1 && c.Args[1].Enum != null)
            {
                indexing = c.Args[1].Enum switch
                {
                    "ONE" => 1,
                    "ZERO" => 0,
                    _ => throw c.Unsupported($"unknown indexing '{c.Args[1].Enum}'."),
                };
                valueIndex = 2;
            }
            if (c.Count > valueIndex + 1)
            {
                // Kusto datetimes are UTC; only UTC can be honored without a time zone database.
                string? zone = c.StringLiteralArg(valueIndex + 1);
                if (zone == null || !s_utcNames.Contains(zone))
                    throw c.Unsupported("only the UTC time zone is supported.");
            }

            void Value() => c.Arg(valueIndex);
            void Part(string part, int offset = 0)
            {
                c.W("tolong(datetime_part('");
                c.W(part);
                c.W("', ");
                Value();
                c.W(")");
                if (offset != 0) c.W(offset > 0 ? " + " + offset : " - " + -offset);
                c.W(")");
            }

            switch (component)
            {
                case "YEAR": Part("year"); break;
                case "QUARTER": Part("quarter", indexing - 1); break;
                case "MONTH": Part("month", indexing - 1); break;
                case "DAY": Part("day", indexing - 1); break;
                case "DAY_OF_YEAR": Part("dayofyear", indexing - 1); break;
                case "ISO_WEEK": Part("week_of_year", indexing - 1); break;
                case "HOUR": Part("hour"); break;
                case "MINUTE": Part("minute"); break;
                case "SECOND": Part("second"); break;
                case "MILLISECOND": Part("millisecond"); break;
                case "SUBSECOND": Part("microsecond"); break;
                case "MICROSECOND":
                    // Substrait counts from the last full millisecond; datetime_part from the last full second.
                    c.W("(tolong(datetime_part('microsecond', ");
                    Value();
                    c.W(")) % 1000)");
                    break;
                case "NANOSECOND":
                    c.W("(tolong(datetime_part('nanosecond', ");
                    Value();
                    c.W(")) % 1000)");
                    break;
                case "SUNDAY_DAY_OF_WEEK":
                    // dayofweek() is the timespan since the preceding Sunday.
                    c.W("(tolong(dayofweek(");
                    Value();
                    c.W(") / 1d)");
                    if (indexing == 1) c.W(" + 1");
                    c.W(")");
                    break;
                case "MONDAY_DAY_OF_WEEK":
                    c.W("((tolong(dayofweek(");
                    Value();
                    c.W(") / 1d) + 6) % 7");
                    if (indexing == 1) c.W(" + 1");
                    c.W(")");
                    break;
                case "UNIX_TIME":
                    c.W("tolong(floor((");
                    Value();
                    c.W(" - datetime(1970-01-01)) / 1s, 1.0))");
                    break;
                default:
                    throw c.Unsupported($"extracting '{component}' is not supported.");
            }
        }

        static void Count(KqlCall c)
        {
            // count(<non-null literal>), as producers write COUNT(*), counts rows.
            bool countsRows = c.Count == 0 || (!c.Distinct && c.Args[0].Value is LiteralExpr { IsNull: false });
            if (countsRows)
            {
                if (c.Filter == null)
                {
                    c.W("count()");
                }
                else
                {
                    c.W("countif(");
                    c.WriteFilter();
                    c.W(")");
                }
                return;
            }

            // KQL's count() takes no column; count(x) must not count nulls.
            c.W(c.Distinct ? "count_distinctif(" : "countif(");
            if (c.Distinct)
            {
                c.Arg(0);
                c.W(", ");
            }
            WriteNotNullAndFilter(c);
            c.W(")");
        }

        static void WriteNotNullAndFilter(KqlCall c)
        {
            c.NotNull(0);
            if (c.Filter != null)
            {
                c.W(" and (");
                c.WriteFilter();
                c.W(")");
            }
        }

        static void RejectDistinct(KqlCall c)
        {
            if (c.Distinct)
                throw c.Unsupported("DISTINCT is only supported for count.");
        }

        /// <summary>Writes agg(x), or aggif(x, filter) when the measure is filtered.</summary>
        static void PlainAggregate(KqlCall c, string agg, string aggIf)
        {
            RejectDistinct(c);
            c.W(c.Filter == null ? agg : aggIf);
            c.W("(");
            c.Arg(0);
            if (c.Filter != null)
            {
                c.W(", ");
                c.WriteFilter();
            }
            c.W(")");
        }

        /// <summary>
        /// Kusto aggregates return 0 (sum) or NaN (avg) over no values, where Substrait
        /// returns null, so the result is replaced by null unless enough non-null inputs exist.
        /// </summary>
        static void GuardedAggregate(KqlCall c, string agg, string aggIf, int minimumCount = 1)
        {
            RejectDistinct(c);
            c.W("iif(countif(");
            WriteNotNullAndFilter(c);
            c.W(") < ");
            c.W(minimumCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            c.W(", ");
            c.TypedNull(c.ResultType);
            c.W(", ");
            PlainAggregate(c, agg, aggIf);
            c.W(")");
        }

        static void Dispersion(KqlCall c, string sample, string sampleIf, string population, string? populationIf)
        {
            // The distribution is an enumeration argument (current signature) or an option (deprecated one).
            string distribution;
            int valueIndex;
            if (c.Args.Count > 0 && c.Args[0].Enum != null)
            {
                distribution = c.Args[0].Enum!;
                valueIndex = 1;
            }
            else
            {
                distribution = c.Options.TryGetValue("distribution", out var d) ? d : "SAMPLE";
                valueIndex = 0;
            }

            var call = valueIndex == 0 ? c : new KqlCall(c, 1);
            if (distribution == "SAMPLE")
                GuardedAggregate(call, sample, sampleIf, minimumCount: 2);
            else if (distribution == "POPULATION")
            {
                if (populationIf == null && call.Filter != null)
                    throw c.Unsupported("a filtered population standard deviation is not supported.");
                GuardedAggregate(call, population, populationIf ?? population);
            }
            else
                throw c.Unsupported($"unknown distribution '{distribution}'.");
        }

        static void Median(KqlCall c)
        {
            // percentile() is an estimate, so only an approximate median can be delivered.
            if (c.EnumArg(0) != "APPROXIMATE")
                throw c.Unsupported("only the APPROXIMATE median is supported.");
            if (c.Filter != null)
                throw c.Unsupported("a filtered median is not supported.");
            RejectDistinct(c);
            var call = new KqlCall(c, 1);
            call.W("iif(countif(");
            call.NotNull(0);
            call.W(") < 1, real(null), percentile(");
            call.Arg(0);
            call.W(", 50))");
        }

        #endregion
    }
}
