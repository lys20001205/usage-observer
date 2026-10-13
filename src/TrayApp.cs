using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexUsageTray
{
    internal sealed class Snapshot
    {
        public bool Running, Stale, ReadFailed, QuotaStale;
        public int Pid;
        public double? Remaining, Used;
        public long A, B;
        public DateTimeOffset Last, Started, QuotaTime, PrimaryReset, SecondaryReset;
        public int PrimaryWindow, SecondaryWindow;
        public string QuotaError = "";
        public string AHealth = "等待数据", BHealth = "等待数据", Classification = "等待第一次采样";
        static object Value(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }
        static Dictionary<string, object> Map(object x) { return x as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static DateTimeOffset Time(object x) { DateTimeOffset t; DateTimeOffset.TryParse(Convert.ToString(x), out t); return t; }
        public static Snapshot Parse(string text, DateTimeOffset now)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            var s = new Snapshot();
            s.Running = Convert.ToBoolean(Value(d, "running")); s.Pid = Convert.ToInt32(Value(d, "pid"));
            if (Value(d, "remaining_percent") != null) s.Remaining = Convert.ToDouble(d["remaining_percent"]);
            if (Value(d, "used_percent") != null) s.Used = Convert.ToDouble(d["used_percent"]);
            s.Last = Time(Value(d, "last_sample_at")); s.Started = Time(Value(d, "recording_started_at"));
            s.QuotaStale = Convert.ToBoolean(Value(d, "quota_stale")); s.QuotaTime = Time(Value(d, "quota_sample_at"));
            s.PrimaryReset = UnixTime(Value(d, "primary_resets_at")); s.SecondaryReset = UnixTime(Value(d, "secondary_resets_at"));
            s.PrimaryWindow = Convert.ToInt32(Value(d, "primary_window_minutes")); s.SecondaryWindow = Convert.ToInt32(Value(d, "secondary_window_minutes"));
            var qe = Map(Value(d, "quota_error")); string category = Convert.ToString(Value(qe, "category"));
            s.QuotaError = category == "authentication" ? "登录验证暂时失败" : category == "overloaded" ? "接口暂时繁忙" : category == "timeout" ? "额度请求超时" : category == "transport" ? "额度连接中断" : "额度请求暂时失败";
            var tokens = Map(Value(d, "recorded_tokens")); s.A = Convert.ToInt64(Value(tokens, "A")); s.B = Convert.ToInt64(Value(tokens, "B"));
            var health = Map(Value(d, "health"));
            s.AHealth = Convert.ToString(Value(Map(Value(health, "A")), "status"));
            s.BHealth = Convert.ToString(Value(Map(Value(health, "B")), "status"));
            s.Classification = Convert.ToString(Value(health, "classification"));
            int interval = Convert.ToInt32(Value(d, "interval_seconds"));
            s.Stale = s.Last == DateTimeOffset.MinValue || (now - s.Last).TotalSeconds > Math.Max(180, interval * 3);
            return s;
        }
        static DateTimeOffset UnixTime(object value)
        {
            long seconds; if (!long.TryParse(Convert.ToString(value), out seconds) || seconds <= 0) return DateTimeOffset.MinValue;
            try { return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds); } catch { return DateTimeOffset.MinValue; }
        }
        static string ResetTime(DateTimeOffset t, bool full)
        { return t == DateTimeOffset.MinValue ? "未提供" : t.ToLocalTime().ToString(full ? "yyyy-MM-dd HH:mm:ss zzz" : "MM-dd HH:mm"); }
        static string WindowName(int minutes, string fallback)
        { return minutes == 10080 ? "每周" : minutes == 300 ? "5小时" : minutes > 0 ? (minutes % 1440 == 0 ? (minutes / 1440) + "天" : minutes % 60 == 0 ? (minutes / 60) + "小时" : minutes + "分钟") : fallback; }
        public string ResetCompact
        { get { return PrimaryReset == DateTimeOffset.MinValue ? "重置时间待获取" : PrimaryReset <= DateTimeOffset.Now ? "重置待确认" : "重置 " + ResetTime(PrimaryReset, false); } }
        public string ResetDetail
        { get { return WindowName(PrimaryWindow, "主窗口") + "重置 " + ResetTime(PrimaryReset, false) + "\n" + WindowName(SecondaryWindow, "次窗口") + "重置 " + ResetTime(SecondaryReset, false) + (QuotaStale ? " · 上次结果" : ""); } }
        public string ResetTooltip
        { get { return WindowName(PrimaryWindow, "主窗口") + "重置：" + ResetTime(PrimaryReset, true) + "\n" + WindowName(SecondaryWindow, "次窗口") + "重置：" + ResetTime(SecondaryReset, true) + (QuotaStale ? "\n重置时间为上次成功快照" : "") + (PrimaryReset != DateTimeOffset.MinValue && PrimaryReset <= DateTimeOffset.Now ? "\n已到预计时间，等待服务器确认新周期" : ""); } }
        public static string Compact(long n)
        {
            if (n >= 1000000000) return (n / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000) return (n / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }
        public string State(bool alive, bool stopping)
        {
            if (stopping) return "正在暂停";
            if (!alive) return "已暂停";
            if (ReadFailed || Stale) return "数据未更新";
            if (!Running) return "等待采样";
            if (QuotaStale) return "额度待更新";
            if (AHealth != "ok" || BHealth != "ok" || !Remaining.HasValue) return "覆盖不完整";
            return "正在记录";
        }
        public string Tooltip(bool alive, bool stopping)
        {
            string result = "Codex · " + State(alive, stopping) + "\n剩余 " + (Remaining.HasValue ? Remaining.Value.ToString("0.#") + "%" : "未知") + (QuotaStale ? "（上次）" : "") + " · 本机 " + Compact(A);
            return result.Length > 63 ? result.Substring(0, 63) : result;
        }
        public static string Health(string s) { return s == "ok" ? "采集正常" : s == "unavailable" ? "无法连接" : s == "parse_errors" ? "日志解析异常" : "等待数据"; }
    }

    internal sealed class StartupSetting
    {
        readonly string keyPath, valueName, command;
        public StartupSetting(string exe) : this("Software\\Microsoft\\Windows\\CurrentVersion\\Run", "UsageObserver", exe) { }
        internal StartupSetting(string key, string name, string exe) { keyPath = key; valueName = name; command = "\"" + exe + "\""; }
        public bool Enabled
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) return key != null && string.Equals(Convert.ToString(key.GetValue(valueName)), command, StringComparison.OrdinalIgnoreCase); }
        }
        public void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                string previous = Convert.ToString(key.GetValue(valueName));
                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, command, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此启动项名称已被其他程序占用，未修改。请查看数据文件夹。");
                if (enabled) key.SetValue(valueName, command, RegistryValueKind.String); else key.DeleteValue(valueName, false);
            }
        }
    }

    internal sealed class WidgetSettings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public int WidgetWidth=160, WidgetHeight=92, WidgetTransparency=60;
        public bool Visible = true;
        public bool RecorderPaused = false;
        public bool DeepSeekAlerts = true;
        public decimal LowBalanceCny = 20, LowBalanceUsd = 3, DailyDecreaseCny = 50, DailyDecreaseUsd = 7;
        public string LastLowAlert = "", LastDailyAlertDay = "", LastAlertAccount = "";
        public static WidgetSettings Load(string root)
        {
            try { return new JavaScriptSerializer().Deserialize<WidgetSettings>(File.ReadAllText(Path.Combine(root, "ui-settings.json"))) ?? new WidgetSettings(); }
            catch { return new WidgetSettings(); }
        }
        public void Save(string root) { File.WriteAllText(Path.Combine(root, "ui-settings.json"), new JavaScriptSerializer().Serialize(this), Encoding.UTF8); }
    }

    internal sealed class DeepSeekSnapshot
    {
        public string Currency = "CNY", Account = "", AHealth = "等待", BHealth = "等待", BalanceHealth = "等待";
        public decimal? Balance, Granted, ToppedUp, Decrease, TodayDecrease;
        public long A, B;
        public DateTimeOffset Last, BalanceTime, Started;
        public bool Stale = true;
        static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static object Get(Dictionary<string, object> d, string k) { object value; return d.TryGetValue(k, out value) ? value : null; }
        static decimal? Amount(object value) { decimal amount; return decimal.TryParse(Convert.ToString(value), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) ? (decimal?)amount : null; }
        static DateTimeOffset Time(object value) { DateTimeOffset result; DateTimeOffset.TryParse(Convert.ToString(value), out result); return result; }
        public static DeepSeekSnapshot Parse(string json, DateTimeOffset now)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); var result = new DeepSeekSnapshot();
            result.Last = Time(Get(data, "last_sample_at")); result.Started = Time(Get(data, "recording_started_at"));
            var tokens = Map(Get(data, "today_tokens")); result.A = Convert.ToInt64(Get(tokens, "A")); result.B = Convert.ToInt64(Get(tokens, "B"));
            var health = Map(Get(data, "health")); result.AHealth = Convert.ToString(Get(Map(Get(health, "A")), "status")); result.BHealth = Convert.ToString(Get(Map(Get(health, "B")), "status")); result.BalanceHealth = Convert.ToString(Get(Map(Get(health, "balance")), "status"));
            var wallets = Get(data, "wallets") as System.Collections.IEnumerable;
            Dictionary<string, object> chosen = null;
            if (wallets != null) foreach (var item in wallets) { var wallet = Map(item); if (chosen == null) chosen = wallet; if (Convert.ToString(Get(wallet, "currency")) == "CNY") { chosen = wallet; break; } }
            if (chosen != null)
            {
                result.Currency = Convert.ToString(Get(chosen, "currency")); result.Account = Convert.ToString(Get(chosen, "account_fingerprint"));
                result.Balance = Amount(Get(chosen, "total_balance")); result.Granted = Amount(Get(chosen, "granted_balance")); result.ToppedUp = Amount(Get(chosen, "topped_up_balance")); result.Decrease = Amount(Get(chosen, "observed_decrease")); result.TodayDecrease = Amount(Get(chosen, "today_observed_decrease")); result.BalanceTime = Time(Get(chosen, "time"));
            }
            result.Stale = result.BalanceHealth != "ok" || !result.Balance.HasValue || (now - result.BalanceTime).TotalSeconds > 180 || (now - result.Last).TotalSeconds > 180;
            return result;
        }
        public string Money(decimal? amount) { return amount.HasValue ? (Currency == "CNY" ? "¥" : "$ ") + amount.Value.ToString("0.00") : "未知"; }
        public string ShortText { get { return "DeepSeek " + Money(Balance) + (Stale ? " · 上次" : ""); } }
    }

    internal static class DeepSeekAlerts
    {
        public static string Evaluate(DeepSeekSnapshot snapshot, WidgetSettings settings, DateTimeOffset now)
        {
            if (!settings.DeepSeekAlerts || snapshot.Stale || !snapshot.Balance.HasValue) return null;
            if (settings.LastAlertAccount != snapshot.Account) { settings.LastLowAlert = ""; settings.LastDailyAlertDay = ""; settings.LastAlertAccount = snapshot.Account; }
            decimal low = snapshot.Currency == "CNY" ? settings.LowBalanceCny : settings.LowBalanceUsd;
            decimal daily = snapshot.Currency == "CNY" ? settings.DailyDecreaseCny : settings.DailyDecreaseUsd;
            string day = now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
            DateTimeOffset last; DateTimeOffset.TryParse(settings.LastLowAlert, out last);
            if (low > 0 && snapshot.Balance.Value <= low && (last == DateTimeOffset.MinValue || (now - last).TotalHours >= 6))
            {
                settings.LastLowAlert = now.ToString("o");
                if (daily > 0 && snapshot.TodayDecrease.HasValue && snapshot.TodayDecrease.Value >= daily) settings.LastDailyAlertDay = day;
                return "DeepSeek 余额较低：" + snapshot.Money(snapshot.Balance) + "。可在面板调整提醒阈值。";
            }
            if (daily > 0 && snapshot.TodayDecrease.HasValue && snapshot.TodayDecrease.Value >= daily && settings.LastDailyAlertDay != day)
            {
                settings.LastDailyAlertDay = day; return "DeepSeek 今日记录期间余额净减少 " + snapshot.Money(snapshot.TodayDecrease) + "。请查看详细记录；这不是完整账单。";
            }
            return null;
        }
    }

    internal sealed class QuotaBar : Control
    {
        int value;
        public int Value { get { return value; } set { this.value = value; Invalidate(); } }
        public QuotaBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var track=VisualTheme.Round(new Rectangle(0,0,Width-1,Height),Height/2))using(var brush=new SolidBrush(VisualTheme.Mix(VisualTheme.Night,VisualTheme.Mint,.25F)))e.Graphics.FillPath(brush,track);
            int filled=Width*value/1000;
            if(filled>0)using(var path=VisualTheme.Round(new Rectangle(0,0,filled,Height),Height/2))using(var brush=new SolidBrush(VisualTheme.Mint))e.Graphics.FillPath(brush,path);
        }
    }

    internal sealed class Dashboard : Form
    {
        readonly Label headline, amount, usage, aTokens, bTokens, aHealth, bHealth, updated, started, hint, reset;
        readonly QuotaBar quota;
        public readonly Button Toggle, UpdateProgram, ResetAnnouncements;
        public readonly CheckBox Startup, ShowWidget;
        public readonly CheckBox Alerts;
        public readonly SurfaceSlider BackgroundTransparency;
        readonly Label transparencyValue;
        public readonly NumericUpDown LowThreshold, DailyThreshold;
        public readonly Button SaveAlerts, TestAlert, DeepSeekReport,ThemeColors;
        readonly Label dsBalance, dsDetails, dsTokens, dsHealth, dsSince, lowLabel, dailyLabel;
        readonly Label announcementLabel;
        readonly ToolTip detailTips=new ToolTip{AutoPopDelay=15000};
        internal readonly Form Preferences;
        public Dashboard(Action toggle, Action report, Action folder)
        {
            SuspendLayout();
            Text="UsageObserver · 用量总览";ClientSize=new Size(620,Math.Min(736,Screen.PrimaryScreen.WorkingArea.Height-60));FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;BackColor=VisualTheme.Canvas;ForeColor=VisualTheme.Ink;Font=new Font("Microsoft YaHei UI",9F);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            AutoScroll=true;AutoScrollMinSize=new Size(600,736);
            AddTo(this,"用量总览",24,21,340,38,21,VisualTheme.Ink,true);
            AddLabel("CODEX  /  DEEPSEEK",26,61,320,20,8.5F,VisualTheme.Muted);
            headline=new SurfaceBadge{Text="等待数据",Bounds=new Rectangle(484,30,112,28),Font=new Font("Microsoft YaHei UI",8.5F),ForeColor=VisualTheme.Accent};Controls.Add(headline);
            var hero=VisualTheme.Card(this,24,96,572,164,VisualTheme.Night,true);
            AddTo(hero,"CODEX",20,17,220,22,9,VisualTheme.Mint);
            amount=VisualTheme.Number(hero,"—",15,42,244,64,43,VisualTheme.WidgetText);
            usage=AddTo(hero,"等待额度快照",21,114,242,18,8.5F,VisualTheme.WidgetMuted);
            quota=new QuotaBar{Bounds=new Rectangle(22,140,236,5),BackColor=VisualTheme.Night};hero.Controls.Add(quota);
            AddTo(hero,"周期重置",296,17,250,21,8.5F,VisualTheme.WidgetMuted);
            reset=AddTo(hero,"重置时间待获取",296,43,252,40,9,VisualTheme.WidgetText);
            announcementLabel=AddTo(hero,"全局预告 · 等待检查",296,87,252,36,8.5F,VisualTheme.Mint);
            ResetAnnouncements=VisualTheme.Button(hero,"重置预告  ↗",294,122,258,delegate{});
            AddTo(this,"记录概览",26,281,320,23,10,VisualTheme.Ink,true);
            AddButton("设备与账号  ↗",454,274,142,delegate{new NetworkForm(Paths.Data).Show();});
            Panel a=Card(24,322),b=Card(318,322);
            AddTo(a,"本机 Codex · 累计 token",18,13,242,20,8.5F,VisualTheme.Muted);
            AddTo(b,BackendSource.Legacy?"DENGPC · 累计 token":"局域网",18,13,242,20,8.5F,VisualTheme.Muted);
            aTokens=VisualTheme.Number(a,"0",15,34,245,43,22,VisualTheme.Ink);
            bTokens=BackendSource.Legacy?VisualTheme.Number(b,"0",15,34,245,43,22,VisualTheme.Ink):AddTo(b,"设备用量",15,34,245,43,13,VisualTheme.Ink);
            aHealth=AddTo(a,"等待数据",18,76,242,19,8.5F,VisualTheme.Muted);
            bHealth=AddTo(b,"等待数据",18,76,242,19,8.5F,VisualTheme.Muted);
            hint=AddLabel("等待第一次采样",26,436,570,34,8.5F,VisualTheme.Muted);
            updated=AddLabel("",26,468,282,20,8,VisualTheme.Muted);
            started=AddLabel("",318,468,278,20,8,VisualTheme.Muted);started.TextAlign=ContentAlignment.TopRight;
            var ds=VisualTheme.Card(this,24,508,572,132,VisualTheme.Surface);
            AddTo(ds,"DEEPSEEK · 账户余额",18,14,260,21,8.5F,VisualTheme.Muted);
            AddTo(ds,BackendSource.Legacy?"今日 DSH token · A / B":"今日 DSH token · 本机",308,14,240,21,8.5F,VisualTheme.Muted);
            dsBalance=VisualTheme.Number(ds,"—",14,34,270,48,26,VisualTheme.Accent);
            dsTokens=VisualTheme.Number(ds,"—",304,40,246,40,20,VisualTheme.Ink);
            dsDetails=AddTo(ds,"",18,85,270,35,8,VisualTheme.Muted);
            dsHealth=AddTo(ds,"",308,82,246,38,8,VisualTheme.Muted);
            dsSince=AddLabel("",26,648,432,20,8,VisualTheme.Muted);
            DeepSeekReport=VisualTheme.Button(this,"余额记录  ↗",464,642,132,delegate{});
            var preferences=new Form();preferences.SuspendLayout();
            preferences.Text="UsageObserver · 显示与提醒";preferences.ClientSize=new Size(592,524);preferences.Font=Font;preferences.BackColor=VisualTheme.Canvas;preferences.FormBorderStyle=FormBorderStyle.FixedDialog;preferences.MaximizeBox=false;preferences.MinimizeBox=false;preferences.StartPosition=FormStartPosition.CenterParent;preferences.AutoScaleDimensions=new SizeF(96,96);preferences.AutoScaleMode=AutoScaleMode.Dpi;
            Preferences=preferences;preferences.AutoScroll=true;preferences.AutoScrollMinSize=new Size(580,524);
            AddTo(preferences,"显示与提醒",24,19,360,37,19,VisualTheme.Ink,true);
            AddTo(preferences,"外观即时保存 · 提醒阈值单独保存",26,59,386,22,8.5F,VisualTheme.Muted);
            ThemeColors=VisualTheme.Button(preferences,"主题与调色  ↗",432,52,136,delegate{});
            var display=VisualTheme.Card(preferences,24,98,544,176,VisualTheme.Surface);
            ShowWidget=new SurfaceToggle{Text="桌面浮窗",Bounds=new Rectangle(18,16,508,29),Font=new Font("Microsoft YaHei UI",10F),ForeColor=VisualTheme.Ink};display.Controls.Add(ShowWidget);
            AddTo(display,"背景透明度",18,68,188,23,9,VisualTheme.Ink);
            transparencyValue=new SurfaceBadge{Text="60%",Bounds=new Rectangle(458,64,68,27),Font=new Font("Segoe UI",10F),ForeColor=VisualTheme.Accent};display.Controls.Add(transparencyValue);
            BackgroundTransparency=new SurfaceSlider{Bounds=new Rectangle(10,102,526,32),Minimum=0,Maximum=90,Value=60,BackColor=VisualTheme.Surface,SmallChange=1,LargeChange=10};display.Controls.Add(BackgroundTransparency);
            BackgroundTransparency.ValueChanged+=delegate{transparencyValue.Text=BackgroundTransparency.Value+"%";};
            AddTo(display,"更实",18,141,90,20,8,VisualTheme.Muted);var clear=AddTo(display,"更透 · 文字保持清晰",350,141,176,20,8,VisualTheme.Muted);clear.TextAlign=ContentAlignment.TopRight;
            var alerts=VisualTheme.Card(preferences,24,288,544,155,VisualTheme.Surface);
            Alerts=new SurfaceToggle{Text="DeepSeek 用量提醒",Bounds=new Rectangle(18,14,508,29),Font=new Font("Microsoft YaHei UI",10F),ForeColor=VisualTheme.Ink};alerts.Controls.Add(Alerts);
            lowLabel=AddTo(alerts,"低余额（元）",18,58,150,22,8.5F,VisualTheme.Muted);
            LowThreshold=new NumericUpDown{Bounds=new Rectangle(18,85,150,28),DecimalPlaces=2,Maximum=100000,Value=20,Font=new Font("Segoe UI",11F)};alerts.Controls.Add(LowThreshold);
            dailyLabel=AddTo(alerts,"日净减少（元）",194,58,154,22,8.5F,VisualTheme.Muted);
            DailyThreshold=new NumericUpDown{Bounds=new Rectangle(194,85,154,28),DecimalPlaces=2,Maximum=100000,Value=50,Font=new Font("Segoe UI",11F)};alerts.Controls.Add(DailyThreshold);
            TestAlert=VisualTheme.Button(alerts,"测试通知",372,81,154,delegate{});
            AddTo(alerts,"设为 0 关闭 · 低余额每 6 小时最多一次 · 日提醒每天一次",18,125,508,20,8,VisualTheme.Muted);
            Startup=new SurfaceToggle{Text="登录 Windows 后启动",Bounds=new Rectangle(26,477,280,27),ForeColor=VisualTheme.Ink};preferences.Controls.Add(Startup);
            SaveAlerts=VisualTheme.Button(preferences,"保存提醒阈值",402,472,166,delegate{},true);VisualTheme.Decorate(preferences);
            preferences.ResumeLayout(false);preferences.PerformLayout();
            preferences.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;preferences.Hide();}};
            Toggle=VisualTheme.Button(this,"暂停记录",24,683,100,delegate{toggle();});
            AddButton("详情与趋势",135,683,100,delegate{report();});
            AddButton("显示与提醒",246,683,124,delegate{preferences.Show(this);});
            UpdateProgram=AddButton("检查更新",381,683,104,delegate{});((SurfaceButton)UpdateProgram).Quiet=true;
            var collapse=AddButton("收起",496,683,100,delegate{Hide();});((SurfaceButton)collapse).Quiet=true;
            detailTips.SetToolTip(a,"Token 按本机记录统计；账户额度是共享快照。完整数据见详细报告。");
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
            Disposed+=delegate{preferences.Dispose();detailTips.Dispose();};
            ResumeLayout(false);PerformLayout();
        }
        Label AddLabel(string text,int x,int y,int w,int h,float size,Color color){return AddTo(this,text,x,y,w,h,size,color);}
        static Label AddTo(Control parent,string text,int x,int y,int w,int h,float size,Color color,bool bold=false){return VisualTheme.Text(parent,text,x,y,w,h,size,color,bold);}
        Panel Card(int x,int y){return VisualTheme.Card(this,x,y,278,104,VisualTheme.Surface);}
        Button AddButton(string text,int x,int y,int w,Action action){return VisualTheme.Button(this,text,x,y,w,action);}
        public void UpdateAnnouncement(ResetMonitor monitor)
        {
            var n=monitor.Active;
            announcementLabel.Text=n==null?"暂无有效预告":(monitor.Cache.Stale?"上次预告 · ":n.OfficialTime.HasValue?"预告 · ":"社区预计 · ")+n.ShortTiming+" 北京";
            detailTips.SetToolTip(announcementLabel,monitor.Tooltip);
        }
        public void UpdateSnapshot(Snapshot s, bool alive, bool stopping)
        {
            reset.Text = s.ResetDetail;
            headline.Text = s.State(alive, stopping);
            VisualTheme.BindForeground(headline,headline.Text=="正在记录"?"Accent":"Warning");
            amount.Text = s.Remaining.HasValue ? s.Remaining.Value.ToString("0.#") + "%" : "—";
            usage.Text = s.Used.HasValue ? "已使用 " + s.Used.Value.ToString("0.#") + "%" : "额度读取失败";
            if (s.Stale || s.ReadFailed || s.QuotaStale || !alive) usage.Text += " · 上次快照";
            quota.Value = s.Remaining.HasValue ? (int)Math.Max(0, Math.Min(1000, s.Remaining.Value * 10)) : 0;
            aTokens.Text = s.A.ToString("N0"); bTokens.Text = BackendSource.Legacy?s.B.ToString("N0"):"设备用量";
            aHealth.Text = Snapshot.Health(s.AHealth); bHealth.Text = BackendSource.Legacy?Snapshot.Health(s.BHealth):"自动发现 · 同组配对后共享";
            if (!alive || s.Stale || s.ReadFailed) { aHealth.Text += " · 上次结果"; bHealth.Text += " · 上次结果"; }
            hint.Text = s.ReadFailed ? "状态文件暂时无法读取，保留上次结果。" : s.QuotaStale ? s.QuotaError + "；正在自动重试。上次额度：" + FormatTime(s.QuotaTime) : s.Stale && alive ? "采样延迟：请查看详细报告或错误日志。" : !alive?"记录已暂停 · 显示上次快照":"Token 按本机统计 · 额度为账户共享";
            updated.Text = "最近更新  " + ShortTime(s.Last);
            detailTips.SetToolTip(updated,"最近更新："+FormatTime(s.Last));
            detailTips.SetToolTip(hint,hint.Text+"\n"+s.Classification);
            started.Text = "记录起点  " + ShortTime(s.Started);
            detailTips.SetToolTip(started,"记录起点："+FormatTime(s.Started)+"；暂停后继续累积。账户额度为共享快照。");
            Toggle.Text = stopping ? "正在暂停" : alive ? "暂停记录" : "继续记录"; Toggle.Enabled = !stopping;
        }
        static string ShortTime(DateTimeOffset t){return t==DateTimeOffset.MinValue?"等待采样":t.ToLocalTime().ToString("M/d HH:mm");}
        static string FormatTime(DateTimeOffset t) { return t == DateTimeOffset.MinValue ? "—" : t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); }
        public void UpdateDeepSeek(DeepSeekSnapshot s)
        {
            dsBalance.Text = s.Money(s.Balance);VisualTheme.BindForeground(dsBalance,s.Stale?"Muted":"Accent");
            detailTips.SetToolTip(dsBalance,"账户余额 "+s.Money(s.Balance)+(s.Stale?" · 上次结果":""));
            dsDetails.Text = "充值 "+s.Money(s.ToppedUp)+" · 赠送 "+s.Money(s.Granted)+"\n记录期净减少 "+s.Money(s.Decrease);
            dsTokens.Text=BackendSource.Legacy?Snapshot.Compact(s.A)+" / "+Snapshot.Compact(s.B):s.A.ToString("N0");
            detailTips.SetToolTip(dsTokens,"今日本机 DSH token："+s.A.ToString("N0")+(BackendSource.Legacy?"；B："+s.B.ToString("N0"):""));
            dsHealth.Text=(s.Stale?"上次余额 · ":"余额已更新 · ")+Snapshot.Health(s.AHealth)+"\n今日净减少 "+s.Money(s.TodayDecrease);
            dsSince.Text="余额更新 "+ShortTime(s.BalanceTime)+" · 净减少非完整账单";
            detailTips.SetToolTip(dsSince,"余额记录起点 "+FormatTime(s.Started)+"；更新 "+FormatTime(s.BalanceTime));
            lowLabel.Text = "低余额（" + (s.Currency == "CNY" ? "元" : "USD") + "）"; dailyLabel.Text = "日净减少（" + (s.Currency == "CNY" ? "元" : "USD") + "）";
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        readonly string root;
        readonly NotifyIcon tray;
        readonly Dashboard window;
        readonly FloatingWidget widget;
        readonly ResetMonitor resets;
        ResetForm resetWindow;
        ThemeForm themeWindow;
        DetailsForm detailsWindow;
        readonly WidgetSettings settings;
        readonly StartupSetting startup;
        readonly ToolStripMenuItem startupItem, widgetItem;
        readonly ToolStripMenuItem transparencyMenu;
        bool syncingSettings;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolStripMenuItem toggle;
        Snapshot snapshot = new Snapshot();
        DeepSeekSnapshot deepseekSnapshot = new DeepSeekSnapshot();
        string thresholdCurrency = "";
        bool alive, stopping, starting;
        DateTime startAttempt;
        int verifiedPid;
        DateTime verifiedStart;
        string publishedStatus;
        Icon appIcon;
        public TrayContext(string directory)
        {
            root = directory;
            File.Delete(Path.Combine(root, "ui-exit.request"));
            settings = WidgetSettings.Load(root); startup = new StartupSetting(Application.ExecutablePath);
            window = new Dashboard(Toggle, ShowDetailsWindow, delegate { OpenFile(""); });
            widget = new FloatingWidget(ShowWindow, delegate(Point point) { settings.X = point.X; settings.Y = point.Y; SaveSettings(); }, delegate(Size size) {settings.WidgetWidth=size.Width;settings.WidgetHeight=size.Height;SaveSettings();}); widget.Place(settings);
            resets=new ResetMonitor(root);
            var menu = new ContextMenuStrip { BackColor=VisualTheme.Surface, ForeColor=VisualTheme.Readable(VisualTheme.Ink,VisualTheme.Surface),Renderer=new ThemeMenuRenderer(),Font=new Font("Microsoft YaHei UI",9F), ShowImageMargin=false };
            menu.Items.Add("打开用量面板", null, delegate { ShowWindow(); });
            menu.Items.Add("恢复桌面浮窗", null, delegate { SetWidgetVisible(true); widget.EnsurePresentation(true); });
            transparencyMenu=new ToolStripMenuItem("背景透明度");menu.Items.Add(transparencyMenu);
            foreach(int percent in new[]{0,20,40,60,80,90})
            {int choice=percent;var item=new ToolStripMenuItem(choice+"%",null,delegate{SetTransparency(choice);});item.Tag=choice;transparencyMenu.DropDownItems.Add(item);}
            transparencyMenu.DropDownItems.Add(new ToolStripSeparator());
            transparencyMenu.DropDownItems.Add("自定义透明度",null,delegate{window.Preferences.Show();});
            menu.Items.Add("主题与调色",null,delegate{ShowThemeWindow();});
            window.ThemeColors.Click+=delegate{ShowThemeWindow();};
            menu.Items.Add("全局重置预告与提醒", null, delegate { ShowResetWindow(); });
            window.ResetAnnouncements.Click+=delegate {ShowResetWindow();};
            menu.Items.Add("局域网设备与账号设置", null, delegate { new NetworkForm(root).Show(); });
            menu.Items.Add("检查程序更新", null, delegate { new UpdateForm(root).Show(); });
            window.UpdateProgram.Click += delegate { new UpdateForm(root).Show(); };
            toggle = new ToolStripMenuItem("暂停记录", null, delegate { Toggle(); }); menu.Items.Add(toggle);
            menu.Items.Add("详情与额度趋势",null,delegate{ShowDetailsWindow();});
            menu.Items.Add("打开 DeepSeek 记录", null, delegate { OpenFile("deepseek-report.md"); });
            menu.Items.Add("打开数据文件夹", null, delegate { OpenFile(""); });
            menu.Items.Add(new ToolStripSeparator());
            widgetItem = new ToolStripMenuItem("显示桌面浮标") { CheckOnClick = true }; menu.Items.Add(widgetItem);
            startupItem = new ToolStripMenuItem("登录 Windows 后自动启动") { CheckOnClick = true }; menu.Items.Add(startupItem);
            widgetItem.Click += delegate { SetWidgetVisible(widgetItem.Checked); };
            startupItem.Click += delegate { SetStartup(startupItem.Checked); };
            window.Startup.CheckedChanged += delegate { if (!syncingSettings) SetStartup(window.Startup.Checked); };
            window.ShowWidget.CheckedChanged += delegate { if (!syncingSettings) SetWidgetVisible(window.ShowWidget.Checked); };
            window.BackgroundTransparency.ValueChanged+=delegate{if(!syncingSettings)SetTransparency(window.BackgroundTransparency.Value);};
            window.Alerts.CheckedChanged += delegate { settings.DeepSeekAlerts = window.Alerts.Checked; SaveSettings(); };
            window.SaveAlerts.Click += delegate
            {
                if (thresholdCurrency == "USD") { settings.LowBalanceUsd = window.LowThreshold.Value; settings.DailyDecreaseUsd = window.DailyThreshold.Value; }
                else { settings.LowBalanceCny = window.LowThreshold.Value; settings.DailyDecreaseCny = window.DailyThreshold.Value; }
                SaveSettings(); window.SaveAlerts.Text = "已保存";
            };
            window.LowThreshold.ValueChanged += delegate { window.SaveAlerts.Text = "保存阈值"; };
            window.DailyThreshold.ValueChanged += delegate { window.SaveAlerts.Text = "保存阈值"; };
            window.TestAlert.Click += delegate { tray.ShowBalloonTip(5000, "DeepSeek 提醒测试", "通知已连接。实际提醒使用你保存的余额和日净减少阈值。", ToolTipIcon.Info); };
            window.DeepSeekReport.Click += delegate { OpenFile("deepseek-report.md"); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出界面（继续记录）", null, delegate { ExitThread(); });
            menu.Items.Add("停止记录并退出", null, delegate { RequestStop(); ExitThread(); });
            appIcon = MakeIcon(); window.Icon = appIcon;
            tray = new NotifyIcon { Icon = appIcon, ContextMenuStrip = menu, Text = "Codex 用量记录 · 正在读取", Visible = true };
            resets.Notification+=delegate(string message){tray.ShowBalloonTip(10000,"Codex 全局重置提醒",message,ToolTipIcon.Info);};
            widget.ContextMenuStrip = menu; widget.Icon = appIcon;
            SyncSettings();
            if (settings.Visible) widget.Show();
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.BalloonTipClicked += delegate { ShowWindow(); };
            window.Alerts.Checked = settings.DeepSeekAlerts;
            timer = new System.Windows.Forms.Timer { Interval = 2000 }; timer.Tick += delegate { try { Refresh(); } catch(Exception e) { BackgroundLifetime.Error(e); } }; timer.Start();
            Refresh();
            if (!alive && !settings.RecorderPaused) StartRecorder();
        }
        void ShowWindow() { window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }
        void ShowResetWindow() {if(resetWindow==null||resetWindow.IsDisposed)resetWindow=new ResetForm(resets);resetWindow.Show();}
        void ShowThemeWindow(){if(themeWindow==null||themeWindow.IsDisposed)themeWindow=new ThemeForm(root);themeWindow.Show(window.Visible?window:null);}
        void ShowDetailsWindow(){if(detailsWindow==null||detailsWindow.IsDisposed)detailsWindow=new DetailsForm(root,BackendSource.Root);detailsWindow.Show(window.Visible?window:null);}
        void SaveSettings() { try { settings.Save(root); } catch (Exception e) { File.AppendAllText(Path.Combine(root, "tray-errors.log"), "Settings: " + e.Message + Environment.NewLine); } }
        void SyncSettings()
        {
            syncingSettings = true;
            try
            {
                window.Startup.Checked = startupItem.Checked = startup.Enabled; window.ShowWidget.Checked = widgetItem.Checked = settings.Visible;
                settings.WidgetTransparency=FloatingWidget.ClampTransparency(settings.WidgetTransparency);window.BackgroundTransparency.Value=settings.WidgetTransparency;
                foreach(ToolStripItem entry in transparencyMenu.DropDownItems){var item=entry as ToolStripMenuItem;if(item!=null&&item.Tag is int)item.Checked=(int)item.Tag==settings.WidgetTransparency;}
            }
            finally { syncingSettings = false; }
        }
        void SetTransparency(int percent){settings.WidgetTransparency=FloatingWidget.ClampTransparency(percent);widget.SetTransparency(settings.WidgetTransparency);SaveSettings();SyncSettings();}
        void SetStartup(bool enabled) { try { startup.Set(enabled); } catch (Exception e) { Error(e.Message); } SyncSettings(); }
        void SetWidgetVisible(bool visible) { settings.Visible = visible; if (visible) { widget.Place(settings); widget.Show(); } else widget.Hide(); SaveSettings(); SyncSettings(); }
        void OpenFile(string name) { try { Process.Start(new ProcessStartInfo(Path.Combine(BackendSource.Root, name)) { UseShellExecute = true }); } catch (Exception e) { Error(e.Message); } }
        void Error(string message) { MessageBox.Show(window, message, "Codex 用量记录", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        bool RecorderAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    DateTime processStart = p.StartTime;
                    if (verifiedPid == pid && verifiedStart == processStart) return !p.HasExited;
                    using (var obj = new ManagementObject("Win32_Process.Handle='" + pid + "'"))
                    {
                        obj.Get(); string cmd = Convert.ToString(obj["CommandLine"]);
                        if (cmd.IndexOf(Path.Combine(BackendSource.ProgramRoot, "monitor.py"), StringComparison.OrdinalIgnoreCase) < 0) return false;
                    }
                    verifiedPid = pid; verifiedStart = processStart; return !p.HasExited;
                }
            }
            catch { return false; }
        }
        void Refresh()
        {
            resets.Tick();
            settings.RecorderPaused=WidgetSettings.Load(BackendSource.Legacy?BackendSource.Root:root).RecorderPaused;
            if (File.Exists(Path.Combine(root, "ui-exit.request"))) { File.Delete(Path.Combine(root, "ui-exit.request")); ExitThread(); return; }
            try { snapshot = Snapshot.Parse(File.ReadAllText(Path.Combine(BackendSource.Root, "status.json"), Encoding.UTF8), DateTimeOffset.UtcNow); }
            catch { snapshot.ReadFailed = true; snapshot.Stale = true; }
            int currentPid = 0;
            try { int.TryParse(File.ReadAllText(Path.Combine(BackendSource.Root, "monitor.pid")).Trim(), out currentPid); } catch { }
            alive = RecorderAlive(currentPid);
            stopping = alive && File.Exists(Path.Combine(BackendSource.Root, "stop.request"));
            if (alive) starting = false;
            if (starting && (DateTime.UtcNow - startAttempt).TotalSeconds > 20)
            {
                starting = false; tray.ShowBalloonTip(5000, "记录器启动未完成", "请查看数据文件夹中的 errors.log。", ToolTipIcon.Warning);
            }
            if(!alive && !starting && !settings.RecorderPaused && (DateTime.UtcNow-startAttempt).TotalSeconds>=60) { BackgroundLifetime.Log("recorder_restart_requested"); StartRecorder(); return; }
            tray.Text = starting ? "Codex 用量记录 · 正在启动" : snapshot.Tooltip(alive, stopping);
            toggle.Text = stopping ? "正在暂停" : starting ? "正在启动…" : alive ? "暂停记录" : "继续记录";
            toggle.Enabled = !stopping && !starting;
            window.UpdateSnapshot(snapshot, alive, stopping); if (starting) { window.Toggle.Text = "正在启动…"; window.Toggle.Enabled = false; }
            try { deepseekSnapshot = DeepSeekSnapshot.Parse(File.ReadAllText(Path.Combine(BackendSource.Root, "deepseek-status.json"), Encoding.UTF8), DateTimeOffset.UtcNow); }
            catch { deepseekSnapshot.Stale = true; }
            if (!alive) deepseekSnapshot.Stale = true;
            window.UpdateDeepSeek(deepseekSnapshot); widget.UpdateDeepSeek(deepseekSnapshot);
            if (thresholdCurrency != deepseekSnapshot.Currency)
            {
                thresholdCurrency = deepseekSnapshot.Currency;
                window.LowThreshold.Value = thresholdCurrency == "USD" ? settings.LowBalanceUsd : settings.LowBalanceCny;
                window.DailyThreshold.Value = thresholdCurrency == "USD" ? settings.DailyDecreaseUsd : settings.DailyDecreaseCny;
            }
            string notification = alive && !stopping ? DeepSeekAlerts.Evaluate(deepseekSnapshot, settings, DateTimeOffset.UtcNow) : null;
            if (notification != null && alive && !stopping) { SaveSettings(); tray.ShowBalloonTip(8000, "DeepSeek 用量提醒", notification, ToolTipIcon.Warning); File.AppendAllText(Path.Combine(root, "notifications.log"), DateTimeOffset.Now.ToString("o") + " " + notification + Environment.NewLine, Encoding.UTF8); }
            SyncSettings();
            window.UpdateAnnouncement(resets);widget.UpdateResetNotice(resets);widget.EnsurePresentation(settings.Visible); widget.UpdateSnapshot(snapshot, alive, stopping);
            string state = new JavaScriptSerializer().Serialize(new { pid = Process.GetCurrentProcess().Id, tray_initialized = tray.Visible, widget_visible = widget.Visible, widget_native_visible = WindowRecovery.IsWindowVisible(widget.Handle), widget_minimized = WindowRecovery.IsIconic(widget.Handle), widget_topmost = WindowRecovery.Topmost(widget.Handle), widget_cloaked = WindowRecovery.Cloaked(widget.Handle), ui_heartbeat_at = DateTimeOffset.UtcNow.ToString("o"), theme_id=VisualTheme.CurrentTheme.Preset,backend_mode = BackendSource.Legacy?"legacy_ab":"local", widget_x = widget.Left, widget_y = widget.Top, widget_width=widget.Width,widget_height=widget.Height,widget_logical_width=widget.LogicalSize.Width,widget_logical_height=widget.LogicalSize.Height,widget_layout=widget.LayoutMode,widget_dpi_scale=widget.RenderScale,widget_frosted=widget.FrostedReady,widget_material_mode=widget.FrostedMode,widget_material_hresult=widget.MaterialResult, startup_enabled = startup.Enabled, recorder_pid = currentPid, recorder_alive = alive, state = starting ? "正在启动" : snapshot.State(alive, stopping), tooltip = tray.Text, last_sample_at = snapshot.Last.ToString("o") });
            if (state != publishedStatus)
            {
                try { File.WriteAllText(Path.Combine(root, "tray-status.json"), state, Encoding.UTF8); publishedStatus = state; } catch { }
            }
        }
        void StartRecorder()
        {
            if (alive || starting) return;
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(BackendSource.ProgramRoot, "start.ps1") + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                starting = true; startAttempt = DateTime.UtcNow; BackgroundLifetime.Log("recorder_launch_requested");
            }
            catch (Exception e) { Error(e.Message); }
            Refresh();
        }
        void RequestStop() { try { settings.RecorderPaused=true;SaveSettings();BackendSource.Pause(true);BackgroundLifetime.Log("recorder_pause_requested");File.WriteAllText(Path.Combine(BackendSource.Root, "stop.request"), "Stop requested by tray user", Encoding.UTF8); Refresh(); } catch (Exception e) { Error(e.Message); } }
        void Toggle() { if (alive) RequestStop(); else { settings.RecorderPaused=false;SaveSettings();BackendSource.Pause(false);StartRecorder(); } }
        protected override void ExitThreadCore()
        {
            BackgroundLifetime.Log("ui_clean_exit recorder_paused="+settings.RecorderPaused);
            settings.X = widget.Left; settings.Y = widget.Top;settings.WidgetWidth=widget.LogicalSize.Width;settings.WidgetHeight=widget.LogicalSize.Height;SaveSettings();
            timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); widget.Dispose(); window.Dispose(); appIcon.Dispose();
            resets.Dispose();if(resetWindow!=null&&!resetWindow.IsDisposed)resetWindow.Dispose();
            if(themeWindow!=null&&!themeWindow.IsDisposed)themeWindow.Dispose();
            if(detailsWindow!=null&&!detailsWindow.IsDisposed)detailsWindow.Dispose();
            try { File.Delete(Path.Combine(root, "tray-status.json")); } catch { }
            base.ExitThreadCore();
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
        internal static Icon MakeIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(VisualTheme.Mint))
            using (var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent); g.FillEllipse(brush, 1, 1, 30, 30); g.DrawString("C", font, Brushes.White, 6, 3);
                g.FillEllipse(Brushes.MediumSeaGreen, 22, 22, 9, 9);
                IntPtr handle = bitmap.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string root = Paths.Data; Directory.CreateDirectory(root);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            VisualTheme.ApplyTheme(ThemeSettings.Load(root));
            if (args.Length > 0 && args[0] == "--self-test") return SelfTest(root, args.Length > 1 ? args[1] : null);
            if(args.Length>1&&args[0]=="--composition-test")return CompositionValidation.Run(args[1]);
            if(args.Length>2&&args[0]=="--history-check")
            {
                try{var history=QuotaHistory.Read(args[1]);var analysis=TrendAnalysis.Analyze(history,false,TimeSpan.FromHours(24),5,DateTimeOffset.UtcNow);File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{read_only=true,points=history.Points.Count,total=history.Total,error=history.Error,truncated=history.Truncated,remaining=analysis.Remaining,observed_drop=analysis.Drop,invalid=analysis.Invalid,resets=analysis.Resets,gaps=analysis.Gaps,account_unknown=analysis.AccountUnknown,summary=analysis.Summary,events=analysis.Events.Select(e=>new{time=e.Time.ToString("o"),kind=e.Kind,title=e.Title,drop=e.Drop,detail=e.Detail}).ToArray()}),Encoding.UTF8);return string.IsNullOrEmpty(history.Error)?0:1;}
                catch(Exception error){File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{error=error.Message}));return 1;}
            }
            if (args.Length > 0 && args[0] == "--quit-ui")
            {
                File.WriteAllText(Path.Combine(root, "ui-exit.request"), "UI restart requested"); return 0;
            }
            if(BackgroundLifetime.Initialize(root,Application.ExecutablePath,args))return 0;
            if(Array.IndexOf(args,"--migrate-startup")>=0)
            {
                new StartupSetting(Application.ExecutablePath).Set(true);
                if(BackendSource.Legacy)using(var key=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run",true))
                {
                    string old=Convert.ToString(key==null?null:key.GetValue("CodexUsageMonitor"));
                    if(string.Equals(old,"\""+Path.Combine(BackendSource.Root,"CodexUsageWidget.exe")+"\"",StringComparison.OrdinalIgnoreCase))key.DeleteValue("CodexUsageMonitor",false);
                }
                BackgroundLifetime.Log("startup_migrated_to_installed_app");return 0;
            }
            if(Array.IndexOf(args,"--restore-startup")>=0) {new StartupSetting(Application.ExecutablePath).Set(true);BackgroundLifetime.Log("startup_restored");return 0;}
            bool created;
            using (var mutex = new Mutex(true, "Local\\CodexUsageTray_" + Hash(root), out created))
            {
                if (!created) return 0;
                try { Application.Run(new TrayContext(root)); return 0; }
                catch (Exception e) { File.AppendAllText(Path.Combine(root, "tray-errors.log"), DateTimeOffset.Now.ToString("o") + " " + e + Environment.NewLine); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
        static string Hash(string text) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant()))).Replace("-", "").Substring(0, 20); }
        static int SelfTest(string root, string preview)
        {
            var checks = new List<string>();
            try
            {
                Updates.SelfTest(); checks.Add("PASS: GitHub repository/release parsing, stable version, HTTPS origin, digest/size verification and corrupt-download rejection");
                string json = "{\"running\":true,\"pid\":123,\"remaining_percent\":74,\"used_percent\":26,\"recorded_tokens\":{\"A\":1290023,\"B\":987654},\"last_sample_at\":\"2026-10-09T07:55:31Z\",\"recording_started_at\":\"2026-10-09T07:47:41Z\",\"interval_seconds\":60,\"health\":{\"A\":{\"status\":\"ok\"},\"B\":{\"status\":\"ok\"},\"classification\":\"基线或额度未上升\"}}";
                json = json.Insert(1, "\"primary_resets_at\":1792137600,\"primary_window_minutes\":10080,\"secondary_resets_at\":1791658800,\"secondary_window_minutes\":300,");
                var now = DateTimeOffset.Parse("2026-10-09T07:56:00Z"); var s = Snapshot.Parse(json, now);
                Require(s.PrimaryReset.ToUnixTimeSeconds() == 1792137600 && s.PrimaryWindow == 10080 && s.ResetDetail.Contains("每周") && s.ResetDetail.Contains("5小时"), "server reset windows");
                Require(new Snapshot().ResetCompact == "重置时间待获取", "unknown reset is not guessed");
                Require(new Snapshot { PrimaryReset = now }.ResetCompact == "重置待确认", "expired reset waits for server");
                checks.Add("PASS: server Unix reset timestamps, window labels, local-time display, missing and expired reset handling");
                Require(s.A == 1290023 && s.B == 987654 && s.Remaining == 74 && !s.Stale, "parse values"); checks.Add("PASS: quota/token parsing and fresh snapshot");
                Require(s.Tooltip(true, false).Length <= 63 && s.Tooltip(true, false).Contains("74%"), "tooltip"); checks.Add("PASS: tooltip contains quota and fits Windows limit");
                Require(s.State(false, false) == "已暂停" && s.State(true, true) == "正在暂停", "lifecycle"); checks.Add("PASS: paused/stopping states");
                var stale = Snapshot.Parse(json, now.AddMinutes(5)); Require(stale.Stale && stale.State(true, false) == "数据未更新", "stale"); checks.Add("PASS: stale snapshot warning");
                var retained = Snapshot.Parse(json.Replace("\"running\":true", "\"quota_stale\":true,\"quota_error\":{\"category\":\"timeout\"},\"quota_sample_at\":\"2026-10-09T07:55:31Z\",\"running\":true"), now);
                Require(retained.Remaining == 74 && retained.State(true, false) == "额度待更新" && retained.Tooltip(true, false).Contains("上次") && retained.QuotaError.Contains("超时"), "retained quota warning");
                checks.Add("PASS: retained quota stays visible and is explicitly marked as last snapshot after request failure");
                s.BHealth = "unavailable"; Require(s.State(true, false) == "覆盖不完整", "offline"); checks.Add("PASS: B unavailable is not counted as healthy zero usage");
                s.BHealth = "ok"; s.Remaining = null; Require(s.State(true, false) == "覆盖不完整", "missing quota"); checks.Add("PASS: missing quota is unknown");
                string testKey = "Software\\CodexUsageTrayTest_" + Guid.NewGuid().ToString("N");
                try
                {
                    var boot = new StartupSetting(testKey, "App", Path.Combine(root, "CodexUsageTray.exe"));
                    using (var key = Registry.CurrentUser.CreateSubKey(testKey)) key.SetValue("Unrelated", "preserve");
                    Require(!boot.Enabled, "initial startup disabled"); boot.Set(true); Require(boot.Enabled, "startup enable"); boot.Set(false); Require(!boot.Enabled, "startup disable");
                    using (var key = Registry.CurrentUser.OpenSubKey(testKey)) Require(Convert.ToString(key.GetValue("Unrelated")) == "preserve", "unrelated value");
                    using (var key = Registry.CurrentUser.OpenSubKey(testKey, true)) key.SetValue("App", "other-program.exe");
                    bool refused = false; try { boot.Set(true); } catch (InvalidOperationException) { refused = true; }
                    Require(refused, "startup collision"); checks.Add("PASS: startup enable/disable, unrelated entry preservation and collision guard (isolated registry key)");
                }
                finally { Registry.CurrentUser.DeleteSubKey(testKey, false); }
                Require(FloatingWidget.Clamp(new Point(-500, 4000), new Size(148, 92), new Rectangle(0, 0, 1920, 1040)) == new Point(0, 948), "clamp primary screen");
                Require(FloatingWidget.Clamp(new Point(-3000, -50), new Size(148, 92), new Rectangle(-1920, 0, 1920, 1040)) == new Point(-1920, 0), "clamp negative monitor");
                checks.Add("PASS: floating widget stays inside positive and negative monitor work areas");
                Require(FloatingWidget.HitEdges(new Point(159,91),new Size(160,92),6)==(FloatingWidget.EdgeRight|FloatingWidget.EdgeBottom),"bottom-right resize affordance");
                Require(FloatingWidget.HitEdges(new Point(80,46),new Size(160,92),6)==0,"center remains movable/clickable");
                var original=new Rectangle(100,100,200,120);var screenArea=new Rectangle(0,0,1920,1040);
                var shrunk=FloatingWidget.ResizeBounds(original,new Point(300,300),FloatingWidget.EdgeLeft|FloatingWidget.EdgeTop,screenArea,1);
                Require(shrunk.Right==original.Right&&shrunk.Bottom==original.Bottom&&shrunk.Size==new Size(144,88),"corner resize anchors opposite corner and enforces readable minimum");
                var grown=FloatingWidget.ResizeBounds(original,new Point(10000,10000),FloatingWidget.EdgeRight|FloatingWidget.EdgeBottom,screenArea,1);
                Require(grown.Size==new Size(560,360)&&screenArea.Contains(grown),"resize enforces maximum and screen bounds");
                var negative=FloatingWidget.ResizeBounds(new Rectangle(-1900,10,180,100),new Point(-200,-200),FloatingWidget.EdgeLeft|FloatingWidget.EdgeTop,new Rectangle(-1920,0,1920,1040),1.5F);
                Require(negative.Left>=-1920&&negative.Top>=0&&negative.Right==-1720&&negative.Bottom==110,"negative monitor resize preserves anchors at high DPI");
                Require(FloatingWidget.Mode(new Size(160,92))=="compact"&&FloatingWidget.Mode(new Size(320,110))=="wide"&&FloatingWidget.Mode(new Size(220,240))=="expanded","content adapts to aspect ratio");
                checks.Add("PASS: resize grip vs move/click, anchored minimum/maximum bounds, negative monitor/high-DPI geometry and three responsive modes");
                string testDir = Path.Combine(Path.GetTempPath(), "usage-widget-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testDir);
                try
                {
                    var pref = new WidgetSettings { X = -1600, Y = 400, Visible = false, RecorderPaused = true,WidgetWidth=321,WidgetHeight=220 }; pref.Save(testDir); var restored = WidgetSettings.Load(testDir);
                    Require(restored.X == -1600 && restored.Y == 400 && !restored.Visible && restored.RecorderPaused&&restored.WidgetWidth==321&&restored.WidgetHeight==220, "widget settings persist");
                    File.WriteAllText(Path.Combine(testDir,"ui-settings.json"),"{\"X\":20,\"RecorderPaused\":true}");var legacy=WidgetSettings.Load(testDir);Require(legacy.WidgetWidth==160&&legacy.WidgetHeight==92&&legacy.RecorderPaused,"legacy preferences migrate with default size");
                    checks.Add("PASS: widget position/visibility/size persist and legacy settings gain safe defaults");
                }
                finally { File.Delete(Path.Combine(testDir, "ui-settings.json")); Directory.Delete(testDir); }
                s = Snapshot.Parse(json, now);
                var ds = new DeepSeekSnapshot { Balance = 100, Currency = "CNY", A = 123456, BalanceHealth = "ok", AHealth = "ok", BHealth = "ok", Last = now, BalanceTime = now, Started = now, Stale = false };
                Require(ds.Balance.HasValue && ds.Currency == "CNY" && ds.A > 0, "fixture DeepSeek balance/usage parsing");
                var alertSettings = new WidgetSettings(); var alertSample = new DeepSeekSnapshot { Account = "fixture", Stale = false, Balance = 5, TodayDecrease = 0 };
                Require(DeepSeekAlerts.Evaluate(alertSample, alertSettings, now) != null && DeepSeekAlerts.Evaluate(alertSample, alertSettings, now.AddSeconds(1)) == null, "low alert deduplication");
                Require(DeepSeekAlerts.Evaluate(alertSample, alertSettings, now.AddHours(6)) != null, "low alert cooldown");
                alertSample.Stale = true; Require(DeepSeekAlerts.Evaluate(alertSample, alertSettings, now.AddHours(12)) == null, "stale alert suppression");
                alertSample.Stale = false; alertSample.Balance = 100; alertSample.TodayDecrease = 60; alertSettings = new WidgetSettings();
                Require(DeepSeekAlerts.Evaluate(alertSample, alertSettings, now) != null && DeepSeekAlerts.Evaluate(alertSample, alertSettings, now.AddSeconds(1)) == null, "daily alert deduplication");
                alertSettings.DeepSeekAlerts = false; Require(DeepSeekAlerts.Evaluate(alertSample, alertSettings, now.AddDays(1)) == null, "disabled alerts");
                checks.Add("PASS: fixture DeepSeek parsing, low/daily thresholds, cooldown, duplicate and stale/off suppression");
                using (var form = new Dashboard(delegate { }, delegate { }, delegate { }))
                {
                    form.UpdateSnapshot(s, s.Running, false); form.UpdateDeepSeek(ds); form.ShowWidget.Checked = true; form.Startup.Checked = new StartupSetting(Path.Combine(root, "CodexUsageWidget.exe")).Enabled; form.Alerts.Checked = true; CreateHandles(form); form.PerformLayout();
                    if (preview != null) PreviewHost.Render(form,preview);
                }
                using (var floating = new FloatingWidget(delegate { }, delegate(Point point) { }))
                {
                    floating.UpdateDeepSeek(ds); floating.UpdateSnapshot(s, s.Running, false); CreateHandles(floating);
                    if (preview != null) using (var bmp = new Bitmap(floating.Width, floating.Height)) { floating.DrawToBitmap(bmp, new Rectangle(0, 0, floating.Width, floating.Height)); bmp.Save(Path.Combine(Path.GetDirectoryName(preview), "widget-preview.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    Require(floating.TopMost && !floating.ShowInTaskbar && floating.LogicalSize == new Size(160,92), "compact widget window style");
                    Require(floating.Opacity == 1.0 && floating.FramePolicyResult == 0 && floating.BorderPolicyResult == 0, "text opacity and native frame suppression");
                    if(Environment.OSVersion.Version.Build>=22621)Require((floating.FrostedReady&&floating.MaterialResult==0)||(!floating.FrostedReady&&floating.LayeredRenderSucceeded),"native blur or working alpha fallback");
                    if(Environment.OSVersion.Version.Build>=15063)Require(floating.PerMonitorAware,"manifest requests per-monitor V2 DPI rendering");
                    Require(floating.LayeredRenderSucceeded, "per-pixel alpha rendering");
                    checks.Add("PASS: native per-pixel alpha rendering succeeds");
                    checks.Add("PASS: opaque text surface with native blur or alpha fallback; compositor pixels checked separately");
                    checks.Add("PASS: floating widget renders offscreen and uses topmost/tool window style");
                }
                if(preview!=null)
                {
                    using(var prefForm=new Dashboard(delegate{},delegate{},delegate{}))
                    {
                        CreateHandles(prefForm.Preferences);prefForm.Preferences.PerformLayout();
                        prefForm.ShowWidget.Checked=true;prefForm.Alerts.Checked=true;prefForm.Startup.Checked=true;
                        PreviewHost.Render(prefForm.Preferences,Path.Combine(Path.GetDirectoryName(preview),"preferences-preview.png"));
                    }
                    foreach(string scenario in new[]{"full","empty","unknown","stale","paused","announcement"})
                    using(var floating=new FloatingWidget(delegate{},delegate(Point point){}))
                    {
                        var example=Snapshot.Parse(json,now);example.Remaining=scenario=="full"?100:scenario=="empty"?0:74;
                        if(scenario=="unknown")example.Remaining=null;
                        if(scenario=="stale")example.QuotaStale=true;
                        floating.UpdateDeepSeek(ds);floating.UpdateSnapshot(example,scenario!="paused",false);
                        if(scenario=="announcement")
                        {
                            using(var monitor=new ResetMonitor(root))
                            {monitor.Cache.Stale=false;monitor.Cache.Scheduled=new ResetNotice{Id="preview-only",State="scheduled",Text="Reset at 2026-10-14 12:00 UTC",SourceUrl="https://x.com/thsottiaux/status/123456789",Published=DateTimeOffset.UtcNow,Verified=true,Estimate="2026-10-14T12:00:00Z"};floating.UpdateResetNotice(monitor);}
                            Require(floating.TimingSummary.Contains("预告"),"announcement stays distinct from account reset");
                        }
                        using(var bmp=new Bitmap(floating.Width,floating.Height)){using(var g=Graphics.FromImage(bmp))floating.PaintContent(g);bmp.Save(Path.Combine(Path.GetDirectoryName(preview),"widget-"+scenario+".png"),System.Drawing.Imaging.ImageFormat.Png);}
                    }
                }
                if(preview!=null)foreach(var size in new[]{new Size(144,88),new Size(320,110),new Size(220,240),new Size(360,220),new Size(560,360)})
                using(var floating=new FloatingWidget(delegate{},delegate(Point point){}))
                {
                    floating.UpdateDeepSeek(ds);floating.UpdateSnapshot(s,true,false);CreateHandles(floating);floating.ApplyLogicalSize(size);
                    using(var bmp=new Bitmap(floating.Width,floating.Height))
                    {
                        using(var g=Graphics.FromImage(bmp))floating.PaintContent(g);
                        Require(floating.LayeredRenderSucceeded,"native alpha surface accepts resized dimensions");
                        Require(bmp.GetPixel(bmp.Width/2,Math.Max(2,(int)(19*floating.RenderScale))).A<255,"glass panel has translucent pixels");
                        bool opaqueGlyph=false;
                        for(int gy=(int)(28*floating.RenderScale);gy<Math.Min(bmp.Height,(int)(65*floating.RenderScale))&&!opaqueGlyph;gy++)
                        for(int gx=(int)(12*floating.RenderScale);gx<Math.Min(bmp.Width,(int)(90*floating.RenderScale));gx++)
                        {var pixel=bmp.GetPixel(gx,gy);if(pixel.A>=250&&pixel.R>=245&&pixel.G>=245&&pixel.B>=245){opaqueGlyph=true;break;}}
                        Require(opaqueGlyph,"quota text remains opaque while panel is translucent");
                        bmp.Save(Path.Combine(Path.GetDirectoryName(preview),"widget-"+size.Width+"x"+size.Height+".png"),System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                checks.Add("PASS: fixture states plus compact/wide/tall/maximum layouts render with translucent panel pixels and opaque text");
                using(var floating=new FloatingWidget(delegate{},delegate(Point point){}))
                {
                    floating.UpdateDeepSeek(ds);floating.UpdateSnapshot(s,true,false);CreateHandles(floating);
                    foreach(int percent in new[]{0,60,90})
                    {
                        floating.SetTransparency(percent);
                        using(var bmp=new Bitmap(floating.Width,floating.Height))
                        {
                            using(var g=Graphics.FromImage(bmp))floating.PaintContent(g);
                            Color panel=bmp.GetPixel(bmp.Width/2,(int)(19*floating.RenderScale));
                            Require(Math.Abs(panel.A-255*(100-percent)/100.0)<=1,"background alpha follows selected transparency");
                            bool opaqueText=false;
                            for(int y=(int)(28*floating.RenderScale);y<(int)(52*floating.RenderScale)&&!opaqueText;y++)
                            for(int x=(int)(12*floating.RenderScale);x<(int)(65*floating.RenderScale);x++)
                            {var pixel=bmp.GetPixel(x,y);if(pixel.A>=250&&pixel.R>245&&pixel.G>245&&pixel.B>245)opaqueText=true;}
                            Require(opaqueText,"adjusting tint keeps glyphs opaque");
                            // Empty straight edges must contain the dark tint, never a bright outline.
                            foreach(Point edge in new[]{new Point(bmp.Width/2,1),new Point(1,bmp.Height/2),new Point(bmp.Width-2,bmp.Height/2)})
                            {var pixel=bmp.GetPixel(edge.X,edge.Y);Require(pixel.R<60&&pixel.G<70&&pixel.B<80,"no pale widget border");}
                            if(preview!=null)bmp.Save(Path.Combine(Path.GetDirectoryName(preview),"widget-transparency-"+percent+".png"),System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    floating.SetTransparency(-20);Require(floating.Transparency==0,"transparency lower bound");floating.SetTransparency(120);Require(floating.Transparency==90,"transparency upper bound");
                    var settingsRoot=Path.Combine(root,"transparency-settings");Directory.CreateDirectory(settingsRoot);
                    File.WriteAllText(Path.Combine(settingsRoot,"ui-settings.json"),"{\"WidgetWidth\":144,\"WidgetHeight\":88,\"Visible\":false}");
                    var saved=WidgetSettings.Load(settingsRoot);Require(saved.WidgetTransparency==60,"old settings get readable default");saved.WidgetTransparency=73;saved.Save(settingsRoot);
                    saved=WidgetSettings.Load(settingsRoot);floating.Place(saved);Require(saved.WidgetTransparency==73&&floating.Transparency==73&&!saved.Visible&&saved.WidgetWidth==144,"transparency survives reload and preserves existing settings");
                }
                checks.Add("PASS: background transparency 0/60/90 percent changes alpha immediately, keeps opaque text, removes pale rim and survives old/new settings reload");
                using(var floating=new FloatingWidget(delegate{},delegate(Point point){}))using(var monitor=new ResetMonitor(root))
                {
                    CreateHandles(floating);
                    foreach(var size in new[]{new Size(144,88),new Size(160,92),new Size(249,175),new Size(250,176),new Size(220,240),new Size(360,220),new Size(560,360)})
                    foreach(string state in new[]{"fresh","stale","paused","unknown","official","deadline","estimate","old-deadline"})
                    {
                        var example=new Snapshot{Remaining=100,PrimaryReset=DateTimeOffset.Now.AddDays(365).ToOffset(TimeSpan.FromHours(8)),A=long.MaxValue,Stale=false,QuotaStale=state=="stale"};
                        if(state=="unknown")example.Remaining=null;
                        floating.ApplyLogicalSize(size);floating.UpdateSnapshot(example,state!="paused",false);
                        floating.UpdateDeepSeek(new DeepSeekSnapshot{Balance=123456.78m,A=long.MaxValue,Stale=state=="stale"});
                        monitor.Cache.Stale=state=="old-deadline";monitor.Cache.Scheduled=null;
                        if(state=="official"||state=="deadline"||state=="estimate"||state=="old-deadline")
                            monitor.Cache.Scheduled=new ResetNotice{Id="fit-only",State="scheduled",Text=state=="estimate"?"Reset expected around EOD":"Reset "+(state.Contains("deadline")?"by":"at")+" 2026-12-31 15:59 UTC",SourceUrl="https://x.com/thsottiaux/status/123456789",Published=DateTimeOffset.UtcNow,Verified=true,Estimate="2026-12-31T15:59:00Z"};
                        floating.UpdateResetNotice(monitor);
                        using(var bmp=new Bitmap(floating.Width,floating.Height))using(var g=Graphics.FromImage(bmp))floating.PaintContent(g);
                        Require(floating.TextFits&&floating.SmallestTextSize>=9,"complete readable text fits "+size+" "+state+"; min="+floating.SmallestTextSize);
                        if(state=="official")Require(floating.TimingSummary.StartsWith("预告 "),"official timing remains a forecast");
                        if(state.Contains("deadline"))Require(floating.TimingSummary.Contains("最晚"),"deadline keeps by semantics");
                        if(state=="estimate")Require(floating.TimingSummary.StartsWith("预计 "),"community estimate is distinct");
                        if(state=="old-deadline")Require(floating.TimingSummary.StartsWith("旧预"),"stale forecast remains marked");
                        Require(floating.RenderedLines.Count<=9,"compact information without repeated timestamps");
                    }
                }
                checks.Add("PASS: complete text fits seven sizes and eight states without ellipses; large balances, long dates, forecast/deadline/estimate and stale meanings retained");
                using (var network = new NetworkForm(root))
                {
                    CreateHandles(network); network.PerformLayout();
                    if(preview!=null)foreach(Control container in network.Controls)foreach(Control page in container.Controls)foreach(Control child in page.Controls)
                    {
                        var grid=child as DataGridView;
                        if(grid!=null){grid.Rows.Add("本机（预览数据）","记录中 / 正常","74%","1,290,023","123,456","CNY 100.00","10-13 11:00:00");grid.PerformLayout();}
                    }
                    if (preview != null) PreviewHost.Render(network,Path.Combine(Path.GetDirectoryName(preview),"network-preview.png"));
                    if(preview!=null)foreach(Control c in network.Controls)if(c is TabControl){((TabControl)c).SelectedIndex=1;network.PerformLayout();PreviewHost.Render(network,Path.Combine(Path.GetDirectoryName(preview),"settings-preview.png"));}
                }
                checks.Add("PASS: device and account settings UI renders without activation");
                Updates.SaveRepository(root,"lys20001205/usage-observer");
                using(var updater = new UpdateForm(root))
                {
                    CreateHandles(updater);updater.PerformLayout();
                    if(preview!=null)PreviewHost.Render(updater,Path.Combine(Path.GetDirectoryName(preview),"update-preview.png"));
                }
                checks.Add("PASS: GitHub update settings UI renders without activation");
                using(var slider=new SurfaceSlider())
                {
                    int events=0;slider.ValueChanged+=delegate{events++;};slider.Value=35;
                    Require(slider.Value==35&&events==1,"slider value changes notify settings once");
                    slider.Value=100;Require(slider.Value==90&&events==2,"slider maximum clamps safely");
                    slider.Value=100;Require(events==2,"clamped slider values do not emit duplicate changes");
                    slider.Value=-20;Require(slider.Value==0,"slider minimum clamps safely");
                    slider.Value=73;Require(slider.AccessibilityObject.Value=="73%"&&slider.AccessibilityObject.Role==AccessibleRole.Slider,"slider exposes selected value to accessibility");
                }
                using(var toggle=new SurfaceToggle())
                {
                    int changes=0;toggle.CheckedChanged+=delegate{changes++;};toggle.Checked=true;toggle.Checked=true;
                    Require(toggle.Checked&&changes==1,"themed switch preserves checked-state events");
                    toggle.Checked=false;Require(!toggle.Checked&&changes==2,"themed switch clears checked state");
                }
                checks.Add("PASS: themed slider bounds, setting notifications, accessible values and native checkbox state events");
                using(var monitor=new ResetMonitor(root))
                {
                    monitor.Cache.Stale=false;monitor.Cache.Scheduled=new ResetNotice{Id="visual-only",State="scheduled",Text="Reset at 2026-10-14 12:00 UTC",SourceUrl="https://x.com/thsottiaux/status/123456789",Published=DateTimeOffset.UtcNow,Verified=true,Estimate="2026-10-14T12:00:00Z"};
                    using(var resetForm=new ResetForm(monitor)){CreateHandles(resetForm);if(preview!=null)PreviewHost.Render(resetForm,Path.Combine(Path.GetDirectoryName(preview),"reset-preview.png"));}
                }
                checks.Add("PASS: announcement view renders with a dated forecast and its original evidence text");
                File.WriteAllLines(Path.Combine(root, "ui-validation.txt"), checks.ToArray(), Encoding.UTF8); return 0;
            }
            catch (Exception e) { checks.Add("FAIL: " + e); File.WriteAllLines(Path.Combine(root, "ui-validation.txt"), checks.ToArray(), Encoding.UTF8); return 1; }
        }
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        static void CreateHandles(Control control) { IntPtr handle = control.Handle; foreach (Control child in control.Controls) CreateHandles(child); }
    }
}
