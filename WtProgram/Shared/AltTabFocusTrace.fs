namespace Bemo

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text
open System.Threading
open Bemo.Json
open Bemo.Win32

/// An opt-in, passive foreground flight recorder. No window titles, key text,
/// screenshots, file paths, or process command lines are collected.
module AltTabFocusTrace =
    let private folder = AppDomain.CurrentDomain.BaseDirectory
    let enabled = IO.File.Exists(IO.Path.Combine(folder,"focus-trace.enabled"))
    let private path = IO.Path.Combine(folder,"focus-trace.jsonl")
    let private clock = Stopwatch.StartNew()
    let private buffer = ConcurrentQueue<string>()
    let private stateGate = obj()
    let private writerGate = obj()
    let mutable private activeAttempt = false
    let mutable private altDown = false
    let mutable private lastTabTime = -10000L
    let mutable private attempt = 0

    /// This is *not* a correctness verdict: a user can intentionally select the
    /// same window. It is a neutral description of the foreground sequence.
    let foregroundPattern before (observed:IntPtr list) =
        let moved = observed |> List.exists(fun hwnd -> hwnd<>IntPtr.Zero && hwnd<>before)
        if not moved then "no-foreground-change"
        elif observed |> List.tryLast = Some before then "foreground-returned-to-start"
        else "foreground-changed"

    /// Only HWND geometry/ownership metadata is sampled; no titles or text.
    /// A combined tab's application HWND will normally have a WindowTabs-owned
    /// proxy HWND as GW_OWNER. The normal uncombined window usually has none.
    let private ownerChain hwnd =
        let rec walk (seen:Set<IntPtr>) depth (current:IntPtr) =
            if depth>=4 || current=IntPtr.Zero || seen.Contains current || not(WinUserApi.IsWindow current) then []
            else
                let owner = WinUserApi.GetWindow(current,GetWindowConstants.GW_OWNER)
                let _,pid = WinUserApi.GetWindowThreadProcessId(current)
                let style = WinUserApi.GetWindowLong(current,WindowLongFieldOffset.GWL_EXSTYLE).ToInt64()
                let detail =
                    sprintf "%d(pid=%d,owner=%d,visible=%b,minimized=%b,exstyle=0x%X)" (current.ToInt64()) pid (owner.ToInt64()) (WinUserApi.IsWindowVisible current) (WinUserApi.IsIconic current) style
                detail :: walk (seen.Add current) (depth+1) owner
        try walk Set.empty 0 hwnd |> String.concat " -> "
        with _ -> "unavailable"

    let private currentAttempt() = lock stateGate (fun () -> attempt)
    let record (stage:string) (hwnd:IntPtr) (detail:string) =
        if enabled then
            try
                let foreground = WinUserApi.GetForegroundWindow()
                let entry = JObject(
                    JProperty("utc",DateTime.UtcNow.ToString("O")),
                    JProperty("elapsedMs",clock.ElapsedMilliseconds),
                    JProperty("attempt",currentAttempt()),
                    JProperty("stage",stage),
                    JProperty("hwnd",hwnd.ToInt64()),
                    JProperty("foreground",foreground.ToInt64()),
                    JProperty("detail",detail)).ToString(Formatting.None)
                buffer.Enqueue(entry)
            with _ -> ()

    let snapshot phase =
        if enabled then
            let foreground = WinUserApi.GetForegroundWindow()
            record ("owner-chain-"+phase) foreground (ownerChain foreground)

    let private flush() =
        if enabled then
            lock writerGate (fun () ->
                try
                    let sb = StringBuilder()
                    let mutable line = Unchecked.defaultof<string>
                    while buffer.TryDequeue(&line) do sb.AppendLine(line) |> ignore
                    if sb.Length>0 then
                        // Bounded storage: keep only a single, recent backup of old events.
                        if IO.File.Exists(path) && IO.FileInfo(path).Length > 5L*1024L*1024L then
                            let previous = IO.Path.Combine(folder,"focus-trace.previous.jsonl")
                            IO.File.Copy(path,previous,true)
                            IO.File.WriteAllText(path,String.Empty)
                        IO.File.AppendAllText(path,sb.ToString())
                with _ -> ())

    let mark kind =
        record ("manual-"+kind) IntPtr.Zero "user-marked-recent-alt-tab-attempt"
        flush()

    let attach (os:OS) : IDisposable =
        if not enabled then {new IDisposable with member _.Dispose()=()}
        else
            record "recorder-start" IntPtr.Zero "passive; no key suppression; HWND IDs only"
            let foregroundHook = os.setSingleWinEvent WinEvent.EVENT_SYSTEM_FOREGROUND (fun hwnd ->
                record "os-foreground" hwnd "")
            let switchStart = os.setSingleWinEvent WinEvent.EVENT_SYSTEM_SWITCHSTART (fun hwnd ->
                record "switcher-start" hwnd "")
            let switchEnd = os.setSingleWinEvent WinEvent.EVENT_SYSTEM_SWITCHEND (fun hwnd ->
                record "switcher-end" hwnd "")
            let keyboard = os.registerKeyboardLLHook (fun (message:IntPtr,data:KBDLLHOOKSTRUCT) ->
                try
                    let msg = message.ToInt32()
                    let down = msg=WindowMessages.WM_KEYDOWN || msg=WindowMessages.WM_SYSKEYDOWN
                    let up = msg=WindowMessages.WM_KEYUP || msg=WindowMessages.WM_SYSKEYUP
                    let vk = data.vkCode
                    let alt = vk=0x12 || vk=0xA4 || vk=0xA5
                    let tab = vk=0x09
                    if alt && (down || up) then
                        let wasAttempt = lock stateGate (fun () ->
                            altDown <- down
                            if up then
                                let active = activeAttempt
                                activeAttempt <- false
                                active
                            else false)
                        if wasAttempt then
                            record "alt-released" IntPtr.Zero ""
                            // The shell can activate the selected window *after* Alt-up.
                            // Delayed samples help identify late activation or reassertion.
                            ThreadPool.QueueUserWorkItem(fun _ ->
                                Thread.Sleep(200)
                                record "sample-200ms" IntPtr.Zero ""
                                snapshot "200ms"
                                Thread.Sleep(600)
                                record "sample-800ms" IntPtr.Zero ""
                                snapshot "800ms") |> ignore
                    elif tab && down then
                        let isAlt = (data.flags &&& LlKeyboardHookFlags.LLKHF_ALTDOWN) <> 0 ||
                                    lock stateGate (fun () -> altDown)
                        if isAlt then
                            lock stateGate (fun () ->
                                if not activeAttempt then
                                    attempt <- attempt+1
                                    activeAttempt <- true
                                lastTabTime <- clock.ElapsedMilliseconds)
                            record "alt-tab-pressed" IntPtr.Zero
                                (sprintf "injected=%b" ((int data.flags &&& 0x10)<>0))
                    elif tab && up then
                        if lock stateGate (fun () -> activeAttempt) then
                            record "tab-released" IntPtr.Zero ""
                with _ -> ()
                // Never intercept or alter Alt+Tab or any other keystroke.
                None)
            let timer = new Timer(TimerCallback(fun _ -> flush()),null,1000,1000)
            let mutable disposed = 0
            {new IDisposable with
                member _.Dispose() =
                    if Interlocked.Exchange(&disposed,1)=0 then
                        keyboard.Dispose()
                        foregroundHook.Dispose()
                        switchStart.Dispose()
                        switchEnd.Dispose()
                        timer.Dispose()
                        record "recorder-stop" IntPtr.Zero ""
                        flush()}
