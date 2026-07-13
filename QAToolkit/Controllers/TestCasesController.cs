using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class TestCasesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public TestCasesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: TestCases
        public async Task<IActionResult> Index(string? project, string? search)
        {
            var query = _context.TestCaseDocs.AsQueryable();

            if (!string.IsNullOrEmpty(project))
                query = query.Where(t => t.Project == project);

            if (!string.IsNullOrEmpty(search))
                query = query.Where(t => t.Title.Contains(search)
                    || (t.Module != null && t.Module.Contains(search)));

            ViewBag.Projects = await _context.TestCaseDocs
                .Where(t => t.Project != null)
                .Select(t => t.Project)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();
            ViewBag.SelectedProject = project;
            ViewBag.Search = search;

            var docs = await query
                .Select(t => new TestCaseDoc
                {
                    Id = t.Id,
                    Title = t.Title,
                    Project = t.Project,
                    Module = t.Module,
                    IsPublic = t.IsPublic,
                    CreatedBy = t.CreatedBy,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt
                }) // skip HtmlContent — can be large
                .OrderBy(t => t.Project)
                .ThenBy(t => t.Module)
                .ThenByDescending(t => t.CreatedAt)
                .ToListAsync();

            return View(docs);
        }

        // GET: TestCases/Details/5 — public docs accessible without login
        [AllowAnonymous]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var doc = await _context.TestCaseDocs.FirstOrDefaultAsync(t => t.Id == id);
            if (doc == null)
                return NotFound();

            if (!doc.IsPublic && User.Identity?.IsAuthenticated != true)
                return RedirectToAction("Login", "Account");

            return View(doc);
        }

        // GET: TestCases/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: TestCases/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string title, string? project, string? module, bool isPublic, IFormFile htmlFile)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                TempData["Error"] = "Title is required.";
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

            var doc = new TestCaseDoc
            {
                Title = title.Trim(),
                Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
                Module = string.IsNullOrWhiteSpace(module) ? null : module.Trim(),
                HtmlContent = htmlContent,
                IsPublic = isPublic,
                CreatedBy = User.Identity?.Name,
                CreatedAt = DateTimeHelper.BdNow
            };

            _context.Add(doc);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Test case document uploaded successfully!";
            return RedirectToAction(nameof(Details), new { id = doc.Id });
        }

        // GET: TestCases/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var doc = await _context.TestCaseDocs.FindAsync(id);
            if (doc == null)
                return NotFound();

            return View(doc);
        }

        // POST: TestCases/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string title, string? project, string? module, bool isPublic, IFormFile? htmlFile)
        {
            var doc = await _context.TestCaseDocs.FindAsync(id);
            if (doc == null)
                return NotFound();

            if (string.IsNullOrWhiteSpace(title))
            {
                TempData["Error"] = "Title is required.";
                return View(doc);
            }

            doc.Title = title.Trim();
            doc.Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
            doc.Module = string.IsNullOrWhiteSpace(module) ? null : module.Trim();
            doc.IsPublic = isPublic;
            doc.UpdatedAt = DateTimeHelper.BdNow;

            // Only update HTML if a new file is uploaded
            if (htmlFile != null && htmlFile.Length > 0)
            {
                using var reader = new StreamReader(htmlFile.OpenReadStream());
                doc.HtmlContent = await reader.ReadToEndAsync();
            }

            _context.Update(doc);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Test case document updated successfully!";
            return RedirectToAction(nameof(Details), new { id = doc.Id });
        }

        // POST: TestCases/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var doc = await _context.TestCaseDocs.FindAsync(id);
            if (doc != null)
            {
                _context.TestCaseDocs.Remove(doc);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Test case document deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
