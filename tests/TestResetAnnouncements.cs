using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace CodexUsageTray
{
    internal static class ResetAnnouncementTests
    {
        static int count;
        static void Check(bool condition,string name)
        {
            if(!condition)throw new Exception(name);
            count++;Console.WriteLine("PASS: "+name);
        }
        static ResetNotice Notice(DateTimeOffset now,string id,bool verified,string state)
        {
            return new ResetNotice{Id=id,Text="Lands tomorrow before 10am PST.",FeedText="Lands tomorrow before 10am PST.",SourceUrl="https://x.com/thsottiaux/status/"+id,Published=now,State=state,Verified=verified,Estimate=now.AddHours(12).ToString("o")};
        }
        static Snapshot Quota(DateTimeOffset at,double used)
        {
            return new Snapshot{QuotaTime=at,Used=used,PrimaryWindow=10080};
        }
        static string Feed(DateTimeOffset now,bool stale)
        {
            return new JavaScriptSerializer().Serialize(new {
                scheduled_reset=new{id="123",reset_type="regular",announced_at=now.ToString("o"),text="Lands tomorrow before 10am PST.",scheduled_for=now.AddHours(12).ToString("o"),source=new{url="https://x.com/thsottiaux/status/123"}},
                latest_reset=(object)null,
                freshness=new{last_success_at=now.ToString("o"),stale=stale}});
        }
        public static int Main(string[] args)
        {
            try
            {
                string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                var now=DateTimeOffset.UtcNow;
                var instant=DateTimeOffset.Parse("2026-10-22T17:00:00Z");
                var tokyo=TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                var china=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                var india=TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var pacific=TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                Check(MachineTime.InZone(instant,tokyo).ToString("MM-dd HH:mm")=="10-23 02:00","Tokyo converts forecast across midnight");
                Check(MachineTime.InZone(instant,china).ToString("MM-dd HH:mm")=="10-23 01:00","same instant follows a Chinese machine zone");
                Check(MachineTime.InZone(instant,india).ToString("MM-dd HH:mm")=="10-22 22:30","fractional UTC offset supported");
                Check(MachineTime.InZone(instant,TimeZoneInfo.Utc).Hour==17,"UTC machine does not gain a fixed offset");
                Check(MachineTime.InZone(instant,tokyo).UtcDateTime==instant.UtcDateTime,"conversion preserves the original instant");
                Check(MachineTime.ZoneLabel(instant,tokyo)=="本机时间 UTC+09:00","zone label matches converted timestamp");
                Check(MachineTime.InZone(DateTimeOffset.Parse("2026-07-01T12:00:00Z"),pacific).Offset==TimeSpan.FromHours(-7),"summer daylight offset comes from OS zone rules");
                Check(MachineTime.InZone(DateTimeOffset.Parse("2026-01-01T12:00:00Z"),pacific).Offset==TimeSpan.FromHours(-8),"historical winter offset uses its own date");
                Check(MachineTime.ZoneLabel(DateTimeOffset.Parse("2026-11-01T08:30:00Z"),pacific).EndsWith("-07:00")&&MachineTime.ZoneLabel(DateTimeOffset.Parse("2026-11-01T09:30:00Z"),pacific).EndsWith("-08:00"),"repeated DST hour has distinct explicit offsets");
                Check(MachineTime.Local(instant).Offset==TimeZoneInfo.Local.GetUtcOffset(instant),"production conversion selects this machine's actual zone");
                var zonedNotice=Notice(now,"123",false,"scheduled");zonedNotice.Estimate=instant.ToString("o");string fingerprint=zonedNotice.Fingerprint;
                Check(zonedNotice.ShortTiming==MachineTime.Format(instant,"MM-dd HH:mm")&&zonedNotice.Timing.Contains(MachineTime.Label(instant)),"forecast text and its label use the same machine zone");
                Check(zonedNotice.Fingerprint==fingerprint,"local presentation does not change notification identity");
                Check(new SpeedStatus{LastResponse=instant.ToString("o")}.Detail.Contains(MachineTime.DescribeFull(instant)),"last-response details convert UTC before display");
                Check(ResetMonitor.Classify("Lands tomorrow before 10am PST.",true)=="scheduled","short forecast reply recognized");
                Check(ResetMonitor.Classify("Lands tomorrow before 10am PST.",false)=="unknown","reply cannot confirm completion");
                Check(ResetMonitor.Classify("Reset is not landing tomorrow",true)=="unknown","negated forecast rejected");
                Check(ResetMonitor.Classify("Reset credit lands tomorrow",true)=="unknown","reset cards do not become global forecasts");
                var notice=Notice(now,"123",true,"scheduled");
                Check(!notice.OfficialTime.HasValue,"relative PST forecast stays advisory");
                notice.Text="Reset at 2026-10-23 17:00 UTC";
                Check(notice.OfficialTime.HasValue,"explicit dated zoned forecast still supported");
                var cache=new ResetCache{Stale=false,Scheduled=Notice(now,"123",false,"scheduled")};
                var messages=ResetMonitor.Evaluate(cache,now);
                Check(messages.Count==1&&messages[0].Contains("待核对")&&!messages[0].Contains("已确认完成"),"oEmbed failure delivers labelled advisory");
                Check(ResetMonitor.Evaluate(cache,now).Count==0,"same advisory deduplicated");
                var restored=new JavaScriptSerializer().Deserialize<ResetCache>(new JavaScriptSerializer().Serialize(cache));
                Check(ResetMonitor.Evaluate(restored,now).Count==0,"deduplication survives restart");
                cache.Scheduled.Estimate=now.AddHours(13).ToString("o");
                Check(ResetMonitor.Evaluate(cache,now).Count==1,"changed forecast time notifies");
                cache.Stale=true;cache.Seen.Clear();
                Check(ResetMonitor.Evaluate(cache,now).Count==0,"stale source suppressed");
                cache.Stale=false;cache.Scheduled.Published=now.AddHours(-73);
                Check(ResetMonitor.Evaluate(cache,now).Count==0,"expired notice suppressed");
                cache.Scheduled.Published=now.AddMinutes(6);
                Check(ResetMonitor.Evaluate(cache,now).Count==0,"future notice suppressed");
                cache.Scheduled.Published=now;cache.Scheduled.SourceUrl="https://example.com/123";
                Check(ResetMonitor.Evaluate(cache,now).Count==0,"unapproved source suppressed");
                cache.Scheduled=Notice(now,"123",false,"scheduled");cache.Latest=Notice(now.AddMinutes(-1),"122",false,"unknown");
                messages=ResetMonitor.Evaluate(cache,now);
                Check(messages.Count==2&&messages[1].Contains("社区预计"),"latest forecast ordered after older unseen history");
                cache=new ResetCache{Stale=false};
                Check(ResetMonitor.PollDelay(cache,now).TotalSeconds==60,"normal checks every 60 seconds");
                cache.Scheduled=Notice(now,"123",false,"scheduled");
                Check(ResetMonitor.PollDelay(cache,now).TotalSeconds==30,"forecast checks every 30 seconds");
                cache.Stale=true;
                Check(ResetMonitor.PollDelay(cache,now).TotalSeconds==15,"failed checks retry in 15 seconds");
                using(var monitor=new ResetMonitor(root))
                {
                    Check(monitor.NextCheck<=now,"startup does not inherit one-hour delay");
                    int downloads=0;
                    monitor.DownloadText=delegate(string url){downloads++;if(url==ResetMonitor.Feed)return Feed(now,false);throw new IOException("simulated oEmbed timeout");};
                    int delivered=0;monitor.Notification+=delegate(string message){delivered++;};
                    monitor.CheckAsync().GetAwaiter().GetResult();
                    Check(delivered==1&&!monitor.Cache.Scheduled.Verified&&!monitor.Cache.Stale,"transport failure advisory reaches notification event");
                    Check((monitor.NextCheck-DateTimeOffset.UtcNow).TotalSeconds<=31,"live scheduler uses short interval");
                    monitor.CheckAsync().GetAwaiter().GetResult();
                    Check(delivered==1,"repeated transport failure does not repeat alert");
                    monitor.Cache.Scheduled.Verified=true;monitor.Cache.Scheduled.State="scheduled";
                    monitor.Cache.Scheduled.Text="Lands tomorrow before 10am PST.  ";
                    downloads=0;var fetched=monitor.Fetch();
                    Check(downloads==1&&fetched.Scheduled.Verified,"verified text cached by unchanged feed text");
                    monitor.DownloadText=delegate(string url){throw new IOException("simulated feed failure");};
                    monitor.CheckAsync().GetAwaiter().GetResult();
                    Check(monitor.Cache.Stale&&(monitor.NextCheck-DateTimeOffset.UtcNow).TotalSeconds<=16,"feed failure preserves old notice and retries quickly");
                    monitor.DownloadText=delegate(string url){if(url==ResetMonitor.Feed)return Feed(now.AddMinutes(-16),false);throw new IOException();};
                    Check(monitor.Fetch().Stale,"source stalled for 16 minutes marked stale");
                }
                cache=new ResetCache{Stale=true};
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now,80),now)==null,"first account observation only seeds baseline");
                string recovery=ResetMonitor.EvaluateQuota(cache,Quota(now.AddMinutes(1),0),now.AddMinutes(1));
                Check(recovery!=null&&recovery.Contains("20% → 100%")&&!recovery.Contains("全局重置"),"account recovery independent of stale community source");
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now.AddMinutes(1),0),now.AddMinutes(1))==null,"duplicate account snapshot suppressed");
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now.AddSeconds(30),95),now.AddMinutes(1))==null&&cache.Quota.Used==0,"out-of-order account sample ignored");
                foreach(string invalid in new[]{"stale","failed","old","future","nan","out-of-range","window"})
                {
                    cache.Quota=new QuotaObservation{Used=80,SampleAt=now.ToString("o"),Window=10080};
                    var q=Quota(now.AddMinutes(1),0);
                    if(invalid=="stale")q.QuotaStale=true;
                    if(invalid=="failed")q.ReadFailed=true;
                    if(invalid=="old")q.QuotaTime=now.AddMinutes(-4);
                    if(invalid=="future")q.QuotaTime=now.AddMinutes(2);
                    if(invalid=="nan")q.Used=double.NaN;
                    if(invalid=="out-of-range")q.Used=-1;
                    if(invalid=="window")q.PrimaryWindow=300;
                    Check(ResetMonitor.EvaluateQuota(cache,q,now.AddMinutes(1))==null,"no false recovery from "+invalid);
                }
                cache.Quota=new QuotaObservation{Used=80,SampleAt=now.AddMinutes(-5).ToString("o"),Window=10080};
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now,0),now)==null,"long gap reseeds without claiming recovery");
                cache.Enabled=false;cache.Quota=new QuotaObservation{Used=80,SampleAt=now.ToString("o"),Window=10080};
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now.AddMinutes(1),0),now.AddMinutes(1))==null,"disabled alerts respect preference and update baseline");
                cache.Enabled=true;
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now.AddMinutes(2),0),now.AddMinutes(2))==null,"re-enable does not replay disabled recovery");
                cache.Quota=new QuotaObservation{Used=80,SampleAt=now.ToString("o"),Window=10080};
                Check(ResetMonitor.EvaluateQuota(cache,Quota(now.AddMinutes(1),79),now.AddMinutes(1))==null,"minor quota jitter not a recovery alert");
                Console.WriteLine(count+" reset regression checks passed");return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
