using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QAToolkit.Controllers
{
    /// <summary>
    /// Server-side proxy for UMS cascading dropdowns (Organization → Program →
    /// Session → Course) used by the Playwright script parameter form.
    /// Logs into the target UMS server with user-provided credentials and keeps
    /// the authenticated cookie session cached per server+user.
    /// </summary>
    public class UmsCascadeController : Controller
    {
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

        public UmsCascadeController(IMemoryCache cache) => _cache = cache;

        public class CascadeRequest
        {
            public string? Server { get; set; }
            public string? Username { get; set; }
            public string? Password { get; set; }
            public string? OrgId { get; set; }
            public string? ProgramId { get; set; }
            public string? SessionId { get; set; }
        }

        public record Item(string Id, string Name);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Organizations([FromBody] CascadeRequest req) =>
            WithSession(req, async client =>
            {
                var resp = await client.GetAsync("/Exam/Exams/CreateExam");
                var html = await resp.Content.ReadAsStringAsync();
                return ParseSelectOptions(html, "OrganizationId");
            });

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Programs([FromBody] CascadeRequest req) =>
            WithSession(req, client => PostAjaxList(client,
                "/Administration/CommonAjax/LoadProgram",
                new Dictionary<string, string>
                {
                    ["organizationIds"] = req.OrgId ?? "",
                    ["isAuthorized"] = "true"
                }));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Sessions([FromBody] CascadeRequest req) =>
            WithSession(req, client => PostAjaxList(client,
                "/Administration/CommonAjax/LoadSession",
                new Dictionary<string, string>
                {
                    ["programIds"] = req.ProgramId ?? "",
                    ["isAuthorized"] = "true"
                }));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Courses([FromBody] CascadeRequest req) =>
            WithSession(req, client => PostAjaxList(client,
                "/Administration/CommonAjax/LoadCourse",
                new Dictionary<string, string>
                {
                    ["programIds"] = req.ProgramId ?? "",
                    ["sessionIds"] = req.SessionId ?? ""
                }));

        // ── Core session/login handling ──────────────────────────────────────

        private async Task<IActionResult> WithSession(CascadeRequest? req, Func<HttpClient, Task<List<Item>>> work)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Json(new { success = false, error = "UMS username and password are required." });

            string baseUrl;
            try { baseUrl = UmsBase(req.Server); }
            catch (ArgumentException ex) { return Json(new { success = false, error = ex.Message }); }

            var cacheKey = $"ums-session|{baseUrl}|{req.Username.Trim().ToLowerInvariant()}";

            try
            {
                // Attempt 0 uses the cached cookie session; attempt 1 forces a fresh login.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var cookies = attempt == 0
                        ? _cache.GetOrCreate(cacheKey, e => { e.SlidingExpiration = SessionTtl; return new CookieContainer(); })!
                        : new CookieContainer();
                    if (attempt == 1)
                        _cache.Set(cacheKey, cookies, new MemoryCacheEntryOptions { SlidingExpiration = SessionTtl });

                    using var handler = new HttpClientHandler
                    {
                        CookieContainer = cookies,
                        AllowAutoRedirect = true,
                        UseCookies = true
                    };
                    using var client = new HttpClient(handler)
                    {
                        BaseAddress = new Uri(baseUrl),
                        Timeout = TimeSpan.FromSeconds(40)
                    };

                    var hasAuth = cookies.GetCookies(new Uri(baseUrl))[".UERP_AUTH"] != null;
                    if (!hasAuth)
                    {
                        var loginError = await LoginAsync(client, req.Username, req.Password);
                        if (loginError != null)
                            return Json(new { success = false, error = loginError });
                    }

                    var items = await work(client);
                    if (items.Count > 0 || attempt == 1)
                        return Json(new { success = true, items });
                    // Empty on first attempt → cookie may be stale; retry with fresh login.
                }
                return Json(new { success = true, items = new List<Item>() });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "UMS request failed: " + ex.Message });
            }
        }

        private static string UmsBase(string? server)
        {
            var s = (server ?? "").Trim();
            if (string.IsNullOrEmpty(s) || s == "0") return "https://ums.osl.team";
            if (!Regex.IsMatch(s, "^[A-Za-z0-9-]{1,10}$"))
                throw new ArgumentException($"Invalid server \"{s}\" — use a number like 2, or 0 for the base server.");
            return $"https://ums-{s}.osl.team";
        }

        private static async Task<string?> LoginAsync(HttpClient client, string username, string password)
        {
            var page = await client.GetAsync("/Account/Login");
            var html = await page.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

            var form = new Dictionary<string, string>
            {
                ["UserName"] = username,
                ["Password"] = password,
                ["Submit"] = "Log in"
            };
            if (!string.IsNullOrEmpty(token)) form["__RequestVerificationToken"] = token;

            var resp = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
            var finalPath = resp.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            if (finalPath.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
                return "UMS login failed — check username/password (and server number).";
            return null;
        }

        // ── Response parsing ─────────────────────────────────────────────────

        private static async Task<List<Item>> PostAjaxList(HttpClient client, string path, Dictionary<string, string> form)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(form)
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(request);
            var txt = await resp.Content.ReadAsStringAsync();
            return ParseJsonList(txt);
        }

        private static List<Item> ParseJsonList(string json)
        {
            var list = new List<Item>();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { return list; }

            using (doc)
            {
                var arr = doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Object)
                {
                    // UMS wraps lists in varying keys (returnProgramList, returnSessionList,
                    // returnCourse, …) — take the first array-valued property.
                    foreach (var prop in arr.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            arr = prop.Value;
                            break;
                        }
                    }
                }
                if (arr.ValueKind != JsonValueKind.Array) return list;

                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(el, "Value", "value", "Id", "id");
                    var name = GetString(el, "Text", "text", "Name", "name");
                    if (!string.IsNullOrEmpty(id) && id != "0" && !string.IsNullOrEmpty(name))
                        list.Add(new Item(id, name.Trim()));
                }
            }
            return list;
        }

        private static string GetString(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (el.TryGetProperty(k, out var v))
                    return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
            }
            return "";
        }

        private static List<Item> ParseSelectOptions(string html, string selectId)
        {
            var list = new List<Item>();
            var m = Regex.Match(html, $"<select[^>]*id=\"{selectId}\"[\\s\\S]*?</select>", RegexOptions.IgnoreCase);
            if (!m.Success) return list;

            foreach (Match om in Regex.Matches(m.Value, "<option[^>]*value=\"([^\"]*)\"[^>]*>([^<]*)</option>", RegexOptions.IgnoreCase))
            {
                var id = om.Groups[1].Value.Trim();
                var name = WebUtility.HtmlDecode(om.Groups[2].Value).Trim();
                if (id != "" && id != "0" && name != "")
                    list.Add(new Item(id, name));
            }
            return list;
        }
    }
}
