using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using QuizWeb;
using QuizWeb.Data;
using QuizWeb.Services;

string dataDir = Directory.GetCurrentDirectory();

// ---------------- CLI tooling (same commands as the WinForms app) ----------------
if (args.Length > 0)
{
    switch (args[0])
    {
        case "--import":
            return Cli.RunImport(args);
        case "--export-xlsx":
            return Cli.RunExport(args);
        case "--check":
            return Cli.RunCheck(args);
        case "--help":
        case "-h":
            Console.WriteLine("Usage:");
            Console.WriteLine("  QuizWeb [--urls http://0.0.0.0:5000]        Run the quiz web app on the LAN");
            Console.WriteLine("  QuizWeb --import <file.csv> [--out <file.txt>]   Convert a CSV into questions.txt");
            Console.WriteLine("  QuizWeb --export-xlsx <file.txt> [--out <file.xlsx>]   Convert a questions.txt into questions.xlsx");
            Console.WriteLine("  QuizWeb --check <file.xlsx>   Validate a workbook and report issues");
            Console.WriteLine("Data files (config.txt lives next to the exe; when quizFolder is set,");
            Console.WriteLine("  questions.xlsx/.txt, images/ and Result.txt live inside that subfolder).");
            return 0;
    }
}

var config = QuizConfig.Load(dataDir);
// If quizFolder is set (e.g. Math, Radhika_Nepali), all quiz data
// (questions.xlsx/.txt, Result.txt, images/) lives inside that subfolder.
// This keeps each teacher/subject isolated with no code change per quiz.
string baseDir = dataDir;
if (!string.IsNullOrWhiteSpace(config.QuizFolder))
{
    dataDir = Path.Combine(baseDir, config.QuizFolder);
    try { Directory.CreateDirectory(dataDir); } catch { /* best effort */ }
}
var bank = QuestionBank.Load(dataDir);
var quizInfo = bank.Info;
int port = config.Port ?? 5000;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
// Bind to 0.0.0.0 so students on the LAN can reach us. An explicit
// --urls argument (or ASPNETCORE_URLS) always wins for flexibility.
if (!args.Contains("--urls") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton(new ResultStore(config.ResultPath(dataDir)));
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
builder.Services.AddSingleton(new ActiveSessions());
var app = builder.Build();

// Always deliver fresh files to browsers. Without this, a student computer
// can keep a stale copy of index.html while the app has been updated, which
// breaks the page (missing elements, old markup). Cheap on a LAN quiz.
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store";
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();

string imagesDir = Path.Combine(dataDir, "images");
if (Directory.Exists(imagesDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(imagesDir),
        RequestPath = "/images"
    });
}

app.MapGet("/api/config", () =>
{
    var q = QuestionBank.Load(dataDir);
    int totalQuestions = q.Found ? q.Questions.Count : bank.Questions.Count;
    return Results.Ok(new
    {
        timeMinutes = config.TimeMinutes,
        negativeMarkingPct = config.NegativeMarkingPct,
        totalQuestions,
        allowResultViewing = config.AllowResultViewing,
        allowImport = config.AllowImport,
        allowReview = config.AllowReview,
        allowAnswerDetails = config.AllowAnswerDetails,
        subject = quizInfo.Subject,
        className = quizInfo.Class,
        examType = quizInfo.ExamType,
        resultFile = config.ResultFile
    });
});

app.MapGet("/api/questions", () =>
{
    var q = QuestionBank.Load(dataDir);
    if (!q.Found || q.Questions.Count == 0)
        return Results.Problem(q.Error ?? "No questions found.", statusCode: 404);
    return Results.Ok(new
    {
        quizInfo = new { q.Info.Subject, q.Info.Class, q.Info.ExamType },
        source = q.Source,
        warnings = q.Warnings,
        questions = q.Questions.Select(x => new
        {
            x.Number,
            x.Text,
            x.Options,
            x.IsMultiCorrect,
            image = NormalizeImagePath(x.ImagePath),
            x.Passage,
            x.GroupId
        })
    });
});

// Retake guard: called when a student clicks Start.
app.MapPost("/api/check", (CheckRequest req) =>
{
    var store = app.Services.GetRequiredService<ResultStore>();
    var sessions = app.Services.GetRequiredService<ActiveSessions>();
    var now = DateTime.UtcNow;
    var prev = QuizEngine.FindPreviousMarks(config.ResultPath(dataDir), store,
        req.Name, req.Class, req.Section);

    if (prev.HasValue)
        return Results.Ok(new { alreadyTaken = true, previousMarks = prev.Value, activeSession = false });

    if (sessions.IsActive(req.Name, req.Class, req.Section, now))
        return Results.Ok(new { alreadyTaken = true, previousMarks = 0, activeSession = true });

    sessions.Register(req.Name, req.Class, req.Section, now.AddMinutes(config.TimeMinutes + 5));
    return Results.Ok(new { alreadyTaken = false, previousMarks = 0, activeSession = false });
});

app.MapPost("/api/submit", async (SubmitRequest req, HttpContext httpCtx) =>
    {
        // Extract client IP from the connection
        var ipAddress = httpCtx.Connection.RemoteIpAddress;
        if (ipAddress != null)
        {
            // Handle both IPv4 and IPv6 (prefer IPv4 if mapped)
            string ipStr = ipAddress.ToString();
            // If IPv6 mapped IPv4, extract the IPv4 part
            if (ipStr.StartsWith("::ffff:"))
                ipStr = ipStr.Substring(7);
            req.IPAddress = ipStr;
        }

        // Try to get computer name from the connection (if available on intranet)
        // We'll leave ComputerName empty if not available - it's best-effort

        var store = app.Services.GetRequiredService<ResultStore>();
        var sessions = app.Services.GetRequiredService<ActiveSessions>();
        var gate = app.Services.GetRequiredService<SemaphoreSlim>();
        var questions = QuestionBank.Load(dataDir).Questions;

        await gate.WaitAsync();
        try
        {
            var result = QuizEngine.Submit(req, questions, quizInfo, config, dataDir, store);
            sessions.Remove(req.Name, req.Class, req.Section);
            if (result.AlreadyTaken || !config.AllowReview)
                return Results.Ok(result);
            var review = questions.Select(q => new
            {
                number = q.Number,
                correctIndices = q.CorrectIndices
            }).ToList();
            return Results.Ok(new
            {
                result.AlreadyTaken,
                result.PreviousMarks,
                result.Marks,
                result.Correct,
                result.Wrong,
                result.Attempted,
                result.Total,
                result.Saved,
                result.SavePending,
                review
            });
        }
        finally
        {
            gate.Release();
        }
    });

app.MapGet("/api/results/students", () =>
{
    if (!config.AllowResultViewing)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var rows = ResultFile.LoadResults(config.ResultPath(dataDir));
    return Results.Ok(rows
        .OrderByDescending(r => r.Marks)
        .Select(r => new
        {
            r.Name, r.Class, r.Section, r.Subject, r.ExamType, r.QuizClass,
            r.Marks, r.Date, r.ComputerName, r.IPAddress
        }));
});

app.MapGet("/api/results/detail", (string name, string className, string section) =>
{
    if (!config.AllowResultViewing)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!config.AllowAnswerDetails)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var rows = ResultFile.LoadResults(config.ResultPath(dataDir));
    var match = rows.FirstOrDefault(r =>
        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.Class, className, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase));
    if (match == null) return Results.NotFound();
    var bankQuestions = QuestionBank.Load(dataDir).Questions;
    var correctMap = bankQuestions.ToDictionary(q => q.Number, q => q.CorrectIndices);
    return Results.Ok(new
    {
        match.Name, match.Class, match.Section, match.Subject, match.ExamType,
        match.QuizClass, match.Marks, match.Date,
        match.ComputerName, match.IPAddress,
        answers = match.Answers,
        correctMap
    });
});

app.MapPost("/api/import", async (HttpContext ctx) =>
{
    if (!config.AllowImport)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("questions");
    if (file == null || file.Length == 0)
        return Results.BadRequest("No CSV file uploaded.");

    string tmp = Path.Combine(Path.GetTempPath(), "quizweb_" + Guid.NewGuid() + ".csv");
    try
    {
        await using (var fs = File.Create(tmp))
            await file.CopyToAsync(fs);

        string outPath = Path.Combine(dataDir, "questions.txt");
        var result = CsvImporter.Import(tmp, outPath);
        // Regenerate questions.xlsx too, otherwise the xlsx (which takes
        // priority) would keep serving the old bank and the import would
        // appear to do nothing.
        string xlsxPath = Path.Combine(dataDir, "questions.xlsx");
        try
        {
            string[] lines = File.ReadAllLines(outPath);
            var qs = QuestionBank.ParseTxt(lines);
            if (qs.Count > 0)
                XlsxReader.WriteBank(xlsxPath, qs, QuestionBank.ParseTxtInfo(lines));
        }
        catch (Exception ex)
        {
            result.Warnings.Add("Could not refresh questions.xlsx: " + ex.Message);
        }
        return Results.Ok(new
        {
            result.QuestionsWritten,
            result.RowsSkipped,
            result.Warnings
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest("Import failed: " + ex.Message);
    }
    finally
    {
        try { File.Delete(tmp); } catch { /* best effort */ }
    }
});

// ---------------- Admin (hot-reload + restart) ----------------
var adminTokens = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
var loginAttempts = new System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime blockUntil)>();

bool IsAdminAuthorized(HttpContext ctx)
{
    if (string.IsNullOrEmpty(config.AdminPasswordHash)) return false;
    string token = ctx.Request.Headers["X-Admin-Token"].ToString();
    if (string.IsNullOrEmpty(token))
    {
        string auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth.Substring(7).Trim();
    }
    if (string.IsNullOrEmpty(token)) return false;
    if (adminTokens.TryGetValue(token, out DateTime exp))
    {
        if (exp > DateTime.UtcNow) return true;
        adminTokens.TryRemove(token, out _);
    }
    return false;
}

void CleanupAdminTokens()
{
    var now = DateTime.UtcNow;
    foreach (var kv in adminTokens)
        if (kv.Value <= now) adminTokens.TryRemove(kv.Key, out _);
}

void UpdateConfigFile(Dictionary<string, string> updates)
{
    string cfgPath = Path.Combine(baseDir, "config.txt");
    List<string> lines = new();
    if (File.Exists(cfgPath)) lines = File.ReadAllLines(cfgPath).ToList();
    foreach (var kv in updates)
    {
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            int eq = line.IndexOf('=');
            if (eq > 0 && line.Substring(0, eq).Trim().Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = kv.Key + "=" + kv.Value;
                found = true;
                break;
            }
        }
        if (!found) lines.Add(kv.Key + "=" + kv.Value);
    }
    File.WriteAllLines(cfgPath, lines);
}

app.MapGet("/api/admin/status", () => Results.Ok(new { hasPassword = !string.IsNullOrEmpty(config.AdminPasswordHash) }));

app.MapPost("/api/admin/login", async (HttpContext ctx) =>
{
    string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (loginAttempts.TryGetValue(ip, out var st) && st.blockUntil > DateTime.UtcNow)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    LoginRequest? req = null;
    try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); } catch {}
    if (req == null || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest("Missing password.");

    if (string.IsNullOrEmpty(config.AdminPasswordHash))
        return Results.BadRequest("Admin password not set. Use setup or set adminPassword in config.txt and restart.");

    bool ok = config.VerifyAdminPassword(req.Password);
    if (!ok)
    {
        var cur = loginAttempts.GetOrAdd(ip, (0, DateTime.MinValue));
        int cnt = cur.count + 1;
        DateTime block = cnt >= 5 ? DateTime.UtcNow.AddMinutes(5) : DateTime.MinValue;
        loginAttempts[ip] = (cnt, block);
        if (cnt >= 5) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        return Results.Json(new { ok = false }, statusCode: 401);
    }
    loginAttempts.TryRemove(ip, out _);
    string token = Guid.NewGuid().ToString("N");
    adminTokens[token] = DateTime.UtcNow.AddMinutes(30);
    CleanupAdminTokens();
    return Results.Ok(new { ok = true, token });
});

app.MapPost("/api/admin/setup", async (HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(config.AdminPasswordHash))
        return Results.BadRequest("Admin password already set. Use login.");
    LoginRequest? req = null;
    try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); } catch {}
    if (req == null || string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
        return Results.BadRequest("Password must be at least 4 characters.");
    string hash = QuizConfig.ComputeHash(req.Password);
    UpdateConfigFile(new Dictionary<string, string> { ["adminPasswordHash"] = hash });
    // hot-reload to pick up new hash
    config = QuizConfig.Load(baseDir);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/admin/config", (HttpContext ctx) =>
{
    if (!IsAdminAuthorized(ctx)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
    var folders = new List<string>();
    try
    {
        foreach (var d in Directory.GetDirectories(baseDir))
        {
            string name = Path.GetFileName(d);
            if (name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("publish", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith(".")) continue;
            if (File.Exists(Path.Combine(d, "questions.xlsx")) || File.Exists(Path.Combine(d, "questions.txt")))
                folders.Add(name);
        }
    } catch {}
    return Results.Ok(new
    {
        timeMinutes = config.TimeMinutes,
        negativeMarkingPct = config.NegativeMarkingPct,
        resultFile = config.ResultFile,
        allowResultViewing = config.AllowResultViewing,
        allowImport = config.AllowImport,
        allowReview = config.AllowReview,
        allowAnswerDetails = config.AllowAnswerDetails,
        port = config.Port,
        quizFolder = config.QuizFolder,
        hasPassword = !string.IsNullOrEmpty(config.AdminPasswordHash),
        availableFolders = folders
    });
});

app.MapPost("/api/admin/config", async (HttpContext ctx) =>
{
    if (!IsAdminAuthorized(ctx)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
    AdminConfigRequest? req = null;
    try { req = await ctx.Request.ReadFromJsonAsync<AdminConfigRequest>(); } catch {}
    if (req == null) return Results.BadRequest("Invalid config.");

    var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (req.Time.HasValue) updates["Time"] = req.Time.Value.ToString();
    if (req.NegativeMarking != null)
    {
        string v = req.NegativeMarking.Trim();
        if (v.EndsWith("%")) v = v[..^1];
        updates["NegativeMarking"] = v + "%";
    }
    if (req.ResultFile != null) updates["resultFile"] = req.ResultFile;
    if (req.AllowResultViewing.HasValue) updates["allowResultViewing"] = req.AllowResultViewing.Value ? "true" : "false";
    if (req.AllowImport.HasValue) updates["allowImport"] = req.AllowImport.Value ? "true" : "false";
    if (req.AllowReview.HasValue) updates["allowReview"] = req.AllowReview.Value ? "true" : "false";
    if (req.AllowAnswerDetails.HasValue) updates["allowAnswerDetails"] = req.AllowAnswerDetails.Value ? "true" : "false";
    if (req.Port.HasValue) updates["Port"] = req.Port.Value.ToString();
    if (req.QuizFolder != null)
    {
        string sanitized = req.QuizFolder.Trim();
        // allow empty to clear
        if (sanitized.Length == 0) updates["quizFolder"] = "";
        else
        {
            // reuse sanitize logic via QuizConfig
            var tmp = new QuizConfig();
            // we call the same sanitize via reflection? just write raw, Load will sanitize
            updates["quizFolder"] = sanitized;
        }
    }
    if (!string.IsNullOrWhiteSpace(req.NewAdminPassword))
    {
        if (req.NewAdminPassword.Length < 4) return Results.BadRequest("Admin password must be at least 4 characters.");
        updates["adminPasswordHash"] = QuizConfig.ComputeHash(req.NewAdminPassword);
        // remove plain adminPassword if present
    }

    string oldFolder = config.QuizFolder;
    int? oldPort = config.Port;
    UpdateConfigFile(updates);

    // Check if restart needed for folder/port
    var newConfig = QuizConfig.Load(baseDir);
    bool needRestart = !string.Equals(oldFolder ?? "", newConfig.QuizFolder ?? "", StringComparison.OrdinalIgnoreCase)
                    || oldPort != newConfig.Port;

    if (needRestart)
    {
        return Results.Ok(new { ok = true, needRestart = true, message = "Config saved. Restart required for folder/port change. Use Restart button." });
    }

    // Hot-reload in-place for other settings
    string newDataDir = baseDir;
    if (!string.IsNullOrWhiteSpace(newConfig.QuizFolder))
    {
        newDataDir = Path.Combine(baseDir, newConfig.QuizFolder);
        try { Directory.CreateDirectory(newDataDir); } catch {}
    }
    // update globals
    config = newConfig;
    dataDir = newDataDir;
    bank = QuestionBank.Load(dataDir);
    quizInfo = bank.Info;
    // update ResultStore path
    try
    {
        var rs = app.Services.GetRequiredService<ResultStore>();
        rs.UpdatePath(config.ResultPath(dataDir));
    } catch {}
    return Results.Ok(new { ok = true, needRestart = false });
});

app.MapPost("/api/admin/restart", (HttpContext ctx) =>
{
    if (!IsAdminAuthorized(ctx)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
    string exe = Environment.ProcessPath ?? Path.Combine(baseDir, "QuizWeb.exe");
    if (!File.Exists(exe)) exe = Path.Combine(baseDir, "QuizWeb.exe");
    if (!File.Exists(exe)) return Results.Problem("Cannot determine executable path.");
    try
    {
        string arguments = args.Length > 0 ? string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")) : "";
        // Use `start` so the new QuizWeb gets its own visible black window (console) for teachers.
        // The initial cmd is hidden and just waits 1s for the old port to free.
        string cmd = $"timeout /t 1 /nobreak >nul & start \"\" \"{exe}\" {arguments}";
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {cmd}")
        {
            UseShellExecute = false,
            WorkingDirectory = baseDir,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi);
    }
    catch (Exception ex) { return Results.Problem("Failed to start new process: " + ex.Message); }

    var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
    _ = Task.Run(async () => { await Task.Delay(300); lifetime.StopApplication(); });
    return Results.Ok(new { ok = true, restarting = true });
});

var store = app.Services.GetRequiredService<ResultStore>();
try
{
    PrintBanner(port, config, args);
    app.Run();
}
finally
{
    store.Dispose();
}

return 0;

static string NormalizeImagePath(string? imagePath)
{
    if (string.IsNullOrWhiteSpace(imagePath)) return null!;
    string cleaned = imagePath.Replace('\\', '/').Trim();
    if (cleaned.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
        cleaned = cleaned["images/".Length..];
    return "images/" + cleaned.TrimStart('/');
}

static void PrintBanner(int configPort, QuizConfig config, string[] args)
{
    int port = configPort;
    for (int i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] == "--urls" && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var u) && u.Port > 0)
            port = u.Port;
    }
    Console.WriteLine("==================================================");
    Console.WriteLine("  Quiz Web App");
    string folderInfo = string.IsNullOrWhiteSpace(config.QuizFolder) ? "(root)" : config.QuizFolder;
    Console.WriteLine("  Folder: " + folderInfo + " | Time: " + config.TimeMinutes + " min | Negative marking: "
        + config.NegativeMarkingPct + "% | Result file: " + config.ResultFile);
    Console.WriteLine("  Students on the LAN open one of these URLs:");
    foreach (string ip in LocalAddresses())
        Console.WriteLine($"    http://{ip}:{port}");
    Console.WriteLine("  (or http://localhost:" + port + " on this computer)");
    Console.WriteLine("  Press Ctrl+C to stop.");
    Console.WriteLine("==================================================");
}

static IEnumerable<string> LocalAddresses()
{
    var set = new SortedSet<string>();
    try
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    set.Add(addr.Address.ToString());
            }
        }
    }
    catch { /* best effort */ }
    return set;
}

public sealed class CheckRequest
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Section { get; set; } = "";
}

public sealed class LoginRequest
{
    public string Password { get; set; } = "";
}

public sealed class AdminConfigRequest
{
    public int? Time { get; set; }
    public string? NegativeMarking { get; set; }
    public string? ResultFile { get; set; }
    public bool? AllowResultViewing { get; set; }
    public bool? AllowImport { get; set; }
    public bool? AllowReview { get; set; }
    public bool? AllowAnswerDetails { get; set; }
    public int? Port { get; set; }
    public string? QuizFolder { get; set; }
    public string? NewAdminPassword { get; set; }
}
