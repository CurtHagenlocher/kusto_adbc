using System;
using System.Collections.Generic;
using Apache.Arrow.Adbc;

namespace KustoAdbc
{
    /// <summary>
    /// ADBC driver entry point for Azure Data Explorer (Kusto).
    /// </summary>
    public sealed class KustoDriver : AdbcDriver
    {
        public override AdbcDatabase Open(IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            return new KustoDatabase(parameters);
        }
    }
}
