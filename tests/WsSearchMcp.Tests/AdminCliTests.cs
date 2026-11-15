using System.Text.Json;
using WsSearchMcp.Admin;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests;

public class AdminCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wssearch-admin-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryClientStore _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = AdminCli.Run(args, o, e, _store, new ServerPaths(_root), TimeProvider.System);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Mint_prints_only_the_token_on_stdout_and_stores_the_client()
    {
        var (code, stdout, stderr) = Run("client", "mint", "Work VS Code");
        Assert.Equal(0, code);
        var token = stdout.Trim();
        Assert.Equal(3, token.Split('.').Length);
        Assert.DoesNotContain(' ', token);
        Assert.Contains("Work VS Code", stderr);
        Assert.Contains("shown once", stderr);
        Assert.True(ClientTokens.Validate(token, _store, DateTimeOffset.UtcNow).Ok);
    }

    [Fact]
    public void Mint_rejects_duplicates_and_invalid_names()
    {
        Assert.Equal(0, Run("client", "mint", "Laptop").code);
        var dup = Run("client", "mint", "laptop");
        Assert.NotEqual(0, dup.code);
        Assert.Contains("already exists", dup.stderr);
        Assert.Equal("", dup.stdout);

        var bad = Run("client", "mint", "no,commas");
        Assert.NotEqual(0, bad.code);
        Assert.Equal("", bad.stdout);
        Assert.NotEqual(0, Run("client", "mint").code);
    }

    [Fact]
    public void Mint_expires_option()
    {
        Assert.Equal(0, Run("client", "mint", "short", "--expires", "7d").code);
        Assert.Equal(0, Run("client", "mint", "forever", "--expires", "never").code);
        Assert.NotEqual(0, Run("client", "mint", "bad", "--expires", "soon").code);
        Assert.NotEqual(0, Run("client", "mint", "bad2", "--expires", "0d").code);

        var byName = _store.List().ToDictionary(c => c.Name);
        Assert.InRange((byName["short"].Expires!.Value - byName["short"].Created).TotalDays, 6.99, 7.01);
        Assert.Null(byName["forever"].Expires);
        Assert.False(byName.ContainsKey("bad"));
    }

    [Fact]
    public void List_with_no_clients()
    {
        var (code, stdout, _) = Run("client", "list");
        Assert.Equal(0, code);
        Assert.Equal("No clients.", stdout.Trim());
    }

    [Fact]
    public void List_is_sorted_by_name_and_excludes_revoked()
    {
        Run("client", "mint", "zeta");
        Run("client", "mint", "Alpha");
        Run("client", "mint", "mid");
        Run("client", "revoke", "mid");

        var (code, stdout, _) = Run("client", "list");
        Assert.Equal(0, code);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("NAME", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 current clients
        Assert.StartsWith("Alpha", lines[1]);
        Assert.StartsWith("zeta", lines[2]);
        Assert.DoesNotContain("mid", stdout);
    }

    [Fact]
    public void List_flags_expired_clients_and_shows_last_used()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-60);
        var m = ClientTokens.Mint(_store, "old", TimeSpan.FromDays(30), past);
        var (_, plain, _) = Run("client", "list");
        Assert.Contains("EXPIRED", plain);

        new ClientUsageTracker(new ServerPaths(_root)).Touch(m.Client.Kid, DateTimeOffset.UtcNow);
        var (_, withUsage, _) = Run("client", "list");
        var row = withUsage.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd HH:"), row); // last-used column populated
        Assert.DoesNotContain("never", row);
    }

    [Fact]
    public void List_json_shape()
    {
        var m = ClientTokens.Mint(_store, "json client", TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
        var (code, stdout, _) = Run("client", "list", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("json client", row.GetProperty("name").GetString());
        Assert.Equal(m.Client.Kid, row.GetProperty("kid").GetString());
        Assert.False(row.GetProperty("expired").GetBoolean());
        Assert.Equal(m.Client.Fingerprint, row.GetProperty("fingerprint").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastUsed").ValueKind);
        Assert.DoesNotContain(m.Token, stdout);
    }

    [Fact]
    public void Json_list_of_nothing_is_an_empty_array()
    {
        var (_, stdout, _) = Run("client", "list", "--json");
        Assert.Equal(0, JsonDocument.Parse(stdout).RootElement.GetArrayLength());
    }

    [Fact]
    public void Revoke_by_name_is_case_insensitive_and_unknown_name_fails()
    {
        Run("client", "mint", "Phone");
        var ok = Run("client", "revoke", "pHoNe");
        Assert.Equal(0, ok.code);
        Assert.Contains("Revoked client 'Phone'", ok.stdout);
        Assert.Empty(_store.List());

        var missing = Run("client", "revoke", "Phone");
        Assert.NotEqual(0, missing.code);
        Assert.Contains("No client named", missing.stderr);
    }

    [Fact]
    public void Revoked_clients_tokens_stop_validating()
    {
        var token = Run("client", "mint", "temp").stdout.Trim();
        Run("client", "revoke", "temp");
        Assert.False(ClientTokens.Validate(token, _store, DateTimeOffset.UtcNow).Ok);
    }

    [Theory]
    [InlineData("365d", 365.0)]
    [InlineData("1D", 1.0)]
    [InlineData("never", null)]
    public void Lifetime_parsing_accepts(string text, double? days)
    {
        Assert.True(AdminCli.TryParseLifetime(text, out var span));
        Assert.Equal(days, span?.TotalDays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0d")]
    [InlineData("-5d")]
    [InlineData("12")]
    [InlineData("99999d")]
    [InlineData("1y")]
    public void Lifetime_parsing_rejects(string text) => Assert.False(AdminCli.TryParseLifetime(text, out _));

    [Fact]
    public void Unknown_command_and_no_args_fail_with_usage()
    {
        Assert.NotEqual(0, Run("frobnicate").code);
        Assert.Contains("client mint", Run().stderr);
        Assert.Equal(0, Run("--help").code);
    }
}
