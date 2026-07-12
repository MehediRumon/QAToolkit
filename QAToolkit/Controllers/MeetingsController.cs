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
            var meeting = new Meeting
            {
                Title = string.IsNullOrWhiteSpace(request.Title)
                    ? $"Meeting {now:MMM dd, yyyy HH:mm}"
                    : request.Title.Trim(),
                MeetingDate = request.MeetingDate ?? now,
                DurationMinutes = request.DurationMinutes,
                Participants = request.Participants,
                Source = request.Source,
                Summary = request.Summary,
                Transcript = request.Transcript,
                CreatedBy = request.CreatedBy,
                CreatedAt = now
            };

            _context.Meetings.Add(meeting);
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
