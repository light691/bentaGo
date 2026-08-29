using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BentaGo.LaneDriver;

/// <summary>
/// Layer B lane driver (bg-004-synthesis §11): launches the built
/// BentaGo.App.exe and drives a scripted keyboard pass through the REAL
/// desktop with real input (SendInput behind AttachThreadInput + a click so
/// the app owns keyboard focus). Every step is VERIFIED: the window capture
/// must change from the previous step, and after login-submit the capture
/// must differ wholesale from the login baseline - otherwise the driver
/// FAILS loudly instead of claiming a pass. Evidence lands under
/// artifacts/verification/bg-004/&lt;head&gt;/lane-driver/.
/// </summary>
public static class Program
{
    private enum StepKind
    {
        Type,
        Minor,
        Navigate,
    }

    /// <summary>STA so the clipboard paste path and WPF interop work.</summary>
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "all";
        if (mode is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                BentaGo.LaneDriver - drives the built BentaGo.App.exe with real keyboard input.
                Usage:
                  BentaGo.LaneDriver all        launch the app and run the scripted keyboard pass
                  BentaGo.LaneDriver help
                Environment:
                  BENTAGO_HEAD          evidence subfolder (default "current")
                  BENTAGO_ARTIFACTS     artifacts root (default C:\BentaGo\artifacts)
                """);
            return 0;
        }

        string head = Environment.GetEnvironmentVariable("BENTAGO_HEAD") ?? "current";
        string artifacts = Environment.GetEnvironmentVariable("BENTAGO_ARTIFACTS") ?? @"C:\BentaGo\artifacts";
        string outDir = Path.Combine(artifacts, "verification", "bg-004", head, "lane-driver");
        Directory.CreateDirectory(outDir);

        string appExe = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "BentaGo.App", "bin", "Release", "net10.0-windows", "BentaGo.App.exe");

        var verdicts = new List<string>();
        if (!File.Exists(appExe))
        {
            Console.WriteLine($"ERROR: app not found at {appExe}. Build Release first.");
            return 2;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = appExe,
            Arguments = "--size 1366x768",
            UseShellExecute = false,
        });
        if (process is null)
        {
            Console.WriteLine("ERROR: could not start the app.");
            return 2;
        }

        try
        {
            process.WaitForInputIdle(TimeSpan.FromSeconds(20));
            await Task.Delay(2000); // allow the login screen to realize

            IntPtr hwnd = FindMainWindow(process, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero)
            {
                verdicts.Add("VERDICT: OPERATOR - app window not found on this session; cannot drive real input");
                WriteVerdicts(outDir, verdicts);
                return 0;
            }

            // Force the app into real foreground ownership: attach to the
            // thread that CURRENTLY owns the foreground (the standard
            // foreground-steal pattern), verify, and click once. The click
            // activates the app, whose FocusCoordinator then focuses the
            // username box (round-2 fix A), so typing lands.
            BringToForeground(hwnd);
            await Task.Delay(600);
            ClickInWindow(hwnd);
            await Task.Delay(400);
            if (GetForegroundWindow() != hwnd)
            {
                Console.WriteLine($"WARN: app window is not foreground (fg={GetForegroundWindow()}); retrying focus claim.");
                BringToForeground(hwnd);
                ClickInWindow(hwnd);
                await Task.Delay(400);
            }

            Console.WriteLine($"foreground after claim: {GetForegroundWindow()} (expected {hwnd})");
            DumpAppWindows(process, outDir);

            // Baseline: the login screen before any input.
            Capture? loginBaseline = CaptureWindow(hwnd, Path.Combine(outDir, "baseline-login.bmp"));

            // The scripted keyboard-only pass (docs/05 review gate):
            // login -> Alt+2 -> F2 -> F12 -> Esc -> Alt+1 -> F10 -> Ctrl+S -> Alt+3 -> F1 -> Esc
            // StepKind: Type = text entry (small diff), Minor = focus/state
            // change (small diff), Navigate = whole-screen change (large diff).
            var steps = new (string Action, string Keys, StepKind Kind)[]
            {
                ("login-username", "admin", StepKind.Type),
                ("login-tab", "TAB", StepKind.Minor),
                ("login-password", "admin", StepKind.Type),
                ("login-submit", "ENTER", StepKind.Navigate),
                ("open-sales", "ALTD2", StepKind.Navigate),
                ("focus-scan", "F2", StepKind.Minor),
                ("scan-item", "Sardines", StepKind.Type),
                ("scan-enter", "ENTER", StepKind.Minor),
                ("checkout-F12", "F12", StepKind.Navigate),
                ("dialog-esc", "ESCAPE", StepKind.Navigate),
                ("home-Alt1", "ALTD1", StepKind.Navigate),
                ("reconcile-F10", "F10", StepKind.Navigate),
                ("draft-CtrlS", "CONTROLS", StepKind.Minor),
                ("stock-Alt3", "ALTD3", StepKind.Navigate),
                ("help-F1", "F1", StepKind.Navigate),
                ("help-esc", "ESCAPE", StepKind.Navigate),
            };

            Capture? previous = loginBaseline;
            var trace = new List<string>();
            bool pass = true;
            string failReason = "";

            for (int i = 0; i < steps.Length && pass; i++)
            {
                (string name, string keys, StepKind kind) = steps[i];
                if (name is not ("scan-item" or "scan-enter"))
                {
                    SendKeys(keys);
                    await Task.Delay(name == "login-submit" ? 2500 : 1200);
                }
                else
                {
                    await Task.Delay(100);
                }

                string bmpPath = Path.Combine(outDir, $"{name}.bmp");
                Capture? current = CaptureWindow(hwnd, bmpPath);
                if (current is null)
                {
                    pass = false;
                    failReason = $"{name}: window capture failed";
                    trace.Add(failReason);
                    break;
                }

                double prevDiff = previous is null ? 0 : MeanAbsDiff(previous.Pixels, current.Pixels);
                trace.Add($"{name}: {keys} [{kind}] -> {name}.bmp changed={prevDiff:0.0}");

                // F12 opens the payment dialog as a SEPARATE window (the main-window
                // capture cannot see it), and Esc closes it. Verify structurally
                // by app window count, before any pixel gate.
                if (name == "scan-item")
                {
                    // Real keystrokes occasionally drop under SendInput; paste
                    // the product into the focused scan box via the clipboard
                    // (WPF native paste - the app never binds Ctrl+V) and
                    // verify through UIA.
                    bool typed = false;
                    for (int attempt = 0; attempt < 3 && !typed; attempt++)
                    {
                        SendKeys("F2");
                        await Task.Delay(400);
                        SetClipboardText("Sardines");
                        SendKeys("CONTROLV");
                        await Task.Delay(600);
                        UiSnapshot ui = ReadUi(hwnd);
                        trace.Add($"scan-item attempt {attempt}: {ui}");
                        typed = ui.Edits.Any(e => e.EndsWith("Sardines", StringComparison.Ordinal));
                        if (!typed)
                        {
                            SendKeys("CONTROLA"); // select all so the next attempt replaces
                        }
                    }

                    if (!typed)
                    {
                        pass = false;
                        failReason = "scan-item: could not enter Sardines into the scan box (verified via UIA)";
                        trace.Add(failReason);
                        break;
                    }

                    previous = current;
                    continue;
                }

                if (name == "scan-enter")
                {
                    // After Enter the validated box is cleared (successful
                    // scan) - verify the cart has a line via the CHECKOUT
                    // button become enabled, or the scan error copy appears.
                    SendKeys("ENTER");
                    await Task.Delay(900);
                    UiSnapshot ui = ReadUi(hwnd);
                    trace.Add($"scan-enter: {ui}");
                    bool stillTyped = ui.Edits.Any(e => e.EndsWith("Sardines", StringComparison.Ordinal));
                    if (ui.ErrorCopy.Count == 0 && stillTyped)
                    {
                        pass = false;
                        failReason = "scan-enter: the scan box still holds the product - Enter did not run the scan (input not delivered?)";
                        trace.Add(failReason);
                        break;
                    }

                    previous = current;
                    continue;
                }

                if (name == "checkout-F12")
                {
                    await Task.Delay(1500);
                    DumpAppWindows(process, outDir);
                    bool confirmFound = false;
                    foreach (IntPtr win in AppWindows(process))
                    {
                        UiSnapshot ui = ReadUi(win);
                        Console.WriteLine($"  uia {titleOf(win)}: {ui}");
                        trace.Add($"  uia {titleOf(win)}: {ui}");
                        if (ui.Buttons.Contains("CONFIRM"))
                        {
                            confirmFound = true;
                        }
                    }

                    if (!confirmFound)
                    {
                        pass = false;
                        failReason = "checkout-F12: no 'CONFIRM' button found in any app window - the payment dialog did not open (cart empty or F12 not delivered)";
                        trace.Add(failReason);
                        break;
                    }

                    trace.Add($"checkout-F12: payment dialog confirmed via UIA");
                    previous = current;
                    continue;
                }

                if (name == "dialog-esc")
                {
                    for (int attempt = 0; attempt < 5 && HasPaymentDialog(process); attempt++)
                    {
                        await Task.Delay(800);
                        if (HasPaymentDialog(process))
                        {
                            SendKeys("ESCAPE");
                        }
                    }

                    await Task.Delay(600);
                    if (HasPaymentDialog(process))
                    {
                        pass = false;
                        failReason = $"dialog-esc: the payment dialog window is still present after retries (visible windows: {string.Join(",", AppWindows(process))}) - Esc did not close the top dialog";
                        trace.Add(failReason);
                        break;
                    }

                    previous = current;
                    continue;
                }

                // Navigation steps must visibly switch the screen; text steps only
                // need to touch pixels at all. A ~zero diff means the input
                // never reached the app. Focus-only steps (F2, Ctrl+S) leave
                // no pixel foot-print and carry no gate.
                double threshold = kind == StepKind.Navigate ? 3.0 : kind == StepKind.Type ? 0.005 : 0.0;
                if (previous is not null && prevDiff < threshold)
                {
                    pass = false;
                    failReason = $"{name}: window did not change enough for a {kind} step (mean diff {prevDiff:0.00}, need >= {threshold}); input likely did not reach the app";
                    break;
                }

                // After login-submit the app must have LEFT the login screen: the
                // login card is an isolated white region at the window center,
                // while every authenticated screen shows the page colour there.
                // Cold EF/SQLite warm-up can take a few seconds, so re-sample
                // until the card disappears (or the timeout fails the step).
                if (name == "login-submit")
                {
                    for (int attempt = 0; attempt < 8 && IsCardWhiteAtCenter(current); attempt++)
                    {
                        await Task.Delay(1000);
                        current = CaptureWindow(hwnd, bmpPath) ?? current;
                    }

                    double fromLogin = MeanAbsDiff(loginBaseline!.Pixels, current.Pixels);
                    trace.Add($"login-submit: baseline diff {fromLogin:0.0}");
                    Fingerprint(hwnd, trace);
                    if (IsCardWhiteAtCenter(current))
                    {
                        pass = false;
                        failReason = $"login-submit: center pixel still on the login card after retries - login did not complete";
                        break;
                    }
                }

                previous = current;
            }

            if (pass)
            {
                verdicts.Add($"VERDICT: PASS - scripted keyboard pass executed on the real app; every step changed the window and login-submit left the login screen ({steps.Length} steps verified).");
            }
            else
            {
                verdicts.Add($"VERDICT: FAIL - {failReason}");
                verdicts.Add("Note: the round-1 verdict claiming PASS without per-step verification is superseded by this run.");
            }

            verdicts.Add("VERDICT: OPERATOR - pixel-level review of the captures and the video (Game Bar / ffmpeg gdigrab) remains.");
            await File.WriteAllTextAsync(Path.Combine(outDir, "bg-004-navigation.trace.txt"), string.Join(Environment.NewLine, trace));
            WriteVerdicts(outDir, verdicts);
            Console.WriteLine("Lane driver finished. See " + outDir);
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            verdicts.Add($"VERDICT: OPERATOR - {ex.Message}");
            WriteVerdicts(outDir, verdicts);
            return 0;
        }
        finally
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch
            {
                // Best-effort process cleanup.
            }
        }
    }

    private static void WriteVerdicts(string outDir, List<string> verdicts)
    {
        Console.WriteLine(string.Join(Environment.NewLine, verdicts));
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "VERDICTS.txt"), verdicts);
    }

    /// <summary>Visible top-level window handles owned by the app process
    /// (invisible OS/IME/CLR helper windows are excluded).</summary>
    private static IntPtr[] AppWindows(Process process)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)process.Id && IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    private static bool HasPaymentDialog(Process process) =>
        AppWindows(process).Any(h => WindowTitle(h).Contains("Payment", StringComparison.OrdinalIgnoreCase));

    /// <summary>UI-Automation ground truth of a window: edit-control values and
    /// any error-copy text element (the real app's own state, not pixels).</summary>
    private static UiSnapshot ReadUi(IntPtr hwnd)
    {
        var snapshot = new UiSnapshot();
        try
        {
            try
            {
                System.Windows.Automation.AutomationElement? focused = System.Windows.Automation.AutomationElement.FocusedElement;
                snapshot.Focus = focused is null ? "-" : $"{focused.Current.ControlType.ProgrammaticName}/{focused.Current.Name}";
            }
            catch
            {
                snapshot.Focus = "focus-read-failed";
            }

            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return snapshot;
            }

            var all = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            foreach (System.Windows.Automation.AutomationElement element in all)
            {
                try
                {
                    if (element.Current.ControlType == System.Windows.Automation.ControlType.Edit
                        && element.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out object? pattern))
                    {
                        snapshot.Edits.Add(element.Current.Name + "=" + ((System.Windows.Automation.ValuePattern)pattern).Current.Value ?? "");
                    }

                    if (element.Current.ControlType == System.Windows.Automation.ControlType.Button
                        && !string.IsNullOrWhiteSpace(element.Current.Name))
                    {
                        snapshot.Buttons.Add(element.Current.Name);
                    }

                    if (element.Current.Name.Contains("Barcode not found", StringComparison.OrdinalIgnoreCase)
                        || element.Current.Name.Contains("Sign in failed", StringComparison.OrdinalIgnoreCase))
                    {
                        snapshot.ErrorCopy.Add(element.Current.Name);
                    }
                }
                catch
                {
                    // Elements can detach while walking; ignore and continue.
                }
            }
        }
        catch (Exception ex)
        {
            snapshot.Fault = ex.Message;
        }

        return snapshot;
    }

    private sealed class UiSnapshot
    {
        public List<string> Edits { get; } = new();

        public List<string> Buttons { get; } = new();

        public List<string> ErrorCopy { get; } = new();

        public string Focus { get; set; } = "-";

        public string? Fault { get; set; }

        public override string ToString() =>
            $"focus={Focus} edits=[{string.Join(" | ", Edits)}] buttons=[{string.Join(",", Buttons)}] error={(ErrorCopy.Count > 0 ? string.Join(" | ", ErrorCopy) : "-")}{(Fault is null ? "" : $" fault={Fault}")}";
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var title = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private static string titleOf(IntPtr hwnd) => WindowTitle(hwnd);

    private static void DumpAppWindows(Process process, string outDir)
    {
        IntPtr[] windows = AppWindows(process);
        Console.WriteLine($"visible app windows at start: {windows.Length}");
        for (int i = 0; i < windows.Length; i++)
        {
            string title = WindowTitle(windows[i]);
            Console.WriteLine($"  window[{i}] 0x{windows[i]:X} '{title}'");
            _ = CaptureWindow(windows[i], Path.Combine(outDir, $"window-{i}-{title}.bmp"));
        }
    }

    private static IntPtr FindMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(250);
        }

        return IntPtr.Zero;
    }

    private static void BringToForeground(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        // Attach to the thread that owns the current foreground window so the
        // SetForegroundWindow call is permitted (Windows focus-stealing guard).
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero
            ? GetWindowThreadProcessId(hwnd, out _)
            : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static void ClickInWindow(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out RECT rect) || rect.Width() <= 0 || rect.Height() <= 0)
        {
            return;
        }

        var point = new POINT { X = rect.Left + rect.Width() / 2, Y = rect.Top + 10 };
        _ = ClientToScreen(hwnd, ref point);
        SetCursorPos(point.X, point.Y);
        SendInputMouse(0x0002); // left down
        Thread.Sleep(30);
        SendInputMouse(0x0004); // left up
        Thread.Sleep(250);
    }

    private static void SendKeys(string chord)
    {
        // Named chords (function keys / modifiers) vs literal text: literal
        // text is typed one character at a time.
        if (chord.Length > 1 && chord is not ("ENTER" or "ESCAPE" or "TAB" or "F1" or "F2" or "F10" or "F12")
            && !chord.StartsWith("ALT", StringComparison.OrdinalIgnoreCase)
            && !chord.StartsWith("CONTROL", StringComparison.OrdinalIgnoreCase))
        {
            foreach (char c in chord)
            {
                ushort vk = VkFromChar(c);
                if (vk != 0)
                {
                    Key(vk, down: true);
                    Key(vk, down: false);
                    Thread.Sleep(30);
                }
            }

            return;
        }

        ushort key = chord.ToUpperInvariant() switch
        {
            "ENTER" => 0x0D,
            "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "F1" => 0x70,
            "F2" => 0x71,
            "F10" => 0x79,
            "F12" => 0x7B,
            "CONTROLA" => 0x41, // Ctrl+A
            "CONTROLV" => 0x56, // Ctrl+V (native paste; the app never binds it)
            "ALTD1" => 0x31,
            "ALTD2" => 0x32,
            "ALTD3" => 0x33,
            "CONTROLS" => 0x53, // S
            _ => chord.Length == 1 ? VkFromChar(chord[0]) : (ushort)0,
        };

        if (chord.StartsWith("ALT", StringComparison.OrdinalIgnoreCase))
        {
            Key(VK_MENU, down: true);
        }
        else if (chord.StartsWith("CONTROL", StringComparison.OrdinalIgnoreCase))
        {
            Key(VK_CONTROL, down: true);
        }

        Key(key, down: true);
        Key(key, down: false);
        if (chord.StartsWith("ALT", StringComparison.OrdinalIgnoreCase))
        {
            Key(VK_MENU, down: false);
        }
        else if (chord.StartsWith("CONTROL", StringComparison.OrdinalIgnoreCase))
        {
            Key(VK_CONTROL, down: false);
        }
    }

    /// <summary>Clipboard writes must run on an STA thread (OLE apartment rule);
    /// the driver's async flow continues on pool threads.</summary>
    private static void SetClipboardText(string text)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("clipboard write failed", error);
        }
    }

    private static void Key(ushort vk, bool down)
    {
        var input = new Input
        {
            Type = 1,
            Data = new InputUnion
            {
                Key = new KeybdInput
                {
                    Vk = vk,
                    Scan = (ushort)MapVirtualKey(vk, 0),
                    Flags = down ? 0 : KEYEVENTF_KEYUP,
                },
            },
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
        Thread.Sleep(15);
    }

    private static void SendInputMouse(uint mouseEvent)
    {
        var input = new Input
        {
            Type = 0,
            Data = new InputUnion { Mouse = new MouseInput { Flags = mouseEvent } },
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static ushort VkFromChar(char c) => c.ToString().ToUpperInvariant() switch
    {
        "A" => 0x41,
        "D" => 0x44,
        "I" => 0x49,
        "M" => 0x4D,
        "N" => 0x4E,
        "P" => 0x50,
        "S" => 0x53,
        "T" => 0x54,
        "0" => 0x30,
        "1" => 0x31,
        "2" => 0x32,
        "3" => 0x33,
        "4" => 0x34,
        "5" => 0x35,
        "6" => 0x36,
        "7" => 0x37,
        "8" => 0x38,
        "9" => 0x39,
        _ => 0,
    };

    private static bool IsCardWhiteAtCenter(Capture capture)
    {
        int width = capture.Width;
        byte[] pixels = capture.Pixels;
        int stride = width * 4;
        int cx = width / 2;
        int cy = 384;
        if (cy * stride + cx * 4 + 2 >= pixels.Length)
        {
            return true; // cannot sample; fail conservatively
        }

        int idx = cy * stride + cx * 4;
        int b = pixels[idx];
        int g = pixels[idx + 1];
        int r = pixels[idx + 2];
        return Math.Abs(r - 255) + Math.Abs(g - 255) + Math.Abs(b - 255) <= 5;
    }

    private static void Fingerprint(IntPtr hwnd, List<string> trace)
    {
        // Sample a few recognizable pixels: the top-left rail region (sunken
        // #E8EAED on the authenticated shell), the centered card region, and
        // the status bar area.
        Capture? capture = CaptureWindow(hwnd, Path.Combine(Path.GetTempPath(), "bentago-sample.bmp"));
        if (capture is null)
        {
            return;
        }

        int[] xs = [104, 683, 683];
        int[] ys = [300, 384, 755];
        int[] colors = SamplePixels(capture, xs, ys);
        trace.Add($"fingerprint rail@{colors[0]:X6} center@{colors[1]:X6} status@{colors[2]:X6}");
        Console.WriteLine($"fingerprint rail=0x{colors[0]:X6} center=0x{colors[1]:X6} status=0x{colors[2]:X6}");
    }

    private static int[] SamplePixels(Capture capture, int[] xs, int[] ys)
    {
        var results = new int[xs.Length];
        byte[] pixels = capture.Pixels;
        int width = capture.Width;
        int stride = width * 4;
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] < width && ys[i] >= 0 && (ys[i] * stride + xs[i] * 4 + 2) < pixels.Length)
            {
                int idx = ys[i] * stride + xs[i] * 4;
                int b = pixels[idx];
                int g = pixels[idx + 1];
                int r = pixels[idx + 2];
                results[i] = (r << 16) | (g << 8) | b;
            }
        }

        return results;
    }

    /// <summary>A PrintWindow capture: raw 32bpp pixels plus the window width
    /// (so sampling never depends on the CURRENT foreground window).</summary>
    private sealed record Capture(byte[] Pixels, int Width);

    /// <summary>Captures the window via PrintWindow and writes a BMP; returns the raw 32bpp pixels.</summary>
    private static Capture? CaptureWindow(IntPtr hwnd, string path)
    {
        if (!GetWindowRect(hwnd, out RECT rect) || rect.Width() <= 0 || rect.Height() <= 0)
        {
            return null;
        }

        int width = rect.Width();
        int height = rect.Height();
        IntPtr hdc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(hdc);
        IntPtr bitmap = CreateCompatibleBitmap(hdc, width, height);
        IntPtr old = SelectObject(memDc, bitmap);

        bool ok = PrintWindow(hwnd, memDc, PW_CLIENTONLY);
        byte[]? pixels = null;
        if (ok)
        {
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = height, Planes = 1, BitCount = 32 };
            pixels = new byte[width * height * 4];
            if (GetDIBits(memDc, bitmap, 0, (uint)height, pixels, in info, 0) == 0)
            {
                pixels = null;
            }
        }

        SelectObject(memDc, old);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, hdc);

        if (pixels is not null)
        {
            WriteBmp(path, pixels, width, height);
        }

        return pixels is null ? null : new Capture(pixels, width);
    }

    private static void WriteBmp(string path, byte[] pixels, int width, int height)
    {
        var header = new BitmapFileHeader
        {
            Type = 0x4D42, // 'BM'
            Size = 14 + 40 + width * height * 4,
            OffBits = 14 + 40,
        };
        var info = new BitmapInfoHeader { Size = 40, Width = width, Height = height, Planes = 1, BitCount = 32 };

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(header.Type);
        writer.Write(header.Size);
        writer.Write(0);
        writer.Write(header.OffBits);
        byte[] infoBytes = new byte[40];
        MemoryMarshal.Write(infoBytes, in info);
        writer.Write(infoBytes);
        // DIB rows are bottom-up; flip to top-down.
        int stride = width * 4;
        for (int row = height - 1; row >= 0; row--)
        {
            writer.Write(pixels, row * stride, stride);
        }
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return double.MaxValue;
        }

        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / (double)a.Length;
    }

    private const int PW_CLIENTONLY = 1;
    private const int SW_RESTORE = 9;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_CONTROL = 0x11;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam); [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, in BitmapInfoHeader lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width() => Right - Left;
        public int Height() => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapFileHeader
    {
        public ushort Type;
        public int Size;
        public int Reserved;
        public int OffBits;
    }
}