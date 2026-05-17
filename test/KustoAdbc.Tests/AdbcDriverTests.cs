// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    public class AdbcDriverTests
    {
        [Fact]
        public void Driver_Open_CreatesDatabase()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            var db = driver.Open(options);
            Assert.NotNull(db);
        }

        [Fact]
        public void Database_Connect_CreatesConnection()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            var db = driver.Open(options);
            var conn = db.Connect(null);
            Assert.NotNull(conn);
        }

        [Fact]
        public void Connection_CreateStatement_ReturnsStatement()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stmt = conn.CreateStatement();
            Assert.NotNull(stmt);
        }

        [Fact]
        public void Statement_SetSqlQuery_StoresQuery()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stmt = conn.CreateStatement();
            stmt.SqlQuery = "MyTable | take 10";
            Assert.Equal("MyTable | take 10", stmt.SqlQuery);
        }

        [Fact]
        public void Statement_SetSubstraitPlan_StoresPlan()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stmt = conn.CreateStatement();
            byte[] plan = new byte[] { 1, 2, 3 };
            stmt.SubstraitPlan = plan;
            Assert.Equal(plan, stmt.SubstraitPlan);
        }

        [Fact]
        public void Database_Connect_RequiresEndpoint()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            var db = driver.Open(options);
            Assert.Throws<AdbcException>(() => db.Connect(null));
        }

        [Fact]
        public void Database_Connect_RequiresDatabase()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.AccessToken] = "test-token",
            };

            var db = driver.Open(options);
            Assert.Throws<AdbcException>(() => db.Connect(null));
        }

        [Fact]
        public void Database_Connect_RequiresAccessToken()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
            };

            var db = driver.Open(options);
            Assert.Throws<AdbcException>(() => db.Connect(null));
        }

        [Fact]
        public async Task Connection_GetTableTypes_ReturnsKustoTypes()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stream = conn.GetTableTypes();

            var batch = await stream.ReadNextRecordBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(3, batch!.Length); // Table, MaterializedView, ExternalTable

            var typeCol = (Apache.Arrow.StringArray)batch.Column(0);
            Assert.Equal("Table", typeCol.GetString(0));
            Assert.Equal("MaterializedView", typeCol.GetString(1));
            Assert.Equal("ExternalTable", typeCol.GetString(2));
        }

        [Fact]
        public async Task Connection_GetInfo_ReturnsDriverInfo()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stream = conn.GetInfo(new[] { AdbcInfoCode.VendorName, AdbcInfoCode.DriverName });

            var batch = await stream.ReadNextRecordBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(2, batch!.Length);
        }

        [Fact]
        public void Database_SetOption_OverridesParameters()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "old-token",
            };

            var db = driver.Open(options);
            db.SetOption(KustoParameters.AccessToken, "new-token");

            // Connect should succeed with the new token
            using var conn = db.Connect(null);
            Assert.NotNull(conn);
        }

        [Fact]
        public void Statement_NoQueryOrPlan_ThrowsOnExecute()
        {
            var driver = new KustoDriver();
            var options = new Dictionary<string, string>
            {
                [KustoParameters.Endpoint] = "https://test.kusto.windows.net",
                [KustoParameters.Database] = "testdb",
                [KustoParameters.AccessToken] = "test-token",
            };

            using var db = driver.Open(options);
            using var conn = db.Connect(null);
            using var stmt = conn.CreateStatement();

            // No query or plan set — should throw
            Assert.Throws<AdbcException>(() => stmt.ExecuteQuery());
        }

        [Fact]
        public void Capabilities_YamlIsAccessible()
        {
            string yaml = KustoCapabilities.GetCapabilityYaml();
            Assert.NotEmpty(yaml);
            Assert.Contains("scalar_functions:", yaml);
            Assert.Contains("aggregate_functions:", yaml);
            Assert.Contains("name: \"add\"", yaml);
            Assert.Contains("name: \"count\"", yaml);
            Assert.Contains("name: \"char_length\"", yaml);
        }
    }
}
