using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;
using QAToolkit.Services;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class ScheduledRunsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ScheduledRunsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var schedules = await _context.ScheduledRuns
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var scriptIds = schedules.Select(s => s.ScriptId).Distinct().ToList();
            var scripts = await _context.PlaywrightScripts
                .Where(s => scriptIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            ViewBag.ScriptNames = scripts;
            return View(schedules);
        }

        public async Task<IActionResult> Create(int? scriptId)
        {
            ViewBag.Scripts = await _context.PlaywrightScripts
                .OrderBy(s => s.Name)
                .ToListAsync();
            ViewBag.PreselectedScriptId = scriptId;

            // Pre-fill ParamsJson from the script if available
            string? paramsJson = null;
            if (scriptId.HasValue)
            {
                var script = await _context.PlaywrightScripts.FindAsync(scriptId.Value);
                if (script?.ScriptContent.Contains("__PARAMS__") == true)
                {
                    // Leave empty — user will fill it in
                }
            }
            ViewBag.ParamsJson = paramsJson;
            return View("CreateEdit", (ScheduledRun?)null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            int scriptId, string name, string scheduleType,
            DateTime? runOnce, string? dailyTime, int? intervalMinutes,
            string? paramsJson)
        {
            if (!await ValidateScheduleInputAsync(scriptId, name, scheduleType, runOnce, dailyTime, intervalMinutes))
                return await ReturnCreateEditView(null, scriptId);

            var now = DateTimeHelper.BdNow;
            var schedule = new ScheduledRun
            {
                ScriptId = scriptId,
                Name = name.Trim(),
                ScheduleType = scheduleType,
                RunOnce = runOnce,
                DailyTime = dailyTime,
                IntervalMinutes = intervalMinutes,
                ParamsJson = string.IsNullOrWhiteSpace(paramsJson) ? null : paramsJson.Trim(),
                IsEnabled = true,
                CreatedBy = User.Identity?.Name,
                CreatedAt = now
            };

            schedule.NextRunAt = scheduleType == "once"
                ? runOnce
                : SchedulerHostedService.CalculateNextRun(schedule, now);

            _context.ScheduledRuns.Add(schedule);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Schedule \"{schedule.Name}\" created. Next run: {schedule.NextRunAt?.ToString("dd MMM yyyy HH:mm") ?? "N/A"}";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            ViewBag.Scripts = await _context.PlaywrightScripts.OrderBy(s => s.Name).ToListAsync();
            ViewBag.PreselectedScriptId = schedule.ScriptId;
            return View("CreateEdit", schedule);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id, int scriptId, string name, string scheduleType,
            DateTime? runOnce, string? dailyTime, int? intervalMinutes,
            string? paramsJson)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            if (!await ValidateScheduleInputAsync(scriptId, name, scheduleType, runOnce, dailyTime, intervalMinutes))
                return await ReturnCreateEditView(schedule, scriptId);

            var now = DateTimeHelper.BdNow;
            schedule.ScriptId = scriptId;
            schedule.Name = name.Trim();
            schedule.ScheduleType = scheduleType;
            schedule.RunOnce = runOnce;
            schedule.DailyTime = dailyTime;
            schedule.IntervalMinutes = intervalMinutes;
            schedule.ParamsJson = string.IsNullOrWhiteSpace(paramsJson) ? null : paramsJson.Trim();
            schedule.UpdatedAt = now;
            schedule.NextRunAt = scheduleType == "once"
                ? runOnce
                : SchedulerHostedService.CalculateNextRun(schedule, now);

            // Re-enable if it was a completed one-time run being reset
            if (scheduleType == "once" && runOnce > now)
                schedule.IsEnabled = true;

            _context.ScheduledRuns.Update(schedule);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Schedule updated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule != null)
            {
                // Delete logs first
                var logs = _context.ScheduledRunLogs.Where(l => l.ScheduledRunId == id);
                _context.ScheduledRunLogs.RemoveRange(logs);
                _context.ScheduledRuns.Remove(schedule);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Schedule deleted.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            schedule.NextRunAt = DateTimeHelper.BdNow;
            schedule.IsEnabled = true;
            _context.ScheduledRuns.Update(schedule);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"\"{schedule.Name}\" queued — will run within 30 seconds.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearLogs(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            var logs = _context.ScheduledRunLogs.Where(l => l.ScheduledRunId == id);
            _context.ScheduledRunLogs.RemoveRange(logs);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Run logs cleared.";
            return RedirectToAction(nameof(Logs), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            schedule.IsEnabled = !schedule.IsEnabled;

            // Recalculate NextRunAt when re-enabling
            if (schedule.IsEnabled && schedule.NextRunAt == null && schedule.ScheduleType != "once")
                schedule.NextRunAt = SchedulerHostedService.CalculateNextRun(schedule, DateTimeHelper.BdNow);

            // If re-enabling a one-time run that already fired, clear the status so it can fire again
            if (schedule.IsEnabled && schedule.ScheduleType == "once" && schedule.LastRunAt != null)
                schedule.NextRunAt = schedule.RunOnce;

            _context.ScheduledRuns.Update(schedule);
            await _context.SaveChangesAsync();

            TempData["Success"] = schedule.IsEnabled ? "Schedule enabled." : "Schedule disabled.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Logs(int id)
        {
            var schedule = await _context.ScheduledRuns.FindAsync(id);
            if (schedule == null) return NotFound();

            var script = await _context.PlaywrightScripts.FindAsync(schedule.ScriptId);
            ViewBag.ScriptName = script?.Name ?? "(deleted)";
            ViewBag.Schedule = schedule;

            var logs = await _context.ScheduledRunLogs
                .Where(l => l.ScheduledRunId == id)
                .OrderByDescending(l => l.StartedAt)
                .Take(50)
                .ToListAsync();

            return View(logs);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private async Task<bool> ValidateScheduleInputAsync(
            int scriptId, string name, string scheduleType,
            DateTime? runOnce, string? dailyTime, int? intervalMinutes)
        {
            if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = "Schedule name is required."; return false; }

            var script = await _context.PlaywrightScripts.FindAsync(scriptId);
            if (script == null) { TempData["Error"] = "Selected script not found."; return false; }

            if (scheduleType == "once" && runOnce == null) { TempData["Error"] = "Please specify when to run."; return false; }
            if (scheduleType == "daily" && string.IsNullOrEmpty(dailyTime)) { TempData["Error"] = "Please specify a daily time."; return false; }
            if (scheduleType == "interval" && (intervalMinutes == null || intervalMinutes < 1)) { TempData["Error"] = "Interval must be at least 1 minute."; return false; }

            return true;
        }

        private async Task<IActionResult> ReturnCreateEditView(ScheduledRun? existing, int scriptId)
        {
            ViewBag.Scripts = await _context.PlaywrightScripts.OrderBy(s => s.Name).ToListAsync();
            ViewBag.PreselectedScriptId = scriptId;
            return View("CreateEdit", existing);
        }
    }
}
