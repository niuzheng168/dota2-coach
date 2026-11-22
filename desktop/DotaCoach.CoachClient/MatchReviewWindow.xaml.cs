using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DotaCoach.TwoPc;

internal partial class MatchReviewWindow : Window
{
    private const int CatalogPageSize = 40;
    private const string Unavailable = "档案损坏或不可读";
    private sealed record Choice(MatchArchiveEntry Entry, string Label);
    private readonly ReviewStore store;
    private readonly MatchReviewText formatter;
    private readonly Func<string?> activeMatch;
    private readonly string bookmark;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim reader = new(1);
    private CancellationTokenSource? readStop, jobStop;
    private Task job = Task.CompletedTask;
    private Task? shutdown;
    private IReadOnlyList<MatchArchiveEntry> catalog = [];
    private MatchReviewTimeline? timeline;
    private MatchArchiveEntry? selected;
    private int catalogPage;
    private bool verified, hasReplay, changing, catalogBusy, indexBusy, allowClose;
    private ReviewMode mode;

    internal MatchReviewWindow(ReviewStore store, Func<string?> activeMatch, HeroNames names, string preferencesFile)
    {
        this.store = store; this.activeMatch = activeMatch;
        bookmark = Path.ChangeExtension(preferencesFile, ".review-last.txt");
        InitializeComponent();
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
        formatter = new MatchReviewText(names);
        KnownPane.Configure(formatter, false); ReplayPane.Configure(formatter, true);
        Filter.ItemsSource = MatchReviewTimeline.Filters.Select(f => f.Label).ToArray(); Filter.SelectedIndex = 0;
        Filter.SelectionChanged += (_, _) => { CheckAudit(); Draw(); };
        Scrubber.ValueChanged += (_, _) => { if (!changing) { CheckAudit(); Draw(); } };
        AtStart.Click += (_, _) => Scrubber.Value = Scrubber.Minimum; AtEnd.Click += (_, _) => Scrubber.Value = Scrubber.Maximum;
        StepBack.Click += (_, _) => Scrubber.Value -= 15; StepForward.Click += (_, _) => Scrubber.Value += 15;
        KnownMode.Checked += (_, _) => ChangeMode(ReviewMode.Known);
        ReplayMode.Checked += (_, _) => ChangeMode(ReviewMode.Replay);
        CompareMode.Checked += (_, _) => ChangeMode(ReviewMode.Compare);
        Matches.SelectionChanged += async (_, _) => { if (!changing) await ChooseAsync(null, ReviewMode.Known); };
        PrevMatches.Click += async (_, _) => await CatalogPageAsync(catalogPage - 1);
        NextMatches.Click += async (_, _) => await CatalogPageAsync(catalogPage + 1);
        SearchId.TextChanged += async (_, _) => { if (!changing) await CatalogPageAsync(0); };
        RefreshMatches.Click += async (_, _) => await RefreshAsync();
        DownloadReplay.Click += async (_, _) => await StartJobAsync(local: false);
        LocalReplay.Click += async (_, _) => await StartJobAsync(local: true);
        CancelReplay.Click += (_, _) => {
            if (jobStop == null) return;
            CancelReplay.IsEnabled = false; Notice.Text = "正在取消；等待离线进程退出并清理临时文件…";
            _ = jobStop.CancelAsync();
        };
        Loaded += async (_, _) => await RefreshAsync(initial: true);
        Activated += (_, _) => CheckAudit();
        Closing += OnClosing;
        SetMode(ReviewMode.Known); Draw(); Controls();
    }

    private void SetMode(ReviewMode next)
    {
        mode = next;
        changing = true;
        KnownMode.IsChecked = next == ReviewMode.Known;
        ReplayMode.IsChecked = next == ReviewMode.Replay; CompareMode.IsChecked = next == ReviewMode.Compare;
        changing = false;
        KnownPane.Visibility = next == ReviewMode.Replay ? Visibility.Collapsed : Visibility.Visible;
        ReplayPane.Visibility = next == ReviewMode.Known ? Visibility.Collapsed : Visibility.Visible;
        KnownWidth.Width = new GridLength(next == ReviewMode.Replay ? 0 : 1, GridUnitType.Star);
        ReplayWidth.Width = new GridLength(next == ReviewMode.Known ? 0 : 1, GridUnitType.Star);
    }

    private async void ChangeMode(ReviewMode wanted)
    {
        if (changing || wanted == mode || lifetime.IsCancellationRequested) return;
        if (wanted != ReviewMode.Known && (!CheckAudit() || !verified || !hasReplay)) { SetMode(ReviewMode.Known); return; }
        await ChooseAsync(Scrubber.Value, wanted);
    }

    private void Draw()
    {
        double at = Scrubber.Value;
        CursorLabel.Text = timeline == null ? "—" : MatchReviewTimeline.Time(at);
        if (timeline == null && (Scrubber.Maximum != 0 || Scrubber.Minimum != 0)) {
            changing = true;
            Scrubber.Minimum = Scrubber.Maximum = Scrubber.Value = 0;
            changing = false;
        }
        var filter = Filter.SelectedIndex >= 0 ? MatchReviewTimeline.Filters[Filter.SelectedIndex].Key : ReviewFilter.All;
        // Clear hidden panes too. In particular no late detail/last-known snapshot survives a backward seek.
        KnownPane.Show(mode == ReviewMode.Replay ? null : timeline?.Known, at, filter);
        ReplayPane.Show(mode == ReviewMode.Known ? null : timeline?.Replay, at, filter);
        AtStart.IsEnabled = StepBack.IsEnabled = timeline != null && at > Scrubber.Minimum;
        AtEnd.IsEnabled = StepForward.IsEnabled = timeline != null && at < Scrubber.Maximum;
    }

    private void Controls()
    {
        bool idle = jobStop == null && !lifetime.IsCancellationRequested, canPick = idle && !catalogBusy;
        Matches.IsEnabled = SearchId.IsEnabled = canPick;
        RefreshMatches.IsEnabled = canPick;
        PrevMatches.IsEnabled = canPick && catalogPage > 0; NextMatches.IsEnabled = canPick && catalogPage < catalogPages - 1;
        KnownMode.IsEnabled = !indexBusy && selected is { Damaged: false };
        bool replayAllowed = KnownMode.IsEnabled && verified && store.AuditReady;
        ReplayMode.IsEnabled = CompareMode.IsEnabled = replayAllowed && hasReplay;
        DownloadReplay.IsEnabled = LocalReplay.IsEnabled = canPick && replayAllowed;
        CancelReplay.Visibility = JobProgress.Visibility = jobStop == null ? Visibility.Collapsed : Visibility.Visible;
        CancelReplay.IsEnabled = jobStop is { IsCancellationRequested: false };
        Scrubber.IsEnabled = timeline != null && Scrubber.Maximum > Scrubber.Minimum;
    }

    // A fault after the window opened revokes replay access. Keeping the independently indexed known prefix is safe.
    private bool CheckAudit()
    {
        if (store.AuditReady) return true;
        verified = hasReplay = false;
        if (jobStop is { IsCancellationRequested: false } running) _ = running.CancelAsync();
        if (mode != ReviewMode.Known) {
            SetMode(ReviewMode.Known);
            if (timeline != null) {
                timeline = new(timeline.Known, new([]));
                var (min, max) = timeline.Range(ReviewMode.Known);
                changing = true; Scrubber.Minimum = min; Scrubber.Maximum = max; changing = false;
            }
            Draw();
        }
        Controls(); Notice.Text = ReviewStore.LiveOnly; return false;
    }

    private void ShowUnavailable(string id)
    {
        MatchStatus.Text = $"{id}　·　{Unavailable} · 录像入口锁定。";
        Notice.Text = "未修改原档案，也不会用新录像覆盖它。可选择其他对局；若已恢复备份或权限，请重启教练端再查看。";
    }

    // A stored sidecar is validated only on explicit access or replacement. Ask the catalog whether the
    // failure was this match's; a rejected new DEM or a global writer fault must not condemn a healthy ID.
    private async Task<bool> ShowIfDamagedAsync(string id, CancellationToken ct)
    {
        try {
            var latest = await ReadAsync(store.List, ct);
            if (ct.IsCancellationRequested || selected?.MatchId != id || !latest.Any(m => m.MatchId == id && m.Damaged)) return false;
            verified = hasReplay = false; timeline = null; SetMode(ReviewMode.Known); Draw();
            catalog = latest; BindPage(id, true); selected = (Matches.SelectedItem as Choice)?.Entry;
            ShowUnavailable(id); Controls(); return true;
        } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException) {
            return false; // Preserve the original error and catalog; do not clear other selections.
        }
    }

    private int catalogPages = 1;
    private void BindPage(string? preferred, bool findPage)
    {
        string query = SearchId.Text.Trim(), active = activeMatch() ?? "";
        var matches = catalog.Where(e => e.MatchId.Contains(query, StringComparison.Ordinal)).ToArray();
        catalogPages = Math.Max(1, (matches.Length + CatalogPageSize - 1) / CatalogPageSize);
        int index = preferred == null ? -1 : Array.FindIndex(matches, e => e.MatchId == preferred);
        catalogPage = findPage && index >= 0 ? index / CatalogPageSize : Math.Clamp(catalogPage, 0, catalogPages - 1);
        var page = matches.Skip(catalogPage * CatalogPageSize).Take(CatalogPageSize).Select(e =>
            new Choice(e, $"{e.MatchId}　·　" + (e.Damaged ? Unavailable : e.Ended ? (e.HasReplay ? "已结束 · 有录像" : "已结束") :
                e.MatchId == active ? "记录中" : "未验证结束"))).ToArray();
        changing = true;
        Matches.ItemsSource = page;
        Matches.SelectedIndex = page.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(page, c => c.Entry.MatchId == preferred));
        changing = false;
        MatchPage.Text = $"{catalogPage + 1}/{catalogPages}";
        Controls();
    }

    private async Task CatalogPageAsync(int page)
    {
        if (catalogBusy || jobStop != null || lifetime.IsCancellationRequested) return;
        catalogPage = page; BindPage(null, false);
        await ChooseAsync(null, ReviewMode.Known);
    }

    private CancellationTokenSource NewRead()
    {
        readStop?.Cancel();
        return readStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }

    // Read/List/IsSealed are synchronous archive APIs. Serialize potentially large reads off the dispatcher.
    // Superseded reads cannot repaint a different match, and a fast series of selections cannot fan out I/O.
    private async Task<T> ReadAsync<T>(Func<T> action, CancellationToken token)
    {
        await reader.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(action, token).ConfigureAwait(false); }
        finally { reader.Release(); }
    }

    private async Task RefreshAsync(bool initial = false)
    {
        if (lifetime.IsCancellationRequested) return;
        using var request = NewRead(); var ct = request.Token;
        string? preferred = selected?.MatchId;
        double? resume = preferred != null ? Scrubber.Value : null;
        ReviewMode previous = mode;
        catalogBusy = true; selected = null; verified = hasReplay = false; timeline = null;
        SetMode(ReviewMode.Known); Draw(); Controls(); MatchStatus.Text = "正在读取本机档案目录…";
        try {
            var (entries, last, labels) = await ReadAsync(() => (store.List(), initial ? ReadBookmark() : null, MatchReviewText.ReadItemLabels()), ct);
            if (ct.IsCancellationRequested) return;
            formatter.UseItemLabels(labels);
            catalog = entries; BindPage(preferred ?? last, true);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) {
            if (!ct.IsCancellationRequested) {
                catalog = []; BindPage(null, false);
                MatchStatus.Text = "无法读取本机档案。"; Notice.Text = Error(e);
            }
            return;
        } finally {
            if (ReferenceEquals(readStop, request)) { readStop = null; catalogBusy = false; indexBusy = false; Controls(); }
        }
        bool same = preferred != null && (Matches.SelectedItem as Choice)?.Entry.MatchId == preferred;
        await ChooseAsync(same ? resume : null, same ? previous : ReviewMode.Known);
    }

    private async Task ChooseAsync(double? resume, ReviewMode wanted)
    {
        if (lifetime.IsCancellationRequested) return;
        using var request = NewRead(); var ct = request.Token;
        selected = (Matches.SelectedItem as Choice)?.Entry;
        var entry = selected;
        verified = hasReplay = false; indexBusy = true; timeline = null;
        SetMode(entry is { Damaged: false } && store.AuditReady ? wanted : ReviewMode.Known); Draw(); Controls();
        try {
            if (entry == null) {
                MatchStatus.Text = catalog.Count == 0 ? "尚无本机档案。收到本局的玩家数据后可在此查看；无需开启 Debug。" : "没有匹配该局号的档案。";
                Notice.Text = "可稍后刷新，或清除局号筛选。"; return;
            }
            if (entry.Damaged) { ShowUnavailable(entry.MatchId); return; } // never probe, read, or import this match
            MatchStatus.Text = $"正在读取 {entry.MatchId}…";
            var result = await ReadAsync(() => {
                bool ended = entry.Ended && store.IsSealed(entry.MatchId);
                bool replay = wanted != ReviewMode.Known && ended && entry.HasReplay;
                ct.ThrowIfCancellationRequested();
                var facts = store.Read(entry.MatchId, replay);
                var index = MatchReviewTimeline.Build(entry.MatchId, facts, replay, ct);
                if (ended && !store.IsSealed(entry.MatchId)) throw new InvalidOperationException("终局封存已变化；请刷新档案。");
                return (index, ended, replay);
            }, ct);
            if (ct.IsCancellationRequested) return;
            bool auditing = store.AuditReady;
            verified = result.ended && auditing; hasReplay = verified && entry.HasReplay;
            SetMode(result.replay && auditing ? wanted : ReviewMode.Known);
            timeline = !auditing && result.replay ? new(result.index.Known, new([])) : result.index;
            var (min, max) = timeline.Range(mode);
            changing = true;
            Scrubber.Minimum = min; Scrubber.Maximum = max;
            Scrubber.Value = Math.Clamp(resume ?? Math.Ceiling(timeline.Known.First ?? timeline.Replay.First ?? 0), min, max);
            changing = false;
            MatchStatus.Text = $"{entry.MatchId}　·　" + (verified ? "已验证结束 / 已封存　·　" + (hasReplay ? "录像已导入" : "尚无录像") :
                activeMatch() == entry.MatchId ? "正在记录 · 比赛尚未验证结束，录像入口锁定。" :
                "记录中断或未验证结束 · 断开连接、重启或已有文件都不算终局，录像入口锁定。");
            if (jobStop == null) Notice.Text = auditing ? "拖动游标，或用方向键逐秒查看。档案是读取时的快照；用「刷新档案」获取新记录。" : ReviewStore.LiveOnly;
            Draw();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) {
            if (!ct.IsCancellationRequested) {
                verified = hasReplay = false; timeline = null; SetMode(ReviewMode.Known); Draw();
                MatchStatus.Text = $"未能加载 {entry?.MatchId}，请刷新或选择另一场。"; Notice.Text = Error(e);
                if (entry != null && e is (IOException or InvalidDataException or UnauthorizedAccessException))
                    await ShowIfDamagedAsync(entry.MatchId, ct);
            }
        } finally {
            if (ReferenceEquals(readStop, request)) { readStop = null; indexBusy = false; Controls(); }
        }
    }

    private async Task StartJobAsync(bool local)
    {
        if (lifetime.IsCancellationRequested || !CheckAudit() || selected is not { Ended: true, Damaged: false } entry ||
            !verified || indexBusy || jobStop != null) return;
        string? file = null;
        if (local) {
            var dialog = new OpenFileDialog { Title = $"导入对局 {entry.MatchId} 的本地成品录像（不上传）",
                Filter = "Dota 2 录像 (*.dem;*.dem.bz2)|*.dem;*.dem.bz2", Multiselect = false, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            file = dialog.FileName;
            if (hasReplay && MessageBox.Show(this, "本局已有录像时间线。只在新录像完整通过核验后才替换它；继续吗？",
                "确认重新导入", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        } else if (MessageBox.Show(this, $"将向公开元数据服务查询局号 {entry.MatchId}，仅从 Valve 录像服务器下载。" +
            $"\n\n{SealedReplayJob.BudgetNotice}\n" + (hasReplay ? "本局已有录像；核验成功后会替换原录像时间线。\n" : "") +
            "不会登录 Steam、上传本地文件或调用付费模型。继续吗？",
            "确认下载及本机磁盘使用", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        // The native dialogs run a nested dispatcher. Do not act on a selection/gate which changed underneath them.
        if (lifetime.IsCancellationRequested || !CheckAudit() || selected != entry || !verified || indexBusy || jobStop != null) return;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        jobStop = cancel; Controls();
        job = RunJobAsync(entry.MatchId, file, cancel);
        await job;
    }

    private async Task RunJobAsync(string id, string? local, CancellationTokenSource cancel)
    {
        Notice.Text = "检查终局封存、本地工具和空间…"; JobProgress.IsIndeterminate = true;
        var progress = new Progress<ReviewProgress>(p => {
            if (lifetime.IsCancellationRequested || !ReferenceEquals(jobStop, cancel) || cancel.IsCancellationRequested) return;
            Notice.Text = p.Text; JobProgress.IsIndeterminate = p.Percent == null;
            if (p.Percent is double percent) JobProgress.Value = percent;
        });
        string result; bool installed = false, checkDamage = false;
        try {
            await Task.Run(() => SealedReplayJob.RunAsync(store, id, local, progress, cancel.Token));
            result = "录像已核验并导入。选择「录像全图」或「并排对照」；不会改写当时已知。"; installed = true;
        } catch (OperationCanceledException) { result = "已取消录像任务；没有导入部分结果。"; }
        catch (Exception e) { result = "录像任务失败：" + Error(e); checkDamage = e is IOException or InvalidDataException or UnauthorizedAccessException; }
        finally { jobStop = null; cancel.Dispose(); Controls(); }
        if (lifetime.IsCancellationRequested || !CheckAudit()) return;
        if (checkDamage && await ShowIfDamagedAsync(id, lifetime.Token)) return;
        if (installed) await RefreshAsync();
        if (!lifetime.IsCancellationRequested && selected?.MatchId == id) Notice.Text = result;
    }

    private static string Error(Exception e) => MatchReviewText.Clean(e.Message, 720);

    private string? ReadBookmark()
    {
        try {
            if (new FileInfo(bookmark) is { Exists: true, Length: <= 64 }) {
                var id = File.ReadAllText(bookmark).Trim(); return ValidId(id) ? id : null;
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit);
    private void WriteBookmark(string id)
    {
        string tmp = bookmark + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(bookmark))!);
            File.WriteAllText(tmp, id); File.Move(tmp, bookmark, true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* The archive is independent of this convenience. */ }
        finally { try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        await StopAsync();
    }

    internal Task StopAsync() => shutdown ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        IsEnabled = false;
        string? last = selected?.MatchId;
        await lifetime.CancelAsync(); // kills an ongoing child before deleting its temporary directory
        await job;
        await reader.WaitAsync(); reader.Release(); // synchronous archive calls cannot race owner disposal
        if (ValidId(last)) await Task.Run(() => WriteBookmark(last!));
        await System.Windows.Threading.Dispatcher.Yield(); // never call Close recursively in the current Closing notification
        allowClose = true; Close();
        lifetime.Dispose();
    }
}
