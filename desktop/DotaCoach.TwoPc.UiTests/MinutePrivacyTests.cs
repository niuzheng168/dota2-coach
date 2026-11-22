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
    private static async Task MinuteLocalCaptureChecks(Window game)
    {
        // Exercise the real lifecycle but never construct a GPU, window capture, or GSI receiver.
        object? Invoke(string name, params object[] args) =>
            game.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, args);
        var captured = new FakeLocalCapture();
        Set(game, "creepHp", captured);
        Set(game, "creepHpBusy", true); // A scanner startup was already awaiting the receiver.
        Task privacy = (Task)Invoke("EnterMinutePrivacy")!;
        var button = Field<Button>(game, "creepHpButton");
        await Wait(() => !privacy.IsCompleted && !button.IsEnabled && !captured.StopRequested,
            "GSI-only mode locks out new screen scans before waiting for an in-progress local scanner setup");
        Set(game, "creepHpBusy", false);
        await Wait(() => captured.StopRequested && !privacy.IsCompleted,
            "GSI-only connection waits for local capture disposal, not just a disabled button");
        captured.Stopped.SetResult(); await privacy;
        var sentinel = new FakeLocalCapture(); sentinel.Stopped.SetResult(); Set(game, "creepHp", sentinel);
        await (Task)Invoke("ToggleCreepHp")!;
        await Wait(() => !sentinel.StopRequested && !button.IsEnabled,
            "programmatic scanner toggle is also refused while the GSI-only connection owns privacy");
        Set(game, "creepHp", null!); Invoke("SetIdle", true);
        await Wait(() => button.IsEnabled && !Field<bool>(game, "gsiOnlyConnection"),
            "disconnect releases the GSI-only privacy lock without restarting capture");
    }
}
