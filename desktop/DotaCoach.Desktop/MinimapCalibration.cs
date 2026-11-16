using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DotaCoach.Desktop;

/// <summary>Client-relative physical pixels, not monitor DIPs. No guessed default alignment.</summary>
public sealed record MinimapCalibration(
    double Left, double Top, double Width, double Height,
    double MinX, double MaxX, double MinY, double MaxY, int ClientWidth, int ClientHeight)
{
    public static double Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n)
            ? n : throw new ArgumentException("小地图标定数值无效：" + key);

    public static MinimapCalibration Parse(JsonElement e, int width, int height, bool requireConfirmed = true)
    {
        if (requireConfirmed && (!e.TryGetProperty("confirmed", out var yes) || yes.ValueKind != JsonValueKind.True))
            throw new ArgumentException("请先在自定义局确认白点与英雄小地图图标重合，再启用标记");
        var c = new MinimapCalibration(Number(e, "left"), Number(e, "top"), Number(e, "width"), Number(e, "height"),
            Number(e, "minX"), Number(e, "maxX"), Number(e, "minY"), Number(e, "maxY"), width, height);
        if (width < 640 || height < 480 || c.Left < 0 || c.Top < 0 || c.Width < 80 || c.Height < 80 ||
            c.Left + c.Width > width || c.Top + c.Height > height ||
            c.MinX >= c.MaxX || c.MinY >= c.MaxY || c.MinX < -16000 || c.MaxX > 16000 ||
            c.MinY < -16000 || c.MaxY > 16000)
            throw new ArgumentException("小地图范围、世界坐标边界或游戏客户区尺寸无效");
        return c;
    }

    public (double X, double Y)? Project(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < MinX || x > MaxX || y < MinY || y > MaxY) return null;
        return (Left + (x - MinX) / (MaxX - MinX) * Width, Top + (MaxY - y) / (MaxY - MinY) * Height);
    }

    public static bool Fresh(JsonElement state, long receivedAt, long now)
    {
        try
        {
            if (now < receivedAt || now - receivedAt > 8000 || !state.GetProperty("enabled").GetBoolean()) return false;
            // A new client must not display an old server's rule-based route as an LLM plan.
            string? source = state.GetProperty("planner").GetProperty("kind").GetString();
            if (source is not ("llm" or "numeric-benchmark")) return false;
            double age = now - Number(state, "updatedAt");
            var p = state.GetProperty("player"); var route = state.GetProperty("route");
            double clock = Number(p, "clock"), planned = Number(route, "plannedAt");
            return age >= -500 && age <= 8000 && p.GetProperty("alive").GetBoolean() && p.GetProperty("canAct").GetBoolean() &&
                route.GetProperty("status").GetString() == "ready" && clock >= planned && clock - planned <= 12 &&
                Number(route, "validUntil") > clock;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }
}
