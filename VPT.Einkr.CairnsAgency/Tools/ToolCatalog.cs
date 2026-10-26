using System.Reflection;
using System.Text.Json;
using VPT.Einkr.CairnsAgency.Agency;

namespace VPT.Einkr.CairnsAgency.Tools;

/// <summary>The tool schemas offered to the model. The file/web/notes tools are loaded from tool-definitions.json -- exported
/// verbatim from the desktop app's own renderer (Scripts/export-tool-definitions.js) -- and selected by the same
/// category rules app.js's #buildToolBindingCore applies to a Chat session. The two agency-only tools are defined here.</summary>
public sealed class ToolCatalog
{
    public const string SetWakeInterval = "set_wake_interval";
    public const string PauseUntilJoshReturns = "pause_until_josh_returns";

    private readonly List<JsonElement> _local = [];
    private readonly List<JsonElement> _web = [];
    private readonly List<JsonElement> _toolOutput = [];
    private readonly List<JsonElement> _utility = [];

    public ToolCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tool-definitions.json")
            ?? throw new InvalidOperationException("tool-definitions.json is not embedded in the assembly.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var t in doc.RootElement.GetProperty("local").EnumerateArray()) _local.Add(t.Clone());
        foreach (var t in doc.RootElement.GetProperty("web").EnumerateArray()) _web.Add(t.Clone());
        foreach (var t in doc.RootElement.GetProperty("toolOutput").EnumerateArray()) _toolOutput.Add(t.Clone());
        foreach (var t in doc.RootElement.GetProperty("utility").EnumerateArray()) _utility.Add(t.Clone());
    }

    private static string CategoryOf(JsonElement t) => t.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
    public static string NameOf(JsonElement t) => t.GetProperty("function").GetProperty("name").GetString() ?? "";

    /// <summary>What a Chat session with these toggles is offered: [local tools by category] + web + the tool-output look-around
    /// tools + get_date_time (every binding gets the last two), exactly as #buildToolBinding assembles them. The agency's own
    /// two tools follow.</summary>
    public List<JsonElement> Build(ToolOptions options, int minIntervalMinutes = 480, IEnumerable<JsonElement>? extra = null)
    {
        var categories = new HashSet<string>();
        if (options.ShellExecute) categories.Add("shell");
        if (options.Notes) categories.Add("notes");
        if (options.Writable) { categories.Add("discovery"); categories.Add("write"); }

        var offered = new List<JsonElement>();
        offered.AddRange(_local.Where(t => categories.Contains(CategoryOf(t))
            && !(options.ForbidDelete && NameOf(t) == "delete_path")));
        if (options.Web) offered.AddRange(_web);
        offered.AddRange(_toolOutput);
        offered.AddRange(_utility);
        if (extra != null) offered.AddRange(extra);
        offered.AddRange(AgencyTools(minIntervalMinutes));
        return offered;
    }

    private static List<JsonElement> AgencyTools(int minIntervalMinutes)
    {
        const string setInterval = """
        {
          "type": "function",
          "function": {
            "name": "set_wake_interval",
            "description": "Set how many hours the Cairns Agency waits before the next wake. Takes effect when this wake ends. The shortest interval the agency allows is {MIN}. If you ask for less it applies the minimum and tells you what it applied. There is no maximum. `reason` is optional and is recorded in the agency log for Josh.",
            "parameters": {
              "type": "object",
              "properties": {
                "hours": { "type": "number", "description": "Hours until the next wake (fractions allowed, e.g. 0.5)." },
                "reason": { "type": "string", "description": "One sentence on why. Optional." }
              },
              "required": ["hours"]
            }
          }
        }
        """;
        const string pause = """
        {
          "type": "function",
          "function": {
            "name": "pause_until_josh_returns",
            "description": "Stop the Cairns Agency wakes until Josh next writes to this session; they resume on their own when he does. The current wake can finish normally and your final message is saved for Josh. `reason` is optional and is recorded in the agency log.",
            "parameters": {
              "type": "object",
              "properties": {
                "reason": { "type": "string", "description": "One honest sentence. Optional." }
              }
            }
          }
        }
        """;
        var minText = minIntervalMinutes % 60 == 0 ? $"{minIntervalMinutes / 60} hours" : $"{minIntervalMinutes} minutes";
        return [JsonDocument.Parse(setInterval.Replace("{MIN}", minText)).RootElement.Clone(), JsonDocument.Parse(pause).RootElement.Clone()];
    }
}
