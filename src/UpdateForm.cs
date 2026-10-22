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
    internal sealed class ReleasePackage
    {
        public Version Version;
        public string Url, Sha256;
        public long Size;
    }
    internal static class Updates
    {
        public const string VersionText = "1.0.4.16";
        static object Get(Dictionary<string,object> d, string k) { object v; return d.TryGetValue(k,out v)?v:null; }
        internal static WebClient CreateClient()
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            // GitHub JSON is UTF-8. ANSI decoding can consume closing JSON quotes.
            var client=new WebClient {Encoding=Encoding.UTF8};
            client.Headers[HttpRequestHeader.UserAgent]="UsageObserver/"+VersionText;
            client.Headers[HttpRequestHeader.Accept]="application/vnd.github+json";
            return client;
        }
        public static string Repository(string input)
        {
            string repo=input.Trim();
            if(repo.StartsWith("https://github.com/",StringComparison.OrdinalIgnoreCase))repo=repo.Substring(19).TrimEnd('/');
            if(!Regex.IsMatch(repo,@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") || repo.EndsWith("/.") || repo.EndsWith("/.."))throw new FormatException("请输入 GitHub 仓库：用户名/仓库名，或该仓库的 HTTPS 地址。");
            return repo;
        }
        public static ReleasePackage ParseRelease(string json,string repo)
        {
            repo=Repository(repo);
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            Version version;string tag=Convert.ToString(Get(d,"tag_name"));
            if(Convert.ToBoolean(Get(d,"draft"))||Convert.ToBoolean(Get(d,"prerelease"))||!System.Version.TryParse(tag.TrimStart('v'),out version))throw new FormatException("未找到有效的正式版本。");
            var assets=Get(d,"assets") as IEnumerable;
            if(assets!=null)foreach(var item in assets)
            {
                var asset=item as Dictionary<string,object>;if(asset==null||Convert.ToString(Get(asset,"name"))!="UsageObserver-Setup.exe")continue;
                string url=Convert.ToString(Get(asset,"browser_download_url")),digest=Convert.ToString(Get(asset,"digest"));
                long size=Convert.ToInt64(Get(asset,"size"));
                string prefix="https://github.com/"+repo+"/releases/download/";
                if(!url.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Uri.IsWellFormedUriString(url,UriKind.Absolute)||size<1024||size>150*1024*1024||!Regex.IsMatch(digest,@"^sha256:[a-fA-F0-9]{64}$"))throw new FormatException("发布附件地址、大小或 SHA-256 摘要无效，未运行安装程序。");
                return new ReleasePackage {Version=version,Url=url,Sha256=digest.Substring(7).ToUpperInvariant(),Size=size};
            }
            throw new FormatException("该 Release 缺少 UsageObserver-Setup.exe 安装附件。");
        }
        public static string Hash(string path)
        { using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""); }
        public static void Verify(string path,ReleasePackage package)
        { if(new FileInfo(path).Length!=package.Size||!string.Equals(Hash(path),package.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新下载不完整或校验失败，现有程序未改变。"); }
        public static string LoadRepository(string data)
        {
            foreach(string file in new[]{Path.Combine(data,"update-settings.json"),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-defaults.json")})
                try { var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));string repo=Convert.ToString(Get(d,"repository"));if(!string.IsNullOrWhiteSpace(repo))return Repository(repo); }catch{}
            return "";
        }
        public static void SaveRepository(string data,string repo)
        { Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"update-settings.json"),new JavaScriptSerializer().Serialize(new {repository=Repository(repo)}),Encoding.UTF8); }
        internal static void SelfTest()
        {
            string digest=new string('a',64),json="{\"tag_name\":\"v1.0.3\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"UsageObserver-Setup.exe\",\"size\":2048,\"digest\":\"sha256:"+digest+"\",\"browser_download_url\":\"https://github.com/owner/project/releases/download/v1.0.3/UsageObserver-Setup.exe\"}]}";
            var p=ParseRelease(json,"https://github.com/owner/project");if(p.Version!=new Version("1.0.3"))throw new Exception("release version");
            foreach(string invalid in new[]{json.Replace("sha256:","md5:"),json.Replace("https://github.com/owner/","http://github.com/owner/"),json.Replace("false,\"prerelease\":false","false,\"prerelease\":true"),json.Replace("2048","157286401")})
            { bool rejected=false;try {ParseRelease(invalid,"owner/project");}catch(FormatException){rejected=true;}if(!rejected)throw new Exception("unsafe release accepted"); }
            string path=Path.GetTempFileName();try {File.WriteAllBytes(path,new byte[2048]);p.Sha256=Hash(path);Verify(path,p);File.WriteAllBytes(path,new byte[2047]);bool rejected=false;try{Verify(path,p);}catch(IOException){rejected=true;}if(!rejected)throw new Exception("truncated download accepted");File.WriteAllBytes(path,new byte[2048]);p.Sha256=digest;rejected=false;try{Verify(path,p);}catch(IOException){rejected=true;}if(!rejected)throw new Exception("wrong digest accepted");}finally{File.Delete(path);}
        }
    }
    internal sealed class UpdateForm : Form
    {
        readonly string data;
        readonly TextBox repository;
        readonly Label status;
        readonly Button update;
        readonly ProgressBar progress;
        bool busy;
        public UpdateForm(string root)
        {
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;data=root;BackColor=VisualTheme.Canvas;Text="UsageObserver · 程序更新";ClientSize=new Size(592,362);Font=new Font("Microsoft YaHei UI",9);StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
            VisualTheme.Text(this,"程序更新",24,20,350,38,21,VisualTheme.Ink,true);
            VisualTheme.Text(this,"从 GitHub 正式版本获取更新",26,63,430,22,8.5F,VisualTheme.Muted);
            Controls.Add(new SurfaceBadge{Text="v"+Updates.VersionText,Bounds=new Rectangle(452,29,116,28),Font=new Font("Segoe UI",10F),ForeColor=VisualTheme.Accent});
            var card=VisualTheme.Card(this,24,102,544,172,VisualTheme.Surface);
            VisualTheme.Text(card,"更新仓库",18,15,500,23,9,VisualTheme.Ink);
            repository=new TextBox{Text=Updates.LoadRepository(data),Location=new Point(18,46),Size=new Size(508,29),Font=new Font("Segoe UI",11F)};card.Controls.Add(repository);
            status=VisualTheme.Text(card,"已安装 "+Updates.VersionText+"，可随时检查新版本。",18,91,508,44,9,VisualTheme.Muted);
            progress=new ProgressBar{Location=new Point(18,143),Size=new Size(508,5),Visible=false};card.Controls.Add(progress);
            VisualTheme.Text(this,"更新自动保留记录、配对及本机设置",26,283,540,23,8,VisualTheme.Muted);
            update=VisualTheme.Button(this,"检查并更新",24,314,170,delegate{},true);update.Click+=async delegate{await RunUpdate();};
            var save=VisualTheme.Button(this,"保存更新源",206,314,164,delegate{});save.Click+=delegate{try{Updates.SaveRepository(data,repository.Text);status.Text="更新源已保存。";}catch(Exception e){status.Text=e.Message;}};
            var close=VisualTheme.Button(this,"关闭",448,314,120,delegate{});close.Click+=delegate{Close();};
            VisualTheme.Decorate(this);FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy)e.Cancel=true;};
        }
        async Task RunUpdate()
        {
            busy=true;update.Enabled=false;repository.Enabled=false;progress.Value=0;progress.Visible=true;
            try
            {
                string repo=Updates.Repository(repository.Text);Updates.SaveRepository(data,repo);
                using(var client=Updates.CreateClient())
                using(var deadline=new System.Threading.Timer(delegate {client.CancelAsync();},null,180000,System.Threading.Timeout.Infinite))
                {
                    status.Text="正在检查 GitHub 正式版本…";
                    var package=Updates.ParseRelease(await client.DownloadStringTaskAsync("https://api.github.com/repos/"+repo+"/releases/latest"),repo);
                    if(package.Version<=new Version(Updates.VersionText)){status.Text="当前已是最新版本（"+Updates.VersionText+"）。";return;}
                    string cache=Path.Combine(data,"updates",package.Version.ToString()+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);
                    string setup=Path.Combine(cache,"UsageObserver-Setup.exe");
                    client.Headers.Remove(HttpRequestHeader.Accept);
                    client.DownloadProgressChanged+=delegate(object sender,DownloadProgressChangedEventArgs e){if(e.BytesReceived>package.Size)client.CancelAsync();progress.Value=Math.Max(0,Math.Min(100,e.ProgressPercentage));status.Text="下载 "+package.Version+" · "+progress.Value+"%";};
                    await client.DownloadFileTaskAsync(new Uri(package.Url),setup);
                    await Task.Run(delegate {Updates.Verify(setup,package);});
                    string helper=Path.Combine(cache,"UpdateHelper.exe");File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"UpdateHelper.exe"),helper);
                    string program=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                    Process.Start(new ProcessStartInfo(helper,"\""+setup+"\" \""+package.Sha256+"\" \""+program+"\" \""+data.TrimEnd('\\')+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
                    status.Text="校验完成，正在替换程序并重新启动…";
                }
            }
            catch(WebException){status.Text="无法读取 GitHub 发布。请检查网络、仓库名称及正式 Release；当前支持公开仓库。";}
            catch(System.OperationCanceledException){status.Text="更新请求超时或已取消，现有程序未改变。";}
            catch(Exception e){status.Text=e.Message;}
            finally{busy=false;update.Enabled=true;repository.Enabled=true;}
        }
    }
}
