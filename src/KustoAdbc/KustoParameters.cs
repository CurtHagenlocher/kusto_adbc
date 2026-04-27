// Copyright (c) Microsoft Corporation.  All rights reserved.

namespace KustoAdbc
{
    /// <summary>
    /// Option key constants for the Kusto ADBC driver.
    /// </summary>
    public static class KustoParameters
    {
        /// <summary>
        /// The Kusto cluster endpoint URL (e.g., "https://mycluster.region.kusto.windows.net").
        /// </summary>
        public const string Endpoint = "adbc.kusto.endpoint";

        /// <summary>
        /// The database name.
        /// </summary>
        public const string Database = "adbc.kusto.database";

        /// <summary>
        /// Bearer access token for authentication.
        /// </summary>
        public const string AccessToken = "adbc.kusto.access_token";

        /// <summary>
        /// Batch size for streaming results (number of rows per RecordBatch). Default: 65536.
        /// </summary>
        public const string BatchSize = "adbc.kusto.batch_size";
    }
}
