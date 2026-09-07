using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

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
        /// 現在ログイン中のユーザーの絵文字を取得または設定します。
        /// </summary>
        public string UserEmoji { get; set; } = "👤";

        /// <summary>
        /// アカウント情報変更時のメッセージです。
        /// </summary>
        public string? AccountMessage { get; set; }

        /// <summary>
        /// アカウント情報変更時のエラーメッセージです。
        /// </summary>
        public string? AccountErrorMessage { get; set; }

        /// <summary>
        /// GET リクエスト時にログイン中のユーザー情報を取得します。
        /// </summary>
        public void OnGet()
        {
            CurrentUser = User.Identity?.Name ?? "不明";

            string dbDir = System.Environment.GetEnvironmentVariable("DB_DIR") ?? "./data";
            string connectionString = $"Data Source={Path.Combine(dbDir, "habittracker.db")}";

            using var connection = new SqliteConnection(connectionString);
            var user = connection.QuerySingleOrDefault<User>("SELECT Emoji FROM Users WHERE Name = @Name", new { Name = CurrentUser });
            if (user != null)
            {
                UserEmoji = user.Emoji;
            }
        }

        /// <summary>
        /// ユーザー名・パスワード変更フォーム送信処理（標準フォーム POST 処理）です。
        /// </summary>
        public async Task<IActionResult> OnPostChangeAccountAsync([FromForm] string currentPassword, [FromForm] string newPassword, [FromForm] string? newUsername)
        {
            CurrentUser = User.Identity?.Name ?? "不明";

            if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
            {
                AccountErrorMessage = "現在のパスワードと新しいパスワードを両方入力してください。";
                OnGet();
                return Page();
            }

            string dbDir = System.Environment.GetEnvironmentVariable("DB_DIR") ?? "./data";
            string connectionString = $"Data Source={Path.Combine(dbDir, "habittracker.db")}";

            using var connection = new SqliteConnection(connectionString);
            var user = connection.QuerySingleOrDefault<User>("SELECT Id, Name, Password, Emoji FROM Users WHERE Name = @Name", new { Name = CurrentUser });

            if (user == null || user.Password != currentPassword)
            {
                AccountErrorMessage = "現在のパスワードが正しくありません。";
                OnGet();
                return Page();
            }

            string updatedUsername = CurrentUser;
            if (!string.IsNullOrWhiteSpace(newUsername) && newUsername.Trim() != CurrentUser)
            {
                string candidate = newUsername.Trim();
                bool duplicate = connection.ExecuteScalar<bool>("SELECT COUNT(1) FROM Users WHERE Name = @Name AND Id <> @Id", new { Name = candidate, Id = user.Id });
                if (duplicate)
                {
                    AccountErrorMessage = "指定されたユーザー名は既に使用されています。";
                    OnGet();
                    return Page();
                }
                updatedUsername = candidate;
            }

            connection.Execute("UPDATE Users SET Password = @NewPassword, Name = @NewUsername WHERE Id = @Id",
                new { NewPassword = newPassword.Trim(), NewUsername = updatedUsername, Id = user.Id });

            if (updatedUsername != CurrentUser)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, updatedUsername),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
                };
                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            }

            AccountMessage = "🔒 アカウント情報が正常に変更されました！";
            CurrentUser = updatedUsername;
            UserEmoji = user.Emoji;
            return Page();
        }
    }
}
