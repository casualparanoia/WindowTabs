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
using System.Threading;
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

    static void CheckNoUnrelatedBrowserWindows(int stagedPid, string browserName)
    {
        // WindowTabs includedPaths matches an *executable*, not a specific
        // PID. If ordinary browser windows enter while this test runs,
        // WindowTabs could group them even though the driver never clicks
        // them. Refuse all foreign same-browser top-level HWNDs.
        var unrelated=new List<object>();
        EnumWindows((hwnd,data)=>{
            if(!IsWindowVisible(hwnd) ||
               !Class(hwnd).StartsWith("Chrome_WidgetWin_",StringComparison.Ordinal))
                return true;
            uint ownerPid; GetWindowThreadProcessId(hwnd,out ownerPid);
            if(ownerPid==(uint)stagedPid) return true;
            try
            {
                using(var process=Process.GetProcessById((int)ownerPid))
                    if(string.Equals(process.ProcessName,browserName,
                                      StringComparison.OrdinalIgnoreCase))
                        unrelated.Add(new {hwnd=hwnd.ToInt64(),pid=ownerPid});
            }
            catch(Exception) { }
            return true;
        },IntPtr.Zero);
        Check(unrelated.Count==0,
              "A foreign browser window exists. Exit regular browser sessions and retry "+
              "the isolated fixture; refusing to risk grouping personal windows. "+
              Json.Serialize(unrelated.ToArray()));
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
                browserMembers=members.Select(DescribeNativeAltTabWindow).ToArray(),
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

    static async Task RunNativeAltTabBrowser(int switches, bool lifecycle=false)
    {
        phase=lifecycle ? "browser membership lifecycle preflight" :
                          "isolated browser native Alt+Tab preflight";
        Check(switches>=2 && switches%2==0,"Browser Alt+Tab count must be even and at least 2");
        Check(!lifecycle || switches>=6,
              "Browser lifecycle test requires at least 6 switches (2 per membership phase)");
        await Until("Release input before isolated browser test",
                    ()=>new[]{1,2,0x10,0x11,0x12}.All(k=>GetAsyncKeyState(k)>=0),10000);
        Preflight();
        var executable=File.ReadAllText(Path.Combine(Root,"browser-executable.txt")).Trim();
        Check(Path.IsPathRooted(executable) && File.Exists(executable),
              "The explicit browser executable does not exist");
        var filename=Path.GetFileName(executable).ToLowerInvariant();
        Check(new[]{"vivaldi.exe","chrome.exe","chromium.exe","msedge.exe"}.Contains(filename),
              "Only Chromium-family browsers are supported by the isolated fixture");
        var browserName=Path.GetFileNameWithoutExtension(executable);
        // Important: staged WindowTabs filters by executable path, NOT just
        // the staged PID. Refuse to start if the user has any normal browser
        // instance of the same name, including background resident processes.
        var preexisting=Process.GetProcessesByName(browserName);
        try
        {
            Check(preexisting.Length==0,
                  "Close all existing "+browserName+" processes before testing; "+
                  "WindowTabs includedPaths cannot isolate a personal browser session "+
                  "from a staged session of the same executable.");
        }
        finally { foreach(var existing in preexisting) existing.Dispose(); }
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

            // IncludedPaths targets the whole browser executable, not a PID:
            // demand exclusive use of this browser name while grouping.
            CheckNoUnrelatedBrowserWindows(browserPid,browserName);
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
            phase=lifecycle ? "native Alt+Tab browser membership lifecycle" :
                              "native Alt+Tab isolated Chromium group";
            // Explicit, bounded three-phase workload: 2 -> 3 -> 2 browser
            // windows. Each phase gets at least one complete Alt+Tab pair.
            // The baseline mode remains unchanged.
            var beforeCount=lifecycle ? Math.Max(2,2*(switches/6)) : switches;
            var expandedCount=lifecycle ? Math.Max(2,2*(switches/6)) : 0;
            var afterCount=lifecycle ? switches-beforeCount-expandedCount : 0;
            Check(!lifecycle || (afterCount>=2 && afterCount%2==0),
                  "Invalid browser membership phase allocation");
            IntPtr addedWindow=IntPtr.Zero;
            for(int i=0;i<switches;i+=2)
            {
                step="prepare browser pair "+(i/2+1);
                CheckNoUnrelatedBrowserWindows(browserPid,browserName);
                if(lifecycle && i==beforeCount)
                {
                    phase="browser lifecycle: add third window to live group";
                    Check(browserWindows.Count==2 &&
                          BrowserTopWindows(browserPid).Count==2,
                          "Unexpected browser HWND count before adding the third window");
                    // Use the same staged profile, executable and browser PID.
                    // The second/third launcher request may forward to the
                    // already-running *isolated* browser instance.
                    StartStagedBrowser(executable,profile,false);
                    await Until("Third isolated Chromium top-level window did not appear",
                                ()=>!browser.HasExited &&
                                    BrowserTopWindows(browserPid).Count==3,20000);
                    var expanded=BrowserTopWindows(browserPid);
                    Check(members.All(expanded.Contains) && expanded.Distinct().Count()==3,
                          "Existing isolated browser HWND changed while adding third window");
                    addedWindow=expanded.Single(h=>!members.Contains(h));
                    // Auto-grouping must absorb the late-created browser
                    // HWND, keeping a single combined taskbar entry.
                    await Until("WindowTabs did not attach all three browser HWNDs to one native group",
                                ()=>{
                                    if(BrowserGroupStrips(app.Id,expanded).Count!=1 ||
                                       expanded.Any(h=>GetWindow(h,4)==IntPtr.Zero)) return false;
                                    var sharedRoot=AltTabRootOwner(expanded[0]);
                                    return expanded.All(h=>AltTabRootOwner(h)==sharedRoot);
                                },18000);
                    strip=BrowserGroupStrips(app.Id,expanded).Single();
                    members=await ReadOrder(strip,3,expanded);
                    var expandedRoot=AltTabRootOwner(members[0]);
                    Check(members.All(h=>GetWindow(h,4)!=IntPtr.Zero &&
                                         AltTabRootOwner(h)==expandedRoot),
                          "Expanded 3-window browser group has inconsistent owner roots");
                    Remember("browser-group-expanded",new {
                        added=addedWindow.ToInt64(),members=members.Select(h=>h.ToInt64()).ToArray(),
                        root=expandedRoot.ToInt64(),strip=strip.ToInt64()
                    });
                    phase="browser lifecycle: expanded group native Alt+Tab";
                }
                if(lifecycle && i==beforeCount+expandedCount)
                {
                    phase="browser lifecycle: close third window";
                    Check(addedWindow!=IntPtr.Zero && members.Contains(addedWindow),
                          "Third browser member was not established before closure");
                    // Close ONLY the just-created staged browser HWND.
                    // Failure to close is a membership/setup failure, not
                    // an Alt+Tab wrong-target result.
                    Check(PostMessage(addedWindow,0x10,IntPtr.Zero,IntPtr.Zero),
                          "WM_CLOSE failed for staged third Chromium window");
                    await Until("Third Chromium window did not close",
                                ()=>!IsWindow(addedWindow) &&
                                    BrowserTopWindows(browserPid).Count==2,15000);
                    var remaining=BrowserTopWindows(browserPid);
                    Check(members.Where(h=>h!=addedWindow).All(remaining.Contains) &&
                          remaining.Distinct().Count()==2,
                          "Closing the third browser HWND unexpectedly changed other members");
                    await Until("WindowTabs did not restore the two-member native group",
                                ()=>{
                                    if(BrowserGroupStrips(app.Id,remaining).Count!=1 ||
                                       remaining.Any(h=>GetWindow(h,4)==IntPtr.Zero)) return false;
                                    var sharedRoot=AltTabRootOwner(remaining[0]);
                                    return remaining.All(h=>AltTabRootOwner(h)==sharedRoot);
                                },18000);
                    strip=BrowserGroupStrips(app.Id,remaining).Single();
                    members=await ReadOrder(strip,2,remaining);
                    var remainingRoot=AltTabRootOwner(members[0]);
                    Check(members.All(h=>GetWindow(h,4)!=IntPtr.Zero &&
                                         AltTabRootOwner(h)==remainingRoot),
                          "Two-member browser group has inconsistent owner roots after close");
                    Check(!IsWindow(addedWindow),
                          "Closed browser window reappeared after group contraction");
                    Remember("browser-group-contracted",new {
                        removed=addedWindow.ToInt64(),
                        members=members.Select(h=>h.ToInt64()).ToArray(),
                        root=remainingRoot.ToInt64(),strip=strip.ToInt64()
                    });
                    phase="browser lifecycle: restored group native Alt+Tab";
                }
                CheckNoUnrelatedBrowserWindows(browserPid,browserName);
                Check(BrowserTopWindows(browserPid).Count==members.Count,
                      "Browser fixture window count diverged from expected group membership");
                var slot=(i/2)%members.Count;
                var member=members[slot];
                Remember("browser-rotate-member",new {
                    pair=i/2,member=member.ToInt64(),memberCount=members.Count,
                    lifecycle
                });
                await Click(strip,slot,members.Count);
                await Activated(member,strip);
                await ClickNativeAltTabAnchor(anchor);
                await BrowserNativeSwitch(i,member,strip,members,anchor.Window,browserPid);
                await BrowserNativeSwitch(i+1,anchor.Window,strip,members,anchor.Window,browserPid);
            }
            completed=1;
            if(lifecycle)
                Log("PASS: "+totalSwitches+" genuine browser native Alt+Tab switches across 2 -> 3 -> 2 group membership; foreground/owner/visual hit-test agreed.");
            else
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
            var browserProcessCleanup=new List<object>();
            var allBrowserProcessesExited=true;
            foreach(var launched in BrowserLaunchedProcesses)
            {
                var pid=launched.Id;
                var exited=false;
                try
                {
                    // Chromium's main and forwarding processes may still
                    // flush SQLite/LevelDB files after closing the windows.
                    // Let each spawned PID exit normally before forcing it.
                    if(!launched.HasExited && !launched.WaitForExit(3000))
                    {
                        launched.Kill();
                        launched.WaitForExit(3000);
                    }
                    exited=launched.HasExited;
                }
                catch(Exception e)
                {
                    Log("NOTE: Could not confirm staged browser PID "+pid+
                        " exited: "+e.GetType().Name);
                }
                allBrowserProcessesExited=allBrowserProcessesExited && exited;
                browserProcessCleanup.Add(new {pid,exited});
                launched.Dispose();
            }
            BrowserLaunchedProcesses.Clear();
            BrowserAllowedPids.Clear();

            // Never remove a profile when one of our spawned browser PIDs
            // could still own open files. Otherwise retry only this unique
            // test-stage directory, allowing Chromium children to release
            // SQLite and IndexedDB handles. Do not touch default profiles.
            var allExited=allBrowserProcessesExited;
            var profileRemoved=false;
            var cleanupAttempts=0;
            string cleanupError=null;
            if(allExited)
            {
                for(int attempt=0;attempt<12 && !profileRemoved;attempt++)
                {
                    cleanupAttempts++;
                    try
                    {
                        if(Directory.Exists(profile)) Directory.Delete(profile,true);
                        profileRemoved=!Directory.Exists(profile);
                    }
                    catch(IOException e) { cleanupError=e.Message; }
                    catch(UnauthorizedAccessException e) { cleanupError=e.Message; }
                    if(!profileRemoved) Thread.Sleep(350);
                }
            }
            else cleanupError="One or more staged browser PIDs may still be running";
            File.WriteAllText(Path.Combine(Root,"browser-cleanup.json"),Json.Serialize(new {
                profileRemoved,cleanupAttempts,profileFolder=Path.GetFileName(profile),
                spawnedProcesses=browserProcessCleanup.ToArray(),error=profileRemoved?null:cleanupError
            }));
            if(!profileRemoved)
                Log("NOTE: Disposable browser profile remains at "+profile+
                    "; see browser-cleanup.json. Remove only after all staged browser processes exit.");
        }
    }
}
