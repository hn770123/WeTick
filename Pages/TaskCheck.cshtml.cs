using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HabitTracker.Pages
{
    /// <summary>
    /// タスクのワンタップ実行記録ページの PageModel です。認証が要求されます。
    /// </summary>
    [Authorize]
    public class TaskCheckModel : PageModel
    {
        public string CurrentUser { get; set; } = string.Empty;

        public void OnGet()
        {
            CurrentUser = User.Identity?.Name ?? "不明";
        }
    }
}
