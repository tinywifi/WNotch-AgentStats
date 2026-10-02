using System.Text.Json.Serialization;

namespace AgentUsage;

internal enum Provider { Codex, Claude, Gemini, Grok, Cursor }
internal enum AccountSource { Profile, Secret, Manual }

internal sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public AccountSource Source { get; set; }
    public string Label { get; set; } = "";
    public string? Email { get; set; }
    public string? ProfilePath { get; set; }
    public string? Secret { get; set; }
    public string? ManualPlan { get; set; }
    public List<QuotaWindow> ManualWindows { get; set; } = [];
    public bool DefaultProfile { get; set; }
}

internal sealed class AccountData
{
    public List<Account> Accounts { get; set; } = [];
    public HashSet<Provider> HiddenDefaults { get; set; } = [];
}

internal sealed record QuotaWindow(string Label, double? UsedPercent, DateTimeOffset? ResetsAt);

internal sealed record UsageSnapshot(
    string? Plan,
    string? Identity,
    IReadOnlyList<QuotaWindow> Windows,
    DateTimeOffset CheckedAt,
    string? Error = null,
    bool Manual = false,
    long? BankedResets = null,
    long? ApplicableBankedResets = null);

internal static class UsageText
{
    public static string Percent(double? value) => value is null ? "Unavailable" : $"{value:0.#}% used · {Math.Max(0, 100 - value.Value):0.#}% left";

    public static string Reset(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "Reset unavailable";
        TimeSpan left = at.Value - now;
        if (left <= TimeSpan.Zero) return "Reset due; refresh for current quota";
        string relative = left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : $"{(int)left.TotalHours}h {left.Minutes}m";
        return $"Resets in {relative} ({at.Value.ToLocalTime():g})";
    }

    public static QuotaWindow? ParseManual(string label, string percent, string reset)
    {
        if (!double.TryParse(percent, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double used) || !double.IsFinite(used) || used is < 0 or > 100)
            return null;
        if (!DateTimeOffset.TryParse(reset, System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out DateTimeOffset at))
            return null;
        return new(label, used, at);
    }
}
