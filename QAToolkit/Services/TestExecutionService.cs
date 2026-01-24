using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using QAToolkit.Models.Automation;

namespace QAToolkit.Services;

public class TestExecutionService : IHostedService
{
    private readonly ILogger<TestExecutionService> _logger;
    private readonly TestRunnerSettings _settings;
    private readonly ConcurrentDictionary<string, TestStatus> _runStatuses;
    private readonly ConcurrentQueue<TestRunRequest> _testQueue;
    private readonly SemaphoreSlim _executionSemaphore;
    private CancellationTokenSource? _cts;
    private Task? _processingTask;

    public TestExecutionService(
        ILogger<TestExecutionService> logger,
        IOptions<TestRunnerSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        _runStatuses = new ConcurrentDictionary<string, TestStatus>();
        _testQueue = new ConcurrentQueue<TestRunRequest>();
        _executionSemaphore = new SemaphoreSlim(_settings.MaxParallelRuns);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessQueueAsync(_cts.Token);
        _logger.LogInformation("Test Execution Service started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_processingTask != null)
        {
            await _processingTask;
        }
        _logger.LogInformation("Test Execution Service stopped");
    }

    public TestRunResponse QueueTestRun(TestRunRequest request)
    {
        var runId = request.RunId ?? $"RUN-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";
        request.RunId = runId;

        // Join multiple suites for display
        var suitesDisplay = request.Suites.Count > 0 ? string.Join(", ", request.Suites) : "All";

        var status = new TestStatus
        {
            RunId = runId,
            Status = "QUEUED",
            Suite = suitesDisplay,
            Environment = request.Environment,
            Browser = request.Browser,
            Project = request.Project,
            StartTime = DateTime.Now,
            TriggeredBy = request.TriggeredBy
        };

        _runStatuses[runId] = status;
        _testQueue.Enqueue(request);

        _logger.LogInformation("Test run {RunId} queued: Suites={Suites}, Env={Env}, Browser={Browser}",
            runId, suitesDisplay, request.Environment, request.Browser);

        return new TestRunResponse
        {
            RunId = runId,
            Status = "QUEUED",
            Message = "Test run queued successfully",
            StartTime = status.StartTime
        };
    }

    public TestStatus? GetStatus(string runId)
    {
        return _runStatuses.TryGetValue(runId, out var status) ? status : null;
    }

    public IEnumerable<TestStatus> GetAllStatuses()
    {
        return _runStatuses.Values.OrderByDescending(s => s.StartTime);
    }

    public IEnumerable<TestStatus> GetRecentRuns(int count = 20)
    {
        return _runStatuses.Values
            .OrderByDescending(s => s.StartTime)
            .Take(count);
    }

    // Cache for test suites per project
    private readonly ConcurrentDictionary<string, (List<TestSuiteInfo> Suites, DateTime LastScanned)> _suitesCache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public List<TestSuiteInfo> GetSuites(string? project = null)
    {
        // Default to ERP if no project specified
        project ??= "ERP";

        // Check cache
        if (_suitesCache.TryGetValue(project, out var cached) && DateTime.Now - cached.LastScanned < _cacheExpiry)
        {
            return cached.Suites;
        }

        // Scan feature files for tags
        var suites = ScanFeatureFilesForTags(project);
        _suitesCache[project] = (suites, DateTime.Now);

        return suites;
    }

    private List<TestSuiteInfo> ScanFeatureFilesForTags(string project)
    {
        var tags = new HashSet<string> { "All" }; // Always include "All"

        var projectPath = project == "ERP"
            ? Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.ERP")
            : Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.OnlinePortal");

        try
        {
            // Scan .feature files for @Tag annotations
            if (Directory.Exists(projectPath))
            {
                var featureFiles = Directory.GetFiles(projectPath, "*.feature", SearchOption.AllDirectories);

                foreach (var file in featureFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file);

                        // Match @TagName patterns (tags start with @ and are alphanumeric)
                        var tagMatches = Regex.Matches(content, @"@(\w+)", RegexOptions.Multiline);

                        foreach (Match match in tagMatches)
                        {
                            var tag = match.Groups[1].Value;

                            // Skip common Gherkin keywords and system tags
                            if (!IsSystemTag(tag))
                            {
                                tags.Add(tag);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to read feature file {File}: {Error}", file, ex.Message);
                    }
                }
            }

            _logger.LogInformation("Scanned {Count} tags for project {Project}", tags.Count, project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan feature files for project {Project}", project);
        }

        // Sort alphabetically and return
        return tags
            .OrderBy(t => t == "All" ? "" : t) // "All" first
            .Select(t => new TestSuiteInfo
            {
                Name = t,
                Description = t == "All" ? "Run all available tests" : t
            })
            .ToList();
    }

    private static bool IsSystemTag(string tag)
    {
        // Skip Gherkin keywords and common system tags
        var systemTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "feature", "scenario", "given", "when", "then", "and", "but",
            "background", "outline", "examples", "ignore", "skip", "wip",
            "debug", "manual", "automated", "regression", "smoke", "sanity"
        };

        return systemTags.Contains(tag) || tag.Length < 3;
    }

    public void RefreshSuitesCache(string? project = null)
    {
        if (project != null)
        {
            _suitesCache.TryRemove(project, out _);
        }
        else
        {
            _suitesCache.Clear();
        }
    }

    public List<EnvironmentInfo> GetEnvironments()
    {
        return _settings.Environments.Select(e => new EnvironmentInfo
        {
            Name = e.Key,
            BaseUrl = e.Value,
            IsActive = true
        }).ToList();
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_testQueue.TryDequeue(out var request))
            {
                await _executionSemaphore.WaitAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteTestRunAsync(request, cancellationToken);
                    }
                    finally
                    {
                        _executionSemaphore.Release();
                    }
                }, cancellationToken);
            }
            else
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task ExecuteTestRunAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        var runId = request.RunId!;
        var status = _runStatuses[runId];

        void AddLog(string message)
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            status.Logs.Add(logEntry);
            _logger.LogInformation("{RunId}: {Message}", runId, message);
        }

        try
        {
            status.Status = "RUNNING";
            status.StartTime = DateTime.Now;
            status.CurrentStep = "Initializing...";
            AddLog("Test run started");

            // Step 1: Git Pull (skip if fails - not critical)
            status.CurrentStep = "Pulling latest code...";
            AddLog("Pulling latest code from Git");
            try
            {
                await GitPullAsync(cancellationToken);
                AddLog("Git pull completed");
            }
            catch (Exception ex)
            {
                AddLog($"Git pull skipped: {ex.Message}");
            }

            // Step 2: Build the project FIRST (copies source appsettings to bin)
            status.CurrentStep = "Building project...";
            AddLog($"Building {request.Project} project");
            await BuildProjectAsync(request.Project, cancellationToken);
            AddLog("Build completed successfully");

            // Step 3: Update appsettings AFTER build (so our changes aren't overwritten)
            status.CurrentStep = "Configuring test settings...";
            AddLog("Updating test configuration");
            await UpdateAppSettingsAsync(request);
            AddLog($"Settings configured: Headless={request.IsHeadless}, Browser={request.Browser}");

            // Step 4: Run Tests
            var suitesDisplay = string.Join(", ", request.Suites);
            status.CurrentStep = $"Running {suitesDisplay} tests...";
            AddLog($"Starting test execution: {suitesDisplay}");
            var testResult = await RunTestsAsync(request, cancellationToken);
            AddLog("Test execution completed");

            // Step 5: Parse Results
            status.CurrentStep = "Parsing test results...";
            AddLog("Parsing test results");
            ParseTestResults(status, testResult);
            AddLog($"Results: {status.PassedTests} passed, {status.FailedTests} failed, {status.SkippedTests} skipped");

            // Step 6: Generate HTML Report
            status.CurrentStep = "Generating report...";
            AddLog("Generating HTML report");
            await GenerateReportAsync(request, status);
            status.ReportUrl = $"/automation/report/{runId}";
            AddLog("Report generated successfully");

            status.EndTime = DateTime.Now;
            status.Duration = (status.EndTime.Value - status.StartTime).ToString(@"mm\:ss");
            status.Status = status.FailedTests > 0 ? "FAILED" : "PASSED";
            status.CurrentStep = status.Status == "PASSED" ? "Completed successfully" : "Completed with failures";
            AddLog($"Test run completed: {status.Status}");
        }
        catch (Exception ex)
        {
            status.Status = "ERROR";
            status.ErrorMessage = ex.Message;
            status.CurrentStep = "Error occurred";
            status.EndTime = DateTime.Now;
            if (status.StartTime != default)
            {
                status.Duration = (status.EndTime.Value - status.StartTime).ToString(@"mm\:ss");
            }
            AddLog($"Error: {ex.Message}");
            _logger.LogError(ex, "Test run {RunId} failed with error: {Message}", runId, ex.Message);
        }
    }

    private async Task GitPullAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pulling latest code from Git");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "pull origin main",
                WorkingDirectory = _settings.SolutionPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && !error.Contains("Already up to date"))
        {
            _logger.LogWarning("Git pull warning: {Error}", error);
        }
    }

    private async Task UpdateAppSettingsAsync(TestRunRequest request)
    {
        var projectPath = request.Project == "ERP"
            ? Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.ERP")
            : Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.OnlinePortal");

        var baseUrlToUse = !string.IsNullOrWhiteSpace(request.BaseUrl)
            ? request.BaseUrl
            : _settings.Environments.TryGetValue(request.Environment, out var envUrl) ? envUrl : "https://ums-1.osl.team";

        var username = !string.IsNullOrWhiteSpace(request.Username) ? request.Username : "rumon.onnorokom@gmail.com";
        var password = !string.IsNullOrWhiteSpace(request.Password) ? request.Password : "Mrumon4726";

        // Write complete appsettings.json content
        var appSettingsContent = $@"{{
  ""Settings"": {{
    ""BaseUrl"": ""{baseUrlToUse}"",
    ""IsHeadless"": {request.IsHeadless.ToString().ToLower()}
  }},
  ""Admin"": {{
    ""Username"": ""{username}"",
    ""Password"": ""{password}"",
    ""MobileNo"": """"
  }},
  ""TeacherPayment"": {{
    ""ClassPaymentUsedb"": ""true""
  }}
}}";

        // Update bin/Release folder (this is what tests use with --configuration Release)
        var binReleasePath = Path.Combine(projectPath, "bin", "Release", "net8.0", "appsettings.json");

        _logger.LogInformation("Writing appsettings to: {Path}", binReleasePath);
        _logger.LogInformation("IsHeadless value: {Value}", request.IsHeadless);

        try
        {
            await File.WriteAllTextAsync(binReleasePath, appSettingsContent);
            _logger.LogInformation("Successfully wrote appsettings.json with IsHeadless={Headless}", request.IsHeadless);

            // Verify the write
            var verify = await File.ReadAllTextAsync(binReleasePath);
            _logger.LogInformation("Verification - file contains: {Content}", verify.Substring(0, Math.Min(200, verify.Length)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write appsettings.json: {Message}", ex.Message);
        }
    }

    private async Task BuildProjectAsync(string project, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building project: {Project}", project);

        var projectPath = project == "ERP"
            ? Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.ERP", "UMS.UI.Test.ERP.csproj")
            : Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.OnlinePortal", "UMS.UI.Test.OnlinePortal.csproj");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" --configuration Release",
                WorkingDirectory = _settings.SolutionPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Build failed: {Error}", error);
            throw new Exception($"Build failed: {error}");
        }

        _logger.LogInformation("Build completed successfully");
    }

    private async Task<string> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running tests: Suites={Suites}, Browser={Browser}", string.Join(",", request.Suites), request.Browser);

        var projectPath = request.Project == "ERP"
            ? Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.ERP", "UMS.UI.Test.ERP.csproj")
            : Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.OnlinePortal", "UMS.UI.Test.OnlinePortal.csproj");

        var resultsPath = Path.Combine(_settings.ResultsPath, request.RunId!);
        Directory.CreateDirectory(resultsPath);

        var filter = BuildTestFilter(request);

        var arguments = new StringBuilder();
        arguments.Append($"test \"{projectPath}\" ");
        arguments.Append("--configuration Release ");
        arguments.Append("--no-build ");

        if (!string.IsNullOrEmpty(filter))
        {
            arguments.Append($"--filter \"{filter}\" ");
        }

        arguments.Append($"--logger \"trx;LogFileName=results.trx\" ");
        arguments.Append($"--logger \"html;LogFileName=results.html\" ");
        arguments.Append($"--results-directory \"{resultsPath}\" ");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments.ToString(),
                WorkingDirectory = _settings.SolutionPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = false
            }
        };

        process.StartInfo.EnvironmentVariables["TEST_ENVIRONMENT"] = request.Environment;
        process.StartInfo.EnvironmentVariables["TEST_BROWSER"] = request.Browser;
        process.StartInfo.EnvironmentVariables["TEST_RUN_ID"] = request.RunId!;
        process.StartInfo.EnvironmentVariables["IS_HEADLESS"] = request.IsHeadless.ToString();

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(resultsPath, "output.log"), output + "\n" + error);

        _logger.LogInformation("Test execution completed with exit code: {ExitCode}", process.ExitCode);

        return output + "\n" + error;
    }

    private string BuildTestFilter(TestRunRequest request)
    {
        var filters = new List<string>();

        // Handle multiple suites with OR logic
        var validSuites = request.Suites
            .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("all", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (validSuites.Count > 0)
        {
            // Multiple categories: (Category=Suite1|Category=Suite2|Category=Suite3)
            var categoryFilters = validSuites.Select(s => $"Category={s}");
            filters.Add($"({string.Join("|", categoryFilters)})");
        }

        if (!string.IsNullOrEmpty(request.SpecificCategory))
        {
            filters.Add($"Category={request.SpecificCategory}");
        }

        if (request.RetryFailed)
        {
            filters.Add("Outcome=Failed");
        }

        return filters.Count > 0 ? string.Join("&", filters) : "";
    }

    private void ParseTestResults(TestStatus status, string output)
    {
        var passedMatch = Regex.Match(output, @"Passed:\s*(\d+)");
        var failedMatch = Regex.Match(output, @"Failed:\s*(\d+)");
        var skippedMatch = Regex.Match(output, @"Skipped:\s*(\d+)");
        var totalMatch = Regex.Match(output, @"Total:\s*(\d+)");

        status.PassedTests = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value) : 0;
        status.FailedTests = failedMatch.Success ? int.Parse(failedMatch.Groups[1].Value) : 0;
        status.SkippedTests = skippedMatch.Success ? int.Parse(skippedMatch.Groups[1].Value) : 0;
        status.TotalTests = totalMatch.Success ? int.Parse(totalMatch.Groups[1].Value) :
            status.PassedTests + status.FailedTests + status.SkippedTests;

        var trxPath = Path.Combine(_settings.ResultsPath, status.RunId, "results.trx");
        if (File.Exists(trxPath))
        {
            ParseTrxFile(status, trxPath);
        }
    }

    private void ParseTrxFile(TestStatus status, string trxPath)
    {
        try
        {
            var xml = File.ReadAllText(trxPath);

            // Parse each UnitTestResult with its details
            var resultPattern = @"<UnitTestResult[^>]*executionId=""([^""]+)""[^>]*testId=""([^""]+)""[^>]*testName=""([^""]+)""[^>]*" +
                               @"(?:computerName=""([^""]*)""\s*)?duration=""([^""]+)""[^>]*" +
                               @"(?:startTime=""([^""]*)""\s*)?(?:endTime=""([^""]*)""\s*)?" +
                               @"outcome=""([^""]+)""[^>]*>(.*?)</UnitTestResult>";

            var resultMatches = Regex.Matches(xml, resultPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in resultMatches)
            {
                var testName = match.Groups[3].Value;
                var duration = match.Groups[5].Value;
                var outcome = match.Groups[8].Value;
                var innerContent = match.Groups[9].Value;

                // Parse duration to seconds
                double durationSeconds = 0;
                if (TimeSpan.TryParse(duration, out var ts))
                {
                    durationSeconds = ts.TotalSeconds;
                }

                // Extract error message and stack trace for failed tests
                string? errorMessage = null;
                string? stackTrace = null;

                if (outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to extract error message
                    var messageMatch = Regex.Match(innerContent, @"<Message>(.*?)</Message>", RegexOptions.Singleline);
                    if (messageMatch.Success)
                    {
                        errorMessage = System.Net.WebUtility.HtmlDecode(messageMatch.Groups[1].Value.Trim());
                    }

                    // Try to extract stack trace
                    var stackTraceMatch = Regex.Match(innerContent, @"<StackTrace>(.*?)</StackTrace>", RegexOptions.Singleline);
                    if (stackTraceMatch.Success)
                    {
                        stackTrace = System.Net.WebUtility.HtmlDecode(stackTraceMatch.Groups[1].Value.Trim());
                    }
                }

                // Extract class name from test name (usually format: Namespace.ClassName.MethodName)
                var className = "";
                var parts = testName.Split('.');
                if (parts.Length >= 2)
                {
                    className = parts[^2]; // Second to last part
                }

                status.TestCases.Add(new TestCaseResult
                {
                    Name = testName,
                    ClassName = className,
                    Status = outcome,
                    Duration = duration,
                    DurationSeconds = durationSeconds,
                    ErrorMessage = errorMessage,
                    StackTrace = stackTrace
                });
            }

            // If the pattern didn't match, fall back to simpler parsing
            if (status.TestCases.Count == 0)
            {
                var simplePattern = @"<UnitTestResult[^>]*testName=""([^""]+)""[^>]*outcome=""([^""]+)""[^>]*duration=""([^""]+)""";
                var simpleMatches = Regex.Matches(xml, simplePattern, RegexOptions.IgnoreCase);

                foreach (Match match in simpleMatches)
                {
                    status.TestCases.Add(new TestCaseResult
                    {
                        Name = match.Groups[1].Value,
                        Status = match.Groups[2].Value,
                        Duration = match.Groups[3].Value
                    });
                }
            }

            // Try to find screenshots in TestFiles\Reports folder (where automation framework saves them)
            // Screenshots are named: {ScenarioTitle}-{timestamp}.png
            var projectFolder = status.Project == "ERP"
                ? Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.ERP")
                : Path.Combine(_settings.SolutionPath, "UMS.UI.Test", "UMS.UI.Test.OnlinePortal");

            var screenshotsPaths = new[]
            {
                Path.Combine(projectFolder, "TestFiles", "Reports"),
                Path.Combine(projectFolder, "bin", "Release", "net8.0", "TestFiles", "Reports"),
                Path.Combine(projectFolder, "bin", "Debug", "net8.0", "TestFiles", "Reports"),
                Path.Combine(_settings.ResultsPath, status.RunId) // Also check test results folder
            };

            var screenshotFiles = new List<string>();
            foreach (var screenshotsPath in screenshotsPaths)
            {
                if (Directory.Exists(screenshotsPath))
                {
                    screenshotFiles.AddRange(Directory.GetFiles(screenshotsPath, "*.png", SearchOption.AllDirectories));
                    screenshotFiles.AddRange(Directory.GetFiles(screenshotsPath, "*.jpg", SearchOption.AllDirectories));
                }
            }

            // Get only recent screenshots (within last 30 minutes to avoid old screenshots)
            var recentScreenshots = screenshotFiles
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime > DateTime.Now.AddMinutes(-30))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            foreach (var testCase in status.TestCases.Where(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)))
            {
                // Extract test method name (last part after the dot)
                // Example TRX name: "UMS.UI.Test.ERP.Areas.Admin.AdminDashboard.Steps.AdminDashboardStep.ThenAdminDashboardPageTest"
                // Screenshot name: "Admin Dashboard page Test-0671.png"
                var testMethodName = testCase.Name.Split('.').Last();

                // Normalize test name for matching: remove common suffixes, convert to lowercase
                var normalizedTestName = testMethodName
                    .Replace("Test", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Step", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Then", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("When", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Given", "", StringComparison.OrdinalIgnoreCase)
                    .ToLower();

                // Try to match screenshot by scenario/test name
                // Screenshots are named: {ScenarioTitle}-{timestamp}.png (e.g., "Admin Dashboard page Test-0671.png")
                var screenshot = recentScreenshots.FirstOrDefault(f =>
                {
                    // Get filename without timestamp suffix (e.g., "Admin Dashboard page Test" from "Admin Dashboard page Test-0671.png")
                    var fileName = Path.GetFileNameWithoutExtension(f.Name);
                    var lastDash = fileName.LastIndexOf('-');
                    if (lastDash > 0 && fileName.Length - lastDash <= 5) // Timestamp is usually 4 digits
                    {
                        fileName = fileName.Substring(0, lastDash);
                    }

                    // Normalize screenshot name for comparison
                    var normalizedScreenshot = fileName
                        .Replace(" ", "")
                        .Replace("-", "")
                        .Replace("_", "")
                        .Replace("Test", "", StringComparison.OrdinalIgnoreCase)
                        .ToLower();

                    // Check for partial match
                    return normalizedScreenshot.Contains(normalizedTestName) ||
                           normalizedTestName.Contains(normalizedScreenshot) ||
                           // Also check if key words match
                           GetKeywords(testMethodName).Any(kw => fileName.Contains(kw, StringComparison.OrdinalIgnoreCase));
                });

                if (screenshot != null)
                {
                    testCase.ScreenshotPath = screenshot.FullName;
                    try
                    {
                        var bytes = File.ReadAllBytes(screenshot.FullName);
                        testCase.ScreenshotBase64 = Convert.ToBase64String(bytes);
                        _logger.LogInformation("Found screenshot for {TestName}: {Path}", testCase.Name, screenshot.FullName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to read screenshot {Path}: {Error}", screenshot.FullName, ex.Message);
                    }
                }
            }

            // If no specific matches, try to attach most recent screenshot to first failed test
            if (!status.TestCases.Any(t => !string.IsNullOrEmpty(t.ScreenshotBase64)) && recentScreenshots.Any())
            {
                var firstFailed = status.TestCases.FirstOrDefault(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
                if (firstFailed != null)
                {
                    var mostRecent = recentScreenshots.First();
                    firstFailed.ScreenshotPath = mostRecent.FullName;
                    try
                    {
                        var bytes = File.ReadAllBytes(mostRecent.FullName);
                        firstFailed.ScreenshotBase64 = Convert.ToBase64String(bytes);
                        _logger.LogInformation("Attached most recent screenshot to first failed test: {Path}", mostRecent.FullName);
                    }
                    catch { /* Ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse TRX file");
        }
    }

    // Extract keywords from camelCase or PascalCase test names for matching
    private static List<string> GetKeywords(string testName)
    {
        var keywords = new List<string>();

        // Split by common delimiters
        var parts = Regex.Split(testName, @"(?=[A-Z])|[-_\s]")
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Length > 2)
            .ToList();

        keywords.AddRange(parts);

        // Also add combined consecutive words (e.g., "AdminDashboard" -> "Admin Dashboard")
        for (int i = 0; i < parts.Count - 1; i++)
        {
            keywords.Add(parts[i] + " " + parts[i + 1]);
        }

        // Filter out common test-related words
        var excludeWords = new[] { "test", "step", "then", "when", "given", "and", "page", "the" };
        return keywords
            .Where(k => !excludeWords.Contains(k.ToLower()))
            .Distinct()
            .ToList();
    }

    private async Task GenerateReportAsync(TestRunRequest request, TestStatus status)
    {
        var reportDir = Path.Combine(_settings.ResultsPath, request.RunId!, "reports");
        Directory.CreateDirectory(reportDir);

        var reportPath = Path.Combine(reportDir, "report.html");
        var html = GenerateHtmlReport(status);
        await File.WriteAllTextAsync(reportPath, html);

        var jsonPath = Path.Combine(reportDir, "result.json");
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json);

        _logger.LogInformation("Reports generated at: {Path}", reportDir);
    }

    private string GenerateHtmlReport(TestStatus status)
    {
        var statusColor = status.Status switch
        {
            "PASSED" => "#28a745",
            "FAILED" => "#dc3545",
            "RUNNING" => "#ffc107",
            _ => "#6c757d"
        };

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Test Report - {status.RunId}</title>
    <link href=""https://cdn.jsdelivr.net/npm/bootstrap@5.3.2/dist/css/bootstrap.min.css"" rel=""stylesheet"">
    <style>
        body {{ background: #f5f5f5; padding: 20px; }}
        .header {{ background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%); color: white; padding: 30px; border-radius: 10px; margin-bottom: 20px; }}
        .status-badge {{ display: inline-block; padding: 8px 20px; border-radius: 20px; font-weight: bold; background: {statusColor}; color: white; margin-top: 15px; }}
        .stat-card {{ background: white; padding: 25px; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); text-align: center; }}
        .stat-value {{ font-size: 32px; font-weight: bold; }}
        .passed {{ color: #28a745; }}
        .failed {{ color: #dc3545; }}
        .skipped {{ color: #ffc107; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h1>Test Execution Report</h1>
            <div>Run ID: {status.RunId}</div>
            <div class=""status-badge"">{status.Status}</div>
        </div>
        <div class=""row mb-4"">
            <div class=""col-md-3""><div class=""stat-card""><h6>Total Tests</h6><div class=""stat-value"">{status.TotalTests}</div></div></div>
            <div class=""col-md-3""><div class=""stat-card""><h6>Passed</h6><div class=""stat-value passed"">{status.PassedTests}</div></div></div>
            <div class=""col-md-3""><div class=""stat-card""><h6>Failed</h6><div class=""stat-value failed"">{status.FailedTests}</div></div></div>
            <div class=""col-md-3""><div class=""stat-card""><h6>Skipped</h6><div class=""stat-value skipped"">{status.SkippedTests}</div></div></div>
        </div>
        <div class=""card"">
            <div class=""card-body"">
                <h5>Run Details</h5>
                <table class=""table"">
                    <tr><td>Suite</td><td>{status.Suite}</td></tr>
                    <tr><td>Environment</td><td>{status.Environment}</td></tr>
                    <tr><td>Browser</td><td>{status.Browser}</td></tr>
                    <tr><td>Project</td><td>{status.Project}</td></tr>
                    <tr><td>Triggered By</td><td>{status.TriggeredBy ?? "System"}</td></tr>
                    <tr><td>Start Time</td><td>{status.StartTime:yyyy-MM-dd HH:mm:ss}</td></tr>
                    <tr><td>End Time</td><td>{status.EndTime:yyyy-MM-dd HH:mm:ss}</td></tr>
                    <tr><td>Duration</td><td>{status.Duration}</td></tr>
                </table>
            </div>
        </div>
    </div>
</body>
</html>";
    }
}
