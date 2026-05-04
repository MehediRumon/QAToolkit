using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class WorkflowsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public WorkflowsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Workflows
        public async Task<IActionResult> Index(string? category)
        {
            var query = _context.Workflows.AsQueryable();

            if (!string.IsNullOrEmpty(category))
                query = query.Where(w => w.Category == category);

            ViewBag.Categories = await _context.Workflows
                .Where(w => w.Category != null)
                .Select(w => w.Category)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedCategory = category;

            return View(await query.OrderByDescending(w => w.CreatedAt).ToListAsync());
        }

        // GET: Workflows/Details/5 — public workflows accessible without login
        [AllowAnonymous]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var workflow = await _context.Workflows.FirstOrDefaultAsync(w => w.Id == id);
            if (workflow == null)
                return NotFound();

            // If not public, require authentication
            if (!workflow.IsPublic && User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");

            return View(workflow);
        }

        // GET: Workflows/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Workflows/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string? category, bool isPublic, IFormFile htmlFile)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Error"] = "Workflow name is required.";
                return View();
            }

            if (htmlFile == null || htmlFile.Length == 0)
            {
                TempData["Error"] = "Please upload an HTML file.";
                return View();
            }

            string htmlContent;
            using (var reader = new StreamReader(htmlFile.OpenReadStream()))
            {
                htmlContent = await reader.ReadToEndAsync();
            }

            var workflow = new Workflow
            {
                Name = name,
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                HtmlContent = htmlContent,
                IsPublic = isPublic,
                CreatedBy = User.Identity?.Name,
                CreatedAt = DateTimeHelper.BdNow
            };

            _context.Add(workflow);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Workflow uploaded successfully!";
            return RedirectToAction(nameof(Details), new { id = workflow.Id });
        }

        // GET: Workflows/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var workflow = await _context.Workflows.FindAsync(id);
            if (workflow == null)
                return NotFound();

            if (workflow.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own workflows.";
                return RedirectToAction(nameof(Index));
            }

            return View(workflow);
        }

        // POST: Workflows/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string name, string? category, bool isPublic, IFormFile? htmlFile)
        {
            var workflow = await _context.Workflows.FindAsync(id);
            if (workflow == null)
                return NotFound();

            if (workflow.CreatedBy != User.Identity?.Name)
            {
                TempData["Error"] = "You can only edit your own workflows.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Error"] = "Workflow name is required.";
                return View(workflow);
            }

            workflow.Name = name;
            workflow.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            workflow.IsPublic = isPublic;
            workflow.UpdatedAt = DateTimeHelper.BdNow;

            // Only update HTML if a new file is uploaded
            if (htmlFile != null && htmlFile.Length > 0)
            {
                using var reader = new StreamReader(htmlFile.OpenReadStream());
                workflow.HtmlContent = await reader.ReadToEndAsync();
            }

            _context.Update(workflow);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Workflow updated successfully!";
            return RedirectToAction(nameof(Details), new { id = workflow.Id });
        }

        // POST: Workflows/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var workflow = await _context.Workflows.FindAsync(id);
            if (workflow != null)
            {
                if (workflow.CreatedBy != User.Identity?.Name)
                {
                    TempData["Error"] = "You can only delete your own workflows.";
                    return RedirectToAction(nameof(Index));
                }

                _context.Workflows.Remove(workflow);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Workflow deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
