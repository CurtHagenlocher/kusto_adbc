// Copyright (c) Microsoft Corporation.  All rights reserved.

using Xunit;

namespace KustoAdbc.Tests
{
    public class GetObjectsTests
    {
        [Theory]
        [InlineData("MyTable", "%", true)]
        [InlineData("MyTable", "MyTable", true)]
        [InlineData("MyTable", "mytable", true)]
        [InlineData("MyTable", "Other", false)]
        [InlineData("MyTable", "My%", true)]
        [InlineData("MyTable", "%Table", true)]
        [InlineData("MyTable", "%Tab%", true)]
        [InlineData("MyTable", "M_Table", true)]
        [InlineData("MyTable", "M_T%", true)]
        [InlineData("MyTable", "___able", true)]
        [InlineData("MyTable", "_______", true)]
        [InlineData("MyTable", "________", false)]
        [InlineData("X", "_", true)]
        [InlineData("X", "__", false)]
        [InlineData("", "%", true)]
        [InlineData("", "", true)]
        [InlineData("", "_", false)]
        public void MatchesPattern_WorksCorrectly(string value, string pattern, bool expected)
        {
            Assert.Equal(expected, KustoConnection.MatchesPattern(value, pattern));
        }
    }
}
