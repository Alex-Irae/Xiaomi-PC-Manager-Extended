// Purpose: convert the existing frontend translation glyph into a multi-size Windows icon.
// Dependencies: Windows .NET Framework System.Drawing, no image packages.
// Outputs: logo.ico. Build: tools/build_app.py. Command: IconBuilder.exe output.ico.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
internal static class IconBuilder
{
    static void Main(string[] args)
    {
        var frames=new List<byte[]>();int[] sizes={16,24,32,48,64,128,256};
        foreach(int size in sizes)
        {
            using(var bitmap=new Bitmap(size,size))using(var g=Graphics.FromImage(bitmap))
            {
                g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size/44f,size/44f);
                using(var background=new GraphicsPath())using(var brush=new SolidBrush(Color.Black))
                {background.AddArc(0,0,28,28,180,90);background.AddArc(16,0,28,28,270,90);background.AddArc(16,16,28,28,0,90);background.AddArc(0,16,28,28,90,90);background.CloseFigure();g.FillPath(brush,background);}
                g.TranslateTransform(9,9);g.ScaleTransform(26f/24,26f/24);
                using(var pen=new Pen(Color.White,1.6f)){pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                    g.DrawLine(pen,3,5,14,5);g.DrawLine(pen,8,3,8,5);g.DrawLine(pen,5,9,12,16);
                    g.DrawBezier(pen,12,5,12,11,8,15,3,17);g.DrawLines(pen,new[]{new PointF(14,20),new PointF(18,8),new PointF(22,20)});g.DrawLine(pen,15,17,21,17);}
                using(var stream=new MemoryStream()){bitmap.Save(stream,ImageFormat.Png);frames.Add(stream.ToArray());}
            }
        }
        using(var stream=File.Create(args[0]))using(var writer=new BinaryWriter(stream))
        {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;}
            foreach(byte[] frame in frames)writer.Write(frame);
        }
    }
}
