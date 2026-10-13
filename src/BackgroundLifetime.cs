using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal static class BackgroundLifetime
    {
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool assigned);
        static string root;
        static string lastError="";
        static DateTime lastErrorTime;
        public static bool InJob() {bool assigned;using(var p=Process.GetCurrentProcess())return IsProcessInJob(p.Handle,IntPtr.Zero,out assigned)&&assigned;}
        public static void Log(string message)
        {
            try {File.AppendAllText(Path.Combine(root,"lifecycle.log"),DateTimeOffset.Now.ToString("o")+" pid="+Process.GetCurrentProcess().Id+" "+message+Environment.NewLine,Encoding.UTF8);}catch{}
        }
        public static void Error(Exception exception)
        {
            string text=exception.GetType().Name+": "+exception.Message;
            if(text==lastError&&(DateTime.UtcNow-lastErrorTime).TotalSeconds<60)return;
            lastError=text;lastErrorTime=DateTime.UtcNow;Log("ui_exception "+exception);
        }
        public static bool Initialize(string data,string exe,string[] args)
        {
            root=data;Log("ui_start in_job="+InJob());
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=delegate(object sender,System.Threading.ThreadExceptionEventArgs e){Error(e.Exception);};
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){Log("fatal_exception "+e.ExceptionObject);};
            AppDomain.CurrentDomain.ProcessExit+=delegate {Log("process_exit");};
            bool standalone=Array.IndexOf(args,"--standalone")>=0;
            if(!InJob()||standalone)return false;
            try
            {
                // Windows creates this per-user process outside the launching task's job.
                // This is local WMI, with no elevation, remote host or UI input.
                using(var cls=new ManagementClass("Win32_Process"))
                using(var startupClass=new ManagementClass("Win32_ProcessStartup"))
                using(var startup=startupClass.CreateInstance())
                using(var input=cls.GetMethodParameters("Create"))
                {
                    startup["ShowWindow"]=(UInt16)0;
                    var environment=new List<string>();foreach(DictionaryEntry entry in Environment.GetEnvironmentVariables())environment.Add(entry.Key+"="+entry.Value);
                    startup["EnvironmentVariables"]=environment.ToArray();
                    input["CommandLine"]="\""+exe+"\" --standalone"+(Array.IndexOf(args,"--restore-startup")>=0?" --restore-startup":"")+(Array.IndexOf(args,"--migrate-startup")>=0?" --migrate-startup":"");
                    input["CurrentDirectory"]=Path.GetDirectoryName(exe);input["ProcessStartupInformation"]=startup;
                    using(var result=cls.InvokeMethod("Create",input,null))
                    {
                        uint code=Convert.ToUInt32(result["ReturnValue"]);if(code!=0)throw new InvalidOperationException("Independent launch failed with code "+code);
                        Log("independent_handoff child_pid="+result["ProcessId"]);return true;
                    }
                }
            }
            catch(Exception e){Error(e);Log("independent_launch_unavailable; continuing current process");return false;}
        }
    }
}
