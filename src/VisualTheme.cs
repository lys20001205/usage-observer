using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CodexUsageTray
{
    internal static class VisualTheme
    {
        internal static Color Ink=Color.FromArgb(31,48,42), Muted=Color.FromArgb(111,125,118), Accent=Color.FromArgb(35,116,87), Canvas=Color.FromArgb(245,247,243), Night=Color.FromArgb(22,43,36), Mint=Color.FromArgb(145,227,185), Border=Color.FromArgb(226,233,225), Warning=Color.FromArgb(230,181,103), Soft=Color.FromArgb(234,241,233), Surface=Color.White,WidgetText=Color.White,WidgetMuted=Color.FromArgb(197,214,202);
        static ThemeSettings current=new ThemeSettings();
        internal static ThemeSettings CurrentTheme {get{return current.Copy();}}
        internal static event EventHandler ThemeChanged;
        sealed class Binding {public Binding(){}internal string Back,Fore,Fill;}
        static readonly ConditionalWeakTable<Control,Binding> bindings=new ConditionalWeakTable<Control,Binding>();
        static readonly List<WeakReference> roots=new List<WeakReference>();
        internal static void Track(Control control)
        {
            while(control.Parent!=null)control=control.Parent;if(!(control is Form))return;
            for(int i=roots.Count-1;i>=0;i--){var existing=roots[i].Target as Control;if(existing==null||existing.IsDisposed)roots.RemoveAt(i);else if(existing==control)return;}
            roots.Add(new WeakReference(control));
        }
        internal static Color Mix(Color first,Color second,float amount){return Color.FromArgb((int)(first.R+(second.R-first.R)*amount),(int)(first.G+(second.G-first.G)*amount),(int)(first.B+(second.B-first.B)*amount));}
        static double Linear(byte value){double x=value/255.0;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        static double Luma(Color color){return .2126*Linear(color.R)+.7152*Linear(color.G)+.0722*Linear(color.B);}
        internal static double Contrast(Color first,Color second){double a=Luma(first),b=Luma(second);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);}
        internal static Color Readable(Color desired,Color background)
        {
            if(Contrast(desired,background)>=4.5)return desired;
            Color target=Contrast(Color.White,background)>Contrast(Color.Black,background)?Color.White:Color.Black;
            for(float n=.1F;n<=1;n+=.1F){Color candidate=Mix(desired,target,Math.Min(1,n));if(Contrast(candidate,background)>=4.5)return candidate;}return target;
        }
        static Color Token(string role){return (Color)typeof(VisualTheme).GetField(role,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);}
        static string Role(Color color,bool foreground,Control control)
        {
            if(color.A==0)return null;
            if(foreground)
            {
                for(Control p=control.Parent;p!=null;p=p.Parent){var card=p as SurfaceCard;if(card!=null&&card.Hero){if(color.ToArgb()==WidgetText.ToArgb())return "WidgetText";if(color.ToArgb()==Color.FromArgb(183,207,188).ToArgb())return "WidgetMuted";break;}}
                foreach(string role in new[]{"Ink","Muted","Accent","Mint","Warning","WidgetMuted","WidgetText"})if(color.ToArgb()==Token(role).ToArgb())return role;
                if(color.ToArgb()==Color.FromArgb(30,120,86).ToArgb())return "Accent";
                if(color.ToArgb()==Color.FromArgb(154,97,20).ToArgb())return "Warning";
            }
            else foreach(string role in new[]{"Canvas","Surface","Night","Soft","Border"})if(color.ToArgb()==Token(role).ToArgb())return role;
            return null;
        }
        static Color Background(Control control)
        {
            if(control is TextBox||control is NumericUpDown||control is ComboBox)return Surface;
            for(Control p=control.Parent;p!=null;p=p.Parent){var card=p as SurfaceCard;if(card!=null)return card.Fill;if(p.BackColor.A==255)return p.BackColor;}
            return Canvas;
        }
        static Color Foreground(string role,Control control)
        {
            Color color=Token(role),background=Background(control);
            if(role=="Ink"&&Contrast(color,background)<7)return Contrast(Color.White,background)>Contrast(Color.Black,background)?Color.White:Color.Black;
            return role=="WidgetText"||role=="WidgetMuted"||role=="Mint"?color:Readable(color,background);
        }
        internal static void BindForeground(Control control,string role){bindings.GetOrCreateValue(control).Fore=role;control.ForeColor=Foreground(role,control);}
        static void Capture(Control control)
        {
            var binding=bindings.GetOrCreateValue(control);
            if(binding.Back==null)binding.Back=control is Form||control is TabPage?"Canvas":Role(control.BackColor,false,control);
            if(binding.Fore==null)binding.Fore=Role(control.ForeColor,true,control);
            var card=control as SurfaceCard;if(card!=null)binding.Fill=card.Hero?"Night":"Surface";
            foreach(Control child in control.Controls)Capture(child);
        }
        static void Recolor(Control control)
        {
            var binding=bindings.GetOrCreateValue(control);var card=control as SurfaceCard;
            if(card!=null&&binding.Fill!=null)card.Fill=Token(binding.Fill);
            if(binding.Back!=null)control.BackColor=Token(binding.Back);
            if(control is SurfaceButton||control is SurfaceToggle||control is SurfaceSlider)control.BackColor=Background(control);
            if(binding.Fore!=null)control.ForeColor=Foreground(binding.Fore,control);
            var grid=control as DataGridView;if(grid!=null)DecorateGrid(grid);
            foreach(Control child in control.Controls)Recolor(child);control.Invalidate(true);
        }
        internal static void ApplyTheme(ThemeSettings settings)
        {
            foreach(string key in ThemeSettings.Keys)ThemeSettings.Parse(settings.Get(key));
            var alive=new List<Control>();for(int i=roots.Count-1;i>=0;i--){var root=roots[i].Target as Control;if(root==null||root.IsDisposed)roots.RemoveAt(i);else{Capture(root);alive.Add(root);}}
            current=settings.Copy();Accent=ThemeSettings.Parse(settings.Accent);Canvas=ThemeSettings.Parse(settings.Canvas);Surface=ThemeSettings.Parse(settings.Surface);Night=ThemeSettings.Parse(settings.WidgetBackground);WidgetText=ThemeSettings.Parse(settings.WidgetText);Mint=ThemeSettings.Parse(settings.WidgetAccent);
            Color baseInk=Color.FromArgb(31,48,42);Ink=Contrast(baseInk,Canvas)>=7?baseInk:Contrast(Color.White,Canvas)>Contrast(Color.Black,Canvas)?Color.White:Color.Black;
            Muted=Readable(Mix(Ink,Canvas,.42F),Canvas);Border=Mix(Canvas,Ink,.12F);Soft=Mix(Canvas,Accent,.07F);WidgetMuted=Mix(WidgetText,Night,.22F);
            foreach(var root in alive)Recolor(root);if(ThemeChanged!=null)ThemeChanged(null,EventArgs.Empty);
        }
        internal static GraphicsPath Round(Rectangle r,int radius)
        {
            var p=new GraphicsPath();int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
            if(d<=0){p.AddRectangle(r);return p;}
            p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        internal static float Scale(Control control){using(var g=control.CreateGraphics())return g.DpiX/96F;}
        internal static Label Text(Control parent,string text,int x,int y,int w,int h,float size,Color color,bool bold=false)
        {
            var label=new Label{Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular),Text=text,ForeColor=color,BackColor=Color.Transparent,UseCompatibleTextRendering=false,Bounds=new Rectangle(x,y,w,h)};parent.Controls.Add(label);string role=Role(color,true,label);if(role!=null)BindForeground(label,role);Track(parent);return label;
        }
        internal static Label Number(Control parent,string text,int x,int y,int w,int h,float size,Color color)
        {
            // Set the final font before parenting, preventing a second DPI size adjustment.
            var label=new Label{Font=new Font("Segoe UI",size,FontStyle.Regular),Text=text,ForeColor=color,BackColor=Color.Transparent,UseCompatibleTextRendering=false,Bounds=new Rectangle(x,y,w,h)};parent.Controls.Add(label);string role=Role(color,true,label);if(role!=null)BindForeground(label,role);Track(parent);return label;
        }
        internal static Panel Card(Control parent,int x,int y,int w,int h,Color fill,bool hero=false)
        {
            var card=new SurfaceCard{Bounds=new Rectangle(x,y,w,h),Fill=fill,Hero=hero,BackColor=parent.BackColor};parent.Controls.Add(card);Track(parent);return card;
        }
        internal static Button Button(Control parent,string text,int x,int y,int w,Action action,bool primary=false)
        {
            var button=new SurfaceButton{Text=text,Bounds=new Rectangle(x,y,w,34),Primary=primary,BackColor=parent is SurfaceCard?((SurfaceCard)parent).Fill:parent.BackColor,Font=new Font("Microsoft YaHei UI",9F)};
            button.Click+=delegate{action();};parent.Controls.Add(button);Track(parent);return button;
        }
        internal static void Decorate(Control parent)
        {
            Track(parent);
            if(parent is Form||parent is TabPage){parent.BackColor=Canvas;parent.ForeColor=Ink;}
            foreach(Control c in parent.Controls)
            {
                if(c is Label)BindForeground(c,bindings.GetOrCreateValue(c).Fore??Role(c.ForeColor,true,c)??"Ink");
                if(c is TextBox||c is NumericUpDown||c is ComboBox){c.BackColor=Surface;c.ForeColor=Readable(Ink,Surface);var binding=bindings.GetOrCreateValue(c);binding.Back="Surface";binding.Fore="Ink";}
                var text=c as TextBox;if(text!=null&&!text.ReadOnly)text.BorderStyle=BorderStyle.FixedSingle;
                var grid=c as DataGridView;
                if(grid!=null)
                {
                    grid.BackgroundColor=Surface;grid.BorderStyle=BorderStyle.None;grid.GridColor=Border;
                    grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;
                    grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;grid.ColumnHeadersHeight=48;
                    grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Soft,ForeColor=Muted,Font=new Font("Microsoft YaHei UI",8.5F),Padding=new Padding(8),WrapMode=DataGridViewTriState.True,SelectionBackColor=Soft,SelectionForeColor=Muted};
                    grid.DefaultCellStyle.BackColor=Surface;grid.DefaultCellStyle.ForeColor=Ink;grid.DefaultCellStyle.Padding=new Padding(8,10,8,10);grid.DefaultCellStyle.Font=new Font("Microsoft YaHei UI",9F);
                    grid.DefaultCellStyle.SelectionBackColor=Soft;grid.DefaultCellStyle.SelectionForeColor=Ink;DecorateGrid(grid);
                }
                var tabs=c as TabControl;
                if(tabs!=null)
                {
                    tabs.DrawMode=TabDrawMode.OwnerDrawFixed;tabs.ItemSize=new Size(148,40);tabs.SizeMode=TabSizeMode.Fixed;tabs.Padding=new Point(12,8);
                    tabs.DrawItem+=delegate(object sender,DrawItemEventArgs e)
                    {
                        bool selected=tabs.SelectedIndex==e.Index;using(var brush=new SolidBrush(Canvas))e.Graphics.FillRectangle(brush,e.Bounds);
                        var box=e.Bounds;box.Inflate(-5,-4);using(var path=Round(box,8))using(var brush=new SolidBrush(selected?Soft:Canvas))e.Graphics.FillPath(brush,path);
                        TextRenderer.DrawText(e.Graphics,tabs.TabPages[e.Index].Text,tabs.Font,box,selected?Accent:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                    };
                }
                Decorate(c);
            }
        }
        static void DecorateGrid(DataGridView grid)
        {
            grid.BackgroundColor=Surface;grid.GridColor=Border;grid.ColumnHeadersDefaultCellStyle.BackColor=Soft;grid.ColumnHeadersDefaultCellStyle.ForeColor=Readable(Muted,Soft);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor=Soft;grid.ColumnHeadersDefaultCellStyle.SelectionForeColor=Readable(Muted,Soft);
            grid.DefaultCellStyle.BackColor=Surface;grid.DefaultCellStyle.ForeColor=Readable(Ink,Surface);grid.DefaultCellStyle.SelectionBackColor=Soft;grid.DefaultCellStyle.SelectionForeColor=Readable(Ink,Soft);grid.AlternatingRowsDefaultCellStyle.BackColor=Mix(Surface,Canvas,.4F);
        }
    }
    internal sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){using(var brush=new SolidBrush(VisualTheme.Surface))e.Graphics.FillRectangle(brush,e.AffectedBounds);}
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e){using(var brush=new SolidBrush(e.Item.Selected?VisualTheme.Soft:VisualTheme.Surface))e.Graphics.FillRectangle(brush,new Rectangle(Point.Empty,e.Item.Size));}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){e.TextColor=VisualTheme.Readable(e.Item.Enabled?VisualTheme.Ink:VisualTheme.Muted,e.Item.Selected?VisualTheme.Soft:VisualTheme.Surface);base.OnRenderItemText(e);}
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e){e.ArrowColor=VisualTheme.Readable(VisualTheme.Ink,VisualTheme.Surface);base.OnRenderArrow(e);}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){using(var pen=new Pen(VisualTheme.Border))e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);}
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e){using(var pen=new Pen(VisualTheme.Border))e.Graphics.DrawLine(pen,8,e.Item.Height/2,e.Item.Width-8,e.Item.Height/2);}
    }
    internal sealed class SurfaceCard : Panel
    {
        public Color Fill=Color.White;public bool Hero;
        public SurfaceCard(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int radius=(int)(16*e.Graphics.DpiX/96F);
            using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),radius))
            {
                if(Hero){using(var brush=new LinearGradientBrush(ClientRectangle,VisualTheme.Mix(VisualTheme.Night,VisualTheme.Mint,.12F),VisualTheme.Night,35F))e.Graphics.FillPath(brush,path);}
                else {using(var brush=new SolidBrush(Fill))e.Graphics.FillPath(brush,path);using(var pen=new Pen(VisualTheme.Border))e.Graphics.DrawPath(pen,path);}
            }
        }
    }
    internal sealed class SurfaceBadge : Label
    {
        public SurfaceBadge(){BackColor=Color.Transparent;TextAlign=ContentAlignment.MiddleCenter;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),Height/2))using(var brush=new SolidBrush(VisualTheme.Soft))e.Graphics.FillPath(brush,path);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
        }
    }
    internal sealed class SurfaceButton : Button
    {
        public bool Primary,Quiet;bool hover,pressed;
        public SurfaceButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;bool dark=BackColor.GetBrightness()<.3F;
            Color fill=Primary?(pressed?VisualTheme.Mix(VisualTheme.Accent,Color.Black,.25F):hover?VisualTheme.Mix(VisualTheme.Accent,Color.Black,.12F):VisualTheme.Accent):dark?VisualTheme.Mix(BackColor,VisualTheme.WidgetText,.12F):hover?VisualTheme.Soft:VisualTheme.Surface;
            if(Quiet&&!hover&&!pressed)fill=BackColor;
            if(!Enabled)fill=VisualTheme.Soft;
            using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),(int)(9*e.Graphics.DpiX/96F)))
            {using(var brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);if(!Primary&&!dark&&!Quiet)using(var pen=new Pen(hover?Color.FromArgb(186,210,188):VisualTheme.Border))e.Graphics.DrawPath(pen,path);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,VisualTheme.Readable(!Enabled?VisualTheme.Muted:Primary?Color.White:dark?VisualTheme.Mint:VisualTheme.Ink,fill),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(4,4,Width-8,Height-8));
        }
    }
    internal sealed class SurfaceToggle : CheckBox
    {
        public SurfaceToggle(){AutoSize=false;Cursor=Cursors.Hand;UseVisualStyleBackColor=false;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.Opaque,true);SetStyle(ControlStyles.OptimizedDoubleBuffer,false);BackColor=VisualTheme.Canvas;}
        protected override void OnParentChanged(EventArgs e){base.OnParentChanged(e);BackColor=Parent is SurfaceCard?((SurfaceCard)Parent).Fill:Parent==null?VisualTheme.Canvas:Parent.BackColor;}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // A transparent native CheckBox can replay sibling content during live redraw.
            // Paint this control's local surface instead of asking Windows to copy its parent.
            Color fill=Parent is SurfaceCard?((SurfaceCard)Parent).Fill:BackColor;
            using(var brush=new SolidBrush(fill))e.Graphics.FillRectangle(brush,ClientRectangle);
        }
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            OnPaintBackground(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;float scale=e.Graphics.DpiX/96F;int w=(int)(34*scale),h=(int)(20*scale),margin=(int)(3*scale);var track=new Rectangle(Width-w-1,(Height-h)/2,w,h);
            using(var path=VisualTheme.Round(track,h/2))using(var brush=new SolidBrush(!Enabled?VisualTheme.Border:Checked?VisualTheme.Accent:Color.FromArgb(198,209,197)))e.Graphics.FillPath(brush,path);
            int diameter=h-margin*2;using(var brush=new SolidBrush(Color.White))e.Graphics.FillEllipse(brush,Checked?track.Right-margin-diameter:track.Left+margin,track.Top+margin,diameter,diameter);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,0,Width-w-(int)(12*scale),Height),Enabled?ForeColor:VisualTheme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.PreserveGraphicsClipping|TextFormatFlags.PreserveGraphicsTranslateTransform);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(1,1,Width-3,Height-3));
        }
    }
    internal sealed class SurfaceSlider : Control
    {
        int value=60;bool dragging;public int Minimum=0,Maximum=90,SmallChange=1,LargeChange=10;
        public event EventHandler ValueChanged;
        public int Value {get{return value;}set{int clamped=Math.Max(Minimum,Math.Min(Maximum,value));if(this.value==clamped)return;this.value=clamped;Invalidate();AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);if(ValueChanged!=null)ValueChanged(this,EventArgs.Empty);}}
        public SurfaceSlider(){TabStop=true;AccessibleRole=AccessibleRole.Slider;AccessibleName="浮窗透明度";Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.Selectable|ControlStyles.Opaque,true);SetStyle(ControlStyles.OptimizedDoubleBuffer,false);}
        protected override bool IsInputKey(Keys keyData){return keyData==Keys.Left||keyData==Keys.Right||keyData==Keys.Up||keyData==Keys.Down||keyData==Keys.Home||keyData==Keys.End||keyData==Keys.PageUp||keyData==Keys.PageDown||base.IsInputKey(keyData);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Down)Value-=SmallChange;else if(e.KeyCode==Keys.Right||e.KeyCode==Keys.Up)Value+=SmallChange;else if(e.KeyCode==Keys.PageDown)Value-=LargeChange;else if(e.KeyCode==Keys.PageUp)Value+=LargeChange;else if(e.KeyCode==Keys.Home)Value=Minimum;else if(e.KeyCode==Keys.End)Value=Maximum;else return;e.Handled=true;}
        void SetFromX(int x){int inset=(int)(10*VisualTheme.Scale(this));Value=Minimum+(int)Math.Round((Maximum-Minimum)*(x-inset)/(double)Math.Max(1,Width-inset*2));}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){Focus();dragging=true;Capture=true;SetFromX(e.X);}}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)SetFromX(e.X);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left){dragging=false;Capture=false;}}
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
        protected override AccessibleObject CreateAccessibilityInstance(){return new SliderAccessibility(this);}
        sealed class SliderAccessibility : ControlAccessibleObject
        {
            readonly SurfaceSlider owner;public SliderAccessibility(SurfaceSlider slider):base(slider){owner=slider;}
            public override string Value {get{return owner.Value+"%";}set{int parsed;if(int.TryParse(value.TrimEnd('%'),out parsed))owner.Value=parsed;}}
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;float scale=e.Graphics.DpiX/96F;int inset=(int)(10*scale),thickness=Math.Max(4,(int)(4*scale));int x=inset+(int)((Width-2*inset)*(Value-Minimum)/(float)Math.Max(1,Maximum-Minimum));
            using(var pen=new Pen(VisualTheme.Border,thickness)){pen.StartCap=pen.EndCap=LineCap.Round;e.Graphics.DrawLine(pen,inset,Height/2,Width-inset,Height/2);}
            if(x>inset)using(var pen=new Pen(VisualTheme.Accent,thickness)){pen.StartCap=pen.EndCap=LineCap.Round;e.Graphics.DrawLine(pen,inset,Height/2,x,Height/2);}
            using(var brush=new SolidBrush(VisualTheme.Accent))e.Graphics.FillEllipse(brush,x-7*scale,Height/2-7*scale,14*scale,14*scale);
            using(var brush=new SolidBrush(Color.White))e.Graphics.FillEllipse(brush,x-3*scale,Height/2-3*scale,6*scale,6*scale);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(1,1,Width-3,Height-3));
        }
    }
}
