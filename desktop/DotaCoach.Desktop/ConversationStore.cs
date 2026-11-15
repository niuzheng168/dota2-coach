using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DotaCoach.Desktop;

public sealed record ConversationTurn(string id, string speaker, string sourceKind, string role,
    string source, string target, long timestamp);

/// <summary>Bounded in-memory history belongs to the session, not the web page.</summary>
public sealed class ConversationStore
{
    private readonly List<ConversationTurn> turns = new();
    private readonly Dictionary<string, string> latestSources = new();
    public ConversationTurn[] Snapshot() => turns.ToArray();
    public void Clear() { turns.Clear(); latestSources.Clear(); }
    public ConversationTurn? Accept(JsonElement message)
    {
        string type = NativeVoiceChannel.Text(message, "type");
        if (!type.StartsWith("subtitle.")) return null;
        string text = NativeVoiceChannel.Text(message, "text");
        if (text.Length > 4000) text = text[..4000];
        string speaker = NativeVoiceChannel.Text(message, "speaker");
        string stream = NativeVoiceChannel.Text(message, "streamId");
        if (stream.Length == 0) stream = speaker + ":" + NativeVoiceChannel.Text(message, "role");
        bool source = type.StartsWith("subtitle.source.");
        if (source)
        {
            if (latestSources.Count >= 100 && !latestSources.ContainsKey(stream)) latestSources.Remove(latestSources.Keys.First());
            latestSources[stream] = text;
        }
        string turn = NativeVoiceChannel.Text(message, "turnId");
        if (turn.Length == 0) return null;
        string key = stream + ":" + turn;
        int index = turns.FindIndex(t => t.id == key);
        var entry = index < 0 ? new ConversationTurn(key, speaker, NativeVoiceChannel.Text(message, "sourceKind"),
            NativeVoiceChannel.Text(message, "role"), source ? "" : latestSources.GetValueOrDefault(stream, ""),
            "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : turns[index];
        entry = source ? entry with { source = text } : entry with { target = text };
        if (index < 0) turns.Add(entry); else turns[index] = entry;
        if (turns.Count > 300) turns.RemoveAt(0);
        return entry;
    }
}
