using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class ScriptFilesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xls", ".xlsx", ".csv", ".json", ".txt", ".pdf", ".docx", ".doc", ".zip", ".xml", ".tsv"
        };

        public ScriptFilesController(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        private string UploadDir =>
            Path.Combine(_config["Playwright:WorkingDirectory"] ?? @"C:\qa-scripts", "script-uploads");

        public async Task<IActionResult> Index()
        {
            var files = await _context.ScriptFiles.OrderByDescending(f => f.UploadedAt).ToListAsync();
            ViewBag.UploadDir = UploadDir;
            return View(files);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile file, string? description)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select a file to upload.";
                return RedirectToAction(nameof(Index));
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                TempData["Error"] = $"File type '{ext}' is not allowed. Allowed: {string.Join(", ", AllowedExtensions)}";
                return RedirectToAction(nameof(Index));
            }

            if (file.Length > 50 * 1024 * 1024)
            {
                TempData["Error"] = "File is too large (max 50 MB).";
                return RedirectToAction(nameof(Index));
            }

            // Normalize filename: lowercase, spaces → underscores
            var fileName = Path.GetFileNameWithoutExtension(file.FileName)
                               .ToLowerInvariant()
                               .Replace(' ', '_')
                               .Replace('/', '_')
                               .Replace('\\', '_') + ext;

            Directory.CreateDirectory(UploadDir);
            var storedPath = Path.Combine(UploadDir, fileName);

            await using (var stream = System.IO.File.Create(storedPath))
                await file.CopyToAsync(stream);

            // Upsert — replace existing record if same filename
            var existing = await _context.ScriptFiles.FirstOrDefaultAsync(f => f.FileName == fileName);
            if (existing != null)
            {
                existing.OriginalName = file.FileName;
                existing.Description = string.IsNullOrWhiteSpace(description) ? existing.Description : description.Trim();
                existing.FileSizeBytes = file.Length;
                existing.ContentType = file.ContentType;
                existing.StoredPath = storedPath;
                existing.UploadedBy = User.Identity?.Name ?? "";
                existing.UploadedAt = DateTimeHelper.BdNow;
                TempData["Success"] = $"\"{fileName}\" replaced successfully.";
            }
            else
            {
                _context.ScriptFiles.Add(new ScriptFile
                {
                    FileName = fileName,
                    OriginalName = file.FileName,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    FileSizeBytes = file.Length,
                    ContentType = file.ContentType,
                    StoredPath = storedPath,
                    UploadedBy = User.Identity?.Name ?? "",
                    UploadedAt = DateTimeHelper.BdNow
                });
                TempData["Success"] = $"\"{fileName}\" uploaded successfully.";
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadAjax(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, error = "No file selected." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return Json(new { success = false, error = $"File type '{ext}' not allowed." });

            if (file.Length > 50 * 1024 * 1024)
                return Json(new { success = false, error = "File too large (max 50 MB)." });

            var fileName = Path.GetFileNameWithoutExtension(file.FileName)
                               .ToLowerInvariant()
                               .Replace(' ', '_')
                               .Replace('/', '_')
                               .Replace('\\', '_') + ext;

            Directory.CreateDirectory(UploadDir);
            var storedPath = Path.Combine(UploadDir, fileName);

            await using (var stream = System.IO.File.Create(storedPath))
                await file.CopyToAsync(stream);

            var existing = await _context.ScriptFiles.FirstOrDefaultAsync(f => f.FileName == fileName);
            if (existing != null)
            {
                existing.OriginalName = file.FileName;
                existing.FileSizeBytes = file.Length;
                existing.ContentType = file.ContentType;
                existing.StoredPath = storedPath;
                existing.UploadedBy = User.Identity?.Name ?? "";
                existing.UploadedAt = DateTimeHelper.BdNow;
            }
            else
            {
                _context.ScriptFiles.Add(new ScriptFile
                {
                    FileName = fileName,
                    OriginalName = file.FileName,
                    FileSizeBytes = file.Length,
                    ContentType = file.ContentType,
                    StoredPath = storedPath,
                    UploadedBy = User.Identity?.Name ?? "",
                    UploadedAt = DateTimeHelper.BdNow
                });
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, path = storedPath, fileName });
        }

        public async Task<IActionResult> Download(int id)
        {
            var file = await _context.ScriptFiles.FindAsync(id);
            if (file == null) return NotFound();
            if (!System.IO.File.Exists(file.StoredPath)) return NotFound();

            var bytes = await System.IO.File.ReadAllBytesAsync(file.StoredPath);
            var contentType = file.ContentType ?? "application/octet-stream";
            return File(bytes, contentType, file.OriginalName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var file = await _context.ScriptFiles.FindAsync(id);
            if (file != null)
            {
                try { System.IO.File.Delete(file.StoredPath); } catch { }
                _context.ScriptFiles.Remove(file);
                await _context.SaveChangesAsync();
                TempData["Success"] = $"\"{file.FileName}\" deleted.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
