using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace CodexUsageTray
{
    internal static class BackendSource
    {
        static string source;
        public static string Root
        {
            get
            {
                if(source!=null)return source;
                source=Paths.Data;
                try
                {
                    var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Paths.Data,"legacy-source.json"),Encoding.UTF8));
                    string path=Path.GetFullPath(Convert.ToString(d["root"]));
                    if(File.Exists(Path.Combine(path,"monitor.py"))&&File.Exists(Path.Combine(path,"start.ps1"))&&File.Exists(Path.Combine(path,"status.json")))source=path;
                    else BackgroundLifetime.Log("legacy_source_invalid path="+path);
                }catch(Exception e){BackgroundLifetime.Log("legacy_source_unavailable "+e.GetType().Name+": "+e.Message);}
                BackgroundLifetime.Log("backend_source="+source);
                return source;
            }
        }
        public static bool Legacy {get{return !string.Equals(Root.TrimEnd('\\'),Paths.Data.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);}}
        public static string ProgramRoot {get{return Legacy?Root:AppDomain.CurrentDomain.BaseDirectory;}}
        public static void Pause(bool paused)
        {
            if(!Legacy)return;
            var preferences=WidgetSettings.Load(Root);preferences.RecorderPaused=paused;preferences.Save(Root);
        }
    }
}
