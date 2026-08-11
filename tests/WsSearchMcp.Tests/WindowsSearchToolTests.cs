using System;
using System.Collections.Generic;
using Xunit;
using WsSearchMcp;

namespace WsSearchMcp.Tests
{
    public class WindowsSearchToolTests
    {
        [Theory]
        [InlineData(0, "TOP 1")]
        [InlineData(1, "TOP 1")]
        [InlineData(1001, "TOP 1000")]
        [InlineData(50, "TOP 50")]
        public void BuildSql_ClampsMaxResults(int maxResults, string expectedTop)
        {
            var (sql, scope, cols) = WindowsSearchTool.BuildSql("q", "C:\\temp", maxResults, "contains", null);
            Assert.Contains(expectedTop, sql);
        }

        [Fact]
        public void BuildSql_IncludesPropertiesAndDeduplicates()
        {
            var (sql, _, cols) = WindowsSearchTool.BuildSql("q", "C:\\", 10, "contains", new[] { "System.Size", "System.Size", "System.ItemType" });
            Assert.Contains("System.Size", sql);
            var occurrences = CountOccurrences(sql, "System.ItemPathDisplay");
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void BuildSql_UsesFilenameMode()
        {
            var (sql, _, cols) = WindowsSearchTool.BuildSql("file", "", 5, "filename", null);
            Assert.Contains("System.FileName LIKE '%file%'", sql);
            Assert.DoesNotContain("SCOPE=", sql);
        }

        [Fact]
        public void BuildSql_UnsetScope_SearchesEntireIndex()
        {
            var (sql, scopeUri, _) = WindowsSearchTool.BuildSql("file", null, 5, "filename", null);
            Assert.Null(scopeUri);
            Assert.DoesNotContain("SCOPE=", sql);
        }

        [Fact]
        public void BuildSql_ContainsModeAlsoMatchesFilename()
        {
            var (sql, _, cols) = WindowsSearchTool.BuildSql("gastro", null, 5, "contains", null);
            Assert.Contains("CONTAINS('\"gastro\"')", sql);
            Assert.Contains("System.FileName LIKE '%gastro%'", sql);
        }

        [Fact]
        public void BuildSql_ContainsMode_DoubleQuotesNeutralized()
        {
            // A double quote in the term must not leak into the CONTAINS phrase and
            // produce an unbalanced/malformed full-text predicate.
            var (sql, _, _) = WindowsSearchTool.BuildSql("say \"hi\" there", null, 5, "contains", null);
            Assert.Contains("CONTAINS('\"say hi there\"')", sql);
            Assert.Contains("System.FileName LIKE '%say hi there%'", sql);
            // The only double quotes left are the phrase delimiters ADO requires.
            Assert.Equal(2, CountOccurrences(sql, "\""));
        }

        [Fact]
        public void BuildSql_ContainsMode_OnlyQuotesThrows()
        {
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql("\"\"", null, 5, "contains", null));
        }

        [Fact]
        public void BuildSql_ContainsFilter_DoubleQuotesNeutralized()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Author", Op = "CONTAINS", Value = "John \"Jack\" Smith" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("CONTAINS(System.Author, 'John Jack Smith')", sql);
        }

        [Fact]
        public void BuildSql_FilenameMode_ApostropheBecomesWildcard()
        {
            var (sql, _, _) = WindowsSearchTool.BuildSql("Samantha's driver", "", 5, "filename", null);
            Assert.Contains("System.FileName LIKE '%Samantha_s driver%'", sql);
        }

        [Theory]
        [InlineData("O'Brien")]      // straight apostrophe U+0027
        [InlineData("O’Brien")] // typographic apostrophe U+2019
        [InlineData("O‘Brien")] // left single quote U+2018
        public void BuildSql_ContainsMode_ApostropheVariantsCanonicalizeToWildcard(string query)
        {
            var (sql, _, _) = WindowsSearchTool.BuildSql(query, "", 5, "contains", null);
            Assert.Contains("System.FileName LIKE '%O_Brien%'", sql);
        }

        [Fact]
        public void BuildSql_UsesRawMode()
        {
            var (sql, _, cols) = WindowsSearchTool.BuildSql("x>0", null, 5, "raw", null);
            Assert.Contains("WHERE x>0", sql);
        }

        [Fact]
        public void BuildSql_RawMode_AqsSizeComparisonExplainsExpectedSql()
        {
            var error = Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql("System.Size:>100mb", null, 5, "raw", null));
            Assert.Contains("System.Size > 104857600", error.Message);
        }

        [Fact]
        public void BuildSql_FiltersAlone_WithoutQuery_Works()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = ">", Value = "1024", ValueType = "number" } };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("WHERE System.Size > 1024", sql);
            Assert.DoesNotContain("CONTAINS", sql);
            Assert.DoesNotContain("LIKE", sql);
        }

        [Fact]
        public void BuildSql_SizeGreaterThan_HtmlEscapedOperatorWorks()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Size", Op = "&gt;", Value = "104857600", ValueType = "number" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("WHERE System.Size > 104857600", sql);
        }

        [Fact]
        public void BuildSql_OrLogic_CombineQueryAndFilters()
        {
            var filters = new[] { new SearchFilter { Property = "System.Kind", Op = "=", Value = "picture" } };
            var (sql, _, _) = WindowsSearchTool.BuildSql("foo", "", 10, "contains", null, filters, null, null, "OR");
            Assert.Contains(" OR System.Kind = 'picture'", sql);
        }

        [Fact]
        public void BuildSql_OrLogic_CombinesEveryClause()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Kind", Op = "=", Value = "picture" },
                new SearchFilter { Property = "System.Size", Op = ">", Value = "1024", ValueType = "number" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql("foo", "", 10, "filename", null, filters, null, null, "OR");
            Assert.Contains("System.FileName LIKE '%foo%' OR System.Kind = 'picture' OR System.Size > 1024", sql);
        }

        [Fact]
        public void BuildSql_InvalidFilterLogic_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql("foo", "", 10, "contains", null, null, null, null, "XOR"));
        }

        [Fact]
        public void BuildSql_NoQueryAndNoFilters_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql(null, "", 10, "contains", null, null));
        }

        [Fact]
        public void BuildSql_DateFilterRendersAsQuotedLiteral()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.DateModified", Op = ">=", Value = "2024-01-01T00:00:00" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("System.DateModified >= '2024-01-01T00:00:00'", sql);
        }

        [Fact]
        public void BuildSql_MultipleFiltersAreAndCombined()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.DateModified", Op = ">=", Value = "2024-01-01" },
                new SearchFilter { Property = "System.Size", Op = "<", Value = "1000000", ValueType = "number" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql("foo", "", 10, "filename", null, filters);
            Assert.Contains("System.FileName LIKE '%foo%' AND System.DateModified >= '2024-01-01' AND System.Size < 1000000", sql);
        }

        [Fact]
        public void BuildSql_ContainsOperatorTargetsSpecificProperty()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Author", Op = "CONTAINS", Value = "Smith" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("CONTAINS(System.Author, 'Smith')", sql);
        }

        [Fact]
        public void BuildSql_IsNullOperatorOmitsValue()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Author", Op = "IS NULL" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("System.Author IS NULL", sql);
        }

        [Fact]
        public void BuildSql_BoolValueRendersAsOneOrZero()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.IsEncrypted", Op = "=", Value = "true", ValueType = "bool" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("System.IsEncrypted = 1", sql);
        }

        [Fact]
        public void BuildSql_InvalidPropertyNameThrows()
        {
            var filters = new[] { new SearchFilter { Property = "bad name; DROP TABLE", Op = "=", Value = "x" } };
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters));
        }

        [Fact]
        public void BuildSql_InvalidOperatorThrows()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = "MATCHES", Value = "x" } };
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters));
        }

        [Fact]
        public void BuildSql_InvalidNumberValueThrows()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = ">", Value = "not-a-number", ValueType = "number" } };
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters));
        }

        [Fact]
        public void BuildSql_OrderByAppearsAtEnd()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = ">", Value = "0", ValueType = "number" } };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters,
                orderBy: "System.DateModified", orderDir: "DESC");
            Assert.EndsWith("ORDER BY System.DateModified DESC", sql);
        }

        [Fact]
        public void BuildSql_OrderByDefaultsToAsc()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = ">", Value = "0", ValueType = "number" } };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters,
                orderBy: "System.Size");
            Assert.EndsWith("ORDER BY System.Size ASC", sql);
        }

        [Fact]
        public void BuildSql_InvalidOrderDirThrows()
        {
            var filters = new[] { new SearchFilter { Property = "System.Size", Op = ">", Value = "0", ValueType = "number" } };
            Assert.Throws<ArgumentException>(() =>
                WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters,
                    orderBy: "System.Size", orderDir: "sideways"));
        }

        [Fact]
        public void BuildSql_SqlInjectionInValueIsEscaped()
        {
            var filters = new[]
            {
                new SearchFilter { Property = "System.Author", Op = "=", Value = "O'Reilly' OR 1=1 --" }
            };
            var (sql, _, _) = WindowsSearchTool.BuildSql(null, "", 10, "contains", null, filters);
            Assert.Contains("System.Author = 'O''Reilly'' OR 1=1 --'", sql);
        }

        [Fact]
        public void ListPropertiesByFamily_Photo_ReturnsPhotoProperties()
        {
            var json = WindowsSearchTool.ListPropertiesByFamily("Photo", 50);
            Assert.Contains("System.Photo.", json);
            Assert.DoesNotContain("System.Music.", json);
        }

        [Fact]
        public void ListPropertiesByFamily_Root_ReturnsRootProperties()
        {
            var json = WindowsSearchTool.ListPropertiesByFamily("", 200);
            Assert.Contains("System.DateModified", json);
            Assert.Contains("System.FileName", json);
            Assert.DoesNotContain("System.Photo.", json);
        }

        [Fact]
        public void ListProperties_FilterByDateModified_Finds()
        {
            var json = WindowsSearchTool.ListProperties("DateModified", 50);
            Assert.Contains("System.DateModified", json);
        }

        [Fact]
        public void ListProperties_NoFilter_ReturnsMany()
        {
            var json = WindowsSearchTool.ListProperties(null, 2000);
            Assert.Contains("System.FileName", json);
            Assert.Contains("System.Photo.", json);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
