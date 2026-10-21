// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Translates a Substrait binary plan into KQL.
    /// Output is UTF-8 bytes (ready for HTTP) via <see cref="Utf8KqlWriter"/>.
    ///
    /// The plan is decoded into an intermediate model (rejecting anything whose
    /// semantics can't be preserved) and then written as KQL. The supported
    /// relations, expressions, types and functions are listed in the dialect
    /// file returned by <see cref="KustoCapabilities.GetDialectYaml"/>.
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
            return TranslateWithSchema(planBytes).Kql;
        }

        /// <summary>Translates a plan and also returns the query's output columns.</summary>
        internal static (Utf8KqlWriter Kql, List<KqlColumn> Columns) TranslateWithSchema(byte[] planBytes)
        {
            if (planBytes == null || planBytes.Length == 0)
                throw new ArgumentException("Substrait plan is empty.", nameof(planBytes));

            var decoder = new SubstraitPlanDecoder(planBytes);
            var root = decoder.DecodePlan();
            var writer = new Utf8KqlWriter();
            var columns = new KqlEmitter(writer, decoder.PerRecordEvaluation).EmitRoot(root);
            return (writer, columns);
        }
    }
}
