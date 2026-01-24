using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QAToolkit.Models.Automation;

namespace QAToolkit.Services;

/// <summary>
/// Background service that polls a remote server for pending test runs,
/// executes them locally, and reports results back.
/// Only runs when IsRemoteRunner is true in settings.
/// </summary>
public class RemoteTestRunnerService : BackgroundService
{
    private readonly ILogger<RemoteTestRunnerService> _logger;
    private readonly TestRunnerSettings _settings;
    private readonly TestExecutionService _executionService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _runnerId;

    public RemoteTestRunnerService(
        ILogger<RemoteTestRunnerService> logger,
        IOptions<TestRunnerSettings> settings,
        TestExecutionService executionService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settings = settings.Value;
        _executionService = executionService;
        _httpClientFactory = httpClientFactory;
        _runnerId = $"runner-{Environment.MachineName}-{Guid.NewGuid().ToString()[..8]}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only run if configured as remote runner
        if (!_settings.IsRemoteRunner || string.IsNullOrEmpty(_settings.RemoteServerUrl))
        {
            _logger.LogInformation("Remote runner mode is disabled. Service will not poll for remote tests.");
            return;
        }

        _logger.LogInformation("Remote Test Runner started. Polling {Url} every {Interval}s",
            _settings.RemoteServerUrl, _settings.PollingIntervalSeconds);

        // Sync suites to remote server on startup
        await SyncSuitesToRemoteAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndExecuteAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in remote runner polling cycle");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.PollingIntervalSeconds), stoppingToken);
        }
    }

    private async Task SyncSuitesToRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateHttpClient();

            // Sync ERP suites
            var erpSuites = _executionService.GetSuites("ERP");
            if (erpSuites.Count > 1)
            {
                var response = await client.PostAsJsonAsync("/api/automation/runner/sync-suites/ERP", erpSuites, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Synced {Count} ERP suites to remote server", erpSuites.Count);
                }
            }

            // Sync OnlinePortal suites
            var portalSuites = _executionService.GetSuites("OnlinePortal");
            if (portalSuites.Count > 1)
            {
                var response = await client.PostAsJsonAsync("/api/automation/runner/sync-suites/OnlinePortal", portalSuites, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Synced {Count} OnlinePortal suites to remote server", portalSuites.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to sync suites to remote server: {Message}", ex.Message);
        }
    }

    private async Task PollAndExecuteAsync(CancellationToken cancellationToken)
    {
        var client = CreateHttpClient();

        try
        {
            // Step 1: Get pending test runs
            var pendingRuns = await GetPendingRunsAsync(client, cancellationToken);

            if (pendingRuns == null || !pendingRuns.Any())
            {
                _logger.LogDebug("No pending test runs found");
                return;
            }

            _logger.LogInformation("Found {Count} pending test run(s)", pendingRuns.Count());

            // Step 2: Claim and execute each run (one at a time)
            foreach (var pendingRun in pendingRuns)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var runId = pendingRun.RunId;
                if (string.IsNullOrEmpty(runId))
                    continue;

                // Try to claim the run
                var claimedRun = await ClaimRunAsync(client, runId, cancellationToken);
                if (claimedRun == null)
                {
                    _logger.LogDebug("Could not claim run {RunId} - may be already claimed", runId);
                    continue;
                }

                _logger.LogInformation("Claimed test run {RunId}", runId);

                // Execute the test run
                await ExecuteAndReportAsync(client, claimedRun, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Failed to connect to remote server: {Message}", ex.Message);
        }
    }

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient("RemoteRunner");
        client.BaseAddress = new Uri(_settings.RemoteServerUrl!);
        client.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrEmpty(_settings.RunnerApiKey))
        {
            client.DefaultRequestHeaders.Add("X-Runner-Key", _settings.RunnerApiKey);
        }
        client.DefaultRequestHeaders.Add("X-Runner-Id", _runnerId);

        return client;
    }

    private async Task<IEnumerable<TestRunRequest>?> GetPendingRunsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync("/api/automation/runner/pending", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to get pending runs: {Status}", response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<IEnumerable<TestRunRequest>>(cancellationToken: cancellationToken);
    }

    private async Task<TestRunRequest?> ClaimRunAsync(HttpClient client, string runId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"/api/automation/runner/claim/{runId}", null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<TestRunRequest>(cancellationToken: cancellationToken);
    }

    private async Task ExecuteAndReportAsync(HttpClient client, TestRunRequest request, CancellationToken cancellationToken)
    {
        var runId = request.RunId!;

        try
        {
            // Update status to RUNNING
            await UpdateRemoteStatusAsync(client, runId, new TestStatus
            {
                RunId = runId,
                Status = "RUNNING",
                Suite = string.Join(", ", request.Suites),
                Environment = request.Environment,
                Browser = request.Browser,
                Project = request.Project,
                StartTime = DateTime.Now,
                CurrentStep = "Starting execution on remote runner..."
            }, cancellationToken);

            // Queue the test run locally
            _logger.LogInformation("Starting local execution of test run {RunId}", runId);
            var response = _executionService.QueueTestRun(request);

            // Poll local status and update remote
            await MonitorAndReportStatusAsync(client, runId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute test run {RunId}", runId);

            // Report error to remote
            await UpdateRemoteStatusAsync(client, runId, new TestStatus
            {
                RunId = runId,
                Status = "ERROR",
                ErrorMessage = ex.Message,
                EndTime = DateTime.Now
            }, cancellationToken);
        }
    }

    private async Task MonitorAndReportStatusAsync(HttpClient client, string runId, CancellationToken cancellationToken)
    {
        var lastReportedStep = "";
        var pollingInterval = TimeSpan.FromSeconds(5); // Update remote every 5 seconds

        while (!cancellationToken.IsCancellationRequested)
        {
            var localStatus = _executionService.GetStatus(runId);

            if (localStatus == null)
            {
                _logger.LogWarning("Lost track of local test run {RunId}", runId);
                break;
            }

            // Report status update if step changed or test completed
            if (localStatus.CurrentStep != lastReportedStep ||
                localStatus.Status == "PASSED" ||
                localStatus.Status == "FAILED" ||
                localStatus.Status == "ERROR")
            {
                await UpdateRemoteStatusAsync(client, runId, localStatus, cancellationToken);
                lastReportedStep = localStatus.CurrentStep;
            }

            // Check if execution is complete
            if (localStatus.Status == "PASSED" || localStatus.Status == "FAILED" || localStatus.Status == "ERROR")
            {
                _logger.LogInformation("Test run {RunId} completed with status: {Status}", runId, localStatus.Status);
                break;
            }

            await Task.Delay(pollingInterval, cancellationToken);
        }
    }

    private async Task UpdateRemoteStatusAsync(HttpClient client, string runId, TestStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.PutAsJsonAsync($"/api/automation/runner/status/{runId}", status, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to update remote status for {RunId}: {Status}", runId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating remote status for {RunId}: {Message}", runId, ex.Message);
        }
    }
}
