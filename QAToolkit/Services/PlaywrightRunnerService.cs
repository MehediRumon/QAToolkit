using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QAToolkit.Services
{
    public class PlaywrightRunnerService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;

        public record RunResult(bool Success, int ExitCode, string Output, long DurationMs, bool TimedOut);

        public PlaywrightRunnerService(ApplicationDbContext context, IConfiguration config, IWebHostEnvironment env)
        {
            _context = context;
            _config = config;
            _env = env;
        }

        public string ResolveNodeDirectory()
        {
            var configured = _config.GetValue<string>("Playwright:NodeDirectory");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "node.exe")))
                return configured;

            var candidates = new[]
            {
                @"C:\Program Files\nodejs",
                @"C:\Program Files (x86)\nodejs",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"),
            };
            foreach (var path in candidates)
                if (File.Exists(Path.Combine(path, "node.exe")))
                    return path;

            return "";
        }

        public static bool IsPageFunction(string content)
        {
            foreach (var line in content.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("//")) continue;
                return t.StartsWith("async (page) =>") || t.StartsWith("async(page)=>");
            }
            return false;
        }

        public async Task<string> BuildPlaywrightConfigAsync(string effectiveWorkingDir, string testFileName, int timeoutMs)
        {
            var headless = _config.GetValue<bool>("Playwright:Headless") ? "true" : "false";
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var shotDir = Path.Combine(webRoot, "outputs", "screenshots").Replace('\\', '/');
            Directory.CreateDirectory(shotDir);

            var reporterFile = Path.Combine(effectiveWorkingDir, "qa_reporter.js");
            await File.WriteAllTextAsync(reporterFile,
                "const path = require('path');\n" +
                "class QAReporter {\n" +
                "  constructor(options) { this.webRoot = (options && options.webRoot) || ''; }\n" +
                "  onTestEnd(test, result) {\n" +
                "    for (const att of (result.attachments || [])) {\n" +
                "      if (att.path && /\\.(png|jpe?g)$/i.test(att.path)) {\n" +
                "        let url = att.path;\n" +
                "        if (this.webRoot) {\n" +
                "          const rel = path.relative(this.webRoot, att.path);\n" +
                "          if (rel && !rel.startsWith('..')) url = '/' + rel.replace(/\\\\/g, '/');\n" +
                "        }\n" +
                "        process.stderr.write('[SCREENSHOT] ' + url + '\\n');\n" +
                "      }\n" +
                "    }\n" +
                "  }\n" +
                "}\n" +
                "module.exports = QAReporter;\n");

            var reporterJsPath = reporterFile.Replace('\\', '/').Replace("'", "\\'");
            var webRootEsc = webRoot.Replace('\\', '/').Replace("'", "\\'");
            var shotDirEsc = shotDir.Replace("'", "\\'");

            var pwConfigFile = Path.Combine(effectiveWorkingDir, $"qa_config_{Guid.NewGuid():N}.js");
            await File.WriteAllTextAsync(pwConfigFile,
                "module.exports = {\n" +
                "  testDir: './__qa_temp',\n" +
                $"  testMatch: ['{testFileName}'],\n" +
                $"  reporter: [['list'], ['{reporterJsPath}', {{ webRoot: '{webRootEsc}' }}]],\n" +
                "  workers: 1,\n" +
                $"  timeout: {timeoutMs},\n" +
                $"  outputDir: '{shotDirEsc}',\n" +
                $"  use: {{ headless: {headless}, screenshot: 'only-on-failure' }}\n" +
                "};\n");

            return pwConfigFile;
        }

        public string WrapForPlaywright(string scriptContent)
        {
            var headless = _config.GetValue<bool>("Playwright:Headless") ? "true" : "false";
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var shotDir = Path.Combine(webRoot, "outputs", "screenshots")
                .Replace('\\', '/').Replace("'", "\\'");
            return
                "if (process.stdout._handle?.setBlocking) process.stdout._handle.setBlocking(true);\n" +
                "if (process.stderr._handle?.setBlocking) process.stderr._handle.setBlocking(true);\n" +
                "const { chromium } = require('playwright');\n" +
                "const __fs = require('fs'), __path = require('path');\n" +
                "const __SHOT_DIR = '" + shotDir + "';\n" +
                "async function __snap(browser, tag) {\n" +
                "  if (!browser) return;\n" +
                "  try {\n" +
                "    __fs.mkdirSync(__SHOT_DIR, { recursive: true });\n" +
                "    const pages = browser.contexts().flatMap(c => c.pages());\n" +
                "    const ts = new Date().toISOString().replace(/[:.]/g, '-');\n" +
                "    for (let i = 0; i < pages.length; i++) {\n" +
                "      const fn = `${ts}_${tag}_p${i}.png`;\n" +
                "      const fp = __path.join(__SHOT_DIR, fn);\n" +
                "      try {\n" +
                "        await pages[i].screenshot({ path: fp, fullPage: true });\n" +
                "        process.stderr.write('[SCREENSHOT] /outputs/screenshots/' + fn + '\\n');\n" +
                "      } catch (e) { process.stderr.write('[SCREENSHOT-FAIL] ' + e.message + '\\n'); }\n" +
                "    }\n" +
                "  } catch (e) { process.stderr.write('[SCREENSHOT-FAIL] ' + e.message + '\\n'); }\n" +
                "}\n" +
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
                "    const subFailed = result && Array.isArray(result.results) && result.results.some(r => r && r.success === false);\n" +
                "    const failed = (result && result.success === false) || subFailed;\n" +
                "    if (failed) await __snap(browser, 'fail');\n" +
                "    process.exit(result && result.success === false ? 1 : 0);\n" +
                "  } catch (err) {\n" +
                "    process.stderr.write('\\n[ERROR] ' + err.message + '\\n');\n" +
                "    await __snap(browser, 'error');\n" +
                "    process.exit(1);\n" +
                "  } finally {\n" +
                "    if (browser) try { await browser.close(); } catch (_) {}\n" +
                "  }\n" +
                "})();\n";
        }

        public static string InjectParams(string content, string paramsJson)
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

        private static string InjectArgv(string content, Dictionary<string, JsonElement> p)
        {
            var argv = new List<string> { "node", "script.js" };
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

        public ProcessStartInfo BuildProcessStartInfo(string args, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            };
            var nodeDir = ResolveNodeDirectory();
            if (!string.IsNullOrWhiteSpace(nodeDir))
            {
                var currentPath = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : "";
                psi.Environment["PATH"] = nodeDir + ";" + currentPath;
            }
            var browsersPath = _config.GetValue<string>("Playwright:BrowsersPath") ?? @"C:\playwright-browsers";
            psi.Environment["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;
            psi.Environment["NODE_PATH"] = Path.Combine(_config["Playwright:WorkingDirectory"] ?? @"D:\Projects", "node_modules");
            return psi;
        }

        // Run a script (non-streaming). Used by the scheduler and the Run action.
        public async Task<RunResult> RunAsync(int scriptId, string? paramsJson, CancellationToken cancellationToken = default)
        {
            var script = await _context.PlaywrightScripts.FindAsync(new object[] { scriptId }, cancellationToken);
            if (script == null)
                return new RunResult(false, -1, "[ERROR] Script not found.", 0, false);

            var content = script.ScriptContent;

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try { JsonDocument.Parse(paramsJson); }
                catch (Exception ex)
                {
                    return new RunResult(false, -1, $"[ERROR] Invalid params JSON: {ex.Message}", 0, false);
                }
                content = InjectParams(content, paramsJson);
            }
            else if (content.Contains("__PARAMS__"))
            {
                return new RunResult(false, -1, "[ERROR] Template script — fill in Parameters before running.", 0, false);
            }

            var workingDir = _config["Playwright:WorkingDirectory"] ?? @"D:\Projects";
            var timeoutSec = _config.GetValue<int?>("Playwright:TimeoutSeconds") ?? 300;
            var isPlaywrightRunner = script.RunMode == "playwright";

            if (!isPlaywrightRunner && IsPageFunction(content))
                content = WrapForPlaywright(content);

            var ext = script.FileExtension ?? ".js";
            var effectiveWorkingDir = Directory.Exists(workingDir) ? workingDir : Path.GetTempPath();
            string tempFile;
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
                await File.WriteAllTextAsync(tempFile, content, System.Text.Encoding.UTF8, cancellationToken);

                string args;
                if (isPlaywrightRunner)
                {
                    var timeoutMs = timeoutSec * 1000;
                    var testFileName = Path.GetFileName(tempFile);
                    pwConfigFile = await BuildPlaywrightConfigAsync(effectiveWorkingDir, testFileName, timeoutMs);
                    args = $"/c npx playwright test --config \"{pwConfigFile}\"";
                }
                else
                {
                    args = $"/c node \"{tempFile}\"";
                }

                var psi = BuildProcessStartInfo(args, effectiveWorkingDir);
                var sw = Stopwatch.StartNew();
                using var process = Process.Start(psi)!;

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                bool timedOut = false;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try { await process.WaitForExitAsync(linkedCts.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = !cancellationToken.IsCancellationRequested;
                    try { process.Kill(true); } catch { }
                }

                sw.Stop();
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var output = stdout + (stderr.Length > 0 ? (stdout.Length > 0 ? "\n" : "") + stderr : "");
                if (output.Length > 50000) output = output[..50000] + "\n...(truncated)";

                var exitCode = timedOut ? -1 : (process.HasExited ? process.ExitCode : -1);
                return new RunResult(!timedOut && process.HasExited && process.ExitCode == 0,
                    exitCode, output, sw.ElapsedMilliseconds, timedOut);
            }
            catch (Exception ex)
            {
                return new RunResult(false, -1, $"[ERROR] Failed to start process: {ex.Message}", 0, false);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                try { if (pwConfigFile != null && File.Exists(pwConfigFile)) File.Delete(pwConfigFile); } catch { }
            }
        }
    }
}
