using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal sealed class TrendSettings
    {
        public decimal RapidDrop=5;
        internal static TrendSettings Load(string root){try{var value=new JavaScriptSerializer().Deserialize<TrendSettings>(File.ReadAllText(Path.Combine(root,"trend-settings.json")));return value!=null&&value.RapidDrop>=.5m&&value.RapidDrop<=50?value:new TrendSettings();}catch{return new TrendSettings();}}
        internal void Save(string root){string path=Path.Combine(root,"trend-settings.json"),temp=path+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    }
    internal sealed class DetailsForm : Form
    {
        readonly string dataRoot,source;readonly ComboBox range,window;readonly NumericUpDown threshold;readonly Label summary,coverage,current,total,hour,rate;
        readonly TextBox findings,report;readonly DataGridView events;readonly QuotaTrendChart chart;readonly System.Windows.Forms.Timer refresh=new System.Windows.Forms.Timer{Interval=60000};
        readonly TrendSettings settings;QuotaHistory history=new QuotaHistory();internal TrendAnalysis Analysis;bool busy;int generation;
        public DetailsForm(string root,string backend)
        {
            dataRoot=root;source=backend;settings=TrendSettings.Load(root);SuspendLayout();Text="UsageObserver · 额度趋势与分析";Font=new Font("Microsoft YaHei UI",9F);BackColor=VisualTheme.Canvas;ForeColor=VisualTheme.Ink;AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(984,758);MinimumSize=new Size(820,640);StartPosition=FormStartPosition.CenterParent;
            var outer=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=3};outer.RowStyles.Add(new RowStyle(SizeType.Absolute,56));outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,24));Controls.Add(outer);
            var title=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty};outer.Controls.Add(title,0,0);VisualTheme.Text(title,"额度趋势与分析",0,0,510,39,20,VisualTheme.Ink,true);
            var reload=VisualTheme.Button(title,"刷新",0,4,94,delegate{Reload();});reload.Anchor=AnchorStyles.Top|AnchorStyles.Right;title.SizeChanged+=delegate{reload.Left=Math.Max(0,title.ClientSize.Width-reload.Width);};
            var tabs=new TabControl{Dock=DockStyle.Fill,Margin=Padding.Empty,Font=Font};outer.Controls.Add(tabs,0,1);var trend=new TabPage("额度趋势");var raw=new TabPage("记录报告");tabs.TabPages.Add(trend);tabs.TabPages.Add(raw);
            var content=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=1,RowCount=6,BackColor=VisualTheme.Canvas};trend.Controls.Add(content);
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));content.RowStyles.Add(new RowStyle(SizeType.Absolute,72));content.RowStyles.Add(new RowStyle(SizeType.Percent,55));content.RowStyles.Add(new RowStyle(SizeType.Absolute,26));content.RowStyles.Add(new RowStyle(SizeType.Percent,45));content.RowStyles.Add(new RowStyle(SizeType.Absolute,90));
            var filters=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=new Padding(0,0,0,7),BackColor=VisualTheme.Canvas};content.Controls.Add(filters,0,0);
            range=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Font=Font,Width=130,Margin=new Padding(0,3,8,0)};range.Items.AddRange(new object[]{"近 6 小时","近 24 小时","近 7 天","全部记录"});range.SelectedIndex=1;filters.Controls.Add(range);
            window=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Font=Font,Width=144,Margin=new Padding(0,3,12,0)};window.Items.AddRange(new object[]{"主额度窗口","次额度窗口"});window.SelectedIndex=0;filters.Controls.Add(window);
            filters.Controls.Add(new Label{Text="快速下降阈值",AutoSize=false,Size=new Size(108,28),Margin=new Padding(0,7,0,0),ForeColor=VisualTheme.Muted});
            threshold=new NumericUpDown{Font=new Font("Segoe UI",10F),Width=70,DecimalPlaces=1,Minimum=.5m,Maximum=50,Increment=.5m,Value=settings.RapidDrop,Margin=new Padding(0,3,7,0)};filters.Controls.Add(threshold);
            filters.Controls.Add(new Label{Text="百分点 / 15 分钟",AutoSize=false,Size=new Size(174,28),Margin=new Padding(0,7,0,0),ForeColor=VisualTheme.Muted});
            var metrics=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=2,Margin=Padding.Empty,BackColor=VisualTheme.Surface};for(int i=0;i<4;i++)metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));metrics.RowStyles.Add(new RowStyle(SizeType.Absolute,24));metrics.RowStyles.Add(new RowStyle(SizeType.Percent,100));content.Controls.Add(metrics,0,1);
            string[] names={"当前剩余","区间观测下降","近 1 小时下降","近 15 分钟速度"};var values=new Label[4];for(int i=0;i<4;i++){metrics.Controls.Add(new Label{Text=names[i],Dock=DockStyle.Fill,ForeColor=VisualTheme.Muted,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0)},i,0);values[i]=new Label{Font=new Font("Segoe UI",18F),Text="—",Dock=DockStyle.Fill,ForeColor=VisualTheme.Ink,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0)};metrics.Controls.Add(values[i],i,1);}current=values[0];total=values[1];hour=values[2];rate=values[3];
            chart=new QuotaTrendChart{Dock=DockStyle.Fill,Margin=new Padding(0,8,0,4)};content.Controls.Add(chart,0,2);
            summary=new Label{Dock=DockStyle.Fill,Text="正在读取本地历史…",ForeColor=VisualTheme.Muted,Margin=Padding.Empty};content.Controls.Add(summary,0,3);
            events=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,Margin=Padding.Empty};events.Columns.Add("time","时间");events.Columns.Add("drop","观测下降");events.Columns.Add("kind","判断");events.Columns.Add("basis","依据 / 排查建议");events.Columns[0].FillWeight=95;events.Columns[1].FillWeight=70;events.Columns[2].FillWeight=130;events.Columns[3].FillWeight=390;events.DefaultCellStyle.WrapMode=DataGridViewTriState.True;events.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;content.Controls.Add(events,0,4);
            findings=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,Margin=new Padding(0,8,0,0),Font=Font};content.Controls.Add(findings,0,5);
            report=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",10F)};raw.Controls.Add(report);
            coverage=new Label{Dock=DockStyle.Fill,Text="账号共享额度 · 本地只读分析",ForeColor=VisualTheme.Muted,Margin=Padding.Empty};outer.Controls.Add(coverage,0,2);
            range.SelectedIndexChanged+=delegate{UpdateAnalysis();};window.SelectedIndexChanged+=delegate{UpdateAnalysis();};threshold.ValueChanged+=delegate{settings.RapidDrop=threshold.Value;try{settings.Save(dataRoot);}catch{coverage.Text="检测阈值暂时未能保存；本次预览仍有效。";}UpdateAnalysis();};
            events.SelectionChanged+=delegate{if(events.CurrentRow!=null&&events.CurrentRow.Tag is TrendEvent){var entry=(TrendEvent)events.CurrentRow.Tag;chart.Highlight(entry.Time);}};
            Shown+=delegate{Reload();};refresh.Tick+=delegate{if(Visible)Reload();};refresh.Start();FormClosed+=delegate{generation++;};VisualTheme.Decorate(this);ResumeLayout(false);PerformLayout();
        }
        internal void SetHistory(QuotaHistory value){history=value;UpdateAnalysis();}
        void Reload()
        {
            if(busy||IsDisposed)return;busy=true;int request=++generation;summary.Text="正在读取本地历史…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                QuotaHistory loaded;string raw;
                try{loaded=QuotaHistory.Read(Path.Combine(source,"records.sqlite"));raw=File.Exists(Path.Combine(source,"report.md"))?File.ReadAllText(Path.Combine(source,"report.md")):"尚无记录报告。";}
                catch(Exception error){loaded=new QuotaHistory{Error="read_failed"};raw="读取暂时失败："+error.Message;}
                try{if(!IsDisposed&&IsHandleCreated)BeginInvoke(new Action(delegate{if(request!=generation||IsDisposed)return;busy=false;report.Text=raw;SetHistory(loaded);}));}catch(InvalidOperationException){}
            });
        }
        void UpdateAnalysis()
        {
            TimeSpan? span=range.SelectedIndex==0?TimeSpan.FromHours(6):range.SelectedIndex==1?TimeSpan.FromHours(24):range.SelectedIndex==2?TimeSpan.FromDays(7):(TimeSpan?)null;
            Analysis=TrendAnalysis.Analyze(history,window.SelectedIndex==1,span,(double)threshold.Value,DateTimeOffset.UtcNow);chart.SetAnalysis(Analysis);
            current.Text=Analysis.Remaining.HasValue?Analysis.Remaining.Value.ToString("0.##")+"%":"—";total.Text=Analysis.Remaining.HasValue?Analysis.Drop.ToString("0.##")+" pp":"—";hour.Text=Analysis.Remaining.HasValue?Analysis.HourDrop.ToString("0.##")+" pp":"—";rate.Text=Analysis.RateAvailable?Analysis.RecentRate.ToString("0.##")+" pp/h":"数据不足";summary.Text=Analysis.Summary;
            events.Rows.Clear();foreach(var entry in Analysis.Events.OrderByDescending(e=>e.Kind=="rapid"||e.Kind=="spike"||e.Kind=="unattributed").ThenByDescending(e=>e.Time).Take(200)){int row=events.Rows.Add(entry.Time.ToLocalTime().ToString("MM-dd HH:mm"),entry.Kind=="reset"?"—":entry.Drop.ToString("0.##")+" pp",entry.Title,entry.Detail);events.Rows[row].Tag=entry;}
            findings.Text="pp = 百分点。区间合计相加各记录段的有效下降；不计算跨重置、账号切换和缺测边界的变化。列表优先展示待核查时段。\r\n账号额度由多设备共享，token 只作采集线索，无法换算或归属额度消耗。快速下降是待核查提示，可能来自正常密集任务、长任务尚未结束或延迟结算。"+(Analysis.AccountUnknown?"\r\n旧记录缺少账号标记，无法识别历史登录切换；请核对当时登录账户。":"")+"\r\n检测采用 15 分钟分段、手动阈值与历史中位数 / 波动范围；历史不足时仅显示阈值提示。";
            var valid=Analysis.Points.Where(p=>p.Valid(Analysis.Secondary)).ToList();coverage.Text=valid.Count==0?"所选窗口 / 时间范围暂无有效额度记录":valid.First().Time.ToLocalTime().ToString("MM-dd HH:mm")+" — "+valid.Last().Time.ToLocalTime().ToString("MM-dd HH:mm")+" · "+valid.Count+" 个有效点"+(history.Truncated?" · 仅加载最新 30,000 个采样":"")+(Analysis.AccountChanges>0?" · 已隔离其他账号记录":"")+" · 每分钟自动刷新";
        }
        protected override void Dispose(bool disposing){if(disposing){generation++;refresh.Stop();refresh.Dispose();}base.Dispose(disposing);}
    }
    internal sealed class QuotaTrendChart : Control
    {
        TrendAnalysis data=new TrendAnalysis();List<QuotaPoint> valid=new List<QuotaPoint>();QuotaPoint hovered;DateTimeOffset? selected;Rectangle plot;DateTimeOffset start,end;
        public QuotaTrendChart(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);MinimumSize=new Size(0,140);AccessibleName="剩余额度趋势图";}
        internal void SetAnalysis(TrendAnalysis value){data=value;valid=data.Points.Where(p=>p.Valid(data.Secondary)).ToList();hovered=null;Invalidate();}
        internal void Highlight(DateTimeOffset time){selected=time;Invalidate();}
        float X(DateTimeOffset time){return plot.Left+(float)((time-start).TotalSeconds/Math.Max(1,(end-start).TotalSeconds))*plot.Width;}
        float Y(double remaining){return plot.Bottom-(float)(remaining/100.0)*plot.Height;}
        protected override void OnMouseLeave(EventArgs e){hovered=null;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!plot.Contains(e.Location)||valid.Count==0){if(hovered!=null){hovered=null;Invalidate();}return;}
            double target=(e.X-plot.Left)/(double)Math.Max(1,plot.Width);var time=start.AddSeconds((end-start).TotalSeconds*target);int lo=0,hi=valid.Count-1;while(lo<hi){int mid=(lo+hi)/2;if(valid[mid].Time<time)lo=mid+1;else hi=mid;}var next=lo>0&&Math.Abs((valid[lo-1].Time-time).TotalSeconds)<Math.Abs((valid[lo].Time-time).TotalSeconds)?valid[lo-1]:valid[lo];if(next!=hovered){hovered=next;Invalidate();}
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(VisualTheme.Surface);g.SmoothingMode=SmoothingMode.AntiAlias;float scale=g.DpiX/96F;plot=new Rectangle((int)(52*scale),(int)(26*scale),Math.Max(1,Width-(int)(72*scale)),Math.Max(1,Height-(int)(61*scale)));
            using(var font=new Font("Microsoft YaHei UI",8.5F))
            {
                TextRenderer.DrawText(g,"剩余额度（%） · 采样值",font,new Point((int)(8*scale),0),VisualTheme.Readable(VisualTheme.Muted,VisualTheme.Surface));
                for(int n=0;n<=100;n+=25){float y=Y(n);using(var pen=new Pen(VisualTheme.Border))g.DrawLine(pen,plot.Left,y,plot.Right,y);TextRenderer.DrawText(g,n.ToString(),font,new Rectangle(0,(int)y-(int)(9*scale),plot.Left-(int)(7*scale),(int)(20*scale)),VisualTheme.Readable(VisualTheme.Muted,VisualTheme.Surface),TextFormatFlags.Right|TextFormatFlags.VerticalCenter);}
                if(valid.Count==0){TextRenderer.DrawText(g,"所选范围暂无有效采样",font,plot,VisualTheme.Readable(VisualTheme.Muted,VisualTheme.Surface),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
                start=valid.First().Time;end=valid.Last().Time;if(end==start)end=start.AddMinutes(1);
                for(int n=0;n<4;n++){var time=start.AddSeconds((end-start).TotalSeconds*n/3);int width=(int)(96*scale),left=Math.Min(Width-width,Math.Max(0,plot.Left+n*plot.Width/3-width/2));var box=new Rectangle(left,plot.Bottom+(int)(8*scale),width,(int)(22*scale));TextRenderer.DrawText(g,time.ToLocalTime().ToString("MM-dd HH:mm"),font,box,VisualTheme.Readable(VisualTheme.Muted,VisualTheme.Surface),TextFormatFlags.HorizontalCenter);}
                QuotaPoint previous=null;bool gap=false;using(var path=new GraphicsPath())using(var pen=new Pen(VisualTheme.Readable(VisualTheme.Accent,VisualTheme.Surface),Math.Max(1.5F,2*scale)))
                {
                    foreach(var point in data.Points)
                    {
                        if(!point.Valid(data.Secondary)){gap=true;continue;}float x=X(point.Time),y=Y(100-point.Used(data.Secondary).Value);
                        if(previous==null||gap||data.Breaks.Contains(point))path.StartFigure();else{path.AddLine(X(previous.Time),Y(100-previous.Used(data.Secondary).Value),x,Y(100-previous.Used(data.Secondary).Value));path.AddLine(x,Y(100-previous.Used(data.Secondary).Value),x,y);}
                        previous=point;gap=false;
                    }
                    if(path.PointCount>0)g.DrawPath(pen,path);
                }
                foreach(var entry in data.Events.Where(a=>a.Time>=start&&a.Time<=end).Take(80))
                {
                    float x=X(entry.Time);Color color=entry.Kind=="reset"?VisualTheme.Accent:VisualTheme.Warning;using(var pen=new Pen(color,1){DashStyle=DashStyle.Dot})g.DrawLine(pen,x,plot.Top,x,plot.Bottom);
                }
                if(selected.HasValue&&selected>=start&&selected<=end)using(var pen=new Pen(VisualTheme.Warning,2))g.DrawLine(pen,X(selected.Value),plot.Top,X(selected.Value),plot.Bottom);
                var last=valid.Last();using(var brush=new SolidBrush(VisualTheme.Accent))g.FillEllipse(brush,X(last.Time)-3*scale,Y(100-last.Used(data.Secondary).Value)-3*scale,6*scale,6*scale);
                if(hovered!=null)
                {
                    float x=X(hovered.Time),y=Y(100-hovered.Used(data.Secondary).Value);using(var pen=new Pen(VisualTheme.Muted){DashStyle=DashStyle.Dot})g.DrawLine(pen,x,plot.Top,x,plot.Bottom);using(var brush=new SolidBrush(VisualTheme.Accent))g.FillEllipse(brush,x-4*scale,y-4*scale,8*scale,8*scale);
                    string text=hovered.Time.ToLocalTime().ToString("MM-dd HH:mm:ss")+" · 剩余 "+(100-hovered.Used(data.Secondary).Value).ToString("0.##")+"%";var size=TextRenderer.MeasureText(text,font);var box=new Rectangle(Math.Min(plot.Right-size.Width-12,Math.Max(plot.Left,(int)x+8)),plot.Top+6,size.Width+12,size.Height+8);using(var brush=new SolidBrush(VisualTheme.Soft))g.FillRectangle(brush,box);TextRenderer.DrawText(g,text,font,box,VisualTheme.Readable(VisualTheme.Ink,VisualTheme.Soft),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
