// Native Explorer Alt+Tab against genuine Chromium-family HWNDs, using a
// throwaway --user-data-dir. This is an opt-in interactive workload only;
// it never attaches to or closes windows in the user's normal browser profile.
// The .NET Framework 4.x harness compiler supports C# 5 syntax.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

static partial class DesktopE2E
{
    // The existing native chord guard permits only known fixture HWND owners.
    // Extend it exclusively to the browser process launched from this stage.
    static readonly HashSet<uint> BrowserAllowedPids=new HashSet<uint>();
    static readonly List<Process> BrowserLaunchedProcesses=new List<Process>();

    static string BrowserArguments(string profile, bool first)
    {
        // Every launch uses the SAME unique staged directory. Chromium
        // forwards a second --new-window invocation to its isolated browser
        // process; no ordinary/default profile is ever selected.
        return "--user-data-dir=\"" + profile + "\" --no-first-run " +
               "--no-default-browser-check --disable-extensions --disable-sync " +
               "--disable-background-mode --window-size=850,530 " +
               (first ? "--window-position=50,90 " : "--window-position=160,145 ") +
               "--new-window about:blank";
    }

    static Process StartStagedBrowser(string executable, string profile, bool first)
    {
        var request=Process.Start(new ProcessStartInfo(executable,BrowserArguments(profile,first)) {
            WorkingDirectory=Root,UseShellExecute=false
        });
        Check(request!=null,"Failed to start the requested browser executable");
        BrowserLaunchedProcesses.Add(request);
        return request;
    }

    static List<IntPtr> BrowserTopWindows(int pid)
    {
        return Windows(pid).Where(h=>IsWindowVisible(h) && !IsIconicForAltTab(h) &&
            Class(h).StartsWith("Chrome_WidgetWin_",StringComparison.Ordinal)).ToList();
    }

    static List<IntPtr> BrowserGroupStrips(int appPid, IEnumerable<IntPtr> expectedWindows)
    {
        var expected=new HashSet<IntPtr>(expectedWindows);
        return Windows(appPid).Where(h=>IsWindowVisible(h) &&
            expected.Contains(GetWindow(h,4)) &&
            (GetWindowLong(h,-20)&0x80000)!=0 && (GetWindowLong(h,-20)&0x20)==0).ToList();
    }

    static async Task BrowserNativeSwitch(
        int index, IntPtr expected, IntPtr strip, List<IntPtr> members,
        IntPtr anchor, int browserPid)
    {
        step="browser-native-switch "+(index+1);
        var before=GetForegroundWindow();
        Remember("browser-native-before",new {
            index,from=before.ToInt64(),expected=expected.ToInt64(),
            activeStripOwner=GetWindow(strip,4).ToInt64(),browserPid
        });
        await NativeAltTabChord();
        var timer=Stopwatch.StartNew();
        var transitions=new List<object>();
        var last=before;
        while(GetForegroundWindow()!=expected && timer.ElapsedMilliseconds<1600)
        {
            var current=GetForegroundWindow();
            if(current!=last)
            {
                transitions.Add(new {elapsedMs=timer.ElapsedMilliseconds,hwnd=current.ToInt64()});
                last=current;
            }
            await Task.Delay(25);
        }
        var observed=GetForegroundWindow();
        Remember("browser-native-after",new {
            index,expected=expected.ToInt64(),observed=observed.ToInt64(),
            elapsedMs=timer.ElapsedMilliseconds,
            owner=observed==IntPtr.Zero?0:GetWindow(observed,4).ToInt64()
        });
        if(observed!=expected)
        {
            var mismatch=new {
                mode="isolated-chromium-browser",
                index,browserPid,elapsedMs=timer.ElapsedMilliseconds,
                before=DescribeNativeAltTabWindow(before),
                expected=DescribeNativeAltTabWindow(expected),
                observed=DescribeNativeAltTabWindow(observed),
                anchor=DescribeNativeAltTabWindow(anchor),
                browserMemberA=DescribeNativeAltTabWindow(members[0]),
                browserMemberB=DescribeNativeAltTabWindow(members[1]),
                strip=DescribeNativeAltTabWindow(strip),
                foregroundTransitions=transitions.ToArray()
            };
            File.WriteAllText(Path.Combine(Root,"foreground-mismatch.json"),Json.Serialize(mismatch));
            Remember("browser-native-mismatch",mismatch);
        }
        Check(observed==expected,
              "Native Alt+Tab chose unexpected HWND on isolated browser switch "+(index+1)+
              "; expected="+expected+" observed="+observed+" (see foreground-mismatch.json)");
        if(members.Contains(expected))
        {
            await Activated(expected,strip);
        }
        await Task.Delay(55);
        var r=Bounds(expected);
        var center=new Point((r.Left+r.Right)/2,(r.Top+r.Bottom)/2);
        var visible=TopLevelAt(center);
        Remember("browser-native-hit-test",new {
            index,expected=expected.ToInt64(),visible=visible.ToInt64()
        });
        Check(visible==expected,
              "Correct native Alt+Tab foreground but another window obscures isolated browser/anchor"+
              "; expected="+expected+" visible="+visible);
        totalSwitches++;
        await Task.Delay(120);
    }

    static async Task RunNativeAltTabBrowser(int switches)
    {
        phase="isolated browser native Alt+Tab preflight";
        Check(switches>=2 && switches%2==0,"Browser Alt+Tab count must be even and at least 2");
        await Until("Release input before isolated browser test",
                    ()=>new[]{1,2,0x10,0x11,0x12}.All(k=>GetAsyncKeyState(k)>=0),10000);
        Preflight();
        var executable=File.ReadAllText(Path.Combine(Root,"browser-executable.txt")).Trim();
        Check(Path.IsPathRooted(executable) && File.Exists(executable),
              "The explicit browser executable does not exist");
        var filename=Path.GetFileName(executable).ToLowerInvariant();
        Check(new[]{"vivaldi.exe","chrome.exe","chromium.exe","msedge.exe"}.Contains(filename),
              "Only Chromium-family browsers are supported by the isolated fixture");
        GetCursorPos(out savedCursor);
        inputOwned=true;

        // A brand-new profile path is exclusively owned by THIS E2E stage.
        // NEVER re-use the user's normal Vivaldi/Chrome profile.
        var profile=Path.Combine(Root,"browser-profile");
        Check(!Directory.Exists(profile),
              "Browser profile directory already exists; refusing to reuse it");
        Directory.CreateDirectory(profile);
        Process browser=null;
        try
        {
            phase="isolated Chromium window startup";
            browser=StartStagedBrowser(executable,profile,true);
            Check(!browser.HasExited,
                  "Browser launch process exited immediately; isolated profile may be unavailable");
            var browserPid=browser.Id;
            BrowserAllowedPids.Add((uint)browserPid);
            await Until("First isolated browser Chrome_WidgetWin HWND did not appear",
                        ()=>!browser.HasExited && BrowserTopWindows(browserPid).Count==1,20000);
            var first=BrowserTopWindows(browserPid).Single();

            // A second --new-window request with the *same* profile should be
            // forwarded to the original isolated Chromium browser process.
            StartStagedBrowser(executable,profile,false);
            await Until("Second isolated Chromium browser window did not appear in the original process",
                        ()=>!browser.HasExited && BrowserTopWindows(browserPid).Count==2,20000);
            var browserWindows=BrowserTopWindows(browserPid);
            Check(browserWindows.Contains(first) && browserWindows.Distinct().Count()==2,
                  "Chromium created unexpected HWNDs or replaced the first window");
            Check(browserWindows.All(h=> {
                uint pid; GetWindowThreadProcessId(h,out pid);
                return pid==(uint)browserPid && Class(h).StartsWith("Chrome_WidgetWin_");
            }),"Browser HWND ownership did not match the isolated launched browser PID");
            Remember("browser-fixture",new {
                browserPid,browserClasses=browserWindows.Select(Class).ToArray(),
                windows=browserWindows.Select(h=>h.ToInt64()).ToArray()
            });

            // Add only the explicit browser executable to WindowTabs's staged
            // portable settings. No running personal browser HWND is in this
            // fixture's browser PID allowlist or WindowTabs includedPaths.
            Fixture(true,"auto",executable);
            phase="isolated browser WindowTabs grouping";
            app=Process.Start(new ProcessStartInfo(Path.Combine(Root,"WindowTabs.exe")) {
                WorkingDirectory=Root,UseShellExecute=false
            });
            await Until("WindowTabs did not combine two browser HWNDs into one tab strip",
                        ()=>BrowserGroupStrips(app.Id,browserWindows).Count==1,18000);
            Check(!HasCrash(),"Staged WindowTabs crashed during browser grouping");
            var strip=BrowserGroupStrips(app.Id,browserWindows).Single();
            var members=await ReadOrder(strip,2,browserWindows);
            var root=AltTabRootOwner(members[0]);
            Check(!browserWindows.Contains(root) &&
                  members.All(h=>GetWindow(h,4)!=IntPtr.Zero && AltTabRootOwner(h)==root),
                  "Isolated browser windows did not share one synthetic native group owner root");
            Remember("browser-group-ready",new {
                browserPid,strip=strip.ToInt64(),ownerRoot=root.ToInt64(),
                members=members.Select(h=>h.ToInt64()).ToArray()
            });

            phase="isolated browser Alt+Tab anchor";
            var anchor=await StartAnchor(verifyDesktop:false);
            await ClickNativeAltTabAnchor(anchor);
            phase="native Alt+Tab isolated Chromium group";
            for(int i=0;i<switches;i+=2)
            {
                step="prepare browser pair "+(i/2+1);
                var member=members[(i/2)%2];
                Remember("browser-rotate-member",new {pair=i/2,member=member.ToInt64()});
                await Click(strip,(i/2)%2,2);
                await Activated(member,strip);
                await ClickNativeAltTabAnchor(anchor);
                await BrowserNativeSwitch(i,member,strip,members,anchor.Window,browserPid);
                await BrowserNativeSwitch(i+1,anchor.Window,strip,members,anchor.Window,browserPid);
            }
            completed=1;
            Log("PASS: "+totalSwitches+" native Alt+Tab switches between isolated Chromium browser group and independent anchor; browser member, owner and visual hit-test verified.");
        }
        finally
        {
            // Only explicit fixture processes are closed/killed. No discovery
            // or termination by executable name, so user's normal browser
            // sessions are never targeted.
            if(browser!=null)
            {
                foreach(var hwnd in BrowserTopWindows(browser.Id))
                    PostMessage(hwnd,0x10,IntPtr.Zero,IntPtr.Zero); // WM_CLOSE
            }
            foreach(var anchor in Anchors)
            {
                try
                {
                    if(anchor.Process!=null && !anchor.Process.HasExited)
                    {
                        if(anchor.Window!=IntPtr.Zero)
                            PostMessage(anchor.Window,0x10,IntPtr.Zero,IntPtr.Zero);
                        if(!anchor.Process.WaitForExit(1500)) anchor.Process.Kill();
                    }
                }
                catch(Exception) { }
                if(anchor.Process!=null) anchor.Process.Dispose();
            }
            Anchors.Clear();
            foreach(var launched in BrowserLaunchedProcesses)
            {
                try
                {
                    if(!launched.HasExited && !launched.WaitForExit(1800))
                        launched.Kill();
                }
                catch(Exception) { }
                launched.Dispose();
            }
            BrowserLaunchedProcesses.Clear();
            BrowserAllowedPids.Clear();
            // Keep action/failure JSON but remove this temporary profile
            // (which contains no personal browser state). If Chromium is
            // still releasing files, report it without hiding test errors.
            try { Directory.Delete(profile,true); }
            catch(Exception e) { Log("NOTE: Isolated browser profile cleanup deferred: "+e.GetType().Name); }
        }
    }
}
