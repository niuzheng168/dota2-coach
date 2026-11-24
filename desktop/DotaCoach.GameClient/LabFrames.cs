using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaCoach.TwoPc;

/// <summary>
/// Last-hit lab only (scripts/lasthit/lab.ts --frames): the Dota window's client area saved as JPEG pictures, at most
/// <see cref="Rate"/> a second, for training a model on creep attacks. frames.jsonl gets one line per picture: its file, the
/// capture time (QPC seconds, the clock of the lab's sync file) and the bars the scan found in it. Encoded on the thread pool;
/// a picture that would wait behind <see cref="MaxQueued"/> others is skipped and counted.
/// </summary>
internal sealed class LabFrames : IDisposable
{
    public const double Rate = 30;
    public const int MaxQueued = 6, Quality = 92;
    private readonly string directory;
    private readonly StreamWriter index;
    private readonly object gate = new();
    private int next, queued, skipped;
    private double due;

    public LabFrames(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        // Flushed line by line: the lab ends the game client by killing it.
        index = new StreamWriter(new FileStream(Path.Combine(directory, "frames.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    /// <summary>Whether the frame captured at <paramref name="at"/> is to be saved (a few milliseconds of slack for capture jitter).</summary>
    public bool Due(double at)
    {
        if (at < due) return false;
        due = at + 1 / Rate - .004;
        if (Volatile.Read(ref queued) < MaxQueued) return true;
        Interlocked.Increment(ref skipped);
        return false;
    }

    /// <summary>A buffer for one picture of this size; <see cref="Save"/> returns it.</summary>
    public static byte[] Rent(int width, int height) => ArrayPool<byte>.Shared.Rent(width * height * 4);

    /// <summary>Encodes and writes the picture (BGRA rows of <paramref name="width"/> pixels) in the background.</summary>
    public void Save(byte[] pixels, int width, int height, double at, IReadOnlyList<CreepHealthBar> bars)
    {
        int number = next++;
        var found = bars.Select(b => new[] { b.X, b.Y, b.Rows, b.Fill, b.Ally ? 1 : 0, b.Covered ? 1 : 0 }).ToArray();
        Interlocked.Increment(ref queued);
        Task.Run(() => {
            try {
                string file = $"{number:D6}.jpg";
                var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
                var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using (var stream = File.Create(Path.Combine(directory, file))) encoder.Save(stream);
                string line = JsonSerializer.Serialize(new { i = number, file, at, width, height, bars = found, skipped = Volatile.Read(ref skipped) });
                lock (gate) index.WriteLine(line);
            } catch (Exception ex) {
                lock (gate) index.WriteLine(JsonSerializer.Serialize(new { i = number, error = ex.Message }));
            } finally {
                ArrayPool<byte>.Shared.Return(pixels);
                Interlocked.Decrement(ref queued);
            }
        });
    }

    public void Dispose()
    {
        for (int i = 0; i < 200 && Volatile.Read(ref queued) > 0; i++) Thread.Sleep(25);
        lock (gate) index.Dispose();
    }
}
