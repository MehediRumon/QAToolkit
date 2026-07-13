using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class LearningController : Controller
    {
        private readonly ApplicationDbContext _context;

        public LearningController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Learning
        public async Task<IActionResult> Index(string? status)
        {
            var query = _context.LearningTopics.AsQueryable();

            if (!string.IsNullOrEmpty(status))
                query = query.Where(t => t.Status == status);

            ViewBag.SelectedStatus = status;

            return View(await query.OrderByDescending(t => t.CreatedAt).ToListAsync());
        }

        // GET: Learning/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var topic = await _context.LearningTopics.FirstOrDefaultAsync(t => t.Id == id);
            if (topic == null)
                return NotFound();

            return View(topic);
        }

        // GET: Learning/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Learning/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(LearningTopic topic)
        {
            if (ModelState.IsValid)
            {
                topic.CreatedAt = DateTimeHelper.BdNow;
                topic.CreatedBy = User.Identity?.Name;
                _context.Add(topic);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Learning topic added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(topic);
        }

        // GET: Learning/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var topic = await _context.LearningTopics.FindAsync(id);
            if (topic == null)
                return NotFound();

            return View(topic);
        }

        // POST: Learning/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, LearningTopic topic)
        {
            if (id != topic.Id)
                return NotFound();

            var existing = await _context.LearningTopics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (existing == null)
                return NotFound();

            if (ModelState.IsValid)
            {
                topic.CreatedBy = existing.CreatedBy;
                topic.CreatedAt = existing.CreatedAt;
                topic.UpdatedAt = DateTimeHelper.BdNow;
                _context.Update(topic);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Learning topic updated successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(topic);
        }

        // POST: Learning/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var topic = await _context.LearningTopics.FindAsync(id);
            if (topic != null)
            {
                _context.LearningTopics.Remove(topic);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Learning topic deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
