using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageTray
{
    internal sealed class QuotaPoint
    {
        internal DateTimeOffset Time;internal double? Primary,Secondary;internal long ResetPrimary,ResetSecondary;internal int MinutesPrimary,MinutesSecondary;
        internal string Account;internal bool Error,TokenCoverage;internal double Tokens;
        internal double? Used(bool secondary){return secondary?Secondary:Primary;}
        internal long Reset(bool secondary){return secondary?ResetSecondary:ResetPrimary;}
        internal int Minutes(bool secondary){return secondary?MinutesSecondary:MinutesPrimary;}
        internal bool Valid(bool secondary){double? used=Used(secondary);return !Error&&used.HasValue&&used>=0&&used<=100;}
    }
    internal sealed class QuotaHistory
    {
        internal readonly List<QuotaPoint> Points=new List<QuotaPoint>();internal int Total;internal bool Truncated;internal string Error;
        static object Get(Dictionary<string,object> data,string key){object value;return data.TryGetValue(key,out value)?value:null;}
        static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        static double? Number(object value){double result;return value!=null&&double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out result)&&!double.IsNaN(result)&&!double.IsInfinity(result)?(double?)result:null;}
        internal static QuotaHistory Parse(string text)
        {
            var data=new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Deserialize<Dictionary<string,object>>(text);var result=new QuotaHistory{Total=Convert.ToInt32(Get(data,"total")),Truncated=Convert.ToBoolean(Get(data,"truncated")),Error=Convert.ToString(Get(data,"error"))};
            var points=Get(data,"points") as System.Collections.IEnumerable;if(points==null)return result;
            foreach(var item in points)
            {
                var row=Map(item);DateTimeOffset time;if(!DateTimeOffset.TryParse(Convert.ToString(Get(row,"time")),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out time))continue;
                var p=Map(Get(row,"primary"));var s=Map(Get(row,"secondary"));
                result.Points.Add(new QuotaPoint{Time=time,Primary=Number(Get(p,"used")),Secondary=Number(Get(s,"used")),ResetPrimary=(long)(Number(Get(p,"reset"))??0),ResetSecondary=(long)(Number(Get(s,"reset"))??0),MinutesPrimary=(int)(Number(Get(p,"minutes"))??0),MinutesSecondary=(int)(Number(Get(s,"minutes"))??0),Account=Convert.ToString(Get(row,"account")),Error=Convert.ToBoolean(Get(row,"error")),TokenCoverage=Convert.ToBoolean(Get(row,"token_coverage")),Tokens=Number(Get(row,"tokens"))??0});
            }
            var ordered=result.Points.OrderBy(p=>p.Time).ToList();result.Points.Clear();result.Points.AddRange(ordered);return result;
        }
        static string Quote(string value){if(value.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Invalid history path");return "\""+value+"\"";}
        internal static QuotaHistory Read(string database)
        {
            string program=AppDomain.CurrentDomain.BaseDirectory,python=Path.Combine(program,"runtime\\python\\python.exe"),script=Path.Combine(program,"quota_history.py");
            if(!File.Exists(python)||!File.Exists(script))throw new IOException("历史读取组件不完整，请重新安装程序。");
            using(var process=Process.Start(new ProcessStartInfo(python,Quote(script)+" --database "+Quote(database)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8}))
            {
                Task<string> output=process.StandardOutput.ReadToEndAsync(),errors=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(10000)){try{process.Kill();}catch{}throw new IOException("历史读取超时；采集仍继续运行。");}
                string text=output.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException("暂时无法读取历史，请稍后刷新。");return Parse(text);
            }
        }
    }
    internal sealed class TrendEvent
    {
        internal DateTimeOffset Time,Start;internal string Kind,Title,Detail;internal double Drop;
    }
    internal sealed class TrendStep
    {
        internal QuotaPoint First,Last;internal double Drop;internal bool Covered;internal double Tokens;
        internal double Minutes {get{return (Last.Time-First.Time).TotalMinutes;}}
    }
    internal sealed class TrendAnalysis
    {
        internal readonly List<QuotaPoint> Points=new List<QuotaPoint>();internal readonly List<TrendStep> Steps=new List<TrendStep>();internal readonly List<TrendEvent> Events=new List<TrendEvent>();
        internal readonly HashSet<QuotaPoint> Breaks=new HashSet<QuotaPoint>();internal double Drop,HourDrop,RecentRate;internal double? Remaining;internal int Invalid,Resets,Gaps,AccountChanges;internal bool AccountUnknown,Stale,Secondary,RateAvailable;internal string Summary="等待历史数据";
        static double Median(List<double> values){var a=values.OrderBy(x=>x).ToArray();return a.Length==0?0:a.Length%2==1?a[a.Length/2]:(a[a.Length/2-1]+a[a.Length/2])/2;}
        static double Percentile(List<double> values,double fraction){var a=values.OrderBy(x=>x).ToArray();return a.Length==0?0:a[(int)Math.Floor((a.Length-1)*fraction)];}
        internal static TrendAnalysis Analyze(QuotaHistory history,bool secondary,TimeSpan? span,double rapidDrop,DateTimeOffset now)
        {
            var result=new TrendAnalysis{Secondary=secondary};if(history.Points.Count==0){result.Summary=history.Error=="read_failed"?"历史暂时无法读取，请稍后刷新":"尚无额度历史；采样后自动生成趋势";return result;}
            DateTimeOffset cutoff=span.HasValue?now-span.Value:DateTimeOffset.MinValue;
            var source=history.Points.Where(p=>p.Time<=now).GroupBy(p=>p.Time).Select(g=>g.Last()).OrderBy(p=>p.Time).ToList();if(source.Count==0)return result;
            string latestAccount=source.LastOrDefault(p=>!string.IsNullOrEmpty(p.Account))==null?"":source.Last(p=>!string.IsNullOrEmpty(p.Account)).Account;
            // A marked account switch ends the earlier series. Never combine unrelated accounts.
            int first=0;
            if(!string.IsNullOrEmpty(latestAccount))
                for(int i=source.Count-1;i>=0;i--){if(source[i].Account==latestAccount)first=i;else if(!string.IsNullOrEmpty(source[i].Account)||!source[i].Error){first=i+1;break;}}
            result.AccountChanges=first>0?1:0;
            var scoped=source.Skip(first).ToList();result.AccountUnknown=string.IsNullOrEmpty(latestAccount);
            if(!scoped.Any(p=>p.Valid(secondary))){result.Points.AddRange(scoped.Where(p=>p.Time>=cutoff));result.Invalid=result.Points.Count(p=>p.Error);result.Summary=secondary?"次额度窗口暂无有效历史记录":"额度读取失败或字段缺失，暂无有效采样";return result;}
            QuotaPoint previous=null;bool interrupted=false;
            foreach(var point in scoped)
            {
                bool visible=point.Time>=cutoff;if(visible)result.Points.Add(point);
                if(!point.Valid(secondary)){if(visible)result.Invalid++;interrupted=true;continue;}
                result.Remaining=100-point.Used(secondary).Value;
                if(previous!=null)
                {
                    double minutes=(point.Time-previous.Time).TotalMinutes,change=point.Used(secondary).Value-previous.Used(secondary).Value;
                    bool windowChanged=point.Minutes(secondary)>0&&previous.Minutes(secondary)>0&&point.Minutes(secondary)!=previous.Minutes(secondary);
                    bool resetShift=point.Reset(secondary)>0&&previous.Reset(secondary)>0&&Math.Abs(point.Reset(secondary)-previous.Reset(secondary))>=300;
                    bool rise=change<-.001;
                    if(windowChanged||resetShift||rise)
                    {
                        result.Breaks.Add(point);if(visible){result.Resets++;result.Events.Add(new TrendEvent{Time=point.Time,Start=previous.Time,Kind="reset",Title=windowChanged?"额度窗口变化":resetShift?"重置 / 新周期":"额度回升 / 校正",Drop=0,Detail="趋势从这里重新分段，跨段变化不计入下降速度。"});}
                    }
                    else if(interrupted||minutes>5||minutes<=0)
                    {
                        result.Breaks.Add(point);if(visible){result.Gaps++;result.Events.Add(new TrendEvent{Time=point.Time,Start=previous.Time,Kind="gap",Title="采样间断",Drop=Math.Max(0,change),Detail="前后额度减少 "+Math.Max(0,change).ToString("0.##")+" 个百分点；发生时段无法确定，不作为突降依据。"});}
                    }
                    else if(previous.Time>=cutoff)
                    {
                        var step=new TrendStep{First=previous,Last=point,Drop=Math.Max(0,change),Covered=point.TokenCoverage,Tokens=point.Tokens};result.Steps.Add(step);result.Drop+=step.Drop;if(previous.Time>=now.AddHours(-1))result.HourDrop+=step.Drop;
                    }
                }
                previous=point;interrupted=false;
            }
            if(previous!=null)result.Stale=(now-previous.Time).TotalMinutes>5;
            foreach(var step in result.Steps.Where(s=>s.Drop>=rapidDrop&&s.Drop>=.5))
                result.Events.Add(new TrendEvent{Time=step.Last.Time,Start=step.First.Time,Kind="spike",Title="单次采样突降（待核查）",Drop=step.Drop,Detail=step.Minutes.ToString("0.#")+" 分钟内观测到减少 "+step.Drop.ToString("0.##")+" 个百分点。采样更新或集中结算也可能造成跳变，不能直接认定异常消费。"});
            var buckets=new SortedDictionary<long,List<TrendStep>>();
            foreach(var step in result.Steps){long key=step.Last.Time.ToUnixTimeSeconds()/900;List<TrendStep> bucket;if(!buckets.TryGetValue(key,out bucket)){bucket=new List<TrendStep>();buckets[key]=bucket;}bucket.Add(step);}
            var baseline=new List<double>();TrendEvent lastRapid=null;
            foreach(var entry in buckets)
            {
                var steps=entry.Value;double minutes=steps.Sum(s=>s.Minutes),drop=steps.Sum(s=>s.Drop);bool complete=minutes>=10&&!result.Breaks.Any(p=>p.Time>steps[0].First.Time&&p.Time<=steps[steps.Count-1].Last.Time);
                if(!complete)continue;
                double scaled=drop*15/minutes,threshold=Math.Max(.5,rapidDrop);bool enough=baseline.Count>=8;
                if(enough){double median=Median(baseline),mad=Median(baseline.Select(x=>Math.Abs(x-median)).ToList());threshold=Math.Max(threshold,Math.Max(Percentile(baseline,.9)*1.5,median+6*mad));}
                if(scaled>=threshold&&drop>=rapidDrop&&!result.Events.Any(e=>e.Kind=="spike"&&e.Time>steps[0].First.Time&&e.Time<=steps[steps.Count-1].Last.Time))
                {
                    var end=steps[steps.Count-1].Last.Time;string detail="约 "+minutes.ToString("0")+" 分钟减少 "+drop.ToString("0.##")+" 个百分点；"+(enough?"高于历史常见速度（参考阈值 "+threshold.ToString("0.##")+" / 15 分钟）。":"触发手动阈值；历史不足，暂不能判断偏离常态。")+" 可能是正常密集使用或延迟结算，需结合任务核查。";
                    if(lastRapid!=null&&(end-lastRapid.Time).TotalMinutes<=20){lastRapid.Time=end;lastRapid.Drop+=drop;lastRapid.Detail="这一连续时段累计减少 "+lastRapid.Drop.ToString("0.##")+" 个百分点。"+detail;}
                    else{lastRapid=new TrendEvent{Time=end,Start=steps[0].First.Time,Kind="rapid",Title=enough?"下降速度偏高（待核查）":"快速下降（阈值提示）",Drop=drop,Detail=detail};result.Events.Add(lastRapid);}
                }
                if(drop>=2&&steps.All(s=>s.Covered)&&steps.Sum(s=>s.Tokens)==0)
                    result.Events.Add(new TrendEvent{Time=steps[steps.Count-1].Last.Time,Start=steps[0].First.Time,Kind="unattributed",Title="额度下降但未见 token 增长",Drop=drop,Detail="已观测设备在这一时段没有新增 token。请核对其他设备、登录账号和结算延迟；不能据此确认异常消费或归属。"});
                baseline.Add(scaled);if(baseline.Count>288)baseline.RemoveAt(0);
            }
            var recent=result.Steps.Where(s=>s.First.Time>=now.AddMinutes(-15)).ToList();double recentMinutes=recent.Sum(s=>s.Minutes);result.RateAvailable=recentMinutes>=10;result.RecentRate=result.RateAvailable?recent.Sum(s=>s.Drop)*60/recentMinutes:0;
            result.Events.Sort((a,b)=>b.Time.CompareTo(a.Time));int suspicious=result.Events.Count(e=>e.Kind=="rapid"||e.Kind=="spike"||e.Kind=="unattributed");
            result.Summary=result.Stale?"最新额度采样已超过 5 分钟，分析可信度下降":suspicious>0?"发现 "+suspicious+" 个待核查时段，查看下方依据":result.Steps.Count<10?"有效历史较少，暂不足以判断下降是否异常":"已完成检查，未触发当前检测规则";
            if(result.Invalid>0)result.Summary+=" · "+result.Invalid+" 个无效 / 失败采样已排除";return result;
        }
    }
}
