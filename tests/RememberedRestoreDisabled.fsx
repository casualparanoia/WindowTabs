// Regression: default-off restoration must leave manually grouped windows and app rules usable.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Debug/Win32.dll"
#r "Debug/WindowTabs.exe"
open System
open System.IO
open System.Threading
open System.Windows.Forms
open Bemo

let check condition message = if not condition then failwith message
let pumpUntil description predicate =
    let clock = Diagnostics.Stopwatch.StartNew()
    let mutable ok = predicate()
    while not ok && clock.ElapsedMilliseconds<10000L do
        Application.DoEvents()
        Thread.Sleep(10)
        ok <- predicate()
    check ok description

let main() =
    check (not Program.RememberedGroupRestore.enabled) "The fork's restoration switch is no longer off"
    let oldDirectory = Environment.CurrentDirectory
    let directory = Path.Combine(__SOURCE_DIRECTORY__,"Debug","no-restore-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    Environment.CurrentDirectory <- directory
    try
        let settings = new Settings(true,saveDelay=0)
        let api = settings :> ISettings
        api.setValue("replaceAltTab",box false)
        api.setValue("enableTabbingByDefault",box false)
        api.setValue("combineIconsInTaskbar",box false)
        let path = Diagnostics.Process.GetCurrentProcess().MainModule.FileName
        api.setValue("includedPaths",box(Set2(List2([path]))))
        let app = AppIdentity.key path
        let initial = [{ id=5151; seen=DateTime.UtcNow
                         members=[{ app=app; title="Remembered A" };{ app=app; title="Remembered B" }]
                         paths=Map.empty }]
        settings.rememberedGroups <- initial

        use one = new Form(ShowInTaskbar=false,Text="Token A")
        use two = new Form(ShowInTaskbar=false,Text="Token B")
        use namedA = new Form(ShowInTaskbar=false,Text="Remembered A")
        use namedB = new Form(ShowInTaskbar=false,Text="Remembered B")
        use late = new Form(ShowInTaskbar=false,Text="Loading")
        let all = [one;two;namedA;namedB;late]
        let handles = all |> List.map(fun form -> form.Handle)
        WindowGroupToken.save one.Handle 4242
        WindowGroupToken.save two.Handle 4242

        let baseFilter = FilterService() :> IFilterService
        Services.registerLocal<IFilterService>({ new IFilterService with
            member _.isAppWindow hwnd = List.contains hwnd handles
            member _.isAppWindowStyle hwnd = List.contains hwnd handles
            member _.isTabbableWindow hwnd = List.contains hwnd handles && baseFilter.getIsTabbingEnabledForProcess path
            member _.isTabbingEnabledForAllProcessesByDefault with get()=false and set _=()
            member _.setIsTabbingEnabledForProcess value enabled = baseFilter.setIsTabbingEnabledForProcess value enabled
            member _.setIsTabbingEnabledForProcesses values enabled = baseFilter.setIsTabbingEnabledForProcesses values enabled
            member _.getIsTabbingEnabledForProcess value = baseFilter.getIsTabbingEnabledForProcess value })
        use lifetime = new LifetimeScope(fun error -> raise error)
        let program = Program.Program(lifetime,settings=settings)
        program.initializePlugins(List2())
        for form in all do form.Show()
        program.refresh()
        let groupOf hwnd =
            Services.desktop.groups.list |> List.tryFind(fun group -> group.windows.contains((=)hwnd))
        pumpUntil "Startup did not discover the five fixture windows" (fun () ->
            handles |> List.forall(fun hwnd -> (groupOf hwnd).IsSome))
        let groups = handles |> List.choose groupOf
        check (groups |> List.distinctBy id |> List.length = handles.Length)
              "A token- or title-remembered group was restored despite the switch"
        check (handles |> List.forall(fun hwnd ->
            program.restoredToken hwnd = None && program.savedPlacement(OS().windowFromHwnd hwnd) = None))
              "One of the saved-placement entry points ignored the switch"
        late.Text <- "Remembered A"
        let watch = Diagnostics.Stopwatch.StartNew()
        while watch.ElapsedMilliseconds<1600L do
            Application.DoEvents()
            Thread.Sleep(20)
        check (handles |> List.choose groupOf |> List.distinctBy id |> List.length = handles.Length)
              "Delayed title settling restored a remembered group"
        check (settings.rememberedGroups=initial) "Startup destroyed the existing saved-group records"

        // Model the drag/drop regroup path: detach from its current group and attach to the
        // target marked manual. Group membership must still be editable during this run.
        let target = (groupOf one.Handle).Value :?> GroupInfo
        let source = (groupOf two.Handle).Value :?> GroupInfo
        target.group.isManual <- true
        target.addAfterDetaching(source,two.Handle)
        pumpUntil "Manual regrouping stopped working" (fun () ->
            match groupOf one.Handle,groupOf two.Handle with
            | Some a,Some b -> obj.ReferenceEquals(a,b)
            | _ -> false)
        check ((program :> IProgram).getAutoGroupingEnabled path = false)
              "The restoration flag unexpectedly enabled per-app auto grouping"
        for form in all do form.Close()
        program.refresh()
        pumpUntil "Fixture groups did not exit" (fun () -> Services.desktop.groups.isEmpty)
    finally
        Environment.CurrentDirectory <- oldDirectory
    printfn "PASS: token/title/late restoration disabled; saved records intact; manual regrouping works."

TestInit.run main
