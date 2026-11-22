using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DotaCoach.TwoPc;

internal static partial class Program
{
    // Run on the suite's STA dispatcher. No game window, capture, synthetic JPEG, or direct call to Refresh/Decide.
    private static async Task MinuteLiveLoopTests()
    {
        var temp = Directory.CreateTempSubdirectory("dota-minute-loop-");
        try {
            string settings = Path.Combine(temp.FullName, "preferences.json"), fakeEnv = Path.Combine(temp.FullName, "fake.env");
            File.WriteAllText(fakeEnv, "AOAI_ENDPOINT=https://minute-fixture.openai.azure.com\nFOUNDRY_KEY=not-a-key\n");
            // Toggling modes rebuilds the production provider. Even an unexpected rebuild must fail closed:
            // only our in-process HTTP handler gets a usable configuration, never the window's provider factory.
            var prefs = new LiveCoachSettings { FoundryEnvFile = Path.Combine(temp.FullName, "absent.env"), Model = "ui-fixture",
                RequestLimit = 12, ModelTimeoutSeconds = 20, EnemyPanel = false, EnemyVisionAlerts = false };
            prefs.Save(settings);
            var type = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow", true)!;
            var w = (Window)Activator.CreateInstance(type, [null, false, settings, Path.Combine(temp.FullName, "debug")])!;
            using var teardown = new CancellationTokenSource();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            w.Closed += (_, _) => closed.TrySetResult();
            try {
                Set(w, "scanFile", Path.Combine(temp.FullName, "no-scan.json"));
                var host = await CoachHost.Start(IPAddress.Loopback, 0);
                Set(w, "host", host); // Owned and disposed by CoachWindow.Close; no fixed port or automatic startup.
                using var link = new GameLink(host.Pair(IPAddress.Loopback));
                await link.Connect(default);
                var right = MinimapLayout.Suggest(1920, 1080, right: true);
                var luna = new OwnState("9130000742", "npc_dota_hero_luna", "radiant", 0, true, 1200, 580, 3978, -5027,
                    [new("item_power_treads", null, 0)], [new("luna_moon_glaive", 3, 0)],
                    new(true, true, false, MoveSpeed: 370, AttackDamage: 108, AttackSpeed: 175, Level: 11,
                        InstalledBuild: 6989, Epoch: Guid.NewGuid()));
                long sequence = 0;
                async Task<long> State(int clock) {
                    var ticket = await link.GetTicket(default);
                    var meta = new FrameMeta(link.SessionId!, ticket.Id, ++sequence, "dota2-gsi", 1920, 1080, luna with { Clock = clock }, 0);
                    await link.UploadMinuteState(meta, Environment.TickCount64, default);
                    return meta.Sequence;
                }

                var timer = Field<DispatcherTimer>(w, "timer");
                var mode = Field<CheckBox>(w, "minuteFarm742");
                var confirm = Field<Button>(w, "confirmMinuteGame");
                var withdraw = Field<Button>(w, "withdrawMinuteGame");
                var calls = new ConcurrentQueue<MinuteLoopCall>();
                CloudCoach? fake = null;
                int attempts = 0;
                void BindFake() {
                    // Do not dispose/cancel the previous provider here: cancellation must come from the window.
                    if (Field<CloudCoach?>(w, "cloud") is { } current) {
                        if (current != fake) throw new Exception("Unexpected non-fixture provider");
                    } else {
                        fake = new CloudCoach(fakeEnv, prefs.Model, "",
                            new MinuteLoopHandler(calls, () => Interlocked.Increment(ref attempts), teardown.Token),
                            localBotOnly: false, timeout: TimeSpan.FromSeconds(prefs.ModelTimeoutSeconds));
                        Set(w, "cloud", fake);
                    }
                    Set(w, "modelConfigured", true);
                }
                void Mode(bool selected) { mode.IsChecked = selected; BindFake(); }
                bool Offered() => confirm.IsEnabled && confirm.Visibility == Visibility.Visible;
                void Confirm() {
                    if (!Offered()) throw new Exception("No enabled minute-game offer to click");
                    Click(w, (string)confirm.Content);
                }
                // Include the window's paid-attempt counter: a mistaken call to the legacy path could reject
                // the GSI-only frame before reaching SendAsync. Never confuse that with "not scheduled".
                bool Count(int n) => Field<int>(w, "modelCalls") == n && Volatile.Read(ref attempts) == n && calls.Count == n;
                Reminder? Published() => host.Mailbox.Poll(link.SessionId!);
                bool Quiet(int n) => Count(n) && !Field<bool>(w, "deciding") && Published() == null;
                async Task<MinuteLoopCall> Request(int clock) {
                    await Wait(() => calls.Any(c => c.Clock == clock), $"coach timer dispatched the {clock / 60}:00 model request");
                    return calls.Single(c => c.Clock == clock);
                }
                async Task<Reminder> ReleaseRoute(MinuteLoopCall call, long origin) {
                    call.Release.SetResult();
                    await Wait(() => Published()?.FarmMinute == call.Clock / 60, $"coach published the {call.Clock / 60}:00 reply");
                    var r = await link.Poll(default);
                    Check(r is { Kind: "minute-farm-route", ShowSubtitle: false, Marker: not null, Route.Length: > 0, NextSteps.Length: > 0 } &&
                        r.FarmMinute == call.Clock / 60 && r.SessionId == link.SessionId && r.FrameSequence == origin &&
                        r.Minimap == right && r.Route.All(right.Contains) && r.NextSteps[0].Contains(call.FirstStop),
                        "paired game received the model-selected stop on the confirmed right HUD, on its original GSI sequence");
                    return r!;
                }
                static void Check(bool ok, string name) {
                    if (!ok) throw new Exception("Minute live loop: " + name);
                    passed++; Console.WriteLine("PASS " + name);
                }

                BindFake();
                await State(480); await MinuteTicks(timer);
                Check(mode.IsChecked == false && Field<TextBlock>(w, "previewTitle").Text.StartsWith("本人 GSI · ") &&
                    Field<Image>(w, "image").Source == null && Quiet(0),
                    "ordinary configured coach receives metadata but never starts its legacy image model");
                Mode(true);
                Field<CheckBox>(w, "subtitles").IsChecked = false; // Real operator switch: routes without captions.
                Children(w).OfType<Expander>().Single(e => Equals(e.Header, "小地图校准")).IsExpanded = true;
                await State(540);
                Click(w, "预设：右下"); await MinuteTicks(timer);
                Check(!Offered() && Quiet(0), "minute mode and a right-hand draft do not authorize a request");
                Click(w, "确认校准"); await MinuteTicks(timer);
                Check(Offered() && LiveCoachSettings.Load(settings).Calibrations.Single() == right && Quiet(0),
                    "saved right layout offers this participant; neither model runs before the session click");

                await State(599); Confirm(); await MinuteTicks(timer);
                Check(withdraw.Visibility == Visibility.Visible && Quiet(0), "confirming with one game second left does not spend a request");
                long firstOrigin = await State(600);
                var first = await Request(600);
                await State(601); // New evidence while the reply is held must not become its origin.
                var route = await ReleaseRoute(first, firstOrigin);
                // Exercise more than the local 3s probe throttle, keeping both game time and the freshness lease moving.
                for (int clock = 602; clock <= 606; clock++) { await State(clock); await MinuteTicks(timer, 7); }
                Check(Count(1) && (await link.Poll(default))?.Id == route.Id,
                    "advancing same-minute GSI and ordinary timer ticks neither charge again nor replace the route");
                await State(660);
                var late = await Request(660);
                Check(!late.Token.IsCancellationRequested && Count(2) && Published() == null && await link.Poll(default) == null &&
                    !Field<TextBlock>(w, "visionText").Text.Contains("已向游戏小地图下发"),
                    "10:00 route is withdrawn at the next minute even while the 11:00 request is unanswered");
                await State(720); await MinuteTicks(timer);
                Check(late.Token.IsCancellationRequested && Field<bool>(w, "deciding") && Count(2) && Published() == null,
                    "crossing a game minute cancels the busy request; no concurrent call or fallback route");
                late.Release.SetResult(); // Deliberately return a valid old selection despite cancellation.
                var off = await Request(720);
                Check(!off.Token.IsCancellationRequested && Count(3) && Published() == null && await link.Poll(default) == null,
                    "late 11:00 reply is not retagged while the new 12:00 reply is held");
                mode.IsChecked = false;
                Check(off.Token.IsCancellationRequested, "operator switching mode off cancels the in-flight provider");
                BindFake();
                off.Release.SetResult();
                await Wait(() => !Field<bool>(w, "deciding"), "coach settled the late reply after mode-off");
                await State(721); await MinuteTicks(timer);
                Check(Quiet(3) && await link.Poll(default) == null, "a reply after mode-off publishes nothing and starts no legacy work");

                Mode(true); long oldOrigin = await State(780); await MinuteTicks(timer);
                Check(Offered() && Quiet(3), "re-enabling in an uncharged minute requires a new session click");
                Confirm(); await ReleaseRoute(await Request(780), oldOrigin);
                Mode(false);
                Check(Quiet(4) && await link.Poll(default) == null, "turning minute mode off withdraws a still-live route in the same minute");
                Mode(true); await State(781); await MinuteTicks(timer); Confirm(); await MinuteTicks(timer);
                Check(withdraw.Visibility == Visibility.Visible && Quiet(4),
                    "reconfirming the already-paid minute restores consent, not the old route or a new charge");

                string oldSession = link.SessionId!;
                await link.End(default); await link.Connect(default);
                long newOrigin = await State(840); await MinuteTicks(timer);
                Check(link.SessionId != oldSession && Offered() && Quiet(4) && await link.Poll(default) == null,
                    "reconnecting the same Luna, match and HUD in an uncharged minute does not inherit consent");
                Confirm(); await ReleaseRoute(await Request(840), newOrigin);
                Click(w, (string)withdraw.Content);
                Check(Quiet(5) && await link.Poll(default) == null, "withdrawal removes the authorized route before the clock changes");
                await State(900); await MinuteTicks(timer);
                Check(mode.IsChecked == true && Offered() && Quiet(5),
                    "explicit withdrawal keeps minute mode selected but prevents further routes and charges");
            } finally {
                teardown.Cancel(); w.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
        } finally { temp.Delete(recursive: true); }
    }

    // Observe, never drive, the window's ordinary timer. Negative checks must span real scheduling opportunities.
    private static async Task MinuteTicks(DispatcherTimer timer, int count = 3)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Tick(object? sender, EventArgs e) { if (--count == 0) done.TrySetResult(); }
        timer.Tick += Tick;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(8)); } finally { timer.Tick -= Tick; }
    }

    private sealed record MinuteLoopCall(int Clock, string FirstStop, CancellationToken Token)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MinuteLoopHandler(ConcurrentQueue<MinuteLoopCall> calls, Action attempt, CancellationToken teardown) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            attempt(); // Count unexpected legacy requests too; don't synthesize a successful legacy response.
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (body.RootElement.GetProperty("text").GetProperty("format").GetProperty("name").GetString() != "minute_farm_choice")
                return new(HttpStatusCode.BadRequest);
            var part = body.RootElement.GetProperty("input")[0].GetProperty("content").EnumerateArray().Single();
            using var data = JsonDocument.Parse(part.GetProperty("text").GetString()!);
            // Select a supplied whole itinerary with ample slack; never supply model-authored coordinates.
            var pick = data.RootElement.GetProperty("options").EnumerateArray()
                .MinBy(o => o.GetProperty("stops").EnumerateArray().Last().GetProperty("finishSeconds").GetInt32());
            var call = new MinuteLoopCall(data.RootElement.GetProperty("clock").GetInt32(), pick.GetProperty("stops")[0].GetProperty("label").GetString()!, token);
            calls.Enqueue(call);
            await call.Release.Task.WaitAsync(teardown); // Intentionally ignore provider cancellation; teardown still unblocks failures.
            string text = JsonSerializer.Serialize(new { optionId = pick.GetProperty("id").GetString() });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "completed",
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } })) };
        }
    }
}
