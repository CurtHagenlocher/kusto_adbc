// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using KustoAdbc.Substrait;
using Xunit;

namespace KustoAdbc.Tests
{
    /// <summary>
    /// Translates Substrait plans produced by a real producer (DataFusion) and compares the
    /// result with reviewed snapshots in Fixtures/DataFusion/*.kql. A snapshot is either the
    /// expected KQL, which must also pass semantic analysis, or "ERROR: &lt;message fragment&gt;"
    /// for plans the driver must reject.
    ///
    /// Set KUSTO_ADBC_UPDATE_SNAPSHOTS=1 to rewrite the snapshots, then review the diff.
    /// </summary>
    public class DataFusionFixtureTests
    {
        const string ErrorPrefix = "ERROR: ";

        static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DataFusion");

        public static IEnumerable<object[]> Fixtures()
            => Directory.GetFiles(FixtureDirectory, "*.pb")
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void TranslatesAsReviewed(string name)
        {
            byte[] plan = File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".pb"));
            string actual;
            try
            {
                actual = SubstraitToKqlTranslator.Translate(plan).Replace("\r\n", "\n");
            }
            catch (SubstraitTranslationException ex)
            {
                actual = ErrorPrefix + ex.Message;
            }

            if (Environment.GetEnvironmentVariable("KUSTO_ADBC_UPDATE_SNAPSHOTS") == "1")
            {
                // Write to the source tree, not the test output directory.
                string source = Path.Combine(FindSourceFixtureDirectory(), name + ".kql");
                File.WriteAllText(source, actual + "\n");
                return;
            }

            string snapshotPath = Path.Combine(FixtureDirectory, name + ".kql");
            Assert.True(File.Exists(snapshotPath), $"Missing snapshot {name}.kql. Actual translation:\n{actual}");
            string expected = File.ReadAllText(snapshotPath).Replace("\r\n", "\n").TrimEnd('\n');

            if (expected.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            {
                Assert.True(actual.StartsWith(ErrorPrefix, StringComparison.Ordinal), $"Expected rejection, got:\n{actual}");
                Assert.Contains(expected.Substring(ErrorPrefix.Length), actual);
                return;
            }

            Assert.False(actual.StartsWith(ErrorPrefix, StringComparison.Ordinal), actual);
            Assert.Equal(expected, actual);
            KqlAssert.Analyzes(actual);
        }

        [Fact]
        public void PartialPushdown_ProducesValidPlansForAllFixtures()
        {
            foreach (var file in Directory.GetFiles(FixtureDirectory, "*.pb"))
            {
                var result = SubstraitPartialPushdown.Pushdown(File.ReadAllBytes(file));
                Assert.True(result.Plan.Length > 0, file);
                if (result.FullyPushed)
                    Assert.NotNull(result.Kql);
            }
        }

        static string FindSourceFixtureDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KustoAdbc.Tests.csproj")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("Cannot locate the test project directory.");
            return Path.Combine(dir.FullName, "Fixtures", "DataFusion");
        }
    }
}
