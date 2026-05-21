using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using QAToolkit.Helpers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// Register OMR Filler Service
builder.Services.AddScoped<IOmrFillerService, OmrFillerService>();

// Register Playwright runner (scoped — needs DbContext)
builder.Services.AddScoped<PlaywrightRunnerService>();

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
