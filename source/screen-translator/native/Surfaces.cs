// Purpose: physical-pixel capture selection and click-through text replacement.
// Dependencies: Windows 11, .NET Windows Desktop; no Python UI or web renderer.
// Outputs: in-memory overlay only. Command: pwsh -NoProfile -File ./dev.ps1 [SDK options].
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalScreenTranslator;

internal static class Native
{
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd,out uint affinity);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr first,IntPtr second);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    internal static bool Exclude(IntPtr handle)=>SetWindowDisplayAffinity(handle,0x11)&&GetWindowDisplayAffinity(handle,out uint value)&&value==0x11;
}

internal sealed class RegionSelector : Form
{
    Point? anchor;
    internal Rectangle Selection;
    internal RegionSelector(Rectangle screen)
    {
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;TopMost=true;BackColor=Color.Black;Opacity=.4;
        Cursor=Cursors.Cross;KeyPreview=true;DoubleBuffered=true;
        _=Handle;Bounds=screen;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);using var pen=new Pen(Color.White,2);
        e.Graphics.DrawRectangle(pen,Selection);
        e.Graphics.DrawString("Drag to select. Esc to cancel.",Font,Brushes.White,20,20);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button==MouseButtons.Left){anchor=e.Location;Capture=true;}
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(anchor is not Point start)return;
        Selection=Rectangle.Intersect(ClientRectangle,Rectangle.FromLTRB(Math.Min(start.X,e.X),Math.Min(start.Y,e.Y),Math.Max(start.X,e.X),Math.Max(start.Y,e.Y)));
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);if(e.Button!=MouseButtons.Left||anchor is null)return;
        Capture=false;DialogResult=Selection.Width>=5&&Selection.Height>=5?DialogResult.OK:DialogResult.Cancel;Close();
    }
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}}
}

internal sealed class ReplacementOverlay : Form
{
    Bitmap? canvas;
    readonly List<Rectangle> painted=new();
    internal bool Masked;
    internal bool HasVisibleText=>painted.Count>0;
    internal bool CaptureExcluded;
    internal int Clipped,Blocks;
    internal Color Accent=Color.FromArgb(77,142,225);
    internal string FontFamilyName="Segoe UI",DisplayStyle="underline";
    internal bool AutoFit=true;
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams
    {
        get{var value=base.CreateParams;value.ExStyle|=0x80000|0x20|0x80|0x08000000|0x8;return value;}
    }
    internal ReplacementOverlay()
    {
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
        // Native WS_EX_TOPMOST avoids WinForms' TopMost focus restoration on every Show().
        BackColor=Color.Fuchsia;TransparencyKey=Color.Fuchsia;DoubleBuffered=true;
        _=Handle;
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x21){message.Result=new IntPtr(3);return;}
        if(message.Msg==0x84){message.Result=new IntPtr(-1);return;}
        base.WndProc(ref message);
    }
    static Color Palette(Bitmap image,Rectangle rect)
    {
        var samples=new List<Color>();
        // Sample the perimeter outside source glyphs; each channel's median resists text outliers.
        for(int step=0;step<=12;step++)
        {
            int x=rect.Left+rect.Width*step/12,y=rect.Top+rect.Height*step/12;
            foreach(var point in new[]{new Point(x,rect.Top-1),new Point(x,rect.Bottom),new Point(rect.Left-1,y),new Point(rect.Right,y)})
                samples.Add(image.GetPixel(Math.Clamp(point.X,0,image.Width-1),Math.Clamp(point.Y,0,image.Height-1)));
        }
        int Median(Func<Color,int> channel)=>samples.Select(channel).Order().ElementAt(samples.Count/2);
        return Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
    }
    internal void Replace(Bitmap image,JsonElement regions,Rectangle target,float scale,bool debug,bool present=true)
    {
        // Keep the visible surface alive during refresh; UI-thread painting cannot interleave this swap.
        var previousCanvas=canvas;canvas=new Bitmap(image.Width,image.Height,PixelFormat.Format32bppArgb);
        canvas.SetResolution(96,96);using var graphics=Graphics.FromImage(canvas);
        graphics.Clear(Color.Fuchsia);graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var shape=new Region();shape.MakeEmpty();Clipped=Blocks=0;painted.Clear();Masked=false;
        var obstacles=regions.EnumerateArray().Select(region=>region.GetProperty("polygon").EnumerateArray()
            .Select(p=>new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray()).Where(p=>p.Length>=4)
            .Select(p=>RectangleF.FromLTRB(p.Min(v=>v.X)-2,p.Min(v=>v.Y)-2,p.Max(v=>v.X)+2,p.Max(v=>v.Y)+2)).ToArray();
        // One pixel copy makes conservative whitespace scanning independent of GDI GetPixel overhead.
        using var pixelImage=image.Clone(new Rectangle(0,0,image.Width,image.Height),PixelFormat.Format32bppArgb);
        var locked=pixelImage.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        int stride=locked.Stride;var pixels=new byte[stride*image.Height];
        try{Marshal.Copy(locked.Scan0,pixels,0,pixels.Length);}finally{pixelImage.UnlockBits(locked);}
        bool FlatColumn(int x,Rectangle area,Color colour)
        {
            for(int y=area.Top;y<area.Bottom;y++)
            {int index=y*stride+x*4;if(Math.Abs(pixels[index]-colour.B)>20||Math.Abs(pixels[index+1]-colour.G)>20||Math.Abs(pixels[index+2]-colour.R)>20)return false;}
            return true;
        }
        bool FlatRow(int y,Rectangle area,Color colour)
        {
            for(int x=area.Left;x<area.Right;x++)
            {int index=y*stride+x*4;if(Math.Abs(pixels[index]-colour.B)>20||Math.Abs(pixels[index+1]-colour.G)>20||Math.Abs(pixels[index+2]-colour.R)>20)return false;}
            return true;
        }
        foreach(var region in regions.EnumerateArray())
        {
            string text=region.GetProperty("translated").GetString()??"";
            if(text.Length==0||text==region.GetProperty("source").GetString())continue;
            var points=region.GetProperty("polygon").EnumerateArray().Select(p=>new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray();
            if(points.Length<4||points.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)))throw new InvalidDataException("Invalid OCR polygon");
            var rect=Rectangle.Intersect(new Rectangle(0,0,image.Width,image.Height),Rectangle.FromLTRB(
                (int)Math.Floor(points.Min(p=>p.X))-1,(int)Math.Floor(points.Min(p=>p.Y))-1,
                (int)Math.Ceiling(points.Max(p=>p.X))+1,(int)Math.Ceiling(points.Max(p=>p.Y))+1));
            if(rect.Width<3||rect.Height<3)continue;
            Color background=Palette(image,rect);
            int maximum=Math.Max(8,(int)Math.Round(rect.Height*.60*scale)),minimum=AutoFit?Math.Max(8,(int)Math.Round(maximum*.35)):maximum;
            using(var preferred=new Font(FontFamilyName,maximum,FontStyle.Regular,GraphicsUnit.Pixel))
            {
                if(scale>1||FontFamilyName!="Segoe UI"||!AutoFit)
                {
                    var own=RectangleF.FromLTRB(points.Min(p=>p.X)-2,points.Min(p=>p.Y)-2,points.Max(p=>p.X)+2,points.Max(p=>p.Y)+2);
                    // DrawString includes leading beyond the font's line height; reserve its measured extent.
                    int desiredHeight=(int)Math.Ceiling(Math.Max(preferred.GetHeight(graphics),graphics.MeasureString(text,preferred).Height))+4;
                    bool CanGrow(int y)=>y>=0&&y<image.Height&&FlatRow(y,rect,background)&&
                        !obstacles.Any(o=>o!=own&&o.IntersectsWith(new RectangleF(rect.Left,y,rect.Width,1)))&&
                        !painted.Any(o=>o.IntersectsWith(new Rectangle(rect.Left,y,rect.Width,1)));
                    // Grow equally above and below only through flat whitespace, stopping before other glyphs.
                    bool above=true,below=true;
                    while(rect.Height<desiredHeight&&(above||below))
                    {
                        if(above){above=CanGrow(rect.Top-1);if(above){rect.Y--;rect.Height++;}}
                        if(rect.Height<desiredHeight&&below){below=CanGrow(rect.Bottom);if(below)rect.Height++;}
                    }
                }
                int desired=(int)Math.Ceiling(graphics.MeasureString(text,preferred).Width)+4;
                int limit=Math.Min(image.Width,rect.Left+Math.Min(desired,rect.Width*4));
                foreach(var obstacle in obstacles)
                    if(obstacle.Left>=rect.Right&&obstacle.Top<rect.Bottom&&obstacle.Bottom>rect.Top)
                        limit=Math.Min(limit,(int)Math.Floor(obstacle.Left)-2);
                // Expand only into uninterrupted flat pixels. Stop before neighbouring text, icons or borders.
                int right=rect.Right;while(right<limit&&FlatColumn(right,rect,background))right++;
                rect.Width=right-rect.Left;
            }
            // Perceived RGB luminance chooses readable contrast on the sampled flat background.
            double luminance=.2126*background.R+.7152*background.G+.0722*background.B;
            if(DisplayStyle=="contrast"){background=Color.FromArgb(24,26,30);luminance=24;}
            using var fill=new SolidBrush(background);using var ink=new SolidBrush(luminance>=145?Color.FromArgb(32,33,36):Color.White);
            graphics.SetClip(rect);graphics.FillRectangle(fill,rect);
            var available=new RectangleF(rect.X+1,rect.Y,Math.Max(1,rect.Width-2),rect.Height);
            // Try both single-line and wrapped layout at each size, retaining readable pixel height.
            using var format=new StringFormat {Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Center,
                FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter};
            Font? font=null;bool fits=false;
            for(int size=maximum;size>=minimum;size--)
            {
                font?.Dispose();font=new Font(FontFamilyName,size,FontStyle.Regular,GraphicsUnit.Pixel);
                format.FormatFlags=StringFormatFlags.NoWrap;
                var measured=graphics.MeasureString(text,font,int.MaxValue,format);
                if(measured.Width<=available.Width&&measured.Height<=available.Height-2){fits=true;break;}
                if(!text.Any(char.IsWhiteSpace))continue;
                format.FormatFlags=StringFormatFlags.LineLimit;
                measured=graphics.MeasureString(text,font,new SizeF(available.Width,100000),format);
                if(measured.Width<=available.Width&&measured.Height<=available.Height-2){fits=true;break;}
            }
            try
            {
                if(!fits)
                {
                    Clipped++;format.FormatFlags=StringFormatFlags.NoWrap;
                }
                graphics.DrawString(text,font!,ink,available,format);
                // Only changed regions get an underline. Amber exposes text that cannot fit.
                if(DisplayStyle=="underline"||!fits)
                    using(var underline=new Pen(fits?Accent:Color.FromArgb(193,124,30),Math.Max(1,rect.Height/18f)))
                        graphics.DrawLine(underline,rect.Left+1,rect.Bottom-1,rect.Right-2,rect.Bottom-1);
                if(debug){using var pen=new Pen(Color.FromArgb(52,130,255));graphics.DrawRectangle(pen,rect.X,rect.Y,rect.Width-1,rect.Height-1);}
            }
            finally{font?.Dispose();graphics.ResetClip();}
            shape.Union(rect);painted.Add(rect);Blocks++;
        }
        var previous=Region;Region=shape.Clone();previous?.Dispose();Bounds=target;
        previousCanvas?.Dispose();
        if(Blocks==0){Hide();return;}
        if(present&&!Visible)Show();CaptureExcluded=Native.Exclude(Handle);Invalidate();if(present)Update();
    }
    internal void MaskChanged(IReadOnlyList<Rectangle> changes)
    {
        if(canvas is null)return;
        // Remove whole rendered words, including whitespace expansion; never leave half a translation.
        int removed=painted.RemoveAll(r=>changes.Any(c=>c.IntersectsWith(r)));
        if(removed==0)return;Masked=true;
        using var shape=new Region();shape.MakeEmpty();foreach(var rect in painted)shape.Union(rect);
        var previous=Region;Region=shape.Clone();previous?.Dispose();
        if(painted.Count==0)Hide();else Invalidate();
    }
    internal void Clear(){Hide();canvas?.Dispose();canvas=null;painted.Clear();Masked=false;CaptureExcluded=false;Clipped=Blocks=0;}
    internal bool Reveal(){if(canvas is null||!HasVisibleText)return false;Show();CaptureExcluded=Native.Exclude(Handle);Invalidate();return true;}
    internal object[] CheckPaintDpi()
    {
        if(canvas is null)throw new InvalidOperationException("Render generated content before checking DPI");
        var checks=new List<object>();
        foreach(int dpi in new[]{96,144,192,240})
        {
            using var painted=new Bitmap(canvas.Width,canvas.Height,PixelFormat.Format32bppArgb);painted.SetResolution(dpi,dpi);
            using(var graphics=Graphics.FromImage(painted))
            {graphics.Clear(Color.Fuchsia);graphics.SetClip(Region!,CombineMode.Replace);OnPaint(new PaintEventArgs(graphics,new Rectangle(Point.Empty,canvas.Size)));}
            int differing=0;
            for(int y=0;y<canvas.Height;y++)for(int x=0;x<canvas.Width;x++)
                if(painted.GetPixel(x,y).ToArgb()!=canvas.GetPixel(x,y).ToArgb())differing++;
            checks.Add(new {dpi,differingPixels=differing,ok=differing==0});
        }
        return checks.ToArray();
    }
    internal void SaveComposite(Bitmap image,string path)
    {
        using var result=(Bitmap)image.Clone();using var graphics=Graphics.FromImage(result);
        if(canvas is not null){using var layer=(Bitmap)canvas.Clone();layer.MakeTransparent(Color.Fuchsia);graphics.SetClip(Region!,CombineMode.Replace);DrawPixels(graphics,layer);}
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write);result.Save(output,ImageFormat.Png);
    }
    static void DrawPixels(Graphics graphics,Bitmap image)
    {
        // Source and destination are physical pixels. The two-argument image draw scales by DPI.
        var state=graphics.Save();
        try{graphics.PageUnit=GraphicsUnit.Pixel;graphics.DrawImage(image,new Rectangle(Point.Empty,image.Size),0,0,image.Width,image.Height,GraphicsUnit.Pixel);}
        finally{graphics.Restore(state);}
    }
    protected override void OnPaint(PaintEventArgs e){if(canvas is not null)DrawPixels(e.Graphics,canvas);}
    protected override void Dispose(bool disposing){if(disposing)canvas?.Dispose();base.Dispose(disposing);}
}

internal sealed class ToolbarButton : Button
{
    // Remove WinForms' rectangular white cue. A subtle fill change preserves
    // keyboard focus feedback inside the button's existing rounded region.
    protected override bool ShowFocusCues=>false;
    public override Color BackColor
    {
        get{var colour=base.BackColor;return Focused&&Enabled?Color.FromArgb((colour.R*9+ForeColor.R)/10,(colour.G*9+ForeColor.G)/10,(colour.B*9+ForeColor.B)/10):colour;}
        set=>base.BackColor=value;
    }
}

internal sealed class TranslationToolbar : Form
{
    Color accent=Color.FromArgb(52,130,255),surface=Color.White,foreground=Color.FromArgb(47,64,87);
    readonly Button original=new ToolbarButton(),filter=new ToolbarButton(),screenshot=new ToolbarButton(),dismiss=new ToolbarButton();
    readonly Label state=new();
    readonly Label title=new();
    internal bool IsActive
    {
        get{var foreground=Native.GetForegroundWindow();return foreground==Handle||foreground==IntPtr.Zero&&Native.GetActiveWindow()==Handle;}
    }
    internal void FocusForAction(){Activate();original.Focus();Native.SetForegroundWindow(Handle);}
    internal bool CaptureExcluded;
    internal bool ScreenshotAvailable {get=>screenshot.Enabled;set=>screenshot.Enabled=value;}
    internal void ScreenshotSaved(){state.Text="PNG saved";}
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams
    {get{var value=base.CreateParams;value.ExStyle|=0x8|0x80;return value;}}
    internal TranslationToolbar(Func<Task> toggleOriginal,Func<Task> toggleFilter,Action close,Func<Task> saveScreenshot)
    {
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;Text="Screen Translator";BackColor=Color.FromArgb(247,249,252);
        _=Handle;double scale=DeviceDpi/96.0;int Px(int value)=>(int)Math.Round(value*scale);
        ClientSize=new Size(Px(560),Px(86));Font=new Font("Segoe UI",10);
        using(var corners=new GraphicsPath())
        {
            int diameter=Px(28);corners.AddArc(0,0,diameter,diameter,180,90);corners.AddArc(Width-diameter,0,diameter,diameter,270,90);
            corners.AddArc(Width-diameter,Height-diameter,diameter,diameter,0,90);corners.AddArc(0,Height-diameter,diameter,diameter,90,90);corners.CloseFigure();Region=new Region(corners);
        }
        title.Text="Screen Translator";title.Font=new Font("Segoe UI Semibold",10);title.ForeColor=Color.FromArgb(36,45,61);
        title.SetBounds(Px(16),Px(10),Px(156),Px(22));title.Cursor=Cursors.SizeAll;
        title.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){Native.ReleaseCapture();Native.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}};
        state.SetBounds(Px(172),Px(10),Px(372),Px(22));state.TextAlign=ContentAlignment.MiddleRight;state.ForeColor=Color.FromArgb(91,104,125);
        int left=16;
        foreach(var button in new[]{original,filter,screenshot,dismiss})
        {
            int width=button==original?152:button==filter?126:button==screenshot?110:114;
            button.SetBounds(Px(left),Px(40),Px(width),Px(32));left+=width+8;
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=0;button.BackColor=Color.White;button.ForeColor=Color.FromArgb(47,64,87);button.Cursor=Cursors.Hand;
            using(var corners=new GraphicsPath())
            {int d=Px(16),w=button.Width,h=button.Height;corners.AddArc(0,0,d,d,180,90);corners.AddArc(w-d,0,d,d,270,90);corners.AddArc(w-d,h-d,d,d,0,90);corners.AddArc(0,h-d,d,d,90,90);corners.CloseFigure();button.Region=new Region(corners);}
            button.FlatAppearance.MouseOverBackColor=Color.FromArgb(231,238,248);Controls.Add(button);
        }
        Controls.Add(title);Controls.Add(state);
        original.Click+=async (_,_)=>await toggleOriginal();filter.Click+=async (_,_)=>await toggleFilter();dismiss.Click+=(_,_)=>close();dismiss.Text="Dismiss · Esc";
        screenshot.Text="Save PNG";screenshot.Enabled=false;screenshot.Click+=async (_,_)=>await saveScreenshot();
        KeyPreview=true;KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){e.Handled=true;close();}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;close();}};
    }
    internal void Present(Rectangle target,bool showingOriginal,bool enabled,int blocks,int clipped)
    {
        original.Enabled=filter.Enabled=true;
        original.Text=showingOriginal?"Show translation":"Show original";filter.Text=enabled?"Filter: On":"Filter: Off";
        filter.BackColor=enabled?accent:surface;filter.ForeColor=enabled?Color.White:foreground;
        state.Text=clipped>0?$"{clipped} do not fit":showingOriginal?"Original · paused":$"{blocks} translated · {(enabled?"Live":"Snapshot")}";
        if(!Visible){var area=Screen.FromRectangle(target).WorkingArea;Location=new Point(area.Right-Width-16,area.Top+16);Show();}
        CaptureExcluded=Native.Exclude(Handle);
    }
    internal void SavePreview(string path){using var preview=new Bitmap(Width,Height);DrawToBitmap(preview,ClientRectangle);preview.Save(path,ImageFormat.Png);}
    internal void MarkStale(){state.Text="Updating changed text…";}
    internal void Loading(Rectangle bounds)
    { Present(bounds,false,false,0,0);state.Text="Loading cached models…";original.Enabled=filter.Enabled=screenshot.Enabled=false; }
    internal void Appearance(Color colour,bool dark)
    {
        accent=colour;surface=dark?Color.FromArgb(37,41,47):Color.White;foreground=dark?Color.FromArgb(232,237,244):Color.FromArgb(47,64,87);
        BackColor=dark?Color.FromArgb(23,26,31):Color.FromArgb(247,249,252);title.ForeColor=state.ForeColor=foreground;
        foreach(var button in new[]{original,filter,screenshot,dismiss}){button.BackColor=surface;button.ForeColor=foreground;button.FlatAppearance.MouseOverBackColor=dark?Color.FromArgb(52,58,66):Color.FromArgb(231,238,248);}
    }
}
