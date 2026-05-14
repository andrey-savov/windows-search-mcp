using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WsSearchMcp;

// Debug mode: ws-search-mcp.exe --tool <name> [--input <json>]
var toolIndex = Array.IndexOf(args, "--tool");
if (toolIndex >= 0 && toolIndex + 1 < args.Length)
{
    var toolName = args[toolIndex + 1];
    var inputIndex = Array.IndexOf(args, "--input");
    var inputJson = inputIndex >= 0 && inputIndex + 1 < args.Length ? args[inputIndex + 1] : "{}";

    using var doc = JsonDocument.Parse(inputJson);
    var root = doc.RootElement;

    string result;
    try
    {
        result = toolName switch
        {
            "query" => WindowsSearchTool.Search(
                text:       root.TryGetProperty("text",       out var q)   ? (q.GetString() ?? "")                                   : "",
                scope:      root.TryGetProperty("scope",      out var sc)  ? sc.GetString()                                           : null,
                maxResults: root.TryGetProperty("maxResults", out var mr)  ? mr.GetInt32()                                            : 50,
                mode:       root.TryGetProperty("mode",       out var mo)  ? (mo.GetString() ?? "contains")                          : "contains",
                columns:    root.TryGetProperty("columns",    out var co)  ? co.Deserialize<string[]>()                               : null,
                filters:    root.TryGetProperty("filters",    out var fi)  ? fi.Deserialize<SearchFilter[]>()                         : null,
                orderBy:    root.TryGetProperty("orderBy",    out var ob)  ? ob.GetString()                                           : null,
                orderDir:   root.TryGetProperty("orderDir",   out var od)  ? od.GetString()                                           : null),

            "list_properties" => WindowsSearchTool.ListProperties(
                filter:     root.TryGetProperty("filter",     out var f)   ? f.GetString()                                            : null,
                maxResults: root.TryGetProperty("maxResults", out var mr2) ? mr2.GetInt32()                                           : 500),

            "list_properties_by_family" => WindowsSearchTool.ListPropertiesByFamily(
                family:     root.TryGetProperty("family",     out var fam) ? (fam.GetString() ?? "")                                  : "",
                maxResults: root.TryGetProperty("maxResults", out var mr3) ? mr3.GetInt32()                                           : 500),

            _ => throw new ArgumentException($"Unknown tool '{toolName}'. Known tools: search, list_properties, list_properties_by_family.")
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    Console.WriteLine(result);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
