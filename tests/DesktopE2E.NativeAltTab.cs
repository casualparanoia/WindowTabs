// Opt-in live Explorer native Alt+Tab smoke test. Different from the regular
// DesktopE2E shortcut tests: sends actual Alt+Tab keyboard input and compares
// the observed HWND to a known two-entry MRU sequence on the unlocked desktop.
// Never run in CI on an isolated/noninteractive desktop.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

static partial class DesktopE2E
{
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool SetForegroundWindow(IntPtr window);

    static IntPtr AltTabRootOwner(IntPtr hwnd)
    {
        var seen=new HashSet<IntPtr>();
        for(int i=0;i<12 && hwnd!=IntPtr.Zero;i++)
        {
            Check(seen.Add(hwnd),"Cyclic native GW_OWNER chain in Alt+Tab fixture");
            var owner=GetWindow(hwnd,4); // GW_OWNER
            if(owner==IntPtr.Zero) return hwnd;
            hwnd=owner;
        }
        throw new Exception("Malformed native owner chain in Alt+Tab fixture");
    }

    static async Task NativeAltTabChord()
    {
        var foreground=GetForegroundWindow();
        uint pid; GetWindowThreadProcessId(foreground,out pid);
        var allowed=pid==(uint)Process.GetCurrentProcess().Id ||
                    (app!=null && pid==(uint)app.Id) ||
                    Anchors.Any(a=>a.Process!=null && !a.Process.HasExited && pid==(uint)a.Process.Id);
        Check(allowed,"Unexpected foreground application before Alt+Tab; refusing to send input. HWND="+foreground);
        // Hold Alt briefly while the shell opens the switcher. Input is only
        // sent from an explicitly opted-in interactive desktop E2E run.
        Inject(KeyInput(0x12,false),KeyInput(0x09,false),KeyInput(0x09,true));
        await Task.Delay(125);
        Inject(KeyInput(0x12,true));
    }

    static async Task RunNativeAltTab(int switches)
    {
        phase="native Alt+Tab preflight";
        Check(switches>=2 && switches%2==0,"Native Alt+Tab count must be a positive even number");
        await Until("Release mouse and modifier keys before native Alt+Tab test",
                    ()=>new[]{1,2,0x10,0x11,0x12}.All(k=>GetAsyncKeyState(k)>=0),10000);
        Preflight();
        GetCursorPos(out savedCursor); inputOwned=true;
        Fixture(true,"auto"); // group only the driver's path, NOT DesktopAnchor.exe
        try
        {
            var area=Screen.PrimaryScreen.WorkingArea;
            for(int i=0;i<2;i++)
            {
                var helper=new Form {
                    Text="WindowTabs AltTab fixture "+(i==0?"A":"B"),
                    StartPosition=FormStartPosition.Manual,
                    Location=new Point(area.Left+80,area.Top+100),
                    Size=new Size(600,380)
                };
                Forms.Add(helper);
                helper.Show();
                ShowWindow(helper.Handle,4); // SW_SHOWNOACTIVATE
            }
            var handles=Helpers();
            phase="native Alt+Tab grouping";
            app=Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Root,"WindowTabs.exe")){
                WorkingDirectory=Root,UseShellExecute=false});
            await Until("WindowTabs did not create one tab strip for two auto-grouped windows",
                        ()=>Strips().Count==1,15000);
            var strip=Strips().Single();
            var order=await ReadOrder(strip,2,handles);
            await Until("Combined group has not established shared GW_OWNER root",()=>{
                var first=GetWindow(order[0],4); var second=GetWindow(order[1],4);
                return first!=IntPtr.Zero && second!=IntPtr.Zero &&
                    first!=second && AltTabRootOwner(order[0])==AltTabRootOwner(order[1]); },10000);
            var root=AltTabRootOwner(order[0]);
            Check(!handles.Contains(root),"Combined group root owner is a real member, not synthetic host");
            Remember("combined-group-ready",new {
                host=root.ToInt64(),members=order.Select(h=>h.ToInt64()).ToArray(),
                owners=order.Select(h=>GetWindow(h,4).ToInt64()).ToArray()});

            // Separate process/path: cannot be accidentally auto-grouped with
            // the two application HWNDs. It is our known external MRU target.
            phase="native Alt+Tab anchor";
            var anchor=await StartAnchor();
            Check(SetForegroundWindow(anchor.Window),"Cannot foreground the independent anchor window");
            await Until("Independent anchor did not become foreground",
                        ()=>GetForegroundWindow()==anchor.Window);
            var lastGroupMember=order.Last();
            // Alternate between anchor and the one combined group. This is
            // a falsifiable 2-entity MRU test; no guesses about Explorer's
            // highlighted tile are needed. An unexpected HWND fails quickly.
            phase="native Alt+Tab switching";
            for(int i=0;i<switches;i++)
            {
                step="native switch "+(i+1)+"/"+switches;
                var expected=(i%2==0)?lastGroupMember:anchor.Window;
                var before=GetForegroundWindow();
                Remember("native-alt-tab-before",new {index=i,from=before.ToInt64(),
                    expected=expected.ToInt64(),groupRoot=AltTabRootOwner(lastGroupMember).ToInt64()});
                await NativeAltTabChord();
                var timeout=Stopwatch.StartNew();
                while(GetForegroundWindow()!=expected && timeout.ElapsedMilliseconds<1600)
                {
                    await Task.Delay(25);
                }
                var observed=GetForegroundWindow();
                Remember("native-alt-tab-after",new {index=i,expected=expected.ToInt64(),
                    observed=observed.ToInt64(),delayMs=timeout.ElapsedMilliseconds,
                    owner=observed==IntPtr.Zero?0:GetWindow(observed,4).ToInt64()});
                Check(observed==expected,
                      "Native Explorer Alt+Tab selected/activated unexpected HWND at step "+i+
                      "; expected="+expected+" actual="+observed);
                totalSwitches++;
                await Task.Delay(120); // avoid overlapping a still-closing switcher
            }
            completed=1;
            Log("PASS: "+totalSwitches+" native Explorer Alt+Tab toggles between one combined group and independent anchor.");
        }
        finally
        {
            foreach(var anchor in Anchors)
            {
                if(anchor.Process!=null)
                {
                    try {
                        if(!anchor.Process.HasExited) {
                            if(anchor.Window!=IntPtr.Zero) PostMessage(anchor.Window,0x10,IntPtr.Zero,IntPtr.Zero); // WM_CLOSE
                            if(!anchor.Process.WaitForExit(1200)) anchor.Process.Kill();
                        }
                    } catch(Exception) { }
                    anchor.Process.Dispose();
                }
            }
            Anchors.Clear();
        }
    }
}
