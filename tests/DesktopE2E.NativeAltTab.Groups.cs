// Opt-in native Explorer Alt+Tab test between TWO simultaneously combined
// WindowTabs groups. Unlike the group-to-external-anchor fixture, both
// directions must target real members owned by distinct taskbar proxy roots.
// C# 5 compatible for the .NET Framework desktop E2E compiler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static partial class DesktopE2E
{
    static async Task CheckNativeAltTabBetweenGroups(
        int stepNumber, IntPtr expected, IntPtr expectedStrip,
        List<IntPtr> membersA, List<IntPtr> membersB,
        IntPtr stripA, IntPtr stripB)
    {
        step="group-to-group native switch "+(stepNumber+1);
        var before=GetForegroundWindow();
        Remember("two-groups-alt-tab-before",new {
            index=stepNumber,from=before.ToInt64(),expected=expected.ToInt64(),
            ownerA=GetWindow(stripA,4).ToInt64(),
            ownerB=GetWindow(stripB,4).ToInt64(),
            groupRootA=AltTabRootOwner(membersA[0]).ToInt64(),
            groupRootB=AltTabRootOwner(membersB[0]).ToInt64()
        });
        await NativeAltTabChord();
        var timer=Stopwatch.StartNew();
        var transitions=new List<object>();
        var previous=before;
        while(GetForegroundWindow()!=expected && timer.ElapsedMilliseconds<1600)
        {
            var actual=GetForegroundWindow();
            if(actual!=previous)
            {
                transitions.Add(new {elapsedMs=timer.ElapsedMilliseconds,hwnd=actual.ToInt64()});
                previous=actual;
            }
            await Task.Delay(25);
        }
        var observed=GetForegroundWindow();
        Remember("two-groups-alt-tab-after",new {
            index=stepNumber,expected=expected.ToInt64(),actual=observed.ToInt64(),
            delayMs=timer.ElapsedMilliseconds,
            expectedStripOwner=GetWindow(expectedStrip,4).ToInt64()
        });
        if(observed!=expected)
        {
            var failure=new {
                mode="two-combined-groups",index=stepNumber,elapsedMs=timer.ElapsedMilliseconds,
                before=DescribeNativeAltTabWindow(before),
                expected=DescribeNativeAltTabWindow(expected),
                observed=DescribeNativeAltTabWindow(observed),
                groupA0=DescribeNativeAltTabWindow(membersA[0]),
                groupA1=DescribeNativeAltTabWindow(membersA[1]),
                groupB0=DescribeNativeAltTabWindow(membersB[0]),
                groupB1=DescribeNativeAltTabWindow(membersB[1]),
                stripA=DescribeNativeAltTabWindow(stripA),
                stripB=DescribeNativeAltTabWindow(stripB),
                foregroundTransitions=transitions.ToArray()
            };
            File.WriteAllText(Path.Combine(Root,"foreground-mismatch.json"),Json.Serialize(failure));
            Remember("two-groups-alt-tab-mismatch",failure);
        }
        Check(observed==expected,
              "Native Alt+Tab between distinct combined groups chose an unexpected foreground at step "+
              (stepNumber+1)+"; expected="+expected+" observed="+observed+
              " (see foreground-mismatch.json)");

        // The expected HWND can be foreground while its sibling is visually
        // covering it. Verify the group's active proxy owner and the actual
        // unobscured client center, not just GetForegroundWindow().
        await Activated(expected,expectedStrip);
        await Task.Delay(55);
        var r=Bounds(expected);
        var center=new Point((r.Left+r.Right)/2,(r.Top+r.Bottom)/2);
        var visible=TopLevelAt(center);
        Remember("two-groups-alt-tab-hit-test",new {
            index=stepNumber,expected=expected.ToInt64(),visible=visible.ToInt64(),
            x=center.X,y=center.Y
        });
        Check(visible==expected,
              "Native Alt+Tab reached expected group member but another window is visually on top: expected="+
              expected+" actualAtCenter="+visible+" step="+(stepNumber+1));
        totalSwitches++;
        await Task.Delay(120);
    }

    static async Task RunNativeAltTabTwoGroups(int switches)
    {
        phase="two combined groups native Alt+Tab preflight";
        Check(switches>=2 && switches%2==0,"Two-group Alt+Tab count must be a positive even number");
        await Until("Release mouse and modifier keys before two-group test",
                    ()=>new[]{1,2,0x10,0x11,0x12}.All(k=>GetAsyncKeyState(k)>=0),10000);
        Preflight();
        GetCursorPos(out savedCursor); inputOwned=true;
        // Four helpers from one executable. Auto-grouping would incorrectly
        // put all four in ONE group, so use the production manual drag path.
        Fixture(true,"manual");
        var area=Screen.PrimaryScreen.WorkingArea;
        var positions=new[]{
            new Point(area.Left+30,area.Top+100),
            new Point(area.Left+520,area.Top+100),
            new Point(area.Left+30,area.Top+390),
            new Point(area.Left+520,area.Top+390)
        };
        for(int i=0;i<4;i++)
        {
            var helper=new Form {
                Text="WindowTabs two-group native E2E "+(char)('A'+i),
                StartPosition=FormStartPosition.Manual,
                Location=positions[i],
                Size=new Size(440,175)
            };
            Forms.Add(helper);
            helper.Show();
            ShowWindow(helper.Handle,4); // SW_SHOWNOACTIVATE
        }
        var handles=Helpers();
        phase="two combined groups setup";
        app=Process.Start(new ProcessStartInfo(Path.Combine(Root,"WindowTabs.exe")) {
            WorkingDirectory=Root,UseShellExecute=false
        });
        await Until("Four independent test tab strips did not appear",
                    ()=>Strips().Count==4,15000);
        Check(!HasCrash(),"WindowTabs logged a startup exception");

        phase="two combined groups grouping";
        var sourceA=Strips().Single(h=>GetWindow(h,4)==handles[0]);
        var stripA=Strips().Single(h=>GetWindow(h,4)==handles[1]);
        await Drag(sourceA,stripA,1);
        await Until("First manual pair did not become one group",()=>Strips().Count==3);
        var membersA=await ReadOrder(stripA,2,handles.Take(2));

        var sourceB=Strips().Single(h=>GetWindow(h,4)==handles[2]);
        var stripB=Strips().Single(h=>GetWindow(h,4)==handles[3]);
        await Drag(sourceB,stripB,1);
        await Until("Second manual pair did not become a second group",()=>Strips().Count==2);
        var membersB=await ReadOrder(stripB,2,handles.Skip(2));

        // Different *combined taskbar hosts*, each owning two unique tab
        // preview proxies. Assert this topology before interpreting native
        // Alt+Tab results as group-to-group behavior.
        var rootA=AltTabRootOwner(membersA[0]);
        var rootB=AltTabRootOwner(membersB[0]);
        Check(rootA!=rootB && !handles.Contains(rootA) && !handles.Contains(rootB),
              "Two combined groups did not acquire distinct synthetic native owner roots");
        Check(membersA.All(h=>GetWindow(h,4)!=IntPtr.Zero && AltTabRootOwner(h)==rootA) &&
              membersB.All(h=>GetWindow(h,4)!=IntPtr.Zero && AltTabRootOwner(h)==rootB),
              "Grouped member owner roots changed before native Alt+Tab testing");
        Remember("two-groups-ready",new {
            rootA=rootA.ToInt64(),rootB=rootB.ToInt64(),
            stripA=stripA.ToInt64(),stripB=stripB.ToInt64(),
            membersA=membersA.Select(h=>h.ToInt64()).ToArray(),
            membersB=membersB.Select(h=>h.ToInt64()).ToArray()
        });

        phase="native Alt+Tab two combined groups";
        // Re-prime an unambiguous group-A then group-B MRU pair, rotating the
        // selected tab inside BOTH groups between pairs. Alt+Tab A then B.
        for(int i=0;i<switches;i+=2)
        {
            step="prepare group pair "+(i/2+1);
            var selectedA=membersA[(i/2)%2];
            var selectedB=membersB[((i/2)+1)%2];
            Remember("two-groups-rotate",new {
                pair=i/2,selectedA=selectedA.ToInt64(),selectedB=selectedB.ToInt64()
            });
            await Click(stripA,(i/2)%2,2);
            await Activated(selectedA,stripA);
            await Click(stripB,((i/2)+1)%2,2);
            await Activated(selectedB,stripB);
            await CheckNativeAltTabBetweenGroups(i,selectedA,stripA,membersA,membersB,stripA,stripB);
            await CheckNativeAltTabBetweenGroups(i+1,selectedB,stripB,membersA,membersB,stripA,stripB);
        }
        completed=1;
        Log("PASS: "+totalSwitches+" native Explorer Alt+Tab switches between TWO combined taskbar groups; both members rotated, foreground/owner/visual hit-test agreed.");
    }
}
