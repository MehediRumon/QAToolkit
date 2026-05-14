using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class PlaywrightScriptsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _activeRuns = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _stoppedRuns = new();

        public PlaywrightScriptsController(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        private string ResolveNodeDirectory()
        {
            var configured = _config.GetValue<string>("Playwright:NodeDirectory");
            if (!string.IsNullOrWhiteSpace(configured) && System.IO.File.Exists(Path.Combine(configured, "node.exe")))
                return configured;

            var candidates = new[]
            {
                @"C:\Program Files\nodejs",
                @"C:\Program Files (x86)\nodejs",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"),
            };
            foreach (var path in candidates)
                if (System.IO.File.Exists(Path.Combine(path, "node.exe")))
                    return path;

            return "";
        }

        private static bool IsPageFunction(string content)
        {
            foreach (var line in content.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("//")) continue;
                return t.StartsWith("async (page) =>") || t.StartsWith("async(page)=>");
            }
            return false;
        }

        private string WrapForPlaywright(string scriptContent)
        {
            var headless = _config.GetValue<bool>("Playwright:Headless") ? "true" : "false";
            return
                "const { chromium } = require('playwright');\n" +
                "const __fn = (" + scriptContent + ");\n" +
                "(async () => {\n" +
                "  let browser;\n" +
                "  try {\n" +
                "    browser = await chromium.launch({ headless: " + headless + " });\n" +
                "    const page = await browser.newPage();\n" +
                "    const result = await __fn(page);\n" +
                "    if (result && typeof result === 'object' && !Array.isArray(result)) {\n" +
                "      if (Array.isArray(result.log)) result.log.forEach(l => console.log(l));\n" +
                "      if (Array.isArray(result.results)) result.results.forEach(r => {\n" +
                "        if (r && Array.isArray(r.log)) r.log.forEach(l => console.log(l));\n" +
                "        if (r && r.success === false) process.stderr.write('[FAIL] ' + (r.message || '') + '\\n');\n" +
                "      });\n" +
                "    } else if (result != null && !Array.isArray(result)) {\n" +
                "      console.log(JSON.stringify(result, null, 2));\n" +
                "    }\n" +
                "    process.exit(result && result.success === false ? 1 : 0);\n" +
                "  } catch (err) {\n" +
                "    process.stderr.write('\\n[ERROR] ' + err.message + '\\n');\n" +
                "    process.exit(1);\n" +
                "  } finally {\n" +
                "    if (browser) try { await browser.close(); } catch (_) {}\n" +
                "  }\n" +
                "})();\n";
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(int id, [FromForm] string? paramsJson = null)
        {
            var script = await _context.PlaywrightScripts.FindAsync(id);
            if (script == null) return NotFound();

            var content = script.ScriptContent;

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try { JsonDocument.Parse(paramsJson); }
                catch (Exception ex) { return Json(new { success = false, exitCode = -1, output = $"Invalid JSON in parameters: {ex.Message}", durationMs = 0L, timedOut = false }); }
                content = InjectParams(content, paramsJson);
            }
            else if (content.Contains("__PARAMS__"))
            {
                return Json(new { success = false, exitCode = -1, output = "This is a template script — fill in the Parameters panel before running.", durationMs = 0L, timedOut = false });
            }

            var workingDir = _config["Playwright:WorkingDirectory"] ?? @"D:\Projects";
            var timeoutSec = _config.GetValue<int?>("Playwright:TimeoutSeconds") ?? 300;
            var isPlaywrightRunner = script.RunMode == "playwright";

            // Wrap page-function scripts for node runner only
            if (!isPlaywrightRunner && IsPageFunction(content))
                content = WrapForPlaywright(content);

            var ext = script.FileExtension ?? ".js";
            var effectiveWorkingDir = Directory.Exists(workingDir) ? workingDir : Path.GetTempPath();
            string tempFile;
            string? pwConfigFile = null;

            if (isPlaywrightRunner)
            {
                // Test file must be inside the working directory so playwright can discover it
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
                    var headless = _config.GetValue<bool>("Playwright:Headless") ? "true" : "false";
                    var testFileName = Path.GetFileName(tempFile);
                    // Minimal per-run config: only runs our one temp file
                    pwConfigFile = Path.Combine(effectiveWorkingDir, $"qa_config_{Guid.NewGuid():N}.js");
                    await System.IO.File.WriteAllTextAsync(pwConfigFile,
                        $"module.exports = {{ testDir: './__qa_temp', testMatch: ['{testFileName}'], " +
                        $"reporter: [['list']], workers: 1, timeout: {timeoutMs}, " +
                        $"use: {{ headless: {headless} }} }};");
                    args = $"/c npx playwright test --config \"{pwConfigFile}\"";
                }
                else
                {
                    args = $"/c node \"{tempFile}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = effectiveWorkingDir
                };
                // Prepend node directory to PATH so IIS app pool can find node/npx
                var nodeDir = ResolveNodeDirectory();
                if (!string.IsNullOrWhiteSpace(nodeDir))
                {
                    var currentPath = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : "";
                    psi.Environment["PATH"] = nodeDir + ";" + currentPath;
                }
                psi.Environment["NODE_PATH"] = Path.Combine(workingDir, "node_modules");
                // Use shared browser path so Chromium is found regardless of which Windows user IIS runs as
                var browsersPath = _config.GetValue<string>("Playwright:BrowsersPath") ?? @"C:\playwright-browsers";
                psi.Environment["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

                var sw = Stopwatch.StartNew();
                using var process = Process.Start(psi)!;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                bool completed = true;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { completed = false; try { process.Kill(true); } catch { } }

                sw.Stop();
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var output = stdout + (stderr.Length > 0 ? (stdout.Length > 0 ? "\n" : "") + stderr : "");

                return Json(new
                {
                    success = completed && process.ExitCode == 0,
                    exitCode = completed ? process.ExitCode : -1,
                    output,
                    durationMs = sw.ElapsedMilliseconds,
                    timedOut = !completed
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, exitCode = -1, output = $"Failed to start process: {ex.Message}", durationMs = 0L, timedOut = false });
            }
            finally
            {
                try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); } catch { }
                try { if (pwConfigFile != null && System.IO.File.Exists(pwConfigFile)) System.IO.File.Delete(pwConfigFile); } catch { }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task RunStream(int id, [FromForm] string? paramsJson = null)
        {
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

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
                content = InjectParams(content, paramsJson);
            }
            else if (content.Contains("__PARAMS__"))
            {
                await Send(JsonSerializer.Serialize("[ERROR] Template script — fill in the Parameters panel before running."));
                await Send("{\"exitCode\":-1,\"durationMs\":0,\"timedOut\":false}", "done");
                return;
            }

            var workingDir = _config["Playwright:WorkingDirectory"] ?? @"D:\Projects";
            var timeoutSec = _config.GetValue<int?>("Playwright:TimeoutSeconds") ?? 300;
            var isPlaywrightRunner = script.RunMode == "playwright";

            if (!isPlaywrightRunner && IsPageFunction(content))
                content = WrapForPlaywright(content);

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
                    var headless = _config.GetValue<bool>("Playwright:Headless") ? "true" : "false";
                    var testFileName = Path.GetFileName(tempFile);
                    pwConfigFile = Path.Combine(effectiveWorkingDir, $"qa_config_{Guid.NewGuid():N}.js");
                    await System.IO.File.WriteAllTextAsync(pwConfigFile,
                        $"module.exports = {{ testDir: './__qa_temp', testMatch: ['{testFileName}'], " +
                        $"reporter: [['list']], workers: 1, timeout: {timeoutMs}, " +
                        $"use: {{ headless: {headless} }} }};");
                    args = $"/c npx playwright test --config \"{pwConfigFile}\"";
                }
                else
                {
                    args = $"/c node \"{tempFile}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = effectiveWorkingDir
                };
                var nodeDir = ResolveNodeDirectory();
                if (!string.IsNullOrWhiteSpace(nodeDir))
                {
                    var currentPath = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : "";
                    psi.Environment["PATH"] = nodeDir + ";" + currentPath;
                }
                psi.Environment["NODE_PATH"] = Path.Combine(workingDir, "node_modules");
                var browsersPath = _config.GetValue<string>("Playwright:BrowsersPath") ?? @"C:\playwright-browsers";
                psi.Environment["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

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

        // ── Param injection helpers ───────────────────────────────────────────

        private static string InjectParams(string content, string paramsJson)
        {
            if (content.Contains("__PARAMS__"))
                return content.Replace("__PARAMS__", paramsJson);

            Dictionary<string, JsonElement>? p;
            try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson); }
            catch { return content; }
            if (p == null || p.Count == 0) return content;

            if (Regex.IsMatch(content, @"const\s+CONFIG\s*=\s*\{"))
                return Regex.Replace(content, @"const\s+CONFIG\s*=\s*\{[\s\S]*?\n[ \t]*\};", $"const CONFIG = {paramsJson};");

            if (content.Contains("process.argv"))
                return InjectArgv(content, p);

            return InjectConstants(content, p);
        }

        // Build process.argv shim from options comment: //   -s <servers>  desc  (default: 5)
        private static string InjectArgv(string content, Dictionary<string, JsonElement> p)
        {
            var argv = new List<string> { "node", "script.js" };

            // Positional from: //   node script.js <mobile> [options]
            var pos = Regex.Match(content, @"//\s+node\s+\S+\.js\s+<(\w+)>");
            if (pos.Success)
            {
                var key = pos.Groups[1].Value;
                if (p.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString();
                    if (!string.IsNullOrEmpty(s)) argv.Add(s);
                }
            }

            // Flags from: //   -s <servers>  or  //   --session <year>
            foreach (Match m in Regex.Matches(content, @"//\s+(--?\w+)\s+<(\w+)>", RegexOptions.Multiline))
            {
                var flag = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                if (p.TryGetValue(varName, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString();
                    if (!string.IsNullOrEmpty(s)) { argv.Add(flag); argv.Add(s); }
                }
            }

            return $"process.argv = {JsonSerializer.Serialize(argv)};\n" + content;
        }

        // Replace top-level const declarations: const KEY = 'old' → const KEY = 'new'
        private static string InjectConstants(string content, Dictionary<string, JsonElement> p)
        {
            foreach (var (key, val) in p)
            {
                if (val.ValueKind == JsonValueKind.Null) continue;
                var jsLit = val.ValueKind == JsonValueKind.String
                    ? $"'{val.GetString()?.Replace("\\", "\\\\").Replace("'", "\\'")}'"
                    : val.ToString();
                content = Regex.Replace(content,
                    $@"(const\s+{Regex.Escape(key)}\s*=\s*)(?:'[^']*'|""[^""]*""|`[^`]*`|\[[^\]]*\]|-?\d+(?:\.\d+)?)",
                    m => m.Groups[1].Value + jsLit);
            }
            return content;
        }
    }
}
