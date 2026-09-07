using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

namespace HabitTracker.Pages
{
    /// <summary>
    /// メインダッシュボードページの PageModel です。認証が要求されます。
    /// </summary>
    [Authorize]
    public class IndexModel : PageModel
    {
        public string CurrentUser { get; set; } = string.Empty;
        public List<GroupDto> UserGroups { get; set; } = new();

        public void OnGet()
        {
            CurrentUser = User.Identity?.Name ?? "不明";

            string dbDir = System.Environment.GetEnvironmentVariable("DB_DIR") ?? "./data";
            string connectionString = $"Data Source={Path.Combine(dbDir, "habittracker.db")}";

            using var connection = new SqliteConnection(connectionString);
            var user = connection.QuerySingleOrDefault<User>("SELECT Id FROM Users WHERE Name = @Name", new { Name = CurrentUser });
            if (user != null)
            {
                UserGroups = connection.Query<GroupDto>(@"
                    SELECT g.Id, g.Name, g.InviteCode, g.CreatedAt
                    FROM Groups g INNER JOIN GroupMembers gm ON g.Id = gm.GroupId
                    WHERE gm.UserId = @UserId ORDER BY g.Id DESC;", new { UserId = user.Id }).ToList();
            }
        }
    }
}
