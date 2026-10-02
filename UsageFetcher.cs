using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentUsage;

internal sealed class UsageFetcher
{
    static UsageFetcher() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static string? DefaultProfile(Provider provider)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return provider switch
        {
            Provider.Codex => Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "auth.json"),
            Provider.Claude => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), ".credentials.json"),
            Provider.Gemini => Path.Combine(home, ".gemini", "oauth_creds.json"),
            Provider.Grok => Path.Combine(Environment.GetEnvironmentVariable("GROK_HOME") ?? Path.Combine(home, ".grok"), "auth.json"),
            Provider.Cursor => CursorDefaultProfile(),
            _ => null
        };
    }

    private static string CursorDefaultProfile()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor");
        string cli = Path.Combine(root, "auth.json");
        return File.Exists(cli) ? cli : Path.Combine(root, "User", "globalStorage", "state.vscdb");
    }

    public string CredentialScope(Account account) => Fingerprint(Resolve(account).Token);

    public async Task<(UsageSnapshot Snapshot, string Scope)> FetchAsync(Account account, CancellationToken cancellation)
    {
        if (account.Source == AccountSource.Manual)
            return (new(account.ManualPlan, account.Label, account.ManualWindows.ToArray(), DateTimeOffset.UtcNow, Manual: true), "manual");

        Credential credential = Resolve(account);
        string scope = Fingerprint(credential.Token);
        UsageSnapshot snapshot = account.Provider switch
        {
            Provider.Codex => await FetchCodex(credential, cancellation),
            Provider.Claude => await FetchClaude(credential, cancellation),
            Provider.Gemini => await FetchGemini(credential, cancellation),
            Provider.Grok => await FetchGrok(credential, cancellation),
            Provider.Cursor => await FetchCursor(credential, cancellation),
            _ => throw new InvalidOperationException("Unknown provider.")
        };
        if (account.Source == AccountSource.Profile && CredentialScope(account) != scope)
            throw new InvalidOperationException("Account sign-in changed during refresh; refresh again.");
        return (snapshot, scope);
    }

    private static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Credential Resolve(Account account)
    {
        if (account.Source == AccountSource.Secret)
        {
            if (string.IsNullOrWhiteSpace(account.Secret)) throw new InvalidOperationException("No saved credential.");
            string token = account.Secret.Trim();
            if (account.Provider != Provider.Codex) return new(token);
            JsonElement claims = JwtClaims(token);
            return new(token, JsonValue.String(claims, "email"),
                JsonValue.String(claims, "https://api.openai.com/auth", "chatgpt_plan_type"),
                JsonValue.String(claims, "https://api.openai.com/auth", "chatgpt_account_id"));
        }

        string path = account.ProfilePath ?? DefaultProfile(account.Provider) ?? throw new InvalidOperationException("No profile path.");
        if (Directory.Exists(path)) path = Path.Combine(path, Path.GetFileName(DefaultProfile(account.Provider)!));
        if (!File.Exists(path)) throw new FileNotFoundException("Sign-in file is missing. Sign in with the provider and refresh.");
        if (account.Provider == Provider.Cursor)
        {
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using JsonDocument cursorAuth = JsonDocument.Parse(File.ReadAllText(path));
                string? token = JsonValue.String(cursorAuth.RootElement, "accessToken");
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Cursor CLI sign-in is unavailable.");
                string config = Path.Combine(Path.GetDirectoryName(path)!, "cli-config.json");
                string? email = null;
                if (File.Exists(config))
                {
                    using JsonDocument cursorConfig = JsonDocument.Parse(File.ReadAllText(config));
                    email = JsonValue.String(cursorConfig.RootElement, "authInfo", "email");
                }
                return new(token, email);
            }
            return new(ReadCursorToken(path));
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return account.Provider switch
        {
            Provider.Codex => ReadCodex(root),
            Provider.Claude => ReadClaude(root),
            Provider.Gemini => ReadGemini(root),
            Provider.Grok => ReadGrok(root),
            _ => throw new InvalidOperationException("Unknown provider.")
        };
    }

    private static Credential ReadCodex(JsonElement root)
    {
        string? access = JsonValue.String(root, "tokens", "access_token");
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("Codex OAuth sign-in is unavailable; API keys do not report subscription quotas.");
        JsonElement claims = JwtClaims(JsonValue.String(root, "tokens", "id_token"));
        return new(access, JsonValue.String(claims, "email"),
            JsonValue.String(claims, "https://api.openai.com/auth", "chatgpt_plan_type"),
            JsonValue.String(root, "tokens", "account_id"));
    }

    private static Credential ReadClaude(JsonElement root)
    {
        JsonElement oauth = JsonValue.At(root, "claudeAiOauth");
        string? token = JsonValue.String(oauth, "accessToken");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Claude Code OAuth sign-in is unavailable.");
        long? expiry = JsonValue.Long(oauth, "expiresAt");
        if (expiry > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiry.Value) <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Claude Code token expired. Sign in again with Claude Code.");
        return new(token, JsonValue.String(root, "oauthAccount", "emailAddress"),
            JsonValue.String(root, "oauthAccount", "subscriptionType"));
    }

    private static Credential ReadGemini(JsonElement root)
    {
        string? token = JsonValue.String(root, "access_token");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Gemini CLI OAuth sign-in is unavailable.");
        long? expiry = JsonValue.Long(root, "expiry_date");
        if (expiry > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiry.Value) <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Gemini CLI token expired. Open Gemini CLI to renew its sign-in.");
        JsonElement claims = JwtClaims(JsonValue.String(root, "id_token"));
        return new(token, JsonValue.String(claims, "email"),
            JsonValue.String(claims, "hd") is null ? null : "Workspace");
    }

    private static Credential ReadGrok(JsonElement root)
    {
        foreach (JsonProperty entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            string? token = JsonValue.String(entry.Value, "key");
            if (string.IsNullOrWhiteSpace(token)) continue;
            DateTimeOffset? expires = JsonValue.Date(entry.Value, "expires_at");
            if (expires <= DateTimeOffset.UtcNow) continue;
            return new(token, JsonValue.String(entry.Value, "email"));
        }
        throw new InvalidOperationException("Grok CLI sign-in is unavailable or expired.");
    }

    internal static string ReadCursorToken(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1";
        object? value = command.ExecuteScalar();
        string? token = value switch
        {
            string text => text,
            byte[] bytes when bytes.Length > 2 && bytes[1] == 0 => Encoding.Unicode.GetString(bytes),
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Cursor app sign-in is unavailable.");
        return token.Trim().Trim('"');
    }

    private static async Task<UsageSnapshot> FetchCodex(Credential auth, CancellationToken cancellation)
    {
        using JsonDocument doc = await Get("https://chatgpt.com/backend-api/wham/usage", auth.Token, false, cancellation, auth.AccountId);
        return ParseCodex(doc.RootElement, auth.Plan, auth.Identity);
    }

    internal static UsageSnapshot ParseCodex(JsonElement root, string? plan = null, string? identity = null)
    {
        JsonElement limits = JsonValue.At(root, "rate_limit");
        var windows = new List<QuotaWindow>();
        JsonElement primary = JsonValue.At(limits, "primary_window");
        JsonElement secondary = JsonValue.At(limits, "secondary_window");
        Add(windows, CodexWindowLabel(primary, "Primary limit"), primary, "used_percent", "reset_at");
        Add(windows, CodexWindowLabel(secondary, "Secondary limit"), secondary, "used_percent", "reset_at");
        JsonElement extras = JsonValue.At(root, "additional_rate_limits");
        if (extras.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement extra in extras.EnumerateArray())
            {
                string label = JsonValue.String(extra, "limit_name") ?? JsonValue.String(extra, "name") ?? "Model limit";
                JsonElement modelPrimary = JsonValue.At(extra, "rate_limit", "primary_window");
                JsonElement modelSecondary = JsonValue.At(extra, "rate_limit", "secondary_window");
                Add(windows, $"{label} · {CodexWindowLabel(modelPrimary, "primary limit")}", modelPrimary, "used_percent", "reset_at");
                Add(windows, $"{label} · {CodexWindowLabel(modelSecondary, "secondary limit")}", modelSecondary, "used_percent", "reset_at");
            }
        }
        JsonElement banked = JsonValue.At(root, "rate_limit_reset_credits");
        return new(JsonValue.String(root, "plan_type") ?? plan, identity, windows, DateTimeOffset.UtcNow,
            BankedResets: JsonValue.Long(banked, "available_count"),
            ApplicableBankedResets: JsonValue.Long(banked, "applicable_available_count"));
    }

    private static string CodexWindowLabel(JsonElement window, string fallback)
    {
        long? seconds = JsonValue.Long(window, "limit_window_seconds");
        if (seconds is null or <= 0) return fallback;
        return seconds.Value switch
        {
            18_000 => "5-hour",
            604_800 => "Weekly",
            >= 86_400 when seconds.Value % 86_400 == 0 => $"{seconds.Value / 86_400}-day",
            >= 3_600 when seconds.Value % 3_600 == 0 => $"{seconds.Value / 3_600}-hour",
            _ => $"{seconds.Value}-second"
        };
    }

    private static async Task<UsageSnapshot> FetchClaude(Credential auth, CancellationToken cancellation)
    {
        using JsonDocument doc = await Get("https://api.anthropic.com/api/oauth/usage", auth.Token, false, cancellation, beta: "oauth-2025-04-20");
        UsageSnapshot snapshot = ParseClaude(doc.RootElement, auth.Plan, auth.Identity);
        using JsonDocument? profile = await OptionalGet("https://api.anthropic.com/api/oauth/profile", auth.Token, false, cancellation, beta: "oauth-2025-04-20");
        if (profile is not null)
        {
            JsonElement root = profile.RootElement;
            snapshot = snapshot with
            {
                Plan = JsonValue.String(root, "subscriptionType") ?? JsonValue.String(root, "account", "subscriptionType") ?? snapshot.Plan,
                Identity = JsonValue.String(root, "email") ?? JsonValue.String(root, "account", "email") ?? snapshot.Identity
            };
        }
        return snapshot;
    }

    internal static UsageSnapshot ParseClaude(JsonElement root, string? plan = null, string? identity = null)
    {
        var windows = new List<QuotaWindow>();
        foreach ((string key, string label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Weekly"), ("seven_day_opus", "Opus weekly"), ("seven_day_sonnet", "Sonnet weekly") })
            Add(windows, label, JsonValue.At(root, key), "utilization", "resets_at");
        JsonElement scoped = JsonValue.At(root, "limits");
        if (scoped.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement limit in scoped.EnumerateArray())
            {
                string name = JsonValue.String(limit, "name") ?? JsonValue.String(limit, "model") ?? "Model";
                Add(windows, name, limit, "utilization", "resets_at");
            }
        }
        return new(plan, identity, windows, DateTimeOffset.UtcNow);
    }

    private static async Task<UsageSnapshot> FetchGemini(Credential auth, CancellationToken cancellation)
    {
        using JsonDocument tierDoc = await Post("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist", auth.Token,
            "{\"metadata\":{\"ideType\":\"GEMINI_CLI\",\"pluginType\":\"GEMINI\"}}", cancellation);
        JsonElement tier = tierDoc.RootElement;
        JsonElement ineligible = JsonValue.At(tier, "ineligibleTiers");
        if (ineligible.ValueKind == JsonValueKind.Array && auth.Plan != "Workspace" &&
            JsonValue.String(tier, "paidTier", "name") is null &&
            JsonValue.String(tier, "currentTier", "id") != "standard-tier" &&
            ineligible.EnumerateArray().Any(x => JsonValue.String(x, "reasonCode") == "UNSUPPORTED_CLIENT"))
            throw new InvalidOperationException("Google no longer supplies Gemini CLI quota for this individual plan. Use manual tracking.");
        string? project = JsonValue.String(tier, "cloudaicompanionProject");
        string body = project is null ? "{}" : JsonSerializer.Serialize(new { project });
        using JsonDocument quotaDoc = await Post("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota", auth.Token, body, cancellation);
        string? plan = JsonValue.String(tier, "paidTier", "name") ?? JsonValue.String(tier, "currentTier", "name") ?? JsonValue.String(tier, "currentTier", "id") ?? auth.Plan;
        return ParseGemini(quotaDoc.RootElement, plan, auth.Identity);
    }

    internal static UsageSnapshot ParseGemini(JsonElement root, string? plan = null, string? identity = null)
    {
        JsonElement buckets = JsonValue.At(root, "buckets");
        var windows = new Dictionary<string, QuotaWindow>();
        if (buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                string model = JsonValue.String(bucket, "modelId") ?? "Gemini";
                string label = model.Contains("lite", StringComparison.OrdinalIgnoreCase) ? "Flash Lite" :
                    model.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "Flash" : "Pro";
                double? remaining = JsonValue.Double(bucket, "remainingFraction");
                if (remaining is null or < 0 or > 1) continue;
                var window = new QuotaWindow(label, (1 - remaining.Value) * 100, JsonValue.Date(bucket, "resetTime"));
                if (!windows.TryGetValue(label, out QuotaWindow? old) || window.UsedPercent > old.UsedPercent) windows[label] = window;
            }
        }
        return new(plan, identity, windows.Values.ToArray(), DateTimeOffset.UtcNow);
    }

    private static async Task<UsageSnapshot> FetchGrok(Credential auth, CancellationToken cancellation)
    {
        using JsonDocument doc = await Get("https://cli-chat-proxy.grok.com/v1/billing?format=credits", auth.Token, false, cancellation, grok: true);
        UsageSnapshot snapshot = ParseGrok(doc.RootElement, auth.Identity);
        using JsonDocument? settings = await OptionalGet("https://cli-chat-proxy.grok.com/v1/settings", auth.Token, false, cancellation, grok: true);
        if (settings is not null)
        {
            snapshot = snapshot with { Plan = JsonValue.String(settings.RootElement, "subscription_tier_display") ?? snapshot.Plan };
        }
        return snapshot;
    }

    internal static UsageSnapshot ParseGrok(JsonElement root, string? identity = null)
    {
        JsonElement config = JsonValue.At(root, "config");
        double? percent = JsonValue.Double(config, "creditUsagePercent");
        if (percent is null)
        {
            double? used = JsonValue.Double(config, "onDemandUsed", "val");
            double? cap = JsonValue.Double(config, "onDemandCap", "val");
            if (used >= 0 && cap > 0) percent = used / cap * 100;
        }
        DateTimeOffset? start = JsonValue.Date(config, "currentPeriod", "start") ?? JsonValue.Date(config, "billingPeriodStart");
        DateTimeOffset? end = JsonValue.Date(config, "currentPeriod", "end") ?? JsonValue.Date(config, "billingPeriodEnd");
        TimeSpan? period = end - start;
        string label = period is null || period <= TimeSpan.Zero ? "Credits" : period.Value.TotalDays <= 9 ? "Weekly credits" : "Monthly credits";
        var windows = new List<QuotaWindow>();
        if (percent >= 0 || end is not null) windows.Add(new(label, percent >= 0 ? percent : null, end));
        return new(JsonValue.String(config, "subscriptionTier"), identity, windows, DateTimeOffset.UtcNow);
    }

    private static async Task<UsageSnapshot> FetchCursor(Credential auth, CancellationToken cancellation)
    {
        string cookie = CursorCookie(auth.Token);
        using JsonDocument doc = await Get("https://cursor.com/api/usage-summary", cookie, true, cancellation);
        UsageSnapshot snapshot = ParseCursor(doc.RootElement) with { Identity = auth.Identity };
        using JsonDocument? profile = await OptionalGet("https://cursor.com/api/auth/me", cookie, true, cancellation);
        if (profile is not null)
            snapshot = snapshot with { Identity = JsonValue.String(profile.RootElement, "email") ?? JsonValue.String(profile.RootElement, "user", "email") ?? snapshot.Identity };
        return snapshot;
    }

    internal static UsageSnapshot ParseCursor(JsonElement root)
    {
        JsonElement plan = JsonValue.At(root, "individualUsage", "plan");
        double? used = JsonValue.Double(plan, "totalPercentUsed");
        if (used is null)
        {
            double? spent = JsonValue.Double(plan, "used");
            double? limit = JsonValue.Double(plan, "limit");
            if (spent >= 0 && limit > 0) used = spent / limit * 100;
        }
        DateTimeOffset? end = JsonValue.Date(root, "billingCycleEnd");
        var windows = new List<QuotaWindow>();
        if (used >= 0 || end is not null) windows.Add(new("Monthly plan", used >= 0 ? used : null, end));
        Add(windows, "Cursor models", plan, "autoPercentUsed", "billingCycleEnd", end);
        Add(windows, "Third-party models", plan, "apiPercentUsed", "billingCycleEnd", end);
        return new(JsonValue.String(root, "membershipType"), null, windows, DateTimeOffset.UtcNow);
    }

    private static string CursorCookie(string token)
    {
        string clean = token.Trim();
        if (clean.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) clean = clean[7..].Trim();
        if (!clean.Contains('=')) return "WorkosCursorSessionToken=" + clean;
        string? value = clean.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(x =>
            x.StartsWith("WorkosCursorSessionToken=", StringComparison.Ordinal) ||
            x.StartsWith("__Secure-next-auth.session-token=", StringComparison.Ordinal) ||
            x.StartsWith("next-auth.session-token=", StringComparison.Ordinal));
        return value ?? throw new InvalidOperationException("Cursor session must contain a supported session cookie.");
    }

    private static void Add(List<QuotaWindow> windows, string label, JsonElement source, string percentKey, string resetKey, DateTimeOffset? defaultReset = null)
    {
        double? used = JsonValue.Double(source, percentKey);
        DateTimeOffset? reset = JsonValue.Date(source, resetKey) ?? defaultReset;
        if (used < 0) used = null;
        if (used is not null || reset is not null) windows.Add(new(label, used, reset));
    }

    private static JsonElement JwtClaims(string? token)
    {
        if (token is null) return default;
        string[] parts = token.Split('.');
        if (parts.Length != 3) return default;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return default; }
    }

    private static async Task<JsonDocument> Get(string url, string secret, bool cookie, CancellationToken cancellation,
        string? accountId = null, string? beta = null, bool grok = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, secret, cookie, accountId, beta, grok);
        return await Send(request, cancellation);
    }

    private static async Task<JsonDocument?> OptionalGet(string url, string secret, bool cookie, CancellationToken cancellation,
        string? beta = null, bool grok = false)
    {
        using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        shortTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await Get(url, secret, cookie, shortTimeout.Token, beta: beta, grok: grok); }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is (HttpRequestException or TaskCanceledException or JsonException)) { return null; }
    }

    private static async Task<JsonDocument> Post(string url, string bearer, string body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        ApplyAuth(request, bearer, false, null, null, false);
        return await Send(request, cancellation);
    }

    private static void ApplyAuth(HttpRequestMessage request, string secret, bool cookie, string? accountId, string? beta, bool grok)
    {
        if (cookie) request.Headers.TryAddWithoutValidation("Cookie", secret);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (!string.IsNullOrWhiteSpace(accountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        if (beta is not null) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        if (grok) request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            string reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Sign-in rejected; reconnect this account.",
                HttpStatusCode.TooManyRequests => "Provider rate limit; try again later.",
                _ => $"Provider returned HTTP {(int)response.StatusCode}."
            };
            throw new HttpRequestException(reason, null, response.StatusCode);
        }
        await response.Content.LoadIntoBufferAsync(1_000_000, cancellation);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 48 }, cancellation);
    }

    private sealed record Credential(string Token, string? Identity = null, string? Plan = null, string? AccountId = null);
}

internal static class JsonValue
{
    public static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (string part in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return default;
        }
        return value;
    }

    public static string? String(JsonElement value, params string[] path)
    {
        JsonElement found = At(value, path);
        return found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    }

    public static double? Double(JsonElement value, params string[] path)
    {
        JsonElement found = At(value, path);
        if (found.ValueKind == JsonValueKind.Number && found.TryGetDouble(out double number) && double.IsFinite(number)) return number;
        if (found.ValueKind == JsonValueKind.String && double.TryParse(found.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return number;
        return null;
    }

    public static long? Long(JsonElement value, params string[] path)
    {
        JsonElement found = At(value, path);
        return found.ValueKind == JsonValueKind.Number && found.TryGetInt64(out long number) ? number : null;
    }

    public static DateTimeOffset? Date(JsonElement value, params string[] path)
    {
        JsonElement found = At(value, path);
        if (found.ValueKind == JsonValueKind.Number && found.TryGetInt64(out long unix))
        {
            try { return unix > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (found.ValueKind == JsonValueKind.String && long.TryParse(found.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long numeric))
        {
            try { return numeric > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return found.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(found.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date : null;
    }
}
