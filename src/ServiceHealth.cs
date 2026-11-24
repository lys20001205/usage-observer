using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal sealed class OfficialStatus
    {
        public string State="unknown",Description="等待官方状态",Incident="",IncidentUrl="https://status.openai.com/",Error="";
        public DateTimeOffset Checked=DateTimeOffset.MinValue;
        public bool Stale {get{return Error.Length>0||Checked==DateTimeOffset.MinValue||Checked>DateTimeOffset.UtcNow.AddMinutes(1)||DateTimeOffset.UtcNow-Checked>TimeSpan.FromMinutes(5);}}
        public string Compact {get{return Stale?(Checked==DateTimeOffset.MinValue?"OpenAI 待查":"OpenAI 旧") : State=="operational"?"OpenAI 正常":State=="unknown"?"OpenAI 未知":State=="under_maintenance"?"OpenAI 维护":"OpenAI 异常";}}
        public bool Unhealthy {get{return !Stale&&State!="operational"&&State!="unknown";}}
    }
    internal sealed class SpeedGroup
    {
        public string Key="",Session="",Title="",Model="",Tier="unknown",ConfiguredTier="unknown";
        public bool ActiveTurn;
        public Dictionary<string,object> Recent=new Dictionary<string,object>(),History=new Dictionary<string,object>();
        public Dictionary<string,object> Window(int seconds){return seconds==1800?History:Recent;}
        public string TierLabel {get{return Tier=="standard"?"Standard":Tier=="fast"?"Fast":Tier=="ultrafast"?"Ultrafast":"未知";}}
        public string ConfiguredLabel {get{return ConfiguredTier=="standard"?"Standard":ConfiguredTier=="fast"?"Fast":ConfiguredTier=="ultrafast"?"Ultrafast":"未知";}}
    }
    internal sealed class SpeedStatus
    {
        public double? Effective,Visible;
        public int Samples,Recorded,Matched;
        public string Error="",Coverage="unavailable",LastResponse="",Models="";
        public string SelectedKey="",Title="",Tier="unknown",ConfiguredTier="unknown";
        public int WindowSeconds=300;
        public readonly List<SpeedGroup> Groups=new List<SpeedGroup>();
        public DateTimeOffset Checked=DateTimeOffset.MinValue;
        public bool Stale {get{return Error.Length>0||Checked==DateTimeOffset.MinValue||Checked>DateTimeOffset.UtcNow.AddMinutes(1)||DateTimeOffset.UtcNow-Checked>TimeSpan.FromSeconds(60);}}
        internal static string Number(double value){return (value>=1000000?value/1000000:value>=1000?value/1000:value).ToString(value>=1000?"0.#":"0.0",System.Globalization.CultureInfo.InvariantCulture)+(value>=1000000?"M":value>=1000?"K":"");}
        internal static double? Metric(Dictionary<string,object> d,string key)
        {
            object v=ServiceHealth.Get(d,key);double n;
            return v!=null&&double.TryParse(Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out n)&&!double.IsInfinity(n)&&!double.IsNaN(n)&&n>=0?(double?)n:null;
        }
        public void Select(string key,int seconds)
        {
            WindowSeconds=seconds==1800?1800:300;
            if(Groups.Count==0)return;
            SpeedGroup selected=null;
            if(!string.IsNullOrEmpty(key))selected=Groups.Find(delegate(SpeedGroup g){return g.Key==key;});
            else selected=Groups.Find(delegate(SpeedGroup g){return (Metric(g.Window(WindowSeconds),"samples")??0)>0;});
            Effective=null;Visible=null;Samples=0;LastResponse="";SelectedKey=key??"";Title="选定聊天暂无样本";Tier="unknown";ConfiguredTier="unknown";
            if(selected==null)return;
            var d=selected.Window(WindowSeconds);SelectedKey=selected.Key;Title=selected.Title;Tier=selected.Tier;
            Effective=Metric(d,"effective_tps");Samples=(int)(Metric(d,"samples")??0);LastResponse=ServiceHealth.Str(d,"last_response_at");
            ConfiguredTier=selected.ConfiguredTier;
            Models=selected.Model+" · "+selected.TierLabel+(selected.ActiveTurn&&selected.Tier!=selected.ConfiguredTier?" · 下轮 "+selected.ConfiguredLabel:"");
        }
        public string Compact {get{return Effective.HasValue?(Stale?"旧 ":"≈")+Number(Effective.Value)+" t/s":"待 t/s";}}
        public string Detail {get{return "近"+(WindowSeconds/60)+"分钟 · "+Title+"\r\n"+Models+" · "+Compact+"；样本 "+Samples+"；覆盖 "+Matched+" / "+Recorded+"\r\n最近响应："+MachineTime.Timestamp(LastResponse)+"\r\n请求耗时含首字等待与传输，扣除显式工具执行；按聊天和档位独立统计。"+(Error.Length>0?"\r\n速度读取失败："+Error:"");}}
    }
    internal sealed class ServiceHealth : IDisposable
    {
        public OfficialStatus Status=new OfficialStatus();
        public SpeedStatus Speed=new SpeedStatus();
        public bool Alerts=true;
        public event Action<string> Notification;
        readonly string root,backend;
        public string SelectedSpeedKey="";
        public int SpeedWindowSeconds=300;
        bool statusBusy,speedBusy,disposed;
        DateTimeOffset nextStatus=DateTimeOffset.MinValue,nextSpeed=DateTimeOffset.MinValue;
        string lastNotified="";
        public ServiceHealth(string data,string source)
        {
            root=data;backend=source;
            try
            {
                var settings=Map(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root,"speed-settings.json"),Encoding.UTF8)));
                SelectedSpeedKey=Str(settings,"selected_key");SpeedWindowSeconds=Convert.ToInt32(Get(settings,"window_seconds")??300)==1800?1800:300;
            }catch{}
            try
            {
                var saved=Map(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root,"service-health.json"),Encoding.UTF8)));
                Status=ParseSaved(Map(Get(saved,"status")));var alerts=Get(saved,"alerts");if(alerts!=null)Alerts=Convert.ToBoolean(alerts);lastNotified=Str(saved,"last_notified");
            }catch{}
        }
        internal static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        internal static object Get(Dictionary<string,object> d,string key){object v;return d.TryGetValue(key,out v)?v:null;}
        internal static string Str(Dictionary<string,object> d,string key){return Convert.ToString(Get(d,key))??"";}
        static DateTimeOffset Time(string s){DateTimeOffset v;return DateTimeOffset.TryParse(s,out v)?v:DateTimeOffset.MinValue;}
        static OfficialStatus ParseSaved(Dictionary<string,object> d){return new OfficialStatus{State=Str(d,"state"),Description=Str(d,"description"),Incident=Str(d,"incident"),IncidentUrl=Str(d,"incident_url"),Error=Str(d,"error"),Checked=Time(Str(d,"checked_at"))};}
        static bool Relevant(string name){return name.IndexOf("codex",StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf("ChatGPT Work",StringComparison.OrdinalIgnoreCase)>=0;}
        static int Severity(string state){return state=="major_outage"?4:state=="partial_outage"?3:state=="degraded_performance"?2:state=="under_maintenance"?1:state=="operational"?0:-1;}
        internal static OfficialStatus ParseOfficial(string json,DateTimeOffset now)
        {
            var d=Map(new JavaScriptSerializer{MaxJsonLength=1024*1024}.DeserializeObject(json));
            var result=new OfficialStatus{Checked=now};int worst=-1;bool unknown=false;var relevantIds=new HashSet<string>();
            var components=Get(d,"components") as IEnumerable;
            if(components!=null)foreach(object value in components)
            {
                var c=Map(value);if(!Relevant(Str(c,"name")))continue;relevantIds.Add(Str(c,"id"));string state=Str(c,"status");int level=Severity(state);
                if(level<0)unknown=true;
                if(level>worst){worst=level;result.State=state;}
            }
            if(worst<0||unknown&&worst==0){result.State="unknown";result.Description="官方数据未能确认 Codex / Work 状态";}
            else result.Description=result.State=="operational"?"Codex / ChatGPT Work 官方组件正常":"Codex / ChatGPT Work 官方组件："+result.State;
            var incidents=Get(d,"incidents") as IEnumerable;
            if(incidents!=null)foreach(object value in incidents)
            {
                var c=Map(value);string state=Str(c,"status");if(state=="resolved"||state=="postmortem")continue;
                bool relevant=Relevant(Str(c,"name"));var affected=Get(c,"components") as IEnumerable;
                if(affected!=null)foreach(object component in affected)if(relevantIds.Contains(Str(Map(component),"id")))relevant=true;
                if(!relevant)continue;
                result.Incident=Str(c,"name");string id=Str(c,"id");
                if(System.Text.RegularExpressions.Regex.IsMatch(id,"^[A-Za-z0-9_-]{5,80}$"))result.IncidentUrl="https://status.openai.com/incidents/"+id;
                if(result.State=="operational"||result.State=="unknown")result.State="degraded_performance";
                result.Description=result.Incident+" ("+state+")";break;
            }
            return result;
        }
        internal static SpeedStatus ParseSpeed(string json,DateTimeOffset now)
        {
            var d=Map(new JavaScriptSerializer{MaxJsonLength=2*1024*1024}.DeserializeObject(json));var result=new SpeedStatus{Checked=Time(Str(d,"sampled_at")),Error=Str(d,"error"),Coverage=Str(d,"coverage"),LastResponse=Str(d,"last_response_at")};
            if(result.Checked==DateTimeOffset.MinValue)result.Checked=now;
            object value=Get(d,"effective_tps");double number;if(value!=null&&double.TryParse(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out number)&&!double.IsInfinity(number)&&!double.IsNaN(number)&&number>=0)result.Effective=number;
            value=Get(d,"visible_equivalent_tps");if(value!=null)result.Visible=Convert.ToDouble(value);
            result.Samples=Convert.ToInt32(Get(d,"samples")??0);result.Recorded=Convert.ToInt32(Get(d,"recorded_responses")??0);result.Matched=Convert.ToInt32(Get(d,"matched_responses")??0);
            foreach(var item in Map(Get(d,"models")))result.Models+=item.Key+" · "+Str(Map(item.Value),"effective_tps")+" t/s\r\n";
            var groups=Get(d,"groups") as IEnumerable;
            if(groups!=null)foreach(object item in groups)
            {
                var g=Map(item);result.Groups.Add(new SpeedGroup{Key=Str(g,"key"),Session=Str(g,"session_id"),Title=Str(g,"title"),Model=Str(g,"model"),Tier=Str(g,"speed"),ConfiguredTier=Str(g,"configured_speed").Length>0?Str(g,"configured_speed"):Str(g,"speed"),ActiveTurn=Convert.ToBoolean(Get(g,"active_turn")??false),Recent=Map(Get(g,"recent")),History=Map(Get(g,"history"))});
            }
            if(result.Groups.Count>0)result.Select("",300);
            if(result.Effective.HasValue&&result.Samples<=0){result.Effective=null;result.Error="invalid_sample_count";}
            return result;
        }
        internal static string ChangeNotice(OfficialStatus previous,OfficialStatus next,string notified)
        {
            if(next.Stale||next.State=="unknown")return null;
            string key=next.State+"|"+next.Incident;if(key==notified)return null;
            if(next.State=="operational")return previous.Checked!=DateTimeOffset.MinValue&&previous.State!="operational"&&previous.State!="unknown"?"OpenAI 官方状态已恢复正常。":null;
            return next.Description;
        }
        static string Download()
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create("https://status.openai.com/api/v2/summary.json");
            request.Timeout=10000;request.ReadWriteTimeout=10000;request.Method="GET";request.AllowAutoRedirect=false;request.UserAgent="UsageObserver/"+Updates.VersionText+" public-status";request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            using(var response=(HttpWebResponse)request.GetResponse())using(var stream=response.GetResponseStream())using(var reader=new StreamReader(stream,Encoding.UTF8))
            {char[] buffer=new char[4096];var text=new StringBuilder();int n;while((n=reader.Read(buffer,0,buffer.Length))>0){text.Append(buffer,0,n);if(text.Length>1024*1024)throw new IOException("Official status response exceeds limit");}return text.ToString();}
        }
        string ReadSpeed()
        {
            string home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            try{var d=Map(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(backend,"status.json"),Encoding.UTF8)));string current=Str(Map(Get(Map(Get(d,"health")),"A")),"root");if(Directory.Exists(current))home=current;}catch{}
            string app=AppDomain.CurrentDomain.BaseDirectory;
            var start=new ProcessStartInfo(Path.Combine(app,"runtime","python","python.exe"),Quote(Path.Combine(app,"speed_probe.py"))+" --database "+Quote(Path.Combine(backend,"records.sqlite"))+" --codex-home "+Quote(home)+" --cache "+Quote(Path.Combine(root,"speed-cache.json"))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(start))
            {
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(15000)){process.Kill();throw new TimeoutException("Speed probe timeout");}
                if(process.ExitCode!=0)throw new IOException("Speed probe failed ("+process.ExitCode+")");
                if(output.Result.Length>2*1024*1024)throw new IOException("Speed result exceeds limit");return output.Result;
            }
        }
        static string Quote(string path){if(path.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Invalid path");return "\""+path.TrimEnd('\\')+"\"";}
        public void Tick()
        {
            if(disposed)return;var now=DateTimeOffset.UtcNow;
            if(!statusBusy&&now>=nextStatus)PollStatus();
            if(!speedBusy&&now>=nextSpeed)PollSpeed();
        }
        async void PollStatus()
        {
            statusBusy=true;
            try
            {
                string json=await Task.Run((Func<string>)Download);if(disposed)return;
                var next=ParseOfficial(json,DateTimeOffset.UtcNow);string message=ChangeNotice(Status,next,lastNotified);Status=next;
                if(Alerts&&message!=null){lastNotified=next.State+"|"+next.Incident;var handler=Notification;if(handler!=null)handler(message);}
            }
            catch(Exception error){if(!disposed)Status.Error=error.GetType().Name;}
            finally{statusBusy=false;nextStatus=DateTimeOffset.UtcNow.AddSeconds(60);if(!disposed)Save();}
        }
        async void PollSpeed()
        {
            speedBusy=true;
            try{string json=await Task.Run((Func<string>)ReadSpeed);if(!disposed){var next=ParseSpeed(json,DateTimeOffset.UtcNow);next.Select(SelectedSpeedKey,SpeedWindowSeconds);if(next.Error.Length>0)Speed.Error=next.Error;else Speed=next;}}
            catch(Exception error){if(!disposed)Speed.Error=error.GetType().Name;}
            finally{speedBusy=false;nextSpeed=DateTimeOffset.UtcNow.AddSeconds(15);if(!disposed)Save();}
        }
        public void SelectSpeed(string key,int seconds)
        {
            SelectedSpeedKey=key??"";SpeedWindowSeconds=seconds==1800?1800:300;Speed.Select(SelectedSpeedKey,SpeedWindowSeconds);
            try
            {
                string path=Path.Combine(root,"speed-settings.json"),temp=path+".tmp";
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new{selected_key=SelectedSpeedKey,window_seconds=SpeedWindowSeconds}),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }catch(Exception e){BackgroundLifetime.Log("speed_settings_save_failed "+e.GetType().Name);}
        }
        public void Save()
        {
            try
            {
                string path=Path.Combine(root,"service-health.json"),temp=path+".tmp";
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new{alerts=Alerts,last_notified=lastNotified,status=new{state=Status.State,description=Status.Description,incident=Status.Incident,incident_url=Status.IncidentUrl,error=Status.Error,checked_at=Status.Checked.ToString("o")},speed=new{effective_tps=Speed.Effective,samples=Speed.Samples,coverage=Speed.Coverage,error=Speed.Error,checked_at=Speed.Checked.ToString("o")}}),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }catch(Exception error){BackgroundLifetime.Log("service_health_save_failed "+error.GetType().Name);}
        }
        public string Detail {get{return Status.Compact+"\r\n"+Status.Description+"\r\n官方状态检查："+(Status.Checked==DateTimeOffset.MinValue?"等待查询":MachineTime.DescribeFull(Status.Checked))+(Status.Error.Length>0?"\r\n查询失败："+Status.Error:"")+"\r\n\r\n"+Speed.Detail+"\r\n\r\n官方状态每60秒检查；速度每15秒刷新。官方状态是服务整体报告，不能单独诊断你的网络。";}}
        public void Dispose(){disposed=true;}
        internal static void SelfTest()
        {
            var now=DateTimeOffset.UtcNow;
            const string normal="{\"components\":[{\"id\":\"c\",\"name\":\"Codex in ChatGPT Desktop\",\"status\":\"operational\"}],\"incidents\":[]}";
            var good=ParseOfficial(normal,now);if(good.State!="operational"||good.Stale)throw new Exception("official operational parsing");
            var bad=ParseOfficial(normal.Replace("operational","degraded_performance"),now);if(!bad.Unhealthy||ChangeNotice(good,bad,"")==null||ChangeNotice(good,bad,"degraded_performance|")!=null)throw new Exception("status deterioration and deduplication");
            if(ChangeNotice(bad,good,"")==null)throw new Exception("recovery notification");
            bad.Error="timeout";if(!bad.Stale||ChangeNotice(good,bad,"")!=null||bad.Compact.IndexOf("旧")<0)throw new Exception("failed fetch retains stale status");
            if(ParseOfficial("{}",now).State!="unknown"||ParseOfficial(normal.Replace("Codex in ChatGPT Desktop","Compliance API"),now).State!="unknown")throw new Exception("unrelated components cannot prove Codex health");
            if(ParseOfficial(normal.Replace("operational","new_state"),now).State!="unknown")throw new Exception("unknown official enum");
            var incident=ParseOfficial(normal.Replace("[]","[{\"id\":\"incident_123\",\"name\":\"Codex degraded\",\"status\":\"identified\"}]"),now);if(!incident.Unhealthy||!incident.IncidentUrl.EndsWith("incident_123"))throw new Exception("relevant incident overrides nominal component");
            var speed=ParseSpeed("{\"effective_tps\":5.125,\"samples\":14,\"recorded_responses\":14,\"matched_responses\":14}",now);if(speed.Compact!="≈5.1 t/s")throw new Exception("speed formatting");
            if(ParseSpeed("{\"effective_tps\":0,\"samples\":0}",now).Effective.HasValue)throw new Exception("missing samples are not zero speed");
            speed.Checked=now.AddMinutes(-2);if(!speed.Stale||!speed.Compact.StartsWith("旧"))throw new Exception("speed freshness");
            var grouped=ParseSpeed("{\"groups\":[{\"key\":\"chat|model|ultrafast\",\"title\":\"Test chat\",\"model\":\"gpt-6.1-sol\",\"speed\":\"ultrafast\",\"recent\":{\"effective_tps\":800,\"samples\":2},\"history\":{\"effective_tps\":650,\"samples\":8}}]}",now);
            if(grouped.Effective!=800||grouped.Title!="Test chat")throw new Exception("grouped speed above 300");
            grouped.Select("chat|model|ultrafast",1800);if(grouped.Effective!=650||grouped.Samples!=8)throw new Exception("independent speed windows");
            grouped.Select("missing",300);if(grouped.Effective.HasValue)throw new Exception("pinned missing chat must not display another chat");
        }
    }
    internal sealed class SpeedChoice : Button
    {
        public readonly List<object> Items=new List<object>();
        int selected=-1;
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get{return selected;}
            set
            {
                if(selected==value)return;selected=value;
                Text=Convert.ToString(SelectedItem)+"  ▼";
                var handler=SelectedIndexChanged;if(handler!=null)handler(this,EventArgs.Empty);
            }
        }
        public object SelectedItem {get{return selected>=0&&selected<Items.Count?Items[selected]:null;}}
        public SpeedChoice(){TextAlign=ContentAlignment.MiddleLeft;Font=new Font("Microsoft YaHei UI",9);}
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);var menu=new ContextMenuStrip();
            for(int i=0;i<Items.Count;i++)
            {
                int index=i;var item=new ToolStripMenuItem(Convert.ToString(Items[i])){Checked=i==SelectedIndex};
                item.Click+=delegate{SelectedIndex=index;};menu.Items.Add(item);
            }
            menu.Closed+=delegate{menu.Dispose();};menu.Show(this,new Point(0,Height));
        }
    }
    internal sealed class ServiceHealthForm : Form
    {
        readonly ServiceHealth health;
        readonly TextBox text;
        readonly TextBox filter;
        readonly SpeedChoice window,tierFilter;
        readonly DataGridView grid;
        readonly Timer timer=new Timer{Interval=2000};
        public ServiceHealthForm(ServiceHealth value)
        {
            health=value;Text="OpenAI 状态与分组速度";AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(1000,620);MinimumSize=new Size(840,540);ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
            text=new TextBox{Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,Font=new Font("Microsoft YaHei UI",10),Bounds=new Rectangle(20,16,960,128),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,BackColor=VisualTheme.Canvas,ForeColor=VisualTheme.Ink};Controls.Add(text);
            Controls.Add(new Label{Text="聊天",Bounds=new Rectangle(20,154,60,25)});
            filter=new TextBox{Bounds=new Rectangle(88,151,310,28)};Controls.Add(filter);
            tierFilter=new SpeedChoice{Bounds=new Rectangle(412,151,170,28)};tierFilter.Items.AddRange(new object[]{"全部档位","Standard","Fast","Ultrafast","未知"});tierFilter.SelectedIndex=0;Controls.Add(tierFilter);
            window=new SpeedChoice{Bounds=new Rectangle(596,151,130,28)};window.Items.AddRange(new object[]{"最近 5 分钟","最近 30 分钟"});window.SelectedIndex=health.SpeedWindowSeconds==1800?1:0;Controls.Add(window);
            grid=new DataGridView{Bounds=new Rectangle(20,192,960,322),Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=VisualTheme.Canvas,BorderStyle=BorderStyle.None,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
            grid.DefaultCellStyle.Font=new Font("Microsoft YaHei UI",9);grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            grid.Columns.Add("chat","聊天");grid.Columns[0].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.Columns[0].MinimumWidth=160;
            grid.Columns.Add("model","模型");grid.Columns[1].Width=110;
            grid.Columns.Add("tier","样本档位");grid.Columns[2].Width=85;
            grid.Columns.Add("configured","当前设置");grid.Columns[3].Width=105;
            grid.Columns.Add("rate","平均 t/s");grid.Columns[4].Width=82;
            grid.Columns.Add("last","最近 t/s");grid.Columns[5].Width=98;
            grid.Columns.Add("count","样本");grid.Columns[6].Width=52;
            grid.Columns.Add("seconds","平均耗时(s)");grid.Columns[7].Width=104;Controls.Add(grid);
            var pin=new Button{Text="选定行固定到浮窗",Bounds=new Rectangle(20,534,165,32),Anchor=AnchorStyles.Bottom|AnchorStyles.Left};
            pin.Click+=delegate{if(grid.SelectedRows.Count>0){health.SelectSpeed(Convert.ToString(grid.SelectedRows[0].Tag),window.SelectedIndex==1?1800:300);RefreshText();}};Controls.Add(pin);
            var follow=new Button{Text="跟随最近聊天",Bounds=new Rectangle(197,534,142,32),Anchor=AnchorStyles.Bottom|AnchorStyles.Left};
            follow.Click+=delegate{health.SelectSpeed("",window.SelectedIndex==1?1800:300);RefreshText();};Controls.Add(follow);
            var open=new Button{Text="查看官方公告",Bounds=new Rectangle(351,534,142,32),Anchor=AnchorStyles.Bottom|AnchorStyles.Left};open.Click+=delegate{string url=health.Status.IncidentUrl;if(!url.StartsWith("https://status.openai.com/",StringComparison.Ordinal))url="https://status.openai.com/";Process.Start(new ProcessStartInfo(url){UseShellExecute=true});};Controls.Add(open);
            Controls.Add(new Label{Text="本轮保留启动时的档位；运行中修改从下一轮生效。\r\n请求耗时包含首字等待与传输，已扣除可识别的工具执行。",Bounds=new Rectangle(20,576,960,42),Anchor=AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right,ForeColor=VisualTheme.Muted});
            filter.TextChanged+=delegate{RefreshText();};tierFilter.SelectedIndexChanged+=delegate{RefreshText();};
            window.SelectedIndexChanged+=delegate{health.SelectSpeed(health.SelectedSpeedKey,window.SelectedIndex==1?1800:300);RefreshText();};
            timer.Tick+=delegate{RefreshText();};timer.Start();RefreshText();VisualTheme.Track(this);
        }
        void RefreshText()
        {
            string summary=health.Speed.Detail;int cutoff=summary.IndexOf("\r\n请求耗时",StringComparison.Ordinal);if(cutoff>=0)summary=summary.Substring(0,cutoff);
            string value=health.Status.Compact+" · "+health.Status.Description+"\r\n"+(string.IsNullOrEmpty(health.SelectedSpeedKey)?"浮窗跟随最近聊天":"浮窗已固定")+"\r\n"+summary;
            if(text.Text!=value)text.Text=value;
            string selected=grid.SelectedRows.Count>0?Convert.ToString(grid.SelectedRows[0].Tag):"";
            int scroll=grid.FirstDisplayedScrollingRowIndex;
            grid.Rows.Clear();
            foreach(var g in health.Speed.Groups)
            {
                if(filter.Text.Length>0&&g.Title.IndexOf(filter.Text,StringComparison.CurrentCultureIgnoreCase)<0&&g.Session.IndexOf(filter.Text,StringComparison.OrdinalIgnoreCase)<0)continue;
                if(tierFilter.SelectedIndex>0&&Convert.ToString(tierFilter.SelectedItem)!=g.TierLabel)continue;
                var d=g.Window(window.SelectedIndex==1?1800:300);var rate=SpeedStatus.Metric(d,"effective_tps");var last=SpeedStatus.Metric(d,"last_tps");double count=SpeedStatus.Metric(d,"samples")??0,seconds=SpeedStatus.Metric(d,"seconds")??0;
                int index=grid.Rows.Add(g.Title,g.Model,g.TierLabel,g.ConfiguredLabel+(g.ActiveTurn&&g.Tier!=g.ConfiguredTier?"（下轮）":""),rate.HasValue?SpeedStatus.Number(rate.Value):"—",last.HasValue?SpeedStatus.Number(last.Value):"—",count.ToString("0"),count>0?(seconds/count).ToString("0.00"):"—");
                grid.Rows[index].Tag=g.Key;grid.Rows[index].Cells[0].ToolTipText=g.Title+"\r\n"+g.Session;
                if(g.Key==selected)grid.Rows[index].Selected=true;
            }
            if(scroll>=0&&scroll<grid.Rows.Count)grid.FirstDisplayedScrollingRowIndex=scroll;
        }
        protected override void Dispose(bool disposing){if(disposing){timer.Stop();timer.Dispose();}base.Dispose(disposing);}
    }
}
