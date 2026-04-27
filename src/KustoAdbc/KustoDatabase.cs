// Copyright (c) Microsoft Corporation.  All rights reserved.

using Apache.Arrow.Adbc;

namespace KustoAdbc
{
    /// <summary>
    /// Represents a Kusto database connection configuration.
    /// Holds connection parameters and creates connections.
    /// </summary>
    public sealed class KustoDatabase : AdbcDatabase
    {
        readonly Dictionary<string, string> _options;

        internal KustoDatabase(IReadOnlyDictionary<string, string> parameters)
        {
            _options = new Dictionary<string, string>();
            foreach (var kvp in parameters)
                _options[kvp.Key] = kvp.Value;
        }

        public override void SetOption(string key, string value)
        {
            _options[key] = value;
        }

        public override AdbcConnection Connect(IReadOnlyDictionary<string, string>? options)
        {
            // Merge connection-level options with database-level options
            var merged = new Dictionary<string, string>(_options);
            if (options != null)
            {
                foreach (var kvp in options)
                    merged[kvp.Key] = kvp.Value;
            }

            string endpoint = GetRequired(merged, KustoParameters.Endpoint);
            string database = GetRequired(merged, KustoParameters.Database);
            string accessToken = GetRequired(merged, KustoParameters.AccessToken);

            int batchSize = 65536;
            if (merged.TryGetValue(KustoParameters.BatchSize, out string? batchSizeStr)
                && int.TryParse(batchSizeStr, out int parsed) && parsed > 0)
            {
                batchSize = parsed;
            }

            return new KustoConnection(endpoint, database, accessToken, batchSize);
        }

        static string GetRequired(Dictionary<string, string> options, string key)
        {
            if (!options.TryGetValue(key, out string? value) || string.IsNullOrEmpty(value))
                throw new AdbcException($"Required option '{key}' is missing or empty.", AdbcStatusCode.InvalidArgument);
            return value;
        }
    }
}
