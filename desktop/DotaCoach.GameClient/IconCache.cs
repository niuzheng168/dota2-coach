using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

/// <summary>
/// Enemy panel icons (Dota's own artwork). A release bundles every hero and item icon in icons\ beside the
/// program; a name it lacks (an item from a later Dota patch, or a build without the bundle) is fetched from the
/// coach once, from the coach machine's local copy. Kept for the client's lifetime; a name the coach could not
/// serve is retried later.
/// </summary>
internal sealed class IconCache
{
    public const int RetryMs = 15_000;
    private readonly string? bundle;
    private readonly Dictionary<string, ImageSource> images = new(StringComparer.Ordinal);
    private readonly HashSet<string> notBundled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> failed = new(StringComparer.Ordinal);
    public IconCache(string? bundleFolder = null) { bundle = bundleFolder; }
    public ImageSource? this[string key] => images.TryGetValue(key, out var image) || FromBundle(key) ? images[key] : null;
    public int Count => images.Count;
    /// <summary>Names on the board that neither this client nor its bundle has, to ask the coach for.</summary>
    public string[] Missing(EnemyBoard board, long now) => board.Heroes
        .SelectMany(h => (h.Items ?? []).Append(h.Neutral).Prepend(h.Hero)).Concat((board.Fog ?? []).Select(f => f.Hero)).OfType<string>().Distinct()
        .Where(key => !images.ContainsKey(key) && !FromBundle(key) && (!failed.TryGetValue(key, out long at) || now - at >= RetryMs)).ToArray();
    public void Add(string key, byte[]? png, long now)
    {
        if (png != null && Decode(key, png)) { failed.Remove(key); return; }
        failed[key] = now;
    }
    private bool FromBundle(string key)
    {
        if (bundle == null || notBundled.Contains(key) || !Wire.ValidIconKey(key)) return false;
        try {
            var file = new FileInfo(Path.Combine(bundle, key + ".png"));
            if (file.Exists && file.Length <= Wire.MaxIconBytes && File.ReadAllBytes(file.FullName) is var png && Wire.IsIcon(png) && Decode(key, png))
                return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        notBundled.Add(key); // looked once; the coach is asked instead
        return false;
    }
    private bool Decode(string key, byte[] png)
    {
        try { images[key] = Ui.Image(png); return true; }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or IOException or InvalidOperationException) { return false; }
    }
}
