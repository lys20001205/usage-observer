using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly: AssemblyVersion("1.0.4.14")]
[assembly: AssemblyFileVersion("1.0.4.14")]
internal static class Setup
{
    const string Version="1.0.4.14";
    static string Quote(string path)
    {
        if(path.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Invalid path argument");
        return "\""+path.TrimEnd('\\')+"\"";
    }
    static void Result(string path,bool success,string error)
    {
        if(string.IsNullOrEmpty(path))return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        object value=success?(object)new{success=true,version=Version,time=DateTimeOffset.UtcNow.ToString("o")}:new{success=false,error=error,time=DateTimeOffset.UtcNow.ToString("o")};
        File.WriteAllText(path,new JavaScriptSerializer().Serialize(value),new UTF8Encoding(false));
    }
    [STAThread] static int Main(string[] args)
    {
        string cache=Path.Combine(Path.GetTempPath(),"UsageObserver-Setup-"+Guid.NewGuid().ToString("N")),resultPath="";
        try
        {
            string options="";
            if(args.Length==2&&args[0]=="--test-install")options=" -NoLaunch -InstallDirectory "+Quote(Path.GetFullPath(args[1]));
            else if(args.Length==2&&args[0]=="--check-package")options=" -CheckOnly -InstallDirectory "+Quote(Path.GetFullPath(args[1]));
            else if(args.Length==6&&args[0]=="--update-install"&&args[2]=="--data-root"&&args[4]=="--result")
            {resultPath=Path.GetFullPath(args[5]);options=" -InstallDirectory "+Quote(Path.GetFullPath(args[1]))+" -DataDirectory "+Quote(Path.GetFullPath(args[3]));}
            else if(args.Length!=0)throw new ArgumentException("Unsupported installation arguments");
            Directory.CreateDirectory(cache);string archive=Path.Combine(cache,"payload.zip"),package=Path.Combine(cache,"package");
            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))using(var output=File.Create(archive))
            {if(input==null)throw new IOException("Missing installation payload");input.CopyTo(output);}
            ZipFile.ExtractToDirectory(archive,package);
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe");
            var start=new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Quote(Path.Combine(package,"install.ps1"))+options){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(start))
            {
                var standardOutput=process.StandardOutput.ReadToEndAsync();var standardError=process.StandardError.ReadToEndAsync();process.WaitForExit();
                if(process.ExitCode!=0)throw new IOException(standardError.Result+standardOutput.Result);
            }
            Result(resultPath,true,null);return 0;
        }
        catch(Exception error)
        {
            try{Result(resultPath,false,error.Message);}catch{}
            File.WriteAllText(Path.Combine(Path.GetTempPath(),"UsageObserver-setup-test-error.txt"),error.ToString(),Encoding.UTF8);
            if(args.Length==0)MessageBox.Show(error.Message,"安装未完成",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\",resolved=Path.GetFullPath(cache);
            if(resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("UsageObserver-Setup-",StringComparison.Ordinal)&&Directory.Exists(resolved))
                try{Directory.Delete(resolved,true);}catch{}
        }
    }
}
