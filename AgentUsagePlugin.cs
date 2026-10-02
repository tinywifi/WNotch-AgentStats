using System.Net;
using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace AgentUsage;

public sealed class AgentUsagePlugin : INotchPlugin
{
    private const string PageId = "usage";
    private readonly object _gate = new();
    private readonly UsageFetcher _fetcher = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Guid, UsageSnapshot> _snapshots = [];
    private readonly Dictionary<Guid, string> _scopes = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextRefresh = [];
    private IPluginHost? _host;
    private AccountStore? _store;
    private AccountData _data = new();
    private Timer? _timer;
    private bool _stopped;
    private View _view;
    private Guid _selected;
    private int _overviewPage;
    private string? _notice;
    private PendingLogin? _pendingLogin;

    public void Start(IPluginHost host)
    {
        _host = host;
        _store = new AccountStore(host.DataDirectory);
        _data = _store.Load();
        bool added = false;
        foreach (Account account in _data.Accounts.Where(a => a.DefaultProfile))
        {
            string oldLabel = $"{ProviderName(account.Provider)} (local)";
            if (account.Label != oldLabel && account.Label != "Default" &&
                !(account.Provider == Provider.Codex && account.Label == "ChatGPT / Codex (local)")) continue;
            account.Label = "System default";
            added = true;
        }
        foreach (Provider provider in Enum.GetValues<Provider>())
        {
            added |= AddDefaultIfFound(provider, false);
        }
        added |= DiscoverManagedProfiles(host.DataDirectory);
        if (added) _store.Save(_data);
        Render();
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public void Stop()
    {
        lock (_gate) _stopped = true;
        _stop.Cancel();
        if (_pendingLogin is { } pending) TryKill(pending.Process);
        _timer?.Dispose();
    }

    private bool AddDefaultIfFound(Provider provider, bool explicitDetect)
    {
        string? path = UsageFetcher.DefaultProfile(provider);
        if (path is null || !File.Exists(path) || _data.Accounts.Any(a => a.Provider == provider && a.DefaultProfile) ||
            (!explicitDetect && _data.HiddenDefaults.Contains(provider))) return false;
        _data.HiddenDefaults.Remove(provider);
        _data.Accounts.Add(new Account { Provider = provider, Source = AccountSource.Profile,
            DefaultProfile = true, Label = "System default" });
        return true;
    }

    private bool DiscoverManagedProfiles(string dataDirectory)
    {
        bool added = false;
        foreach (Provider provider in new[] { Provider.Codex, Provider.Claude })
        {
            string root = Path.Combine(dataDirectory, "profiles", provider.ToString().ToLowerInvariant());
            if (!Directory.Exists(root)) continue;
            string[] profiles;
            try { profiles = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string profile in profiles)
            {
                if (_data.Accounts.Any(a => a.Provider == provider &&
                    string.Equals(a.ProfilePath, profile, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0) continue;
                    _fetcher.CredentialScope(new Account { Provider = provider, Source = AccountSource.Profile,
                        Label = "Recovered", ProfilePath = profile });
                    _data.Accounts.Add(new Account { Provider = provider, Source = AccountSource.Profile,
                        Label = NextLabel(provider), ProfilePath = profile });
                    added = true;
                }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { /* Incomplete sign-ins are not accounts. */ }
            }
        }
        return added;
    }

    private string NextLabel(Provider provider)
    {
        string label = $"Account {_data.Accounts.Count(a => a.Provider == provider) + 1}";
        while (_data.Accounts.Any(a => a.Provider == provider && a.Label == label)) label += "+";
        return label;
    }

    private void Tick()
    {
        try
        {
            Render();
            _ = RefreshAll(false);
        }
        catch (Exception ex) { _host?.Log.Warn($"Agent Usage timer error: {ex.GetType().Name}"); }
    }

    private async Task RefreshAll(bool force)
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            Account[] accounts;
            lock (_gate) accounts = _data.Accounts.ToArray();
            foreach (Account account in accounts)
            {
                if (_stop.IsCancellationRequested) break;
                if (account.Source == AccountSource.Manual) continue;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                lock (_gate)
                    if (!force && _nextRefresh.TryGetValue(account.Id, out DateTimeOffset next) && next > now) continue;
                try
                {
                    (UsageSnapshot snapshot, string scope) = await _fetcher.FetchAsync(account, _stop.Token);
                    lock (_gate)
                    {
                        if (_data.Accounts.Any(a => a.Id == account.Id))
                        {
                            _snapshots[account.Id] = snapshot;
                            _scopes[account.Id] = scope;
                            _nextRefresh[account.Id] = DateTimeOffset.UtcNow.AddMinutes(5);
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    string error = ex switch
                    {
                        HttpRequestException http => http.Message,
                        TaskCanceledException => "Provider timed out.",
                        FileNotFoundException file => file.Message,
                        InvalidOperationException invalid => invalid.Message,
                        _ => $"Could not read usage ({ex.GetType().Name})."
                    };
                    lock (_gate)
                    {
                        if (_data.Accounts.Any(a => a.Id == account.Id))
                        {
                            _snapshots.TryGetValue(account.Id, out UsageSnapshot? previous);
                            string? scope = null;
                            try { scope = _fetcher.CredentialScope(account); } catch { /* Sign-in file may be missing. */ }
                            if (scope is null || !_scopes.TryGetValue(account.Id, out string? savedScope) || savedScope != scope)
                                previous = null;
                            _snapshots[account.Id] = previous is null
                                ? new(null, null, [], DateTimeOffset.UtcNow, error)
                                : previous with { Error = error };
                            _nextRefresh[account.Id] = DateTimeOffset.UtcNow.AddMinutes(ex switch
                            {
                                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => 15,
                                FileNotFoundException => 1,
                                _ => 5
                            });
                        }
                    }
                }
                Render();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host?.Log.Warn($"Agent Usage refresh failed: {ex.GetType().Name}");
        }
        finally { _refreshGate.Release(); }
    }

    private void Render()
    {
        lock (_gate)
        {
            if (_stopped || _host is null) return;
            PluginPage page = _view switch
            {
                View.Add => BuildAdd(),
                View.Detail => BuildDetail(),
                _ => BuildOverview()
            };
            _host.Pages.Set(page);
        }
    }

    private PluginPage BuildOverview()
    {
        Account[] accounts = _data.Accounts.ToArray();
        const int pageSize = 5;
        int pageCount = Math.Max(1, (accounts.Length + pageSize - 1) / pageSize);
        _overviewPage = Math.Clamp(_overviewPage, 0, pageCount - 1);
        var blocks = new List<PluginBlock>();
        foreach (Account account in accounts.Skip(_overviewPage * pageSize).Take(pageSize))
        {
            UsageSnapshot? snapshot = Current(account);
            IReadOnlyList<QuotaWindow> windows = snapshot?.Windows ?? [];
            blocks.Add(new PluginText { Text = $"{ProviderName(account.Provider)} · {account.Label}", Style = PluginTextStyle.Heading });
            blocks.Add(new PluginValueRow { Label = "Email", Value = account.Email ?? snapshot?.Identity ?? "Unavailable" });
            blocks.Add(new PluginValueRow { Label = "Plan", Value = snapshot?.Plan ?? account.ManualPlan ?? "Unavailable" });
            blocks.Add(new PluginTextField { Label = "Account name", Value = account.Label, SubmitLabel = "Rename",
                Submitted = value => Ui(() => SaveLabel(account, value)) });
            AddBankedResets(blocks, snapshot, account.Provider, false);
            if (snapshot?.Error is not null)
                blocks.Add(new PluginText { Text = "Stale / unavailable: " + snapshot.Error, Style = PluginTextStyle.Muted, Color = GlowColor.Amber });
            if (windows.Count == 0)
                blocks.Add(new PluginText { Text = snapshot is null ? "Awaiting refresh" : "No measured quota available", Style = PluginTextStyle.Muted });
            foreach (QuotaWindow window in windows.Take(7))
            {
                if (window.UsedPercent is null)
                    blocks.Add(new PluginValueRow { Label = window.Label, Value = "Usage unavailable" });
                else
                    blocks.Add(new PluginProgress { Label = window.Label, Value = UsageText.Percent(window.UsedPercent),
                        Progress = Math.Clamp(window.UsedPercent.Value / 100, 0, 1),
                        Color = window.UsedPercent >= 90 ? GlowColor.Red : null });
                blocks.Add(new PluginText { Text = UsageText.Reset(window.ResetsAt, DateTimeOffset.UtcNow), Style = PluginTextStyle.Muted });
            }
            blocks.Add(new PluginButtons { Actions =
            [
                new PluginAction { Label = "Move up", Enabled = Array.IndexOf(accounts, account) > 0,
                    Clicked = () => Ui(() => MoveAccount(account, -1)) },
                new PluginAction { Label = "Move down", Enabled = Array.IndexOf(accounts, account) < accounts.Length - 1,
                    Clicked = () => Ui(() => MoveAccount(account, 1)) },
                new PluginAction { Label = windows.Count > 7 ? "Details · more limits" : "Details",
                    Clicked = () => Ui(() => { _selected = account.Id; _view = View.Detail; }) }
            ] });
        }
        if (accounts.Length == 0)
            blocks.Add(new PluginText { Text = "No accounts found. Add a provider account to start.", Style = PluginTextStyle.Muted });
        var actions = new List<PluginAction>
        {
            new() { Label = "Accounts & sign-in", Clicked = () => Ui(() => { _view = View.Add; _notice = null; }) },
            new() { Label = "Refresh", Clicked = () => _ = RefreshAll(true) }
        };
        if (_overviewPage > 0) actions.Add(new PluginAction { Label = "Previous", Clicked = () => Ui(() => _overviewPage--) });
        if (_overviewPage < pageCount - 1) actions.Add(new PluginAction { Label = "Next", Clicked = () => Ui(() => _overviewPage++) });
        return new PluginPage
        {
            Id = PageId,
            Title = "Agent Usage",
            Status = _notice ?? $"Accounts {(_overviewPage * pageSize + Math.Min(1, accounts.Length))}–{Math.Min((_overviewPage + 1) * pageSize, accounts.Length)} of {accounts.Length} · refreshes every 5 minutes",
            Actions = actions,
            Blocks = blocks
        };
    }

    private PluginPage BuildDetail()
    {
        Account? account = _data.Accounts.FirstOrDefault(a => a.Id == _selected);
        if (account is null) { _view = View.Overview; return BuildOverview(); }
        UsageSnapshot? snapshot = Current(account);
        IReadOnlyList<QuotaWindow> windows = account.Source == AccountSource.Manual ? account.ManualWindows : snapshot?.Windows ?? [];
        var blocks = new List<PluginBlock>
        {
            new PluginText { Text = $"{ProviderName(account.Provider)} · {account.Label}", Style = PluginTextStyle.Heading },
            new PluginValueRow { Label = "Plan", Value = snapshot?.Plan ?? account.ManualPlan ?? "Unavailable" },
            new PluginValueRow { Label = "Updated", Value = account.Source == AccountSource.Manual ? "Manual entry" : snapshot is null ? "Not yet" : snapshot.CheckedAt.ToLocalTime().ToString("g") },
        };
        blocks.Add(new PluginValueRow { Label = "Email", Value = account.Email ?? snapshot?.Identity ?? "Unavailable" });
        AddBankedResets(blocks, snapshot, account.Provider, true);
        blocks.Add(new PluginTextField { Label = "Email override", Value = account.Email, Hint = "Optional, submit to save or clear",
            Submitted = value => Ui(() => SaveEmail(account, value)) });
        if (snapshot?.Error is not null) blocks.Add(new PluginText { Text = "Stale / unavailable: " + snapshot.Error, Style = PluginTextStyle.Muted, Color = GlowColor.Amber });
        if (windows.Count == 0) blocks.Add(new PluginText { Text = "No measured quota windows are available for this account.", Style = PluginTextStyle.Muted });
        foreach (QuotaWindow window in windows.Take(20))
        {
            if (window.UsedPercent is null) blocks.Add(new PluginValueRow { Label = window.Label, Value = "Usage unavailable" });
            else blocks.Add(new PluginProgress { Label = window.Label, Value = UsageText.Percent(window.UsedPercent),
                Progress = Math.Clamp(window.UsedPercent.Value / 100, 0, 1),
                Color = window.UsedPercent >= 90 ? GlowColor.Red : null });
            blocks.Add(new PluginText { Text = UsageText.Reset(window.ResetsAt, DateTimeOffset.UtcNow), Style = PluginTextStyle.Muted });
        }
        return new PluginPage
        {
            Id = PageId, Title = "Agent Usage", Status = _notice,
            Back = () => Ui(() => { _view = View.Overview; _notice = null; }), BackLabel = "All accounts",
            Actions =
            [
                new PluginAction { Label = "Refresh", Enabled = account.Source != AccountSource.Manual,
                    Clicked = () => _ = RefreshAll(true) },
                new PluginAction { Label = "Sign in again", Enabled = account.Source == AccountSource.Profile,
                    Clicked = () => Ui(() => StartProviderSignIn(account.Provider, account)) },
                new PluginAction { Label = "Remove", Color = GlowColor.Red, Confirm = true,
                    Clicked = () => Ui(() => Remove(account.Id)) }
            ],
            Stats = windows.Take(8).Select(w => new PluginStat { Label = w.Label, Value = w.UsedPercent is null ? "?" : $"{w.UsedPercent:0.#}%",
                Detail = w.ResetsAt?.ToLocalTime().ToString("g"), Progress = w.UsedPercent is null ? null : w.UsedPercent / 100 }).ToArray(),
            Blocks = blocks
        };
    }

    private PluginPage BuildAdd()
    {
        var blocks = new List<PluginBlock>
        {
            new PluginText { Text = "Provider accounts", Style = PluginTextStyle.Heading },
            new PluginText { Text = "Existing CLI and app sign-ins are detected locally. Add a separate Codex or Claude account only if you want another login; credentials stay on this PC.", Style = PluginTextStyle.Muted }
        };
        foreach (Provider provider in Enum.GetValues<Provider>())
        {
            Account? system = _data.Accounts.FirstOrDefault(a => a.Provider == provider && a.DefaultProfile);
            UsageSnapshot? snapshot = system is null ? null : Current(system);
            blocks.Add(new PluginSeparator());
            blocks.Add(new PluginText { Text = ProviderName(provider), Style = PluginTextStyle.Heading });
            blocks.Add(new PluginText { Text = provider switch
            {
                Provider.Codex => "Official Codex browser sign-in; each added account gets its own local context.",
                Provider.Claude => "Official Claude Code sign-in; each added account gets its own local context.",
                Provider.Gemini => "Uses the Gemini CLI sign-in already on this computer. Individual quota reporting may be unavailable from Google.",
                Provider.Grok => "Uses ~/.grok/auth.json. Sign in with Grok CLI, then detect it here.",
                _ => "Uses the Cursor IDE or cursor-agent sign-in already on this computer."
            }, Style = PluginTextStyle.Muted });
            blocks.Add(new PluginValueRow { Label = "System default", Value = system is null ? "Not detected" :
                system.Email ?? snapshot?.Identity ?? "Detected" });
            Account[] managed = _data.Accounts.Where(a => a.Provider == provider && !a.DefaultProfile).ToArray();
            foreach (Account account in managed.Take(5))
                blocks.Add(new PluginValueRow { Label = account.Label,
                    Value = account.Email ?? Current(account)?.Identity ?? "Awaiting refresh" });
            if (managed.Length > 5)
                blocks.Add(new PluginText { Text = $"{managed.Length - 5} more account(s) on the overview.", Style = PluginTextStyle.Muted });
            blocks.Add(new PluginButtons { Actions =
            [
                new PluginAction { Label = provider is Provider.Codex or Provider.Claude ? "Sign in with OAuth" : "Open provider client",
                    Clicked = () => Ui(() => StartProviderSignIn(provider)) },
                new PluginAction { Label = "Detect local sign-in", Clicked = () => Ui(() => DetectLocal(provider)) }
            ] });
            if (_pendingLogin is { } pending && pending.Provider == provider)
            {
                blocks.Add(new PluginText { Text = "Waiting for the provider's browser sign-in. Your default account is unchanged.", Style = PluginTextStyle.Muted });
                if (pending.AuthUrl is { } url)
                {
                    blocks.Add(new PluginText { Text = url.ToString(), Style = PluginTextStyle.Code });
                    blocks.Add(new PluginButtons { Actions =
                    [
                        new PluginAction { Label = "Open link", Clicked = () => Ui(() => OpenAuthUrl(url)) },
                        new PluginAction { Label = "Copy link", Clicked = () => Ui(() => CopyAuthUrl(url)) }
                    ] });
                }
                if (provider == Provider.Codex && pending.AuthUrl is not null && pending.LocalServer is not null)
                    blocks.Add(new PluginTextField { Label = "Callback URL from another device (optional)", Secret = true,
                        Hint = "Paste the full localhost redirect URL", SubmitLabel = "Complete sign-in",
                        Submitted = value => Ui(() => _ = ForwardCodexCallback(value)) });
                blocks.Add(new PluginButtons { Actions = [new PluginAction { Label = "Cancel sign-in", Clicked = () => Ui(CancelPendingLogin) }] });
            }
        }
        return new PluginPage
        {
            Id = PageId, Title = "Agent Usage", Status = _notice,
            Back = () => Ui(() => { _view = View.Overview; _notice = null; }), BackLabel = "All accounts",
            Blocks = blocks
        };
    }

    private void DetectLocal(Provider provider)
    {
        Account? existing = _data.Accounts.FirstOrDefault(a => a.Provider == provider && a.DefaultProfile);
        bool wasHidden = _data.HiddenDefaults.Contains(provider);
        if (existing is null && AddDefaultIfFound(provider, true))
        {
            try { Save(); }
            catch
            {
                _data.Accounts.RemoveAll(a => a.Provider == provider && a.DefaultProfile);
                if (wasHidden) _data.HiddenDefaults.Add(provider);
                throw;
            }
            _notice = $"{ProviderName(provider)} local sign-in detected.";
        }
        else _notice = existing is null || !File.Exists(UsageFetcher.DefaultProfile(provider))
            ? $"No {ProviderName(provider)} local sign-in found. Sign in with its client first." : "Local sign-in refreshed.";
        _ = RefreshAll(true);
    }

    private void StartProviderSignIn(Provider provider, Account? reconnect = null)
    {
        if (_pendingLogin is not null) { _notice = "Finish or cancel the current sign-in first."; return; }
        string? executable = ProviderSignIn.FindExecutable(provider);
        if (executable is null)
        { _notice = $"{ProviderName(provider)} app/CLI is not installed. Install its official client and sign in there."; return; }
        if (provider is not (Provider.Codex or Provider.Claude))
        {
            using Process? client = Process.Start(ProviderSignIn.StartInfo(executable, provider, null));
            _notice = client is null ? "Could not open the provider client." : "Sign in with the provider client, then press Detect local sign-in.";
            return;
        }
        string profile = reconnect?.ProfilePath is { } path
            ? Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path
            : reconnect?.DefaultProfile == true
                ? Path.GetDirectoryName(UsageFetcher.DefaultProfile(provider)!)!
                : Path.Combine(_host!.DataDirectory, "profiles", provider.ToString().ToLowerInvariant(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string authPath = Path.Combine(profile, provider == Provider.Codex ? "auth.json" : ".credentials.json");
        var pending = new PendingLogin(provider, profile, reconnect, authPath, AuthHash(authPath));
        ProcessStartInfo start = provider == Provider.Codex
            ? ProviderSignIn.CapturedCodexStartInfo(executable, profile)
            : ProviderSignIn.StartInfo(executable, provider, profile);
        pending.Process = Process.Start(start) ?? throw new InvalidOperationException("Could not start provider sign-in.");
        _pendingLogin = pending;
        _view = View.Add;
        _notice = $"{ProviderName(provider)} sign-in started. Complete it in the official browser flow.";
        if (provider == Provider.Codex) _ = ReadCodexOutput(pending);
        _ = WatchLogin(pending);
    }

    private async Task ReadCodexOutput(PendingLogin pending)
    {
        async Task Read(StreamReader stream, bool parse)
        {
            try
            {
                char[] buffer = new char[1024];
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), _stop.Token)) > 0)
                {
                    if (!parse) continue;
                    lock (_gate)
                    {
                        if (_pendingLogin != pending || _stopped) return;
                        string output = pending.Output + new string(buffer, 0, count);
                        pending.Output = output[^Math.Min(8192, output.Length)..];
                        pending.AuthUrl ??= ProviderSignIn.CodexAuthUrl(pending.Output);
                        pending.LocalServer ??= ProviderSignIn.CodexLocalServer(pending.Output);
                        if (pending.AuthUrl is not null) Render();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_gate) if (_pendingLogin == pending && !_stopped)
                    _host?.Log.Warn($"Codex login output could not be read ({ex.GetType().Name}).");
            }
        }
        await Task.WhenAll(Read(pending.Process.StandardOutput, true), Read(pending.Process.StandardError, false));
    }

    private async Task WatchLogin(PendingLogin pending)
    {
        DateTimeOffset? exitedAt = null;
        try
        {
            for (int attempt = 0; attempt < 360 && !_stop.IsCancellationRequested; attempt++)
            {
                lock (_gate) if (_pendingLogin != pending) return;
                string? currentHash = AuthHash(pending.AuthPath);
                if (currentHash is not null && currentHash != pending.InitialHash)
                {
                    try
                    {
                        _fetcher.CredentialScope(new Account { Provider = pending.Provider, Source = AccountSource.Profile,
                            ProfilePath = pending.Profile, Label = "Pending" });
                        Ui(() => CompleteLogin(pending));
                        lock (_gate)
                        {
                            if (_pendingLogin == pending)
                            {
                                _pendingLogin = null;
                                _notice = "Sign-in succeeded, but the account could not be saved. Try again.";
                                Render();
                            }
                        }
                        await Task.Delay(1500, _stop.Token);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
                    { /* Provider may still be writing its credential file. */ }
                }
                if (pending.Process.HasExited)
                {
                    exitedAt ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - exitedAt > TimeSpan.FromSeconds(3)) break;
                }
                await Task.Delay(500, _stop.Token);
            }
            Ui(() =>
            {
                if (_pendingLogin != pending) return;
                _pendingLogin = null;
                _notice = pending.Process.HasExited ? $"{ProviderName(pending.Provider)} sign-in did not complete. Try again." :
                    "Sign-in timed out. Try again.";
            });
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Ui(() => { if (_pendingLogin == pending) { _pendingLogin = null; _notice = $"Sign-in failed ({ex.GetType().Name})."; } });
        }
        finally { TryKill(pending.Process); pending.Process.Dispose(); }
    }

    private void CompleteLogin(PendingLogin pending)
    {
        if (_pendingLogin != pending) return;
        Account? account = pending.Reconnect;
        if (account is null)
        {
            account = new Account { Provider = pending.Provider, Source = AccountSource.Profile,
                Label = NextLabel(pending.Provider), ProfilePath = pending.Profile };
            _data.Accounts.Add(account);
            try { Save(); } catch { _data.Accounts.Remove(account); throw; }
        }
        _pendingLogin = null;
        _selected = account.Id;
        _view = View.Overview;
        _notice = $"{ProviderName(pending.Provider)} account connected.";
        _ = RefreshAll(true);
    }

    private void CancelPendingLogin()
    {
        if (_pendingLogin is not { } pending) return;
        string? currentHash = AuthHash(pending.AuthPath);
        if (currentHash is not null && currentHash != pending.InitialHash)
        {
            try
            {
                _fetcher.CredentialScope(new Account { Provider = pending.Provider, Source = AccountSource.Profile,
                    Label = "Pending", ProfilePath = pending.Profile });
                CompleteLogin(pending);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException) { }
        }
        _pendingLogin = null;
        TryKill(pending.Process);
        _notice = "Sign-in cancelled. Your existing accounts were not changed.";
    }

    private static string? AuthHash(string path)
    {
        try { return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void OpenAuthUrl(Uri url)
    {
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        _notice = "Opened the provider sign-in link in your browser.";
    }

    private void CopyAuthUrl(Uri url)
    {
        using Process process = Process.Start(new ProcessStartInfo("clip.exe")
        { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Could not open clipboard tool.");
        process.StandardInput.Write(url.ToString());
        process.StandardInput.Close();
        if (!process.WaitForExit(3000) || process.ExitCode != 0) throw new InvalidOperationException("Could not copy sign-in link.");
        _notice = "Sign-in link copied. Open it in another browser or device.";
    }

    private async Task ForwardCodexCallback(string pasted)
    {
        PendingLogin? pending;
        Uri? callback;
        lock (_gate)
        {
            pending = _pendingLogin;
            callback = pending is null ? null : ProviderSignIn.ValidateCodexCallback(pasted, pending.AuthUrl, pending.LocalServer);
            if (callback is null) { _notice = "That callback URL does not match the active Codex sign-in."; Render(); return; }
            _notice = "Sending callback to the local Codex sign-in server...";
            Render();
        }
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using HttpResponseMessage response = await client.GetAsync(callback, _stop.Token);
            Ui(() => { if (_pendingLogin == pending) _notice = response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400
                ? "Callback received. Waiting for Codex to finish sign-in." : "Codex rejected that callback. Check the full URL and try again."; });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { Ui(() => { if (_pendingLogin == pending) _notice = "Local Codex callback could not be reached. Is sign-in still open?"; }); }
    }

    private void Remove(Guid id)
    {
        Account? account = _data.Accounts.FirstOrDefault(a => a.Id == id);
        if (account is null) return;
        int index = _data.Accounts.IndexOf(account);
        _data.Accounts.Remove(account);
        bool addedHidden = account.DefaultProfile && _data.HiddenDefaults.Add(account.Provider);
        try { Save(); }
        catch
        {
            _data.Accounts.Insert(index, account);
            if (addedHidden) _data.HiddenDefaults.Remove(account.Provider);
            throw;
        }
        _snapshots.Remove(id); _scopes.Remove(id); _nextRefresh.Remove(id);
        _selected = Guid.Empty; _view = View.Overview; _notice = "Account removed.";
    }

    private void SaveEmail(Account account, string value)
    {
        string email = Clean(value, 254);
        if (!ValidEmail(email)) { _notice = "Enter a valid email address, or clear it to use the provider's address."; return; }
        string? previous = account.Email;
        account.Email = email.Length == 0 ? null : email;
        try { Save(); } catch { account.Email = previous; throw; }
        _notice = "Email saved.";
    }

    private void SaveLabel(Account account, string value)
    {
        string label = Clean(value, 60);
        if (label.Length == 0) { _notice = "Account name cannot be empty."; return; }
        string previous = account.Label;
        account.Label = label;
        try { Save(); } catch { account.Label = previous; throw; }
        _notice = "Account renamed.";
    }

    private void MoveAccount(Account account, int direction)
    {
        int index = _data.Accounts.IndexOf(account);
        int next = index + direction;
        if (index < 0 || next < 0 || next >= _data.Accounts.Count) return;
        (_data.Accounts[index], _data.Accounts[next]) = (_data.Accounts[next], _data.Accounts[index]);
        try { Save(); }
        catch
        {
            (_data.Accounts[index], _data.Accounts[next]) = (_data.Accounts[next], _data.Accounts[index]);
            throw;
        }
        _overviewPage = next / 5;
    }

    private static void AddBankedResets(List<PluginBlock> blocks, UsageSnapshot? snapshot, Provider provider, bool details)
    {
        if (provider != Provider.Codex || snapshot?.BankedResets is not long count) return;
        string available = snapshot.ApplicableBankedResets is long applicable
            ? $"{count} banked · {applicable} usable now" : $"{count} banked";
        blocks.Add(new PluginValueRow { Label = "Banked resets", Value = available });
        if (count <= 0) return;
        IReadOnlyList<DateTimeOffset?>? expiries = snapshot.BankedResetExpiries;
        if (expiries is null || expiries.Count == 0)
        {
            blocks.Add(new PluginText { Text = "Individual expiry dates are unavailable right now.", Style = PluginTextStyle.Muted });
            return;
        }
        int visible = details ? expiries.Count : Math.Min(3, expiries.Count);
        for (int i = 0; i < visible; i++)
            blocks.Add(new PluginValueRow { Label = $"Banked reset {i + 1}", Value = UsageText.Expiry(expiries[i], DateTimeOffset.UtcNow) });
        if (expiries.Count > visible)
            blocks.Add(new PluginText { Text = $"{expiries.Count - visible} more in Details", Style = PluginTextStyle.Muted });
        else if (expiries.Count < count)
            blocks.Add(new PluginText { Text = $"{count - expiries.Count} reset(s) have no individual expiry details.", Style = PluginTextStyle.Muted });
    }

    private static bool ValidEmail(string email) => email.Length == 0 ||
        (System.Net.Mail.MailAddress.TryCreate(email, out var address) && address.Address == email);

    private UsageSnapshot? Current(Account account) => account.Source == AccountSource.Manual
        ? new(account.ManualPlan, account.Label, account.ManualWindows, DateTimeOffset.UtcNow, Manual: true)
        : _snapshots.GetValueOrDefault(account.Id);

    private void Save() => _store!.Save(_data);

    private void Ui(Action action)
    {
        lock (_gate)
        {
            if (_stopped) return;
            try { action(); }
            catch (Exception ex)
            {
                _notice = $"Could not save change ({ex.GetType().Name}).";
                _host?.Log.Warn($"Agent Usage action failed: {ex.GetType().Name}");
            }
            Render();
        }
    }

    private static string Clean(string value, int max)
    {
        value = value.Trim();
        if (value.Length > max || value.Any(char.IsControl)) throw new ArgumentException("Input is too long or contains control characters.");
        return value;
    }

    private static string ProviderName(Provider provider) => provider switch
    {
        Provider.Codex => "Codex",
        Provider.Claude => "Claude Code",
        Provider.Gemini => "Gemini CLI",
        Provider.Grok => "Grok CLI",
        Provider.Cursor => "Cursor",
        _ => provider.ToString()
    };

    private sealed class PendingLogin(Provider provider, string profile, Account? reconnect, string authPath, string? initialHash)
    {
        public Provider Provider { get; } = provider;
        public string Profile { get; } = profile;
        public Account? Reconnect { get; } = reconnect;
        public string AuthPath { get; } = authPath;
        public string? InitialHash { get; } = initialHash;
        public Process Process { get; set; } = null!;
        public string Output { get; set; } = "";
        public Uri? AuthUrl { get; set; }
        public Uri? LocalServer { get; set; }
    }

    private enum View { Overview, Detail, Add }
}
