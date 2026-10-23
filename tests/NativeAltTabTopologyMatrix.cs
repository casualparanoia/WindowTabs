// Opt-in, standalone Windows 11 shell-topology experiment. C# 5 / .NET Framework 4.8.
// No WindowTabs code loaded, no real application HWNDs manipulated, no CI input.
// Only our own disposable forms and hidden proxy windows receive owner changes.
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

[ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList3Probe
{
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr hwnd);
    [PreserveSig] int DeleteTab(IntPtr hwnd);
    [PreserveSig] int ActivateTab(IntPtr hwnd);
    [PreserveSig] int SetActiveAlt(IntPtr hwnd);
    [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool full);
    [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    [PreserveSig] int SetProgressState(IntPtr hwnd, int state);
    [PreserveSig] int RegisterTab(IntPtr tab, IntPtr host);
    [PreserveSig] int UnregisterTab(IntPtr tab);
    [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr before);
    [PreserveSig] int SetTabActive(IntPtr tab, IntPtr host, int reserved);
}

[ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
class TaskbarClassProbe { }

sealed class ShellTopologyMatrix
{
    const int GWL_HWNDPARENT = -8, GW_OWNER = 4;
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x00080000, WS_EX_APPWINDOW = 0x00040000;
    const int WS_EX_NOACTIVATE = 0x08000000, LWA_ALPHA = 2;
    const uint SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10;
    const uint KEYEVENTF_KEYUP = 2;
    const int VK_MENU = 0x12, VK_TAB = 0x09;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW", SetLastError=true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, int size, out uint needed);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll", SetLastError=true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);
    delegate void ForegroundCallback(IntPtr hook, uint evt, IntPtr hwnd,
                                     int objectId, int childId, uint tid, uint millis);
    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr SetWinEventHook(uint evtMin, uint evtMax, IntPtr module,
                                         ForegroundCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    // x64 INPUT layout: DWORD type at 0, 4 bytes padding, union starts at 8.
    // INPUT is 40 bytes (union is 32 bytes); KEYBDINPUT itself is 24 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size=40)]
    struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }

    sealed class TransparentProxy : NativeWindow, IDisposable
    {
        public TransparentProxy(Rectangle bounds, bool host)
        {
            var c = new CreateParams();
            c.Style = WS_POPUP;
            c.ExStyle = WS_EX_LAYERED | (host ? WS_EX_NOACTIVATE | WS_EX_APPWINDOW : 0);
            c.X = bounds.X; c.Y = bounds.Y; c.Width = bounds.Width; c.Height = bounds.Height;
            CreateHandle(c);
            Check(SetLayeredWindowAttributes(Handle, 0, 0, LWA_ALPHA), "Cannot make fixture proxy transparent");
            if (host) ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE; all four cells get the same host
        }
        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    sealed class Cell : IDisposable
    {
        public readonly bool Register, Own;
        public readonly string Name;
        public readonly Form A, B, Anchor;
        public readonly TransparentProxy Host, ProxyA, ProxyB;
        public ITaskbarList3Probe Taskbar;
        bool hostAdded, aRegistered, bRegistered;

        public Cell(bool register, bool own, Rectangle workingArea)
        {
            Register = register; Own = own;
            Name = "register-" + (register ? "on" : "off") + "_owner-" + (own ? "on" : "off");
            int left=workingArea.Left, top=workingArea.Top;
            var hostBounds = new Rectangle(left+60,top+120,580,390);
            Host = new TransparentProxy(hostBounds,true);
            ProxyA = new TransparentProxy(hostBounds,false);
            ProxyB = new TransparentProxy(hostBounds,false);
            A = MakeForm("A",new Point(left+60,top+120));
            B = MakeForm("B",new Point(left+700,top+120));
            Anchor = MakeForm("anchor",new Point(left+400,top+550));
        }

        static Form MakeForm(string role, Point point)
        {
            var form = new Form {
                Text = "WindowTabs isolated topology fixture " + role,
                StartPosition=FormStartPosition.Manual,
                Location=point, Size=new Size(320,230),
                FormBorderStyle=FormBorderStyle.FixedSingle,
                MaximizeBox=false
            };
            form.Show();
            return form;
        }

        public void Configure()
        {
            Taskbar = (ITaskbarList3Probe)new TaskbarClassProbe();
            Hr(Taskbar.HrInit(), "HrInit");
            // Identical host registration in all four cases.
            Hr(Taskbar.AddTab(Host.Handle),"AddTab(host)"); hostAdded=true;
            if (Own)
            {
                SetOwner(ProxyA.Handle,Host.Handle);
                SetOwner(ProxyB.Handle,Host.Handle);
                SetOwner(A.Handle,ProxyA.Handle);
                SetOwner(B.Handle,ProxyB.Handle);
            }
            // Match the proxy-below-application positioning in each cell, not
            // only the ownership-on cell. Never activate an unrelated window.
            Check(SetWindowPos(ProxyA.Handle,A.Handle,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE),
                  "Cannot place proxy A below owned fixture");
            Check(SetWindowPos(ProxyB.Handle,B.Handle,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE),
                  "Cannot place proxy B below owned fixture");
            if (Register)
            {
                Hr(Taskbar.RegisterTab(ProxyA.Handle,Host.Handle),"RegisterTab A"); aRegistered=true;
                Hr(Taskbar.RegisterTab(ProxyB.Handle,Host.Handle),"RegisterTab B"); bRegistered=true;
                Hr(Taskbar.SetTabOrder(ProxyA.Handle,IntPtr.Zero),"SetTabOrder A");
                Hr(Taskbar.SetTabOrder(ProxyB.Handle,IntPtr.Zero),"SetTabOrder B");
                Hr(Taskbar.SetTabActive(ProxyA.Handle,Host.Handle,0),"SetTabActive");
            }
            Check(GetWindow(A.Handle,GW_OWNER)==(Own?ProxyA.Handle:IntPtr.Zero),
                  "Wrong direct owner for test app A");
            Check(GetWindow(B.Handle,GW_OWNER)==(Own?ProxyB.Handle:IntPtr.Zero),
                  "Wrong direct owner for test app B");
            Check(RootOwner(A.Handle)==(Own?Host.Handle:A.Handle),
                  "Wrong root owner for test app A");
            Check(RootOwner(B.Handle)==(Own?Host.Handle:B.Handle),
                  "Wrong root owner for test app B");
            Check(GetWindow(ProxyA.Handle,GW_OWNER)==(Own?Host.Handle:IntPtr.Zero),
                  "Wrong proxy A owner");
            Check(GetWindow(ProxyB.Handle,GW_OWNER)==(Own?Host.Handle:IntPtr.Zero),
                  "Wrong proxy B owner");
        }

        public object Topology()
        {
            return new {
                name=Name, registered=Register, nativeOwnership=Own,
                host=Window(Host.Handle), proxyA=Window(ProxyA.Handle),
                proxyB=Window(ProxyB.Handle), appA=Window(A.Handle),
                appB=Window(B.Handle), anchor=Window(Anchor.Handle)
            };
        }

        public void Dispose()
        {
            // CRITICAL: destroying a proxy owner would otherwise destroy its
            // owned application. Restore only our own HWNDs, before destruction.
            try {
                if(A!=null && !A.IsDisposed && GetWindow(A.Handle,GW_OWNER)==ProxyA.Handle)
                    SetOwner(A.Handle,IntPtr.Zero);
                if(B!=null && !B.IsDisposed && GetWindow(B.Handle,GW_OWNER)==ProxyB.Handle)
                    SetOwner(B.Handle,IntPtr.Zero);
                if(GetWindow(ProxyA.Handle,GW_OWNER)==Host.Handle)
                    SetOwner(ProxyA.Handle,IntPtr.Zero);
                if(GetWindow(ProxyB.Handle,GW_OWNER)==Host.Handle)
                    SetOwner(ProxyB.Handle,IntPtr.Zero);
            } finally {
                if(Taskbar!=null) {
                    try { if(aRegistered) Taskbar.UnregisterTab(ProxyA.Handle); } catch(Exception) {}
                    try { if(bRegistered) Taskbar.UnregisterTab(ProxyB.Handle); } catch(Exception) {}
                    try { if(hostAdded) Taskbar.DeleteTab(Host.Handle); } catch(Exception) {}
                    Marshal.FinalReleaseComObject(Taskbar);
                    Taskbar=null;
                }
                // WinForms must relinquish its windows before the proxy HWNDs.
                if(A!=null) A.Dispose();
                if(B!=null) B.Dispose();
                if(Anchor!=null) Anchor.Dispose();
                ProxyA.Dispose(); ProxyB.Dispose(); Host.Dispose();
                Application.DoEvents();
            }
        }
    }

    static void Hr(int hr, string operation) { if(hr<0) throw new COMException(operation + " failed",hr); }
    static void Check(bool condition, string reason) { if(!condition) throw new InvalidOperationException(reason); }

    static void SetOwner(IntPtr window, IntPtr owner)
    {
        Check(IsWindow(window) && (owner==IntPtr.Zero || IsWindow(owner)), "Invalid fixture owner HWND");
        SetWindowLongPtr(window,GWL_HWNDPARENT,owner);
        Check(GetWindow(window,GW_OWNER)==owner,"SetWindowLongPtr owner did not take effect");
    }

    static IntPtr RootOwner(IntPtr hwnd)
    {
        var seen=new HashSet<IntPtr>();
        for(int i=0;i<16 && hwnd!=IntPtr.Zero && seen.Add(hwnd);i++)
        {
            var next=GetWindow(hwnd,GW_OWNER);
            if(next==IntPtr.Zero) return hwnd;
            hwnd=next;
        }
        return hwnd;
    }

    static object Window(IntPtr hwnd)
    {
        if(hwnd==IntPtr.Zero || !IsWindow(hwnd)) return new {hwnd=hwnd.ToInt64(), exists=false};
        uint pid; GetWindowThreadProcessId(hwnd,out pid);
        var cls=new StringBuilder(256); GetClassName(hwnd,cls,cls.Capacity);
        var root=RootOwner(hwnd);
        return new {
            hwnd=hwnd.ToInt64(), exists=true, pid, windowClass=cls.ToString(),
            visible=IsWindowVisible(hwnd), minimized=IsIconic(hwnd),
            owner=GetWindow(hwnd,GW_OWNER).ToInt64(),
            rootOwner=root.ToInt64(), lastActivePopup=GetLastActivePopup(root).ToInt64()
        };
    }

    static readonly Stopwatch Clock=Stopwatch.StartNew();
    static StreamWriter Writer;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static volatile bool StopRequested;
    static string Scenario="startup";
    // Keep delegate rooted for the entire installation. Out-of-context;
    // never instruct Windows to change foreground.
    static readonly ForegroundCallback OnForeground = ForegroundEvent;
    static void ForegroundEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId,
                                int childId, uint tid, uint millis)
    {
        if (objectId==0 && childId==0 && hwnd!=IntPtr.Zero)
            Record("foreground-event",new {notified=Window(hwnd),
                                          actualAtCallback=GetForegroundWindow().ToInt64()});
    }
    static void Record(string stage, object data)
    {
        var row=new {utc=DateTime.UtcNow.ToString("o"), elapsedMs=Clock.ElapsedMilliseconds,
                     scenario=Scenario, stage, data};
        Writer.WriteLine(Json.Serialize(row));
        Writer.Flush();
    }

    static void Pump(int durationMs)
    {
        var sw=Stopwatch.StartNew();
        while(sw.ElapsedMilliseconds<durationMs && !StopRequested)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Check(!StopRequested,"User stopped the experiment");
    }

    static void ExpectForeground(IntPtr expected, string label)
    {
        var timer=Stopwatch.StartNew();
        while(GetForegroundWindow()!=expected && timer.ElapsedMilliseconds<1500)
            Pump(20);
        Check(GetForegroundWindow()==expected,"Could not focus only our own "+label+" fixture; aborting");
    }

    static void Key(ushort vk, bool up)
    {
        var input=new INPUT {
            type=1,
            keyboard=new KEYBDINPUT {vk=vk,scan=0,flags=(up?KEYEVENTF_KEYUP:0),time=0,extra=UIntPtr.Zero}
        };
        var size=Marshal.SizeOf(typeof(INPUT));
        Check(size==40,"Unexpected x64 INPUT structure size");
        Check(SendInput(1,new[]{input},size)==1,"SendInput did not send the requested fixture key transition");
    }

    static void Chord()
    {
        bool altDown=false, tabDown=false;
        try
        {
            Key(VK_MENU,false); altDown=true;
            Key(VK_TAB,false); tabDown=true;
            Key(VK_TAB,true); tabDown=false;
            Pump(35); // same short hold as prior quick-MRU control, not a latency guarantee
        }
        finally
        {
            // Best effort to avoid leaving injected modifiers/Tab held after
            // a cancelled or failed pulse.
            if(tabDown) { try { Key(VK_TAB,true); } catch(Exception) {} }
            if(altDown) { try { Key(VK_MENU,true); } catch(Exception) {} }
        }
    }

    static void EnsureNoModifiers()
    {
        foreach(var key in new[]{1,2,0x10,0x11,0x12})
            Check(GetAsyncKeyState(key)>=0,"Mouse or modifier held: refusing to inject Alt+Tab");
    }

    static void Preflight()
    {
        Check(Environment.UserInteractive && Process.GetCurrentProcess().SessionId!=0,
              "Requires an unlocked interactive user session");
        Check(Process.GetProcessesByName("WindowTabs").Length==0,
              "Exit normal WindowTabs before using this fully isolated probe");
        var desktop=OpenInputDesktop(0,false,1);
        Check(desktop!=IntPtr.Zero,"Input desktop inaccessible or locked");
        try {
            var name=new StringBuilder(256); uint needed;
            Check(GetUserObjectInformation(desktop,2,name,512,out needed) &&
                  name.ToString()=="Default", "Not the unlocked Default desktop");
        } finally { CloseDesktop(desktop); }
        Check(FindWindow("Shell_TrayWnd",null)!=IntPtr.Zero,"Explorer taskbar not present");
        Check(Screen.PrimaryScreen.WorkingArea.Width>=1100 &&
              Screen.PrimaryScreen.WorkingArea.Height>=850,
              "Requires a minimum 1100x850 working area for non-overlapping fixture windows");
        EnsureNoModifiers();
    }

    static bool[] CasesRegistration={false,true,false,true};
    static bool[] CasesOwnership={false,false,true,true};

    static void CheckPlan()
    {
        var combinations=new HashSet<string>();
        for(int i=0;i<4;i++)
            combinations.Add((CasesRegistration[i]?"1":"0")+(CasesOwnership[i]?"1":"0"));
        Check(combinations.SetEquals(new[]{"00","10","01","11"}),
              "The registration/ownership plan is not a complete 2x2 factorial");
        Check(Marshal.SizeOf(typeof(INPUT))==40 || IntPtr.Size==4,
              "Keyboard structure layout must match the target platform");
        Console.WriteLine("PASS: four independent registration x native-owner cells; no interactive input in plan check");
    }

    static void TestCell(Cell cell, int switches)
    {
        Record("topology",cell.Topology());
        // Prime exactly one recent app/anchor pair once per cell, by activating
        // only windows owned by this fixture, without mouse injection.
        cell.A.Activate(); ExpectForeground(cell.A.Handle,"app A");
        Pump(65);
        cell.Anchor.Activate(); ExpectForeground(cell.Anchor.Handle,"anchor");
        Pump(65);
        Record("mru-primed",new {appA=cell.A.Handle.ToInt64(),anchor=cell.Anchor.Handle.ToInt64()});
        for(int i=0;i<switches;i++)
        {
            var from=i%2==0?cell.Anchor.Handle:cell.A.Handle;
            var expected=i%2==0?cell.A.Handle:cell.Anchor.Handle;
            EnsureNoModifiers();
            Check(GetForegroundWindow()==from,
                  "Another window acquired foreground; refusing further synthetic input");
            Record("before-chord",new {step=i+1, foreground=Window(from),expected=Window(expected)});
            Chord();
            var start=Stopwatch.StartNew();
            foreach(int due in new[]{0,40,120,300,900})
            {
                var remaining=due-(int)start.ElapsedMilliseconds;
                if(remaining>0) Pump(remaining);
                var actual=GetForegroundWindow();
                Record("post-chord-sample",new {
                    step=i+1, requestedDelayMs=due,
                    actualDelayMs=start.ElapsedMilliseconds,
                    observed=Window(actual),
                    expectedHwnd=expected.ToInt64(),
                    appRoot=RootOwner(cell.A.Handle).ToInt64()
                });
            }
            var final=GetForegroundWindow();
            if(final!=expected)
            {
                Record("unexpected-foreground",new {
                    step=i+1,expected=Window(expected),observed=Window(final),topology=cell.Topology()
                });
                throw new InvalidOperationException(
                    "Wrong foreground in "+cell.Name+" at switch "+(i+1)+"; aborted all further input");
            }
            Record("switch-passed",new {step=i+1,observed=final.ToInt64()});
        }
    }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length==1 && args[0]=="--check-plan") { CheckPlan(); return 0; }
            if(args.Length!=4 || args[0]!="--interactive" || args[1]!="--out")
                throw new ArgumentException("Use --check-plan or --interactive --out <trace.jsonl> <even switches 2..20>");
            int switches;
            Check(int.TryParse(args[3],out switches) && switches>=2 && switches<=20 && switches%2==0,
                  "Switch count must be even and between 2 and 20 per matrix cell");
            Check(!File.Exists(args[2]),"Refusing to overwrite existing trace");
            Preflight();
            var output=Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Console.CancelKeyPress += (sender,e) => {e.Cancel=true; StopRequested=true;};
            using(Writer=new StreamWriter(output,false,new UTF8Encoding(false)))
            {
                var foregroundHook=SetWinEventHook(3,3,IntPtr.Zero,OnForeground,0,0,0);
                Check(foregroundHook!=IntPtr.Zero,"Could not install passive foreground WinEvent hook");
                try {
                    Record("run-start",new {switchesPerCell=switches,
                        session=Process.GetCurrentProcess().SessionId});
                    for(int i=0;i<4;i++)
                    {
                        Preflight();
                        Scenario="register-"+(CasesRegistration[i]?"on":"off")+
                                 "_owner-"+(CasesOwnership[i]?"on":"off");
                        using(var cell=new Cell(CasesRegistration[i],CasesOwnership[i],
                                                Screen.PrimaryScreen.WorkingArea))
                        {
                            cell.Configure();
                            TestCell(cell,switches);
                        }
                        Record("cell-complete",new {switches});
                        Pump(180);
                    }
                    Record("run-complete",new {cells=4,switchesPerCell=switches});
                }
                catch(Exception e) {
                    Record("run-failed",new {type=e.GetType().Name,reason=e.Message});
                    throw;
                }
                finally { UnhookWinEvent(foregroundHook); }
            }
            return 0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine("Topology probe stopped: "+e.Message);
            return 1;
        }
    }
}
