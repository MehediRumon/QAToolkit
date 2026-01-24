using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class UrlResourcesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly HttpClient _httpClient;

        public UrlResourcesController(ApplicationDbContext context, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        // GET: UrlResources
        public async Task<IActionResult> Index(string? category, string? project)
        {
            var currentUser = User.Identity?.Name;
            var query = _context.UrlResources.Where(u => u.CreatedBy == currentUser);

            if (!string.IsNullOrEmpty(category))
                query = query.Where(u => u.Category == category);

            if (!string.IsNullOrEmpty(project))
                query = query.Where(u => u.Project == project);

            ViewBag.Categories = await _context.UrlResources
                .Where(u => u.CreatedBy == currentUser && u.Category != null)
                .Select(u => u.Category)
                .Distinct()
                .ToListAsync();

            ViewBag.Projects = await _context.UrlResources
                .Where(u => u.CreatedBy == currentUser && u.Project != null)
                .Select(u => u.Project)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedCategory = category;
            ViewBag.SelectedProject = project;

            return View(await query.OrderByDescending(u => u.IsFavorite).ThenBy(u => u.Name).ToListAsync());
        }

        // GET: UrlResources/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: UrlResources/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(UrlResource urlResource)
        {
            if (ModelState.IsValid)
            {
                urlResource.CreatedAt = DateTimeHelper.BdNow;
                urlResource.CreatedBy = User.Identity?.Name; // Set creator
                _context.Add(urlResource);
                await _context.SaveChangesAsync();
                TempData["Success"] = "URL added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(urlResource);
        }

        // GET: UrlResources/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var urlResource = await _context.UrlResources.FindAsync(id);
            if (urlResource == null || urlResource.CreatedBy != User.Identity?.Name)
                return NotFound();

            return View(urlResource);
        }

        // POST: UrlResources/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, UrlResource urlResource)
        {
            if (id != urlResource.Id)
                return NotFound();

            // Get existing record to preserve CreatedBy and CreatedAt
            var existing = await _context.UrlResources.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
            if (existing == null || existing.CreatedBy != User.Identity?.Name)
                return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    urlResource.CreatedBy = existing.CreatedBy;
                    urlResource.CreatedAt = existing.CreatedAt;
                    _context.Update(urlResource);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "URL updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!UrlResourceExists(urlResource.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(urlResource);
        }

        // POST: UrlResources/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var urlResource = await _context.UrlResources.FindAsync(id);
            if (urlResource != null)
            {
                _context.UrlResources.Remove(urlResource);
                await _context.SaveChangesAsync();
                TempData["Success"] = "URL deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: UrlResources/ToggleFavorite/5
        [HttpPost]
        public async Task<IActionResult> ToggleFavorite(int id)
        {
            var urlResource = await _context.UrlResources.FindAsync(id);
            if (urlResource != null)
            {
                urlResource.IsFavorite = !urlResource.IsFavorite;
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: UrlResources/CheckUrl/5
        [HttpPost]
        public async Task<IActionResult> CheckUrl(int id)
        {
            var urlResource = await _context.UrlResources.FindAsync(id);
            if (urlResource != null)
            {
                try
                {
                    var response = await _httpClient.GetAsync(urlResource.Url);
                    urlResource.IsAlive = response.IsSuccessStatusCode;
                }
                catch
                {
                    urlResource.IsAlive = false;
                }
                urlResource.LastChecked = DateTimeHelper.BdNow;
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // POST: UrlResources/CheckAllUrls
        [HttpPost]
        public async Task<IActionResult> CheckAllUrls()
        {
            var urls = await _context.UrlResources.ToListAsync();
            foreach (var url in urls)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url.Url);
                    url.IsAlive = response.IsSuccessStatusCode;
                }
                catch
                {
                    url.IsAlive = false;
                }
                url.LastChecked = DateTimeHelper.BdNow;
            }
            await _context.SaveChangesAsync();
            TempData["Success"] = "All URLs checked!";
            return RedirectToAction(nameof(Index));
        }

        private bool UrlResourceExists(int id)
        {
            return _context.UrlResources.Any(e => e.Id == id);
        }
    }
}
