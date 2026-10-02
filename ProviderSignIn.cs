using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentUsage;

internal static class ProviderSignIn
{
    public static string? FindExecutable(Provider provider)
    {
        string name = provider switch
        {
            Provider.Codex => "codex",
            Provider.Claude => "claude",
            Provider.Gemini => "gemini",
            Provider.Grok => "grok",
            Provider.Cursor => "cursor-agent",
            _ => ""
        };
        if (name.Length == 0) return null;
        if (provider == Provider.Codex)
        {
            string bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            if (File.Exists(bundled)) return bundled;
        }
        if (provider == Provider.Cursor)
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "cursor", "Cursor.exe");
            if (File.Exists(local)) return local;
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string command in provider == Provider.Cursor ? new[] { "cursor-agent", "Cursor" } : new[] { name })
            foreach (string suffix in new[] { ".exe", ".cmd", ".bat" })
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string path = Path.Combine(directory.Trim('"'), command + suffix);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static ProcessStartInfo StartInfo(string executable, Provider provider, string? profile)
    {
        if (provider == Provider.Cursor && !Path.GetFileName(executable).StartsWith("cursor-agent", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo(executable) { UseShellExecute = true };
        string arguments = provider switch
        {
            Provider.Codex => "login",
            Provider.Claude => "auth login --claudeai",
            Provider.Grok => "login",
            Provider.Cursor when Path.GetFileName(executable).StartsWith("cursor-agent", StringComparison.OrdinalIgnoreCase) => "login",
            _ => ""
        };
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            Arguments = $"/k \"\"{executable}\" {arguments}\""
        };
        if (provider == Provider.Codex && profile is not null) start.Environment["CODEX_HOME"] = profile;
        if (provider == Provider.Claude && profile is not null) start.Environment["CLAUDE_CONFIG_DIR"] = profile;
        return start;
    }

    public static ProcessStartInfo CapturedCodexStartInfo(string executable, string profile)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add("login");
        else
        {
            start.FileName = "cmd.exe";
            start.Arguments = $"/c \"\"{executable}\" login\"";
        }
        start.Environment["CODEX_HOME"] = profile;
        return start;
    }

    public static Uri? CodexAuthUrl(string output)
    {
        const string markerText = "navigate to this URL to authenticate:";
        string plain = Regex.Replace(output, "\u001b\\[[0-9;]*[A-Za-z]", "");
        int marker = plain.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        Match match = Regex.Match(plain[(marker + markerText.Length)..], @"(https://\S+?)\s");
        if (!match.Success || !Uri.TryCreate(match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')', ']'), UriKind.Absolute, out Uri? url))
            return null;
        return url.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    public static Uri? CodexLocalServer(string output)
    {
        Match match = Regex.Match(output, @"Starting local login server on (http://localhost:\d+)", RegexOptions.IgnoreCase);
        return match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    public static Uri? ValidateCodexCallback(string pasted, Uri? authUrl, Uri? localServer)
    {
        if (pasted.Length > 8192 || authUrl is null || localServer is null) return null;
        Uri expected = Uri.TryCreate(Query(authUrl, "redirect_uri"), UriKind.Absolute, out Uri? redirect)
            ? redirect : new Uri(localServer, "/auth/callback");
        if (!expected.IsLoopback || expected.Scheme != Uri.UriSchemeHttp || expected.Port != localServer.Port ||
            !Uri.TryCreate(pasted.Trim(), UriKind.Absolute, out Uri? callback) ||
            callback.Scheme != Uri.UriSchemeHttp || callback.UserInfo.Length > 0 || callback.Fragment.Length > 0 ||
            !callback.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) ||
            callback.Port != expected.Port || callback.AbsolutePath != expected.AbsolutePath) return null;
        string? expectedState = Query(authUrl, "state");
        return !string.IsNullOrEmpty(expectedState) && Query(callback, "state") == expectedState &&
            !string.IsNullOrEmpty(Query(callback, "code")) ? callback : null;
    }

    private static string? Query(Uri uri, string key)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equal = part.IndexOf('=');
            if (equal <= 0 || part[..equal] != key) continue;
            return Uri.UnescapeDataString(part[(equal + 1)..]);
        }
        return null;
    }
}
