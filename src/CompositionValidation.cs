using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace CodexUsageTray
{
    internal static class PreviewHost
    {
        internal static void Render(Form form,string path)
        {
            // Render each native child explicitly: the parent DrawToBitmap omits some hidden native controls.
            form.PerformLayout();PrepareTabs(form);using(var bitmap=new Bitmap(form.Width,form.Height))
            {
                form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                Point origin=form.PointToScreen(Point.Empty);origin.Offset(-form.Left,-form.Top);
                using(var graphics=Graphics.FromImage(bitmap))RenderNativeChildren(form,graphics,origin);
                bitmap.Save(path);
            }
        }
        static void PrepareTabs(Control parent)
        {
            var tabs=parent as TabControl;if(tabs!=null&&tabs.SelectedTab!=null){tabs.SelectedTab.Bounds=tabs.DisplayRectangle;tabs.SelectedTab.PerformLayout();}
            foreach(Control child in parent.Controls)PrepareTabs(child);
        }
        static void RenderNativeChildren(Control parent,Graphics graphics,Point offset)
        {
            foreach(Control child in parent.Controls)
            {
                var tabs=parent as TabControl;if(tabs!=null&&child!=tabs.SelectedTab)continue;
                child.PerformLayout();Point origin=new Point(offset.X+child.Left,offset.Y+child.Top);
                if(child is DataGridView||child is TextBox)
                {
                    using(var image=new Bitmap(Math.Max(1,child.Width),Math.Max(1,child.Height)))
                    {
                        child.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                        graphics.DrawImageUnscaled(image,origin.X,origin.Y);
                    }
                }
                else RenderNativeChildren(child,graphics,origin);
            }
        }
    }
    internal sealed class CompositionCanvas : Form
    {
        internal Color Fill=Color.FromArgb(230,170,60);internal bool Pattern;
        public CompositionCanvas(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;}
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Fill);if(Pattern)for(int x=0;x<Width;x+=8)using(var brush=new SolidBrush((x/8)%2==0?Color.White:Color.Black))e.Graphics.FillRectangle(brush,x,0,8,Height);
        }
    }
    internal static class CompositionValidation
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        static void Settle(){for(int i=0;i<12;i++){Application.DoEvents();Thread.Sleep(20);}DwmFlush();}
        static Bitmap Capture(Rectangle bounds){var bmp=new Bitmap(bounds.Width,bounds.Height);using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);return bmp;}
        static double Distance(Color a,Color b){return (Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/3.0;}
        static double Variation(Bitmap bmp,Rectangle area)
        {
            double count=0,total=0;
            for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left+1;x<area.Right;x++){total+=Distance(bmp.GetPixel(x,y),bmp.GetPixel(x-1,y));count++;}
            return total/Math.Max(1,count);
        }
        internal static int Run(string output)
        {
            Directory.CreateDirectory(output);IntPtr foreground=GetForegroundWindow();
            var area=Screen.PrimaryScreen.WorkingArea;
            using(var canvas=new CompositionCanvas())using(var widget=new FloatingWidget(delegate{},delegate(Point p){}))
            {
                // This diagnostic shows only a generated local pattern; it never activates a window or injects input.
                widget.ApplyLogicalSize(new Size(320,220));
                canvas.Bounds=new Rectangle(area.Left+24,area.Bottom-widget.Height-40,widget.Width+48,widget.Height+28);canvas.Show();
                widget.Location=new Point(canvas.Left+24,canvas.Top+14);
                widget.UpdateSnapshot(new Snapshot{Remaining=74,A=1290023,Running=true,Stale=false,AHealth="ok",BHealth="ok"},true,false);
                widget.UpdateDeepSeek(new DeepSeekSnapshot{Balance=100m,A=123456,Stale=false});widget.SetTransparency(78);widget.Show();Settle();
                Rectangle crop=widget.Bounds;int sampleX=(int)(160*widget.RenderScale),sampleY=(int)(178*widget.RenderScale);
                Color first,second;double cornerError;
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"warm-background.png"));first=bmp.GetPixel(sampleX,sampleY);}
                canvas.Fill=Color.FromArgb(40,100,230);canvas.Invalidate();Settle();
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"cool-background.png"));second=bmp.GetPixel(sampleX,sampleY);}
                widget.SetTransparency(0);Settle();Color opaque;
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"transparency-0.png"));opaque=bmp.GetPixel(sampleX,sampleY);}
                widget.SetTransparency(90);Settle();Color transparent;
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"transparency-90.png"));transparent=bmp.GetPixel(sampleX,sampleY);}
                widget.SetTransparency(60);Settle();
                canvas.Pattern=true;canvas.Invalidate();Settle();double blurVariation,plainVariation;
                var sample=new Rectangle((int)(28*widget.RenderScale),(int)(170*widget.RenderScale),(int)(260*widget.RenderScale),(int)(18*widget.RenderScale));
                Color blurredCorner;
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"blurred-checker.png"));blurVariation=Variation(bmp,sample);blurredCorner=bmp.GetPixel(0,0);}
                widget.Hide();Settle();
                using(var bmp=Capture(crop)){bmp.Save(Path.Combine(output,"unblurred-checker.png"));plainVariation=Variation(bmp,sample);cornerError=Distance(blurredCorner,bmp.GetPixel(0,0));}
                bool preserved=GetForegroundWindow()==foreground;
                var evidence=new{background_response=Distance(first,second),transparency_response=Distance(opaque,transparent),corner_error=cornerError,blur_variation=blurVariation,unblurred_variation=plainVariation,foreground_preserved=preserved,material_request_succeeded=widget.FrostedReady};
                File.WriteAllText(Path.Combine(output,"composition-validation.json"),new JavaScriptSerializer().Serialize(evidence));
                bool materialValid=widget.FrostedReady?blurVariation<plainVariation*.35:blurVariation>plainVariation*.45&&blurVariation<plainVariation*.75;
                return evidence.background_response>15&&evidence.transparency_response>15&&cornerError<3&&plainVariation>3&&materialValid&&preserved?0:1;
            }
        }
    }
}
