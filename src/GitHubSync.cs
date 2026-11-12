using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal static class SyncConnectionCode
    {
        internal static string Repository(string input)
        {
            string value=(input??"").Trim();
            if(value.StartsWith("UO1.",StringComparison.Ordinal))
            {
                string[] pieces=value.Split('.');if(pieces.Length!=3||pieces[1].Length>200)throw new FormatException("连接码格式不完整。");
                string text=pieces[1].Replace('-','+').Replace('_','/');text=text.PadRight((text.Length+3)/4*4,'=');
                value=Encoding.UTF8.GetString(Convert.FromBase64String(text));
                if(!string.Equals(Encode(value),input.Trim(),StringComparison.Ordinal))throw new FormatException("连接码校验失败，请重新复制。");
            }
            if(!Regex.IsMatch(value,"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")||value.Contains(".."))throw new FormatException("请粘贴另一台电脑的连接码，或填写 owner/name 私有仓库。");
            return value;
        }
        internal static string Encode(string repository)
        {
            if(!Regex.IsMatch(repository??"","^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")||repository.Contains(".."))throw new FormatException("请先填写有效的私有仓库。");
            byte[] bytes=Encoding.UTF8.GetBytes(repository);string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").Substring(0,8).ToLowerInvariant();
            return "UO1."+Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')+"."+digest;
        }
    }
    internal static class GitHubSyncRuntime
    {
        internal static Dictionary<string,object> Read(string path) {try{return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8))??new Dictionary<string,object>();}catch{return new Dictionary<string,object>();}}
        internal static object Get(Dictionary<string,object> d,string key) {object value;return d.TryGetValue(key,out value)?value:null;}
        internal static string Str(Dictionary<string,object> d,string key) {return Convert.ToString(Get(d,key));}
        internal static void Ensure(string root)
        {
            var cfg=Read(Path.Combine(root,"github-sync-settings.json"));if(!Convert.ToBoolean(Get(cfg,"enabled")))return;
            string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"github_sync.py");
            string python=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"runtime","python","python.exe");
            if(!File.Exists(script)||!File.Exists(python))return;
            var state=Read(Path.Combine(root,"github-sync-process.json"));
            try {using(var p=Process.GetProcessById(Convert.ToInt32(Get(state,"pid"))))if(!p.HasExited&&string.Equals(Str(state,"script"),script,StringComparison.OrdinalIgnoreCase))return;}catch{}
            try{File.Delete(Path.Combine(root,"github-sync-stop.request"));}catch{}
            Process.Start(new ProcessStartInfo(python,"\""+script+"\" --data \""+root+"\" --loop"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory});
        }
    }

    internal sealed class GitHubSyncPanel : UserControl
    {
        readonly string root;readonly TextBox repository,label,token;readonly CheckBox enabled,upload,gitLogin;
        readonly Label status;readonly DataGridView devices;readonly System.Windows.Forms.Timer timer;
        readonly List<Control> advanced=new List<Control>();bool showAdvanced;
        static object Get(Dictionary<string,object> d,string key){return GitHubSyncRuntime.Get(d,key);}
        static string Str(Dictionary<string,object> d,string key){return GitHubSyncRuntime.Str(d,key);}
        static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        internal GitHubSyncPanel(string data)
        {
            root=data;Dock=DockStyle.Fill;AutoScroll=true;BackColor=VisualTheme.Canvas;Font=new Font("Microsoft YaHei UI",9F);
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(18),ColumnCount=2,RowCount=9};body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,152));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(body);
            var note=new Label{Text="三台电脑使用同一个连接码。每台点击一次「连接并同步本机」，以后自动汇总API金额和token。使用本机已有GitHub登录，数据保存在同一个私有仓库。",AutoSize=true,MaximumSize=new Size(800,0),ForeColor=VisualTheme.Muted,Margin=new Padding(0,0,0,12)};body.Controls.Add(note,0,0);body.SetColumnSpan(note,2);
            var cfg=GitHubSyncRuntime.Read(Path.Combine(root,"github-sync-settings.json"));
            repository=Field(body,"连接码 / 私有仓库",1,Str(cfg,"repository").Length>0?SyncConnectionCode.Encode(Str(cfg,"repository")):"",false);
            label=Field(body,"这台电脑的名称",2,Str(cfg,"label").Length>0&&Str(cfg,"label")!="Device"?Str(cfg,"label"):Environment.MachineName,false);
            token=Field(body,"GitHub 访问令牌",3,"",true);token.UseSystemPasswordChar=true;
            token.AccessibleDescription="首次接入可填写仅允许数据仓库 Contents 读写的访问令牌；保存在本机 Windows 用户保护中。留空保留已保存的令牌。";
            advanced.Add(token);foreach(Control control in body.Controls)if(body.GetRow(control)==3&&control!=token)advanced.Add(control);
            var options=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0,7,0,7)};
            enabled=new CheckBox{Text="启用 GitHub 同步",AutoSize=true,Checked=Convert.ToBoolean(Get(cfg,"enabled"))};
            upload=new CheckBox{Text="上传本机记录",AutoSize=true,Checked=Get(cfg,"upload_local")==null||Convert.ToBoolean(Get(cfg,"upload_local"))};
            gitLogin=new CheckBox{Text="使用本机已有 GitHub 登录",AutoSize=true,Checked=Get(cfg,"use_git_login")==null||Convert.ToBoolean(Get(cfg,"use_git_login"))};
            options.Controls.Add(enabled);options.Controls.Add(upload);options.Controls.Add(gitLogin);body.Controls.Add(options,0,4);body.SetColumnSpan(options,2);
            advanced.Add(options);
            var hint=new Label{Text="首次连接时，每台电脑先完成自己的GitHub登录。连接码只保存仓库地址；访问令牌可在高级设置中填写，按Windows用户加密保存在本机。",AutoSize=true,MaximumSize=new Size(800,0),ForeColor=VisualTheme.Muted,Font=new Font(Font.FontFamily,8.2F),Margin=new Padding(0,0,0,10)};body.Controls.Add(hint,0,5);body.SetColumnSpan(hint,2);advanced.Add(hint);
            var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty};
            actions.Controls.Add(VisualTheme.Button(actions,"连接并同步本机",0,0,170,delegate{enabled.Checked=true;upload.Checked=true;SaveSettings();},true));
            actions.Controls.Add(VisualTheme.Button(actions,"复制连接码",0,0,140,delegate{try{Clipboard.SetText(SyncConnectionCode.Encode(SyncConnectionCode.Repository(repository.Text)));status.Text="连接码已复制。另两台粘贴后点击「连接并同步本机」。";}catch(Exception e){MessageBox.Show(this,e.Message,"连接码未复制");}}));
            actions.Controls.Add(VisualTheme.Button(actions,"立即同步",0,0,138,delegate{try{File.WriteAllText(Path.Combine(root,"github-sync.request"),DateTimeOffset.UtcNow.ToString("o"));GitHubSyncRuntime.Ensure(root);RefreshState();}catch(Exception e){MessageBox.Show(this,e.Message,"同步暂未启动");}},true));
            actions.Controls.Add(VisualTheme.Button(actions,"高级设置",0,0,118,delegate{showAdvanced=!showAdvanced;foreach(Control control in advanced)control.Visible=showAdvanced;body.PerformLayout();}));
            var advancedSave=VisualTheme.Button(actions,"保存高级设置",0,0,140,delegate{SaveSettings();});actions.Controls.Add(advancedSave);advanced.Add(advancedSave);
            foreach(Control control in advanced)control.Visible=false;body.Controls.Add(actions,0,6);body.SetColumnSpan(actions,2);
            status=new Label{Text="尚未启用",AutoSize=true,MaximumSize=new Size(800,0),ForeColor=VisualTheme.Accent,Margin=new Padding(0,12,0,12)};body.Controls.Add(status,0,7);body.SetColumnSpan(status,2);
            devices=new DataGridView{Height=250,Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,Margin=Padding.Empty};
            foreach(string column in new[]{"设备","同步状态","本轮API美元","记录 token","输出","最新上传 / 额度"})devices.Columns.Add(column,column);
            devices.Columns[5].FillWeight=190;devices.DefaultCellStyle.WrapMode=DataGridViewTriState.True;devices.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;body.Controls.Add(devices,0,8);body.SetColumnSpan(devices,2);
            timer=new System.Windows.Forms.Timer{Interval=5000};timer.Tick+=delegate{RefreshState();};timer.Start();Disposed+=delegate{timer.Dispose();};RefreshState();VisualTheme.Decorate(this);
        }
        static TextBox Field(TableLayoutPanel panel,string name,int row,string value,bool secret)
        {
            panel.Controls.Add(new Label{Text=name,AutoSize=true,ForeColor=VisualTheme.Ink,Margin=new Padding(0,6,8,8)},0,row);
            var input=new TextBox{Text=value,Dock=DockStyle.Fill,Margin=new Padding(0,2,0,8),UseSystemPasswordChar=secret};panel.Controls.Add(input,1,row);return input;
        }
        void SaveSettings()
        {
            try
            {
                string repo=SyncConnectionCode.Repository(repository.Text);
                string name=label.Text.Trim();if(name.Length==0||name.Length>32)throw new InvalidOperationException("本机名称需为 1–32 个字符。");
                if(enabled.Checked&&!gitLogin.Checked&&token.Text.Trim().Length==0&&!File.Exists(Path.Combine(root,"github-sync-token.dpapi")))throw new InvalidOperationException("请填写此私有数据仓库的访问令牌，或选择本机已有 GitHub Git 登录。");
                if(token.Text.Trim().Length>0)
                {
                    byte[] protectedToken=ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Text.Trim()),null,DataProtectionScope.CurrentUser);
                    string path=Path.Combine(root,"github-sync-token.dpapi"),temp=path+".tmp";File.WriteAllBytes(temp,protectedToken);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);token.Clear();
                }
                var cfg=new{repository=repo,label=name,enabled=enabled.Checked,upload_local=upload.Checked,use_git_login=gitLogin.Checked};
                string settings=Path.Combine(root,"github-sync-settings.json"),scratch=settings+".tmp";File.WriteAllText(scratch,new JavaScriptSerializer().Serialize(cfg),Encoding.UTF8);if(File.Exists(settings))File.Replace(scratch,settings,null);else File.Move(scratch,settings);
                repository.Text=SyncConnectionCode.Encode(repo);ApiUsageRuntime.Request(root);
                File.WriteAllText(Path.Combine(root,"github-sync.request"),DateTimeOffset.UtcNow.ToString("o"));GitHubSyncRuntime.Ensure(root);status.Text=enabled.Checked?"正在连接并上传本机摘要…":"GitHub 同步已停用；本机记录继续按原设置运行。";
            }
            catch(Exception e){MessageBox.Show(this,e.Message,"同步设置未保存");}
        }
        static string Amount(object value){double n;return double.TryParse(Convert.ToString(value),out n)?(n>=1000000?(n/1000000).ToString("0.00")+"M":n.ToString("N0")):"未知";}
        void RefreshState()
        {
            var current=GitHubSyncRuntime.Read(Path.Combine(root,"github-sync-status.json"));string phase=Str(current,"state");
            string code=Str(current,"code");var messages=new Dictionary<string,string>{{"private_repository_required","仓库必须为私有；本次未上传"},{"github_token_not_configured","请先配置本机 GitHub 访问"},{"git_login_unavailable","本机 GitHub Git 登录不可用，请填写此数据仓库令牌"},{"github_http_401","GitHub 登录失效，请更新本机令牌"},{"github_http_403","仓库权限或 GitHub 限流，请核对令牌后稍后同步"},{"github_http_404","找不到仓库，请核对地址及访问权限"},{"recorder_database_missing","等待本机记录数据库生成"}};
            DateTimeOffset updated;string shownTime=DateTimeOffset.TryParse(Str(current,"updated_at"),out updated)?updated.ToLocalTime().ToString("MM-dd HH:mm:ss"):"未知";
            string detail;status.Text=phase=="ok"?"已同步 "+Str(current,"devices")+" 台 · API金额 "+Str(current,"api_devices")+" 台 · "+shownTime+(Str(current,"mode")=="read_only"?" · 当前仅查看，点击连接并同步可加入本机":"")+(Convert.ToInt32(Get(current,"pending_days"))>0?" · 正在补传历史":""):phase=="disabled"?"同步已停用，点击连接并同步可加入本机":phase=="error"?(messages.TryGetValue(code,out detail)?detail:"同步暂未成功："+code):"粘贴连接码后，点击「连接并同步本机」。";
            devices.Rows.Clear();var cache=GitHubSyncRuntime.Read(Path.Combine(root,"github-sync-devices.json"));var list=Get(cache,"devices") as IEnumerable;if(list==null)return;
            foreach(var item in list)
            {
                var d=Map(item);var t=Map(Get(d,"totals"));var q=Map(Get(d,"latest_quota"));DateTimeOffset time;bool fresh=DateTimeOffset.TryParse(Str(d,"uploaded_at"),out time)&&(DateTimeOffset.UtcNow-time).TotalMinutes<=30;
                var api=Map(Get(d,"api_usage"));var periods=Get(api,"periods") as IEnumerable;var currentPeriod=new Dictionary<string,object>();if(periods!=null)foreach(var p in periods)currentPeriod=Map(p);
                string dollars=api.Count==0?"更新此设备":ApiUsageRuntime.Money(Get(Map(Get(currentPeriod,"full")),"usd"));
                devices.Rows.Add(Str(d,"label")+(Convert.ToBoolean(Get(d,"is_local"))?"（本机）":""),fresh?(Convert.ToBoolean(Get(d,"recording_running"))?"近期已同步":"近期同步 / 记录暂停"):"数据已过期，保留上次值",dollars,Amount(Get(t,"total_tokens")),Amount(Get(t,"output_tokens")),(time==DateTimeOffset.MinValue?"未知":time.ToLocalTime().ToString("MM-dd HH:mm:ss"))+" / 额度已用 "+(Get(q,"used_percent")==null?"未知":Str(q,"used_percent")+"%"));
            }
        }
    }
}
