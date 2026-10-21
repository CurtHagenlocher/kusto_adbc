// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using Xunit;

namespace KustoAdbc.Tests
{
    /// <summary>
    /// KQL assertions backed by the Kusto language service (Microsoft.Azure.Kusto.Language):
    /// syntactic comparison, and semantic analysis against known table schemas, which
    /// catches type errors, unknown functions and wrong arities without a cluster.
    /// </summary>
    static class KqlAssert
    {
        /// <summary>Tables available to translated queries in tests.</summary>
        public static readonly GlobalState Globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("db",
            new TableSymbol("T", "(i:int, l:long, r:real, s:string, b:bool, dt:datetime, ts:timespan, d:decimal, g:guid)"),
            new TableSymbol("U", "(i:int, l:long, s:string)"),
            new TableSymbol("orders", "(id:long, customer_id:long, name:string, price:real, qty:int, ts:datetime, flag:bool)"),
            new TableSymbol("customers", "(id:long, cname:string, region:string)"),
            new TableSymbol("odd name", "(['a b']:long, ['where']:string)")));

        /// <summary>
        /// Asserts that both queries parse and are syntactically identical once whitespace is normalized.
        /// </summary>
        public static void Equal(string expectedKql, string actualKql)
        {
            var expectedCode = KustoCode.Parse(expectedKql);
            var actualCode = KustoCode.Parse(actualKql);

            var expectedDiags = expectedCode.GetSyntaxDiagnostics();
            var actualDiags = actualCode.GetSyntaxDiagnostics();
            Assert.True(expectedDiags.Count == 0, $"Expected KQL has syntax errors: {Format(expectedDiags)}\nKQL: {expectedKql}");
            Assert.True(actualDiags.Count == 0, $"Translated KQL has syntax errors: {Format(actualDiags)}\nKQL: {actualKql}");

            Assert.Equal(Unbracket(Normalize(expectedCode)), Unbracket(Normalize(actualCode)));
        }

        /// <summary>
        /// The translator brackets every name taken from a plan; for comparisons, ['x'] and x are
        /// treated alike when x is a plain identifier. Whether quoting is sufficient is checked by
        /// <see cref="Analyzes"/>, which rejects unbracketed reserved words.
        /// </summary>
        static string Unbracket(string kql)
            => System.Text.RegularExpressions.Regex.Replace(kql, @"\['([A-Za-z_][A-Za-z0-9_]*)'\]", "$1");

        /// <summary>
        /// Asserts that the query is semantically valid against <see cref="Globals"/> and
        /// returns its result columns as "name:type".
        /// </summary>
        public static IReadOnlyList<string> Analyzes(string kql)
        {
            var code = KustoCode.ParseAndAnalyze(kql, Globals);
            var diags = code.GetDiagnostics();
            Assert.True(diags.Count == 0, $"KQL does not analyze cleanly: {Format(diags)}\nKQL:\n{kql}");
            Assert.True(code.ResultType is TableSymbol, $"Query result is not tabular.\nKQL:\n{kql}");
            return ((TableSymbol)code.ResultType).Columns.Select(c => $"{c.Name}:{c.Type.Name}").ToList();
        }

        /// <summary>Asserts both <see cref="Equal"/> and <see cref="Analyzes"/>.</summary>
        public static IReadOnlyList<string> EqualAndAnalyzes(string expectedKql, string actualKql)
        {
            Equal(expectedKql, actualKql);
            return Analyzes(actualKql);
        }

        /// <summary>Whitespace-normalized KQL with simple bracketed names unquoted, for comparisons.</summary>
        public static string Canonical(string kql) => Unbracket(Normalize(KustoCode.Parse(kql)));

        static string Normalize(KustoCode code)
        {
            // Minimal trivia keeps structural whitespace; runs of whitespace are then collapsed,
            // so "T\n| where x" and "T | where x" compare equal.
            string text = code.Syntax.ToString(IncludeTrivia.Minimal);
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        static string Format(IReadOnlyList<Diagnostic> diags)
            => string.Join("; ", diags.Select(d => $"[{d.Start}..{d.Start + d.Length}] {d.Message}"));
    }
}
