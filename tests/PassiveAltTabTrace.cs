// Standalone, opt-in foreground/Alt+Tab recorder for genuine desktop use.
// C# 5, .NET Framework 4.8; no WindowTabs code, DLL injection, or input
// synthesis. No window titles, file paths, command lines, typed characters,
// other keys, screenshots, or clipboard contents are recorded.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static class PassiveAltTabTrace
{
    const int WH_KEYBOARD_LL=13;
    const int WM_KEYDOWN=0x100, WM_KEYUP=0x101;
    const int WM_SYSKEYDOWN=0x104, WM_SYSKEYUP=0x105;
    const int VK_TAB=0x09, VK_MENU=0x12, VK_LMENU=0xA4, VK_RMENU=0xA5;
    const int VK_F12=0x7B, VK_CONTROL=0x11, VK_SHIFT=0x10;
    const uint EVENT_SYSTEM_FOREGROUND=0x0003, WINEVENT_OUTOFCONTEXT=0;
    const uint GW_OWNER=4;

    [StructLayout(LayoutKind.Sequential)]
    struct Kbd {
        public uint vkCode, scanCode, flags, time;
        public IntPtr extraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect {
        public int Left,Top,Right,Bottom;
    }

    delegate IntPtr KeyboardCallback(int code,IntPtr wParam,IntPtr lParam);
    delegate void ForegroundCallback(IntPtr hook,uint eventType,IntPtr hwnd,
                                      int idObject,int idChild,uint thread,uint time);
    delegate bool EnumWindowCallback(IntPtr hwnd,IntPtr lparam);

    [DllImport("user32.dll",SetLastError=true)]
    static extern IntPtr SetWindowsHookEx(int idHook,KeyboardCallback callback,
                                          IntPtr module,uint threadId);
    [DllImport("user32.dll",SetLastError=true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)]
    static extern IntPtr SetWinEventHook(uint eventMin,uint eventMax,IntPtr module,
                                         ForegroundCallback callback,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")]
    static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowCallback callback,IntPtr state);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd,out Rect rectangle);
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    sealed class Signal {
        public string Kind;
        public IntPtr Foreground;
        public long Tick;
        public long Id;
    }
    sealed class Sample {
        public long FromTick;
        public long DueTick;
        public long ChordId;
        public int RequestedDelayMs;
    }

    static readonly object Gate=new object();
    static readonly List<Signal> Pending=new List<Signal>();
    static readonly List<Sample> Samples=new List<Sample>();
    static readonly Stopwatch Clock=Stopwatch.StartNew();
    static readonly long StartTick=Stopwatch.GetTimestamp();
    static readonly Dictionary<uint,string> ProcessNames=new Dictionary<uint,string>();
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=1024*1024 };
    static long Seq;
    static bool AltHeld, TabHeld, TabChordPending;
    static volatile bool StopRequested;
    static IntPtr LastForeground;
    static IntPtr KeyboardHook, WinEventHook;
    static KeyboardCallback OnKeyboard=KeyboardEvent;
    static ForegroundCallback OnForeground=ForegroundEvent;
    static StreamWriter Writer;
    static int MinuteLimit;
    static System.Windows.Forms.Timer Pump;

    static long NowTicks() { return Stopwatch.GetTimestamp(); }
    static long Milliseconds(long ticks) {
        return (long)((ticks*1000.0)/Stopwatch.Frequency);
    }
    static void Record(string kind,IntPtr foreground) {
        lock(Gate) Pending.Add(new Signal {
            Kind=kind,Foreground=foreground,Tick=NowTicks(),
            Id=Interlocked.Increment(ref Seq)
        });
    }

    static IntPtr KeyboardEvent(int code,IntPtr wParam,IntPtr lParam)
    {
        if(code>=0)
        {
            var type=wParam.ToInt64();
            bool down=type==WM_KEYDOWN || type==WM_SYSKEYDOWN;
            bool up=type==WM_KEYUP || type==WM_SYSKEYUP;
            if(down || up)
            {
                var data=(Kbd)Marshal.PtrToStructure(lParam,typeof(Kbd));
                var vk=(int)data.vkCode;
                if(vk==VK_MENU || vk==VK_LMENU || vk==VK_RMENU)
                {
                    if(down) AltHeld=true;
                    else {
                        AltHeld=false;
                        if(TabChordPending) {
                            Record("alt-release",GetForegroundWindow());
                            TabChordPending=false;
                        }
                    }
                }
                else if(vk==VK_TAB)
                {
                    if(up) TabHeld=false;
                    else if(!TabHeld) {
                        TabHeld=true;
                        if(AltHeld || (data.flags & 0x20)!=0)
                        {
                            // Only Alt+Tab timing is logged. Every other
                            // ordinary key is ignored; suppress key repeats.
                            TabChordPending=true;
                            Record("alt-tab",GetForegroundWindow());
                        }
                    }
                }
                else if(vk==VK_F12 && down &&
                        (GetAsyncKeyState(VK_CONTROL)&0x8000)!=0 &&
                        (GetAsyncKeyState(VK_SHIFT)&0x8000)!=0)
                {
                    // Optional manual failure marker; never consume F12.
                    Record("manual-marker",GetForegroundWindow());
                }
            }
        }
        return CallNextHookEx(KeyboardHook,code,wParam,lParam);
    }

    static void ForegroundEvent(IntPtr hook,uint evt,IntPtr hwnd,
                                 int idObject,int idChild,uint tid,uint time)
    {
        if(idObject==0 && idChild==0 && hwnd!=IntPtr.Zero)
            Record("foreground",hwnd);
    }

    static string NameOf(uint pid)
    {
        if(pid==0) return "unknown";
        string name;
        if(ProcessNames.TryGetValue(pid,out name)) return name;
        name="unavailable";
        try {
            using(var p=Process.GetProcessById((int)pid)) name=p.ProcessName;
        }
        catch(Exception) {}
        ProcessNames[pid]=name;
        return name;
    }

    static IntPtr RootOwner(IntPtr hwnd)
    {
        var seen=new HashSet<IntPtr>();
        for(var n=0;n<16 && hwnd!=IntPtr.Zero && IsWindow(hwnd);n++)
        {
            if(!seen.Add(hwnd)) break;
            var next=GetWindow(hwnd,GW_OWNER);
            if(next==IntPtr.Zero) return hwnd;
            hwnd=next;
        }
        return hwnd;
    }

    static object Window(IntPtr hwnd)
    {
        if(hwnd==IntPtr.Zero || !IsWindow(hwnd))
            return new { hwnd=hwnd.ToInt64(),exists=false };
        uint pid;
        var tid=GetWindowThreadProcessId(hwnd,out pid);
        var owner=GetWindow(hwnd,GW_OWNER);
        var root=RootOwner(hwnd);
        var hit=IntPtr.Zero;
        Rect rect;
        if(IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
           GetWindowRect(hwnd,out rect) &&
           rect.Right>rect.Left && rect.Bottom>rect.Top) {
            var center=new Point(rect.Left+(rect.Right-rect.Left)/2,
                                 rect.Top+(rect.Bottom-rect.Top)/2);
            var child=WindowFromPoint(center);
            if(child!=IntPtr.Zero) hit=GetAncestor(child,2); // GA_ROOT
        }
        var cls=new StringBuilder(256);
        GetClassName(hwnd,cls,cls.Capacity);
        return new {
            hwnd=hwnd.ToInt64(),exists=true,pid,tid,process=NameOf(pid),
            windowClass=cls.ToString(),
            owner=owner.ToInt64(),rootOwner=root.ToInt64(),
            lastActivePopup=GetLastActivePopup(root).ToInt64(),
            visible=IsWindowVisible(hwnd),minimized=IsIconic(hwnd),
            exStyle=GetWindowLong(hwnd,-20),
            topmostAtWindowCenter=hit.ToInt64(),
            hasWindowTabsGroupProperty=GetProp(hwnd,"WindowTabs.Group.v1")!=IntPtr.Zero
        };
    }

    // Narrow metadata-only snapshot of related application HWNDs. No titles.
    // The window list is only captured for Alt+Tab and manual markers, not
    // for unrelated foreground notifications.
    static object RelatedTopLevels(IntPtr current,IntPtr previous)
    {
        uint currentPid=0,previousPid=0;
        if(current!=IntPtr.Zero) GetWindowThreadProcessId(current,out currentPid);
        if(previous!=IntPtr.Zero) GetWindowThreadProcessId(previous,out previousPid);
        var currentRoot=RootOwner(current);
        var previousRoot=RootOwner(previous);
        var result=new List<object>();
        var inspected=0;
        var truncated=false;
        EnumWindows((hwnd,lparam)=>{
            if(++inspected>1200) { truncated=true; return false; }
            if(!IsWindow(hwnd)) return true;
            uint pid; GetWindowThreadProcessId(hwnd,out pid);
            var root=RootOwner(hwnd);
            var related=(currentPid!=0 && pid==currentPid) ||
                        (previousPid!=0 && pid==previousPid) ||
                        (currentRoot!=IntPtr.Zero && root==currentRoot) ||
                        (previousRoot!=IntPtr.Zero && root==previousRoot) ||
                        GetProp(hwnd,"WindowTabs.Group.v1")!=IntPtr.Zero ||
                        NameOf(pid).Equals("WindowTabs",StringComparison.OrdinalIgnoreCase);
            if(related)
            {
                if(result.Count<90) result.Add(Window(hwnd));
                else truncated=true;
            }
            return true;
        },IntPtr.Zero);
        return new { relatedWindows=result.ToArray(),scanned=inspected,truncated };
    }

    static void Write(string kind,long tick,object current,object previous,
                      object extras=null,object related=null,long? source=null)
    {
        var row=new Dictionary<string,object>();
        row["type"]=kind;
        row["utc"]=DateTime.UtcNow.ToString("o");
        row["elapsedMs"]=Milliseconds(tick-StartTick);
        row["foreground"]=current;
        row["previousForeground"]=previous;
        if(source.HasValue) row["chordId"]=source.Value;
        if(extras!=null) row["details"]=extras;
        if(related!=null) row["topology"]=related;
        Writer.WriteLine(Json.Serialize(row));
    }

    static void Drain()
    {
        List<Signal> signals;
        lock(Gate) {
            signals=Pending.OrderBy(s=>s.Id).ToList();
            Pending.Clear();
        }
        foreach(var item in signals)
        {
            var prior=LastForeground;
            var foreground=item.Kind=="foreground" ?
                                    item.Foreground : GetForegroundWindow();
            if(item.Kind=="alt-tab")
            {
                // Key-time foreground came from the hook callback. Also
                // capture the immediate current state at drain time and
                // schedule later, nonblocking observations of convergence.
                foreground=item.Foreground;
                var now=NowTicks();
                var delays=new[]{0,40,120,300,900};
                foreach(var delay in delays)
                    Samples.Add(new Sample {
                        FromTick=item.Tick,
                        DueTick=now+(long)(delay*Stopwatch.Frequency/1000.0),
                        ChordId=item.Id,RequestedDelayMs=delay
                    });
            }
            var snapshot=Window(foreground);
            var previous=Window(prior);
            var detail=item.Kind=="alt-tab" ||
                       item.Kind=="manual-marker" ?
                       RelatedTopLevels(foreground,prior) : null;
            Write(item.Kind,item.Tick,snapshot,previous,
                  item.Kind=="alt-tab" ? (object)new {
                      chordId=item.Id,afterDrainForeground=GetForegroundWindow().ToInt64()
                  } : null,detail,item.Kind=="alt-tab"?(long?)item.Id:null);
            if(item.Kind=="foreground")
                LastForeground=item.Foreground;
        }
        var currentTick=NowTicks();
        for(int i=Samples.Count-1;i>=0;i--)
        {
            var sample=Samples[i];
            if(currentTick<sample.DueTick) continue;
            var observed=GetForegroundWindow();
            Write("post-alt-tab-sample",currentTick,Window(observed),Window(LastForeground),
                  new {
                      requestedDelayMs=sample.RequestedDelayMs,
                      actualAfterKeyMs=Milliseconds(currentTick-sample.FromTick)
                  },null,sample.ChordId);
            Samples.RemoveAt(i);
        }
        Writer.Flush();
        if(StopRequested || Clock.Elapsed.TotalMinutes>=MinuteLimit)
            Application.ExitThread();
    }

    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length!=2) {
            Console.Error.WriteLine("Usage: PassiveAltTabTrace.exe <trace.jsonl> <minutes:1..30>");
            return 2;
        }
        if(!int.TryParse(args[1],out MinuteLimit) ||
           MinuteLimit<1 || MinuteLimit>30) {
            Console.Error.WriteLine("Minutes must be 1 through 30.");
            return 2;
        }
        var output=Path.GetFullPath(args[0]);
        if(File.Exists(output)) {
            Console.Error.WriteLine("Refusing to overwrite existing trace: "+output);
            return 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        using(Writer=new StreamWriter(output,false,new UTF8Encoding(false))) {
            Writer.AutoFlush=true;
            Console.CancelKeyPress+=(sender,e)=> {
                e.Cancel=true;
                StopRequested=true;
            };
            LastForeground=GetForegroundWindow();
            Record("recorder-start",LastForeground);
            KeyboardHook=SetWindowsHookEx(WH_KEYBOARD_LL,OnKeyboard,GetModuleHandle(null),0);
            if(KeyboardHook==IntPtr.Zero) {
                Console.Error.WriteLine("Failed to install keyboard timing hook (Win32 "+Marshal.GetLastWin32Error()+")");
                return 1;
            }
            WinEventHook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,
                                         IntPtr.Zero,OnForeground,0,0,WINEVENT_OUTOFCONTEXT);
            if(WinEventHook==IntPtr.Zero) {
                Console.Error.WriteLine("Failed to register foreground events (Win32 "+Marshal.GetLastWin32Error()+")");
                UnhookWindowsHookEx(KeyboardHook);
                return 1;
            }
            Console.WriteLine("Passive Alt+Tab trace started; no input is injected.");
            Console.WriteLine("Log: "+output);
            Console.WriteLine("Use Alt+Tab normally. Press Ctrl+Shift+F12 just after a visible failure to mark it.");
            Console.WriteLine("Press Ctrl+C to stop early, or recording stops after "+MinuteLimit+" minutes.");
            try {
                Pump=new System.Windows.Forms.Timer { Interval=20 };
                Pump.Tick+=(sender,e)=>Drain();
                Pump.Start();
                Application.Run(new ApplicationContext());
            }
            finally {
                Pump.Stop();
                Pump.Dispose();
                UnhookWinEvent(WinEventHook);
                UnhookWindowsHookEx(KeyboardHook);
                DrainRemaining();
                Writer.Flush();
                Console.WriteLine("Trace closed: "+output);
            }
        }
        return 0;
    }

    static void DrainRemaining()
    {
        // After unhooking, flush only event callbacks that arrived before
        // shutdown; do not schedule new follow-up observations.
        List<Signal> items;
        lock(Gate) { items=Pending.OrderBy(x=>x.Id).ToList(); Pending.Clear(); }
        foreach(var item in items)
            Write(item.Kind,item.Tick,Window(item.Foreground),Window(LastForeground));
        Samples.Clear();
        Write("recorder-stop",NowTicks(),Window(GetForegroundWindow()),Window(LastForeground));
    }
}
