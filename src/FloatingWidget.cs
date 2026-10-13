using System;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal sealed class FloatingWidget : Form
    {
        internal const int MinimumWidth=144,MinimumHeight=88,MaximumWidth=560,MaximumHeight=360;
        internal const int EdgeLeft=1,EdgeRight=2,EdgeTop=4,EdgeBottom=8;
        Snapshot snapshot=new Snapshot();DeepSeekSnapshot deepseek=new DeepSeekSnapshot();
        ResetNotice announcement;bool announcementStale,alive,stopping,dragging,resizing,moved,pending,syncingGlass;
        string resetNotice="";int resizeEdges,transparency=60;Point mouseOrigin;Rectangle originalBounds;DateTime lastRaise;
        internal bool TextFits;internal float SmallestTextSize;
        internal readonly List<string> RenderedLines=new List<string>();
        internal int Transparency {get{return transparency;}}
        internal int TintAlpha {get{return (int)Math.Round(255*(100-transparency)/100.0);}}
        internal static int ClampTransparency(int percent){return Math.Max(0,Math.Min(90,percent));}
        internal void SetTransparency(int percent){transparency=ClampTransparency(percent);pending=true;if(IsHandleCreated)Present();}
        readonly Action open;readonly Action<Point> savePosition;readonly Action<Size> saveSize;
        readonly GlassBackdrop glass=new GlassBackdrop();
        readonly FrostedBackground frost=new FrostedBackground();
        readonly Timer renderTimer=new Timer{Interval=16};
        readonly ToolTip tooltip=new ToolTip{InitialDelay=400,ReshowDelay=200,AutoPopDelay=12000};
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref uint value,int size);
        internal int FramePolicyResult,BorderPolicyResult;internal bool LayeredRenderSucceeded;
        internal bool FrostedReady {get{return glass.Ready||frost.Image!=null;}}
        internal int MaterialResult {get{return glass.Ready?glass.MaterialResult:frost.Image!=null?0:glass.MaterialResult;}}
        internal string FrostedMode {get{return glass.Ready?"native":frost.Image!=null?"software":"alpha";}}
        internal bool PerMonitorAware {get{try{return IsHandleCreated&&AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(Handle),new IntPtr(-4));}catch(EntryPointNotFoundException){return false;}}}
        internal float RenderScale {get{try{return IsHandleCreated?Math.Max(1,GetDpiForWindow(Handle)/96F):InitialScale;}catch(EntryPointNotFoundException){return InitialScale;}}}
        static float InitialScale {get{using(var g=Graphics.FromHwnd(IntPtr.Zero))return Math.Max(1,g.DpiX/96F);}}
        internal Size LogicalSize {get{return new Size((int)Math.Round(Width/RenderScale),(int)Math.Round(Height/RenderScale));}}
        internal string LayoutMode {get{return Mode(LogicalSize);}}
        internal static string Mode(Size size){return size.Height>=176?"expanded":size.Width>=250?"wide":"compact";}
        public FloatingWidget(Action show,Action<Point> save):this(show,save,delegate(Size size){}){}
        public FloatingWidget(Action show,Action<Point> save,Action<Size> sizeChanged)
        {
            open=show;savePosition=save;saveSize=sizeChanged;
            Text="Codex 桌面浮标";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
            StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;BackColor=VisualTheme.Night;
            ClientSize=new Size((int)Math.Round(160*InitialScale),(int)Math.Round(92*InitialScale));
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
            VisualTheme.Track(this);VisualTheme.ThemeChanged+=OnThemeChanged;
            renderTimer.Tick+=delegate{if(pending||(!glass.Ready&&Visible&&frost.Due)){pending=false;Present();}};renderTimer.Start();
            MouseDown+=delegate(object sender,MouseEventArgs e)
            {
                if(e.Button!=MouseButtons.Left)return;
                resizeEdges=HitEdges(e.Location,ClientSize,Math.Max(6,(int)Math.Round(6*RenderScale)));
                resizing=resizeEdges!=0;dragging=!resizing;moved=false;mouseOrigin=PointToScreen(e.Location);originalBounds=Bounds;Capture=true;
            };
            MouseMove+=delegate(object sender,MouseEventArgs e)
            {
                if(!dragging&&!resizing){Cursor=EdgeCursor(HitEdges(e.Location,ClientSize,Math.Max(6,(int)Math.Round(6*RenderScale))));return;}
                Point pointer=PointToScreen(e.Location);var delta=new Point(pointer.X-mouseOrigin.X,pointer.Y-mouseOrigin.Y);
                if(Math.Abs(delta.X)>SystemInformation.DragSize.Width/2||Math.Abs(delta.Y)>SystemInformation.DragSize.Height/2)moved=true;
                Rectangle area=Screen.FromRectangle(originalBounds).WorkingArea;
                if(resizing){Bounds=ResizeBounds(originalBounds,delta,resizeEdges,area,RenderScale);pending=true;}
                else if(moved)Location=Clamp(new Point(originalBounds.X+delta.X,originalBounds.Y+delta.Y),Size,Screen.FromPoint(pointer).WorkingArea);
            };
            MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left&&(dragging||resizing))EndGesture(true);};
            MouseCaptureChanged+=delegate{if(!Capture&&(dragging||resizing))EndGesture(false);};
        }
        void EndGesture(bool allowOpen)
        {
            bool wasResize=resizing,wasMove=moved;dragging=resizing=false;Capture=false;Cursor=Cursors.Default;Present();
            if(wasResize){saveSize(LogicalSize);savePosition(Location);}else if(wasMove)savePosition(Location);else if(allowOpen)open();
        }
        internal static int HitEdges(Point p,Size size,int grip)
        {
            int result=0;if(p.X<grip)result|=EdgeLeft;else if(p.X>=size.Width-grip)result|=EdgeRight;
            if(p.Y<grip)result|=EdgeTop;else if(p.Y>=size.Height-grip)result|=EdgeBottom;return result;
        }
        static Cursor EdgeCursor(int edges)
        {
            if(edges==(EdgeLeft|EdgeTop)||edges==(EdgeRight|EdgeBottom))return Cursors.SizeNWSE;
            if(edges==(EdgeRight|EdgeTop)||edges==(EdgeLeft|EdgeBottom))return Cursors.SizeNESW;
            if((edges&(EdgeLeft|EdgeRight))!=0)return Cursors.SizeWE;
            if((edges&(EdgeTop|EdgeBottom))!=0)return Cursors.SizeNS;return Cursors.Default;
        }
        internal static Rectangle ResizeBounds(Rectangle start,Point delta,int edges,Rectangle area,float scale)
        {
            int minW=Math.Min(area.Width,(int)Math.Round(MinimumWidth*scale)),minH=Math.Min(area.Height,(int)Math.Round(MinimumHeight*scale));
            int maxW=Math.Min(area.Width,(int)Math.Round(MaximumWidth*scale)),maxH=Math.Min(area.Height,(int)Math.Round(MaximumHeight*scale));
            int left=start.Left,top=start.Top,right=start.Right,bottom=start.Bottom;
            if((edges&EdgeLeft)!=0)left=Math.Max(area.Left,Math.Max(right-maxW,Math.Min(right-minW,left+delta.X)));
            if((edges&EdgeRight)!=0)right=Math.Min(area.Right,Math.Min(left+maxW,Math.Max(left+minW,right+delta.X)));
            if((edges&EdgeTop)!=0)top=Math.Max(area.Top,Math.Max(bottom-maxH,Math.Min(bottom-minH,top+delta.Y)));
            if((edges&EdgeBottom)!=0)bottom=Math.Min(area.Bottom,Math.Min(top+maxH,Math.Max(top+minH,bottom+delta.Y)));
            return Rectangle.FromLTRB(left,top,right,bottom);
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000080|0x00080000;p.ClassStyle&=~0x00020000;p.Style&=~0x00C40000;return p;}}
        void RemoveSystemFrame()
        {
            uint disabled=1,noBorder=0xFFFFFFFE,ownCorners=1;
            FramePolicyResult=DwmSetWindowAttribute(Handle,2,ref disabled,4);BorderPolicyResult=DwmSetWindowAttribute(Handle,34,ref noBorder,4);DwmSetWindowAttribute(Handle,33,ref ownCorners,4);
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RemoveSystemFrame();glass.Attach(Handle);if(glass.Ready)Owner=glass;Present();}
        protected override void OnShown(EventArgs e){base.OnShown(e);RemoveSystemFrame();SyncGlass();Present();}
        protected override void OnLocationChanged(EventArgs e){base.OnLocationChanged(e);SyncGlass();}
        protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);pending=true;SyncGlass();}
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);SyncGlass();}
        void SyncGlass()
        {
            if(glass==null||glass.IsDisposed||syncingGlass)return;
            if(!glass.Ready){if(glass.Visible)glass.Hide();if(Owner==glass)Owner=null;return;}
            syncingGlass=true;try
            {
                glass.Match(Bounds);
                if(Owner!=glass)Owner=glass;
                if(Visible){if(!glass.Visible)glass.Show();WindowRecovery.SetWindowPos(glass.Handle,Handle,Left,Top,Width,Height,0x10|0x40);}
                else if(glass.Visible)glass.Hide();
            }finally{syncingGlass=false;}
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0021){m.Result=new IntPtr(3);return;}
            if(m.Msg==0x02E0)
            {
                var suggested=(NativeRect)Marshal.PtrToStructure(m.LParam,typeof(NativeRect));Bounds=Rectangle.FromLTRB(suggested.Left,suggested.Top,suggested.Right,suggested.Bottom);pending=true;
                m.Result=IntPtr.Zero;return;
            }
            base.WndProc(ref m);if(m.Msg==0x001A||m.Msg==0x031A||m.Msg==0x031E){RemoveSystemFrame();glass.Attach(Handle);SyncGlass();pending=true;}
        }
        internal static Point Clamp(Point p,Size size,Rectangle area){return new Point(Math.Max(area.Left,Math.Min(p.X,Math.Max(area.Left,area.Right-size.Width))),Math.Max(area.Top,Math.Min(p.Y,Math.Max(area.Top,area.Bottom-size.Height))));}
        internal void ApplyLogicalSize(Size size)
        {
            float scale=RenderScale;ClientSize=new Size((int)Math.Round(Math.Max(MinimumWidth,Math.Min(MaximumWidth,size.Width))*scale),(int)Math.Round(Math.Max(MinimumHeight,Math.Min(MaximumHeight,size.Height))*scale));
            if(IsHandleCreated){Location=Clamp(Location,Size,Screen.FromRectangle(Bounds).WorkingArea);Present();}pending=true;
        }
        public void Place(WidgetSettings settings)
        {
            SetTransparency(settings.WidgetTransparency);
            ApplyLogicalSize(new Size(settings.WidgetWidth,settings.WidgetHeight));
            Rectangle area=settings.X==int.MinValue?Screen.PrimaryScreen.WorkingArea:Screen.FromPoint(new Point(settings.X,settings.Y)).WorkingArea;
            Location=Clamp(settings.X==int.MinValue?new Point(area.Right-Width-24,area.Bottom-Height-24):new Point(settings.X,settings.Y),Size,area);
        }
        public void UpdateSnapshot(Snapshot s,bool running,bool paused)
        {
            snapshot=s;alive=running;stopping=paused;
            tooltip.SetToolTip(this,s.Tooltip(running,paused)+"\n"+s.ResetTooltip+"\n"+resetNotice+"\n"+deepseek.ShortText+"\n今日本机 DSH token："+Snapshot.Compact(deepseek.A)+"\n点击查看详情 · 拖中间移动 · 拖边缘/右下角缩放");pending=true;
        }
        public void UpdateDeepSeek(DeepSeekSnapshot value){deepseek=value;pending=true;}
        public void UpdateResetNotice(ResetMonitor monitor){resetNotice=monitor.Tooltip;announcement=monitor.Active;announcementStale=monitor.Cache.Stale;pending=true;}
        public void EnsurePresentation(bool desired){string reason=WindowRecovery.Ensure(this,desired,dragging||resizing,ref lastRaise);if(reason.Length>0&&reason!="cloaked")BackgroundLifetime.Log("widget_recovered reason="+reason);SyncGlass();}
        internal string TimingSummary
        {
            get
            {
                DateTimeOffset time;string prefix;
                if(announcement==null)
                {
                    if(snapshot.PrimaryReset==DateTimeOffset.MinValue)return "重置待获取";
                    if(snapshot.PrimaryReset<=DateTimeOffset.Now)return "重置待确认";
                    time=snapshot.PrimaryReset;prefix="重置 ";
                }
                else
                {
                    var exact=announcement.OfficialTime;
                    if(exact.HasValue){time=exact.Value;prefix=announcement.ShortTiming.StartsWith("最晚 ")?"最晚 ":"预告 ";}
                    else if(DateTimeOffset.TryParse(announcement.Estimate,out time))prefix="预计 ";
                    else return announcementStale?"旧预告待确认":"预告待确认";
                    if(announcementStale)prefix=exact.HasValue&&announcement.ShortTiming.StartsWith("最晚 ")?"旧预最晚 ":"旧预 ";
                }
                return prefix+time.ToOffset(TimeSpan.FromHours(8)).ToString("M/d H:mm")+" 北京";
            }
        }
        string BalanceText
        {
            get
            {
                if(!deepseek.Balance.HasValue)return "未知";
                decimal amount=deepseek.Balance.Value,magnitude=Math.Abs(amount);string currency=deepseek.Currency=="CNY"?"¥":"$";
                if(magnitude<10000)return currency+amount.ToString("0.00");
                if(magnitude<1000000)return currency+(amount/1000).ToString("0.#")+"K";
                if(magnitude<1000000000)return currency+(amount/1000000).ToString("0.#")+"M";
                if(magnitude<1000000000000)return currency+(amount/1000000000).ToString("0.#")+"G";
                return currency+amount.ToString("0.#E+0");
            }
        }
        protected override void OnPaint(PaintEventArgs e){PaintContent(e.Graphics);}
        internal void PaintContent(Graphics g)
        {
            float scale=RenderScale,w=Width/scale,h=Height/scale,p=12;
            TextFits=true;SmallestTextSize=float.MaxValue;RenderedLines.Clear();
            g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var panel=new Rectangle(0,0,Width-1,Height-1);
            using(var path=VisualTheme.Round(panel,(int)Math.Round(8*scale)))
            using(var fill=new LinearGradientBrush(panel,Color.FromArgb(TintAlpha,VisualTheme.Mix(VisualTheme.Night,VisualTheme.WidgetText,.045F)),Color.FromArgb(TintAlpha,VisualTheme.Night),LinearGradientMode.Vertical))
            {
                if(!glass.Ready&&frost.Image!=null)
                {var state=g.Save();try{g.SetClip(path,CombineMode.Intersect);frost.Draw(g,Bounds);}finally{g.Restore(state);}}
                g.FillPath(fill,path);
            }
            Color muted=VisualTheme.WidgetMuted;bool fresh=alive&&!stopping&&!snapshot.Stale&&!snapshot.ReadFailed&&!snapshot.QuotaStale&&snapshot.Remaining.HasValue;
            string value=snapshot.Remaining.HasValue?snapshot.Remaining.Value.ToString("0.#")+"%":"—";
            bool tall=h>=176,wide=w>=250;float split=wide?w*.53F:w-70;
            string status=stopping||!alive?"·停":snapshot.QuotaStale||snapshot.Stale||snapshot.ReadFailed?"·旧":!snapshot.Remaining.HasValue?"·待":"";
            TextLine(g,"CODEX"+status,11,muted,p,7,wide?split-p-16:w-p*2,17,false,false);
            using(var state=new SolidBrush(fresh?VisualTheme.Mint:VisualTheme.Warning))g.FillEllipse(state,(wide?split-10:w-16)*scale,13*scale,4*scale,4*scale);
            float numberHeight=tall?Math.Min(75,h*.3F):Math.Min(42,h-59),numberSize=tall?Math.Min(60,42+(h-176)*.15F):Math.Min(34,h*.33F);
            TextLine(g,value,numberSize,VisualTheme.WidgetText,p-1,25,wide?split-p-16:split-p+3,numberHeight,false,true);
            float barY=tall?35+numberHeight:h-31;
            if(wide)
            {
                TextLine(g,deepseek.Stale?"DSH·旧":"DSH",11,muted,split+12,7,w-split-p-12,17,false,false);
                TextLine(g,BalanceText,tall?Math.Min(34,27+(h-176)*.05F):23,deepseek.Stale?muted:VisualTheme.Mint,split+10,tall?29:30,w-split-p-10,numberHeight,false,true);
            }
            else if(tall)
            {
                TextLine(g,deepseek.Stale?"DSH·旧":"DSH",11,muted,p,barY+14,w-p*2,17,false,false);
                TextLine(g,BalanceText,20,deepseek.Stale?muted:VisualTheme.Mint,p,barY+32,w-p*2,27,false,true);
            }
            else
            {
                TextLine(g,deepseek.Stale?"DSH·旧":"DSH",10.5F,muted,split,7,w-split-p-9,17,true,false);
                TextLine(g,BalanceText,14,deepseek.Stale?muted:VisualTheme.Mint,split,30,w-split-p,numberHeight-5,true,true);
            }
            float contentW=w-p*2,trackHeight=3*scale;
            using(var path=VisualTheme.Round(new Rectangle((int)(p*scale),(int)(barY*scale),(int)(contentW*scale),(int)trackHeight),(int)Math.Ceiling(trackHeight/2)))
            using(var track=new SolidBrush(Color.FromArgb(72,VisualTheme.Mint)))g.FillPath(track,path);
            float remaining=snapshot.Remaining.HasValue?(float)Math.Max(0,Math.Min(100,snapshot.Remaining.Value)):0;
            int progressWidth=(int)(contentW*remaining/100*scale);
            if(progressWidth>0)using(var path=VisualTheme.Round(new Rectangle((int)(p*scale),(int)(barY*scale),progressWidth,(int)trackHeight),(int)Math.Ceiling(trackHeight/2)))
            using(var progress=new SolidBrush(fresh?VisualTheme.Mint:VisualTheme.Warning))g.FillPath(progress,path);
            if(tall&&wide)
            {
                TextLine(g,"Codex 累计 token",11,muted,p,barY+15,w*.52F-p,18,false,false);
                TextLine(g,Snapshot.Compact(snapshot.A),h>=270?22:17,VisualTheme.WidgetText,p,barY+37,w*.52F-p,29,false,true);
                TextLine(g,"DSH 今日 token",11,muted,split+12,barY+15,w-split-p-12,18,false,false);
                TextLine(g,Snapshot.Compact(deepseek.A),h>=270?22:17,VisualTheme.WidgetText,split+12,barY+37,w-split-p-12,29,false,true);
            }
            else if(tall&&h>=212)
            {
                TextLine(g,"Codex 累计  "+Snapshot.Compact(snapshot.A),11,muted,p,barY+65,w-p*2,20,false,false);
                if(h>=260)TextLine(g,"DSH 今日  "+Snapshot.Compact(deepseek.A),11,muted,p,barY+91,w-p*2,20,false,false);
            }
            if(wide&&!tall)TextLine(g,"本机 "+Snapshot.Compact(snapshot.A),11,muted,w*.65F,h-23,w*.35F-p,17,true,false);
            float timeWidth=wide&&!tall?w*.64F-p:contentW;
            TextLine(g,TimingSummary,11,announcement!=null&&!announcementStale?VisualTheme.Mint:muted,p,h-23,timeWidth,17,false,false);
        }
        void TextLine(Graphics g,string text,float size,Color color,float x,float y,float width,float height,bool right,bool numeric)
        {
            float scale=RenderScale;var bounds=new RectangleF((float)Math.Round(x*scale),(float)Math.Round(y*scale),(float)Math.Round(width*scale),(float)Math.Round(height*scale));
            float pixels=(float)Math.Round(numeric?Math.Min(size*scale,bounds.Height*.78F):size*scale);Font font=null;
            RenderedLines.Add(text);
            try
            {
                using(var brush=new SolidBrush(color))using(var format=new StringFormat(StringFormat.GenericTypographic))
                {
                    format.FormatFlags=StringFormatFlags.NoWrap;format.Trimming=StringTrimming.None;format.Alignment=right?StringAlignment.Far:StringAlignment.Near;format.LineAlignment=StringAlignment.Center;
                    string family=numeric?"Segoe UI Semibold":"Microsoft YaHei UI";
                    font=new Font(family,pixels,FontStyle.Regular,GraphicsUnit.Pixel);
                    float measured=g.MeasureString(text,font,5000,format).Width;
                    if(measured>bounds.Width-1)
                    {font.Dispose();pixels=Math.Max(1,pixels*(bounds.Width-1)/(measured+1));font=new Font(family,pixels,FontStyle.Regular,GraphicsUnit.Pixel);}
                    TextFits&=g.MeasureString(text,font,5000,format).Width<=bounds.Width+.25F&&text.IndexOf('…')<0&&text.IndexOf("...",StringComparison.Ordinal)<0;
                    SmallestTextSize=Math.Min(SmallestTextSize,pixels/scale);g.DrawString(text,font,brush,bounds,format);
                }
            }finally{if(font!=null)font.Dispose();}
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
        [StructLayout(LayoutKind.Sequential)] struct NativeSize {public int Width,Height;public NativeSize(int w,int h){Width=w;Height=h;}}
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend {public byte Operation,Flags,Alpha,Format;}
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr bitmap);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr dc,ref NativePoint destination,ref NativeSize size,IntPtr sourceDC,ref NativePoint source,uint key,ref Blend blend,uint flags);
        void Present()
        {
            if(!IsHandleCreated||Width<=0||Height<=0)return;
            if(!glass.Ready&&Visible)frost.Refresh(this);
            using(var bitmap=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using(var g=Graphics.FromImage(bitmap))PaintContent(g);
                IntPtr dc=GetDC(IntPtr.Zero),memory=CreateCompatibleDC(dc),native=bitmap.GetHbitmap(Color.FromArgb(0)),previous=SelectObject(memory,native);
                try{var destination=new NativePoint(Left,Top);var source=new NativePoint(0,0);var size=new NativeSize(Width,Height);var blend=new Blend{Alpha=255,Format=1};LayeredRenderSucceeded=UpdateLayeredWindow(Handle,dc,ref destination,ref size,memory,ref source,0,ref blend,2);}
                finally{SelectObject(memory,previous);DeleteObject(native);DeleteDC(memory);ReleaseDC(IntPtr.Zero,dc);}
            }
            SyncGlass();
        }
        void OnThemeChanged(object sender,EventArgs e){pending=true;if(IsHandleCreated)Present();}
        protected override void Dispose(bool disposing){if(disposing){VisualTheme.ThemeChanged-=OnThemeChanged;renderTimer.Stop();renderTimer.Dispose();tooltip.Dispose();frost.Dispose();Owner=null;glass.Dispose();}base.Dispose(disposing);}
    }
    internal sealed class FrostedBackground : IDisposable
    {
        [DllImport("user32.dll")]static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
        [DllImport("user32.dll")]static extern bool GetWindowDisplayAffinity(IntPtr hwnd,out uint affinity);
        Bitmap image;Rectangle captured,windowBounds;DateTime updated=DateTime.MinValue;bool capturing;
        internal Bitmap Image {get{return image;}}
        internal bool Due {get{return (DateTime.UtcNow-updated).TotalMilliseconds>=250;}}
        internal void Refresh(Form window)
        {
            if(capturing||!window.Visible||Environment.OSVersion.Version.Build<19041||(!Due&&windowBounds==window.Bounds))return;
            updated=DateTime.UtcNow;capturing=true;uint previous=0;bool excluded=false;
            try
            {
                GetWindowDisplayAffinity(window.Handle,out previous);
                excluded=SetWindowDisplayAffinity(window.Handle,0x11);if(!excluded)return;
                Rectangle region=window.Bounds;region.Inflate(32,32);region=Rectangle.Intersect(region,SystemInformation.VirtualScreen);
                using(var raw=new Bitmap(region.Width,region.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using(var graphics=Graphics.FromImage(raw))graphics.CopyFromScreen(region.Location,Point.Empty,region.Size);
                    // Restore capture visibility before processing. Only this window's local
                    // background is sampled; pixels stay in memory and are never saved.
                    excluded=!SetWindowDisplayAffinity(window.Handle,previous);
                    var blurred=new Bitmap(Math.Max(1,region.Width/3),Math.Max(1,region.Height/3),System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using(var graphics=Graphics.FromImage(blurred)){graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;graphics.DrawImage(raw,new Rectangle(0,0,blurred.Width,blurred.Height));}
                    Blur(blurred,3);
                    var old=image;image=blurred;captured=region;windowBounds=window.Bounds;if(old!=null)old.Dispose();
                }
            }
            catch(System.ComponentModel.Win32Exception){}catch(ExternalException){}
            finally{if(excluded)SetWindowDisplayAffinity(window.Handle,previous);capturing=false;}
        }
        internal void Draw(Graphics graphics,Rectangle bounds)
        {
            float sx=image.Width/(float)captured.Width,sy=image.Height/(float)captured.Height;
            var source=new RectangleF((bounds.Left-captured.Left)*sx,(bounds.Top-captured.Top)*sy,bounds.Width*sx,bounds.Height*sy);
            graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;graphics.DrawImage(image,new RectangleF(0,0,bounds.Width,bounds.Height),source,GraphicsUnit.Pixel);
        }
        static void Blur(Bitmap bitmap,int radius)
        {
            var rect=new Rectangle(0,0,bitmap.Width,bitmap.Height);var data=bitmap.LockBits(rect,System.Drawing.Imaging.ImageLockMode.ReadWrite,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int width=bitmap.Width,height=bitmap.Height,stride=data.Stride;byte[] source=new byte[stride*height],target=new byte[source.Length];Marshal.Copy(data.Scan0,source,0,source.Length);
                for(int pass=0;pass<3;pass++)
                {
                    for(int y=0;y<height;y++)for(int c=0;c<3;c++)
                    {int sum=0;for(int x=-radius;x<=radius;x++)sum+=source[y*stride+Math.Max(0,Math.Min(width-1,x))*4+c];for(int x=0;x<width;x++){target[y*stride+x*4+c]=(byte)(sum/(radius*2+1));sum-=source[y*stride+Math.Max(0,x-radius)*4+c];sum+=source[y*stride+Math.Min(width-1,x+radius+1)*4+c];}}
                    for(int x=0;x<width;x++)for(int c=0;c<3;c++)
                    {int sum=0;for(int y=-radius;y<=radius;y++)sum+=target[Math.Max(0,Math.Min(height-1,y))*stride+x*4+c];for(int y=0;y<height;y++){source[y*stride+x*4+c]=(byte)(sum/(radius*2+1));sum-=target[Math.Max(0,y-radius)*stride+x*4+c];sum+=target[Math.Min(height-1,y+radius+1)*stride+x*4+c];}}
                }
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)source[y*stride+x*4+3]=255;Marshal.Copy(source,0,data.Scan0,source.Length);
            }finally{bitmap.UnlockBits(data);}
        }
        public void Dispose(){if(image!=null){image.Dispose();image=null;}}
    }
}
