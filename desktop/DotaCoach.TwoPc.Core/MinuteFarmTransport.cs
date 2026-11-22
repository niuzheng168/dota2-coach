using System.Net.Http.Json;

namespace DotaCoach.TwoPc;

public sealed partial class CoachMailbox
{
    /// <summary>Dedicated metadata-only ingress. This source is NOT an image, never passes the legacy
    /// vision coach's eligibility check, and cannot be injected through the JPEG/video endpoints.</summary>
    public void AcceptMinuteState(FrameMeta meta, int bytes)
    {
        BoardDelivery.ValidatePhase(meta);
        bool noViewport = meta.SourceWidth == 0 && meta.SourceHeight == 0 && meta.Player?.Farm == null &&
            (meta.Player == null || meta.MatchPhase != null);
        if (meta.Source != "dota2-gsi" || !noViewport && (meta.SourceWidth is < 640 or > 7680 || meta.SourceHeight is < 480 or > 4320) ||
            meta.Sequence < 1 || bytes is <= 0 or > 24576 ||
            (meta.Player != null ? meta.GsiAgeMs is null or < 0 or > 3000 : meta.GsiAgeMs != null))
            throw new ArgumentException("Invalid GSI-only envelope");
        if (meta.Player != null) GsiState.Validate(meta.Player);
        lock (gate) {
            Check(meta.SessionId);
            if (videoOwner != null || ticket is not { } t || meta.Ticket != t.Id ||
                now() - t.At is < 0 or > Wire.TicketLifetimeMs || meta.Sequence <= sequence)
                throw new InvalidOperationException("Stale GSI-only ticket or sequence");
            ticket = null;
            Store(new(meta, [], t.At, now()), bytes);
        }
    }
}

public sealed partial class GameLink
{
    public async Task UploadMinuteState(FrameMeta meta, long sampledAt, CancellationToken cancellation)
    {
        if (meta.Source != "dota2-gsi" || SessionId == null || meta.SessionId != SessionId ||
            now() - sampledAt is < 0 or > Wire.TicketLifetimeMs) throw new InvalidOperationException("Old GSI sample");
        using var request = Request(HttpMethod.Post, "/v1/minute-state");
        request.Content = JsonContent.Create(meta, options: Wire.Json);
        Remember(meta, sampledAt);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("仅 GSI 连接失败；请确认游戏端和教练端均为支持分钟路线的版本。", null, response.StatusCode);
    }
}
