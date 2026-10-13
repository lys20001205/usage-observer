using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal sealed class ResetNotice
    {
        public string Id, State, Text, SourceUrl, Estimate, Evidence;
        public string PublishedAt="";
        [ScriptIgnore] public DateTimeOffset Published { get {DateTimeOffset t;return DateTimeOffset.TryParse(PublishedAt,out t)?t:DateTimeOffset.MinValue;} set {PublishedAt=value.ToString("o");} }
        public bool Verified;
        // Only a verified primary post with an explicit dated, zoned reset timestamp
        // can be promoted. Feed estimates and EOD/relative phrases never qualify.
        [ScriptIgnore] public DateTimeOffset? OfficialTime
        {
            get
            {
                if(!Verified||State!="scheduled"||!ResetMonitor.SourceAllowed(SourceUrl))return null;
                string text=Text??"";
                if(Regex.IsMatch(text,@"\b(approx|approximately|around|estimate|estimated|maybe|might|between)\b",RegexOptions.IgnoreCase))return null;
                if(Regex.Matches(text,@"\d{4}-\d{2}-\d{2}(?:T| )\d{2}:\d{2}").Count!=1)return null;
                var matches=Regex.Matches(text,@"\breset\b[^.!?\r\n]{0,90}?\b(?:at|by|for)\s+(?<time>\d{4}-\d{2}-\d{2}(?:T| )\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:\d{2}|\s+UTC))(?=$|[\s.,!])",RegexOptions.IgnoreCase);
                DateTimeOffset t;
                if(matches.Count!=1||!DateTimeOffset.TryParse(matches[0].Groups["time"].Value.Replace(" UTC","Z").Replace(" utc","Z"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out t))return null;
                return t;
            }
        }
        [ScriptIgnore] public string ShortTiming
        {
            get
            {
                var exact=OfficialTime;DateTimeOffset t;
                if(exact.HasValue)return (Regex.IsMatch(Text??"",@"\bby\s+\d{4}-",RegexOptions.IgnoreCase)?"最晚 ":"")+exact.Value.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm");
                return DateTimeOffset.TryParse(Estimate,out t)?t.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm"):"等待明确时间";
            }
        }
        public string Fingerprint
        {
            get { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Id+"|"+State+"|"+Estimate+"|"+Text))).Replace("-",""); }
        }
        public string StatusText { get { return State=="completed"?"已确认完成（公共公告）":State=="rolling"?"开始传播（公共公告）":State=="scheduled"?"已预告 · 尚未确认执行":"状态待核对（社区记录）"; } }
        public string Timing
        {
            get
            {
                DateTimeOffset t;
                if(OfficialTime.HasValue)return "高置信预告（原帖明确）："+ShortTiming+" 北京时间；执行仍待确认";
                return DateTimeOffset.TryParse(Estimate,out t)?"社区预计参考："+ResetMonitor.Beijing(t)+"（非官方精确时间）":"原帖未给出可确认的精确时间";
            }
        }
    }
    internal sealed class ResetCache
    {
        public bool Enabled=true, Stale=true;
        public string Error="", SourceChecked="";
        public string CheckedAt="";
        [ScriptIgnore] public DateTimeOffset Checked { get {DateTimeOffset t;return DateTimeOffset.TryParse(CheckedAt,out t)?t:DateTimeOffset.MinValue;} set {CheckedAt=value.ToString("o");} }
        public ResetNotice Scheduled, Latest;
        public List<string> Seen=new List<string>();
        public Dictionary<string,string> LastById=new Dictionary<string,string>();
    }
    internal sealed class ResetMonitor : IDisposable
    {
        public const string Feed="https://whennextreset.com/api/v1/status";
        readonly string root;
        public ResetCache Cache;
        public bool Busy;
        bool disposed;
        DateTimeOffset next;
        public event Action Changed;
        public event Action<string> Notification;
        public ResetMonitor(string directory)
        {
            root=directory;
            try { Cache=new JavaScriptSerializer().Deserialize<ResetCache>(File.ReadAllText(Path.Combine(root,"reset-announcements.json"),Encoding.UTF8)); } catch { }
            if(Cache==null)Cache=new ResetCache();
            if(Cache.Seen==null)Cache.Seen=new List<string>();
            if(Cache.LastById==null)Cache.LastById=new Dictionary<string,string>();
            next=Cache.Checked.AddHours(1);
        }
        internal static string Beijing(DateTimeOffset t) { return t.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm")+" 北京时间"; }
        internal static Dictionary<string,object> Map(object x) { return x as Dictionary<string,object>??new Dictionary<string,object>(); }
        internal static object Get(Dictionary<string,object> d,string key) { object v;return d!=null&&d.TryGetValue(key,out v)?v:null; }
        static string Str(Dictionary<string,object> d,string key) { return Convert.ToString(Get(d,key)); }
        internal static bool SourceAllowed(string url)
        {
            return Regex.IsMatch(url??"",@"^https://(?:x\.com|twitter\.com)/thsottiaux/status/[0-9]+$",RegexOptions.IgnoreCase);
        }
        internal static ResetNotice ParseNotice(object input, bool scheduled)
        {
            var d=Map(input);var source=Map(Get(d,"source"));
            if(Str(d,"reset_type")!="regular"||!SourceAllowed(Str(source,"url")))return null;
            DateTimeOffset published;
            if(!DateTimeOffset.TryParse(Str(d,"announced_at"),out published))return null;
            return new ResetNotice {Id=Str(d,"id"),State=scheduled?"scheduled":"unknown",Text=Str(d,"text"),SourceUrl=Str(source,"url"),Published=published,Estimate=scheduled?Str(d,"scheduled_for"):"",Evidence="等待核对原帖"};
        }
        internal static string Classify(string text, bool scheduled)
        {
            if(!Regex.IsMatch(text??"",@"\breset[s]?\b",RegexOptions.IgnoreCase))return "unknown";
            if(Regex.IsMatch(text,@"\b(banked|reset card|reset credit|not|never|haven't|hasn't|isn't|no reset)\b",RegexOptions.IgnoreCase))return "unknown";
            if(Regex.IsMatch(text,@"\b(resets? (?:all )?propagated|resets? (?:has|have) been (?:applied|completed|executed)|resets? (?:is|are) (?:complete|done)|resets? completed)\b",RegexOptions.IgnoreCase))return "completed";
            if(Regex.IsMatch(text,@"\bput the reset in the bag\.\s*all propagated\b",RegexOptions.IgnoreCase))return "completed";
            if(Regex.IsMatch(text,@"\b(rolling out|propagating|started rolling)\b",RegexOptions.IgnoreCase))return "rolling";
            if(scheduled && Regex.IsMatch(text,@"\b(by|tomorrow|today|tonight|will|scheduled|landing|EOD)\b",RegexOptions.IgnoreCase))return "scheduled";
            return "unknown";
        }
        static string Download(string url)
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(url);request.Method="GET";request.Timeout=12000;request.ReadWriteTimeout=12000;request.UserAgent="UsageObserver/1.0.4.1 public-reset-monitor";request.AllowAutoRedirect=true;request.MaximumAutomaticRedirections=3;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            // Public requests carry no Codex/DSH headers, tokens or account identifiers.
            using(var response=(HttpWebResponse)request.GetResponse())
            using(var stream=response.GetResponseStream())
            using(var reader=new StreamReader(stream,Encoding.UTF8))
            {if(response.StatusCode!=HttpStatusCode.OK||response.ResponseUri.Scheme!="https"||!Regex.IsMatch(response.ResponseUri.Host,@"^(whennextreset\.com|www\.whennextreset\.com|publish\.twitter\.com|publish\.x\.com)$",RegexOptions.IgnoreCase))throw new IOException("公告响应来源不符合预期");var text=new StringBuilder();char[] buffer=new char[4096];int n;while((n=reader.Read(buffer,0,buffer.Length))>0){text.Append(buffer,0,n);if(text.Length>1024*1024)throw new IOException("公告响应过大");}if(text.Length==0)throw new IOException("公告响应为空");return text.ToString();}
        }
        void Verify(ResetNotice notice, bool scheduled, ResetNotice old)
        {
            if(notice==null)return;
            if(old!=null&&old.Verified&&old.SourceUrl==notice.SourceUrl&&old.Text==notice.Text)
            {notice.Verified=true;notice.State=old.State;notice.Evidence=old.Evidence;return;}
            try
            {
                var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Download("https://publish.twitter.com/oembed?url="+Uri.EscapeDataString(notice.SourceUrl)+"&omit_script=true"));
                string author=Str(json,"author_url").TrimEnd('/');
                if(author!="https://x.com/thsottiaux"&&author!="https://twitter.com/thsottiaux")throw new IOException("原帖作者未通过核对");
                var p=Regex.Match(Str(json,"html"),@"<p\b[^>]*>(.*?)</p>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
                if(!p.Success)throw new IOException("原帖正文不可用");
                string text=WebUtility.HtmlDecode(Regex.Replace(p.Groups[1].Value,@"<[^>]+>"," ")).Trim();
                if(text.Length>2000)throw new IOException("原帖正文过长");
                notice.Text=text;notice.State=Classify(text,scheduled);notice.Verified=notice.State!="unknown";notice.Evidence=notice.Verified?"已通过 X 官方 oEmbed 核对作者与正文":"原帖措辞不足以确认状态";
            }
            catch(Exception e) {notice.Verified=false;notice.Evidence="原帖暂时无法核对；仅显示社区记录（"+e.GetType().Name+": "+e.Message+"）";File.AppendAllText(Path.Combine(root,"reset-errors.log"),DateTimeOffset.Now.ToString("o")+" "+e+Environment.NewLine,Encoding.UTF8);}
        }
        ResetCache Fetch()
        {
            var response=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Download(Feed));
            var fresh=Map(Get(response,"freshness"));var data=Get(response,"data")==null?response:Map(Get(response,"data"));
            if(!data.ContainsKey("latest_reset")||!response.ContainsKey("freshness"))throw new IOException("公告接口格式发生变化");
            DateTimeOffset sourceTime;bool freshTime=DateTimeOffset.TryParse(Str(fresh,"last_success_at"),out sourceTime)&&DateTimeOffset.UtcNow-sourceTime<TimeSpan.FromHours(2)&&sourceTime<DateTimeOffset.UtcNow.AddMinutes(5);
            var result=new ResetCache {Enabled=Cache.Enabled,Checked=DateTimeOffset.UtcNow,Stale=!freshTime||Get(fresh,"stale")==null||Convert.ToBoolean(Get(fresh,"stale")),SourceChecked=Str(fresh,"last_success_at"),Scheduled=ParseNotice(Get(data,"scheduled_reset"),true),Latest=ParseNotice(Get(data,"latest_reset"),false)};
            Verify(result.Scheduled,true,Cache.Scheduled);Verify(result.Latest,false,Cache.Latest);
            return result;
        }
        public ResetNotice Active
        {
            get
            {
                var s=Cache.Scheduled;
                if(s==null||s.State!="scheduled"||DateTimeOffset.UtcNow-s.Published>TimeSpan.FromHours(72)||s.Published>DateTimeOffset.UtcNow.AddMinutes(5))return null;
                if(Cache.Latest!=null&&Cache.Latest.Verified&&Cache.Latest.State=="completed"&&Cache.Latest.Published>=s.Published)return null;
                return s;
            }
        }
        public ResetNotice Current
        {
            get
            {
                var s=Cache.Scheduled;var latest=Cache.Latest;
                // A rollout may still occupy the schedule slot while the latest slot
                // contains an earlier completed reset. Details follow the newest post.
                return s!=null&&(latest==null||s.Published>latest.Published)?s:latest;
            }
        }
        public string Compact { get { return Active==null?"":"全局重置有预告"+(Cache.Stale?"（上次消息）":""); } }
        public string Tooltip
        {
            get { var s=Active;return s==null?"全局重置：暂无有效预告":Compact+"\n"+s.StatusText+"\n"+s.Timing+"\n"+(s.Verified?"原帖已核对":"原帖待核对"); }
        }
        public void Enable(bool enabled) { Cache.Enabled=enabled;Save();ChangedNow();if(enabled)next=DateTimeOffset.MinValue; }
        public void Tick() { if(Cache.Enabled&&!Busy&&DateTimeOffset.UtcNow>=next)Check(); }
        public async void Check()
        {
            if(Busy||disposed)return;Busy=true;ChangedNow();
            try
            {
                var fetched=await Task.Run((Func<ResetCache>)Fetch);if(disposed)return;
                fetched.Enabled=Cache.Enabled;fetched.Seen=Cache.Seen;fetched.LastById=Cache.LastById;Cache=fetched;
                var messages=Evaluate(Cache,DateTimeOffset.UtcNow);
                Save();
                foreach(string message in messages)
                {
                    File.AppendAllText(Path.Combine(root,"reset-notifications.log"),DateTimeOffset.Now.ToString("o")+" "+message+Environment.NewLine,Encoding.UTF8);
                    if(Notification!=null)Notification(message);
                }
            }
            catch(Exception e){Cache.Stale=true;Cache.Error="读取失败："+e.GetType().Name;Cache.Checked=DateTimeOffset.UtcNow;Save();}
            finally {Busy=false;next=DateTimeOffset.UtcNow.AddHours(1);ChangedNow();}
        }
        internal static List<string> Evaluate(ResetCache cache,DateTimeOffset now)
        {
            var messages=new List<string>();if(!cache.Enabled||cache.Stale)return messages;
            foreach(var n in new[]{cache.Scheduled,cache.Latest})
            {
                if(n==null||!n.Verified||n.State=="unknown"||now-n.Published>TimeSpan.FromHours(72)||n.Published>now.AddMinutes(5))continue;
                if(n.State=="scheduled"&&cache.Latest!=null&&cache.Latest.Verified&&cache.Latest.State=="completed"&&cache.Latest.Published>=n.Published)continue;
                string key=n.Fingerprint;if(cache.Seen.Contains(key))continue;
                string old;bool change=cache.LastById.TryGetValue(n.Id,out old);
                string title=change?"重置预告更新":n.State=="completed"?"公共重置已确认完成":n.State=="rolling"?"公共重置开始传播":"发现全局重置预告";
                messages.Add(title+"。"+n.StatusText+"；"+(n.State=="scheduled"?n.Timing:"公告时间："+Beijing(n.Published))+"。个人到账以账号额度为准。");
                cache.Seen.Add(key);cache.LastById[n.Id]=key;
            }
            if(cache.Seen.Count>200)cache.Seen.RemoveRange(0,cache.Seen.Count-200);
            return messages;
        }
        void Save() { File.WriteAllText(Path.Combine(root,"reset-announcements.json"),new JavaScriptSerializer().Serialize(Cache),Encoding.UTF8); }
        void ChangedNow() { if(!disposed&&Changed!=null)Changed(); }
        public void Dispose(){disposed=true;}
    }
    internal sealed class ResetForm : Form
    {
        readonly ResetMonitor monitor;
        readonly Label health,phase,time,basis,evidence,published;
        readonly TextBox details;
        readonly CheckBox enabled;
        readonly Button check,source;
        bool syncing;
        public ResetForm(ResetMonitor value)
        {
            monitor=value;Text="UsageObserver · 重置预告";ClientSize=new Size(640,584);Font=new Font("Microsoft YaHei UI",9);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterScreen;BackColor=VisualTheme.Canvas;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;
            AutoScroll=true;AutoScrollMinSize=new Size(640,584);
            VisualTheme.Text(this,"重置预告",24,18,340,35,20,VisualTheme.Ink,true);
            VisualTheme.Text(this,"CODEX  /  全局公告追踪",26,57,380,23,8,VisualTheme.Muted);
            var hero=VisualTheme.Card(this,24,94,592,159,VisualTheme.Night);
            phase=VisualTheme.Text(hero,"等待公告",20,17,552,23,9,VisualTheme.Mint);
            time=VisualTheme.Text(hero,"时间待确认",16,45,556,48,28,Color.White,true);
            basis=VisualTheme.Text(hero,"北京时间 · 暂无精确时间",20,107,552,34,9,Color.FromArgb(172,194,199));
            var card=VisualTheme.Card(this,24,269,592,173,Color.White);
            VisualTheme.Text(card,"公告依据",18,14,240,23,10,VisualTheme.Ink,true);
            published=VisualTheme.Text(card,"",18,45,556,22,8.5F,VisualTheme.Muted);
            details=new TextBox{Bounds=new Rectangle(18,74,552,55),Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Color.White,ForeColor=VisualTheme.Ink,Font=new Font("Segoe UI",11)};card.Controls.Add(details);
            evidence=VisualTheme.Text(card,"",18,138,556,24,8,VisualTheme.Accent);
            enabled=new SurfaceToggle{Text="预告提醒",Bounds=new Rectangle(26,463,235,26),ForeColor=VisualTheme.Ink};Controls.Add(enabled);
            enabled.CheckedChanged+=delegate{if(!syncing)monitor.Enable(enabled.Checked);};
            VisualTheme.Text(this,"每小时检查；仅在公告或状态变化时通知。",26,495,410,22,8,VisualTheme.Muted);
            check=VisualTheme.Button(this,"立即检查",358,459,122,delegate{monitor.Check();});
            source=VisualTheme.Button(this,"查看原帖  ↗",492,459,124,delegate{var n=monitor.Current;if(n!=null&&ResetMonitor.SourceAllowed(n.SourceUrl))Process.Start(new ProcessStartInfo(n.SourceUrl){UseShellExecute=true});},true);
            health=VisualTheme.Text(this,"",26,526,588,23,8,VisualTheme.Muted);
            var credit=new LinkLabel{Text="When Next Reset · 社区数据来源",Bounds=new Rectangle(26,555,330,22),LinkColor=VisualTheme.Accent,ActiveLinkColor=VisualTheme.Accent};Controls.Add(credit);credit.LinkClicked+=delegate{Process.Start(new ProcessStartInfo("https://whennextreset.com/zh"){UseShellExecute=true});};
            VisualTheme.Text(this,"个人到账以账户额度为准",376,555,240,22,8,VisualTheme.Muted);
            monitor.Changed+=UpdateState;FormClosed+=delegate{monitor.Changed-=UpdateState;};UpdateState();
        }
        void UpdateState()
        {
            if(IsDisposed)return;syncing=true;enabled.Checked=monitor.Cache.Enabled;syncing=false;check.Enabled=!monitor.Busy;
            var n=monitor.Current;source.Enabled=n!=null&&ResetMonitor.SourceAllowed(n.SourceUrl);
            health.Text=(monitor.Busy?"正在检查":monitor.Cache.Stale?"来源待更新 · 显示上次消息":"来源正常")+"   /   检查："+(monitor.Cache.Checked==DateTimeOffset.MinValue?"尚未检查":ResetMonitor.Beijing(monitor.Cache.Checked))+" "+monitor.Cache.Error;
            phase.Text=n==null?"暂无有效公告":(monitor.Cache.Stale?"上次消息 · ":"")+n.StatusText;
            DateTimeOffset estimate;
            bool pending=n!=null&&n.State=="scheduled";
            bool hasTime=pending&&(n.OfficialTime.HasValue||DateTimeOffset.TryParse(n.Estimate,out estimate));
            time.Text=n==null?"等待重置预告":!pending?n.State=="completed"?"已确认完成":n.State=="rolling"?"正在传播":"状态待确认":hasTime?n.ShortTiming:"时间待确认";
            basis.Text=n==null?"有新公告时将在此显示":!pending?"公告状态不代表每个账户已到账":n.OfficialTime.HasValue?"北京时间 · 高置信预告（原帖明确），执行仍待确认":hasTime?"北京时间 · 社区预计，原帖未给出精确时间":"原帖未给出明确日期、时间和时区";
            published.Text=n==null?"尚未获取公告":"发布于 "+ResetMonitor.Beijing(n.Published)+"  ·  "+(n.Verified?"原帖已核对":"原帖待核对");
            details.Text=n==null?"暂无可显示的直接重置公告。":n.Text;
            evidence.Text=n==null?"":pending&&!n.OfficialTime.HasValue?"EOD 不换算为精确时间；超过预计时间后继续等待确认。":pending?"高置信描述公告时间的依据，不保证按时完成。":"公共公告已记录；个人到账以账户额度为准。";
        }
    }
}
