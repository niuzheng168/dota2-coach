using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotaCoach.TwoPc;

internal static partial class Program
{
    // Detached WPF compositor over the actual six lane polylines. This is not a captured ranked HUD.
    private static void WaveOverlayExample(string input, string png)
    {
        using var sample=JsonDocument.Parse(File.ReadAllText(input));
        var waves=sample.RootElement.GetProperty("map").Deserialize<LaneMap742>(Wire.Json)!;
        const int w=1920, h=1080, side=432;
        var map=new MinimapLayout(w,h,8,h-side-8,side);
        var board=new EnemyBoard([],null,"9000000123",sample.RootElement.GetProperty("team").GetString(),map) { LaneMap=waves };
        if(!Wire.ValidBoard(board)) throw new InvalidDataException("Invalid projected map");
        var screen=new Canvas {Width=w,Height=h};
        AdviceOverlay.Compose(screen,null,new GameOverlaySettings{ ShowEnemies=false,ShowWards=false,ShowLaneWaves=true },
            DisplayGeometry.OverlayUnit(h,1),board,viewport:(w,h));
        screen.Measure(new Size(w,h));screen.Arrange(new Rect(0,0,w,h));screen.UpdateLayout();
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(screen);
        using var geometry=JsonDocument.Parse(File.ReadAllText("data/minute-farm-742-client.json"));
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()){
            var box=new Rect(0,0,side,side);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,31,36)),null,box);
            foreach(var lane in geometry.RootElement.GetProperty("lanes").EnumerateArray()){
                Point? prev=null;
                foreach(var xy in lane.GetProperty("points").EnumerateArray()){
                    var pos=BoardDelivery.OnOverview(xy[0].GetDouble(),xy[1].GetDouble())!;
                    var next=new Point(pos.X*side,pos.Y*side);
                    if(prev is {} p) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(50,74,76)),3),p,next);
                    prev=next;
                }
            }
            dc.DrawImage(new CroppedBitmap(bitmap,new Int32Rect((int)map.Left,(int)map.Top,side,side)),box);
        }
        var crop=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);crop.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(crop));
        using var stream=File.Create(png); encoder.Save(stream);
        Console.WriteLine($"Rendered actual game overlay at {waves.Clock/60}:{waves.Clock%60:00}, {waves.Marks.Length} symbols: {png}");
    }
}
