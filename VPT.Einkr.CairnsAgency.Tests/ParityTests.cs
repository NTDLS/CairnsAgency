using System.Text.Json;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Notes;
using VPT.Einkr.CairnsAgency.Runner;
using VPT.Einkr.CairnsAgency.Tools;
using Xunit;

namespace VPT.Einkr.CairnsAgency.Tests;

/// <summary>The C# port must behave exactly like the desktop app's JavaScript. These replay cases whose expected output was
/// produced by running the real renderer code (golden/make-golden.js).</summary>
public class ParityTests
{
    private static JsonDocument Golden(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", name)));

    [Fact]
    public void NotesStore_matches_the_desktop_app_for_every_case()
    {
        using var doc = Golden("notes-cases.json");
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var store = new NotesStore();
            store.Load(c.GetProperty("load").ValueKind == JsonValueKind.Null ? null : c.GetProperty("load").GetString());

            var expectedResults = c.GetProperty("results").EnumerateArray().Select(r => r.GetString()).ToList();
            var writes = c.GetProperty("writes").EnumerateArray().ToList();
            for (var i = 0; i < writes.Count; i++)
                Assert.True(expectedResults[i] == store.Write(writes[i]), $"[{name}] write #{i} result differs");

            // Compare the persisted state SEMANTICALLY (escaping may differ between JS and .NET, the meaning may not).
            Assert.True(JsonEquals(c.GetProperty("json").GetString()!, store.ToJson()), $"[{name}] persisted JSON differs: expected {c.GetProperty("json").GetString()} got {store.ToJson()}");

            var snapshot = c.GetProperty("snapshot");
            if (snapshot.ValueKind == JsonValueKind.Null) Assert.Null(store.Snapshot());
            else Assert.True(JsonEquals(snapshot.GetString()!, store.Snapshot()!), $"[{name}] snapshot differs");

            Assert.True(c.GetProperty("render").GetString() == store.Render(), $"[{name}] render differs");
        }
    }

    private static bool JsonEquals(string a, string b)
    {
        using var da = JsonDocument.Parse(a);
        using var db = JsonDocument.Parse(b);
        return Canonical(da.RootElement) == Canonical(db.RootElement);
    }

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"\"{p.Name}\":{Canonical(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText()
    };

    [Fact]
    public void Pruning_matches_the_desktop_app()
    {
        using var doc = Golden("prune-cases.json");
        foreach (var c in doc.RootElement.GetProperty("prune").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var messages = c.GetProperty("input").EnumerateArray().Select(m => new WireMessage
            {
                Role = m.GetProperty("role").GetString()!,
                Content = m.GetProperty("pruned").GetBoolean()
                    ? "[Older tool result cleared to free context. Call the tool again if you still need it.]"
                    : new string('x', m.GetProperty("length").GetInt32())
            }).ToList();

            var reclaimed = AgentRunner.PruneOldToolResults(messages, c.GetProperty("protect").GetInt32());
            Assert.True(c.GetProperty("reclaimed").GetInt32() == reclaimed, $"[{name}] reclaimed");

            var expected = c.GetProperty("output").EnumerateArray().Select(x => x.GetInt32()).ToList();
            for (var i = 0; i < expected.Count; i++)
            {
                var pruned = messages[i].Content!.StartsWith("[Older tool result cleared");
                Assert.True((expected[i] == -1) == pruned, $"[{name}] message {i} pruned state");
                if (expected[i] != -1) Assert.True(expected[i] == messages[i].Content!.Length, $"[{name}] message {i} length");
            }
        }
    }

    [Fact]
    public void TruncateMiddle_PreviewHead_and_FormatArgs_match_the_desktop_app()
    {
        using var doc = Golden("prune-cases.json");
        var text = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"line {i} {new string('y', i % 37)}"));

        foreach (var c in doc.RootElement.GetProperty("truncate").EnumerateArray())
            Assert.True(c.GetProperty("result").GetString() == AgentRunner.TruncateMiddle(text, c.GetProperty("limit").GetInt32()), $"truncate {c.GetProperty("limit")}");

        foreach (var c in doc.RootElement.GetProperty("preview").EnumerateArray())
            Assert.True(c.GetProperty("result").GetString() == AgentRunner.PreviewHead(text, c.GetProperty("max").GetInt32()), $"preview {c.GetProperty("max")}");

        foreach (var c in doc.RootElement.GetProperty("args").EnumerateArray())
            Assert.True(c.GetProperty("result").GetString() == AgentRunner.FormatArgs(c.GetProperty("args")),
                $"args {c.GetProperty("args")}: expected <{c.GetProperty("result").GetString()}> got <{AgentRunner.FormatArgs(c.GetProperty("args"))}>");
    }

    [Fact]
    public void GetDateTime_matches_the_desktop_app_for_US_zones_and_UTC()
    {
        using var doc = Golden("datetime-cases.json");
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var instant = DateTimeOffset.Parse(c.GetProperty("iso").GetString()!);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(c.GetProperty("windows").GetString()!);
            var actual = AgencyToolExecutor.DescribeCurrentDateTime(instant, zone);
            Assert.True(c.GetProperty("text").GetString() == actual,
                $"{c.GetProperty("iso")} {c.GetProperty("iana")}: expected <{c.GetProperty("text").GetString()}> got <{actual}>");
        }
    }

    [Fact]
    public void Embedded_tool_schemas_are_what_the_renderer_currently_defines()
    {
        // Regenerate from the renderer's own files and compare: if tool-definitions.js changed, re-run Scripts/export-tool-definitions.js.
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VPT.Einkr.CairnsAgency", "Scripts", "export-tool-definitions.js"));
        if (!File.Exists(script)) return; // running from a copied output folder
        // The schemas are exported from the Einkr desktop app's renderer; a checkout without it (this repo on its own) has nothing to compare against.
        if (!Directory.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script)!, "..", "..", "VPT.Einkr.Desktop.Electron", "renderer")))) return;
        var embedded = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script)!, "..", "Resources", "tool-definitions.json"));

        var tempScript = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.js");
        var tempOut = Path.Combine(Path.GetTempPath(), $"tools-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempScript, File.ReadAllText(script)
                .Replace("path.join(__dirname, '..', 'Resources', 'tool-definitions.json')", JsonSerializer.Serialize(tempOut))
                .Replace("path.join(__dirname, '..', '..', 'VPT.Einkr.Desktop.Electron', 'renderer')", JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script)!, "..", "..", "VPT.Einkr.Desktop.Electron", "renderer")))));
            var psi = new System.Diagnostics.ProcessStartInfo("node", $"\"{tempScript}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(30000);
            Assert.Equal(0, p.ExitCode);
            Assert.True(File.ReadAllText(embedded).ReplaceLineEndings() == File.ReadAllText(tempOut).ReplaceLineEndings(),
                "Resources/tool-definitions.json is stale -- run: node Scripts/export-tool-definitions.js");
        }
        catch (System.ComponentModel.Win32Exception) { /* node not installed: nothing to compare against */ }
        finally { File.Delete(tempScript); File.Delete(tempOut); }
    }
}
