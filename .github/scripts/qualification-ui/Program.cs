using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

internal static class Program
{
    private const string Seed = "Clipthrough hosted upgrade qualification synthetic clip ";
    private static readonly Dictionary<string, object> Evidence = new();
    private static readonly Dictionary<string, Gate> Gates = new()
    {
        ["hosted_input_desktop"] = new(),
        ["normal_ui_storage_and_record_visibility"] = new(),
        ["hotkey_summon_dismiss"] = new(),
        ["plain_paste_target_content"] = new(),
        ["rich_paste_target_content"] = new(),
        ["png_full_detail_paste"] = new()
    };
    private static string output = "";
    private static Process candidate = null!;
    private static nint main;
    private static Receiver receiver = null!;
    private static bool ownsCandidate;

    [STAThread]
    private static int Main(string[] args)
    {
        // No handles, UI, input, clipboard, app launch, or root reads before this guard.
        try { Native.Guard(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        try
        {
            if (args.Length != 4) throw new ArgumentException("Usage: HostedUiHarness <--launch|candidate-pid> <installed-current-Clipthrough.exe> <seeded-data-root> <evidence-directory>");
            var root = Path.GetFullPath(args[2]);
            var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? throw new InvalidOperationException("GITHUB_WORKSPACE missing"));
            if (!root.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Synthetic data root must be a child of GITHUB_WORKSPACE.");
            if (File.ReadAllText(Path.Combine(root, ".hosted-synthetic-root")).Trim() != "Clipthrough hosted synthetic qualification root")
                throw new InvalidOperationException("Missing synthetic-root opt-in sentinel; refusing to inspect app or clipboard.");
            var requestedOutput = Path.GetFullPath(args[3]);
            if (!requestedOutput.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Evidence must be a child of GITHUB_WORKSPACE.");
            output = requestedOutput;
            Directory.CreateDirectory(output);
            var executable = Path.GetFullPath(args[1]);
            Require(Path.GetFileName(executable) == "Clipthrough.exe" && Path.GetFileName(Path.GetDirectoryName(executable)) == "current", "Candidate must be the real installed current\\Clipthrough.exe.");
            Evidence["limitations"] = "Hosted synthetic smoke only; no physical GPU, IME, monitor, display-setting or DPI-transition claim. Accessibility unavailable is blocked, never passed.";
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            receiver = new Receiver();
            receiver.Show();
            // Bound synchronous UIA COM calls as well as our polling loops. The parent must
            // additionally impose an external process timeout (see README).
            using var watchdog = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("Hosted UI harness exceeded its 240-second wall-clock budget.");
                try
                {
                    File.WriteAllText(Path.Combine(output, "watchdog-timeout.txt"), "Incomplete: 240-second wall-clock budget exhausted.", new UTF8Encoding(false));
                    if (ownsCandidate && candidate != null && !candidate.HasExited) candidate.Kill();
                }
                catch (Exception ex) { Console.Error.WriteLine($"Watchdog cleanup: {ex.Message}"); }
                Environment.Exit(3);
            }, null, TimeSpan.FromSeconds(240), Timeout.InfiniteTimeSpan);
            if (!Run("hosted_input_desktop", ProbeInput)) return Finish();
            if (args[0] == "--launch")
            {
                Native.Guard();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add("--data-dir");
                start.ArgumentList.Add(root);
                candidate = Process.Start(start) ?? throw new InvalidOperationException("Candidate launch returned no process.");
                ownsCandidate = true;
            }
            else candidate = Process.GetProcessById(int.Parse(args[0], CultureInfo.InvariantCulture));
            Wait(() => !candidate.HasExited && candidate.MainModule != null, "Installed candidate process initialization");
            Require(string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase), "PID is not the specified installed candidate.");
            var logPath = Path.Combine(root, "logs", "clipthrough.log");
            Wait(() => File.Exists(logPath) && ReadShared(logPath).Contains($"Data folder: {root} (CommandLine).", StringComparison.OrdinalIgnoreCase),
                "App resolves the throwaway command-line root");
            Evidence["candidate"] = new { pid = candidate.Id, executable, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))), root, launchedByHarness = ownsCandidate };
            SaveEvidence();
            var app = FindAppWindow();
            main = (nint)app.Current.NativeWindowHandle;
            Evidence["window"] = new { handle = main.ToInt64(), name = app.Current.Name };
            var storagePassed = Run("normal_ui_storage_and_record_visibility", () => CheckStorageAndRows(root));
            var hotkeysPassed = Run("hotkey_summon_dismiss", CheckHotkeys);
            if (storagePassed && hotkeysPassed)
            {
                Run("plain_paste_target_content", CheckPlainPaste);
                Run("rich_paste_target_content", CheckRichPaste);
                Run("png_full_detail_paste", CheckImagePaste);
            }
            else
            {
                foreach (var name in new[] { "plain_paste_target_content", "rich_paste_target_content", "png_full_detail_paste" })
                    Gates[name].Detail = "Dependency unavailable: normal app UI/storage or real hotkey summon/dismiss was not established.";
            }
        }
        catch (Exception ex)
        {
            Evidence["fatal"] = ex.ToString();
            Console.Error.WriteLine(ex);
        }
        finally
        {
            try
            {
                if (ownsCandidate && candidate != null && !candidate.HasExited)
                {
                    candidate.CloseMainWindow();
                    if (!candidate.WaitForExit(2000)) candidate.Kill();
                    candidate.WaitForExit(5000);
                    Evidence["cleanup"] = new { stoppedOwnedCandidatePid = candidate.Id, exited = candidate.HasExited };
                }
            }
            catch (Exception ex) { Evidence["fatalCleanup"] = ex.ToString(); }
            candidate?.Dispose();
            receiver?.ImageTarget.Image?.Dispose();
            receiver?.Dispose();
        }
        return Finish();
    }

    private static int Finish()
    {
        if (output.Length == 0) return 2;
        Evidence["gates"] = Gates;
        Evidence["finishedUtc"] = DateTime.UtcNow;
        SaveEvidence();
        return Evidence.ContainsKey("fatal") || Evidence.ContainsKey("fatalCleanup") || Gates.Values.Any(g => g.Status == "failed") ? 1 : Gates.Values.Any(g => g.Status != "passed") ? 3 : 0;
    }

    private static void SaveEvidence()
    {
        Evidence["gates"] = Gates;
        File.WriteAllText(Path.Combine(output, "ui-evidence.json"), JsonSerializer.Serialize(Evidence,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), new UTF8Encoding(false));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool Run(string name, Action action)
    {
        try
        {
            action();
            Gates[name] = new Gate { Status = "passed", Detail = "Observed through real hosted input and installed candidate UI; see scenario artifacts." };
            SaveEvidence();
            return true;
        }
        catch (Unavailable ex)
        {
            Gates[name] = new Gate { Status = "blocked", Detail = ex.Message };
        }
        catch (Exception ex)
        {
            Gates[name] = new Gate { Status = "failed", Detail = ex.ToString() };
        }
        SaveEvidence();
        Console.Error.WriteLine($"{name}: {Gates[name].Status}: {Gates[name].Detail}");
        return false;
    }

    private static void ProbeInput()
    {
        var desktop = Native.OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) throw new Unavailable("OpenInputDesktop failed; no usable input desktop established.");
        Native.Check(Native.CloseDesktop(desktop), "CloseDesktop");
        FocusReceiver();
        var token = "input-" + Guid.NewGuid().ToString("N");
        Native.Type(token);
        Wait(() => receiver.Rich.Text == token, "Actual foreground SendInput Unicode delivery to the receiver", 8);
        Evidence["input"] = new { foreground = Native.GetForegroundWindow().ToInt64(), received = receiver.Rich.Text, expected = token };
        receiver.Rich.Clear();
        Screenshot("input-desktop");
    }

    private static AutomationElement FindAppWindow()
    {
        AutomationElement? found = null;
        try
        {
            TopLevelInventory("startup-before-summon");
            // A hidden/cloaked popup may not be in UIA's top-level tree at all.
            // From a verified external foreground, Alt+V summons rather than
            // toggling an already-active popup off. Never use ShowWindow here.
            FocusReceiver();
            Pump(500);
            Native.Chord(0x12, 0x56);
            Wait(() =>
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, candidate.Id),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                    .Cast<AutomationElement>();
                found = windows.FirstOrDefault(window =>
                {
                    var handle = (nint)window.Current.NativeWindowHandle;
                    if (handle == 0 || !Native.Presented(handle)) return false;
                    var welcome = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Create library"));
                    if (welcome != null && !welcome.Current.IsOffscreen)
                    {
                        Native.SetForegroundWindow(handle);
                        return Native.GetForegroundWindow() == handle;
                    }
                    if (Native.GetForegroundWindow() != handle) return false;
                    // Identify the actual main workspace/first-run form by its
                    // controls, not a title that includes the release version.
                    return window.FindFirst(TreeScope.Descendants,
                        new OrCondition(
                            new PropertyCondition(AutomationElement.NameProperty, "Search clips"),
                            new PropertyCondition(AutomationElement.NameProperty, "Create library"))) != null;
                });
                return found != null;
            }, "Alt+V foregrounds accessible candidate workspace or first-run library form", 20);
            TopLevelInventory("startup-after-summon");
            Screenshot("startup-summoned");
        }
        catch (Exception)
        {
            try { TopLevelInventory("startup-discovery-failed"); }
            catch (Exception inventoryError) { Evidence["startupInventoryError"] = inventoryError.ToString(); }
            try { Screenshot("startup-discovery-failed"); }
            catch (Exception screenshotError) { Evidence["startupScreenshotError"] = screenshotError.ToString(); }
            SaveEvidence();
            throw;
        }
        return found!;
    }

    private static void TopLevelInventory(string label)
    {
        var records = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>().Take(200).Select(element =>
            {
                var handle = (nint)element.Current.NativeWindowHandle;
                return new
                {
                    pid = element.Current.ProcessId,
                    candidateOwned = element.Current.ProcessId == candidate.Id,
                    name = element.Current.Name,
                    type = element.Current.ControlType.ProgrammaticName,
                    handle = handle.ToInt64(),
                    foreground = handle != 0 && handle == Native.GetForegroundWindow(),
                    offscreen = element.Current.IsOffscreen,
                    bounds = element.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture)
                };
            }).ToArray();
        File.WriteAllText(Path.Combine(output, label + "-top-level-uia.json"),
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static AutomationElement App() => AutomationElement.FromHandle(main);

    private static AutomationElement Named(AutomationElement parent, string name)
        => parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            ?? throw new Unavailable($"UI Automation does not expose '{name}'.");

    private static IEnumerable<AutomationElement> Descendants(AutomationElement parent)
        => parent.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>();

    private static string Text(AutomationElement element)
    {
        var text = element.Current.Name;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) text += "\n" + ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var document)) text += "\n" + ((TextPattern)document).DocumentRange.GetText(4096);
        return text;
    }

    private static void TypeInto(AutomationElement edit, string text)
    {
        edit.SetFocus();
        Native.Chord(0x11, 0x41);
        Native.Type(text);
        Wait(() => edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && ((ValuePattern)pattern).Current.Value == text,
            "Text input reflected through UI Automation", 8);
    }

    private static void CheckStorageAndRows(string root)
    {
        Summon();
        Wait(() => Descendants(App()).Any(e => !e.Current.IsOffscreen && e.Current.Name is "Create library" or "Search clips"),
            "Normal startup workspace or first-run library form");
        var create = App().FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Create library"));
        if (create != null && !create.Current.IsOffscreen)
        {
            var expectedPath = Path.Combine(root, "clipthrough.db");
            Require(Descendants(App()).Any(e => e.Current.ControlType == ControlType.Edit
                && e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && string.Equals(((ValuePattern)value).Current.Value, expectedPath, StringComparison.OrdinalIgnoreCase)),
                "Welcome UI is not configured for the seeded synthetic library.");
            Dump(App(), "welcome-synthetic-library");
            Screenshot("welcome-synthetic-library");
            if (!create.TryGetCurrentPattern(InvokePattern.Pattern, out var createInvoke))
                throw new Unavailable("Welcome Create library is not accessible.");
            ((InvokePattern)createInvoke).Invoke();
            Wait(() => Descendants(App()).Any(e => e.Current.Name == "Search clips" && !e.Current.IsOffscreen),
                "Welcome completes through app storage setup", 25);
            Evidence["welcome"] = "Committed the app's displayed default synthetic library path using Create library; no draft values changed.";
        }
        foreach (var suffix in new[] { "alpha", "beta" })
        {
            Search(Seed + suffix);
            var row = FindRow(Seed + suffix);
            Require(!row.Current.IsOffscreen && !row.Current.BoundingRectangle.IsEmpty, "Synthetic record exists but is not visible.");
            Screenshot("seed-" + suffix);
            Dump(App(), "seed-" + suffix);
        }
        var settingsButton = Named(App(), "Settings");
        if (!settingsButton.TryGetCurrentPattern(InvokePattern.Pattern, out var openSettings))
            throw new Unavailable("The actual Settings button is not invokable.");
        ((InvokePattern)openSettings).Invoke();
        AutomationElement? settings = null;
        Wait(() =>
        {
            settings = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, candidate.Id),
                    new PropertyCondition(AutomationElement.NameProperty, "Settings")));
            return settings != null;
        }, "Settings window accessibility");
        var filter = Descendants(settings!).FirstOrDefault(e => e.Current.ControlType == ControlType.Edit && !e.Current.IsOffscreen)
            ?? throw new Unavailable("Settings filter has no accessible edit control.");
        TypeInto(filter, "database");
        var heading = Named(settings!, "Database storage");
        var expander = heading;
        while (!expander.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _) && expander != settings)
            expander = TreeWalker.ControlViewWalker.GetParent(expander) ?? throw new Unavailable("Database expander unavailable.");
        if (expander.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            ((ExpandCollapsePattern)expand).Expand();
        var expected = Path.Combine(root, "clipthrough.db");
        Wait(() => Descendants(settings!).Any(e => e.Current.ControlType == ControlType.Edit && !e.Current.IsOffscreen
            && e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
            && string.Equals(((ValuePattern)value).Current.Value, expected, StringComparison.OrdinalIgnoreCase)),
            "App Settings UI exposes the actual synthetic database path");
        Require(File.Exists(expected), "The app-configured database file does not exist.");
        Evidence["storage"] = new { uiDatabasePath = expected, fileLength = new FileInfo(expected).Length, syntheticRows = new[] { Seed + "alpha", Seed + "beta" } };
        Screenshot("settings-database");
        Dump(settings!, "settings-database");
        var cancel = Named(settings!, "Cancel");
        if (!cancel.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new Unavailable("Settings Cancel is not invokable.");
        ((InvokePattern)invoke).Invoke(); // Do not save or change product settings.
        Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, candidate.Id),
                new PropertyCondition(AutomationElement.NameProperty, "Settings"))) == null, "Settings cancellation", 8);
    }

    private static void CheckHotkeys()
    {
        Summon();
        Native.Chord(0x12, 0x56); // Alt+V default global toggle, not ShowWindow.
        Wait(() => !Native.Presented(main), "Alt+V actually dismisses the popup (DWM cloak or hidden)");
        Screenshot("hotkey-dismiss");
        FocusReceiver();
        Native.Chord(0x12, 0x56);
        Wait(() => Native.Presented(main) && Native.GetForegroundWindow() == main, "Alt+V summons and foregrounds the actual popup");
        Screenshot("hotkey-summon");
        Evidence["hotkey"] = new { gesture = "Alt+V", dismissed = true, summoned = true, foregroundVerified = true };
        Native.Chord(0x12, 0x56);
        Wait(() => !Native.Presented(main), "Second Alt+V dismiss");
    }

    private static void CheckPlainPaste()
    {
        var marker = "hosted-plain-" + Guid.NewGuid().ToString("N");
        FocusReceiver();
        Native.Publish(receiver.Handle, (13, Native.Text(marker)));
        Pump(600);
        Summon();
        Search(marker);
        Select(FindRow(marker));
        Screenshot("plain-selected");
        PoisonClipboardAndReselect(marker, marker);
        Native.Chord(0x0D);
        Wait(() => receiver.Rich.Text == marker, "Installed app Enter paste into real text receiver");
        Require(!Native.Presented(main), "App paste did not dismiss popup.");
        Evidence["plainPaste"] = new { expected = marker, received = receiver.Rich.Text };
        Screenshot("plain-received");
    }

    private static void CheckRichPaste()
    {
        var marker = "hosted-rich-" + Guid.NewGuid().ToString("N");
        FocusReceiver();
        var rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red210\green30\blue60;}\f0\fs28\b\cf1 " + marker + @"\b0\cf0  normal}";
        Native.Publish(receiver.Handle, (13, Native.Text(marker + " normal")), (Native.Format("Rich Text Format"), Encoding.ASCII.GetBytes(rtf + '\0')));
        Pump(600);
        Summon();
        Search(marker);
        Select(FindRow(marker));
        Dump(App(), "rich-selected");
        Screenshot("rich-selected");
        PoisonClipboardAndReselect(marker, marker);
        Native.Chord(0x0D);
        Wait(() => receiver.Rich.Text.Contains(marker, StringComparison.Ordinal), "App pastes captured RTF into native RichEdit renderer");
        Require(receiver.Rich.Text == marker + " normal", "Rich paste text differs from fixture.");
        receiver.Rich.Select(0, marker.Length);
        Require(receiver.Rich.SelectionFont?.Bold == true, "Rich target rendered plain text: expected bold marker.");
        Require(receiver.Rich.SelectionColor.ToArgb() == Color.FromArgb(210, 30, 60).ToArgb(), "Rich target lost marker foreground color.");
        receiver.Rich.Select(marker.Length + 1, 6);
        Require(receiver.Rich.SelectionFont?.Bold == false, "Rich formatting leaked into normal text.");
        File.WriteAllText(Path.Combine(output, "received.rtf"), receiver.Rich.Rtf, new UTF8Encoding(false));
        Evidence["richPaste"] = new { format = "RTF", receivedText = receiver.Rich.Text, boldMarker = true, rgb = new[] { 210, 30, 60 }, normalSuffixNotBold = true };
        receiver.Rich.Select(0, 0);
        Screenshot("rich-rendered-target");
    }

    private static void CheckImagePaste()
    {
        using var original = new Bitmap(321, 197, PixelFormat.Format32bppArgb);
        for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
                original.SetPixel(x, y, Color.FromArgb(255, x % 256, y % 256, (x * 7 + y * 11) % 256));
        using var stream = new MemoryStream();
        original.Save(stream, ImageFormat.Png);
        FocusReceiver(image: true);
        Native.Publish(receiver.Handle, (Native.Format("PNG"), stream.ToArray()));
        Pump(600);
        Summon();
        Search("");
        var row = FindRow("321", "197");
        Select(row);
        Wait(() => Descendants(App()).Any(e => e.Current.ControlType == ControlType.Text && !e.Current.IsOffscreen
            && Text(e).Contains("321", StringComparison.Ordinal) && Text(e).Contains("197", StringComparison.Ordinal)
            && !InsideHistory(e)),
            "Image full dimensions shown in the actual app");
        Dump(App(), "image-detail");
        Screenshot("image-detail");
        PoisonClipboardAndReselect("", "321", "197");
        Native.Chord(0x0D);
        Wait(() => receiver.ImageTarget.Image != null || receiver.PasteError != null, "App image paste reaches PNG receiver");
        if (receiver.PasteError != null) throw new InvalidOperationException("PNG paste receiver failed.", receiver.PasteError);
        using var received = new Bitmap(receiver.ImageTarget.Image!);
        Require(received.Width == original.Width && received.Height == original.Height, "App pasted a thumbnail instead of the full image.");
        for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
                Require(received.GetPixel(x, y) == original.GetPixel(x, y), $"Full-detail pixel changed at ({x},{y}).");
        received.Save(Path.Combine(output, "received-full-detail.png"), ImageFormat.Png);
        Evidence["imagePaste"] = new { width = received.Width, height = received.Height, comparedPixels = original.Width * original.Height, allPixelsEqual = true, pngBytesReceived = receiver.PngBytes };
        Screenshot("image-rendered-target");
    }

    private static void PoisonClipboardAndReselect(string query, params string[] rowParts)
    {
        var poison = "hosted-poison-" + Guid.NewGuid().ToString("N");
        Native.Publish(receiver.Handle, (13, Native.Text(poison)));
        Require(Encoding.Unicode.GetString(Native.Read(13)).TrimEnd('\0') == poison,
            "Clipboard poison was not independently read back.");
        Require(!Native.Available(Native.Format("Rich Text Format")) && !Native.Available(Native.Format("PNG")),
            "Clipboard poison retained rich/image fixture formats.");
        Search(poison);
        FindRow(poison);
        Search(query);
        var originalRow = FindRow(rowParts);
        Select(originalRow);
        if (!originalRow.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            throw new Unavailable("Cannot verify selected fixture after poison capture.");
        Require(((SelectionItemPattern)pattern).Current.IsSelected,
            "Poison capture displaced the selected fixture.");
        Require(Encoding.Unicode.GetString(Native.Read(13)).TrimEnd('\0') == poison,
            "Fixture selection changed the clipboard before the actual Enter paste.");
        Evidence["clipboardPoison"] = new { text = poison, verified = true };
    }

    private static void FocusReceiver(bool image = false)
    {
        receiver.Rich.Clear();
        receiver.ImageTarget.Image?.Dispose();
        receiver.ImageTarget.Image = null;
        receiver.ImageMode = image;
        receiver.PasteError = null;
        receiver.Activate();
        Native.SetForegroundWindow(receiver.Handle);
        Wait(() => Native.GetForegroundWindow() == receiver.Handle, "Receiver foreground/input desktop", 8);
        if (image) receiver.Focus(); else receiver.Rich.Focus();
    }

    private static void Summon()
    {
        if (Native.Presented(main) && Native.GetForegroundWindow() == main) return;
        Native.Chord(0x12, 0x56);
        Wait(() => Native.Presented(main) && Native.GetForegroundWindow() == main, "Global Alt+V summons candidate");
    }

    private static void Search(string query) => TypeInto(Named(App(), "Search clips"), query);

    private static AutomationElement FindRow(params string[] parts)
    {
        AutomationElement? found = null;
        Wait(() =>
        {
            var list = Named(App(), "Clipboard history");
            found = Descendants(list).FirstOrDefault(e => e.Current.ControlType == ControlType.ListItem
                && !e.Current.IsOffscreen && parts.All(part => (Text(e) + "\n" + string.Join("\n", Descendants(e).Select(Text))).Contains(part, StringComparison.Ordinal)));
            return found != null;
        }, "Visible synthetic row: " + string.Join(" / ", parts));
        return found!;
    }

    private static void Select(AutomationElement row)
    {
        if (row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        else
        {
            var bounds = row.Current.BoundingRectangle;
            if (bounds.IsEmpty || row.Current.IsOffscreen) throw new Unavailable("Synthetic row cannot be selected visibly.");
            Native.Click((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        }
        row.SetFocus();
        Pump(400);
    }

    private static void Dump(AutomationElement root, string label)
    {
        var records = Descendants(root).Take(1500).Select(e => new
        {
            name = e.Current.Name, type = e.Current.ControlType.ProgrammaticName,
            offscreen = e.Current.IsOffscreen, bounds = e.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture), text = Text(e)
        }).ToArray();
        File.WriteAllText(Path.Combine(output, label + "-uia.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static bool InsideHistory(AutomationElement element)
    {
        var current = TreeWalker.ControlViewWalker.GetParent(element);
        for (var i = 0; current != null && i < 30; i++)
        {
            if (current.Current.Name == "Clipboard history") return true;
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        return false;
    }

    private static void Screenshot(string label)
    {
        var bounds = SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(Path.Combine(output, label + ".png"), ImageFormat.Png);
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(20); }
    }

    private static void Wait(Func<bool> predicate, string description, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (predicate()) return;
            Pump(60);
        }
        throw new Unavailable($"Timed out ({seconds}s): {description}. No passing claim.");
    }

    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private sealed class Unavailable(string message) : Exception(message);
    private sealed class Gate { public string Status { get; set; } = "blocked"; public string Detail { get; set; } = "Not reached."; }
}

internal sealed class Receiver : Form
{
    internal RichTextBox Rich { get; } = new() { Dock = DockStyle.Top, Height = 180, Font = new Font("Arial", 14), DetectUrls = false };
    internal PictureBox ImageTarget { get; } = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
    internal bool ImageMode;
    internal Exception? PasteError;
    internal int PngBytes { get; private set; }

    internal Receiver()
    {
        Text = "Clipthrough hosted synthetic paste target";
        Width = 720;
        Height = 520;
        Controls.Add(ImageTarget);
        Controls.Add(Rich);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ImageMode && keyData == (Keys.Control | Keys.V))
        {
            try
            {
                var bytes = Native.Read(Native.Format("PNG"));
                PngBytes = bytes.Length;
                using var stream = new MemoryStream(bytes);
                using var decoded = System.Drawing.Image.FromStream(stream);
                ImageTarget.Image?.Dispose();
                ImageTarget.Image = new Bitmap(decoded);
                ImageTarget.Refresh(); // Real rendered target, not clipboard-format inspection alone.
            }
            catch (Exception ex) { PasteError = ex; }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData); // Native RichEdit handles normal Ctrl+V itself.
    }
}
