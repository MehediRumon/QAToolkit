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

        public HermesService(ApplicationDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _config = config;
        }

        public async Task<string> ChatAsync(string userName, int chatId, string userMessage)
        {
            var history = await _db.HermesMessages
                .Where(m => m.ChatId == chatId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            var context = await BuildContextAsync(userName);
            var systemPrompt = BuildSystemPrompt(context);

            var apiMessages = new List<object> { new { role = "system", content = systemPrompt } };

            foreach (var m in history.TakeLast(20))
                apiMessages.Add(new { role = m.Role, content = m.Content });

            apiMessages.Add(new { role = "user", content = userMessage });

            var userSettings = await GetUserSettingsAsync(userName);
            var assistantReply = await CallApiAsync(apiMessages, userSettings);

            var now = DateTimeHelper.BdNow;

            _db.HermesMessages.Add(new HermesMessage { ChatId = chatId, Role = "user", Content = userMessage, CreatedAt = now });
            _db.HermesMessages.Add(new HermesMessage { ChatId = chatId, Role = "assistant", Content = assistantReply, CreatedAt = now.AddMilliseconds(1) });

            var chat = await _db.HermesChats.FindAsync(chatId);
            if (chat != null)
            {
                if (!history.Any())
                    chat.Title = userMessage.Length > 60 ? userMessage[..60] + "…" : userMessage;
                chat.UpdatedAt = now;
            }

            await _db.SaveChangesAsync();
            return assistantReply;
        }

        public async Task LogActivityAsync(string activityType, string userName, int entityId, string entityName, string? tags, string? extra = null)
        {
            try
            {
                _db.HermesActivities.Add(new HermesActivity
                {
                    ActivityType = activityType,
                    UserName = userName,
                    EntityId = entityId,
                    EntityName = entityName,
                    Tags = tags,
                    Extra = extra,
                    CreatedAt = DateTimeHelper.BdNow
                });
                await _db.SaveChangesAsync();
            }
            catch { /* never break the caller */ }
        }

        public async Task<HermesUserSettings?> GetUserSettingsAsync(string userName)
        {
            return await _db.HermesUserSettings.FirstOrDefaultAsync(s => s.UserName == userName);
        }

        public async Task SaveUserSettingsAsync(string userName, string provider, string apiKey, string? model)
        {
            var settings = await _db.HermesUserSettings.FirstOrDefaultAsync(s => s.UserName == userName);
            if (settings == null)
            {
                settings = new HermesUserSettings { UserName = userName };
                _db.HermesUserSettings.Add(settings);
            }
            settings.Provider = provider;
            settings.ApiKey = apiKey.Trim();
            settings.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            await _db.SaveChangesAsync();
        }

        private async Task<string> BuildContextAsync(string userName)
        {
            var since = DateTimeHelper.BdNow.AddDays(-14);

            var activities = await _db.HermesActivities
                .Where(a => a.CreatedAt >= since)
                .OrderByDescending(a => a.CreatedAt)
                .Take(60)
                .ToListAsync();

            var knowledge = await _db.HermesKnowledges.OrderBy(k => k.Project).ToListAsync();

            var sb = new StringBuilder();

            if (activities.Any())
            {
                sb.AppendLine("## Team Activity (last 14 days):");
                foreach (var a in activities)
                {
                    var line = $"- [{a.CreatedAt:MMM dd HH:mm}] {a.UserName}: {a.ActivityType} → \"{a.EntityName}\"";
                    if (!string.IsNullOrWhiteSpace(a.Tags)) line += $" [tags: {a.Tags}]";
                    if (!string.IsNullOrWhiteSpace(a.Extra)) line += $" ({a.Extra})";
                    sb.AppendLine(line);
                }
            }

            if (knowledge.Any())
            {
                sb.AppendLine("\n## Project Knowledge:");
                foreach (var k in knowledge)
                {
                    var header = string.IsNullOrWhiteSpace(k.Module) ? k.Project : $"{k.Project} / {k.Module}";
                    sb.AppendLine($"### {header}");
                    sb.AppendLine(k.Summary);
                }
            }

            return sb.ToString();
        }

        private static string BuildSystemPrompt(string context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are Hermes, an AI assistant embedded in QA Toolkit for the osl.team QA engineering team.");
            sb.AppendLine("You help the team with:");
            sb.AppendLine("- Writing Playwright (JavaScript/Node.js) test scripts for UMS Admin, Student Portal, Teacher Portal, OM Admin");
            sb.AppendLine("- Tracking what has been tested and what is missing");
            sb.AppendLine("- Answering questions about team testing activity");
            sb.AppendLine("- Suggesting next steps based on test history");
            sb.AppendLine();
            sb.AppendLine("Projects the team tests:");
            sb.AppendLine("- UMS Admin: ums-{N}.osl.team (N=1-6)");
            sb.AppendLine("- Student Portal: ums-portal-{N}.osl.team");
            sb.AppendLine("- Teacher Portal: ums-teacher-portal-{N}.osl.team");
            sb.AppendLine("- OM Admin: om-admin.osl.team");
            sb.AppendLine();
            sb.AppendLine("When writing Playwright scripts, output them inside ```javascript code blocks.");
            sb.AppendLine("Keep answers concise and practical.");

            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine(context);
            }

            return sb.ToString();
        }

        private async Task<string> CallApiAsync(List<object> messages, HermesUserSettings? userSettings)
        {
            string baseUrl, apiKey, model;

            // User's own key takes priority over global config
            if (userSettings != null && !string.IsNullOrWhiteSpace(userSettings.ApiKey))
            {
                var provider = userSettings.Provider;
                var userModel = userSettings.Model;

                if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
                    apiKey = userSettings.ApiKey;
                    model = userModel ?? "gemini-1.5-flash";
                }
                else if (provider.Equals("Proxy", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = _config["Hermes:ProxyUrl"] ?? "http://localhost:3001/v1/";
                    apiKey = userSettings.ApiKey;
                    model = userModel ?? "llama-3.3-70b-versatile";
                }
                else // Groq default
                {
                    baseUrl = "https://api.groq.com/openai/v1/";
                    apiKey = userSettings.ApiKey;
                    model = userModel ?? "llama-3.3-70b-versatile";
                }
            }
            else
            {
                // Fall back to global config
                var provider = _config["Hermes:Provider"] ?? "Groq";

                if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
                    apiKey = _config["Hermes:GeminiApiKey"] ?? "";
                    model = _config["Hermes:GeminiModel"] ?? "gemini-1.5-flash";
                }
                else if (provider.Equals("Proxy", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = _config["Hermes:ProxyUrl"] ?? "http://localhost:3001/v1/";
                    apiKey = _config["Hermes:ProxyApiKey"] ?? "no-key";
                    model = _config["Hermes:ProxyModel"] ?? "llama-3.3-70b-versatile";
                }
                else
                {
                    baseUrl = "https://api.groq.com/openai/v1/";
                    apiKey = _config["Hermes:GroqApiKey"] ?? "";
                    model = _config["Hermes:GroqModel"] ?? "llama-3.3-70b-versatile";
                }
            }

            var payload = new
            {
                model,
                messages,
                temperature = 0.7,
                max_tokens = 4096
            };

            var http = _httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"{baseUrl}chat/completions", body);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API error {(int)response.StatusCode}: {raw}");

            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "(empty response)";
        }
    }
}
