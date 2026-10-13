using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace CodexUsageTray
{
    internal static class WindowRecovery
    {
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd,int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr insert,int x,int y,int w,int h,uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int bytes);
        internal static bool Topmost(IntPtr hwnd){return (GetWindowLong(hwnd,-20)&8)!=0;}
        internal static int Cloaked(IntPtr hwnd){int value;return DwmGetWindowAttribute(hwnd,14,out value,4)==0?value:0;}
        internal static string Ensure(Form window,bool desired,bool dragging,ref DateTime lastRaise)
        {
            if(!desired||dragging||window.IsDisposed)return "";
            if(!window.IsHandleCreated)window.Show();
            // A window on another virtual desktop is cloaked by Windows. Do not move desktops.
            if(Cloaked(window.Handle)!=0)return "cloaked";
            string reason="";
            if(!window.Visible){window.Show();reason="hidden";}
            if(!IsWindowVisible(window.Handle)||IsIconic(window.Handle)){ShowWindow(window.Handle,4);reason="hidden_or_minimized";}
            bool inside=false;foreach(Screen screen in Screen.AllScreens)if(screen.WorkingArea.Contains(window.Bounds)){inside=true;break;}
            if(!inside){Rectangle area=Screen.FromRectangle(window.Bounds).WorkingArea;window.Location=FloatingWidget.Clamp(window.Location,window.Size,area);reason+=(reason.Length==0?"":"+")+"offscreen";}
            bool missingTopmost=!Topmost(window.Handle);
            if(missingTopmost||reason.Length>0||(DateTime.UtcNow-lastRaise).TotalSeconds>=10)
            {
                // SWP_NOACTIVATE preserves the user's foreground app and input focus.
                if(!SetWindowPos(window.Handle,new IntPtr(-1),0,0,0,0,0x1|0x2|0x10|0x40))return "native_restore_failed";
                lastRaise=DateTime.UtcNow;
                if(missingTopmost)reason+=(reason.Length==0?"":"+")+"topmost_lost";
            }
            return reason;
        }
    }
}
