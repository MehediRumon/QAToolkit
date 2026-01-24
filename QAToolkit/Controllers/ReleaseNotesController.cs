using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class ReleaseNotesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ReleaseNotesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: ReleaseNotes
        public async Task<IActionResult> Index(string? project, string? search)
        {
            var query = _context.ReleaseNotes.AsQueryable();

            if (!string.IsNullOrEmpty(project))
                query = query.Where(r => r.Project == project);

            if (!string.IsNullOrEmpty(search))
            {
                search = search.ToLower();
                query = query.Where(r => 
                    r.Title.ToLower().Contains(search) ||
                    r.Version.ToLower().Contains(search) ||
                    r.Features.ToLower().Contains(search) ||
                    r.Project.ToLower().Contains(search));
            }

            ViewBag.Projects = await _context.ReleaseNotes
                .Select(r => r.Project)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedProject = project;
            ViewBag.SearchTerm = search;

            return View(await query.OrderByDescending(r => r.ReleaseDate).ToListAsync());
        }

        // GET: ReleaseNotes/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var releaseNote = await _context.ReleaseNotes.FirstOrDefaultAsync(m => m.Id == id);
            if (releaseNote == null)
                return NotFound();

            return View(releaseNote);
        }

        // GET: ReleaseNotes/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: ReleaseNotes/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ReleaseNote releaseNote)
        {
            if (ModelState.IsValid)
            {
                releaseNote.CreatedAt = DateTimeHelper.BdNow;
                releaseNote.CreatedBy = User.Identity?.Name; // Set creator
                _context.Add(releaseNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Release note added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(releaseNote);
        }

        // GET: ReleaseNotes/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var releaseNote = await _context.ReleaseNotes.FindAsync(id);
            if (releaseNote == null)
                return NotFound();

            // Only creator can edit
            if (releaseNote.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own release notes.";
                return RedirectToAction(nameof(Index));
            }

            return View(releaseNote);
        }

        // POST: ReleaseNotes/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, ReleaseNote releaseNote)
        {
            if (id != releaseNote.Id)
                return NotFound();

            // Only creator can edit
            var existing = await _context.ReleaseNotes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            if (existing?.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own release notes.";
                return RedirectToAction(nameof(Index));
            }

            if (ModelState.IsValid)
            {
                try
                {
                    releaseNote.CreatedBy = existing.CreatedBy; // Preserve original creator
                    releaseNote.CreatedAt = existing.CreatedAt; // Preserve original creation date
                    releaseNote.ModifiedAt = DateTimeHelper.BdNow;
                    releaseNote.ModifiedBy = User.Identity?.Name;
                    _context.Update(releaseNote);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Release note updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ReleaseNoteExists(releaseNote.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(releaseNote);
        }

        // POST: ReleaseNotes/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var releaseNote = await _context.ReleaseNotes.FindAsync(id);
            if (releaseNote != null)
            {
                // Only creator can delete
                if (releaseNote.CreatedBy != User.Identity?.Name)
                {
                    TempData["Error"] = "You can only delete your own release notes.";
                    return RedirectToAction(nameof(Index));
                }

                _context.ReleaseNotes.Remove(releaseNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Release note deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool ReleaseNoteExists(int id)
        {
            return _context.ReleaseNotes.Any(e => e.Id == id);
        }
    }
}
