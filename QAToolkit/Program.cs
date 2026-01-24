using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Services;
using QAToolkit.Models.Automation;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// Register OMR Filler Service
builder.Services.AddScoped<IOmrFillerService, OmrFillerService>();

// Configure Test Runner Settings from appsettings.json
builder.Services.Configure<TestRunnerSettings>(
    builder.Configuration.GetSection("TestRunner"));

// Register Test Execution Service (Background Service)
builder.Services.AddSingleton<TestExecutionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TestExecutionService>());

// Register TestRunDbService for database operations
builder.Services.AddScoped<TestRunDbService>();

// Register Remote Test Runner Service (only active when IsRemoteRunner=true)
builder.Services.AddHostedService<RemoteTestRunnerService>();

// Register named HttpClient for remote runner
builder.Services.AddHttpClient("RemoteRunner");

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

    // Create TestRuns table if it doesn't exist (for existing databases)
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS TestRuns (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'PENDING',
                Suites TEXT NOT NULL DEFAULT '',
                Environment TEXT NOT NULL DEFAULT '',
                Browser TEXT NOT NULL DEFAULT '',
                Project TEXT NOT NULL DEFAULT '',
                IsHeadless INTEGER NOT NULL DEFAULT 0,
                RetryFailed INTEGER NOT NULL DEFAULT 0,
                SpecificCategory TEXT,
                TriggeredBy TEXT,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT,
                CompletedAt TEXT,
                TotalTests INTEGER NOT NULL DEFAULT 0,
                PassedTests INTEGER NOT NULL DEFAULT 0,
                FailedTests INTEGER NOT NULL DEFAULT 0,
                SkippedTests INTEGER NOT NULL DEFAULT 0,
                Duration TEXT,
                ErrorMessage TEXT,
                CurrentStep TEXT,
                Logs TEXT,
                TestCases TEXT,
                ReportUrl TEXT,
                ClaimedByRunner TEXT
            )");

        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_TestRuns_RunId ON TestRuns (RunId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TestRuns_Status ON TestRuns (Status)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_TestRuns_CreatedAt ON TestRuns (CreatedAt)");
    }
    catch (Exception ex)
    {
        // Table might already exist, ignore
        Console.WriteLine($"Database setup: {ex.Message}");
    }
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
