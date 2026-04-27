// Copyright (c) Microsoft Corporation.  All rights reserved.

using System.Reflection;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Provides access to the Kusto ADBC driver's Substrait capability
    /// declaration — the YAML file listing all supported functions and
    /// their type signatures.
    ///
    /// Plan producers can read this to discover which Substrait functions
    /// the Kusto consumer supports before generating a plan.
    /// </summary>
    public static class KustoCapabilities
    {
        const string ResourceName = "KustoAdbc.kusto_functions.yaml";

        /// <summary>
        /// Returns the capability YAML as a stream (UTF-8).
        /// Caller is responsible for disposing the stream.
        /// </summary>
        public static Stream GetCapabilityYamlStream()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException($"Embedded resource '{ResourceName}' not found.");
        }

        /// <summary>
        /// Returns the capability YAML as a string.
        /// </summary>
        public static string GetCapabilityYaml()
        {
            using var stream = GetCapabilityYamlStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
