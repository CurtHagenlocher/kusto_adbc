// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Provides access to the Kusto ADBC driver's Substrait dialect: a YAML document
    /// in the Substrait dialect format (text/dialect_schema.yaml in the Substrait
    /// repository) listing the types, relations, expressions and functions, with
    /// their implementations and option behaviors, that the driver translates to KQL.
    ///
    /// Plan producers can read this to generate plans the driver accepts.
    /// </summary>
    public static class KustoCapabilities
    {
        const string ResourceName = "KustoAdbc.kusto_dialect.yaml";

        /// <summary>
        /// Returns the dialect YAML as a stream (UTF-8).
        /// Caller is responsible for disposing the stream.
        /// </summary>
        public static Stream GetDialectYamlStream()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException($"Embedded resource '{ResourceName}' not found.");
        }

        /// <summary>
        /// Returns the dialect YAML as a string.
        /// </summary>
        public static string GetDialectYaml()
        {
            using var stream = GetDialectYamlStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Returns the dialect YAML as a stream. Superseded by <see cref="GetDialectYamlStream"/>.</summary>
        [Obsolete("Use GetDialectYamlStream(); the capability document is now a Substrait dialect file.")]
        public static Stream GetCapabilityYamlStream() => GetDialectYamlStream();

        /// <summary>Returns the dialect YAML. Superseded by <see cref="GetDialectYaml"/>.</summary>
        [Obsolete("Use GetDialectYaml(); the capability document is now a Substrait dialect file.")]
        public static string GetCapabilityYaml() => GetDialectYaml();
    }
}
