namespace DotaCoach.TwoPc;

/// <summary>Projection from the game machine's participant receiver. Neither path reads minimap units, pixels
/// or spectator input. An unavailable own state does not erase independently witnessed match phase.</summary>
internal readonly record struct GameGsiSample(OwnState? Player, int? AgeMs, MatchWitness? Phase)
{
    // Terminals use the paired receipt lane exactly once. In particular a still-current terminal
    // must not travel on new-session pictures/heartbeats or be replayed as their live player state.
    public GameGsiSample ForUpload() => Phase?.Phase == "postgame" ? default : this;
    public static GameGsiSample ReadForUpload(GsiReceiver? receiver, bool participantOnly) => Read(receiver, participantOnly).ForUpload();

    public static GameGsiSample Read(GsiReceiver? receiver, bool participantOnly)
    {
        if (receiver == null) return default;
        // A single lock/source instant. A terminal witness stands on its own, without claiming the hero is alive.
        var own = receiver.Latest(out var phase);
        if (phase == null || !MatchWitness.Valid(phase))
            return participantOnly ? default : new(own.Player, own.AgeMs, null); // Legacy video/bot compatibility only.
        if (own.Player == null || own.AgeMs is null or < 0 or >= MatchWitness.MaxAgeMs || !BoardDelivery.SamePlayer(phase, own.Player))
            return new(null, null, phase);
        var player = own.Player;
        if ((phase.Phase != "playing" || phase.Paused) && player.Farm is { InProgress: true, Paused: false })
            player = player with { Farm = null };
        return new(player, System.Math.Max(own.AgeMs.Value, phase.AgeMs), phase);
    }
}
