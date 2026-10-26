using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VPT.Einkr.CairnsAgency.Reddit;

public sealed class RedditException(string message) : Exception(message);

/// <summary>A small client for Reddit's OAuth Data API (https://oauth.reddit.com). Script-app password grant: the token is fetched
/// when needed and reused until shortly before it expires. Calls are spaced at least a second apart (Reddit allows 100 per minute
/// per client) and a 429 is honoured once, using Retry-After.</summary>
public sealed class RedditClient
{
    private readonly HttpClient _http;
    private readonly RedditOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;
    private DateTime _lastCallUtc = DateTime.MinValue;

    public string ApiBase { get; set; } = "https://oauth.reddit.com";
    public string TokenUrl { get; set; } = "https://www.reddit.com/api/v1/access_token";
    public TimeSpan MinSpacing { get; set; } = TimeSpan.FromSeconds(1.1);

    public RedditClient(HttpClient http, RedditOptions options, ILogger log)
    {
        _http = http;
        _options = options;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc - TimeSpan.FromMinutes(2)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _options.Username,
                ["password"] = _options.Password
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));
        request.Headers.TryAddWithoutValidation("User-Agent", _options.EffectiveUserAgent); // Reddit's requested format is not a valid product/version token

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RedditException($"Reddit sign-in failed ({(int)response.StatusCode}). Check the client id/secret, the username/password, and that the app is a \"script\" app owned by that account.");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new RedditException($"Reddit sign-in failed: {err.GetString()}.");
        _token = doc.RootElement.GetProperty("access_token").GetString();
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    /// <summary>GET path (starting with "/") on oauth.reddit.com. raw_json=1 is added so text comes back unescaped.</summary>
    public Task<JsonElement> GetAsync(string pathAndQuery, CancellationToken ct)
        => SendAsync(() =>
        {
            var sep = pathAndQuery.Contains('?') ? '&' : '?';
            return new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{pathAndQuery}{sep}raw_json=1");
        }, ct);

    public Task<JsonElement> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        form["api_type"] = "json";
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}{path}?raw_json=1") { Content = new FormUrlEncodedContent(form) }, ct);
    }

    private async Task<JsonElement> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureTokenAsync(ct);

            for (var attempt = 0; ; attempt++)
            {
                var wait = MinSpacing - (DateTime.UtcNow - _lastCallUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                using var request = build();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.TryAddWithoutValidation("User-Agent", _options.EffectiveUserAgent); // Reddit's requested format is not a valid product/version token

                using var response = await _http.SendAsync(request, ct);
                _lastCallUtc = DateTime.UtcNow;
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    if (retry > TimeSpan.FromSeconds(60)) retry = TimeSpan.FromSeconds(60);
                    _log.LogWarning("Reddit rate limit; waiting {Retry}.", retry);
                    await Task.Delay(retry, ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _token = null; await EnsureTokenAsync(ct); continue; }

                if (!response.IsSuccessStatusCode)
                    throw new RedditException(response.StatusCode switch
                    {
                        HttpStatusCode.Forbidden => "Reddit refused this request (403): the subreddit may be private, banned or quarantined, or the account may not be allowed to do this.",
                        HttpStatusCode.NotFound => "Reddit says that does not exist (404).",
                        HttpStatusCode.TooManyRequests => "Reddit is rate limiting this account right now (429). Try again later.",
                        _ => $"Reddit returned {(int)response.StatusCode}."
                    });

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                return doc.RootElement.Clone();
            }
        }
        finally { _gate.Release(); }
    }
}
