using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal static class VisualTheme
    {
        internal static readonly Color Ink=Color.FromArgb(31,48,42), Muted=Color.FromArgb(111,125,118), Accent=Color.FromArgb(35,116,87), Canvas=Color.FromArgb(245,247,243), Night=Color.FromArgb(22,43,36), Mint=Color.FromArgb(145,227,185), Border=Color.FromArgb(226,233,225), Warning=Color.FromArgb(230,181,103), Soft=Color.FromArgb(234,241,233);
        internal static GraphicsPath Round(Rectangle r,int radius)
        {
            var p=new GraphicsPath();int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
            if(d<=0){p.AddRectangle(r);return p;}
            p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        internal static float Scale(Control control){using(var g=control.CreateGraphics())return g.DpiX/96F;}
        internal static Label Text(Control parent,string text,int x,int y,int w,int h,float size,Color color,bool bold=false)
        {
            var label=new Label{Text=text,Bounds=new Rectangle(x,y,w,h),Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=color,BackColor=Color.Transparent,UseCompatibleTextRendering=false};parent.Controls.Add(label);return label;
        }
        internal static Label Number(Control parent,string text,int x,int y,int w,int h,float size,Color color)
        {
            var label=Text(parent,text,x,y,w,h,size,color);label.Font=new Font("Segoe UI",size,FontStyle.Regular);return label;
        }
        internal static Panel Card(Control parent,int x,int y,int w,int h,Color fill)
        {
            var card=new SurfaceCard{Bounds=new Rectangle(x,y,w,h),Fill=fill,BackColor=parent.BackColor};parent.Controls.Add(card);return card;
        }
        internal static Button Button(Control parent,string text,int x,int y,int w,Action action,bool primary=false)
        {
            var button=new SurfaceButton{Text=text,Bounds=new Rectangle(x,y,w,34),Primary=primary,BackColor=parent is SurfaceCard?((SurfaceCard)parent).Fill:parent.BackColor,Font=new Font("Microsoft YaHei UI",9F)};
            button.Click+=delegate{action();};parent.Controls.Add(button);return button;
        }
        internal static void Decorate(Control parent)
        {
            if(parent is Form||parent is TabPage){parent.BackColor=Canvas;parent.ForeColor=Ink;}
            foreach(Control c in parent.Controls)
            {
                if(c is TextBox||c is NumericUpDown){c.BackColor=Color.White;c.ForeColor=Ink;}
                var text=c as TextBox;if(text!=null&&!text.ReadOnly)text.BorderStyle=BorderStyle.FixedSingle;
                var grid=c as DataGridView;
                if(grid!=null)
                {
                    grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.None;grid.GridColor=Border;
                    grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;
                    grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;grid.ColumnHeadersHeight=48;
                    grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Soft,ForeColor=Muted,Font=new Font("Microsoft YaHei UI",8.5F),Padding=new Padding(8),WrapMode=DataGridViewTriState.True,SelectionBackColor=Soft,SelectionForeColor=Muted};
                    grid.DefaultCellStyle.BackColor=Color.White;grid.DefaultCellStyle.ForeColor=Ink;grid.DefaultCellStyle.Padding=new Padding(8,10,8,10);grid.DefaultCellStyle.Font=new Font("Microsoft YaHei UI",9F);
                    grid.DefaultCellStyle.SelectionBackColor=Soft;grid.DefaultCellStyle.SelectionForeColor=Ink;grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(249,251,248);
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
    }
    internal sealed class SurfaceCard : Panel
    {
        public Color Fill=Color.White;
        public SurfaceCard(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int radius=(int)(16*e.Graphics.DpiX/96F);
            using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),radius))
            {
                if(Fill==VisualTheme.Night){using(var brush=new LinearGradientBrush(ClientRectangle,Color.FromArgb(33,68,53),VisualTheme.Night,35F))e.Graphics.FillPath(brush,path);}
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
            Color fill=Primary?(pressed?Color.FromArgb(25,83,63):hover?Color.FromArgb(29,100,76):VisualTheme.Accent):dark?Color.FromArgb(43,72,57):hover?VisualTheme.Soft:Color.White;
            if(Quiet&&!hover&&!pressed)fill=BackColor;
            if(!Enabled)fill=VisualTheme.Soft;
            using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),(int)(9*e.Graphics.DpiX/96F)))
            {using(var brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);if(!Primary&&!dark&&!Quiet)using(var pen=new Pen(hover?Color.FromArgb(186,210,188):VisualTheme.Border))e.Graphics.DrawPath(pen,path);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,!Enabled?VisualTheme.Muted:Primary?Color.White:dark?VisualTheme.Mint:VisualTheme.Ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(4,4,Width-8,Height-8));
        }
    }
    internal sealed class SurfaceToggle : CheckBox
    {
        public SurfaceToggle(){AutoSize=false;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;float scale=e.Graphics.DpiX/96F;int w=(int)(34*scale),h=(int)(20*scale),margin=(int)(3*scale);var track=new Rectangle(Width-w-1,(Height-h)/2,w,h);
            using(var path=VisualTheme.Round(track,h/2))using(var brush=new SolidBrush(!Enabled?VisualTheme.Border:Checked?VisualTheme.Accent:Color.FromArgb(198,209,197)))e.Graphics.FillPath(brush,path);
            int diameter=h-margin*2;using(var brush=new SolidBrush(Color.White))e.Graphics.FillEllipse(brush,Checked?track.Right-margin-diameter:track.Left+margin,track.Top+margin,diameter,diameter);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,0,Width-w-(int)(12*scale),Height),Enabled?ForeColor:VisualTheme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(1,1,Width-3,Height-3));
        }
    }
    internal sealed class SurfaceSlider : Control
    {
        int value=60;bool dragging;public int Minimum=0,Maximum=90,SmallChange=1,LargeChange=10;
        public event EventHandler ValueChanged;
        public int Value {get{return value;}set{int clamped=Math.Max(Minimum,Math.Min(Maximum,value));if(this.value==clamped)return;this.value=clamped;Invalidate();AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);if(ValueChanged!=null)ValueChanged(this,EventArgs.Empty);}}
        public SurfaceSlider(){TabStop=true;AccessibleRole=AccessibleRole.Slider;AccessibleName="浮窗透明度";Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);}
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
