using System.Text.Json;
using AgentUsage;
using Microsoft.Data.Sqlite;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Check(UsageText.ParseManual("Weekly", "42.5", "2030-01-04 12:00 +00:00")?.UsedPercent == 42.5, "manual limit");
Check(UsageText.ParseManual("Weekly", "101", "2030-01-04 12:00 +00:00") is null, "invalid percent");
Check(UsageText.ParseManual("Weekly", "42", "bad date") is null, "invalid reset");
var login = ProviderSignIn.StartInfo(@"C:\Program Files\Codex\codex.exe", Provider.Codex, @"C:\Profiles\second");
Check(login.FileName == "cmd.exe" && login.Arguments == "/k \"\"C:\\Program Files\\Codex\\codex.exe\" login\"", "Codex login command quoting");
Check(login.Environment["CODEX_HOME"] == @"C:\Profiles\second", "Codex isolated account profile");
string authOutput = "Starting local login server on http://localhost:1455.\nIf your browser did not open, navigate to this URL to authenticate:\n\nhttps://auth.openai.com/oauth/authorize?state=one-time-state\n";
Uri? authUrl = ProviderSignIn.CodexAuthUrl(authOutput);
Uri? localServer = ProviderSignIn.CodexLocalServer(authOutput);
Check(authUrl?.Host == "auth.openai.com" && localServer?.Port == 1455, "Codex official login link and local server");
Check(ProviderSignIn.CodexAuthUrl(authOutput.TrimEnd()) is null, "wait for complete auth link");
Check(ProviderSignIn.CodexAuthUrl("Update: https://example.com/other\n") is null, "unrelated URL must not become login link");
Check(ProviderSignIn.ValidateCodexCallback("http://localhost:1455/auth/callback?code=abc&state=one-time-state", authUrl, localServer) is not null, "matching callback");
Check(ProviderSignIn.ValidateCodexCallback("http://localhost:1455/auth/callback?code=abc&state=wrong", authUrl, localServer) is null, "wrong OAuth state");
Check(ProviderSignIn.ValidateCodexCallback("http://localhost:9999/auth/callback?code=abc&state=one-time-state", authUrl, localServer) is null, "wrong callback port");
Check(ProviderSignIn.ValidateCodexCallback("http://localhost.evil.test:1455/auth/callback?code=abc&state=one-time-state", authUrl, localServer) is null, "callback host confusion");
Uri redirectedAuth = new("https://auth.openai.com/oauth/authorize?state=one-time-state&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fcustom%2Fcallback");
Check(ProviderSignIn.ValidateCodexCallback("http://localhost:1455/custom/callback?code=abc&state=one-time-state", redirectedAuth, localServer) is not null, "provider callback path");

using (JsonDocument doc = JsonDocument.Parse("""{"rate_limit":{"primary_window":{"used_percent":40,"reset_at":1900000000}}}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseCodex(doc.RootElement);
    Check(usage.Windows.Single().UsedPercent == 40, "Codex percent");
    Check(usage.Windows.Single().ResetsAt is not null, "Codex reset");
    Check(usage.Windows.All(w => w.Label != "Weekly"), "missing weekly metric must stay unknown");
}
using (JsonDocument doc = JsonDocument.Parse("""{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":62,"limit_window_seconds":604800,"reset_at":1900000000}},"additional_rate_limits":[{"limit_name":"Model","rate_limit":{"primary_window":{"used_percent":20,"limit_window_seconds":18000,"reset_at":1900000000}}}]}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseCodex(doc.RootElement);
    Check(usage.Windows[0].Label == "Weekly" && usage.Windows[0].UsedPercent == 62, "Codex Pro weekly primary limit");
    Check(usage.Windows[1].Label == "Model · 5-hour", "Codex model window duration");
    Check(usage.Windows.All(w => w.Label != "5-hour"), "Codex Pro must not invent a generic 5-hour limit");
}
using (JsonDocument doc = JsonDocument.Parse("""{"rate_limit_reset_credits":{"available_count":3,"applicable_available_count":2}}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseCodex(doc.RootElement);
    Check(usage.BankedResets == 3 && usage.ApplicableBankedResets == 2, "Codex banked reset counts");
}
using (JsonDocument doc = JsonDocument.Parse("""{"five_hour":{"utilization":18,"resets_at":"2030-01-01T05:00:00Z"},"seven_day":{"utilization":42,"resets_at":"2030-01-07T00:00:00Z"}}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseClaude(doc.RootElement);
    Check(usage.Windows.Count == 2 && usage.Windows[0].UsedPercent == 18 && usage.Windows[1].UsedPercent == 42, "Claude windows");
}
using (JsonDocument doc = JsonDocument.Parse("""{"buckets":[{"modelId":"gemini-pro","remainingFraction":0.35,"resetTime":"2030-01-01T00:00:00Z"},{"modelId":"gemini-flash","remainingFraction":0.8,"resetTime":"2030-01-02T00:00:00Z"}]}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseGemini(doc.RootElement);
    Check(usage.Windows.Single(w => w.Label == "Pro").UsedPercent == 65, "Gemini remaining fraction");
}
using (JsonDocument doc = JsonDocument.Parse("""{"config":{"currentPeriod":{"start":"2030-01-01T00:00:00Z","end":"2030-01-08T00:00:00Z"}}}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseGrok(doc.RootElement);
    Check(usage.Windows.Single().UsedPercent is null, "Grok missing percent must stay unknown");
    Check(usage.Windows.Single().Label == "Weekly credits", "Grok period label");
}
using (JsonDocument doc = JsonDocument.Parse("""{"individualUsage":{"plan":{"totalPercentUsed":0.36,"autoPercentUsed":22,"apiPercentUsed":30}},"billingCycleEnd":"2030-02-01T00:00:00Z","membershipType":"Pro"}"""))
{
    UsageSnapshot usage = UsageFetcher.ParseCursor(doc.RootElement);
    Check(usage.Windows.Count == 3 && usage.Windows[0].UsedPercent == 0.36, "Cursor percent units");
    Check(usage.Plan == "Pro", "Cursor plan");
}

string temporary = Path.Combine(Path.GetTempPath(), "AgentUsage-check-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new AccountStore(temporary);
    var state = new AccountData();
    state.Accounts.Add(new Account { Provider = Provider.Claude, Source = AccountSource.Secret, Label = "test", Secret = "sentinel-secret" });
    state.Accounts.Add(new Account { Provider = Provider.Gemini, Source = AccountSource.Manual, Label = "manual",
        ManualWindows = [new QuotaWindow("Weekly", 42, DateTimeOffset.Parse("2030-01-01T00:00:00Z"))] });
    store.Save(state);
    Check(store.Load().Accounts.Single(a => a.Source == AccountSource.Secret).Secret == "sentinel-secret", "DPAPI round trip");
    Check(store.Load().Accounts.Single(a => a.Source == AccountSource.Manual).ManualWindows.Single().UsedPercent == 42, "manual state round trip");
    byte[] file = File.ReadAllBytes(Path.Combine(temporary, "accounts.dpapi"));
    Check(!System.Text.Encoding.UTF8.GetString(file).Contains("sentinel-secret", StringComparison.Ordinal), "credential must not appear on disk");

    string database = Path.Combine(temporary, "cursor.vscdb");
    using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ItemTable(key TEXT, value TEXT); INSERT INTO ItemTable VALUES('cursorAuth/accessToken','cursor-test-token');";
        command.ExecuteNonQuery();
    }
    Check(UsageFetcher.ReadCursorToken(database) == "cursor-test-token", "Cursor local SQLite sign-in");
    var fetcher = new UsageFetcher();
    string cursorCli = Path.Combine(temporary, "auth.json");
    File.WriteAllText(cursorCli, """{"accessToken":"cursor-cli-test-token"}""");
    Check(fetcher.CredentialScope(new Account { Provider = Provider.Cursor, Source = AccountSource.Profile,
        Label = "CLI", ProfilePath = cursorCli }).Length == 64, "Cursor CLI sign-in");
    var saved = state.Accounts[0];
    string firstScope = fetcher.CredentialScope(saved);
    saved.Secret = "another-secret";
    Check(fetcher.CredentialScope(saved) != firstScope, "credential switch changes account scope");
}
finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }

Console.WriteLine("Synthetic checks passed.");

if (args.Contains("--live-codex"))
{
    var fetcher = new UsageFetcher();
    var account = new Account { Provider = Provider.Codex, Source = AccountSource.Profile, Label = "local" };
    (UsageSnapshot snapshot, _) = await fetcher.FetchAsync(account, CancellationToken.None);
    Console.WriteLine($"Live Codex: plan={(snapshot.Plan is null ? "unknown" : "reported")}; windows={snapshot.Windows.Count}");
    foreach (QuotaWindow window in snapshot.Windows)
        Console.WriteLine($"{window.Label}: percent={(window.UsedPercent is null ? "unknown" : "reported")}, reset={(window.ResetsAt is null ? "unknown" : "reported")}");
}
