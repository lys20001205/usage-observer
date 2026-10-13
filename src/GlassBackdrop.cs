using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexUsageTray
{
    internal sealed class GlassBackdrop : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy {public int State,Flags,Color,Animation;}
        [StructLayout(LayoutKind.Sequential)] struct CompositionData {public int Attribute;public IntPtr Data;public int Size;}
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(IntPtr hwnd,ref CompositionData data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc,ref NativeRect rect,IntPtr brush);
        internal bool Ready;internal int MaterialResult=-1;
        public GlassBackdrop()
        {
            Text="UsageObserver · Frosted surface";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
            StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;BackColor=Color.Black;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams
        {
            get
            {
                var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;p.ExStyle&=~0x00080000;p.ClassStyle&=~0x00020000;
                // Retain frame metadata for DWM's native rounded mask. NCCALCSIZE removes visible chrome.
                p.Style|=0x00C40000;return p;
            }
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Apply();}
        void Apply()
        {
            Ready=false;
            // Windows can accept the accent API while rendering an opaque black surface
            // when its transparency effect is disabled. Keep the alpha overlay usable.
            using(var settings=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                if(SystemInformation.HighContrast||(settings!=null&&Convert.ToInt32(settings.GetValue("EnableTransparency",1))==0))
                {MaterialResult=1;return;}
            try
            {
                int none=1,noBorder=unchecked((int)0xFFFFFFFE),enabledFrame=2,rounded=2;
                DwmSetWindowAttribute(Handle,38,ref none,4);DwmSetWindowAttribute(Handle,34,ref noBorder,4);
                DwmSetWindowAttribute(Handle,2,ref enabledFrame,4);DwmSetWindowAttribute(Handle,33,ref rounded,4);
                var policy=new AccentPolicy{State=3};IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf(policy));
                try{Marshal.StructureToPtr(policy,pointer,false);var data=new CompositionData{Attribute=19,Data=pointer,Size=Marshal.SizeOf(policy)};MaterialResult=SetWindowCompositionAttribute(Handle,ref data)?0:-1;Ready=MaterialResult==0;}
                finally{Marshal.FreeHGlobal(pointer);}
            }catch(DllNotFoundException){Ready=false;}catch(EntryPointNotFoundException){Ready=false;}
        }
        internal void Attach(IntPtr foreground){IntPtr hwnd=Handle;Apply();}
        internal void Match(Rectangle bounds){if(Bounds!=bounds)Bounds=bounds;}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            IntPtr dc=e.Graphics.GetHdc();try{var rect=new NativeRect{Right=Width,Bottom=Height};FillRect(dc,ref rect,GetStockObject(4));}finally{e.Graphics.ReleaseHdc(dc);}
        }
        protected override void OnPaint(PaintEventArgs e){}
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0083){m.Result=IntPtr.Zero;return;}
            if(m.Msg==0x0021){m.Result=new IntPtr(3);return;}
            if(m.Msg==0x0084){m.Result=new IntPtr(-1);return;}
            base.WndProc(ref m);if(m.Msg==0x031E&&IsHandleCreated)Apply();
        }
    }
}
