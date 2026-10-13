using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal static class Paths
    {
        public static string Data { get { return Environment.GetEnvironmentVariable("USAGE_OBSERVER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageObserver"); } }
    }
    internal sealed class NetworkForm : Form
    {
        readonly string root;
        readonly DataGridView devices;
        readonly Label state;
        readonly CheckBox enabled;
        readonly TextBox codexExe, codexHome, dshHome, joinCode;
        readonly System.Windows.Forms.Timer timer;
        static Dictionary<string, object> Map(object x) { return x as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static object Get(Dictionary<string, object> d,string k) { object v;return d.TryGetValue(k,out v)?v:null; }
        static string Str(Dictionary<string, object> d,string k) { return Convert.ToString(Get(d,k)); }
        static Dictionary<string, object> Read(string p) { try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(p,Encoding.UTF8)); } catch { return new Dictionary<string, object>(); } }
        static string When(object x) { DateTimeOffset t;return DateTimeOffset.TryParse(Convert.ToString(x),out t)?t.ToLocalTime().ToString("MM-dd HH:mm:ss"):"—"; }
        public NetworkForm(string data)
        {
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;root=data;Text="UsageObserver · 设备与账号";ClientSize=new Size(920,668);BackColor=VisualTheme.Canvas;Font=new Font("Microsoft YaHei UI",9);StartPosition=FormStartPosition.CenterScreen;
            VisualTheme.Text(this,"设备与账号",24,20,580,38,21,VisualTheme.Ink,true);
            VisualTheme.Text(this,"每台设备独立记录，同一局域网内安全共享",26,63,850,22,8.5F,VisualTheme.Muted);
            MinimumSize=new Size(936,707);
            var tabs=new TabControl {Bounds=new Rectangle(16,101,888,548),Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right};Controls.Add(tabs);
            var page=new TabPage("设备");var prefs=new TabPage("配对与账号");tabs.TabPages.Add(page);tabs.TabPages.Add(prefs);
            state=new Label { Location=new Point(20,18),Size=new Size(838,27),ForeColor=VisualTheme.Accent };page.Controls.Add(state);
            var info=new Label { Location=new Point(20,54),Size=new Size(838,40),ForeColor=VisualTheme.Muted,Font=new Font("Microsoft YaHei UI",8.5F),Text="自动发现同一局域网内运行本程序的电脑；同组配对后共享摘要。\n每台电脑的记录起点独立；token 是该电脑所选日志目录的记录，可包含多个登录账户。额度/余额分别显示，不能相加。" };page.Controls.Add(info);
            devices=new DataGridView { Location=new Point(20,107),Size=new Size(838,291),ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect };page.Controls.Add(devices);
            foreach(string name in new[]{"设备 / 地址","连接状态","额度","Codex token","DSH 今日","DSH 余额","记录起点 / 更新"})devices.Columns.Add(name,name);
            devices.Columns[0].FillWeight=125;devices.Columns[1].FillWeight=130;devices.Columns[2].FillWeight=60;devices.Columns[6].FillWeight=145;
            devices.DefaultCellStyle.WrapMode=DataGridViewTriState.True;devices.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;devices.RowTemplate.Height=48;
            var help=new Label { Location=new Point(20,419),Size=new Size(838,70),ForeColor=VisualTheme.Muted,Font=new Font("Microsoft YaHei UI",8.5F),Text="发现不到：确认两台电脑都已运行、网络是“专用”，并允许程序使用专用网络防火墙。访客 Wi-Fi / AP 隔离 / 不同 VLAN 可能阻止发现。\n配对码只用于这个软件，不是 Codex 或 DSH 的密码；退出配对后不再接受原组摘要。" };page.Controls.Add(help);
            Action fitDevices=delegate{devices.Bounds=new Rectangle(20,107,Math.Max(500,page.ClientSize.Width-40),Math.Max(120,page.ClientSize.Height-213));help.Bounds=new Rectangle(20,devices.Bottom+21,devices.Width,70);};
            page.SizeChanged+=delegate{fitDevices();};fitDevices();
            var cfg=Read(Path.Combine(root,"connection-settings.json"));
            enabled=new SurfaceToggle {Text="局域网发现与同组摘要共享",Location=new Point(22,18),Size=new Size(820,30),ForeColor=VisualTheme.Ink,Checked=Get(cfg,"lan_enabled")==null||Convert.ToBoolean(Get(cfg,"lan_enabled"))};prefs.Controls.Add(enabled);
            AddButton(prefs,"显示本机配对码",22,65,180,delegate { ShowCode(); });
            AddButton(prefs,"生成新配对组",218,65,180,delegate { byte[] key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);SaveKey(key);Save();ShowCode(); });
            prefs.Controls.Add(new Label {Text="在其他电脑输入上面的配对码，然后点击加入；所有电脑各自读取本机登录账户。",Location=new Point(22,107),Size=new Size(820,24),ForeColor=VisualTheme.Muted,Font=new Font("Microsoft YaHei UI",8.5F)});
            joinCode=new TextBox {Location=new Point(22,141),Size=new Size(600,28),UseSystemPasswordChar=true};prefs.Controls.Add(joinCode);
            AddButton(prefs,"加入此配对组",640,137,202,delegate {
                try { string code=joinCode.Text.Trim().Replace('-','+').Replace('_','/');code=code.PadRight((code.Length+3)/4*4,'=');byte[] key=Convert.FromBase64String(code);if(key.Length!=32)throw new FormatException();SaveKey(key);joinCode.Clear();Save();MessageBox.Show(this,"已加入配对组，下一次采样生效（通常 60 秒内）。","配对完成"); }
                catch { MessageBox.Show(this,"请输入完整配对码（43 个字符）。","配对码无效"); }
            });
            prefs.Controls.Add(new Label {Text="自动检测本机安装和登录状态。特殊目录可在下面指定；留空恢复自动检测。不会登录、退出或复制账号凭据。",Location=new Point(22,191),Size=new Size(820,38),ForeColor=VisualTheme.Muted,Font=new Font("Microsoft YaHei UI",8.5F)});
            codexExe=Field(prefs,"Codex 程序（codex.exe）",239,Str(cfg,"codex_exe"),false);
            codexHome=Field(prefs,"Codex 日志目录（CODEX_HOME）",296,Str(cfg,"codex_home"),true);
            dshHome=Field(prefs,"DSH 数据目录（含 sessions 和登录凭据）",353,Str(cfg,"dsh_home"),true);
            AddButton(prefs,"保存设置",22,419,180,delegate {try{Save();MessageBox.Show(this,"设置已保存，下次采样生效。查看本机报告可见实际检测路径。","已保存");}catch(Exception e){MessageBox.Show(this,e.Message,"保存失败");}});
            AddButton(prefs,"打开本机数据",218,419,180,delegate {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root){UseShellExecute=true});});
            prefs.Controls.Add(new Label {Text="余额读取失败：先在本机 DSH 登录；额度读取失败：先在本机 Codex 登录。API Key 模式、无权限账户或不同版本可能没有订阅额度接口。\n新安装不会包含其他电脑的账户、历史数据或配对码。网络摘要使用加密；配对码在本机用 Windows 用户保护保存。",Location=new Point(22,470),Size=new Size(820,40),ForeColor=VisualTheme.Muted,Font=new Font("Microsoft YaHei UI",8F)});
            VisualTheme.Decorate(this);
            timer=new System.Windows.Forms.Timer {Interval=2000};timer.Tick+=delegate {RefreshDevices();};timer.Start();FormClosed+=delegate {timer.Dispose();};RefreshDevices();
        }
        static Button AddButton(Control parent,string text,int x,int y,int width,Action action) {return VisualTheme.Button(parent,text,x,y,width,action,text=="保存设置"||text=="加入此配对组");}
        TextBox Field(Control parent,string label,int y,string value,bool folder)
        {
            parent.Controls.Add(new Label {Text=label,Location=new Point(22,y),Size=new Size(820,22)});
            var box=new TextBox {Text=value,Location=new Point(22,y+25),Size=new Size(708,26)};parent.Controls.Add(box);
            AddButton(parent,"浏览",746,y+20,96,delegate {if(folder){using(var dialog=new FolderBrowserDialog())if(dialog.ShowDialog(this)==DialogResult.OK)box.Text=dialog.SelectedPath;}else{using(var dialog=new OpenFileDialog {Filter="Codex 程序|codex.exe"})if(dialog.ShowDialog(this)==DialogResult.OK)box.Text=dialog.FileName;}});return box;
        }
        void SaveKey(byte[] key) {File.WriteAllBytes(Path.Combine(root,"lan-key.dpapi"),ProtectedData.Protect(key,null,DataProtectionScope.CurrentUser));}
        void Save()
        {
            if(codexExe.Text.Trim().Length>0&&!File.Exists(codexExe.Text.Trim()))throw new IOException("Codex 程序路径不存在。");
            foreach(var b in new[]{codexHome,dshHome})if(b.Text.Trim().Length>0&&!Directory.Exists(b.Text.Trim()))throw new IOException("数据目录不存在："+b.Text.Trim());
            var cfg=new { lan_enabled=enabled.Checked,codex_exe=codexExe.Text.Trim(),codex_home=codexHome.Text.Trim(),dsh_home=dshHome.Text.Trim(),revision=DateTimeOffset.UtcNow.ToString("o")};
            var p=Path.Combine(root,"connection-settings.json");File.WriteAllText(p+".tmp",new JavaScriptSerializer().Serialize(cfg),Encoding.UTF8);if(File.Exists(p))File.Delete(p);File.Move(p+".tmp",p);
        }
        void ShowCode()
        {
            try
            {
                var p=Path.Combine(root,"lan-key.dpapi");if(!File.Exists(p)){byte[] initial=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(initial);SaveKey(initial);}
                byte[] key=ProtectedData.Unprotect(File.ReadAllBytes(p),null,DataProtectionScope.CurrentUser);
                using(var form=new Form {Text="本机配对码",ClientSize=new Size(530,150),StartPosition=FormStartPosition.CenterParent})
                {
                    form.Controls.Add(new Label {Text="将此码输入同网其他电脑的本程序；请只分享给你自己的设备。",Location=new Point(15,15),Size=new Size(500,28)});
                    var box=new TextBox {ReadOnly=true,Text=Convert.ToBase64String(key).TrimEnd('=').Replace('+','-').Replace('/','_'),Location=new Point(15,52),Size=new Size(500,28)};form.Controls.Add(box);
                    AddButton(form,"复制配对码",15,96,150,delegate {Clipboard.SetText(box.Text);});form.ShowDialog(this);
                }
            }catch{MessageBox.Show(this,"无法读取配对码，请生成新的配对组。","配对设置");}
        }
        static string Quota(Dictionary<string,object> s) {var value=Get(s,"remaining_percent");return value==null?"未知":Convert.ToString(value)+"%"+(Convert.ToBoolean(Get(s,"quota_stale"))?" 上次":"");}
        static string Wallet(Dictionary<string,object> s)
        {
            var rows=Get(s,"wallets") as IEnumerable;var results=new List<string>();if(rows!=null)foreach(var row in rows){var w=Map(row);decimal amount;string value=Str(w,"total_balance");if(decimal.TryParse(value,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out amount))value=amount.ToString("0.00");results.Add(Str(w,"currency")+" "+value);}
            return results.Count==0?"未知":string.Join(" / ",results.ToArray())+(Str(s,"balance_health")!="ok"?" 上次":"");
        }
        void RefreshDevices()
        {
            var lan=Read(Path.Combine(root,"lan-status.json"));string network=Str(lan,"status");state.Text="局域网："+(network=="listening"?"正在发现":network=="disabled"?"已关闭":network=="no_private_network"?"没有可用专用 IPv4 网络":network=="EADDRINUSE"?"端口被占用（UDP 47639）":network.Length==0?"等待记录器启动":network)+"；更新时间 "+When(Get(lan,"updated_at"));
            devices.Rows.Clear();var local=Read(Path.Combine(root,"lan-local.json"));
            if(local.Count>0)devices.Rows.Add(Environment.MachineName+"（本机）",Convert.ToBoolean(Get(local,"running"))?"记录中 / "+Str(local,"codex_health"):"已暂停",Quota(local),Get(local,"codex_tokens"),Get(local,"dsh_today_tokens"),Wallet(local),When(Get(local,"recording_started_at"))+"\n"+When(Get(local,"sampled_at")));
            var peers=Get(lan,"devices") as IEnumerable;if(peers==null)return;
            foreach(var item in peers)
            {
                var peer=Map(item);bool paired=Convert.ToBoolean(Get(peer,"paired")),online=Convert.ToBoolean(Get(peer,"online"));var summary=Map(Get(peer,"summary"));
                devices.Rows.Add(Str(peer,"name")+"\n"+Str(peer,"ip"),!online?"离线":!paired?"发现，未配对":!Convert.ToBoolean(Get(summary,"running"))?"已配对 / 记录暂停":"已配对 / "+Str(summary,"codex_health"),paired?Quota(summary):"未知",paired?Get(summary,"codex_tokens"):null,paired?Get(summary,"dsh_today_tokens"):null,paired?Wallet(summary):"未知",paired?When(Get(summary,"recording_started_at"))+"\n"+When(Get(summary,"sampled_at")):"—");
            }
        }
    }
}
