using System.Text.Json;
using WsSearchMcp.Files;
using Xunit;

namespace WsSearchMcp.Tests.Files;

public class IndexedFileResolverTests
{
    private static string Row(string path, long size = 10, int attrs = 32, string? modified = "2026-01-02T03:04:05Z") =>
        JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["System.ItemPathDisplay"] = path, ["System.Size"] = size,
                ["System.FileAttributes"] = attrs, ["System.DateModified"] = modified,
            },
        });

    private static (IndexedFileResolver resolver, List<(string filters, string columns, int max)> calls) With(params string[] replies)
    {
        var calls = new List<(string, string, int)>();
        var i = 0;
        var r = new IndexedFileResolver((f, c, m) => { calls.Add((f, c, m)); return i < replies.Length ? replies[i++] : "[]"; });
        return (r, calls);
    }

    [Fact]
    public void Exact_match_returns_size_modified_and_the_indexs_canonical_path()
    {
        var (r, calls) = With(Row(@"C:\Data\Report.TXT", size: 1234));
        var f = r.Resolve(@"c:\data\report.txt");
        Assert.NotNull(f);
        Assert.Equal(@"C:\Data\Report.TXT", f!.Path);   // index casing wins over the caller's
        Assert.Equal(1234, f.Size);
        Assert.False(f.IsDirectory);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), f.Modified);

        // Exactly one query, an equality filter on ItemPathDisplay (SQL escaping is the shared find_files path's job).
        var call = Assert.Single(calls);
        using var doc = JsonDocument.Parse(call.filters);
        var filter = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("System.ItemPathDisplay", filter.GetProperty("property").GetString());
        Assert.Equal("=", filter.GetProperty("op").GetString());
        Assert.Equal(@"c:\data\report.txt", filter.GetProperty("value").GetString());
        Assert.Contains("System.Size", call.columns);
        Assert.Contains("System.FileAttributes", call.columns);
    }

    [Fact]
    public void Directory_attribute_is_reported()
    {
        var (r, _) = With(Row(@"C:\Data\Folder", attrs: 16));
        Assert.True(r.Resolve(@"C:\Data\Folder")!.IsDirectory);
    }

    [Fact]
    public void Unknown_path_is_null_after_trying_the_DP_fallback()
    {
        var (r, calls) = With("[]", "[]");
        Assert.Null(r.Resolve(@"C:\Nope\x.txt"));
        Assert.Equal(2, calls.Count);
        using var doc = JsonDocument.Parse(calls[1].filters);
        var props = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("property").GetString()).ToArray();
        Assert.Equal(new[] { "System.FileName", "System.ItemUrl" }, props);
        Assert.Equal("DP://%", doc.RootElement[1].GetProperty("value").GetString());
    }

    [Fact]
    public void DP_entries_whose_path_find_files_repairs_are_found_through_the_file_name()
    {
        // First query (exact ItemPathDisplay) finds nothing; the fallback returns the repaired path.
        var (r, _) = With("[]", Row(@"D:\Linked\Photos\a.jpg", size: 99));
        var f = r.Resolve(@"D:\Linked\Photos\a.jpg");
        Assert.NotNull(f);
        Assert.Equal(99, f!.Size);
    }

    [Fact]
    public void A_row_for_a_different_path_is_ignored()
    {
        var (r, _) = With(Row(@"C:\Other\x.txt"), Row(@"C:\Another\x.txt"));
        Assert.Null(r.Resolve(@"C:\Wanted\x.txt"));
    }

    [Fact]
    public void Malformed_or_unexpected_json_is_treated_as_not_found()
    {
        var (r, _) = With("not json", "{\"a\":1}");
        Assert.Null(r.Resolve(@"C:\x\y.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\x.txt")]
    [InlineData(@"C:\a\..\b.txt")]
    [InlineData(@"C:\a\b.txt:stream")]
    public void Invalid_paths_never_reach_the_index(string path)
    {
        var (r, calls) = With(Row(@"C:\a\b.txt"));
        Assert.Null(r.Resolve(path));
        Assert.Empty(calls);
    }

    [Fact]
    public void Quotes_in_the_path_travel_as_data_not_as_sql()
    {
        var (r, calls) = With("[]", "[]");
        r.Resolve(@"C:\x' OR 1=1 --\y.txt");
        using var doc = JsonDocument.Parse(calls[0].filters);
        Assert.Equal(@"C:\x' OR 1=1 --\y.txt", doc.RootElement[0].GetProperty("value").GetString());
    }
}
