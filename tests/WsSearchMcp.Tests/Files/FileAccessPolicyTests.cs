using System.Text;
using WsSearchMcp.Files;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests.Files;

public class FileAccessPolicyTests : IDisposable
{
    private readonly TempTree _t = new();
    public void Dispose() => _t.Dispose();

    // ---- dot components ---------------------------------------------------------------------

    [Theory]
    [InlineData(@".a\f.txt")]                    // depth 1
    [InlineData(@"x\.b\f.txt")]                  // depth 2
    [InlineData(@"a\b\c\d\.e\f.txt")]            // depth 5
    [InlineData(@".dotfile")]                    // the file itself
    [InlineData(@"x\.git\config")]
    [InlineData(@"x\.venv\lib\site.py")]
    [InlineData(@".SSH\id_x")]                   // upper-case dot folder
    public void Any_dot_prefixed_component_is_denied_at_any_depth(string relative)
    {
        var d = _t.Policy().CheckPathText(_t.P(relative));
        Assert.Equal(DenyReason.DotComponent, d.Reason);
    }

    [Theory]
    [InlineData(@"my.folder\file.txt")]
    [InlineData(@"Documents\ssh-notes.txt")]
    [InlineData(@"dir\file.with.dots.txt")]
    [InlineData(@"sshx\a.txt")]
    public void Look_alikes_without_a_leading_dot_are_allowed(string relative)
    {
        _t.Text(relative, "x");
        Assert.True(_t.Policy().Check(_t.P(relative)).Allowed);
    }

    // ---- hidden / system --------------------------------------------------------------------

    [Fact]
    public void Hidden_folder_at_depth_one_denies_everything_below_it()
    {
        var f = _t.Text(@"hid\f.txt", "x");
        TempTree.Hide(_t.P("hid"));
        var d = _t.Policy().Check(f);
        Assert.Equal(DenyReason.HiddenOrSystem, d.Reason);
    }

    [Fact]
    public void Hidden_folder_deep_in_the_path_is_denied()
    {
        var f = _t.Text(@"a\b\hid\c\f.txt", "x");
        TempTree.Hide(_t.P(@"a\b\hid"));
        Assert.Equal(DenyReason.HiddenOrSystem, _t.Policy().Check(f).Reason);
    }

    [Fact]
    public void Hidden_file_and_system_file_are_denied()
    {
        var h = _t.Text(@"d\hidden.txt", "x");
        var s = _t.Text(@"d\system.txt", "x");
        var ok = _t.Text(@"d\plain.txt", "x");
        TempTree.Hide(h);
        TempTree.MakeSystem(s);
        Assert.Equal(DenyReason.HiddenOrSystem, _t.Policy().Check(h).Reason);
        Assert.Equal(DenyReason.HiddenOrSystem, _t.Policy().Check(s).Reason);
        Assert.True(_t.Policy().Check(ok).Allowed);
    }

    [Fact]
    public void System_folder_is_denied()
    {
        var f = _t.Text(@"sys\f.txt", "x");
        TempTree.MakeSystem(_t.P("sys"));
        Assert.Equal(DenyReason.HiddenOrSystem, _t.Policy().Check(f).Reason);
    }

    [Fact]
    public void Volume_root_being_hidden_and_system_does_not_cause_a_denial()
    {
        // C:\ is Hidden+System on Windows; it is the root and must not be judged. C:\Windows is not hidden.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Assert.True(File.Exists(explorer));
        Assert.True(new FileAccessPolicy(Array.Empty<string>()).CheckAttributes(explorer).Allowed);
    }

    [Fact]
    public void Missing_path_cannot_be_judged_and_is_denied()
    {
        Assert.False(_t.Policy().CheckAttributes(_t.P(@"nope\f.txt")).Allowed);
    }

    // ---- protected roots --------------------------------------------------------------------

    [Fact]
    public void Protected_root_is_denied_even_when_not_hidden()
    {
        var prot = _t.Dir("prot");
        var f = _t.Text(@"prot\sub\f.txt", "x");
        var look = _t.Text(@"protx\f.txt", "x");
        var policy = _t.Policy(prot);
        Assert.Equal(DenyReason.ProtectedRoot, policy.CheckPathText(f).Reason);
        Assert.Equal(DenyReason.ProtectedRoot, policy.CheckPathText(prot).Reason);
        Assert.Equal(DenyReason.ProtectedRoot, policy.CheckPathText(f.ToUpperInvariant()).Reason); // case-insensitive
        Assert.True(policy.Check(look).Allowed);                                                    // prefix look-alike
    }

    [Fact]
    public void Every_users_AppData_is_protected()
    {
        var users = _t.Dir("Users");
        var policy = new FileAccessPolicy(Array.Empty<string>(), new[] { _t.Root }, users);
        Assert.Equal(DenyReason.ProtectedRoot, policy.CheckPathText(Path.Combine(users, "bob", "AppData", "Roaming", "x.txt")).Reason);
        Assert.Equal(DenyReason.ProtectedRoot, policy.CheckPathText(Path.Combine(users, "alice", "appdata", "x.txt")).Reason);
        Assert.True(policy.CheckPathText(Path.Combine(users, "bob", "Documents", "x.txt")).Allowed);
    }

    [Fact]
    public void Default_policy_protects_AppData_ProgramData_Windows_and_the_servers_own_folder()
    {
        var home = _t.Dir("home"); // WS_SEARCH_HOME can point anywhere, even somewhere plain
        var policy = FileAccessPolicy.CreateDefault(new ServerPaths(home));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] denied =
        {
            Path.Combine(profile, "AppData", "Roaming", "x.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "x.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "x.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "x.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "x.dll"),
            Path.Combine(home, "config.json"),
        };
        foreach (var p in denied)
            Assert.True(policy.CheckPathText(p).Reason == DenyReason.ProtectedRoot, $"expected ProtectedRoot for {p}");

        Assert.True(policy.CheckPathText(Path.Combine(profile, "Documents", "report.docx")).Allowed);
    }

    // ---- credential names -------------------------------------------------------------------

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("id_ecdsa")]
    [InlineData("ID_DSA")]
    [InlineData("cert.pfx")]
    [InlineData("cert.P12")]
    [InlineData("putty.ppk")]
    [InlineData("vault.kdbx")]
    [InlineData("NTUSER.DAT")]
    [InlineData("ntuser.dat.LOG1")]
    public void Credential_style_names_are_denied(string name)
    {
        Assert.Equal(DenyReason.CredentialName, _t.Policy().CheckPathText(_t.P(@"d\" + name)).Reason);
    }

    [Theory]
    [InlineData("id_rsa.pub")]
    [InlineData("pfx.txt")]
    [InlineData("notes-id.txt")]
    public void Public_keys_and_look_alike_names_are_allowed(string name)
    {
        Assert.True(_t.Policy().CheckPathText(_t.P(@"d\" + name)).Allowed);
    }

    // ---- private key sniffing ---------------------------------------------------------------

    [Fact]
    public void Pem_or_key_file_with_a_private_key_is_denied_but_certificates_and_decks_are_not()
    {
        var priv = _t.Text(@"k\a.pem", "-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n");
        var rsa = _t.Text(@"k\b.key", "-----BEGIN RSA PRIVATE KEY-----\nMIIE\n");
        var pub = _t.Text(@"k\c.pem", "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
        var deck = _t.Bytes(@"k\deck.key", new byte[] { 0x50, 0x4B, 3, 4, 0, 1, 2, 3 }); // zip-like Keynote file
        var txt = _t.Text(@"k\notes.txt", "mentions PRIVATE KEY but is not a key file");
        var policy = _t.Policy();

        foreach (var (path, expected) in new[] { (priv, false), (rsa, false), (pub, true), (deck, true), (txt, true) })
        {
            using var s = File.OpenRead(path);
            s.Position = 3;
            Assert.Equal(expected, policy.Check(path, s).Allowed);
            Assert.Equal(3, s.Position); // the sniff restores the stream position
        }
    }

    // ---- path forms -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path.txt")]
    [InlineData(@"..\x.txt")]
    [InlineData(@"C:\a\..\b\x.txt")]
    [InlineData(@"C:\a\file.txt:hidden")]       // alternate data stream
    [InlineData(@"\\.\C:\x.txt")]               // device path
    [InlineData("C:\\a\\\0b.txt")]
    public void Invalid_path_forms_are_refused(string path)
    {
        Assert.Equal(DenyReason.InvalidPath, _t.Policy().CheckPathText(path).Reason);
    }

    [Fact]
    public void Null_path_is_refused()
    {
        Assert.Equal(DenyReason.InvalidPath, _t.Policy().CheckPathText(null).Reason);
    }

    [Fact]
    public void Extended_length_prefix_and_forward_slashes_are_normalized_before_judging()
    {
        var f = _t.Text(@".hidden\f.txt", "x");
        Assert.Equal(DenyReason.DotComponent, _t.Policy().CheckPathText(@"\\?\" + f).Reason);
        Assert.Equal(DenyReason.DotComponent, _t.Policy().CheckPathText(f.Replace('\\', '/')).Reason);
        Assert.True(FileAccessPolicy.TryNormalize(@"\\?\C:\a\b.txt", out var n).Allowed);
        Assert.Equal(@"C:\a\b.txt", n);
        Assert.True(FileAccessPolicy.TryNormalize(@"\\?\UNC\srv\share\b.txt", out var u).Allowed);
        Assert.Equal(@"\\srv\share\b.txt", u);
    }

    [Fact]
    public void Decision_category_never_contains_the_path()
    {
        var d = _t.Policy().CheckPathText(_t.P(@"customer-acme\.secret-stuff\x"));
        Assert.DoesNotContain("customer-acme", d.Category);
        Assert.DoesNotContain("secret-stuff", d.Category);
        Assert.Contains(".secret-stuff", d.Detail); // detail is for the server log only
    }
}
