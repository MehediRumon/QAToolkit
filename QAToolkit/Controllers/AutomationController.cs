using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QAToolkit.Models.Automation;
using QAToolkit.Services;

namespace QAToolkit.Controllers;

[Authorize]
public class AutomationController : Controller
{
    private readonly TestExecutionService _executionService;
    private readonly TestRunDbService _dbService;
    private readonly TestRunnerSettings _settings;
    private readonly ILogger<AutomationController> _logger;

    public AutomationController(
        TestExecutionService executionService,
        TestRunDbService dbService,
        IOptions<TestRunnerSettings> settings,
        ILogger<AutomationController> logger)
    {
        _executionService = executionService;
        _dbService = dbService;
        _settings = settings.Value;
        _logger = logger;
    }

    // GET: Automation Dashboard
    public async Task<IActionResult> Index()
    {
        var erpSuites = GetSuitesWithDefaults("ERP");
        var portalSuites = GetSuitesWithDefaults("OnlinePortal");

        ViewBag.Suites = erpSuites; // Default to ERP
        ViewBag.Environments = _executionService.GetEnvironments();
        ViewBag.Browsers = new[] { "Chrome", "Edge", "Firefox" };
        ViewBag.Projects = new[]
        {
            new { Name = "ERP", Description = "UMS ERP System Tests", TestCount = erpSuites.Count },
            new { Name = "OnlinePortal", Description = "Student/Teacher Portal Tests", TestCount = portalSuites.Count }
        };

        // Get recent runs from database
        var dbRuns = await _dbService.GetRecentTestRunsAsync(10);
        ViewBag.RecentRuns = dbRuns.Select(e => _dbService.ToTestStatus(e)).ToList();
        return View();
    }

    // Static cache for synced suites from remote runner (shared with API controller)
    public static readonly Dictionary<string, List<TestSuiteInfo>> SyncedSuites = new();

    private List<TestSuiteInfo> GetSuitesWithDefaults(string project)
    {
        // First try locally scanned suites
        var suites = _executionService.GetSuites(project);
        if (suites.Count > 1) // Has more than just "All"
        {
            return suites;
        }

        // Then try synced suites from remote runner
        if (SyncedSuites.TryGetValue(project, out var syncedSuites) && syncedSuites.Count > 0)
        {
            return syncedSuites;
        }

        // Finally fall back to defaults
        return new List<TestSuiteInfo>
        {
            new() { Name = "All", Description = "Run all available tests" },
            new() { Name = "Smoke", Description = "Quick smoke tests" },
            new() { Name = "Regression", Description = "Full regression suite" },
            new() { Name = "Login", Description = "Login functionality tests" },
            new() { Name = "Dashboard", Description = "Dashboard tests" },
            new() { Name = "StudentAdmission", Description = "Student admission tests" },
            new() { Name = "TeacherPayment", Description = "Teacher payment tests" }
        };
    }

    // GET: Suites by Project (for AJAX)
    [HttpGet]
    public IActionResult GetSuites(string project = "ERP")
    {
        var suites = GetSuitesWithDefaults(project);
        return Json(suites);
    }

    // POST: Refresh Suites Cache
    [HttpPost]
    public IActionResult RefreshSuites(string? project = null)
    {
        _executionService.RefreshSuitesCache(project);
        return Ok(new { message = "Cache refreshed", project = project ?? "all" });
    }

    // POST: Run Tests
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunTests([FromForm] TestRunRequest request, [FromForm] string? Suites)
    {
        // Parse comma-separated suites from form
        if (!string.IsNullOrWhiteSpace(Suites))
        {
            request.Suites = Suites.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        if (request.Suites.Count == 0)
        {
            request.Suites = new List<string> { "All" };
        }

        request.TriggeredBy = User.Identity?.Name ?? "Anonymous";

        // Store in database for remote runner to pick up
        var entity = await _dbService.CreateTestRunAsync(request);
        TempData["Success"] = $"Test run {entity.RunId} queued for remote execution!";
        return RedirectToAction(nameof(Index));
    }

    // GET: Test Status (for AJAX polling)
    [HttpGet]
    public async Task<IActionResult> GetStatus(string runId)
    {
        // First check in-memory (for local execution)
        var status = _executionService.GetStatus(runId);
        if (status != null)
            return Json(status);

        // Then check database (for remote execution)
        var entity = await _dbService.GetTestRunAsync(runId);
        if (entity == null)
            return NotFound(new { message = $"Test run '{runId}' not found" });

        return Json(_dbService.ToTestStatus(entity));
    }

    // GET: All Statuses (for AJAX)
    [HttpGet]
    public async Task<IActionResult> GetAllStatuses()
    {
        var dbRuns = await _dbService.GetRecentTestRunsAsync(50);
        return Json(dbRuns.Select(e => _dbService.ToTestStatus(e)));
    }

    // GET: Recent Runs (for AJAX)
    [HttpGet]
    public async Task<IActionResult> GetRecentRuns(int count = 20)
    {
        var dbRuns = await _dbService.GetRecentTestRunsAsync(count);
        return Json(dbRuns.Select(e => _dbService.ToTestStatus(e)));
    }

    // GET: View Report
    public async Task<IActionResult> Report(string runId)
    {
        // First check in-memory
        var status = _executionService.GetStatus(runId);

        // Then check database
        if (status == null)
        {
            var entity = await _dbService.GetTestRunAsync(runId);
            if (entity != null)
                status = _dbService.ToTestStatus(entity);
        }

        if (status == null)
        {
            TempData["Error"] = "Report not found.";
            return RedirectToAction(nameof(Index));
        }

        // Build the view model with insights
        var model = new TestReportViewModel
        {
            Status = status,
            PassRate = status.TotalTests > 0 ? (double)status.PassedTests / status.TotalTests * 100 : 0,
            FailedTests = status.TestCases
                .Where(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            PassedTests = status.TestCases
                .Where(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            SkippedTests = status.TestCases
                .Where(t => !t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                           !t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            SlowestTests = status.TestCases
                .OrderByDescending(t => t.DurationSeconds)
                .Take(5)
                .ToList(),
            ExecutionLogs = status.Logs
        };

        // Calculate average duration
        if (status.TestCases.Any(t => t.DurationSeconds > 0))
        {
            var avgSeconds = status.TestCases.Where(t => t.DurationSeconds > 0).Average(t => t.DurationSeconds);
            model.AverageDuration = TimeSpan.FromSeconds(avgSeconds).ToString(@"mm\:ss\.fff");
        }

        // Categorize failures by error type
        foreach (var failed in model.FailedTests.Where(f => !string.IsNullOrEmpty(f.ErrorMessage)))
        {
            var category = CategorizeError(failed.ErrorMessage!);
            if (model.FailureCategories.ContainsKey(category))
                model.FailureCategories[category]++;
            else
                model.FailureCategories[category] = 1;
        }

        // Try to load output log
        var logPath = Path.Combine(_settings.ResultsPath, runId, "output.log");
        if (System.IO.File.Exists(logPath))
        {
            model.OutputLog = System.IO.File.ReadAllText(logPath);
        }

        return View(model);
    }

    private string CategorizeError(string errorMessage)
    {
        var lowerError = errorMessage.ToLower();

        if (lowerError.Contains("timeout") || lowerError.Contains("timed out"))
            return "Timeout";
        if (lowerError.Contains("element") && (lowerError.Contains("not found") || lowerError.Contains("could not")))
            return "Element Not Found";
        if (lowerError.Contains("assertion") || lowerError.Contains("expected"))
            return "Assertion Failed";
        if (lowerError.Contains("connection") || lowerError.Contains("network"))
            return "Connection Error";
        if (lowerError.Contains("null") || lowerError.Contains("nullreference"))
            return "Null Reference";
        if (lowerError.Contains("stale") || lowerError.Contains("detached"))
            return "Stale Element";
        if (lowerError.Contains("click") || lowerError.Contains("intercepted"))
            return "Click Intercepted";
        if (lowerError.Contains("login") || lowerError.Contains("auth"))
            return "Authentication";

        return "Other";
    }

    // POST: Retry Failed Tests
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Retry(string runId)
    {
        var previousStatus = _executionService.GetStatus(runId);
        if (previousStatus == null)
        {
            TempData["Error"] = $"Test run '{runId}' not found";
            return RedirectToAction(nameof(Index));
        }

        // Parse suites from comma-separated string stored in status
        var suites = previousStatus.Suite
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        var request = new TestRunRequest
        {
            Suites = suites.Count > 0 ? suites : new List<string> { "All" },
            Environment = previousStatus.Environment,
            Browser = previousStatus.Browser,
            Project = previousStatus.Project,
            TriggeredBy = User.Identity?.Name ?? "Retry",
            RetryFailed = true,
            IsHeadless = true
        };

        var response = _executionService.QueueTestRun(request);
        TempData["Success"] = $"Retry queued as {response.RunId}";
        return RedirectToAction(nameof(Index));
    }
}

// API Controller for external integrations
[ApiController]
[Route("api/automation")]
public class AutomationApiController : ControllerBase
{
    private readonly TestExecutionService _executionService;
    private readonly TestRunDbService _dbService;
    private readonly ILogger<AutomationApiController> _logger;

    public AutomationApiController(
        TestExecutionService executionService,
        TestRunDbService dbService,
        ILogger<AutomationApiController> logger)
    {
        _executionService = executionService;
        _dbService = dbService;
        _logger = logger;
    }

    [HttpPost("run")]
    public async Task<ActionResult<TestRunResponse>> RunTests([FromBody] TestRunRequest request)
    {
        _logger.LogInformation("API: Test run requested: Suites={Suites}, Env={Env}", string.Join(",", request.Suites), request.Environment);

        // Store in database for remote runner to pick up
        var entity = await _dbService.CreateTestRunAsync(request);

        return Accepted(new TestRunResponse
        {
            RunId = entity.RunId,
            Status = "PENDING",
            Message = "Test run queued for remote execution",
            StartTime = entity.CreatedAt
        });
    }

    [HttpGet("status/{runId}")]
    public async Task<ActionResult<TestStatus>> GetStatus(string runId)
    {
        // First check in-memory (for local execution)
        var status = _executionService.GetStatus(runId);
        if (status != null)
            return Ok(status);

        // Then check database (for remote execution)
        var entity = await _dbService.GetTestRunAsync(runId);
        if (entity == null)
            return NotFound(new { message = $"Test run '{runId}' not found" });

        return Ok(_dbService.ToTestStatus(entity));
    }

    [HttpGet("status")]
    public async Task<ActionResult<IEnumerable<TestStatus>>> GetAllStatuses()
    {
        // Combine in-memory and database statuses
        var inMemory = _executionService.GetAllStatuses();
        var dbRuns = await _dbService.GetRecentTestRunsAsync(50);
        var dbStatuses = dbRuns.Select(e => _dbService.ToTestStatus(e));

        // Merge, preferring in-memory for running tests
        var combined = inMemory.ToList();
        foreach (var dbStatus in dbStatuses)
        {
            if (!combined.Any(s => s.RunId == dbStatus.RunId))
                combined.Add(dbStatus);
        }

        return Ok(combined.OrderByDescending(s => s.StartTime));
    }

    [HttpGet("recent")]
    public async Task<ActionResult<IEnumerable<TestStatus>>> GetRecentRuns([FromQuery] int count = 20)
    {
        var dbRuns = await _dbService.GetRecentTestRunsAsync(count);
        return Ok(dbRuns.Select(e => _dbService.ToTestStatus(e)));
    }

    [HttpGet("suites")]
    public ActionResult<IEnumerable<TestSuiteInfo>> GetSuites([FromQuery] string? project = null)
    {
        return Ok(_executionService.GetSuites(project));
    }

    [HttpGet("environments")]
    public ActionResult<IEnumerable<EnvironmentInfo>> GetEnvironments()
    {
        return Ok(_executionService.GetEnvironments());
    }

    [HttpGet("health")]
    public ActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    // ============== Remote Runner Endpoints ==============

    /// <summary>
    /// Get pending test runs for remote runner to claim
    /// </summary>
    [HttpGet("runner/pending")]
    public async Task<ActionResult<IEnumerable<TestRunRequest>>> GetPendingRuns([FromHeader(Name = "X-Runner-Key")] string? apiKey)
    {
        // TODO: Validate API key in production
        var pending = await _dbService.GetPendingTestRunsAsync();
        return Ok(pending.Select(e => _dbService.ToTestRunRequest(e)));
    }

    /// <summary>
    /// Claim a test run for execution
    /// </summary>
    [HttpPost("runner/claim/{runId}")]
    public async Task<ActionResult<TestRunRequest>> ClaimRun(string runId, [FromHeader(Name = "X-Runner-Key")] string? apiKey, [FromHeader(Name = "X-Runner-Id")] string? runnerId)
    {
        runnerId ??= $"runner-{System.Environment.MachineName}";

        var entity = await _dbService.ClaimTestRunAsync(runId, runnerId);
        if (entity == null)
            return NotFound(new { message = "Test run not found or already claimed" });

        _logger.LogInformation("Runner {RunnerId} claimed test run {RunId}", runnerId, runId);
        return Ok(_dbService.ToTestRunRequest(entity));
    }

    /// <summary>
    /// Update test run status from remote runner
    /// </summary>
    [HttpPut("runner/status/{runId}")]
    public async Task<ActionResult> UpdateRunStatus(string runId, [FromBody] TestStatus status, [FromHeader(Name = "X-Runner-Key")] string? apiKey)
    {
        await _dbService.UpdateTestRunStatusAsync(runId, status);
        _logger.LogInformation("Runner updated status for {RunId}: {Status}", runId, status.Status);
        return Ok(new { message = "Status updated" });
    }

    /// <summary>
    /// Get suites list for remote display (cached/static list for when local folder not available)
    /// </summary>
    [HttpGet("runner/suites/{project}")]
    public ActionResult<IEnumerable<TestSuiteInfo>> GetSuitesForRunner(string project)
    {
        // Return cached or static suite list for remote UI
        var suites = _executionService.GetSuites(project);
        if (suites.Count == 0)
        {
            // Return default suites if local scan not available
            suites = GetDefaultSuites(project);
        }
        return Ok(suites);
    }

    private List<TestSuiteInfo> GetDefaultSuites(string project)
    {
        // Default suites when running on server without local test project
        return new List<TestSuiteInfo>
        {
            new() { Name = "Smoke", Description = "Quick smoke tests", TestCount = 0 },
            new() { Name = "Regression", Description = "Full regression suite", TestCount = 0 },
            new() { Name = "Login", Description = "Login functionality tests", TestCount = 0 },
            new() { Name = "Dashboard", Description = "Dashboard tests", TestCount = 0 }
        };
    }

    /// <summary>
    /// Sync suites from remote runner
    /// </summary>
    [HttpPost("runner/sync-suites/{project}")]
    public ActionResult SyncSuites(string project, [FromBody] List<TestSuiteInfo> suites, [FromHeader(Name = "X-Runner-Key")] string? apiKey)
    {
        AutomationController.SyncedSuites[project] = suites;
        _logger.LogInformation("Synced {Count} suites for project {Project}", suites.Count, project);
        return Ok(new { message = "Suites synced", count = suites.Count });
    }

    /// <summary>
    /// Get synced suites (for UI)
    /// </summary>
    [HttpGet("runner/synced-suites/{project}")]
    public ActionResult<IEnumerable<TestSuiteInfo>> GetSyncedSuites(string project)
    {
        if (AutomationController.SyncedSuites.TryGetValue(project, out var suites) && suites.Count > 0)
        {
            return Ok(suites);
        }
        return Ok(GetDefaultSuites(project));
    }
}
