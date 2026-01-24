using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class TestNotesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;

        public TestNotesController(ApplicationDbContext context, IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // GET: TestNotes
        public async Task<IActionResult> Index(string? project, string? status)
        {
            var currentUser = User.Identity?.Name;
            var query = _context.TestNotes.Where(t => t.CreatedBy == currentUser);

            if (!string.IsNullOrEmpty(project))
                query = query.Where(t => t.Project == project);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(t => t.Status == status);

            ViewBag.Projects = await _context.TestNotes
                .Where(t => t.CreatedBy == currentUser && t.Project != null)
                .Select(t => t.Project)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedProject = project;
            ViewBag.SelectedStatus = status;

            return View(await query.OrderByDescending(t => t.CreatedAt).ToListAsync());
        }

        // GET: TestNotes/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var testNote = await _context.TestNotes.FirstOrDefaultAsync(m => m.Id == id);
            if (testNote == null || testNote.CreatedBy != User.Identity?.Name)
                return NotFound();

            return View(testNote);
        }

        // GET: TestNotes/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: TestNotes/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TestNote testNote)
        {
            if (ModelState.IsValid)
            {
                testNote.CreatedAt = DateTimeHelper.BdNow;
                testNote.CreatedBy = User.Identity?.Name; // Set creator
                _context.Add(testNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Test note added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(testNote);
        }

        // GET: TestNotes/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var testNote = await _context.TestNotes.FindAsync(id);
            if (testNote == null || testNote.CreatedBy != User.Identity?.Name)
                return NotFound();

            return View(testNote);
        }

        // POST: TestNotes/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, TestNote testNote)
        {
            if (id != testNote.Id)
                return NotFound();

            // Get existing record to preserve CreatedBy and CreatedAt
            var existing = await _context.TestNotes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (existing == null || existing.CreatedBy != User.Identity?.Name)
                return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    testNote.CreatedBy = existing.CreatedBy;
                    testNote.CreatedAt = existing.CreatedAt;
                    testNote.UpdatedAt = DateTimeHelper.BdNow;
                    _context.Update(testNote);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Test note updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TestNoteExists(testNote.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(testNote);
        }

        // POST: TestNotes/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var testNote = await _context.TestNotes.FindAsync(id);
            if (testNote != null)
            {
                _context.TestNotes.Remove(testNote);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Test note deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool TestNoteExists(int id)
        {
            return _context.TestNotes.Any(e => e.Id == id);
        }
    }
}
