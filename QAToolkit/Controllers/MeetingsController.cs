using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class MeetingsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _config;

        public MeetingsController(ApplicationDbContext context, IWebHostEnvironment environment, IConfiguration config)
        {
            _context = context;
            _environment = environment;
            _config = config;
        }

        // GET: Meetings
        public async Task<IActionResult> Index(string? search, string? source)
        {
            var query = _context.Meetings.Include(m => m.ScreenNotes).AsQueryable();

            if (!string.IsNullOrEmpty(search))
                query = query.Where(m => m.Title.Contains(search)
                    || (m.Summary != null && m.Summary.Contains(search))
                    || (m.Participants != null && m.Participants.Contains(search)));

            if (!string.IsNullOrEmpty(source))
                query = query.Where(m => m.Source == source);

            ViewBag.Sources = await _context.Meetings
                .Where(m => m.Source != null)
                .Select(m => m.Source)
                .Distinct()
                .ToListAsync();
            ViewBag.Search = search;
            ViewBag.SelectedSource = source;
            ViewBag.ApiKey = _config["MeetingIngest:ApiKey"];

            return View(await query.OrderByDescending(m => m.MeetingDate).ToListAsync());
        }

        // GET: Meetings/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var meeting = await _context.Meetings
                .Include(m => m.ScreenNotes)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (meeting == null)
                return NotFound();

            meeting.ScreenNotes = meeting.ScreenNotes
                .OrderBy(n => n.CapturedAt ?? DateTime.MaxValue)
                .ThenBy(n => n.Id)
                .ToList();

            return View(meeting);
        }

        // POST: Meetings/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var meeting = await _context.Meetings
                .Include(m => m.ScreenNotes)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (meeting != null)
            {
                _context.MeetingScreenNotes.RemoveRange(meeting.ScreenNotes);
                _context.Meetings.Remove(meeting);
                await _context.SaveChangesAsync();

                try
                {
                    var imageDir = Path.Combine(_environment.WebRootPath, "uploads", "meetings", id.ToString());
                    if (Directory.Exists(imageDir))
                        Directory.Delete(imageDir, recursive: true);
                }
                catch { /* orphaned images are harmless */ }

                TempData["Success"] = "Meeting deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        // ===== Knowledge file (knowledge.json the desktop app fetches) =====

        // GET: Meetings/Knowledge — upload/download page
        public async Task<IActionResult> Knowledge()
        {
            var knowledge = await _context.MeetingKnowledges.FirstOrDefaultAsync();
            ViewBag.ApiKey = _config["MeetingIngest:ApiKey"];
            return View(knowledge);
        }

        // POST /admin/knowledge/upload
        [HttpPost("admin/knowledge/upload")]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(10_485_760)]
        public async Task<IActionResult> UploadKnowledge(IFormFile? file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Choose a file to upload.";
                return RedirectToAction(nameof(Knowledge));
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".md" && ext != ".markdown" && ext != ".txt")
            {
                TempData["Error"] = "Upload rejected — only Markdown (.md) files are allowed.";
                return RedirectToAction(nameof(Knowledge));
            }

            string content;
            using (var reader = new StreamReader(file.OpenReadStream()))
                content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                TempData["Error"] = "Upload rejected — the file is empty.";
                return RedirectToAction(nameof(Knowledge));
            }

            var knowledge = await _context.MeetingKnowledges.FirstOrDefaultAsync();
            if (knowledge == null)
            {
                knowledge = new MeetingKnowledge();
                _context.MeetingKnowledges.Add(knowledge);
            }

            knowledge.Content = content;
            knowledge.FileName = string.IsNullOrWhiteSpace(file.FileName)
                ? "knowledge.md"
                : Path.GetFileName(file.FileName);
            knowledge.Version += 1;
            knowledge.UpdatedAt = DateTimeHelper.BdNow;
            knowledge.UpdatedBy = User.Identity?.Name;
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Knowledge file uploaded — now version {knowledge.Version}.";
            return RedirectToAction(nameof(Knowledge));
        }

        // GET /admin/knowledge/download
        [HttpGet("admin/knowledge/download")]
        public async Task<IActionResult> DownloadKnowledge()
        {
            var knowledge = await _context.MeetingKnowledges.FirstOrDefaultAsync();
            if (knowledge == null)
                return NotFound("No knowledge file uploaded yet.");

            return File(System.Text.Encoding.UTF8.GetBytes(knowledge.Content),
                "text/markdown", knowledge.FileName);
        }

        // GET /api/meetings/knowledge — the desktop app fetches the current file
        [AllowAnonymous]
        [HttpGet("api/meetings/knowledge")]
        public async Task<IActionResult> KnowledgeApi()
        {
            if (!IsApiKeyValid(out var error))
                return Unauthorized(new { ok = false, error });

            var knowledge = await _context.MeetingKnowledges.FirstOrDefaultAsync();
            if (knowledge == null)
                return NotFound(new { ok = false, error = "No knowledge file uploaded yet." });

            Response.Headers["X-Knowledge-Version"] = knowledge.Version.ToString();
            return Content(knowledge.Content, "text/markdown; charset=utf-8");
        }

        // ===== Push API for the AI meeting desktop app =====

        // GET /api/meetings/ping — connectivity + API key check for the desktop app
        [AllowAnonymous]
        [HttpGet("api/meetings/ping")]
        public IActionResult Ping()
        {
            if (!IsApiKeyValid(out var error))
                return Unauthorized(new { ok = false, error });
            return Ok(new { ok = true, message = "QA Toolkit meeting ingest is ready." });
        }

        // POST /api/meetings/ingest — receives summary, transcript and screen notes
        [AllowAnonymous]
        [HttpPost("api/meetings/ingest")]
        [IgnoreAntiforgeryToken]
        [RequestSizeLimit(200_000_000)] // transcripts + base64 screenshots can be large
        public async Task<IActionResult> Ingest([FromBody] MeetingIngestRequest? request)
        {
            if (!IsApiKeyValid(out var error))
                return Unauthorized(new { ok = false, error });

            if (request == null)
                return BadRequest(new { ok = false, error = "Request body must be JSON." });

            var now = DateTimeHelper.BdNow;
            var uuid = string.IsNullOrWhiteSpace(request.Uuid) ? null : request.Uuid.Trim();

            // Upsert on the sender's uuid so re-sends update instead of duplicating
            Meeting? meeting = null;
            var updated = false;
            if (uuid != null)
                meeting = await _context.Meetings
                    .Include(m => m.ScreenNotes)
                    .FirstOrDefaultAsync(m => m.Uuid == uuid);

            if (meeting == null)
            {
                meeting = new Meeting { Uuid = uuid, MeetingDate = now, CreatedAt = now };
                _context.Meetings.Add(meeting);
            }
            else
            {
                updated = true;
                _context.MeetingScreenNotes.RemoveRange(meeting.ScreenNotes);
                meeting.ScreenNotes.Clear();
                try
                {
                    var oldDir = Path.Combine(_environment.WebRootPath, "uploads", "meetings", meeting.Id.ToString());
                    if (Directory.Exists(oldDir))
                        Directory.Delete(oldDir, recursive: true);
                }
                catch { /* orphaned images are harmless */ }
            }

            meeting.Title = string.IsNullOrWhiteSpace(request.Title)
                ? $"Meeting {now:MMM dd, yyyy HH:mm}"
                : request.Title.Trim();
            if (request.MeetingDate.HasValue)
                meeting.MeetingDate = request.MeetingDate.Value;
            meeting.DurationMinutes = request.DurationMinutes;
            meeting.Participants = request.Participants;
            meeting.Source = request.Source;
            meeting.Summary = request.Summary;
            meeting.Transcript = request.Transcript;
            meeting.CreatedBy = request.CreatedBy;

            await _context.SaveChangesAsync(); // need Id for the image folder

            var savedNotes = 0;
            var skippedImages = 0;
            if (request.ScreenNotes != null && request.ScreenNotes.Count > 0)
            {
                var imageDir = Path.Combine(_environment.WebRootPath, "uploads", "meetings", meeting.Id.ToString());
                Directory.CreateDirectory(imageDir);

                var index = 0;
                foreach (var note in request.ScreenNotes)
                {
                    index++;
                    string? imagePath = null;
                    if (!string.IsNullOrWhiteSpace(note.ImageBase64))
                    {
                        try
                        {
                            var base64 = note.ImageBase64;
                            var commaIdx = base64.IndexOf(',');
                            if (base64.StartsWith("data:") && commaIdx > 0)
                                base64 = base64[(commaIdx + 1)..]; // strip data URI prefix

                            var bytes = Convert.FromBase64String(base64);
                            var ext = Path.GetExtension(note.ImageFileName ?? "").ToLowerInvariant();
                            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif" && ext != ".webp")
                                ext = ".png";

                            var fileName = $"note-{index}{ext}";
                            await System.IO.File.WriteAllBytesAsync(Path.Combine(imageDir, fileName), bytes);
                            imagePath = $"/uploads/meetings/{meeting.Id}/{fileName}";
                        }
                        catch
                        {
                            skippedImages++; // bad base64 — keep the text note anyway
                        }
                    }

                    if (imagePath == null && string.IsNullOrWhiteSpace(note.Note))
                        continue; // nothing usable in this entry

                    _context.MeetingScreenNotes.Add(new MeetingScreenNote
                    {
                        MeetingId = meeting.Id,
                        CapturedAt = note.CapturedAt,
                        Note = note.Note,
                        ImagePath = imagePath
                    });
                    savedNotes++;
                }
                await _context.SaveChangesAsync();
            }

            return Ok(new
            {
                ok = true,
                id = meeting.Id,
                updated,
                url = Url.Action(nameof(Details), "Meetings", new { id = meeting.Id }, Request.Scheme),
                screenNotesSaved = savedNotes,
                imagesSkipped = skippedImages
            });
        }

        private bool IsApiKeyValid(out string error)
        {
            var configuredKey = _config["MeetingIngest:ApiKey"];
            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                error = "Meeting ingest is not configured on the server (MeetingIngest:ApiKey is empty).";
                return false;
            }

            var providedKey = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (providedKey != configuredKey)
            {
                error = "Invalid or missing X-Api-Key header.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
