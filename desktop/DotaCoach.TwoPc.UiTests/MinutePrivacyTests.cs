using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private sealed class FakeLocalCapture : IAsyncDisposable
    {
        public bool StopRequested;
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync() { StopRequested = true; return new(Stopped.Task); }
    }
    // Same post-consent preparation as Start; do not drive a MessageBox or install the user's GSI cfg in a test.
    private static Task PrepareSelectedMode(Window game, CancellationToken token = default) =>
        game.GetType().GetMethod("PrepareCaptureMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, Task>>(game)(token);
    private static void SetGameIdle(Window game, bool idle) =>
        game.GetType().GetMethod("SetIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<bool>>(game)(idle);

    private static async Task MinuteLocalCaptureChecks(Window game)
    {
        // Exercise the real lifecycle but never construct a GPU, window capture, or GSI receiver.
        object? Invoke(string name, params object[] args) =>
            game.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, args);
        var captured = new FakeLocalCapture();
        Set(game, "creepHp", captured);
        Set(game, "creepHpBusy", true); // A scanner startup was already awaiting the receiver.
        SetGameIdle(game, false);
        Task privacy = PrepareSelectedMode(game); // The normal default must dispatch to the strict, no-local-capture gate.
        var button = Field<Button>(game, "creepHpButton");
        await Wait(() => !privacy.IsCompleted && !button.IsEnabled && !captured.StopRequested && Field<bool>(game, "participantConnection"),
            "GSI-only mode locks out new screen scans before waiting for an in-progress local scanner setup");
        Set(game, "creepHpBusy", false);
        await Wait(() => captured.StopRequested && !privacy.IsCompleted,
            "GSI-only connection waits for local capture disposal, not just a disabled button");
        captured.Stopped.SetResult(); await privacy;
        var sentinel = new FakeLocalCapture(); sentinel.Stopped.SetResult(); Set(game, "creepHp", sentinel);
        await (Task)Invoke("ToggleCreepHp")!;
        await Wait(() => !sentinel.StopRequested && !button.IsEnabled,
            "programmatic scanner toggle is also refused while the GSI-only connection owns privacy");
        Set(game, "creepHp", null!); SetGameIdle(game, true);
        await Wait(() => button.IsEnabled && !Field<bool>(game, "gsiOnlyConnection"),
            "disconnect releases the GSI-only privacy lock without restarting capture");
        SelectSource(game, "本人 GSI＋可选本机小兵血量");
        SetGameIdle(game, false); await PrepareSelectedMode(game);
        await Wait(() => Field<bool>(game, "participantConnection") && !Field<bool>(game, "gsiOnlyConnection") && button.IsEnabled &&
            Field<IAsyncDisposable?>(game, "creepHp") == null && Field<Grid>(game, "preview").Visibility == Visibility.Collapsed,
            "selecting participant with local HP permits a manual toggle but does not opt in to a scanner or video");
        SetGameIdle(game, true); SelectSource(game, "仅本人 GSI");
    }
}
