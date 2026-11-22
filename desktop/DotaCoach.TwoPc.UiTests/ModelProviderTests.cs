using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotaCoach.TwoPc;

internal static partial class Program
{
    // STA, real coach controls and timer; all model HTTP terminates inside ProviderHandler.
    // No real credentials, proxy, Dota, replay, game client, or external network needed.
    private static async Task ModelProviderChecks()
    {
        var temp = Directory.CreateTempSubdirectory("coach-model-ui-");
        try {
            string path = Path.Combine(temp.FullName, "coach.json"), foundry = Path.Combine(temp.FullName, "old.env"),
                keyFile = Path.Combine(temp.FullName, "model.env"), debug = Path.Combine(temp.FullName, "debug");
            const string legacy = "my-original-deployment", astra = "gpt-6.2-astra", sol = "gpt-6.2-sol";
            File.WriteAllText(foundry, $"AOAI_ENDPOINT=https://ui-fixture.openai.azure.com\nFOUNDRY_KEY={ProviderFixture.FoundryKey}");
            File.WriteAllText(keyFile, $"CODEY_MODEL_API_KEY={ProviderFixture.LocalKey}");
            void Check(bool ok, string description) {
                if (!ok) throw new Exception("Model settings: " + description);
                passed++; Console.WriteLine("PASS " + description);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new { foundryEnvFile = foundry }));
            var old = LiveCoachSettings.Load(path);
            Check(old.ModelProvider == "foundry" && old.EffectiveModel == "gpt-6-astra" && old.FoundryEnvFile == foundry &&
                old.CopilotBaseUrl == "http://127.0.0.1:4141/v1" && old.CopilotModel == astra && old.CopilotKeyFile == "",
                "provider-less profiles keep the old Foundry default and have independent Codey defaults");
            File.WriteAllText(path, JsonSerializer.Serialize(new { foundryEnvFile = foundry, model = legacy,
                automaticAdvice = false, enemyPanel = false, enemyVisionAlerts = false }));
            var fixture = new ProviderFixture();
            var type = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow", true)!;
            Window Open() {
                var w = (Window)Activator.CreateInstance(type, [null, false, path, debug, (Func<HttpMessageHandler>)(() => new ProviderHandler(fixture))])!;
                Set(w, "scanFile", Path.Combine(temp.FullName, "no-scan.json"));
                return w;
            }
            static async Task Close(Window w) {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.Closed += (_, _) => closed.TrySetResult(); w.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            var w = Open(); string? events = null;
            try {
                var provider = Field<ComboBox>(w, "modelProvider"); var models = Field<ComboBox>(w, "copilotModel");
                var endpoint = Field<TextBox>(w, "copilotBaseUrl"); var key = Field<TextBox>(w, "copilotKeyFile");
                var deploy = Field<TextBox>(w, "reasoningModel"); var auto = Field<CheckBox>(w, "automatic");
                var catalog = Field<TextBlock>(w, "catalogStatus"); var hint = Field<TextBlock>(w, "autoStatus");
                var result = Field<TextBlock>(w, "visionText"); var timer = Field<DispatcherTimer>(w, "timer");
                bool Active() => Field<bool>(w, "modelConfigured");
                int Attempts() => Field<int>(w, "modelCalls");
                void Save() => Click(w, "保存模型设置");
                void Probe() => Click(w, "刷新可用模型（不推理）");
                Check(Equals(provider.SelectedValue, "foundry") && deploy.Text == legacy &&
                    ((FrameworkElement)models.Parent).Visibility == Visibility.Collapsed &&
                    !models.IsEditable && models.Items.Cast<string>().SequenceEqual([astra, sol]) && fixture.Calls.IsEmpty,
                    "legacy custom deployment is restored; only Codey has the two exact, non-editable IDs");
                Save();
                Check(Active() && fixture.Calls.IsEmpty, "loading a Foundry configuration sends no validation inference");
                var host = await CoachHost.Start(IPAddress.Loopback, 0); Set(w, "host", host);
                string session = host.Mailbox.Begin().SessionId; long seq = 0;
                var blank = BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgr24, null, new byte[64 * 36 * 3], 64 * 3);
                var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(blank));
                using var buffer = new MemoryStream(); jpeg.Save(buffer); byte[] pixels = buffer.ToArray();
                var own = new OwnState("9130000610", "npc_dota_hero_luna", "radiant", 720, true, 1200, 500, -2000, -3000, [], []);
                void Frame() {
                    var t = host.Mailbox.Issue(session);
                    host.Mailbox.Accept(new(session, t.Id, ++seq, "dota2", 1920, 1080, own, 5,
                        new MatchWitness(own.MatchId, own.Team, own.Hero, own.Clock, "playing", false)), pixels);
                }
                void Due() { Set(w, "nextDecision", 0L); Frame(); }
                var oldPost = fixture.NextPost = new();
                auto.IsChecked = true; Due();
                var oldToken = await oldPost.Seen.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var layout = MinimapLayout.Suggest(1920, 1080);
                host.Mailbox.SetReminder(session, seq, "visible-farming-advice", "fixture", new(.05, .90), layout);
                Check(host.Mailbox.Poll(session)?.Marker != null && Attempts() == 1, "old provider has both a visible AI marker and a pending request");
                provider.SelectedValue = "codey-copilot";
                Check(oldToken.IsCancellationRequested && host.Mailbox.Poll(session) == null && !Active() &&
                    result.Text == "" && ((FrameworkElement)deploy.Parent).Visibility == Visibility.Collapsed &&
                    ((FrameworkElement)models.Parent).Visibility == Visibility.Visible &&
                    LiveCoachSettings.Load(path).ModelProvider == "foundry", "switch cancels and withdraws synchronously without committing the draft");
                oldPost.Release.TrySetResult();
                await Wait(() => !Field<bool>(w, "deciding"), "held Foundry reply settled after switching");
                Check(result.Text == "" && host.Mailbox.Poll(session) == null, "late completion cannot overwrite the new provider UI or republish the old marker");
                auto.IsChecked = false;
                File.Delete(foundry); // Every subsequent successful Codey operation proves Foundry isn't required.
                Frame();
                host.Mailbox.SetReminder(session, seq, "manual-link-test", "fixture", null);
                var manual = host.Mailbox.Poll(session)!.Id;
                key.Text = keyFile; models.SelectedItem = sol;
                Check(host.Mailbox.Poll(session)?.Id == manual, "editing a model or its credential path preserves a manual link diagnostic");
                host.Mailbox.SetReminder(session, seq, "local-vision-observation", "fixture", null);
                var plus = host.Mailbox.Poll(session)!.Id;
                endpoint.Text = "http://192.0.2.1:49161/v1"; Probe(); Save();
                Check(fixture.Lists == 0 && !Active() && host.Mailbox.Poll(session)?.Id == plus &&
                    LiveCoachSettings.Load(path).ModelProvider == "foundry", "invalid address never connects, persists the draft, or withdraws an independent Plus subtitle");
                endpoint.Text = "http://127.0.0.1:49161/v1"; key.Text = foundry; Probe();
                Check(fixture.Lists == 0 && !Active(), "a Foundry configuration cannot be selected as the Codey credential file");
                key.Text = keyFile;
                var minute = Field<CheckBox>(w, "minuteFarm742");
                minute.IsChecked = true; minute.IsChecked = false;
                Check(!Active() && LiveCoachSettings.Load(path).ModelProvider == "foundry" && fixture.Posts == 1,
                    "changing another saved switch never applies or saves the pending provider");
                fixture.Ids = [astra, sol + "-preview", sol.ToUpperInvariant(), ProviderFixture.Untrusted];
                Probe(); await Wait(() => catalog.Text.Contains(sol + "：未列出"), "GET catalog filters exact advertised model IDs");
                Check(catalog.Text.Contains(astra + "：代理已列出") && !catalog.Text.Contains(ProviderFixture.Untrusted) &&
                    Equals(models.SelectedItem, sol) && hint.Text.Contains("Responses") && !Active() && Attempts() == 1 &&
                    LiveCoachSettings.Load(path).ModelProvider == "foundry", "missing selection is explained, not replaced, inferred with, or saved by refresh");
                Save(); var saved = LiveCoachSettings.Load(path);
                Check(!Active() && saved.ModelProvider == "codey-copilot" && saved.EffectiveModel == sol && saved.Model == legacy &&
                    saved.FoundryEnvFile == foundry && saved.CopilotKeyFile == keyFile && !File.ReadAllText(path).Contains("effectiveModel"),
                    "saving an unavailable Codey choice preserves the legacy fields, but enables no fallback");
                minute.IsChecked = true; auto.IsChecked = true; await MinuteTicks(timer);
                Check(hint.Text.StartsWith("7.42 分钟路线") && hint.Text.Contains(sol) && hint.Text.Contains("未列出") &&
                    !hint.Text.Contains("Foundry") && Attempts() == 1, "minute loop explains the missing Codey selection without offering Foundry or charging");
                minute.IsChecked = false; auto.IsChecked = false;
                DebugSwitch(w).IsChecked = true; events = Path.Combine(Recorder(w)!.DirectoryPath, "events.jsonl");
                foreach (var (status, action) in new[] { (401, "CODEY_MODEL_API_KEY"), (403, "权限"), (429, "配额") }) {
                    fixture.ListStatus = (HttpStatusCode)status; Probe();
                    await Wait(() => catalog.Text.Contains($"HTTP {status}") && catalog.Text.Contains(action),
                        $"catalog HTTP {status} has actionable local guidance");
                    Check(!catalog.Text.Contains(ProviderFixture.Untrusted) && !Active() && Attempts() == 1 && fixture.Posts == 1,
                        $"catalog HTTP {status} exposes no response body and spends no inference attempt");
                }
                fixture.ListStatus = HttpStatusCode.OK; fixture.Ids = [astra, sol];
                var lateList = fixture.NextList = new(); Probe();
                var listToken = await lateList.Seen.Task.WaitAsync(TimeSpan.FromSeconds(8));
                endpoint.Text = "http://127.0.0.1:49162/v1";
                Check(listToken.IsCancellationRequested && catalog.Text.Contains("尚未核实"), "editing the proxy invalidates and cancels an outstanding catalog read");
                fixture.Ids = [astra]; Probe();
                await Wait(() => catalog.Text.Contains(sol + "：未列出"), "new proxy catalog is displayed");
                lateList.Release.TrySetResult(); await MinuteTicks(timer);
                Check(catalog.Text.Contains(sol + "：未列出") && !Active(), "a late old catalog cannot authorize a different endpoint");
                fixture.Ids = [astra, sol]; auto.IsChecked = true; Due(); Probe();
                await Wait(() => catalog.Text.Contains(sol + "：代理已列出"), "refreshed proxy advertises the chosen Sol");
                await MinuteTicks(timer);
                Check(!Active() && Attempts() == 1 && fixture.Posts == 1 && LiveCoachSettings.Load(path).CopilotBaseUrl.EndsWith(":49161/v1"),
                    "even with a fresh eligible frame, discovery only lists, and never saves, enables, or performs inference");
                Frame(); Save();
                await Wait(() => host.Mailbox.Poll(session) is { Kind: "visible-farming-advice", Marker: not null } &&
                    result.Text.Contains(sol), "explicit commit uses the exact selected model on the synthetic image and publishes its marker");
                Check(fixture.Calls.Last(c => c.Method == "POST") is { Model: sol, Port: 49162 } && Attempts() == 2 &&
                    Field<LiveCoachSettings>(w, "preferences").Model == legacy, "applied Codey settings use their own address/model with the shared budget");
                var staleRate = fixture.NextPost = new(); fixture.PostStatus = (HttpStatusCode)429; Due();
                var staleToken = await staleRate.Seen.Task.WaitAsync(TimeSpan.FromSeconds(8));
                models.SelectedItem = astra;
                Check(staleToken.IsCancellationRequested && !Active() && host.Mailbox.Poll(session) == null && LiveCoachSettings.Load(path).CopilotModel == sol,
                    "switching only the model immediately withdraws the published AI marker but keeps the saved selection");
                fixture.PostStatus = HttpStatusCode.OK; Save(); Frame();
                host.Mailbox.SetReminder(session, seq, "visible-farming-advice", "new-generation fixture", new(.05, .90), layout);
                var newer = host.Mailbox.Poll(session)!.Id;
                staleRate.Release.TrySetResult(); await Wait(() => !Field<bool>(w, "deciding"), "old Codey error settles after committing the other ID");
                Check(Active() && !result.Text.Contains("429") && !catalog.Text.Contains("429") && host.Mailbox.Poll(session)?.Id == newer,
                    "a late old-provider rejection cannot pause, erase the UI, or withdraw a newer generation's route");
                int beforeVision = fixture.Posts;
                fixture.Vision = false; Due(); Save();
                await Wait(() => result.Text.Contains("视觉") && !Field<bool>(w, "deciding"), "vision capability rejection explains the image requirement in the coach UI");
                Check(fixture.Posts == beforeVision && result.Text.Contains(astra), "rejected vision does not post an image or fall back to another model");
                fixture.Vision = true; Probe();
                await Wait(() => catalog.Text.Contains(astra + "：代理已列出"), "new manual probe reports Astra before a runtime catalog change");
                fixture.Ids = [sol]; Due(); Save();
                await Wait(() => !Active() && !Field<bool>(w, "deciding") && result.Text.Contains("清单"),
                    "runtime catalog removal supersedes earlier availability and pauses the UI");
                Check(fixture.Posts == beforeVision && result.Text.Contains(astra) && !catalog.Text.Contains(astra + "：代理已列出"),
                    "vanished model never posts or silently uses the remaining model");
                fixture.Ids = [astra, sol]; fixture.PostStatus = (HttpStatusCode)429;
                Probe(); await Wait(() => catalog.Text.Contains(astra + "：代理已列出"), "catalog recovers independently of inference");
                Due(); Save();
                await Wait(() => result.Text.Contains("429") && result.Text.Contains("配额"), "per-call rate limiting is visible, actionable and pauses the provider");
                Check(!Active() && !result.Text.Contains(ProviderFixture.Untrusted) && host.Mailbox.Poll(session) == null &&
                    fixture.Calls.All(c => c.Expected) && fixture.Posts == beforeVision + 1, "all requests had allowlisted paths and isolated credentials; failures do not publish");
                auto.IsChecked = false;
            } finally { fixture.Teardown.TrySetResult(); await Close(w); }

            var logs = File.ReadAllLines(events!);
            bool ModelEvent(string stage, string id) => logs.Any(line => {
                using var json = JsonDocument.Parse(line); var row = json.RootElement;
                return row.GetProperty("stage").GetString() == stage && row.GetProperty("details").GetProperty("model").GetString() == id;
            });
            Check(ModelEvent("model-start", sol) && ModelEvent("model-result", sol) && ModelEvent("client-state", sol),
                "debug state, request and result use the effective Copilot ID rather than the old deployment");
            Check(new[] { ProviderFixture.LocalKey, ProviderFixture.FoundryKey, ProviderFixture.Untrusted }.All(s =>
                !File.ReadAllText(path).Contains(s) && !logs.Any(line => line.Contains(s))), "settings and debug traces contain no key contents or raw server error");
            int calls = fixture.Calls.Count;
            var reopened = Open();
            try {
                Check(Equals(Field<ComboBox>(reopened, "modelProvider").SelectedValue, "codey-copilot") &&
                    Equals(Field<ComboBox>(reopened, "copilotModel").SelectedItem, astra) &&
                    Field<TextBlock>(reopened, "catalogStatus").Text.Contains("尚未核实") && !Field<bool>(reopened, "modelConfigured") &&
                    fixture.Calls.Count == calls, "restart restores the Codey profile, never a stale catalog or implicit login");
                Field<ComboBox>(reopened, "modelProvider").SelectedValue = "foundry";
                Click(reopened, "保存模型设置"); var back = LiveCoachSettings.Load(path);
                Check(back.ModelProvider == "foundry" && back.EffectiveModel == legacy && back.FoundryEnvFile == foundry &&
                    back.CopilotBaseUrl == "http://127.0.0.1:49162/v1" && back.CopilotModel == astra && back.CopilotKeyFile == keyFile &&
                    !Field<bool>(reopened, "modelConfigured") && fixture.Calls.Count == calls,
                    "switching back restores the original Foundry deployment; missing Foundry credentials never trigger Codey fallback");
            } finally { await Close(reopened); }
            const string invalidProfile = """{"modelProvider":"codey-copilot","copilotBaseUrl":"http://192.0.2.1/v1"}""";
            File.WriteAllText(path, invalidProfile);
            var invalid = Open();
            try {
                Field<CheckBox>(invalid, "minuteFarm742").IsChecked = true;
                Check(Field<ComboBox>(invalid, "modelProvider").SelectedIndex == -1 && Field<CheckBox>(invalid, "automatic").IsChecked == false &&
                    Field<CloudCoach?>(invalid, "cloud") == null && File.ReadAllText(path) == invalidProfile && fixture.Calls.Count == calls,
                    "invalid saved Codey configuration stays unselected and unmodified; no implicit Foundry activation on another switch");
            } finally { await Close(invalid); }
        } finally { temp.Delete(recursive: true); }
    }

    private sealed class ProviderHold
    {
        public readonly TaskCompletionSource<CancellationToken> Seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record ProviderCall(string Method, int Port, string? Model, bool Expected);
    private sealed class ProviderFixture
    {
        public const string FoundryKey = "fixture-only-foundry-061", LocalKey = "fixture-only-codey-061", Untrusted = "fixture-untrusted-server-body";
        public readonly ConcurrentQueue<ProviderCall> Calls = new();
        public readonly TaskCompletionSource Teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderHold? NextList, NextPost;
        public string[] Ids = []; public bool Vision = true;
        public HttpStatusCode ListStatus = HttpStatusCode.OK, PostStatus = HttpStatusCode.OK;
        public int Lists => Calls.Count(c => c.Method == "GET");
        public int Posts => Calls.Count(c => c.Method == "POST");
    }
    private sealed class ProviderHandler(ProviderFixture f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken token)
        {
            var uri = req.RequestUri!; bool codey = uri.Host == "127.0.0.1", list = req.Method == HttpMethod.Get;
            bool auth = codey ? req.Headers.Authorization is { Scheme: "Bearer", Parameter: ProviderFixture.LocalKey } && !req.Headers.Contains("api-key") :
                uri.Host == "ui-fixture.openai.azure.com" && req.Headers.Authorization == null &&
                req.Headers.TryGetValues("api-key", out var keys) && keys.SequenceEqual([ProviderFixture.FoundryKey]);
            string? model = null; string body;
            if (list) body = JsonSerializer.Serialize(new { data = f.Ids.Select(id => new { id, display_name = id + " (github-copilot)",
                supported_endpoints = new[] { "/responses" }, capabilities = new { supports = new { vision = f.Vision } } }) });
            else {
                using var input = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token));
                model = input.RootElement.GetProperty("model").GetString();
                var advice = new ModelAdvice("npc_dota_hero_luna", "radiant", "合成画面", ["测试"], [], "仅用于界面验证", ["确认野点"], "局势变化", null,
                    "测试地标", ["neutralcamp_good_11"]);
                body = JsonSerializer.Serialize(new { id = "fixture-response", model, status = "completed", usage = new { input_tokens = 20, output_tokens = 10 },
                    output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(advice, Wire.Json) } } } } });
            }
            bool shape = list ? codey && uri.AbsolutePath == "/v1/models" && req.Content == null :
                req.Method == HttpMethod.Post && uri.AbsolutePath == (codey ? "/v1/responses" : "/openai/v1/responses");
            f.Calls.Enqueue(new(req.Method.Method, uri.Port, model, auth && shape && uri.Query == ""));
            var status = list ? f.ListStatus : f.PostStatus;
            var hold = list ? Interlocked.Exchange(ref f.NextList, null) : Interlocked.Exchange(ref f.NextPost, null);
            if (hold != null) {
                hold.Seen.TrySetResult(token);
                await Task.WhenAny(hold.Release.Task, f.Teardown.Task); // Intentionally ignore cancellation.
            }
            return new(status) { Content = new StringContent(status == HttpStatusCode.OK ? body : ProviderFixture.Untrusted) };
        }
    }
}
