using DotaCoach.TwoPc;

/// <summary>Enemies that left the friend team's vision, for the game minimap (the user's request of 2026-11-24).</summary>
internal static class FogTests
{
    private const string Match = "9000000001", Razor = "npc_dota_hero_razor";
    public static void Run(Action<bool, string> check)
    {
        // Own team radiant: Kills.Radiant is the friend team's count.
        SpectatorSnapshot Packet(long ms, (int, int)? kills, params MinimapUnit[] enemies) =>
            new(ms, "team-view", Math.Floor(600 + ms / 1000.0), "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, Match, 2, 0,
                [.. enemies, new MinimapUnit("npc_dota_hero_luna", 2, -6000, -6000, 0, 1800, "minimap_herocircle_self")], ["radiant"], []) { Kills = kills };
        MinimapUnit Seen(double x, double? yaw = 0) => new(Razor, 3, x, 0, yaw, 1800, "minimap_enemyicon");

        var fog = new FogTracker();
        for (long t = 0; t <= 2000; t += 250) fog.Sight(Packet(t, (0, 0), Seen(t)));
        check(fog.Heroes().Length == 0, "an enemy in sight is not in the fog");
        fog.Sight(Packet(2500, (0, 0)));
        check(fog.Heroes().Length == 0, "gone for under a second is a flicker at the edge of vision");
        fog.Sight(Packet(5000, (0, 0)));
        check(fog.Heroes() is [{ Hero: Razor, X: 2000, Y: 0, Yaw: 0, Seconds: 3 }] &&
              FogTracker.Circle(fog.Heroes()[0]) is var (cx, cy, r) && cx == 2500 && cy == 0 && r == 850,
            "an enemy out of sight is kept at its last sighting with its facing; the circle sits ahead of it");
        fog.Sight(Packet(5250, (0, 0), Seen(2100)));
        check(fog.Heroes().Length == 0, "an enemy seen again leaves the fog");

        fog.Sight(Packet(6000, (0, 0), Seen(2000)));
        fog.Sight(Packet(6250, (1, 0)));
        fog.Sight(Packet(8500, (1, 0)));
        check(fog.Heroes().Length == 0, "an enemy that vanished as the friend team's kill count rose died: no fog mark");
        fog.Sight(Packet(9000, (1, 0), Seen(2000)));
        fog.Sight(Packet(9250, (1, 1)));
        fog.Sight(Packet(11000, (1, 1)));
        check(fog.Heroes() is [{ Seconds: 2 }], "a kill by the enemy team does not take its hero off the map");
        fog.Sight(Packet(11750, (2, 1), Seen(2000)));
        fog.Sight(Packet(12000, (2, 1)));
        fog.Sight(Packet(14000, (2, 1)));
        check(fog.Heroes().Length == 0, "a kill counted just before the hero vanishes still claims it");

        // The feed keeps coming (a gap of 3 s forgets everything): one empty packet a second.
        void Quiet(long from, long to) { for (long t = from; t <= to; t += 1000) fog.Sight(Packet(t, (2, 1))); }
        fog.Sight(Packet(15000, (2, 1), Seen(2000, null)));
        Quiet(16000, 18000);
        check(fog.Heroes() is [{ Yaw: null }] && FogTracker.Circle(fog.Heroes()[0]) is var (ux, _, ur) && ux == 2000 && ur > 850,
            "without a facing the circle is centred on the sighting and larger");
        Quiet(19000, 25000);
        check(fog.Heroes() is [{ Seconds: 10 }] && FogTracker.Circle(fog.Heroes()[0]) != null &&
              FogTracker.Circle(fog.Heroes()[0] with { Seconds = 10.5 }) == null, "the circle is drawn for the first 10 s only");
        Quiet(26000, 35000);
        fog.Sight(Packet(35500, (2, 1)));
        check(fog.Heroes().Length == 0, "after 20 s the mark is dropped");

        fog.Sight(Packet(36000, (2, 1), Seen(2000)));
        fog.Sight(Packet(37500, (2, 1)));
        fog.Sight(Packet(41000, (2, 1)));
        check(fog.Heroes().Length == 0, "a gap in the Plus feed forgets every mark");
        fog.Sight(Packet(42000, (2, 1), Seen(2000)));
        fog.Sight(Packet(44000, (2, 1)) with { Mode = "full-spectator", GroupedPlayers = 10 });
        fog.Sight(Packet(44500, (2, 1)));
        check(fog.Heroes().Length == 0, "full spectating never feeds the team-view fog");

        // Ageing on the coach's mailbox: the mark grows older and is dropped at the limit.
        var board = new EnemyBoard([], null, Match, "radiant") { Fog = [new(Razor, 2000, 0, 0, 19.5)] };
        check(BoardDelivery.Age(board, 400, 4600).Fog is [{ Seconds: 19.9 }] && BoardDelivery.Age(board, 600, 4400).Fog is [],
            "a held board ages its fog marks and drops them after 20 s");
        check(BoardDelivery.WithoutStatsOrFog(board) == null &&
              BoardDelivery.WithoutStatsOrFog(board with { Heroes = [new(Razor, 9, null, null, 1) { Stats = new(1, 0, 1, 0, 0, 300, 1, 1, 1, 1) }] })
                  is { Fog: null, Heroes: [{ Stats: null, Level: 9 }] }, "an older game client gets neither stats nor fog marks");
        var parsed = SpectatorGsi.Parse(System.Text.Json.JsonDocument.Parse("""
            {"map":{"matchid":"9000000001","clock_time":600,"game_state":"DOTA_GAMERULES_STATE_GAME_IN_PROGRESS","radiant_score":7,"dire_score":3},
             "player":{"team_name":"spectator"},"minimap":{}}
            """).RootElement, 0);
        check(parsed.Kills == (7, 3), "the Plus packet's kill counts are read from map.radiant_score and dire_score");
    }
}
