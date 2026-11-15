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
    // --input <json> takes precedence; otherwise read the JSON from stdin if it was
    // redirected (this is how the out-of-package breakaway worker receives its args);
    // otherwise default to an empty object.
    string inputJson;
    if (inputIndex >= 0 && inputIndex + 1 < args.Length)
        inputJson = args[inputIndex + 1];
    else if (Console.IsInputRedirected)
        inputJson = Console.In.ReadToEnd();
    else
        inputJson = "{}";
    if (string.IsNullOrWhiteSpace(inputJson)) inputJson = "{}";

    using var doc = JsonDocument.Parse(inputJson);
    var root = doc.RootElement;

    string result;
    try
    {
        result = toolName switch
        {
            "find_files" => WindowsSearchTool.Search(
                text:       root.TryGetProperty("text",       out var q)   ? (q.GetString() ?? "")                                   : "",
                scope:      root.TryGetProperty("scope",      out var sc)  ? (sc.GetString() ?? "<UNSET>")                            : "<UNSET>",
                maxResults: root.TryGetProperty("maxResults", out var mr)  ? mr.GetInt32()                                            : 50,
                mode:       root.TryGetProperty("mode",       out var mo)  ? (mo.GetString() ?? "contains")                          : "contains",
                columns:    root.TryGetProperty("columns",    out var co)  ? (co.GetString() ?? "")                                   : "",
                filters:    root.TryGetProperty("filters",    out var fi)  ? (fi.GetString() ?? "")                                   : "",
                orderBy:    root.TryGetProperty("orderBy",    out var ob)  ? (ob.GetString() ?? "")                                   : "",
                orderDir:   root.TryGetProperty("orderDir",   out var od)  ? (od.GetString() ?? "ASC")                                : "ASC",
                filterLogic: root.TryGetProperty("filterLogic", out var fl) ? (fl.GetString() ?? "AND")                                : "AND"),

            "list_properties" => WindowsSearchTool.ListProperties(
                filter:     root.TryGetProperty("filter",     out var f)   ? f.GetString()                                            : null,
                maxResults: root.TryGetProperty("maxResults", out var mr2) ? mr2.GetInt32()                                           : 500),

            "list_properties_by_family" => WindowsSearchTool.ListPropertiesByFamily(
                family:     root.TryGetProperty("family",     out var fam) ? (fam.GetString() ?? "")                                  : "",
                maxResults: root.TryGetProperty("maxResults", out var mr3) ? mr3.GetInt32()                                           : 500),

            _ => throw new ArgumentException($"Unknown tool '{toolName}'. Known tools: find_files, list_properties, list_properties_by_family.")
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
    .WithToolsFromAssembly(typeof(WindowsSearchTool).Assembly);

await builder.Build().RunAsync();
return 0;
