using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class UsersModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IRoleRepository _roles;

    public List<AppUser> Users { get; set; } = new();
    public IReadOnlyList<Role> AllRoles { get; set; } = new List<Role>();

    [BindProperty] public int TargetUserId { get; set; }
    [BindProperty] public string RoleName { get; set; } = string.Empty;

    public UsersModel(AppDbContext db, IRoleRepository roles)
    {
        _db = db;
        _roles = roles;
    }

    public async Task OnGetAsync()
    {
        Users = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Username)
            .ToListAsync();
        AllRoles = await _roles.GetAllAsync();
    }

    public async Task<IActionResult> OnPostGrantAsync()
    {
        await _roles.AssignRoleAsync(TargetUserId, RoleName);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync()
    {
        await _roles.RemoveRoleAsync(TargetUserId, RoleName);
        return RedirectToPage();
    }
}