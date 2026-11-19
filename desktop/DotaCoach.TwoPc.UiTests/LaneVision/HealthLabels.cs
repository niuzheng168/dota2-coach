using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal readonly record struct HealthTag(Int32Rect Box, LaneDrill.Reading Reading);

// Experimental overlay (test assembly only). Tight clusters share one click-through window.
// Distant creeps never stretch one transparent
// surface across the entire game. Layout and drawing also work offline without any HWND.
internal sealed class HealthLayout
{
    internal const int Columns=6, Rows=4;
    internal sealed class Tile
    {
        internal Int32Rect Bounds;
        internal readonly List<HealthTag> Tags = new(8);
        internal void Add(HealthTag t)
        {
            var a=Bounds; var b=t.Box;
            int x=Math.Min(a.X,b.X), y=Math.Min(a.Y,b.Y);
            Bounds=Tags.Count==0 ? b : new(x,y,Math.Max(a.X+a.Width,b.X+b.Width)-x,Math.Max(a.Y+a.Height,b.Y+b.Height)-y);
            Tags.Add(t);
        }
    }
    internal readonly Tile[] Tiles = new Tile[Columns*Rows];
    private readonly List<Int32Rect> occupied = new(32);
    internal int Placed => occupied.Count;
    internal HealthLayout() { for(int i=0;i<Tiles.Length;i++) Tiles[i]=new(); }
    internal static bool Touches(Int32Rect a, Int32Rect b) => a.X<b.X+b.Width+1 && b.X<a.X+a.Width+1 &&
        a.Y<b.Y+b.Height+1 && b.Y<a.Y+a.Height+1;

    internal void Arrange(ReadOnlySpan<LaneDrill.Reading> readings, int width, int height)
    {
        occupied.Clear();
        foreach(var tile in Tiles) { tile.Tags.Clear(); tile.Bounds=default; }
        foreach(var r in readings) {
            if (!TryPlace(r,readings,width,height) && r.Next!=null)
                TryPlace(r with {Next=null},readings,width,height); // Prefer current HP to no label in a crowd.
        }
    }

    private bool TryPlace(LaneDrill.Reading r,ReadOnlySpan<LaneDrill.Reading> readings,int width,int height)
    {
        var b=r.Bar;
        int w=Math.Max(30,(int)Math.Ceiling(b.Width*HealthInk.DesignWidth(r)/80d)), h=Math.Max(13,b.Width/4);
        if (w>width || h>height) return false;
        for(int side=0;side<4;side++) {
            int x=side switch { 2=>b.X+b.Width+3, 3=>b.X-w-3, _=>b.X+(b.Width-w)/2 };
            int y=side switch { 0=>b.Y-h-2, 1=>b.Y+b.Height+2, _=>b.Y+(b.Height-h)/2 };
            var box=new Int32Rect(Math.Clamp(x,0,width-w),Math.Clamp(y,0,height-h),w,h);
            bool blocked=false;
            foreach(var other in readings) {
                var q=other.Bar;
                if(Touches(box,new(q.X,q.Y,q.Width,q.Height))) { blocked=true; break; }
            }
            if(blocked) continue;
            foreach(var used in occupied) if(Touches(box,used)) { blocked=true; break; }
            if(blocked) continue;
            // If all four adjacent slots are occupied, omit rather than cover a bar or
            // put the number somewhere that appears to belong to a different creep.
            occupied.Add(box);
            int col=(box.X+w/2)*Columns/width, row=(box.Y+h/2)*Rows/height;
            Tiles[row*Columns+col].Add(new(box,r));
            return true;
        }
        return false;
    }
    internal static Int32Rect Screen(Int32Rect r,int fw,int fh,int x,int y,int w,int h)
    {
        int X(int n)=>(int)Math.Round((double)n*w/fw);
        int Y(int n)=>(int)Math.Round((double)n*h/fh);
        return new(x+X(r.X),y+Y(r.Y),X(r.X+r.Width)-X(r.X),Y(r.Y+r.Height)-Y(r.Y));
    }
}

internal sealed class HealthInk
{
    private readonly Dictionary<(int,LaneCreepType,int?,LaneForecast?),DrawingImage> glyphs = new();
    private double dpi;
    private static readonly Typeface Face=new(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
    internal static int DesignWidth(LaneDrill.Reading r) => r.Next!=null ? 232 : r.RangedAlternative!=null ? 174 : 89;
    internal static string Label(LaneDrill.Reading r)
    {
        if(r.RangedAlternative is int ranged) return $"近≈{r.Value} / 远≈{ranged} HP";
        string type=r.Type==LaneCreepType.Melee?"近":"远";
        return r.Next is {} n ? FormattableString.Invariant($"{type}≈{r.Value} → {n.LowHp}–{n.HighHp} HP · {n.EtaMs/1000d:F2}s") :
            $"{type}≈{r.Value} HP";
    }
    internal void Draw(DrawingContext dc, List<HealthTag> tags, int w, int h, Size area, double pixelsPerDip)
    {
        if (w <= 0 || h <= 0) return;
        double sx = area.Width / w, sy = area.Height / h;
        foreach (var t in tags) {
            var b = t.Box;
            dc.DrawImage(Glyph(t.Reading, pixelsPerDip), new Rect(b.X*sx, b.Y*sy, b.Width*sx, b.Height*sy));
        }
    }
    internal DrawingImage Glyph(LaneDrill.Reading r,double pixelsPerDip)
    {
        if (dpi!=pixelsPerDip) { glyphs.Clear(); dpi=pixelsPerDip; }
        var key=(r.Value,r.Type,r.RangedAlternative,r.Next);
        if(glyphs.TryGetValue(key,out var cached)) return cached;
        if(glyphs.Count>=512) glyphs.Clear();
        var group=new DrawingGroup(); double w=DesignWidth(r);
        using(var dc=group.Open()) {
            dc.DrawRoundedRectangle(Brushes.Black,null,new Rect(0,0,w,20),3,3);
            var text=new FormattedText(Label(r),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
                Face,14,Brushes.White,pixelsPerDip);
            dc.DrawText(text,new Point((w-text.Width)/2,(20-text.Height)/2));
        }
        var image=new DrawingImage(group); image.Freeze(); glyphs.Add(key,image); return image;
    }
}
