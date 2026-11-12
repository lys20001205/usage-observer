using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal static class ApiUsageRuntime
    {
        internal static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        internal static object Get(Dictionary<string,object> d,string key){object value;return d.TryGetValue(key,out value)?value:null;}
        internal static string Str(Dictionary<string,object> d,string key){return Convert.ToString(Get(d,key),CultureInfo.InvariantCulture);}
        internal static Dictionary<string,object> Read(string path){try{return Map(new JavaScriptSerializer{MaxJsonLength=8*1024*1024}.DeserializeObject(File.ReadAllText(path,Encoding.UTF8)));}catch{return new Dictionary<string,object>();}}
        internal static string Money(object value){decimal n;return value!=null&&decimal.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Number,CultureInfo.InvariantCulture,out n)?"$"+n.ToString("N2",CultureInfo.InvariantCulture):"待获取";}
        internal static string Percent(object value){decimal n;return value!=null&&decimal.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Number,CultureInfo.InvariantCulture,out n)?n.ToString("0.00",CultureInfo.InvariantCulture)+"%":"待三机同步";}
        internal static string When(object value){DateTimeOffset t;return DateTimeOffset.TryParse(Convert.ToString(value),out t)?t.ToLocalTime().ToString("MM-dd HH:mm:ss"):"等待";}
        internal static void Request(string root){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"api-usage.request"),DateTimeOffset.UtcNow.ToString("o"));Ensure(root);}
        internal static void Ensure(string root)
        {
            string app=AppDomain.CurrentDomain.BaseDirectory,script=Path.Combine(app,"api_usage.py"),python=Path.Combine(app,"runtime","python","python.exe");
            if(!File.Exists(script)||!File.Exists(python))return;
            var state=Read(Path.Combine(root,"api-usage-process.json"));
            try{using(var p=Process.GetProcessById(Convert.ToInt32(Get(state,"pid"))))if(!p.HasExited&&string.Equals(Str(state,"script"),script,StringComparison.OrdinalIgnoreCase))return;}catch{}
            try{File.Delete(Path.Combine(root,"api-usage-stop.request"));}catch{}
            Process.Start(new ProcessStartInfo(python,"\""+script+"\" --data \""+root+"\" --loop"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=app});
        }
    }

    internal sealed class ApiUsageForm:Form
    {
        internal ApiUsageForm(string root)
        {
            Text="UsageObserver · API金额与三机分摊";ClientSize=new Size(1050,720);MinimumSize=new Size(900,630);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Microsoft YaHei UI",9F);BackColor=VisualTheme.Canvas;Controls.Add(new ApiUsagePanel(root));
        }
    }

    internal sealed class ApiUsagePanel:UserControl
    {
        readonly string root;readonly Button period;readonly ContextMenuStrip periodMenu;readonly NumericUpDown budget,expected;
        readonly Label summary,footnote;readonly DataGridView devices,models;
        readonly System.Windows.Forms.Timer timer;int selectedPeriod=-1;string signature="";decimal appliedBudget=200;int appliedExpected=3;DateTimeOffset nextAgeCheck;
        readonly List<Dictionary<string,object>> periods=new List<Dictionary<string,object>>();
        readonly List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
        static object Get(Dictionary<string,object> d,string key){return ApiUsageRuntime.Get(d,key);}
        static string Str(Dictionary<string,object> d,string key){return ApiUsageRuntime.Str(d,key);}
        static Dictionary<string,object> Map(object v){return ApiUsageRuntime.Map(v);}
        static IEnumerable List(Dictionary<string,object> d,string key){return Get(d,key) as IEnumerable??new object[0];}
        internal ApiUsagePanel(string data)
        {
            root=data;Dock=DockStyle.Fill;AutoScroll=true;BackColor=VisualTheme.Canvas;
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(20),ColumnCount=1,RowCount=8};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(body);
            var title=new Label{Text="API金额与三机分摊",AutoSize=true,Font=new Font("Microsoft YaHei UI",20F,FontStyle.Bold),ForeColor=VisualTheme.Ink,Margin=new Padding(0,0,0,14)};body.Controls.Add(title);
            var controls=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0,0,0,12)};
            periodMenu=new ContextMenuStrip{Font=Font,BackColor=VisualTheme.Surface,ForeColor=VisualTheme.Ink,Renderer=new ThemeMenuRenderer(),ShowImageMargin=false};
            period=VisualTheme.Button(controls,"等待周期 ▾",0,0,330,delegate{periodMenu.Show(period,new Point(0,period.Height));});period.AccessibleName="金额统计周期";period.TextAlign=ContentAlignment.MiddleLeft;period.Padding=new Padding(10,0,0,0);controls.Controls.Add(period);
            controls.Controls.Add(new Label{Text="整份额度 $",AutoSize=true,Margin=new Padding(0,7,3,0),ForeColor=VisualTheme.Ink});
            var settings=ApiUsageRuntime.Read(Path.Combine(root,"api-settings.json"));decimal n;
            budget=new NumericUpDown{Minimum=1,Maximum=1000000,DecimalPlaces=2,Value=decimal.TryParse(Str(settings,"budget_usd"),out n)?Math.Max(1,Math.Min(1000000,n)):200,Width=98,Margin=new Padding(0,3,12,3),AccessibleName="整份额度美元"};controls.Controls.Add(budget);
            controls.Controls.Add(new Label{Text="设备数",AutoSize=true,Margin=new Padding(0,7,3,0),ForeColor=VisualTheme.Ink});
            expected=new NumericUpDown{Minimum=1,Maximum=32,Value=decimal.TryParse(Str(settings,"expected_devices"),out n)?Math.Max(1,Math.Min(32,n)):3,Width=52,Margin=new Padding(0,3,10,3),AccessibleName="同步设备数量"};controls.Controls.Add(expected);
            controls.Controls.Add(VisualTheme.Button(controls,"保存分摊设置",0,0,128,Save));body.Controls.Add(controls);
            summary=new Label{Text="正在读取本机API记录…",AutoSize=true,MaximumSize=new Size(950,0),Padding=new Padding(16),BackColor=VisualTheme.Night,ForeColor=VisualTheme.Mint,Font=new Font("Microsoft YaHei UI",11F),Dock=DockStyle.Fill,Margin=new Padding(0,0,0,14)};body.Controls.Add(summary);
            devices=Grid(new[]{"设备","API折算美元","占已用金额","占整份额度","已用额度分摊","状态 / 更新"},250);devices.Columns[5].FillWeight=190;body.Controls.Add(devices);
            body.Controls.Add(new Label{Text="选中设备查看模型构成",AutoSize=true,ForeColor=VisualTheme.Ink,Margin=new Padding(0,15,0,8)});
            models=Grid(new[]{"模型","未缓存输入","缓存输入","输出（含推理）","API折算美元"},155);models.Columns[0].FillWeight=160;body.Controls.Add(models);
            footnote=new Label{Text="API Standard价格 · 含缓存和长上下文加价 · 价格表 2026-11-07\n额度按API金额比例估算；后台补算最近31天，离线设备保留上次记录。",AutoSize=true,MaximumSize=new Size(950,0),ForeColor=VisualTheme.Muted,Margin=new Padding(0,12,0,12)};body.Controls.Add(footnote);
            var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
            actions.Controls.Add(VisualTheme.Button(actions,"连接 / 同步三台电脑",0,0,188,delegate{new NetworkForm(root,true).Show(FindForm());},true));
            actions.Controls.Add(VisualTheme.Button(actions,"刷新API金额",0,0,146,delegate{ApiUsageRuntime.Request(root);signature="";Reload();}));
            actions.Controls.Add(VisualTheme.Button(actions,"打开金额明细",0,0,146,delegate{try{Process.Start(new ProcessStartInfo(Path.Combine(root,"api-usage-status.json")){UseShellExecute=true});}catch{}}));body.Controls.Add(actions);
            devices.SelectionChanged+=delegate{RenderModels();};
            timer=new System.Windows.Forms.Timer{Interval=3000};timer.Tick+=delegate{Reload();};timer.Start();Disposed+=delegate{timer.Dispose();periodMenu.Dispose();};
            Reload();VisualTheme.Decorate(this);
        }
        static DataGridView Grid(string[] columns,int height)
        {
            var grid=new DataGridView{Height=height,Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,Margin=Padding.Empty,BackgroundColor=VisualTheme.Surface};
            foreach(string name in columns)grid.Columns.Add(name,name);grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;return grid;
        }
        void Save()
        {
            string path=Path.Combine(root,"api-settings.json"),temp=path+".tmp";Directory.CreateDirectory(root);
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new{budget_usd=budget.Value.ToString(CultureInfo.InvariantCulture),expected_devices=(int)expected.Value}),Encoding.UTF8);
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);ApiUsageRuntime.Request(root);signature="";
        }
        string Key(Dictionary<string,object> p){return Str(p,"start")+":"+Str(p,"mode");}
        void Reload()
        {
            string path=Path.Combine(root,"api-combined.json"),text="";try{text=File.ReadAllText(path,Encoding.UTF8);}catch{}
            if(text.Length==0)
            {
                var progress=ApiUsageRuntime.Read(Path.Combine(root,"api-usage-progress.json"));string state=Str(progress,"state");
                summary.Text=state=="indexing"?"正在补算压缩历史 · "+Str(progress,"done")+" / "+Str(progress,"total")+" 个会话":state=="error"?"API记录暂未读完 · "+Str(progress,"code"):state=="paused"?"记录已暂停，保留已有金额":"等待后台API账本 · 首次补算需要一些时间";return;
            }
            if(text==signature){if(DateTimeOffset.UtcNow>=nextAgeCheck&&periods.Count>0){nextAgeCheck=DateTimeOffset.UtcNow.AddSeconds(30);RenderPeriod();}return;}signature=text;nextAgeCheck=DateTimeOffset.UtcNow.AddSeconds(30);
            Dictionary<string,object> report;
            try{report=Map(new JavaScriptSerializer{MaxJsonLength=8*1024*1024}.DeserializeObject(text));}
            catch{signature="";summary.Text="金额摘要正在更新，等待下一次有效记录。";return;}
            decimal savedBudget;int savedExpected;if(decimal.TryParse(Str(report,"budget_usd"),NumberStyles.Number,CultureInfo.InvariantCulture,out savedBudget))appliedBudget=savedBudget;if(int.TryParse(Str(report,"expected_devices"),out savedExpected))appliedExpected=savedExpected;
            string selected=selectedPeriod>=0&&selectedPeriod<periods.Count?Key(periods[selectedPeriod]):"";
            periodMenu.Items.Clear();periods.Clear();int index=0,selection=0;
            foreach(var item in List(report,"periods"))
            {
                var p=Map(item);periods.Add(p);string label=ApiUsageRuntime.When(Get(p,"start_at"))+" 周期 · "+(Str(p,"mode")=="at85"?"首次85%":"周期累计");int choice=index;
                periodMenu.Items.Add(label,null,delegate{selectedPeriod=choice;period.Text=periodMenu.Items[choice].Text+" ▾";RenderPeriod();});
                if(Key(p)==selected)selection=index;index++;
            }
            selectedPeriod=periods.Count>0?selection:-1;if(selectedPeriod>=0)period.Text=periodMenu.Items[selection].Text+" ▾";
            if(periods.Count==0)summary.Text="已有API记录，等待额度采样以划分周期。";else RenderPeriod();
        }
        void RenderPeriod()
        {
            if(selectedPeriod<0||selectedPeriod>=periods.Count)return;
            var p=periods[selectedPeriod];string selectedDevice=devices.CurrentRow!=null&&devices.CurrentRow.Index<rows.Count?Str(rows[devices.CurrentRow.Index],"device_id"):"";devices.Rows.Clear();rows.Clear();bool complete=Convert.ToBoolean(Get(p,"complete")??false);int selectedRow=-1;
            var names=new Dictionary<string,string>{{"ready","已同步"},{"saved_snapshot","已保存快照"},{"stale","上次记录"},{"upgrade_required","更新此设备以同步金额"},{"waiting_for_period","等待此周期记录"},{"aligning_cutoff","正在统一85%截止时间"},{"partial_history","正在补齐历史"},{"unpriced_model","存在待定价模型"},{"account_scope_unknown","等待账号范围对齐"}};
            foreach(var item in List(p,"devices"))
            {
                var row=Map(item);rows.Add(row);if(Str(row,"device_id")==selectedDevice)selectedRow=rows.Count-1;string status;string state=Str(row,"state");DateTimeOffset updated;if(state=="ready"&&Str(p,"mode")=="full"&&!Convert.ToBoolean(Get(p,"closed")??false)&&DateTimeOffset.TryParse(Str(row,"updated_at"),out updated)&&DateTimeOffset.UtcNow-updated>TimeSpan.FromMinutes(30)){state="stale";complete=false;}if(!names.TryGetValue(state,out status))status="等待同步";
                object amount=Get(row,"api_usd")??Get(row,"priced_usd");string value=ApiUsageRuntime.Money(amount);
                if(state=="partial_history"&&amount!=null)value="已知 "+value;
                bool valid=state=="ready"||state=="saved_snapshot";devices.Rows.Add(Str(row,"label")+(Convert.ToBoolean(Get(row,"is_local")??false)?"（本机）":""),value,ApiUsageRuntime.Percent(valid?Get(row,"share_percent"):null),ApiUsageRuntime.Percent(valid?Get(row,"budget_percent"):null),!valid||Get(row,"budget_usd")==null?"待三机同步":ApiUsageRuntime.Money(Get(row,"budget_usd")),status+"\n"+ApiUsageRuntime.When(Get(row,"updated_at")));
            }
            decimal used;bool known=decimal.TryParse(Str(p,"used_percent"),NumberStyles.Number,CultureInfo.InvariantCulture,out used);
            if(selectedRow>=0)devices.CurrentCell=devices.Rows[selectedRow].Cells[0];
            summary.Text=(complete?"设备API合计 ":"已同步API合计 ")+ApiUsageRuntime.Money(Get(p,"total_usd"))+"   ·   "+rows.Count+" / "+appliedExpected+" 台\n"+(known?"额度已用 "+used.ToString("0.##")+"% · 按 "+ApiUsageRuntime.Money(appliedBudget)+" 估算已用 "+ApiUsageRuntime.Money(appliedBudget*used/100):"等待当前额度")+(complete?"":" · 等待设备和截止时间对齐后分摊");RenderModels();
        }
        void RenderModels()
        {
            models.Rows.Clear();int selected=devices.CurrentRow==null?-1:devices.CurrentRow.Index;if(selected<0||selected>=rows.Count)return;
            foreach(var model in Map(Get(rows[selected],"models")))
            {
                var item=Map(model.Value);var a=Map(Get(item,"short"));var b=Map(Get(item,"long"));
                long input=Count(a,"input_tokens")+Count(b,"input_tokens"),cache=Count(a,"cached_input_tokens")+Count(b,"cached_input_tokens"),write=Count(a,"cache_write_input_tokens")+Count(b,"cache_write_input_tokens"),output=Count(a,"output_tokens")+Count(b,"output_tokens");
                models.Rows.Add(model.Key,(input-cache-write).ToString("N0"),cache.ToString("N0"),output.ToString("N0"),Get(item,"usd")==null?"待定价":ApiUsageRuntime.Money(Get(item,"usd")));
            }
        }
        static long Count(Dictionary<string,object> d,string key){long n;return long.TryParse(Str(d,key),out n)?n:0;}
    }

    internal static class ApiUsageUiValidation
    {
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static IEnumerable<Control> Walk(Control root){yield return root;foreach(Control c in root.Controls)foreach(Control nested in Walk(c))yield return nested;}
        static void Handles(Control root){foreach(Control control in Walk(root)){IntPtr handle=control.Handle;}root.PerformLayout();}
        internal static int Run(string root,string output)
        {
            Directory.CreateDirectory(output);var checks=new List<string>();
            try
            {
                string code=SyncConnectionCode.Encode("example/private-usage");
                Require(SyncConnectionCode.Repository(code)=="example/private-usage","connection code round trip");
                bool rejected=false;try{SyncConnectionCode.Repository(code.Substring(0,code.Length-1)+(code.EndsWith("0")?"1":"0"));}catch(FormatException){rejected=true;}Require(rejected,"connection code checksum rejects edits");
                Require(!code.Contains("token")&&!code.Contains("password"),"connection code contains no credentials");checks.Add("PASS connection code round trip, checksum and credential-free payload");
                using(var form=new ApiUsageForm(root))
                {
                    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-25000,-25000);Handles(form);
                    DataGridView devices=null,models=null;Button periods=null;foreach(Control control in Walk(form))
                    {
                        var grid=control as DataGridView;if(grid!=null){if(grid.Columns.Count==6)devices=grid;else if(grid.Columns.Count==5)models=grid;}
                        if(control is Button&&control.AccessibleName=="金额统计周期")periods=(Button)control;
                    }
                    Require(periods!=null&&periods.Text.Contains("周期"),"period selection is populated");checks.Add("PASS selected period "+periods.Text);
                    Require(devices!=null&&devices.Rows.Count==3,"API device table has three fixture devices");Require(models!=null&&models.Rows.Count>0,"model amount table populated");
                    Require(Convert.ToString(devices.Rows[0].Cells[1].Value)=="$1,277.53","API dollars rendered at expected precision");
                    decimal total=0;foreach(DataGridViewRow row in devices.Rows){string value=Convert.ToString(row.Cells[4].Value).TrimStart('$');decimal n;Require(decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out n),"budget value populated");total+=n;}
                    Require(total==170m,"displayed 200-dollar budget at 85 percent sums to 170 dollars");
                    PreviewHost.Render(form,Path.Combine(output,"api-amount-preview.png"));checks.Add("PASS native API table, model table and displayed budget sum");
                }
                using(var form=new NetworkForm(root,true))
                {
                    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-25000,-25000);Handles(form);
                    bool connect=false,copy=false;foreach(Control control in Walk(form)){if(control is Button&&control.Text=="连接并同步本机")connect=true;if(control is Button&&control.Text=="复制连接码")copy=true;}
                    Require(connect&&copy,"simple sync actions visible");PreviewHost.Render(form,Path.Combine(output,"sync-setup-preview.png"));checks.Add("PASS simple sync setup has Connect and Copy code actions");
                }
                File.WriteAllText(Path.Combine(output,"api-ui-checks.txt"),string.Join(Environment.NewLine,checks),Encoding.UTF8);return 0;
            }
            catch(Exception e){File.WriteAllText(Path.Combine(output,"api-ui-checks.txt"),string.Join(Environment.NewLine,checks)+Environment.NewLine+e,Encoding.UTF8);return 1;}
        }
    }
}
