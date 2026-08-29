using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using Dapper;

/// <summary>
/// アプリケーションのエントリポイントおよび初期設定を提供するメインクラスです。
/// </summary>
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

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
            CreatedAt TEXT NOT NULL
        );
    ");

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
            Frequency TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id)
        );
    ");

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

    // デフォルトのテスト用初期ユーザーが存在しない場合は追加
    connection.Execute(@"
        INSERT INTO Users (Id, Name, Email, CreatedAt)
        SELECT 1, 'テストユーザー', 'test@example.com', '2025-01-01T00:00:00Z'
        WHERE NOT EXISTS (SELECT 1 FROM Users WHERE Id = 1);
    ");
}

// ------------------------------------------------------------
// ルーティング定義 (Hono風 Minimal API)
// ------------------------------------------------------------

/// <summary>
/// ダッシュボード / マイ習慣＆ワンタップ実行画面 UI エンドポイント
/// </summary>
app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html lang="ja">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>HabitTracker - マイ習慣 & ワンタップ実行</title>
        <script src="https://unpkg.com/htmx.org@1.9.10"></script>
        <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; max-width: 800px; margin: 0 auto; padding: 20px; background-color: #f7f9fa; color: #333; }
            h1, h2 { color: #1a202c; }
            .card { background: white; border-radius: 8px; padding: 20px; margin-bottom: 20px; box-shadow: 0 2px 4px rgba(0,0,0,0.05); }
            .habit-item { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #edf2f7; padding: 12px 0; }
            .habit-item:last-child { border-bottom: none; }
            .btn { background-color: #3182ce; color: white; border: none; padding: 8px 16px; border-radius: 6px; cursor: pointer; font-weight: bold; }
            .btn:hover { background-color: #2b6cb0; }
            .btn-success { background-color: #38a169; }
            .btn-success:hover { background-color: #2f855a; }
            .form-group { margin-bottom: 15px; }
            .form-group label { display: block; margin-bottom: 5px; font-weight: bold; }
            .form-group input, .form-group select { width: 100%; padding: 8px; border: 1px solid #cbd5e0; border-radius: 4px; box-sizing: border-box; }
            .log-item { background: #f0fff4; border-left: 4px solid #38a169; padding: 10px; margin-bottom: 8px; border-radius: 4px; }
        </style>
    </head>
    <body>
        <h1>習慣トラッカー - ダッシュボード</h1>

        <!-- 習慣登録フォーム -->
        <div class="card">
            <h2>新しい習慣を登録</h2>
            <form id="create-habit-form" onsubmit="createHabit(event)">
                <div class="form-group">
                    <label for="title">習慣のタイトル</label>
                    <input type="text" id="title" required placeholder="例: 毎朝散歩する">
                </div>
                <div class="form-group">
                    <label for="description">詳細メモ（任意）</label>
                    <input type="text" id="description" placeholder="例: 20分以上">
                </div>
                <div class="form-group">
                    <label for="frequency">頻度</label>
                    <select id="frequency">
                        <option value="Daily">毎日</option>
                        <option value="Weekly">毎週</option>
                    </select>
                </div>
                <button type="submit" class="btn">習慣を追加</button>
            </form>
        </div>

        <!-- 本日の習慣一覧 & ワンタップ実行 -->
        <div class="card">
            <h2>マイ習慣一覧（ワンタップ実行）</h2>
            <div id="habits-list">読み込み中...</div>
        </div>

        <!-- 実行ログ -->
        <div class="card">
            <h2>最近の実行記録ログ</h2>
            <div id="logs-list">読み込み中...</div>
        </div>

        <script>
            const USER_ID = 1; // デフォルトユーザーID

            async function loadHabits() {
                const res = await fetch(`/api/habits?userId=${USER_ID}`);
                const habits = await res.json();
                const container = document.getElementById('habits-list');

                if (!habits || habits.length === 0) {
                    container.innerHTML = '<p>登録されている習慣がありません。</p>';
                    return;
                }

                container.innerHTML = habits.map(h => `
                    <div class="habit-item">
                        <div>
                            <strong>${escapeHtml(h.title)}</strong> (${escapeHtml(h.frequency)})
                            ${h.description ? `<p style="margin: 4px 0 0; color: #718096; font-size: 0.9em;">${escapeHtml(h.description)}</p>` : ''}
                        </div>
                        <div>
                            <button class="btn btn-success" onclick="executeHabit(${h.id})">✅ 実行 (ワンタップ)</button>
                        </div>
                    </div>
                `).join('');
            }

            async function createHabit(e) {
                e.preventDefault();
                const title = document.getElementById('title').value;
                const description = document.getElementById('description').value;
                const frequency = document.getElementById('frequency').value;

                const res = await fetch('/api/habits', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: USER_ID, title, description, frequency })
                });

                if (res.ok) {
                    document.getElementById('create-habit-form').reset();
                    loadHabits();
                } else {
                    alert('習慣の追加に失敗しました。');
                }
            }

            async function executeHabit(habitId) {
                const res = await fetch(`/api/habits/${habitId}/execute`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: USER_ID, comment: 'ワンタップ実行！' })
                });

                if (res.ok) {
                    loadLogs();
                    alert('🎉 習慣の実行を記録しました！');
                } else {
                    alert('実行記録の保存に失敗しました。');
                }
            }

            async function loadLogs() {
                const res = await fetch(`/api/logs?userId=${USER_ID}&limit=10`);
                const logs = await res.json();
                const container = document.getElementById('logs-list');

                if (!logs || logs.length === 0) {
                    container.innerHTML = '<p>まだ実行記録はありません。</p>';
                    return;
                }

                container.innerHTML = logs.map(l => `
                    <div class="log-item">
                        <strong>習慣ID: ${l.habitId}</strong> - ${new Date(l.executedAt).toLocaleString('ja-JP')}
                        ${l.comment ? `<p style="margin: 4px 0 0;">コメント: ${escapeHtml(l.comment)}</p>` : ''}
                    </div>
                `).join('');
            }

            function escapeHtml(str) {
                if (!str) return '';
                return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
            }

            // 初期ロード
            loadHabits();
            loadLogs();
        </script>
    </body>
    </html>
    """, "text/html; charset=utf-8"));

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
        "SELECT Id, UserId, Title, Description, Frequency, CreatedAt FROM Habits WHERE UserId = @UserId ORDER BY Id DESC",
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
        "SELECT Id, UserId, Title, Description, Frequency, CreatedAt FROM Habits WHERE Id = @Id",
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

    string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        INSERT INTO Habits (UserId, Title, Description, Frequency, CreatedAt)
        VALUES (@UserId, @Title, @Description, @Frequency, @CreatedAt);
        SELECT last_insert_rowid();";

    int id = connection.ExecuteScalar<int>(sql, new
    {
        dto.UserId,
        dto.Title,
        dto.Description,
        Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? "Daily" : dto.Frequency,
        CreatedAt = createdAt
    });

    var createdHabit = new Habit
    {
        Id = id,
        UserId = dto.UserId,
        Title = dto.Title,
        Description = dto.Description,
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

    using var connection = new SqliteConnection(connectionString);
    string sql = @"
        UPDATE Habits
        SET Title = @Title, Description = @Description, Frequency = @Frequency
        WHERE Id = @Id;";

    int rowsAffected = connection.Execute(sql, new
    {
        Id = id,
        dto.Title,
        dto.Description,
        Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? "Daily" : dto.Frequency
    });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "更新対象の習慣が見つかりません。" });
    }

    return Results.Ok(new { message = "習慣が正常に更新されました。", id });
});

/// <summary>
/// 習慣を削除します。
/// </summary>
app.MapDelete("/api/habits/{id:int}", (int id) =>
{
    using var connection = new SqliteConnection(connectionString);
    int rowsAffected = connection.Execute("DELETE FROM Habits WHERE Id = @Id", new { Id = id });

    if (rowsAffected == 0)
    {
        return Results.NotFound(new { message = "削除対象の習慣が見つかりません。" });
    }

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

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// ------------------------------------------------------------
// データモデルおよび DTO クラスの定義
// ------------------------------------------------------------

/// <summary>
/// 習慣（タスク）情報を表すエンティティクラスです。
/// </summary>
public class Habit
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
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
/// 習慣を新規作成するための DTO リクエストモデルです。
/// </summary>
public record CreateHabitDto(int UserId, string Title, string? Description, string Frequency);

/// <summary>
/// 習慣情報を更新するための DTO リクエストモデルです。
/// </summary>
public record UpdateHabitDto(string Title, string? Description, string Frequency);

/// <summary>
/// ワンタップで習慣を実行記録するための DTO リクエストモデルです。
/// </summary>
public record ExecuteHabitDto(int UserId, string? Comment);
