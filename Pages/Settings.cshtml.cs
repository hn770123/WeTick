using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HabitTracker.Pages
{
    /// <summary>
    /// アプリケーション設定ページの PageModel です。認証が要求されます。
    /// </summary>
    [Authorize]
    public class SettingsModel : PageModel
    {
        /// <summary>
        /// 現在ログイン中のユーザー名を取得または設定します。
        /// </summary>
        public string CurrentUser { get; set; } = string.Empty;

        /// <summary>
        /// GET リクエスト時にログイン中のユーザー名を取得します。
        /// </summary>
        public void OnGet()
        {
            CurrentUser = User.Identity?.Name ?? "不明";
        }
    }
}
