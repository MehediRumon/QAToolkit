using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Services
{
    public class SchedulerHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SchedulerHostedService> _logger;

        public SchedulerHostedService(IServiceScopeFactory scopeFactory, ILogger<SchedulerHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Scheduler started.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CheckAndRunDueSchedules(stoppingToken); }
                catch (Exception ex) { _logger.LogError(ex, "Scheduler tick error."); }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        private async Task CheckAndRunDueSchedules(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = DateTimeHelper.BdNow;
            var due = await db.ScheduledRuns
                .Where(s => s.IsEnabled
                    && s.LastRunStatus != "running"
                    && s.NextRunAt != null
                    && s.NextRunAt <= now)
                .ToListAsync(ct);

            foreach (var schedule in due)
            {
                // Mark as running immediately to prevent duplicate execution
                schedule.LastRunStatus = "running";
                await db.SaveChangesAsync(ct);

                // Fire-and-forget with its own scope
                _ = Task.Run(() => ExecuteSchedule(schedule.Id, schedule.ParamsJson), ct);
            }
        }

        private async Task ExecuteSchedule(int scheduleId, string? paramsJson)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var runner = scope.ServiceProvider.GetRequiredService<PlaywrightRunnerService>();

            var schedule = await db.ScheduledRuns.FindAsync(scheduleId);
            if (schedule == null) return;

            var log = new ScheduledRunLog
            {
                ScheduledRunId = scheduleId,
                StartedAt = DateTimeHelper.BdNow
            };
            db.ScheduledRunLogs.Add(log);
            await db.SaveChangesAsync();

            try
            {
                var result = await runner.RunAsync(schedule.ScriptId, paramsJson);

                log.FinishedAt = DateTimeHelper.BdNow;
                log.DurationMs = result.DurationMs;
                log.ExitCode = result.ExitCode;
                log.Output = result.Output;
                log.Status = result.TimedOut ? "timeout" : (result.Success ? "ok" : "error");

                var now = DateTimeHelper.BdNow;
                schedule.LastRunAt = now;
                schedule.LastRunStatus = log.Status;
                schedule.NextRunAt = CalculateNextRun(schedule, now);

                // Disable one-time schedules after they run
                if (schedule.ScheduleType == "once")
                    schedule.IsEnabled = false;

                await db.SaveChangesAsync();

                // Trim old logs — keep last 50 per schedule
                var oldLogs = await db.ScheduledRunLogs
                    .Where(l => l.ScheduledRunId == scheduleId)
                    .OrderByDescending(l => l.StartedAt)
                    .Skip(50)
                    .ToListAsync();
                if (oldLogs.Count > 0)
                {
                    db.ScheduledRunLogs.RemoveRange(oldLogs);
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running schedule {ScheduleId}", scheduleId);
                try
                {
                    log.FinishedAt = DateTimeHelper.BdNow;
                    log.Status = "error";
                    log.Output = $"[ERROR] {ex.Message}";
                    schedule.LastRunAt = DateTimeHelper.BdNow;
                    schedule.LastRunStatus = "error";
                    schedule.NextRunAt = CalculateNextRun(schedule, DateTimeHelper.BdNow);
                    if (schedule.ScheduleType == "once") schedule.IsEnabled = false;
                    await db.SaveChangesAsync();
                }
                catch { }
            }
        }

        public static DateTime? CalculateNextRun(ScheduledRun schedule, DateTime fromNow)
        {
            switch (schedule.ScheduleType)
            {
                case "once":
                    return null;

                case "daily":
                    if (string.IsNullOrEmpty(schedule.DailyTime)) return null;
                    var parts = schedule.DailyTime.Split(':');
                    if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
                        return null;
                    var todayAt = new DateTime(fromNow.Year, fromNow.Month, fromNow.Day, h, m, 0);
                    // If today's slot has passed, schedule for tomorrow
                    return todayAt > fromNow ? todayAt : todayAt.AddDays(1);

                case "interval":
                    if (schedule.IntervalMinutes == null || schedule.IntervalMinutes <= 0) return null;
                    return fromNow.AddMinutes(schedule.IntervalMinutes.Value);

                default:
                    return null;
            }
        }
    }
}
