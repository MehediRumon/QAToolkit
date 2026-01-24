using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Models.Automation;
using System.Text.Json;

namespace QAToolkit.Services;

public class TestRunDbService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TestRunDbService> _logger;

    public TestRunDbService(IServiceScopeFactory scopeFactory, ILogger<TestRunDbService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<TestRunEntity> CreateTestRunAsync(TestRunRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var runId = request.RunId ?? $"RUN-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";

        var entity = new TestRunEntity
        {
            RunId = runId,
            Status = "PENDING",
            Suites = string.Join(",", request.Suites),
            Environment = request.Environment,
            Browser = request.Browser,
            Project = request.Project,
            IsHeadless = request.IsHeadless,
            RetryFailed = request.RetryFailed,
            SpecificCategory = request.SpecificCategory,
            TriggeredBy = request.TriggeredBy,
            CreatedAt = DateTime.UtcNow
        };

        db.TestRuns.Add(entity);
        await db.SaveChangesAsync();

        _logger.LogInformation("Test run {RunId} created in database", runId);
        return entity;
    }

    public async Task<TestRunEntity?> GetTestRunAsync(string runId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.TestRuns.FirstOrDefaultAsync(t => t.RunId == runId);
    }

    public async Task<List<TestRunEntity>> GetPendingTestRunsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.TestRuns
            .Where(t => t.Status == "PENDING")
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<TestRunEntity?> ClaimTestRunAsync(string runId, string runnerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var entity = await db.TestRuns.FirstOrDefaultAsync(t => t.RunId == runId && t.Status == "PENDING");
        if (entity == null)
            return null;

        entity.Status = "RUNNING";
        entity.ClaimedByRunner = runnerId;
        entity.StartedAt = DateTime.UtcNow;
        entity.CurrentStep = "Claimed by runner";

        await db.SaveChangesAsync();
        _logger.LogInformation("Test run {RunId} claimed by runner {RunnerId}", runId, runnerId);
        return entity;
    }

    public async Task UpdateTestRunStatusAsync(string runId, TestStatus status)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var entity = await db.TestRuns.FirstOrDefaultAsync(t => t.RunId == runId);
        if (entity == null)
        {
            _logger.LogWarning("Test run {RunId} not found for status update", runId);
            return;
        }

        entity.Status = status.Status;
        entity.CurrentStep = status.CurrentStep;
        entity.TotalTests = status.TotalTests;
        entity.PassedTests = status.PassedTests;
        entity.FailedTests = status.FailedTests;
        entity.SkippedTests = status.SkippedTests;
        entity.Duration = status.Duration;
        entity.ErrorMessage = status.ErrorMessage;
        entity.ReportUrl = status.ReportUrl;
        entity.Logs = JsonSerializer.Serialize(status.Logs);
        entity.TestCases = JsonSerializer.Serialize(status.TestCases);

        if (status.Status == "PASSED" || status.Status == "FAILED" || status.Status == "ERROR")
        {
            entity.CompletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Test run {RunId} status updated to {Status}", runId, status.Status);
    }

    public async Task<List<TestRunEntity>> GetRecentTestRunsAsync(int count = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.TestRuns
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public TestStatus ToTestStatus(TestRunEntity entity)
    {
        var status = new TestStatus
        {
            RunId = entity.RunId,
            Status = entity.Status,
            Suite = entity.Suites,
            Environment = entity.Environment,
            Browser = entity.Browser,
            Project = entity.Project,
            TotalTests = entity.TotalTests,
            PassedTests = entity.PassedTests,
            FailedTests = entity.FailedTests,
            SkippedTests = entity.SkippedTests,
            Duration = entity.Duration ?? string.Empty,
            StartTime = entity.StartedAt ?? entity.CreatedAt,
            EndTime = entity.CompletedAt,
            ErrorMessage = entity.ErrorMessage,
            TriggeredBy = entity.TriggeredBy,
            CurrentStep = entity.CurrentStep ?? string.Empty,
            ReportUrl = entity.ReportUrl
        };

        if (!string.IsNullOrEmpty(entity.Logs))
        {
            try { status.Logs = JsonSerializer.Deserialize<List<string>>(entity.Logs) ?? new(); }
            catch { status.Logs = new(); }
        }

        if (!string.IsNullOrEmpty(entity.TestCases))
        {
            try { status.TestCases = JsonSerializer.Deserialize<List<TestCaseResult>>(entity.TestCases) ?? new(); }
            catch { status.TestCases = new(); }
        }

        return status;
    }

    public TestRunRequest ToTestRunRequest(TestRunEntity entity)
    {
        return new TestRunRequest
        {
            RunId = entity.RunId,
            Suites = entity.Suites.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Environment = entity.Environment,
            Browser = entity.Browser,
            Project = entity.Project,
            IsHeadless = entity.IsHeadless,
            RetryFailed = entity.RetryFailed,
            SpecificCategory = entity.SpecificCategory,
            TriggeredBy = entity.TriggeredBy
        };
    }
}
