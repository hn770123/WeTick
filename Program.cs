using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Dapper;
using HabitTracker.Components.Pages;

/// <summary>
/// アプリケーションのエントリポイントおよび初期設定を提供するメインクラスです。
/// </summary>
var builder = WebApplication.CreateBuilder(args);

// Cookie認証サービスの設定
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login"; // 未認証アクセス時の遷移先パス
        options.ExpireTimeSpan = TimeSpan.FromDays(7); // Cookieの有効期限
    });

// 認可サービスの設定
builder.Services.AddAuthorization();

// Razor Components (Razor Component SSR) 関連サービスの登録
builder.Services.AddRazorComponents();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// 認証・認可ミドルウェアの適用（適用順序が重要）
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

// SQLite データベースの格納ディレクトリを取得または設定
string dbDir = Environment.GetEnvironmentVariable("DB_DIR") ?? "./data";
Directory.CreateDirectory(dbDir);
string connectionString = $"Data Source={Path.Combine(dbDir, "habittracker.db")}";

// アプリケーション起動時に SQLite のテーブル構造（Phase 1 DB設計書に基づく）を初期化します。
InitializeDatabase(connectionString);

/// <summary>
/// データベースおよび各テーブルが存在しない場合に自動生成します。
/// </summary>
/// <param name="connStr">SQLite 接続文字列</param>
void InitializeDatabase(string connStr)
{
    using var connection = new SqliteConnection(connStr);
    connection.Open();

    // Users (ユーザーテーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Email TEXT NOT NULL,
            Emoji TEXT NOT NULL DEFAULT '👤',
            Password TEXT,
            CreatedAt TEXT NOT NULL
        );
    ");

    // 既存の Users テーブルに Emoji カラムが存在しない場合は追加する（マイグレーション）
    try
    {
        connection.Execute("ALTER TABLE Users ADD COLUMN Emoji TEXT NOT NULL DEFAULT '👤';");
    }
    catch
    {
        // カラムが既に存在する場合は無視します
    }

    // 既存の Users テーブルに Password カラムが存在しない場合は追加する（マイグレーション）
    try
    {
        connection.Execute("ALTER TABLE Users ADD COLUMN Password TEXT;");
    }
    catch
    {
        // カラムが既に存在する場合は無視します
    }

    // Groups (グループテーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Groups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            InviteCode TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );
    ");

    // GroupMembers (グループ所属テーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS GroupMembers (
            GroupId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            Role TEXT NOT NULL,
            PRIMARY KEY (GroupId, UserId),
            FOREIGN KEY (GroupId) REFERENCES Groups(Id),
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

    // Habits (習慣タスクテーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Habits (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            Title TEXT NOT NULL,
            Description TEXT,
            Emoji TEXT NOT NULL DEFAULT '📝',
            Frequency TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

    // 既存の Habits テーブルに Emoji カラムが存在しない場合は追加する（マイグレーション）
    try
    {
        connection.Execute("ALTER TABLE Habits ADD COLUMN Emoji TEXT NOT NULL DEFAULT '📝';");
    }
    catch
    {
        // カラムが既に存在する場合は無視します
    }

    // ExecutionLogs (実行記録テーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS ExecutionLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            HabitId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            ExecutedAt TEXT NOT NULL,
            Comment TEXT,
            FOREIGN KEY (HabitId) REFERENCES Habits(Id),
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

    // Likes (いいね・リアクションテーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Likes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ExecutionLogId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            ReactionType TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (ExecutionLogId) REFERENCES ExecutionLogs(Id),
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

    // Comments (タイムライン投稿コメントテーブル)
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Comments (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ExecutionLogId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            CommentText TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (ExecutionLogId) REFERENCES ExecutionLogs(Id),
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

    // デフォルトのテスト用初期ユーザーが存在しない場合は追加
    connection.Execute(@"
        INSERT INTO Users (Id, Name, Email, Emoji, Password, CreatedAt)
        SELECT 1, 'admin', 'test@example.com', '👤', 'secret-password', '2025-01-01T00:00:00Z'
        WHERE NOT EXISTS (SELECT 1 FROM Users WHERE Id = 1);
    ");

    // 初期ユーザーのパスワードが未設定の場合はデフォルトパスワードを設定
    connection.Execute(@"
        UPDATE Users SET Password = 'secret-password' WHERE Id = 1 AND (Password IS NULL OR Password = '');
    ");
}

// ------------------------------------------------------------
// ルーティング定義 (Hono風 Minimal API)
// ------------------------------------------------------------

/// <summary>
/// ログイン画面 UI エンドポイント（GET）
/// Razor Component (Login.razor) を使って描画します。
/// </summary>
app.MapGet("/login", (HttpContext context) =>
{
    string? errorMessage = context.Request.Query["error"].ToString();
    return new RazorComponentResult<Login>(new
    {
        ErrorMessage = string.IsNullOrEmpty(errorMessage) ? null : errorMessage
    });
}).AllowAnonymous();

/// <summary>
/// ログイン処理 エンドポイント（POST）
/// ユーザーが存在しない場合は自動で新規登録を行い、既存ユーザーの場合はパスワード検証を行います。
/// </summary>
app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    string username = form["Username"].ToString();
    string password = form["Password"].ToString();

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("ユーザー名とパスワードを入力してください。"));
    }

    using var connection = new SqliteConnection(connectionString);
    var existingUser = connection.QuerySingleOrDefault<User>(
        "SELECT Id, Name, Email, Emoji, Password, CreatedAt FROM Users WHERE Name = @Name",
        new { Name = username });

    if (existingUser is null)
    {
        // ユーザーが存在しない場合は新規自動登録を行う
        string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string email = $"{username.ToLower()}@example.com";
        string insertSql = @"
            INSERT INTO Users (Name, Email, Emoji, Password, CreatedAt)
            VALUES (@Name, @Email, '👤', @Password, @CreatedAt);
            SELECT last_insert_rowid();";

        int newUserId = connection.ExecuteScalar<int>(insertSql, new
        {
            Name = username,
            Email = email,
            Password = password,
            CreatedAt = createdAt
        });

        var newClaims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.NameIdentifier, newUserId.ToString())
        };

        var newIdentity = new ClaimsIdentity(newClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(newIdentity));

        return Results.Redirect("/");
    }

    // 既存ユーザーの場合はパスワードをチェック
    if (existingUser.Password == password)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, existingUser.Name),
            new Claim(ClaimTypes.NameIdentifier, existingUser.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Results.Redirect("/");
    }

    return Results.Redirect("/login?error=" + Uri.EscapeDataString("ユーザー名またはパスワードが違います。"));
}).AllowAnonymous();

/// <summary>
/// ログアウト処理 エンドポイント（GET）
/// </summary>
app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

/// <summary>
/// ダッシュボード / メイン画面 UI エンドポイント
/// Razor Component (Index.razor) を使って描画します。
/// ボトムナビゲーションにより「タイムライン」「タスクのチェック」「設定（タスク/パスワード）」の3画面を切り替えて表示します。
/// デフォルトの表示画面は「タイムライン」です。
/// </summary>
app.MapGet("/", (HttpContext context) =>
{
    string currentUser = context.User.Identity?.Name ?? "不明";
    return new RazorComponentResult<HabitTracker.Components.Pages.Index>(new
    {
        CurrentUser = currentUser
    });
}).RequireAuthorization();

/// <summary>
/// データベース状態確認用エンドポイント
/// </summary>
app.MapGet("/health/db", () =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
    return Results.Ok(new { status = "ok", tables = tables });
});

// ------------------------------------------------------------
// ユーザー管理 API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// 現在ログイン中のユーザー情報を取得します。
/// </summary>
app.MapGet("/api/users/me", (HttpContext context) =>
{
    string? username = context.User.Identity?.Name;
    if (string.IsNullOrEmpty(username))
    {
        return Results.Unauthorized();
    }

    using var connection = new SqliteConnection(connectionString);
    var user = connection.QuerySingleOrDefault<User>(
        "SELECT Id, Name, Email, Emoji, CreatedAt FROM Users WHERE Name = @Name",
        new { Name = username });

    return user is not null ? Results.Ok(user) : Results.NotFound(new { message = "ユーザー情報が見つかりません。" });
}).RequireAuthorization();

/// <summary>
/// 指定された ID のユーザー情報を取得します。
/// </summary>
app.MapGet("/api/users/{id:int}", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var user = connection.QuerySingleOrDefault<User>(
        "SELECT Id, Name, Email, Emoji, CreatedAt FROM Users WHERE Id = @Id",
        new { Id = id });
    return user is not null ? Results.Ok(user) : Results.NotFound(new { message = "指定されたユーザーが見つかりません。" });
});

/// <summary>
/// ユーザー情報を更新（名前・絵文字の更新）します。
/// </summary>
app.MapPut("/api/users/{id:int}", (int id, UpdateUserDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name))
    {
        return Results.BadRequest(new { message = "ユーザー名は必須項目です。" });
    }

    string emoji = string.IsNullOrWhiteSpace(dto.Emoji) ? "👤" : dto.Emoji;

    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        UPDATE Users
        SET Name = @Name, Emoji = @Emoji
        WHERE Id = @Id;";

    int rowsAffected = connection.Execute(sql, new { Id = id, dto.Name, Emoji = emoji });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "更新対象のユーザーが見つかりません。" });
    }

    return Results.Ok(new { message = "ユーザー情報が正常に更新されました。", id, name = dto.Name, emoji });
});

/// <summary>
/// ログイン中のユーザーのパスワードおよびユーザー名を変更します。
/// ユーザー名が変更された場合は重複チェックを行い、DBおよび認証Cookie（Claims）を更新します。
/// </summary>
app.MapPost("/api/users/change-password", async (HttpContext context, ChangePasswordDto dto) =>
{
    // 現在認証されているユーザー名を取得
    string? currentUsername = context.User.Identity?.Name;
    if (string.IsNullOrEmpty(currentUsername))
    {
        return Results.Unauthorized();
    }

    // パスワード入力チェック
    if (string.IsNullOrWhiteSpace(dto.CurrentPassword) || string.IsNullOrWhiteSpace(dto.NewPassword))
    {
        return Results.BadRequest(new { message = "現在のパスワードと新しいパスワードを両方入力してください。" });
    }

    using var connection = new SqliteConnection(connectionString);
    var user = connection.QuerySingleOrDefault<User>(
        "SELECT Id, Name, Password FROM Users WHERE Name = @Name",
        new { Name = currentUsername });

    if (user is null)
    {
        return Results.NotFound(new { message = "ユーザーが見つかりません。" });
    }

    // 現在のパスワードの正当性を検証
    if (user.Password != dto.CurrentPassword)
    {
        return Results.BadRequest(new { message = "現在のパスワードが正しくありません。" });
    }

    // 新しいユーザー名が指定されている場合の重複確認および決定
    string updatedUsername = currentUsername;
    if (!string.IsNullOrWhiteSpace(dto.NewUsername) && dto.NewUsername.Trim() != currentUsername)
    {
        string candidateName = dto.NewUsername.Trim();
        bool isDuplicate = connection.ExecuteScalar<bool>(
            "SELECT COUNT(1) FROM Users WHERE Name = @Name AND Id <> @Id",
            new { Name = candidateName, Id = user.Id });

        if (isDuplicate)
        {
            return Results.BadRequest(new { message = "指定されたユーザー名は既に使用されています。" });
        }
        updatedUsername = candidateName;
    }

    // データベースのパスワードおよびユーザー名を更新
    connection.Execute(
        "UPDATE Users SET Password = @NewPassword, Name = @NewUsername WHERE Id = @Id",
        new { NewPassword = dto.NewPassword.Trim(), NewUsername = updatedUsername, Id = user.Id });

    // ユーザー名が変更された場合は、Cookie認証のClaimを更新（再サインイン）
    if (updatedUsername != currentUsername)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, updatedUsername),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    return Results.Ok(new { message = "ユーザー情報（パスワード / ユーザー名）が正常に変更されました。", newUsername = updatedUsername });
}).RequireAuthorization();

// ------------------------------------------------------------
// 習慣（Habit）管理 API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// 指定されたユーザーの習慣一覧を取得します。
/// </summary>
app.MapGet("/api/habits", (int? userId) =>
{
    int targetUserId = userId ?? 1; // 指定がなければデフォルトでユーザーID 1
    using var connection = new SqliteConnection(connectionString);
    var habits = connection.Query<Habit>(
        "SELECT Id, UserId, Title, Description, Emoji, Frequency, CreatedAt FROM Habits WHERE UserId = @UserId ORDER BY Id DESC",
        new { UserId = targetUserId });
    return Results.Ok(habits);
});

/// <summary>
/// 指定された ID の習慣詳細を取得します。
/// </summary>
app.MapGet("/api/habits/{id:int}", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var habit = connection.QuerySingleOrDefault<Habit>(
        "SELECT Id, UserId, Title, Description, Emoji, Frequency, CreatedAt FROM Habits WHERE Id = @Id",
        new { Id = id });
    return habit is not null ? Results.Ok(habit) : Results.NotFound(new { message = "指定された習慣が見つかりません。" });
});

/// <summary>
/// 新しい習慣を登録します。
/// </summary>
app.MapPost("/api/habits", (CreateHabitDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Title))
    {
        return Results.BadRequest(new { message = "タイトルは必須項目です。" });
    }

    string emoji = string.IsNullOrWhiteSpace(dto.Emoji) ? "📝" : dto.Emoji;
    string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        INSERT INTO Habits (UserId, Title, Description, Emoji, Frequency, CreatedAt)
        VALUES (@UserId, @Title, @Description, @Emoji, @Frequency, @CreatedAt);
        SELECT last_insert_rowid();";

    int id = connection.ExecuteScalar<int>(sql, new
    {
        dto.UserId,
        dto.Title,
        dto.Description,
        Emoji = emoji,
        Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? "Daily" : dto.Frequency,
        CreatedAt = createdAt
    });

    var createdHabit = new Habit
    {
        Id = id,
        UserId = dto.UserId,
        Title = dto.Title,
        Description = dto.Description,
        Emoji = emoji,
        Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? "Daily" : dto.Frequency,
        CreatedAt = createdAt
    };

    return Results.Created($"/api/habits/{id}", createdHabit);
});

/// <summary>
/// 習慣の情報を更新します。
/// </summary>
app.MapPut("/api/habits/{id:int}", (int id, UpdateHabitDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Title))
    {
        return Results.BadRequest(new { message = "タイトルは必須項目です。" });
    }

    string emoji = string.IsNullOrWhiteSpace(dto.Emoji) ? "📝" : dto.Emoji;

    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        UPDATE Habits
        SET Title = @Title, Description = @Description, Emoji = @Emoji, Frequency = @Frequency
        WHERE Id = @Id;";

    int rowsAffected = connection.Execute(sql, new
    {
        Id = id,
        dto.Title,
        dto.Description,
        Emoji = emoji,
        Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? "Daily" : dto.Frequency
    });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "更新対象の習慣が見つかりません。" });
    }

    return Results.Ok(new { message = "習慣が正常に更新されました。", id });
});

/// <summary>
/// 習慣を削除します。関連する実行記録（ExecutionLogs）、いいね（Likes）、コメント（Comments）も併せて削除します。
/// </summary>
app.MapDelete("/api/habits/{id:int}", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var transaction = connection.BeginTransaction();

    // 削除対象習慣に関連する ExecutionLogs の Id を取得
    var logIds = connection.Query<int>(
        "SELECT Id FROM ExecutionLogs WHERE HabitId = @HabitId",
        new { HabitId = id },
        transaction).ToList();

    if (logIds.Count > 0)
    {
        // 関連する Likes と Comments を削除
        connection.Execute(
            "DELETE FROM Likes WHERE ExecutionLogId IN @LogIds",
            new { LogIds = logIds },
            transaction);

        connection.Execute(
            "DELETE FROM Comments WHERE ExecutionLogId IN @LogIds",
            new { LogIds = logIds },
            transaction);

        // 関連する ExecutionLogs を削除
        connection.Execute(
            "DELETE FROM ExecutionLogs WHERE HabitId = @HabitId",
            new { HabitId = id },
            transaction);
    }

    // 習慣自体を削除
    int rowsAffected = connection.Execute(
        "DELETE FROM Habits WHERE Id = @Id",
        new { Id = id },
        transaction);

    if (rowsAffected == 0)
    {
        transaction.Rollback();
        return Results.NotFound(new { message = "削除対象の習慣が見つかりません。" });
    }

    transaction.Commit();
    return Results.Ok(new { message = "習慣が正常に削除されました。", id });
});

// ------------------------------------------------------------
// ワンタップ実行記録（ExecutionLog） API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// 指定された習慣のワンタップ実行記録を登録します。
/// </summary>
app.MapPost("/api/habits/{id:int}/execute", (int id, ExecuteHabitDto dto) =>
{
    using var connection = new SqliteConnection(connectionString);

    // 対象の習慣が存在するかチェック
    var habitExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM Habits WHERE Id = @Id",
        new { Id = id });

    if (!habitExists)
    {
        return Results.NotFound(new { message = "指定された習慣が見つかりません。" });
    }

    string executedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    string sql = @"
        INSERT INTO ExecutionLogs (HabitId, UserId, ExecutedAt, Comment)
        VALUES (@HabitId, @UserId, @ExecutedAt, @Comment);
        SELECT last_insert_rowid();";

    int logId = connection.ExecuteScalar<int>(sql, new
    {
        HabitId = id,
        UserId = dto.UserId,
        ExecutedAt = executedAt,
        dto.Comment
    });

    var createdLog = new ExecutionLog
    {
        Id = logId,
        HabitId = id,
        UserId = dto.UserId,
        ExecutedAt = executedAt,
        Comment = dto.Comment
    };

    return Results.Created($"/api/habits/{id}/logs", createdLog);
});

/// <summary>
/// 特定の習慣の過去の実行記録一覧を取得します。
/// </summary>
app.MapGet("/api/habits/{id:int}/logs", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var logs = connection.Query<ExecutionLog>(
        "SELECT Id, HabitId, UserId, ExecutedAt, Comment FROM ExecutionLogs WHERE HabitId = @HabitId ORDER BY Id DESC",
        new { HabitId = id });
    return Results.Ok(logs);
});

/// <summary>
/// 指定されたユーザー（または全ユーザー）の最近の実行記録一覧を取得します。
/// </summary>
app.MapGet("/api/logs", (int? userId, int? limit) =>
{
    int maxCount = limit ?? 20;
    using var connection = new SqliteConnection(connectionString);

    if (userId.HasValue)
    {
        var userLogs = connection.Query<ExecutionLog>(
            "SELECT Id, HabitId, UserId, ExecutedAt, Comment FROM ExecutionLogs WHERE UserId = @UserId ORDER BY Id DESC LIMIT @Limit",
            new { UserId = userId.Value, Limit = maxCount });
        return Results.Ok(userLogs);
    }
    else
    {
        var allLogs = connection.Query<ExecutionLog>(
            "SELECT Id, HabitId, UserId, ExecutedAt, Comment FROM ExecutionLogs ORDER BY Id DESC LIMIT @Limit",
            new { Limit = maxCount });
        return Results.Ok(allLogs);
    }
});

// ------------------------------------------------------------
// グループ管理 API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// 新しいグループを作成し、作成者を管理者（Admin）としてグループに自動追加します。
/// </summary>
app.MapPost("/api/groups", (CreateGroupDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name))
    {
        return Results.BadRequest(new { message = "グループ名は必須項目です。" });
    }

    string inviteCode = Guid.NewGuid().ToString("N")[..8].ToUpper();
    string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var transaction = connection.BeginTransaction();

    string insertGroupSql = @"
        INSERT INTO Groups (Name, InviteCode, CreatedAt)
        VALUES (@Name, @InviteCode, @CreatedAt);
        SELECT last_insert_rowid();";

    int groupId = connection.ExecuteScalar<int>(insertGroupSql, new
    {
        dto.Name,
        InviteCode = inviteCode,
        CreatedAt = createdAt
    }, transaction);

    string insertMemberSql = @"
        INSERT INTO GroupMembers (GroupId, UserId, Role)
        VALUES (@GroupId, @UserId, 'Admin');";

    connection.Execute(insertMemberSql, new
    {
        GroupId = groupId,
        dto.UserId
    }, transaction);

    transaction.Commit();

    var group = new GroupDto
    {
        Id = groupId,
        Name = dto.Name,
        InviteCode = inviteCode,
        CreatedAt = createdAt
    };

    return Results.Created($"/api/groups/{groupId}", group);
});

/// <summary>
/// 指定されたユーザーが所属するグループ一覧を取得します。
/// </summary>
app.MapGet("/api/groups", (int? userId) =>
{
    int targetUserId = userId ?? 1;
    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        SELECT g.Id, g.Name, g.InviteCode, g.CreatedAt
        FROM Groups g
        INNER JOIN GroupMembers gm ON g.Id = gm.GroupId
        WHERE gm.UserId = @UserId
        ORDER BY g.Id DESC;";

    var groups = connection.Query<GroupDto>(sql, new { UserId = targetUserId });
    return Results.Ok(groups);
});

/// <summary>
/// 指定された ID のグループ詳細情報を取得します。
/// </summary>
app.MapGet("/api/groups/{id:int}", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var group = connection.QuerySingleOrDefault<GroupDto>(
        "SELECT Id, Name, InviteCode, CreatedAt FROM Groups WHERE Id = @Id",
        new { Id = id });

    return group is not null ? Results.Ok(group) : Results.NotFound(new { message = "指定されたグループが見つかりません。" });
});

/// <summary>
/// 指定されたグループのメンバー一覧を取得します。
/// </summary>
app.MapGet("/api/groups/{id:int}/members", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        SELECT u.Id AS UserId, u.Name AS UserName, u.Emoji AS UserEmoji, gm.Role
        FROM GroupMembers gm
        INNER JOIN Users u ON gm.UserId = u.Id
        WHERE gm.GroupId = @GroupId
        ORDER BY gm.Role ASC, u.Id ASC;";

    var members = connection.Query<GroupMemberDto>(sql, new { GroupId = id });
    return Results.Ok(members);
});

/// <summary>
/// 招待コードを使用して既存のグループに参加します。
/// </summary>
app.MapPost("/api/groups/join", (JoinGroupDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.InviteCode))
    {
        return Results.BadRequest(new { message = "招待コードは必須項目です。" });
    }

    using var connection = new SqliteConnection(connectionString);
    var group = connection.QuerySingleOrDefault<GroupDto>(
        "SELECT Id, Name, InviteCode, CreatedAt FROM Groups WHERE InviteCode = @InviteCode",
        new { InviteCode = dto.InviteCode.Trim().ToUpper() });

    if (group is null)
    {
        return Results.NotFound(new { message = "指定された招待コードに該当するグループが見つかりません。" });
    }

    bool isAlreadyMember = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM GroupMembers WHERE GroupId = @GroupId AND UserId = @UserId",
        new { GroupId = group.Id, dto.UserId });

    if (isAlreadyMember)
    {
        return Results.BadRequest(new { message = "すでに対象のグループに参加しています。" });
    }

    connection.Execute(
        "INSERT INTO GroupMembers (GroupId, UserId, Role) VALUES (@GroupId, @UserId, 'Member')",
        new { GroupId = group.Id, dto.UserId });

    return Results.Ok(new { message = $"グループ「{group.Name}」に参加しました。", group });
});

/// <summary>
/// 指定されたグループのメンバーによる実行記録のみで構成されるタイムラインを取得します。
/// </summary>
app.MapGet("/api/groups/{id:int}/timeline", (int id, int? limit) =>
{
    int maxCount = limit ?? 20;
    using var connection = new SqliteConnection(connectionString);

    // 指定されたグループが存在するか検証
    var groupExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM Groups WHERE Id = @Id",
        new { Id = id });

    if (!groupExists)
    {
        return Results.NotFound(new { message = "指定されたグループが見つかりません。" });
    }

    string query = @"
        SELECT
            el.Id AS LogId,
            el.HabitId,
            h.Title AS HabitTitle,
            h.Emoji AS HabitEmoji,
            el.UserId,
            u.Name AS UserName,
            u.Emoji AS UserEmoji,
            el.ExecutedAt,
            el.Comment
        FROM ExecutionLogs el
        INNER JOIN Habits h ON el.HabitId = h.Id
        INNER JOIN Users u ON el.UserId = u.Id
        WHERE el.UserId IN (SELECT UserId FROM GroupMembers WHERE GroupId = @GroupId)
        ORDER BY el.Id DESC
        LIMIT @Limit;";

    var timelineItems = connection.Query<TimelineItemDto>(query, new { GroupId = id, Limit = maxCount }).ToList();

    foreach (var item in timelineItems)
    {
        string likesQuery = @"
            SELECT ReactionType AS Emoji, COUNT(1) AS Count
            FROM Likes
            WHERE ExecutionLogId = @LogId
            GROUP BY ReactionType;";

        item.Reactions = connection.Query<ReactionSummaryDto>(likesQuery, new { LogId = item.LogId }).ToList();

        string commentsQuery = @"
            SELECT c.Id, c.ExecutionLogId, c.UserId, u.Name AS UserName, u.Emoji AS UserEmoji, c.CommentText, c.CreatedAt
            FROM Comments c
            INNER JOIN Users u ON c.UserId = u.Id
            WHERE c.ExecutionLogId = @LogId
            ORDER BY c.Id ASC;";

        item.Comments = connection.Query<CommentDto>(commentsQuery, new { LogId = item.LogId }).ToList();
    }

    return Results.Ok(timelineItems);
});

// ------------------------------------------------------------
// タイムライン＆絵文字リアクション API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// タイムライン用データ（実行記録 + ユーザー絵文字 + タスク絵文字 + 絵文字リアクション一覧 + コメント一覧）を取得します。
/// </summary>
app.MapGet("/api/timeline", (int? limit) =>
{
    int maxCount = limit ?? 20;
    using var connection = new SqliteConnection(connectionString);

    // 実行記録とタスク、ユーザー情報を結合して取得
    string query = @"
        SELECT
            el.Id AS LogId,
            el.HabitId,
            h.Title AS HabitTitle,
            h.Emoji AS HabitEmoji,
            el.UserId,
            u.Name AS UserName,
            u.Emoji AS UserEmoji,
            el.ExecutedAt,
            el.Comment
        FROM ExecutionLogs el
        JOIN Habits h ON el.HabitId = h.Id
        JOIN Users u ON el.UserId = u.Id
        ORDER BY el.Id DESC
        LIMIT @Limit;";

    var timelineItems = connection.Query<TimelineItemDto>(query, new { Limit = maxCount }).ToList();

    // 各実行記録に対するリアクションおよびコメントを取得
    foreach (var item in timelineItems)
    {
        string likesQuery = @"
            SELECT ReactionType AS Emoji, COUNT(1) AS Count
            FROM Likes
            WHERE ExecutionLogId = @LogId
            GROUP BY ReactionType;";

        item.Reactions = connection.Query<ReactionSummaryDto>(likesQuery, new { LogId = item.LogId }).ToList();

        string commentsQuery = @"
            SELECT c.Id, c.ExecutionLogId, c.UserId, u.Name AS UserName, u.Emoji AS UserEmoji, c.CommentText, c.CreatedAt
            FROM Comments c
            INNER JOIN Users u ON c.UserId = u.Id
            WHERE c.ExecutionLogId = @LogId
            ORDER BY c.Id ASC;";

        item.Comments = connection.Query<CommentDto>(commentsQuery, new { LogId = item.LogId }).ToList();
    }

    return Results.Ok(timelineItems);
});

/// <summary>
/// 指定された実行記録に対するリアクション一覧を取得します。
/// </summary>
app.MapGet("/api/logs/{logId:int}/reactions", (int logId) =>
{
    using var connection = new SqliteConnection(connectionString);

    // ログが存在するか確認
    var logExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM ExecutionLogs WHERE Id = @Id",
        new { Id = logId });

    if (!logExists)
    {
        return Results.NotFound(new { message = "指定された実行記録が見つかりません。" });
    }

    string sql = @"
        SELECT l.Id, l.ExecutionLogId, l.UserId, u.Name AS UserName, u.Emoji AS UserEmoji, l.ReactionType, l.CreatedAt
        FROM Likes l
        INNER JOIN Users u ON l.UserId = u.Id
        WHERE l.ExecutionLogId = @ExecutionLogId
        ORDER BY l.Id ASC;";

    var reactions = connection.Query<ReactionDetailDto>(sql, new { ExecutionLogId = logId });
    return Results.Ok(reactions);
});

/// <summary>
/// 指定された実行記録に対して絵文字リアクションを追加または解除（トグル）します。
/// 既に同一のリアクションが存在する場合は削除（トグルOFF）します。
/// </summary>
app.MapPost("/api/logs/{logId:int}/reactions", (int logId, AddReactionDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.ReactionType))
    {
        return Results.BadRequest(new { message = "リアクションの絵文字は必須項目です。" });
    }

    using var connection = new SqliteConnection(connectionString);

    // ログが存在するか確認
    var logExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM ExecutionLogs WHERE Id = @Id",
        new { Id = logId });

    if (!logExists)
    {
        return Results.NotFound(new { message = "指定された実行記録が見つかりません。" });
    }

    // 既存の同一リアクションを検索（トグル判定用）
    int existingId = connection.ExecuteScalar<int>(
        "SELECT Id FROM Likes WHERE ExecutionLogId = @ExecutionLogId AND UserId = @UserId AND ReactionType = @ReactionType",
        new { ExecutionLogId = logId, dto.UserId, dto.ReactionType });

    if (existingId > 0)
    {
        // すでにリアクションが存在する場合は解除（削除）
        connection.Execute("DELETE FROM Likes WHERE Id = @Id", new { Id = existingId });
        return Results.Ok(new
        {
            action = "removed",
            executionLogId = logId,
            userId = dto.UserId,
            reactionType = dto.ReactionType,
            message = "リアクションを解除しました。"
        });
    }

    // リアクションが存在しない場合は新規追加
    string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    string sql = @"
        INSERT INTO Likes (ExecutionLogId, UserId, ReactionType, CreatedAt)
        VALUES (@ExecutionLogId, @UserId, @ReactionType, @CreatedAt);
        SELECT last_insert_rowid();";

    int reactionId = connection.ExecuteScalar<int>(sql, new
    {
        ExecutionLogId = logId,
        UserId = dto.UserId,
        ReactionType = dto.ReactionType,
        CreatedAt = createdAt
    });

    return Results.Created($"/api/logs/{logId}/reactions/{reactionId}", new
    {
        action = "added",
        id = reactionId,
        executionLogId = logId,
        userId = dto.UserId,
        reactionType = dto.ReactionType,
        createdAt,
        message = "リアクションを追加しました。"
    });
});

/// <summary>
/// 特定のリアクション ID を指定して削除します。
/// </summary>
app.MapDelete("/api/logs/{logId:int}/reactions/{reactionId:int}", (int logId, int reactionId) =>
{
    using var connection = new SqliteConnection(connectionString);
    int rowsAffected = connection.Execute(
        "DELETE FROM Likes WHERE Id = @ReactionId AND ExecutionLogId = @LogId",
        new { ReactionId = reactionId, LogId = logId });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "削除対象のリアクションが見つかりません。" });
    }

    return Results.Ok(new { message = "リアクションを削除しました。", reactionId });
});

// ------------------------------------------------------------
// タイムラインコメント API エンドポイント
// ------------------------------------------------------------

/// <summary>
/// 指定された実行記録に対するコメント一覧を取得します。
/// </summary>
app.MapGet("/api/logs/{logId:int}/comments", (int logId) =>
{
    using var connection = new SqliteConnection(connectionString);

    var logExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM ExecutionLogs WHERE Id = @Id",
        new { Id = logId });

    if (!logExists)
    {
        return Results.NotFound(new { message = "指定された実行記録が見つかりません。" });
    }

    string sql = @"
        SELECT c.Id, c.ExecutionLogId, c.UserId, u.Name AS UserName, u.Emoji AS UserEmoji, c.CommentText, c.CreatedAt
        FROM Comments c
        INNER JOIN Users u ON c.UserId = u.Id
        WHERE c.ExecutionLogId = @ExecutionLogId
        ORDER BY c.Id ASC;";

    var comments = connection.Query<CommentDto>(sql, new { ExecutionLogId = logId });
    return Results.Ok(comments);
});

/// <summary>
/// 指定された実行記録に対して新しいコメントを投稿します。
/// </summary>
app.MapPost("/api/logs/{logId:int}/comments", (int logId, AddCommentDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.CommentText))
    {
        return Results.BadRequest(new { message = "コメント内容を入力してください。" });
    }

    using var connection = new SqliteConnection(connectionString);

    var logExists = connection.ExecuteScalar<bool>(
        "SELECT COUNT(1) FROM ExecutionLogs WHERE Id = @Id",
        new { Id = logId });

    if (!logExists)
    {
        return Results.NotFound(new { message = "指定された実行記録が見つかりません。" });
    }

    string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    string sql = @"
        INSERT INTO Comments (ExecutionLogId, UserId, CommentText, CreatedAt)
        VALUES (@ExecutionLogId, @UserId, @CommentText, @CreatedAt);
        SELECT last_insert_rowid();";

    int commentId = connection.ExecuteScalar<int>(sql, new
    {
        ExecutionLogId = logId,
        UserId = dto.UserId,
        CommentText = dto.CommentText.Trim(),
        CreatedAt = createdAt
    });

    var user = connection.QuerySingleOrDefault<User>(
        "SELECT Name, Emoji FROM Users WHERE Id = @Id",
        new { Id = dto.UserId });

    var createdComment = new CommentDto
    {
        Id = commentId,
        ExecutionLogId = logId,
        UserId = dto.UserId,
        UserName = user?.Name ?? "不明",
        UserEmoji = user?.Emoji ?? "👤",
        CommentText = dto.CommentText.Trim(),
        CreatedAt = createdAt
    };

    return Results.Created($"/api/logs/{logId}/comments/{commentId}", createdComment);
});

/// <summary>
/// 指定された ID のコメントを削除します。
/// </summary>
app.MapDelete("/api/comments/{commentId:int}", (int commentId) =>
{
    using var connection = new SqliteConnection(connectionString);
    int rowsAffected = connection.Execute("DELETE FROM Comments WHERE Id = @Id", new { Id = commentId });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "削除対象のコメントが見つかりません。" });
    }

    return Results.Ok(new { message = "コメントを削除しました。", commentId });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");


// ------------------------------------------------------------
// データモデルおよび DTO クラスの定義
// ------------------------------------------------------------

/// <summary>
/// ユーザー情報を表すエンティティクラスです。
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Emoji { get; set; } = "👤";
    public string? Password { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// 習慣（タスク）情報を表すエンティティクラスです。
/// </summary>
public class Habit
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Emoji { get; set; } = "📝";
    public string Frequency { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// 習慣の実行記録（ログ）を表すエンティティクラスです。
/// </summary>
public class ExecutionLog
{
    public int Id { get; set; }
    public int HabitId { get; set; }
    public int UserId { get; set; }
    public string ExecutedAt { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

/// <summary>
/// いいね・リアクション情報を表すエンティティクラスです。
/// </summary>
public class Like
{
    public int Id { get; set; }
    public int ExecutionLogId { get; set; }
    public int UserId { get; set; }
    public string ReactionType { get; set; } = "👍";
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// タイムラインで表示するアイテムを表す DTO クラスです。
/// </summary>
public class TimelineItemDto
{
    public int LogId { get; set; }
    public int HabitId { get; set; }
    public string HabitTitle { get; set; } = string.Empty;
    public string HabitEmoji { get; set; } = "📝";
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmoji { get; set; } = "👤";
    public string ExecutedAt { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public List<ReactionSummaryDto> Reactions { get; set; } = new();
    public List<CommentDto> Comments { get; set; } = new();
}

/// <summary>
/// タイムライン投稿のコメント情報を表す DTO クラスです。
/// </summary>
public class CommentDto
{
    public int Id { get; set; }
    public int ExecutionLogId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmoji { get; set; } = "👤";
    public string CommentText { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// コメントを追加するための DTO リクエストモデルです。
/// </summary>
public record AddCommentDto(int UserId, string CommentText);

/// <summary>
/// リアクションの集計情報を表す DTO クラスです。
/// </summary>
public class ReactionSummaryDto
{
    public string Emoji { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// リアクションの詳細情報を表す DTO クラスです。
/// </summary>
public class ReactionDetailDto
{
    public int Id { get; set; }
    public int ExecutionLogId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmoji { get; set; } = "👤";
    public string ReactionType { get; set; } = "👍";
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// 習慣を新規作成するための DTO リクエストモデルです。
/// </summary>
public record CreateHabitDto(int UserId, string Title, string? Description, string? Emoji, string Frequency);

/// <summary>
/// 習慣情報を更新するための DTO リクエストモデルです。
/// </summary>
public record UpdateHabitDto(string Title, string? Description, string? Emoji, string Frequency);

/// <summary>
/// ワンタップで習慣を実行記録するための DTO リクエストモデルです。
/// </summary>
public record ExecuteHabitDto(int UserId, string? Comment);

/// <summary>
/// ユーザー情報を更新するための DTO リクエストモデルです。
/// </summary>
public record UpdateUserDto(string Name, string? Emoji);

/// <summary>
/// ユーザー名およびパスワード変更用の DTO リクエストモデルです。
/// </summary>
public record ChangePasswordDto(string CurrentPassword, string NewPassword, string? NewUsername);

/// <summary>
/// リアクションを追加するための DTO リクエストモデルです。
/// </summary>
public record AddReactionDto(int UserId, string ReactionType);

/// <summary>
/// グループ新規作成用の DTO リクエストモデルです。
/// </summary>
public record CreateGroupDto(int UserId, string Name);

/// <summary>
/// 招待コードによるグループ参加用の DTO リクエストモデルです。
/// </summary>
public record JoinGroupDto(int UserId, string InviteCode);

/// <summary>
/// グループ情報を表す DTO クラスです。
/// </summary>
public class GroupDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string InviteCode { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// グループメンバー情報を表す DTO クラスです。
/// </summary>
public class GroupMemberDto
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmoji { get; set; } = "👤";
    public string Role { get; set; } = "Member";
}
