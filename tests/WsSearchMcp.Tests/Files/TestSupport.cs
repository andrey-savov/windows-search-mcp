using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WsSearchMcp.Files;
using WsSearchMcp.Security;

namespace WsSearchMcp.Tests.Files;

/// <summary>
/// A throwaway folder tree. Its location sits inside AppData\Local\Temp, so policies built from it
/// treat the tree root as an "exempt base": only what lies below it is judged.
/// </summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        // Built from the known folder (always the long-name form) so it matches GetFinalPathNameByHandle output.
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "wssearch-ft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string P(string relative) => Path.Combine(Root, relative);

    public string Dir(string relative)
    {
        var d = P(relative);
        Directory.CreateDirectory(d);
        return d;
    }

    public string Text(string relative, string content, Encoding? encoding = null)
    {
        var p = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, encoding ?? new UTF8Encoding(false));
        return p;
    }

    public string Bytes(string relative, byte[] content)
    {
        var p = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
        return p;
    }

    public static void Hide(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

    public static void MakeSystem(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.System);

    public string Junction(string linkRelative, string target)
    {
        var link = P(linkRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidOperationException("mklink /J failed: " + proc.StandardError.ReadToEnd());
        return link;
    }

    /// <summary>A policy for this tree: nothing protected, the tree root exempt.</summary>
    public FileAccessPolicy Policy(params string[] protectedRoots) =>
        new(protectedRoots, new[] { Root }, null);

    public void Dispose()
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 };
            // Remove junctions first (non-recursively) so cleanup never follows a link.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
            {
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false); } catch { }
            }
            foreach (var e in Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                try { File.SetAttributes(e, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A stand-in for the Windows Search index: "indexes" every file or folder that exists on disk.</summary>
public sealed class FakeIndex
{
    public int Calls { get; private set; }
    public List<string> FiltersSeen { get; } = new();
    public IndexedFileResolver Resolver { get; }

    public FakeIndex() => Resolver = new IndexedFileResolver(Search);

    private string Search(string filtersJson, string columns, int maxResults)
    {
        Calls++;
        FiltersSeen.Add(filtersJson);
        using var doc = JsonDocument.Parse(filtersJson);
        var first = doc.RootElement[0];
        if (first.GetProperty("property").GetString() != "System.ItemPathDisplay") return "[]";
        var path = first.GetProperty("value").GetString()!;
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>
                {
                    ["System.ItemPathDisplay"] = fi.FullName, ["System.Size"] = fi.Length,
                    ["System.FileAttributes"] = (int)fi.Attributes, ["System.DateModified"] = fi.LastWriteTimeUtc,
                },
            });
        }
        if (Directory.Exists(path))
        {
            return JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>
                {
                    ["System.ItemPathDisplay"] = path, ["System.Size"] = 0,
                    ["System.FileAttributes"] = (int)(File.GetAttributes(path) | FileAttributes.Directory),
                },
            });
        }
        return "[]";
    }
}

public static class TestClients
{
    public static ClientRecord Make(bool files, string name = "tester") =>
        ClientTokens.Mint(new InMemoryClientStore(), name, null, DateTimeOffset.UtcNow,
            files ? new[] { ClientPermissions.Files } : null).Client;

    /// <summary>A public-only certificate for options objects that need one but never serve TLS.</summary>
    public static X509Certificate2 PublicCert()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = CertificateManager.CreateSelfSigned(key, new[] { "localhost" }, DateTimeOffset.UtcNow);
        return X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
    }
}
