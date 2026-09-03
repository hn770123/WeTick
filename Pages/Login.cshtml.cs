using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Dapper;

namespace HabitTracker.Pages
{
    /// <summary>
    /// ログインページの PageModel です。認証ログインおよび新規ユーザー自動登録を行います。
    /// </summary>
    public class LoginModel : PageModel
    {
        [BindProperty]
        public string Username { get; set; } = string.Empty;

        [BindProperty]
        public string Password { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }

        public void OnGet(string? error)
        {
            ErrorMessage = error;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "ユーザー名とパスワードを入力してください。";
                return Page();
            }

            string dbDir = Environment.GetEnvironmentVariable("DB_DIR") ?? "./data";
            string connectionString = $"Data Source={System.IO.Path.Combine(dbDir, "habittracker.db")}";

            using var connection = new SqliteConnection(connectionString);
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                "SELECT Id, Name, Email, Emoji, Password, CreatedAt FROM Users WHERE Name = @Name",
                new { Name = Username });

            if (existingUser is null)
            {
                // ユーザーが存在しない場合は新規自動登録を行う
                string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                string email = $"{Username.ToLower()}@example.com";
                string insertSql = @"
                    INSERT INTO Users (Name, Email, Emoji, Password, CreatedAt)
                    VALUES (@Name, @Email, '👤', @Password, @CreatedAt);
                    SELECT last_insert_rowid();";

                int newUserId = await connection.ExecuteScalarAsync<int>(insertSql, new
                {
                    Name = Username,
                    Email = email,
                    Password = Password,
                    CreatedAt = createdAt
                });

                var newClaims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, Username),
                    new Claim(ClaimTypes.NameIdentifier, newUserId.ToString())
                };

                var newIdentity = new ClaimsIdentity(newClaims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(newIdentity));

                return RedirectToPage("/Index");
            }

            // 既存ユーザーの場合はパスワードをチェック
            if (existingUser.Password == Password)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, existingUser.Name),
                    new Claim(ClaimTypes.NameIdentifier, existingUser.Id.ToString())
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                return RedirectToPage("/Index");
            }

            ErrorMessage = "ユーザー名またはパスワードが違います。";
            return Page();
        }
    }
}
