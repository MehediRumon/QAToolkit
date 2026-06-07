using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;
using System.Text;
using System.Text.Json;

namespace QAToolkit.Services
{
    public class HermesService : IHermesService
    {
        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly PlaywrightRunnerService _runner;

        public HermesService(ApplicationDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config, PlaywrightRunnerService runner)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _runner = runner;
        }

        // ── Tools definition ──────────────────────────────────────────────────

        private static readonly object[] _tools = new object[]
        {
            new {
                type = "function",
                function = new {
                    name = "list_scripts",
                    description = "List available Playwright test scripts in QAToolkit. Use this to find scripts by name or tags before running.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            search = new { type = "string", description = "Optional search term to filter by name or tags (e.g. 'exam', 'UMS', 'login')" }
                        }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "run_script",
                    description = "Run a Playwright test script and return the full output and result. Always call list_scripts first if you don't know the script ID.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            script_id   = new { type = "integer", description = "The numeric ID of the script to run" },
                            script_name = new { type = "string",  description = "Name or partial name of the script (used to find it if script_id is unknown)" }
                        }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "read_script",
                    description = "Read the source code of a Playwright test script so you can review, explain or improve it.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            script_id   = new { type = "integer", description = "The numeric ID of the script" },
                            script_name = new { type = "string",  description = "Name or partial name of the script" }
                        }
                    }
                }
            }
        };

        // ── Public API ────────────────────────────────────────────────────────

        public async Task<string> ChatAsync(string userName, int chatId, string userMessage)
        {
            var history = await _db.HermesMessages
                .Where(m => m.ChatId == chatId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            var context = await BuildContextAsync();
            var systemPrompt = BuildSystemPrompt(context);

            var messages = new List<object> { new { role = "system", content = systemPrompt } };
            foreach (var m in history.TakeLast(20))
                messages.Add(new { role = m.Role, content = m.Content });
            messages.Add(new { role = "user", content = userMessage });

            var userSettings = await GetUserSettingsAsync(userName);
            var finalResponse = await RunWithToolsAsync(messages, userSettings);

            var now = DateTimeHelper.BdNow;
            _db.HermesMessages.Add(new HermesMessage { ChatId = chatId, Role = "user",      Content = userMessage,    CreatedAt = now });
            _db.HermesMessages.Add(new HermesMessage { ChatId = chatId, Role = "assistant", Content = finalResponse,  CreatedAt = now.AddMilliseconds(1) });

            var chat = await _db.HermesChats.FindAsync(chatId);
            if (chat != null)
            {
                if (!history.Any())
                    chat.Title = userMessage.Length > 60 ? userMessage[..60] + "…" : userMessage;
                chat.UpdatedAt = now;
            }

            await _db.SaveChangesAsync();
            return finalResponse;
        }

        public async Task LogActivityAsync(string activityType, string userName, int entityId, string entityName, string? tags, string? extra = null)
        {
            try
            {
                _db.HermesActivities.Add(new HermesActivity
                {
                    ActivityType = activityType,
                    UserName     = userName,
                    EntityId     = entityId,
                    EntityName   = entityName,
                    Tags         = tags,
                    Extra        = extra,
                    CreatedAt    = DateTimeHelper.BdNow
                });
                await _db.SaveChangesAsync();
            }
            catch { /* never break the caller */ }
        }

        public async Task<HermesUserSettings?> GetUserSettingsAsync(string userName) =>
            await _db.HermesUserSettings.FirstOrDefaultAsync(s => s.UserName == userName);

        public async Task SaveUserSettingsAsync(string userName, string provider, string apiKey, string? model)
        {
            var settings = await _db.HermesUserSettings.FirstOrDefaultAsync(s => s.UserName == userName);
            if (settings == null)
            {
                settings = new HermesUserSettings { UserName = userName };
                _db.HermesUserSettings.Add(settings);
            }
            settings.Provider = provider;
            settings.ApiKey   = apiKey.Trim();
            settings.Model    = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            await _db.SaveChangesAsync();
        }

        // ── Tool-use loop ─────────────────────────────────────────────────────

        private async Task<string> RunWithToolsAsync(List<object> messages, HermesUserSettings? userSettings)
        {
            for (int i = 0; i < 6; i++) // max 6 tool rounds
            {
                var (content, toolCalls, rawAssistantMessage) = await CallApiAsync(messages, userSettings);

                if (toolCalls == null || toolCalls.Count == 0)
                    return content ?? "(no response)";

                // Add assistant message that contains the tool_calls
                messages.Add(rawAssistantMessage);

                // Execute each tool and add results
                foreach (var tc in toolCalls)
                {
                    var result = await ExecuteToolAsync(tc.Name, tc.Arguments);
                    messages.Add(new { role = "tool", tool_call_id = tc.Id, content = result });
                }
            }

            return "(tool loop limit reached)";
        }

        // ── Tool execution ────────────────────────────────────────────────────

        private async Task<string> ExecuteToolAsync(string toolName, string argsJson)
        {
            try
            {
                using var args = JsonDocument.Parse(argsJson);

                return toolName switch
                {
                    "list_scripts" => await ListScriptsAsync(args),
                    "run_script"   => await RunScriptAsync(args),
                    "read_script"  => await ReadScriptAsync(args),
                    _              => "Unknown tool."
                };
            }
            catch (Exception ex)
            {
                return $"Tool error: {ex.Message}";
            }
        }

        private async Task<string> ListScriptsAsync(JsonDocument args)
        {
            string? search = null;
            if (args.RootElement.TryGetProperty("search", out var s)) search = s.GetString();

            var query = _db.PlaywrightScripts.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(x => x.Name.Contains(search) || (x.Tags != null && x.Tags.Contains(search)));

            var scripts = await query.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).Take(25).ToListAsync();
            if (!scripts.Any()) return "No scripts found.";

            var sb = new StringBuilder();
            foreach (var s2 in scripts)
                sb.AppendLine($"ID:{s2.Id} | {s2.Name} | tags:{s2.Tags ?? "none"} | mode:{s2.RunMode}");
            return sb.ToString();
        }

        private async Task<string> RunScriptAsync(JsonDocument args)
        {
            var script = await ResolveScriptAsync(args);
            if (script == null) return "Script not found. Try list_scripts first.";

            var result = await _runner.RunAsync(script.Id, null);
            var status = result.TimedOut ? "TIMEOUT" : (result.Success ? "PASSED ✅" : "FAILED ❌");
            var output = result.Output?.Length > 3000 ? result.Output[..3000] + "\n...(truncated)" : result.Output;
            return $"Script: {script.Name}\nStatus: {status} | Exit:{result.ExitCode} | {result.DurationMs}ms\n\n{output}";
        }

        private async Task<string> ReadScriptAsync(JsonDocument args)
        {
            var script = await ResolveScriptAsync(args);
            if (script == null) return "Script not found. Try list_scripts first.";

            var content = script.ScriptContent?.Length > 4000
                ? script.ScriptContent[..4000] + "\n...(truncated)"
                : script.ScriptContent;
            return $"Name: {script.Name}\nTags: {script.Tags}\nMode: {script.RunMode}\n\n```javascript\n{content}\n```";
        }

        private async Task<PlaywrightScript?> ResolveScriptAsync(JsonDocument args)
        {
            if (args.RootElement.TryGetProperty("script_id", out var idProp) &&
                idProp.TryGetInt32(out var id))
                return await _db.PlaywrightScripts.FindAsync(id);

            if (args.RootElement.TryGetProperty("script_name", out var nameProp))
            {
                var name = nameProp.GetString() ?? "";
                return await _db.PlaywrightScripts
                    .Where(x => x.Name.Contains(name))
                    .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                    .FirstOrDefaultAsync();
            }
            return null;
        }

        // ── API call ──────────────────────────────────────────────────────────

        private record ToolCall(string Id, string Name, string Arguments);

        private async Task<(string? Content, List<ToolCall>? ToolCalls, object RawAssistantMessage)> CallApiAsync(
            List<object> messages, HermesUserSettings? userSettings)
        {
            var (baseUrl, apiKey, model) = ResolveProvider(userSettings);

            var payload = new
            {
                model,
                messages,
                tools       = _tools,
                tool_choice = "auto",
                temperature = 0.7,
                max_tokens  = 4096
            };

            var http = _httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var body     = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"{baseUrl}chat/completions", body);
            var raw      = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API error {(int)response.StatusCode}: {raw}");

            using var doc    = JsonDocument.Parse(raw);
            var choice       = doc.RootElement.GetProperty("choices")[0];
            var message      = choice.GetProperty("message");
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "";

            if (finishReason == "tool_calls" && message.TryGetProperty("tool_calls", out var tcs))
            {
                var toolCalls = tcs.EnumerateArray().Select(tc => new ToolCall(
                    tc.GetProperty("id").GetString()!,
                    tc.GetProperty("function").GetProperty("name").GetString()!,
                    tc.GetProperty("function").GetProperty("arguments").GetString()!
                )).ToList();

                // Reconstruct the assistant message as a plain object for re-serialization
                var rawMsg = new
                {
                    role       = "assistant",
                    content    = (string?)null,
                    tool_calls = toolCalls.Select(t => new
                    {
                        id       = t.Id,
                        type     = "function",
                        function = new { name = t.Name, arguments = t.Arguments }
                    }).ToArray()
                };

                return (null, toolCalls, rawMsg);
            }

            var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
            return (content ?? "(empty response)", null, new { role = "assistant", content = content });
        }

        // ── Provider resolution ───────────────────────────────────────────────

        private (string BaseUrl, string ApiKey, string Model) ResolveProvider(HermesUserSettings? userSettings)
        {
            if (userSettings != null && !string.IsNullOrWhiteSpace(userSettings.ApiKey))
            {
                return userSettings.Provider switch
                {
                    "Gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/",
                                 userSettings.ApiKey,
                                 userSettings.Model ?? "gemini-1.5-flash"),
                    "Proxy"  => (_config["Hermes:ProxyUrl"] ?? "http://localhost:3001/v1/",
                                 userSettings.ApiKey,
                                 userSettings.Model ?? "llama-3.3-70b-versatile"),
                    _        => ("https://api.groq.com/openai/v1/",
                                 userSettings.ApiKey,
                                 userSettings.Model ?? "llama-3.3-70b-versatile")
                };
            }

            return (_config["Hermes:Provider"] ?? "Groq") switch
            {
                "Gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/",
                             _config["Hermes:GeminiApiKey"] ?? "",
                             _config["Hermes:GeminiModel"] ?? "gemini-1.5-flash"),
                "Proxy"  => (_config["Hermes:ProxyUrl"] ?? "http://localhost:3001/v1/",
                             _config["Hermes:ProxyApiKey"] ?? "no-key",
                             _config["Hermes:ProxyModel"] ?? "llama-3.3-70b-versatile"),
                _        => ("https://api.groq.com/openai/v1/",
                             _config["Hermes:GroqApiKey"] ?? "",
                             _config["Hermes:GroqModel"] ?? "llama-3.3-70b-versatile")
            };
        }

        // ── Context & prompt ──────────────────────────────────────────────────

        private async Task<string> BuildContextAsync()
        {
            var since      = DateTimeHelper.BdNow.AddDays(-14);
            var activities = await _db.HermesActivities
                .Where(a => a.CreatedAt >= since)
                .OrderByDescending(a => a.CreatedAt)
                .Take(60).ToListAsync();
            var knowledge  = await _db.HermesKnowledges.OrderBy(k => k.Project).ToListAsync();

            var sb = new StringBuilder();
            if (activities.Any())
            {
                sb.AppendLine("## Team Activity (last 14 days):");
                foreach (var a in activities)
                {
                    var line = $"- [{a.CreatedAt:MMM dd HH:mm}] {a.UserName}: {a.ActivityType} → \"{a.EntityName}\"";
                    if (!string.IsNullOrWhiteSpace(a.Tags))  line += $" [tags:{a.Tags}]";
                    if (!string.IsNullOrWhiteSpace(a.Extra)) line += $" ({a.Extra})";
                    sb.AppendLine(line);
                }
            }
            if (knowledge.Any())
            {
                sb.AppendLine("\n## Project Knowledge:");
                foreach (var k in knowledge)
                {
                    sb.AppendLine($"### {(string.IsNullOrWhiteSpace(k.Module) ? k.Project : $"{k.Project} / {k.Module}")}");
                    sb.AppendLine(k.Summary);
                }
            }
            return sb.ToString();
        }

        private static string BuildSystemPrompt(string context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are Hermes, an AI assistant embedded in QA Toolkit for the osl.team QA engineering team.");
            sb.AppendLine("You have tools to directly list, read, and run Playwright test scripts in QAToolkit.");
            sb.AppendLine();
            sb.AppendLine("When a user asks to run a script: use list_scripts to find it, then run_script to execute it, then analyze the output.");
            sb.AppendLine("When a user asks to check/review a script: use read_script to get the code, then review it.");
            sb.AppendLine("Always use tools when you can — don't just describe how to do it, actually do it.");
            sb.AppendLine();
            sb.AppendLine("Projects: UMS Admin (ums-{N}.osl.team), Student Portal (ums-portal-{N}.osl.team), Teacher Portal (ums-teacher-portal-{N}.osl.team), OM Admin (om-admin.osl.team)");
            sb.AppendLine("When writing new scripts, output them in ```javascript code blocks.");
            sb.AppendLine("Keep answers concise and practical.");
            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine("\n---");
                sb.AppendLine(context);
            }
            return sb.ToString();
        }
    }
}
