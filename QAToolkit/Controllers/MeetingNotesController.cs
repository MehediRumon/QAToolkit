using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class MeetingNotesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public MeetingNotesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: MeetingNotes
        public async Task<IActionResult> Index(string? meetingWith)
        {
            var query = _context.MeetingNotes.AsQueryable();

            if (!string.IsNullOrEmpty(meetingWith))
                query = query.Where(m => m.MeetingWith == meetingWith);

            ViewBag.MeetingWiths = await _context.MeetingNotes
                .Where(m => m.MeetingWith != null)
                .Select(m => m.MeetingWith)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedMeetingWith = meetingWith;

            return View(await query.OrderByDescending(m => m.MeetingDate).ToListAsync());
        }

        // GET: MeetingNotes/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var meetingNote = await _context.MeetingNotes.FirstOrDefaultAsync(m => m.Id == id);
            if (meetingNote == null)
                return NotFound();

            return View(meetingNote);
        }

        // GET: MeetingNotes/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: MeetingNotes/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(MeetingNote meetingNote)
        {
            if (ModelState.IsValid)
            {
                meetingNote.CreatedAt = DateTimeHelper.BdNow;
                meetingNote.CreatedBy = User.Identity?.Name; // Set creator
                _context.Add(meetingNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Meeting note added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(meetingNote);
        }

        // GET: MeetingNotes/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var meetingNote = await _context.MeetingNotes.FindAsync(id);
            if (meetingNote == null)
                return NotFound();

            // Only creator can edit
            if (meetingNote.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own meeting notes.";
                return RedirectToAction(nameof(Index));
            }

            return View(meetingNote);
        }

        // POST: MeetingNotes/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, MeetingNote meetingNote)
        {
            if (id != meetingNote.Id)
                return NotFound();

            // Only creator can edit
            var existing = await _context.MeetingNotes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
            if (existing?.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own meeting notes.";
                return RedirectToAction(nameof(Index));
            }

            if (ModelState.IsValid)
            {
                try
                {
                    meetingNote.CreatedBy = existing.CreatedBy; // Preserve original creator
                    meetingNote.CreatedAt = existing.CreatedAt; // Preserve original creation date
                    meetingNote.UpdatedAt = DateTimeHelper.BdNow;
                    meetingNote.ModifiedBy = User.Identity?.Name;
                    _context.Update(meetingNote);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Meeting note updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!MeetingNoteExists(meetingNote.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(meetingNote);
        }

        // POST: MeetingNotes/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var meetingNote = await _context.MeetingNotes.FindAsync(id);
            if (meetingNote != null)
            {
                // Only creator can delete
                if (meetingNote.CreatedBy != User.Identity?.Name)
                {
                    TempData["Error"] = "You can only delete your own meeting notes.";
                    return RedirectToAction(nameof(Index));
                }

                _context.MeetingNotes.Remove(meetingNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Meeting note deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool MeetingNoteExists(int id)
        {
            return _context.MeetingNotes.Any(e => e.Id == id);
        }
    }
}
