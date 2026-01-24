namespace QAToolkit.Models.Automation;

public class TestRunRequest
{
    public List<string> Suites { get; set; } = new() { "Smoke" };
    public string Environment { get; set; } = "Staging";
    public string Browser { get; set; } = "Chrome";
    public string? TriggeredBy { get; set; }
    public string? RunId { get; set; }
    public string Project { get; set; } = "ERP";
    public bool IsHeadless { get; set; } = false;
    public string? SpecificCategory { get; set; }
    public bool RetryFailed { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? BaseUrl { get; set; }
}

public class TestRunResponse
{
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = "QUEUED";
    public string Message { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public string? ReportUrl { get; set; }
}

public class TestStatus
{
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public string Suite { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public string Duration { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ReportUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TriggeredBy { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public List<string> Logs { get; set; } = new();
    public List<TestCaseResult> TestCases { get; set; } = new();
}

public class TestCaseResult
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? ScreenshotPath { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public class TestReportViewModel
{
    public TestStatus Status { get; set; } = new();
    public double PassRate { get; set; }
    public List<TestCaseResult> FailedTests { get; set; } = new();
    public List<TestCaseResult> PassedTests { get; set; } = new();
    public List<TestCaseResult> SkippedTests { get; set; } = new();
    public List<TestCaseResult> SlowestTests { get; set; } = new();
    public string? OutputLog { get; set; }
    public List<string> ExecutionLogs { get; set; } = new();
    public Dictionary<string, int> FailureCategories { get; set; } = new();
    public string AverageDuration { get; set; } = string.Empty;
}

public class TestSuiteInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int TestCount { get; set; }
    public List<string> Categories { get; set; } = new();
}

public class EnvironmentInfo
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class TestRunnerSettings
{
    public string SolutionPath { get; set; } = @"E:\OSL-UI-Automation";
    public string ResultsPath { get; set; } = @"E:\OSL-UI-Automation\TestResults";
    public int MaxParallelRuns { get; set; } = 1;
    public Dictionary<string, string> Environments { get; set; } = new()
    {
        ["QA"] = "https://ums-qa.osl.team",
        ["Staging"] = "https://ums-1.osl.team",
        ["Prod"] = "https://ums.osl.team"
    };

    // Remote runner settings
    public bool IsRemoteRunner { get; set; } = false;
    public string? RemoteServerUrl { get; set; }
    public string? RunnerApiKey { get; set; }
    public int PollingIntervalSeconds { get; set; } = 10;
}

// Database entity for persisting test runs
public class TestRunEntity
{
    public int Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public string Suites { get; set; } = string.Empty; // Comma-separated
    public string Environment { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public bool IsHeadless { get; set; }
    public bool RetryFailed { get; set; }
    public string? SpecificCategory { get; set; }
    public string? TriggeredBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public string? Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CurrentStep { get; set; }
    public string? Logs { get; set; } // JSON array
    public string? TestCases { get; set; } // JSON array
    public string? ReportUrl { get; set; }
    public string? ClaimedByRunner { get; set; } // Which runner is executing
}
