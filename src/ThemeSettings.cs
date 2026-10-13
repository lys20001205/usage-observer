using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal sealed class ThemeSettings
    {
        public string Preset="forest",Accent="#237457",Canvas="#F5F7F3",Surface="#FFFFFF",WidgetBackground="#162B24",WidgetText="#FFFFFF",WidgetAccent="#91E3B9";
        internal static readonly string[] Keys={"Accent","Canvas","Surface","WidgetBackground","WidgetText","WidgetAccent"};
        internal static readonly string[] Labels={"界面主色","页面底色","卡片底色","浮窗底色","浮窗文字","浮窗强调色"};
        internal string Get(string key){return (string)GetType().GetField(key).GetValue(this);}
        internal void Set(string key,string value){GetType().GetField(key).SetValue(this,Hex(Parse(value)));}
        internal ThemeSettings Copy(){return (ThemeSettings)MemberwiseClone();}
        internal static string Hex(Color color){return "#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2");}
        internal static Color Parse(string value)
        {
            int rgb;if(value==null||value.Length!=7||value[0]!='#'||!int.TryParse(value.Substring(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out rgb))throw new FormatException("请输入 #RRGGBB 格式的颜色，例如 #237457。");
            return Color.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);
        }
        internal static ThemeSettings Create(string name)
        {
            var result=new ThemeSettings();result.Preset=name;
            switch(name)
            {
                case "ocean":result.Accent="#236EA5";result.Canvas="#F1F5F9";result.WidgetBackground="#152B40";result.WidgetAccent="#94D5FF";break;
                case "violet":result.Accent="#7954AD";result.Canvas="#F6F3FA";result.WidgetBackground="#30203F";result.WidgetAccent="#D8B6FF";break;
                case "graphite":result.Accent="#78B7E7";result.Canvas="#20252B";result.Surface="#2B323A";result.WidgetBackground="#1B2026";result.WidgetAccent="#A6D8FF";break;
                default:result.Preset="forest";break;
            }
            return result;
        }
        internal static ThemeSettings Load(string root)
        {
            try
            {
                var result=new JavaScriptSerializer().Deserialize<ThemeSettings>(File.ReadAllText(Path.Combine(root,"theme-settings.json")));
                if(result==null)return new ThemeSettings();foreach(string key in Keys)result.Set(key,result.Get(key));return result;
            }
            catch{return new ThemeSettings();}
        }
        internal void Save(string root)
        {
            foreach(string key in Keys)Parse(Get(key));Directory.CreateDirectory(root);
            string path=Path.Combine(root,"theme-settings.json"),temp=path+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
    }
    internal sealed class ThemeForm : Form
    {
        readonly string root;readonly ThemeSettings original;
        ThemeSettings working;bool syncing,saved;
        readonly ComboBox presets;readonly Label message;readonly TextBox[] values=new TextBox[6];readonly ThemeSwatch[] swatches=new ThemeSwatch[6];
        public ThemeForm(string directory)
        {
            root=directory;original=VisualTheme.CurrentTheme;working=original.Copy();SuspendLayout();
            Text="UsageObserver · 主题与调色";ClientSize=new Size(592,538);Font=new Font("Microsoft YaHei UI",9F);BackColor=VisualTheme.Canvas;ForeColor=VisualTheme.Ink;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            AutoScroll=true;AutoScrollMinSize=new Size(580,538);
            VisualTheme.Text(this,"主题与调色",24,19,460,37,19,VisualTheme.Ink,true);
            message=VisualTheme.Text(this,"总览与浮窗同步预览 · 保存后重启保留",26,59,540,24,8.5F,VisualTheme.Muted);
            var presetCard=VisualTheme.Card(this,24,98,544,64,VisualTheme.Surface);
            VisualTheme.Text(presetCard,"预设主题",18,20,140,24,10,VisualTheme.Ink);
            presets=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Font=Font,Bounds=new Rectangle(172,17,354,30)};
            presets.Items.AddRange(new object[]{"墨绿（默认）","海蓝","紫罗兰","石墨（深色）","自定义"});presetCard.Controls.Add(presets);
            presets.SelectedIndexChanged+=delegate{if(syncing||presets.SelectedIndex==4)return;working=ThemeSettings.Create(new[]{"forest","ocean","violet","graphite"}[presets.SelectedIndex]);Sync();Preview();};
            var colors=VisualTheme.Card(this,24,176,544,232,VisualTheme.Surface);
            for(int i=0;i<6;i++)
            {
                int index=i,x=i%2==0?18:282,y=14+(i/2)*70;string key=ThemeSettings.Keys[i];
                var swatch=new ThemeSwatch{AccessibleName="选择"+ThemeSettings.Labels[i],Bounds=new Rectangle(x,y+8,42,42)};swatches[i]=swatch;colors.Controls.Add(swatch);
                VisualTheme.Text(colors,ThemeSettings.Labels[i],x+54,y+5,190,22,9,VisualTheme.Ink);
                var value=new TextBox{Font=new Font("Segoe UI",10F),Bounds=new Rectangle(x+54,y+29,178,26),MaxLength=7,AccessibleName=ThemeSettings.Labels[i]+"十六进制颜色"};values[i]=value;colors.Controls.Add(value);
                value.Leave+=delegate{CommitColor(index);};value.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){CommitColor(index);e.SuppressKeyPress=true;}};
                swatch.Click+=delegate
                {
                    using(var picker=new ColorDialog{Color=ThemeSettings.Parse(working.Get(key)),FullOpen=true,AnyColor=true,CustomColors=Array.ConvertAll(ThemeSettings.Keys,k=>ColorTranslator.ToWin32(ThemeSettings.Parse(working.Get(k))))})
                        if(picker.ShowDialog(this)==DialogResult.OK){working.Set(key,ThemeSettings.Hex(picker.Color));working.Preset="custom";Sync();Preview();}
                };
            }
            Controls.Add(new ThemeSample{Bounds=new Rectangle(24,422,544,54)});
            VisualTheme.Button(this,"恢复默认",24,494,132,delegate{working=ThemeSettings.Create("forest");Sync();Preview();});
            var cancel=VisualTheme.Button(this,"取消",324,494,112,delegate{Close();});cancel.DialogResult=DialogResult.Cancel;CancelButton=cancel;
            VisualTheme.Button(this,"保存主题",444,494,124,delegate
            {
                try{var candidate=working.Copy();for(int i=0;i<6;i++){string key=ThemeSettings.Keys[i];candidate.Set(key,values[i].Text.Trim());if(candidate.Get(key)!=working.Get(key))candidate.Preset="custom";}working=candidate;working.Save(root);VisualTheme.ApplyTheme(working);saved=true;Close();}
                catch(Exception error){message.Text="保存失败："+error.Message;}
            },true);
            VisualTheme.Decorate(this);Sync();ResumeLayout(false);PerformLayout();
            FormClosed+=delegate{if(!saved)VisualTheme.ApplyTheme(original);};
        }
        internal bool CommitColor(int index)
        {
            if(syncing)return true;
            try
            {
                string normalized=ThemeSettings.Hex(ThemeSettings.Parse(values[index].Text.Trim()));
                if(working.Get(ThemeSettings.Keys[index])!=normalized){working.Set(ThemeSettings.Keys[index],normalized);working.Preset="custom";Sync();Preview();}
                message.Text="总览与浮窗同步预览 · 保存后重启保留";return true;
            }
            catch(FormatException error){message.Text=ThemeSettings.Labels[index]+"："+error.Message;return false;}
        }
        void Preview(){VisualTheme.ApplyTheme(working);}
        void Sync()
        {
            syncing=true;
            try{presets.SelectedIndex=Array.IndexOf(new[]{"forest","ocean","violet","graphite"},working.Preset);if(presets.SelectedIndex<0)presets.SelectedIndex=4;for(int i=0;i<6;i++){values[i].Text=working.Get(ThemeSettings.Keys[i]);swatches[i].ColorValue=ThemeSettings.Parse(values[i].Text);swatches[i].Invalidate();}}
            finally{syncing=false;}
        }
    }
    internal sealed class ThemeSwatch : Button
    {
        internal Color ColorValue;
        public ThemeSwatch(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(VisualTheme.Surface);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var path=VisualTheme.Round(new Rectangle(1,1,Width-3,Height-3),8))using(var brush=new SolidBrush(ColorValue))using(var pen=new Pen(VisualTheme.Muted)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(4,4,Width-8,Height-8));
        }
    }
    internal sealed class ThemeSample : Control
    {
        public ThemeSample(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(VisualTheme.Canvas);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var path=VisualTheme.Round(new Rectangle(0,0,Width-1,Height-1),10))using(var brush=new SolidBrush(VisualTheme.Night))e.Graphics.FillPath(brush,path);
            using(var font=new Font("Segoe UI",14F))TextRenderer.DrawText(e.Graphics,"CODEX 87%    DSH ¥214.95",font,new Rectangle(12,0,Width-24,Height),VisualTheme.WidgetText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            using(var brush=new SolidBrush(VisualTheme.Mint))e.Graphics.FillRectangle(brush,Width-62,Height/2-3,42,6);
        }
    }
}
