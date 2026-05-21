using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;
using QAToolkit.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class PlaywrightScriptsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly PlaywrightRunnerService _runner;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _activeRuns = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _stoppedRuns = new();

        public PlaywrightScriptsController(ApplicationDbContext context, PlaywrightRunnerService runner)
        {
            _context = context;
            _runner = runner;
        }

        public async Task<IActionResult> Index(string? tag)
        {
            var query = _context.PlaywrightScripts.AsQueryable();

            if (!string.IsNullOrEmpty(tag))
                query = query.Where(s => s.Tags != null && s.Tags.Contains(tag));

            var allTagsRaw = await _context.PlaywrightScripts
                .Where(s => s.Tags != null && s.Tags != "")
                .Select(s => s.Tags!)
                .ToListAsync();

            ViewBag.AllTags = allTagsRaw
                .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            ViewBag.SelectedTag = tag;

            return View(await query.OrderByDescending(s => s.CreatedAt).ToListAsync());
        }

        [AllowAnonymous]
        public IActionResult ScriptGuide() => View();

        [AllowAnonymous]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var script = await _context.PlaywrightScripts.FirstOrDefaultAsync(s => s.Id == id);
            if (script == null) return NotFound();

            if (!script.IsPublic && User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");

            return View(script);
        }

        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string? description, string? tags, string? runMode, bool isPublic, IFormFile? scriptFile, string? scriptContent, string? scriptExt)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Error"] = "Script name is required.";
                return View();
            }

            string content;
            string ext;

            if (scriptFile != null && scriptFile.Length > 0)
            {
                ext = Path.GetExtension(scriptFile.FileName).ToLowerInvariant();
                if (ext != ".js" && ext != ".ts")
                {
                    TempData["Error"] = "Only .js or .ts files are allowed.";
                    return View();
                }
                using var reader = new StreamReader(scriptFile.OpenReadStream());
                content = await reader.ReadToEndAsync();
            }
            else if (!string.IsNullOrWhiteSpace(scriptContent))
            {
                ext = (scriptExt == ".ts") ? ".ts" : ".js";
                content = scriptContent;
            }
            else
            {
                TempData["Error"] = "Please upload a file or paste script content.";
                return View();
            }

            var script = new PlaywrightScript
            {
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
                ScriptContent = content,
                FileExtension = ext,
                RunMode = string.IsNullOrWhiteSpace(runMode) ? "node" : runMode,
                IsPublic = isPublic,
                CreatedBy = User.Identity?.Name,
                CreatedAt = DateTimeHelper.BdNow
            };

            _context.Add(script);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Script imported successfully!";
            return RedirectToAction(nameof(Details), new { id = script.Id });
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null) return NotFound();

            if (script.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own scripts.";
                return RedirectToAction(nameof(Index));
            }

            return View(script);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string name, string? description, string? tags, string? runMode, bool isPublic, IFormFile? scriptFile)
        {
            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null) return NotFound();

            if (script.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own scripts.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Error"] = "Script name is required.";
                return View(script);
            }

            script.Name = name.Trim();
            script.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            script.Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
            script.RunMode = string.IsNullOrWhiteSpace(runMode) ? "node" : runMode;
            script.IsPublic = isPublic;
            script.UpdatedAt = DateTimeHelper.BdNow;

            if (scriptFile != null && scriptFile.Length > 0)
            {
                var ext = Path.GetExtension(scriptFile.FileName).ToLowerInvariant();
                if (ext != ".js" && ext != ".ts")
                {
                    TempData["Error"] = "Only .js or .ts files are allowed.";
                    return View(script);
                }
                using var reader = new StreamReader(scriptFile.OpenReadStream());
                script.ScriptContent = await reader.ReadToEndAsync();
                script.FileExtension = ext;
            }

            _context.Update(script);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Script updated successfully!";
            return RedirectToAction(nameof(Details), new { id = script.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script != null)
            {
                if (script.CreatedBy != User.Identity?.Name)
                {
                    TempData["Error"] = "You can only delete your own scripts.";
                    return RedirectToAction(nameof(Index));
                }

                _context.PlaywrightScripts.Remove(script);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Script deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        [AllowAnonymous]
        public async Task<IActionResult> Download(int id)
        {
            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null) return NotFound();

            if (!script.IsPublic && User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");

            var bytes = System.Text.Encoding.UTF8.GetBytes(script.ScriptContent);
            var fileName = script.Name.Replace(' ', '_').Replace('/', '_') + script.FileExtension;
            return File(bytes, "application/octet-stream", fileName);
        }

        [AllowAnonymous]
        public async Task<IActionResult> GetContent(int id)
        {
            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null) return NotFound();
            if (!script.IsPublic && User.Identity?.IsAuthenticated != true)
                return Unauthorized();
            return Json(new { content = script.ScriptContent, runMode = script.RunMode });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(int id, [FromForm] string? paramsJson = null)
        {
            var result = await _runner.RunAsync(id, paramsJson);
            return Json(new
            {
                success = result.Success,
                exitCode = result.ExitCode,
                output = result.Output,
                durationMs = result.DurationMs,
                timedOut = result.TimedOut
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task RunStream(int id, [FromForm] string? paramsJson = null)
        {
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await Response.Body.FlushAsync();

            async Task Send(string jsonData, string? evtName = null)
            {
                if (evtName != null) await Response.WriteAsync($"event: {evtName}\n");
                await Response.WriteAsync($"data: {jsonData}\n\n");
                await Response.Body.FlushAsync();
            }

            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null)
            {
                await Send(JsonSerializer.Serialize("[ERROR] Script not found."));
                await Send("{\"exitCode\":-1,\"durationMs\":0,\"timedOut\":false}", "done");
                return;
            }

            var content = script.ScriptContent;
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try { JsonDocument.Parse(paramsJson); }
                catch (Exception ex)
                {
                    await Send(JsonSerializer.Serialize($"[ERROR] Invalid params JSON: {ex.Message}"));
                    await Send("{\"exitCode\":-1,\"durationMs\":0,\"timedOut\":false}", "done");
                    return;
                }
                content = PlaywrightRunnerService.InjectParams(content, paramsJson);
            }
            else if (content.Contains("__PARAMS__"))
            {
                await Send(JsonSerializer.Serialize("[ERROR] Template script — fill in the Parameters panel before running."));
                await Send("{\"exitCode\":-1,\"durationMs\":0,\"timedOut\":false}", "done");
                return;
            }

            var workingDir = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Playwright:WorkingDirectory"] ?? @"D:\Projects";
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var timeoutSec = config.GetValue<int?>("Playwright:TimeoutSeconds") ?? 300;
            var isPlaywrightRunner = script.RunMode == "playwright";

            if (!isPlaywrightRunner && PlaywrightRunnerService.IsPageFunction(content))
                content = _runner.WrapForPlaywright(content);

            var ext = script.FileExtension ?? ".js";
            var effectiveWorkingDir = Directory.Exists(workingDir) ? workingDir : Path.GetTempPath();
            string tempFile = "";
            string? pwConfigFile = null;

            if (isPlaywrightRunner)
            {
                var testTempDir = Path.Combine(effectiveWorkingDir, "__qa_temp");
                Directory.CreateDirectory(testTempDir);
                tempFile = Path.Combine(testTempDir, $"qa_{Guid.NewGuid():N}{ext}");
            }
            else
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"qa_script_{Guid.NewGuid():N}{ext}");
            }

            try
            {
                await System.IO.File.WriteAllTextAsync(tempFile, content, System.Text.Encoding.UTF8);

                string args;
                if (isPlaywrightRunner)
                {
                    var timeoutMs = timeoutSec * 1000;
                    var testFileName = Path.GetFileName(tempFile);
                    pwConfigFile = await _runner.BuildPlaywrightConfigAsync(effectiveWorkingDir, testFileName, timeoutMs);
                    args = $"/c npx playwright test --config \"{pwConfigFile}\"";
                }
                else
                {
                    args = $"/c node \"{tempFile}\"";
                }

                var psi = _runner.BuildProcessStartInfo(args, effectiveWorkingDir);
                var runId = Guid.NewGuid().ToString("N");
                var sw = Stopwatch.StartNew();
                using var process = Process.Start(psi)!;
                _activeRuns[runId] = process;
                await Send(JsonSerializer.Serialize(new { runId }), "start");

                var lineChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

                var stdoutTask = Task.Run(async () =>
                {
                    try { string? line; while ((line = await process.StandardOutput.ReadLineAsync()) != null) await lineChannel.Writer.WriteAsync(line); } catch { }
                });
                var stderrTask = Task.Run(async () =>
                {
                    try { string? line; while ((line = await process.StandardError.ReadLineAsync()) != null) await lineChannel.Writer.WriteAsync(line); } catch { }
                });
                _ = Task.WhenAll(stdoutTask, stderrTask).ContinueWith(_ => lineChannel.Writer.TryComplete());

                bool timedOut = false;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    await foreach (var line in lineChannel.Reader.ReadAllAsync(cts.Token))
                        await Send(JsonSerializer.Serialize(line));
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { process.Kill(true); } catch { }
                }

                await Task.WhenAll(stdoutTask, stderrTask);
                if (!process.HasExited) try { await process.WaitForExitAsync(); } catch { }

                sw.Stop();
                _activeRuns.TryRemove(runId, out _);
                var stopped = _stoppedRuns.TryRemove(runId, out _);
                var exitCode = timedOut ? -1 : (process.HasExited ? process.ExitCode : -1);
                await Send(JsonSerializer.Serialize(new { exitCode, durationMs = sw.ElapsedMilliseconds, timedOut, stopped }), "done");
            }
            catch (Exception ex)
            {
                try
                {
                    await Send(JsonSerializer.Serialize($"[ERROR] {ex.Message}"));
                    await Send("{\"exitCode\":-1,\"durationMs\":0,\"timedOut\":false}", "done");
                }
                catch { }
            }
            finally
            {
                try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); } catch { }
                try { if (pwConfigFile != null && System.IO.File.Exists(pwConfigFile)) System.IO.File.Delete(pwConfigFile); } catch { }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult StopRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return Json(new { success = false });
            _stoppedRuns[runId] = true;
            if (_activeRuns.TryGetValue(runId, out var process))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Json(new { success = true });
            }
            return Json(new { success = false });
        }
    }
}
