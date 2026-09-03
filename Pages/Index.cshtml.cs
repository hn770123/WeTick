using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HabitTracker.Pages
{
    /// <summary>
    /// メインダッシュボードページの PageModel です。認証が要求されます。
    /// </summary>
    [Authorize]
    public class IndexModel : PageModel
    {
        public string CurrentUser { get; set; } = string.Empty;

        public void OnGet()
        {
            CurrentUser = User.Identity?.Name ?? "不明";
        }
    }
}
