using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency.Einkr;

namespace VPT.Einkr.CairnsAgency.Tools;

/// <summary>Talks to the SAME C# tool host the desktop app uses (VPT.Einkr.Desktop.ToolHost.exe) over the same
/// newline-delimited JSON-RPC-over-stdio protocol (see ToolHost/Program.cs and the desktop app's
/// services/tool-host-client.js). Spawning the real binary rather than re-implementing the tools is what makes "mimic
/// the app" literally true: read_file, outline_file, write_file, edit_file etc. behave byte-for-byte as they do for Josh,
/// including the working-directory containment the host enforces itself.</summary>
public sealed class ToolHostClient : IDisposable
{
    private readonly string _exePath;
    private readonly ILogger<ToolHostClient> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();
    private Process? _child;
    private long _nextId;

    public ToolHostClient(string exePath, ILogger<ToolHostClient> log)
    {
        _exePath = exePath;
        _log = log;
    }

    public bool ExecutableExists => File.Exists(_exePath);

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_child is { HasExited: false }) return;
            if (!File.Exists(_exePath))
                throw new FileNotFoundException($"Tool host not found at '{_exePath}'. Publish VPT.Einkr.Desktop.ToolHost (Release, win-x64) or set Agency:Tools:ToolHostPath.");

            var psi = new ProcessStartInfo(_exePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            var child = new Process { StartInfo = psi, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) HandleLine(e.Data); };
            child.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogDebug("toolhost stderr: {Line}", e.Data); };
            child.Exited += (_, _) =>
            {
                foreach (var (id, tcs) in _pending.ToArray())
                    if (_pending.TryRemove(id, out _)) tcs.TrySetException(new InvalidOperationException("Tool host process exited unexpectedly."));
            };
            child.Start();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            _child = child;
            _log.LogInformation("Tool host started (pid {Pid}).", child.Id);
        }
    }

    private void HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) return;
            if (!_pending.TryRemove(idEl.GetInt64(), out var tcs)) return;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                tcs.TrySetException(new InvalidOperationException(message ?? "Tool host error."));
                return;
            }

            if (root.TryGetProperty("result", out var result))
                tcs.TrySetResult(result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText());
            else
                tcs.TrySetResult(string.Empty);
        }
        catch (JsonException)
        {
            // Not a JSON-RPC line -- ignore, same as the app.
        }
    }

    /// <summary>Runs one file/discovery/write tool rooted at <paramref name="workingDirectory"/>.</summary>
    public async Task<string> CallAsync(string workingDirectory, string method, JsonElement args, CancellationToken ct, TimeSpan? timeout = null)
    {
        EnsureStarted();

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = new { workingDirectory, args }
        }, Json.Options);

        lock (_gate)
        {
            var child = _child ?? throw new InvalidOperationException("Tool host is not running.");
            child.StandardInput.WriteLine(request);
            child.StandardInput.Flush();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        await using var _ = cts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var gone)) gone.TrySetCanceled(cts.Token);
        });
        return await tcs.Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { if (_child is { HasExited: false }) _child.Kill(entireProcessTree: true); } catch { /* shutting down */ }
            _child?.Dispose();
            _child = null;
        }
    }
}
