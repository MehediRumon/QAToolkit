using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QAToolkit.Data;
using QAToolkit.Helpers;
using QAToolkit.Models;

namespace QAToolkit.Controllers
{
    [Authorize]
    public class UsersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public UsersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Users
        public async Task<IActionResult> Index()
        {
            if (!IsSuperAdmin())
                return Forbid();

            return View(await _context.Users.OrderByDescending(u => u.CreatedAt).ToListAsync());
        }

        // GET: Users/Create
        public IActionResult Create()
        {
            if (!IsSuperAdmin())
                return Forbid();

            return View();
        }

        // POST: Users/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(User user, string password)
        {
            if (!IsSuperAdmin())
                return Forbid();

            if (string.IsNullOrEmpty(password))
            {
                ModelState.AddModelError("", "Password is required");
                return View(user);
            }

            if (await _context.Users.AnyAsync(u => u.Email == user.Email))
            {
                ModelState.AddModelError("Email", "Email already exists");
                return View(user);
            }

            // Remove PasswordHash and CreatedAt from ModelState since we set them manually
            ModelState.Remove("PasswordHash");
            ModelState.Remove("CreatedAt");
            ModelState.Remove("LastLogin");

            if (ModelState.IsValid)
            {
                user.PasswordHash = AccountController.HashPassword(password);
                user.CreatedAt = DateTimeHelper.BdNow;
                _context.Add(user);
                await _context.SaveChangesAsync();
                TempData["Success"] = "User created successfully!";
                return RedirectToAction(nameof(Index));
            }
            return View(user);
        }

        // GET: Users/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (!IsSuperAdmin())
                return Forbid();

            if (id == null)
                return NotFound();

            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound();

            return View(user);
        }

        // POST: Users/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, User user)
        {
            if (!IsSuperAdmin())
                return Forbid();

            if (id != user.Id)
                return NotFound();

            // Remove fields from ModelState that we don't validate
            ModelState.Remove("PasswordHash");
            ModelState.Remove("CreatedAt");
            ModelState.Remove("LastLogin");

            if (ModelState.IsValid)
            {
                try
                {
                    var existingUser = await _context.Users.FindAsync(id);
                    if (existingUser == null)
                        return NotFound();

                    existingUser.Name = user.Name;
                    existingUser.Email = user.Email;
                    existingUser.IsActive = user.IsActive;

                    await _context.SaveChangesAsync();
                    TempData["Success"] = "User updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!UserExists(user.Id))
                        return NotFound();
                    throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(user);
        }

        // POST: Users/ResetPassword/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id, string newPassword)
        {
            if (!IsSuperAdmin())
                return Forbid();

            if (string.IsNullOrEmpty(newPassword))
            {
                TempData["Error"] = "Password cannot be empty";
                return RedirectToAction(nameof(Index));
            }

            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound();

            user.PasswordHash = AccountController.HashPassword(newPassword);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Password reset successfully for {user.Name}!";
            return RedirectToAction(nameof(Index));
        }

        // POST: Users/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!IsSuperAdmin())
                return Forbid();

            var user = await _context.Users.FindAsync(id);
            if (user != null)
            {
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();
                TempData["Success"] = "User deleted successfully!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool UserExists(int id)
        {
            return _context.Users.Any(e => e.Id == id);
        }

        private bool IsSuperAdmin()
        {
            return User.Claims.FirstOrDefault(c => c.Type == "IsSuperAdmin")?.Value == "True";
        }
    }
}
