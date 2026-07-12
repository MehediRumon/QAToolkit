using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using QAToolkit.Helpers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Register OMR Filler Service
builder.Services.AddScoped<IOmrFillerService, OmrFillerService>();

// Register Playwright runner (scoped — needs DbContext)
builder.Services.AddScoped<PlaywrightRunnerService>();

// Register Hermes Agent service
builder.Services.AddScoped<QAToolkit.Services.IHermesService, QAToolkit.Services.HermesService>();

// Register background scheduler
builder.Services.AddHostedService<SchedulerHostedService>();

// Configure Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

// Configure SQLite Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=QAToolkit.db"));

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureCreated();

    // Ensure Workflows table exists (EnsureCreated won't add to existing DB)
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS Workflows (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Category TEXT,
            HtmlContent TEXT NOT NULL DEFAULT '',
            CreatedBy TEXT,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT,
            IsPublic INTEGER NOT NULL DEFAULT 0
        )");
    try { db.Database.ExecuteSqlRaw("ALTER TABLE Workflows ADD COLUMN IsPublic INTEGER NOT NULL DEFAULT 0"); } catch { /* column already exists */ }
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Workflows_Category ON Workflows (Category)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS PlaywrightScripts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Description TEXT,
            Tags TEXT,
            ScriptContent TEXT NOT NULL DEFAULT '',
            FileExtension TEXT NOT NULL DEFAULT '.js',
            RunMode TEXT NOT NULL DEFAULT 'node',
            CreatedBy TEXT,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT,
            IsPublic INTEGER NOT NULL DEFAULT 0
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_PlaywrightScripts_Tags ON PlaywrightScripts (Tags)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ScheduledRuns (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ScriptId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            ScheduleType TEXT NOT NULL DEFAULT 'once',
            RunOnce TEXT,
            DailyTime TEXT,
            IntervalMinutes INTEGER,
            ParamsJson TEXT,
            IsEnabled INTEGER NOT NULL DEFAULT 1,
            LastRunAt TEXT,
            LastRunStatus TEXT,
            NextRunAt TEXT,
            CreatedBy TEXT,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ScheduledRuns_ScriptId ON ScheduledRuns (ScriptId)");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ScheduledRuns_NextRunAt ON ScheduledRuns (NextRunAt)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ScheduledRunLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ScheduledRunId INTEGER NOT NULL,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT,
            Status TEXT NOT NULL DEFAULT '',
            Output TEXT,
            ExitCode INTEGER,
            DurationMs INTEGER NOT NULL DEFAULT 0
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ScheduledRunLogs_ScheduledRunId ON ScheduledRunLogs (ScheduledRunId)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ScriptFiles (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FileName TEXT NOT NULL,
            OriginalName TEXT NOT NULL,
            Description TEXT,
            FileSizeBytes INTEGER NOT NULL DEFAULT 0,
            ContentType TEXT,
            StoredPath TEXT NOT NULL,
            UploadedBy TEXT NOT NULL DEFAULT '',
            UploadedAt TEXT NOT NULL
        )");
    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_ScriptFiles_FileName ON ScriptFiles (FileName)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS HermesChats (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName TEXT NOT NULL DEFAULT '',
            Title TEXT NOT NULL DEFAULT 'New Chat',
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_HermesChats_UserName ON HermesChats (UserName)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS HermesMessages (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChatId INTEGER NOT NULL,
            Role TEXT NOT NULL DEFAULT 'user',
            Content TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_HermesMessages_ChatId ON HermesMessages (ChatId)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS HermesActivities (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName TEXT NOT NULL DEFAULT '',
            ActivityType TEXT NOT NULL DEFAULT '',
            EntityId INTEGER NOT NULL DEFAULT 0,
            EntityName TEXT NOT NULL DEFAULT '',
            Tags TEXT,
            Extra TEXT,
            CreatedAt TEXT NOT NULL
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_HermesActivities_CreatedAt ON HermesActivities (CreatedAt)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS HermesKnowledges (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Project TEXT NOT NULL DEFAULT '',
            Module TEXT,
            Summary TEXT NOT NULL DEFAULT '',
            UpdatedAt TEXT NOT NULL,
            UpdatedBy TEXT
        )");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS HermesUserSettings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName TEXT NOT NULL DEFAULT '',
            Provider TEXT NOT NULL DEFAULT 'Groq',
            ApiKey TEXT NOT NULL DEFAULT '',
            Model TEXT
        )");
    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_HermesUserSettings_UserName ON HermesUserSettings (UserName)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS Meetings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            MeetingDate TEXT NOT NULL,
            DurationMinutes INTEGER,
            Participants TEXT,
            Source TEXT,
            Summary TEXT,
            Transcript TEXT,
            CreatedBy TEXT,
            CreatedAt TEXT NOT NULL
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Meetings_MeetingDate ON Meetings (MeetingDate)");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS MeetingScreenNotes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MeetingId INTEGER NOT NULL,
            CapturedAt TEXT,
            Note TEXT,
            ImagePath TEXT
        )");
    db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MeetingScreenNotes_MeetingId ON MeetingScreenNotes (MeetingId)");

    // Ensure upload directory exists
    var uploadDir = Path.Combine(
        app.Configuration["Playwright:WorkingDirectory"] ?? @"C:\qa-scripts",
        "script-uploads");
    Directory.CreateDirectory(uploadDir);

    // Deploy bundled script libraries (ScriptLibs/*.js, e.g. ums-helpers) into the
    // Playwright working directory's node_modules so any script can require() them
    // (the runner sets NODE_PATH to that folder).
    try
    {
        var libSource = Path.Combine(app.Environment.ContentRootPath, "ScriptLibs");
        if (Directory.Exists(libSource))
        {
            var nodeModulesDir = Path.Combine(
                app.Configuration["Playwright:WorkingDirectory"] ?? @"C:\qa-scripts",
                "node_modules");
            Directory.CreateDirectory(nodeModulesDir);
            foreach (var lib in Directory.GetFiles(libSource, "*.js"))
                File.Copy(lib, Path.Combine(nodeModulesDir, Path.GetFileName(lib)), overwrite: true);
        }
    }
    catch { /* non-fatal — scripts that don't use shared libs are unaffected */ }

    // Seed NextRunAt for any existing schedules that don't have it set
    var pendingSchedules = db.ScheduledRuns
        .Where(s => s.IsEnabled && s.NextRunAt == null && s.ScheduleType != "once")
        .ToList();
    foreach (var s in pendingSchedules)
    {
        s.NextRunAt = SchedulerHostedService.CalculateNextRun(s, DateTimeHelper.BdNow);
    }
    if (pendingSchedules.Count > 0) db.SaveChanges();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Serve static files from wwwroot including dynamically created outputs
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
