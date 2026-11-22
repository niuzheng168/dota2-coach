#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:project ../desktop/DotaCoach.CoachClient/DotaCoach.CoachClient.csproj

// dotnet run --file tests/minute-farm-setup.test.cs
// Exercise the shipped WPF handlers without starting a dispatcher, capturing pixels, or calling a model.
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static class MinuteSetupTest
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Coach = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow", true)!;
    private static T Get<T>(Window w, string field) => (T)Coach.GetField(field, Private)!.GetValue(w)!;
    private static void Set(Window w, string field, object value) => Coach.GetField(field, Private)!.SetValue(w, value);
    private static object? Call(Window w, string method, params object[] args) => Coach.GetMethod(method, Private)!.Invoke(w, args);
    private static void Click(Window w, string button) => Get<Button>(w, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool Armed(Window w, FrameView f) => (bool)Call(w, "MinuteFarmArmedFor", f)!;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }

    [STAThread] public static void Main()
    {
        string settings = Path.GetTempFileName(), env = Path.GetTempFileName();
        var host = CoachHost.Start(IPAddress.Loopback, 0).GetAwaiter().GetResult();
        try {
            File.WriteAllText(env, "AOAI_ENDPOINT=https://fixture.openai.azure.com\nFOUNDRY_KEY=test-only");
            File.WriteAllText(settings, JsonSerializer.Serialize(new { foundryEnvFile = env, requestLimit = 1 }));
            Window Open() => (Window)Activator.CreateInstance(Coach, [null, false, settings, settings + "-unused-debug"])!;
            var w = Open();
            Set(w, "host", host); Set(w, "modelCalls", 1); // Exhaust the fixture budget, including across switches.
            var mode = Get<CheckBox>(w, "minuteFarm742");
            var confirm = Get<Button>(w, "confirmMinuteGame");
            var status = Get<TextBlock>(w, "minuteSetupStatus");
            Check(mode.Visibility == Visibility.Visible && mode.Content is string label && label.Contains("游戏时钟") &&
                label.Contains("每分钟最多调用一次模型") && confirm.Content is string consent && consent.Contains("标准 dota 地图") &&
                consent.Contains("7.42 / build 6989"), "the visible switch names the cadence and the separate confirmation names the actual map/build");
            var ordinary = LiveCoachSettings.Load(settings);
            var player = new OwnState("45678", "npc_dota_hero_luna", "radiant", 607, true, 800, 400, 0, 0, [], []) {
                Farm = new(true, true, false)
            };
            string session = host.Mailbox.Begin().SessionId;
            long sequence = 0;
            FrameView Send(OwnState? p = null, bool image = false, int width = 1920, int height = 1080, int age = 10) {
                var ticket = host.Mailbox.Issue(session);
                var meta = new FrameMeta(session, ticket.Id, ++sequence, image ? "dota2" : "dota2-gsi", width, height, p ?? player, age);
                if (image) // Header-only 1280x720 JPEG, never decoded in this test.
                    host.Mailbox.Accept(meta, [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 2, 208, 5, 0, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1, 0xff, 0xd9]);
                else host.Mailbox.AcceptMinuteState(meta, 200);
                return host.Mailbox.Latest()!;
            }
            Check(!ordinary.MinuteFarm742 && mode.IsChecked == false &&
                ordinary.EffectiveLayout(Send(image: true)) == MinimapLayout.Suggest(1920, 1080), "old preferences keep ordinary image mode");
            session = host.Mailbox.Begin().SessionId;
            mode.IsChecked = true;
            var minute = LiveCoachSettings.Load(settings);
            Check(minute.MinuteFarm742 && !confirm.IsEnabled && status.Text.Contains("等待") &&
                !Get<CheckBox>(w, "autoMinimap").IsEnabled && Get<CalibratedMap?>(w, "liveMap") == null,
                "mode selection persists, but cannot attest without a current participant");
            var provider = Get<CloudCoach>(w, "cloud");
            var a = Send();
            Call(w, "PreviewCalibration", true); Call(w, "UpdateMinuteSetup");
            Check(!confirm.IsEnabled && !Armed(w, a) && minute.EffectiveLayout(a) == null &&
                minute.EffectiveLayout(Send(image: true)) == null && status.Text.Contains("确认校准"),
                "neither an automatic box nor an unconfirmed preset can arm either transport");
            a = Send(player with { Farm = player.Farm! with { InstalledBuild = MinuteFarmContext.SupportedBuild } });
            Call(w, "ConfirmCalibration"); Call(w, "UpdateMinuteSetup");
            Check(confirm.IsEnabled && !Armed(w, a) &&
                LiveCoachSettings.Load(settings).EffectiveLayout(a) == MinimapLayout.Suggest(1920, 1080, right: true),
                "confirmed mirrored GSI-only source dimensions make an offer, not an attestation");

            player = player with { MatchId = "56789" };
            var b = Send();
            Click(w, "confirmMinuteGame");
            Check(!Armed(w, b), "a click on an obsolete offer never confirms the intervening match");
            Click(w, "confirmMinuteGame");
            Check(Armed(w, b) && !Armed(w, a), "only the exact confirmed participant is armed");
            var reopened = Open(); Set(reopened, "host", host); Call(reopened, "UpdateMinuteSetup");
            Check(Get<CheckBox>(reopened, "minuteFarm742").IsChecked == true && !Armed(reopened, b) &&
                !File.ReadAllText(settings).Contains(session), "restart restores the exclusive preference, not consent even on the very same session");
            var normalFrame = Send(player with { Clock = 672 }, image: true);
            Check(Armed(w, normalFrame), "the same match remains confirmed across game-clock minutes on the image transport");
            Get<CheckBox>(w, "subtitles").IsChecked = false;
            Check(Armed(w, normalFrame) && ReferenceEquals(provider, Get<CloudCoach>(w, "cloud")), "subtitles are optional and do not trigger provider reconfiguration");
            host.Mailbox.SetReminder(session, normalFrame.Meta.Sequence, "manual-link-test", "test", null);
            using var pending = new CancellationTokenSource();
            Set(w, "decisionStop", pending);
            Get<CheckBox>(w, "mapMarkers").IsChecked = false;
            Check(pending.IsCancellationRequested && !Armed(w, normalFrame) && host.Mailbox.Poll(session) == null &&
                LiveCoachSettings.Load(settings).MinuteFarm742, "disabling markers cancels work, clears the reminder and keeps minute mode selected");
            Get<CheckBox>(w, "mapMarkers").IsChecked = true;
            Check(!Armed(w, normalFrame) && ReferenceEquals(provider, Get<CloudCoach>(w, "cloud")), "reenabling a dependency cannot reattest or rebuild the provider");
            Send(); Call(w, "UpdateMinuteSetup"); Click(w, "confirmMinuteGame");
            var wrongBuild = Send(player with { Farm = player.Farm! with { InstalledBuild = 6990 } });
            Call(w, "UpdateMinuteSetup");
            Check(!Armed(w, wrongBuild) && !confirm.IsEnabled && status.Text.Contains("6990"), "a known contradictory build rejects and revokes attestation");
            b = Send(); Call(w, "UpdateMinuteSetup"); Check(!Armed(w, b), "loss of build telemetry does not undo that revocation");
            Click(w, "confirmMinuteGame");
            Check(!Armed(w, Send(age: 3000)) && Armed(w, Send()), "stale input pauses work without demanding a new click for every transient gap");
            session = host.Mailbox.Begin().SessionId;
            b = Send(); Check(!Armed(w, b) && !Armed(w, normalFrame), "both new and old evidence are rejected immediately after reconnection");
            Call(w, "UpdateMinuteSetup"); Click(w, "confirmMinuteGame");
            var changed = Send(width: 2560, height: 1440); Call(w, "UpdateMinuteSetup");
            Check(!Armed(w, changed) && !confirm.IsEnabled && status.Text.Contains("2560×1440"), "another resolution requires its own explicit calibration");
            Send(player with { Farm = null }); Call(w, "UpdateMinuteSetup");
            Check(!confirm.IsEnabled, "an unproven participant cannot be confirmed");
            Send(player with { MatchId = "0" }); Call(w, "UpdateMinuteSetup");
            Check(!confirm.IsEnabled && status.Text.Contains("对局号"), "anonymous games cannot borrow a per-match attestation");
            b = Send(); Call(w, "UpdateMinuteSetup"); Click(w, "confirmMinuteGame"); Click(w, "withdrawMinuteGame");
            Check(!Armed(w, b) && LiveCoachSettings.Load(settings).MinuteFarm742, "withdrawing pauses this match, not a fallback to ordinary mode");
            Get<CheckBox>(w, "automatic").IsChecked = false;
            Check(!confirm.IsEnabled && !Armed(w, b), "automatic master is also required");
            mode.IsChecked = false;
            Check(!LiveCoachSettings.Load(settings).MinuteFarm742 && Get<CalibratedMap?>(w, "liveMap") != null &&
                Get<int>(w, "modelCalls") == 1 && !ReferenceEquals(provider, Get<CloudCoach>(w, "cloud")),
                "only an explicit mode switch rebuilds the ordinary provider; it does not reset the process budget");
            File.WriteAllText(settings, """{"minuteFarm742":true,"intervalSeconds":-1}""");
            Check(Get<CheckBox>(Open(), "automatic").IsChecked == false,
                "invalid saved preferences leave automatic advice paused rather than authorizing ordinary-mode calls");
        } finally {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(settings); File.Delete(env);
        }
    }
}
