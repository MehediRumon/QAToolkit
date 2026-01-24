using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class CredentialsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CredentialsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Credentials
        public async Task<IActionResult> Index(string? type, string? environment)
        {
            var currentUser = User.Identity?.Name;
            var query = _context.Credentials.Where(c => c.CreatedBy == currentUser);

            if (!string.IsNullOrEmpty(type))
                query = query.Where(c => c.Type == type);

            if (!string.IsNullOrEmpty(environment))
                query = query.Where(c => c.Environment == environment);

            ViewBag.Types = await _context.Credentials
                .Where(c => c.CreatedBy == currentUser && c.Type != null)
                .Select(c => c.Type)
                .Distinct()
                .ToListAsync();

            ViewBag.Environments = await _context.Credentials
                .Where(c => c.CreatedBy == currentUser && c.Environment != null)
                .Select(c => c.Environment)
                .Distinct()
                .ToListAsync();

            ViewBag.SelectedType = type;
            ViewBag.SelectedEnvironment = environment;

            return View(await query.OrderBy(c => c.Environment).ThenBy(c => c.Type).ToListAsync());
        }

        // GET: Credentials/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Credentials/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Credential credential)
        {
            if (ModelState.IsValid)
            {
                credential.CreatedAt = DateTimeHelper.BdNow;
                credential.CreatedBy = User.Identity?.Name; // Set creator
                _context.Add(credential);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Credential added successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(credential);
        }

        // GET: Credentials/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var credential = await _context.Credentials.FindAsync(id);
            if (credential == null || credential.CreatedBy != User.Identity?.Name)
                return NotFound();

            return View(credential);
        }

        // POST: Credentials/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Credential credential)
        {
            if (id != credential.Id)
                return NotFound();

            // Get existing record to preserve CreatedBy and CreatedAt
            var existing = await _context.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (existing == null || existing.CreatedBy != User.Identity?.Name)
                return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    credential.CreatedBy = existing.CreatedBy;
                    credential.CreatedAt = existing.CreatedAt;
                    credential.UpdatedAt = DateTimeHelper.BdNow;
                    _context.Update(credential);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Credential updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!CredentialExists(credential.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(credential);
        }

        // POST: Credentials/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var credential = await _context.Credentials.FindAsync(id);
            if (credential != null)
            {
                _context.Credentials.Remove(credential);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Credential deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool CredentialExists(int id)
        {
            return _context.Credentials.Any(e => e.Id == id);
        }
    }
}
