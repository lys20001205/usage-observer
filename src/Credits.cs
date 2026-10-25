using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageTray
{
    internal sealed class CreditsSnapshot
    {
        public decimal? Balance,Delta,TodayDecrease,ObservedDecrease;
        public DateTimeOffset Sampled,Started;
        public bool Failed=true,AccountVerified;
        public bool Stale {get{return Failed||Sampled==DateTimeOffset.MinValue||Sampled>DateTimeOffset.UtcNow.AddMinutes(1)||DateTimeOffset.UtcNow-Sampled>TimeSpan.FromMinutes(3);}}
        internal static string Number(decimal? value){return value.HasValue?value.Value.ToString("#,0.##",CultureInfo.InvariantCulture):"待获取";}
        public string Compact {get{return "Credits "+Number(Balance)+(Stale&&Balance.HasValue?" ·旧":"");}}
        public string Change {get{return "今日↓ "+Number(TodayDecrease);}}
        public string Detail {get{return Compact+"\r\n最近一次变化："+(Delta.HasValue?Delta.Value.ToString("+#,0.##;-#,0.##;0",CultureInfo.InvariantCulture):"无连续采样")+"\r\n今日观测减少："+Number(TodayDecrease)+"\r\n本段观测减少："+Number(ObservedDecrease)+"\r\n采样时间："+(Sampled==DateTimeOffset.MinValue?"等待":MachineTime.DescribeFull(Sampled))+"\r\n记录起点："+(Started==DateTimeOffset.MinValue?"等待":MachineTime.DescribeFull(Started))+"\r\n只统计相邻3分钟内的有效余额下降，排除失败、断档和已识别的登录切换；不是逐笔账单。"+(AccountVerified?"":"旧记录没有账号标记，无法识别登录切换。")+"\r\nUTC采样与变化保存在 credits-history.csv；今日按机器时区统计。";}}
        static decimal? Amount(object value){decimal n;return value!=null&&decimal.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Number,CultureInfo.InvariantCulture,out n)?(decimal?)n:null;}
        internal static CreditsSnapshot Parse(string json)
        {
            var d=ServiceHealth.Map(new JavaScriptSerializer().DeserializeObject(json));var s=new CreditsSnapshot();
            s.Balance=Amount(ServiceHealth.Get(d,"balance"));s.Delta=Amount(ServiceHealth.Get(d,"last_delta"));s.TodayDecrease=Amount(ServiceHealth.Get(d,"today_decrease"));s.ObservedDecrease=Amount(ServiceHealth.Get(d,"observed_decrease"));
            DateTimeOffset.TryParse(ServiceHealth.Str(d,"sampled_at"),out s.Sampled);DateTimeOffset.TryParse(ServiceHealth.Str(d,"started_at"),out s.Started);
            s.Failed=ServiceHealth.Get(d,"error")!=null||!d.ContainsKey("stale")||Convert.ToBoolean(d["stale"]);s.AccountVerified=Convert.ToBoolean(ServiceHealth.Get(d,"account_verified")??false);return s;
        }
    }
    internal sealed class CreditsMonitor : IDisposable
    {
        public CreditsSnapshot Snapshot=new CreditsSnapshot();
        readonly string root,backend;bool busy,disposed;DateTimeOffset next;
        public CreditsMonitor(string data,string source){root=data;backend=source;try{Snapshot=CreditsSnapshot.Parse(File.ReadAllText(Path.Combine(root,"credits-status.json"),Encoding.UTF8));Snapshot.Failed=true;}catch{}}
        static string Quote(string value){if(value.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Invalid path");return "\""+value.TrimEnd('\\')+"\"";}
        string Read()
        {
            string app=AppDomain.CurrentDomain.BaseDirectory;
            var start=new ProcessStartInfo(Path.Combine(app,"runtime","python","python.exe"),Quote(Path.Combine(app,"credits_probe.py"))+" --database "+Quote(Path.Combine(backend,"records.sqlite"))+" --output "+Quote(root)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var p=Process.Start(start)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(15000)){p.Kill();throw new TimeoutException();}if(p.ExitCode!=0||output.Result.Length>16384)throw new IOException("Credits probe failed");return output.Result;}
        }
        public async void Tick(bool recording)
        {
            if(disposed)return;if(!recording){Snapshot.Failed=true;return;}if(busy||DateTimeOffset.UtcNow<next)return;busy=true;
            try{string json=await Task.Run((Func<string>)Read);if(disposed)return;var value=CreditsSnapshot.Parse(json);if(ServiceHealth.Map(new JavaScriptSerializer().DeserializeObject(json)).ContainsKey("balance")){Snapshot=value;}else Snapshot.Failed=true;File.WriteAllText(Path.Combine(root,"credits-status.json"),json,new UTF8Encoding(false));}
            catch{if(!disposed)Snapshot.Failed=true;}
            finally{busy=false;next=DateTimeOffset.UtcNow.AddSeconds(15);}
        }
        public void Dispose(){disposed=true;}
    }
}
