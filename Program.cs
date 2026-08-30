using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Dapper;

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

var app = builder.Build();

// 認証・認可ミドルウェアの適用（適用順序が重要）
app.UseAuthentication();
app.UseAuthorization();

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
/// </summary>
app.MapGet("/login", (HttpContext context) =>
{
    string? errorMessage = context.Request.Query["error"].ToString();
    string errorHtml = !string.IsNullOrEmpty(errorMessage)
        ? $"<p style=\"color: #e53e3e; margin-bottom: 15px;\"><small>{errorMessage}</small></p>"
        : "";

    return Results.Content($$"""
        <!DOCTYPE html>
        <html lang="ja">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>ログイン - HabitTracker</title>
            <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background-color: #f7f9fa; display: flex; justify-content: center; align-items: center; min-height: 100vh; margin: 0; }
                .card { background: white; border-radius: 8px; padding: 30px; width: 100%; max-width: 360px; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }
                h2 { margin-top: 0; color: #1a202c; text-align: center; }
                .form-group { margin-bottom: 15px; }
                .form-group label { display: block; margin-bottom: 5px; font-weight: bold; color: #4a5568; }
                .form-group input { width: 100%; padding: 10px; border: 1px solid #cbd5e0; border-radius: 6px; box-sizing: border-box; }
                .btn { background-color: #3182ce; color: white; border: none; padding: 10px; border-radius: 6px; cursor: pointer; font-weight: bold; width: 100%; font-size: 1rem; }
                .btn:hover { background-color: #2b6cb0; }
            </style>
        </head>
        <body>
            <div class="card">
                <h2>🔑 ログイン</h2>
                {{errorHtml}}
                <form action="/login" method="post">
                    <div class="form-group">
                        <label for="username">ユーザー名</label>
                        <input type="text" id="username" name="Username" required autofocus placeholder="admin">
                    </div>
                    <div class="form-group">
                        <label for="password">パスワード</label>
                        <input type="password" id="password" name="Password" required placeholder="secret-password">
                    </div>
                    <button type="submit" class="btn">ログイン</button>
                </form>
            </div>
        </body>
        </html>
        """, "text/html; charset=utf-8");
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
/// ダッシュボード / マイ習慣＆ワンタップ実行画面 UI エンドポイント
/// </summary>
app.MapGet("/", (HttpContext context) =>
{
    string currentUser = context.User.Identity?.Name ?? "不明";
    return Results.Content($$"""
    <!DOCTYPE html>
    <html lang="ja">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>HabitTracker - マイ習慣 & タイムライン</title>
        <script src="https://unpkg.com/htmx.org@1.9.10"></script>
        <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; max-width: 800px; margin: 0 auto; padding: 20px; background-color: #f7f9fa; color: #333; }
            h1, h2 { color: #1a202c; }
            .card { background: white; border-radius: 8px; padding: 20px; margin-bottom: 20px; box-shadow: 0 2px 4px rgba(0,0,0,0.05); }
            .habit-item { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #edf2f7; padding: 12px 0; }
            .habit-item:last-child { border-bottom: none; }
            .btn { background-color: #3182ce; color: white; border: none; padding: 8px 16px; border-radius: 6px; cursor: pointer; font-weight: bold; text-decoration: none; display: inline-block; }
            .btn:hover { background-color: #2b6cb0; }
            .btn-danger { background-color: #e53e3e; }
            .btn-danger:hover { background-color: #c53030; }
            .btn-success { background-color: #38a169; }
            .btn-success:hover { background-color: #2f855a; }
            .form-group { margin-bottom: 15px; }
            .form-group label { display: block; margin-bottom: 5px; font-weight: bold; }
            .form-group input, .form-group select { width: 100%; padding: 8px; border: 1px solid #cbd5e0; border-radius: 4px; box-sizing: border-box; }
            .timeline-item { background: #ffffff; border: 1px solid #e2e8f0; border-left: 5px solid #3182ce; padding: 15px; margin-bottom: 12px; border-radius: 6px; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }
            .timeline-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
            .user-info { display: flex; align-items: center; gap: 8px; font-weight: bold; }
            .user-emoji { font-size: 1.4em; }
            .habit-title { display: flex; align-items: center; gap: 6px; font-size: 1.1em; font-weight: bold; color: #2d3748; margin-bottom: 6px; }
            .task-emoji { font-size: 1.3em; }
            .reaction-bar { display: flex; align-items: center; gap: 8px; margin-top: 10px; flex-wrap: wrap; }
            .reaction-badge { background: #edf2f7; padding: 4px 8px; border-radius: 12px; font-size: 0.9em; display: inline-flex; align-items: center; gap: 4px; cursor: pointer; }
            .reaction-badge:hover { background: #e2e8f0; }
            .btn-reaction { background: #f7fafc; border: 1px solid #cbd5e0; padding: 4px 8px; border-radius: 12px; cursor: pointer; font-size: 0.9em; }
            .btn-reaction:hover { background: #e2e8f0; }
            .comments-section { margin-top: 12px; border-top: 1px dashed #e2e8f0; padding-top: 10px; font-size: 0.9em; }
            .comment-item { display: flex; justify-content: space-between; align-items: center; background: #f7fafc; padding: 6px 10px; border-radius: 6px; margin-bottom: 6px; }
            .comment-form { display: flex; gap: 6px; margin-top: 8px; }
            .comment-input { flex: 1; padding: 6px 10px; border: 1px solid #cbd5e0; border-radius: 6px; font-size: 0.9em; }
            .header-bar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
            .user-profile-bar { display: flex; align-items: center; gap: 10px; background: #ebf8ff; padding: 12px; border-radius: 6px; margin-bottom: 20px; }
        </style>
    </head>
    <body>
        <div class="header-bar">
            <h1>習慣トラッカー - ダッシュボード & タイムライン</h1>
            <div>
                <span style="margin-right: 10px; font-weight: bold;">ログイン中: {{currentUser}}</span>
                <a href="/logout" class="btn btn-danger">ログアウト</a>
            </div>
        </div>

        <!-- ユーザープロフィール設定 -->
        <div class="user-profile-bar card">
            <span style="font-weight: bold;">現在のユーザー設定:</span>
            <span id="user-display" style="font-size: 1.2em;">👤 テストユーザー</span>
            <input type="text" id="user-emoji-input" style="width: 50px; text-align: center; font-size: 1.2em;" value="👤">
            <button class="btn" onclick="updateUserEmoji()">絵文字更新</button>
        </div>

        <!-- パスワード変更セクション -->
        <div class="card">
            <h2>🔒 パスワード変更</h2>
            <form id="change-password-form" onsubmit="changePassword(event)">
                <div style="display: flex; gap: 15px; flex-wrap: wrap;">
                    <div class="form-group" style="flex: 1; min-width: 200px;">
                        <label for="current-password">現在のパスワード</label>
                        <input type="password" id="current-password" required placeholder="現在のパスワード">
                    </div>
                    <div class="form-group" style="flex: 1; min-width: 200px;">
                        <label for="new-password">新しいパスワード</label>
                        <input type="password" id="new-password" required placeholder="新しいパスワード">
                    </div>
                </div>
                <button type="submit" class="btn">パスワードを変更</button>
            </form>
        </div>

        <!-- グループ管理セクション -->
        <div class="card">
            <h2>👥 所属グループ管理</h2>
            <div id="groups-list" style="margin-bottom: 15px;">読み込み中...</div>
            <hr style="border: 0; border-top: 1px solid #edf2f7; margin: 15px 0;">
            <div style="display: flex; gap: 20px; flex-wrap: wrap;">
                <form id="create-group-form" onsubmit="createGroup(event)" style="flex: 1; min-width: 250px;">
                    <div class="form-group">
                        <label for="group-name">新規グループ作成</label>
                        <input type="text" id="group-name" required placeholder="例: 家族チーム, 開発部">
                    </div>
                    <button type="submit" class="btn">グループを作成</button>
                </form>
                <form id="join-group-form" onsubmit="joinGroup(event)" style="flex: 1; min-width: 250px;">
                    <div class="form-group">
                        <label for="invite-code">招待コードで参加</label>
                        <input type="text" id="invite-code" required placeholder="例: A1B2C3D4">
                    </div>
                    <button type="submit" class="btn btn-success">グループに参加</button>
                </form>
            </div>
        </div>

        <!-- 習慣登録フォーム -->
        <div class="card">
            <h2>新しい習慣を登録</h2>
            <form id="create-habit-form" onsubmit="createHabit(event)">
                <div class="form-group">
                    <label for="title">習慣のタイトル</label>
                    <input type="text" id="title" required placeholder="例: 毎朝散歩する">
                </div>
                <div class="form-group">
                    <label for="emoji">タスク絵文字</label>
                    <input type="text" id="emoji" value="📝" placeholder="例: 🏃, 📚, 🧘">
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

        <!-- タイムライン -->
        <div class="card">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; flex-wrap: wrap; gap: 10px;">
                <h2 style="margin: 0;">👥 タイムライン</h2>
                <div style="display: flex; align-items: center; gap: 8px;">
                    <label for="group-filter" style="font-weight: bold;">表示フィルタ:</label>
                    <select id="group-filter" onchange="loadTimeline()" style="padding: 6px 12px; border: 1px solid #cbd5e0; border-radius: 6px;">
                        <option value="all">すべての投稿</option>
                    </select>
                </div>
            </div>
            <div id="timeline-list">読み込み中...</div>
        </div>

        <script>
            let currentUserId = 1;
            let currentUserEmoji = '👤';
            let currentUserName = '{{currentUser}}';

            async function loadUser() {
                const res = await fetch(`/api/users/me`);
                if (res.ok) {
                    const u = await res.json();
                    currentUserId = u.id;
                    currentUserEmoji = u.emoji || '👤';
                    currentUserName = u.name || '{{currentUser}}';
                    document.getElementById('user-display').innerText = `${currentUserEmoji} ${currentUserName}`;
                    document.getElementById('user-emoji-input').value = currentUserEmoji;
                }
            }

            async function updateUserEmoji() {
                const emoji = document.getElementById('user-emoji-input').value || '👤';
                const res = await fetch(`/api/users/${currentUserId}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: currentUserName, emoji: emoji })
                });

                if (res.ok) {
                    currentUserEmoji = emoji;
                    document.getElementById('user-display').innerText = `${currentUserEmoji} ${currentUserName}`;
                    alert('ユーザーの絵文字を更新しました！');
                    loadTimeline();
                } else {
                    alert('ユーザー絵文字の更新に失敗しました。');
                }
            }

            async function changePassword(e) {
                e.preventDefault();
                const currentPassword = document.getElementById('current-password').value;
                const newPassword = document.getElementById('new-password').value;

                const res = await fetch('/api/users/change-password', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ currentPassword, newPassword })
                });

                if (res.ok) {
                    document.getElementById('change-password-form').reset();
                    alert('🔒 パスワードを変更しました！');
                } else {
                    const err = await res.json();
                    alert(err.message || 'パスワードの変更に失敗しました。');
                }
            }

            async function loadHabits() {
                const res = await fetch(`/api/habits?userId=${currentUserId}`);
                const habits = await res.json();
                const container = document.getElementById('habits-list');

                if (!habits || habits.length === 0) {
                    container.innerHTML = '<p>登録されている習慣がありません。</p>';
                    return;
                }

                container.innerHTML = habits.map(h => `
                    <div class="habit-item">
                        <div>
                            <span style="font-size: 1.3em;">${escapeHtml(h.emoji || '📝')}</span>
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
                const emoji = document.getElementById('emoji').value || '📝';
                const description = document.getElementById('description').value;
                const frequency = document.getElementById('frequency').value;

                const res = await fetch('/api/habits', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, title, description, emoji, frequency })
                });

                if (res.ok) {
                    document.getElementById('create-habit-form').reset();
                    document.getElementById('emoji').value = '📝';
                    loadHabits();
                } else {
                    alert('習慣の追加に失敗しました。');
                }
            }

            async function executeHabit(habitId) {
                const res = await fetch(`/api/habits/${habitId}/execute`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, comment: 'ワンタップ実行！' })
                });

                if (res.ok) {
                    loadTimeline();
                    alert('🎉 習慣の実行を記録しました！');
                } else {
                    alert('実行記録の保存に失敗しました。');
                }
            }

            let userGroups = [];

            async function loadGroups() {
                const res = await fetch(`/api/groups?userId=${currentUserId}`);
                if (!res.ok) return;
                userGroups = await res.json();

                const container = document.getElementById('groups-list');
                const filterSelect = document.getElementById('group-filter');

                const currentFilter = filterSelect.value;
                filterSelect.innerHTML = '<option value="all">すべての投稿</option>';

                if (!userGroups || userGroups.length === 0) {
                    container.innerHTML = '<p style="color: #718096;">所属しているグループはありません。</p>';
                    return;
                }

                container.innerHTML = userGroups.map(g => `
                    <div style="background: #f7fafc; padding: 10px; border-radius: 6px; margin-bottom: 8px; display: flex; justify-content: space-between; align-items: center;">
                        <div>
                            <strong>🏢 ${escapeHtml(g.name)}</strong>
                        </div>
                        <div>
                            <span style="font-size: 0.85em; background: #e2e8f0; padding: 4px 8px; border-radius: 4px; color: #4a5568;">
                                招待コード: <strong>${escapeHtml(g.inviteCode)}</strong>
                            </span>
                        </div>
                    </div>
                `).join('');

                userGroups.forEach(g => {
                    const option = document.createElement('option');
                    option.value = g.id;
                    option.textContent = `🏢 ${g.name}`;
                    filterSelect.appendChild(option);
                });

                if (userGroups.some(g => g.id.toString() === currentFilter)) {
                    filterSelect.value = currentFilter;
                }
            }

            async function createGroup(e) {
                e.preventDefault();
                const name = document.getElementById('group-name').value;
                const res = await fetch('/api/groups', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, name })
                });

                if (res.ok) {
                    const newGroup = await res.json();
                    document.getElementById('create-group-form').reset();
                    alert(`🎉 グループ「${newGroup.name}」を作成しました！\n招待コード: ${newGroup.inviteCode}`);
                    await loadGroups();
                } else {
                    const err = await res.json();
                    alert(err.message || 'グループの作成に失敗しました。');
                }
            }

            async function joinGroup(e) {
                e.preventDefault();
                const inviteCode = document.getElementById('invite-code').value;
                const res = await fetch('/api/groups/join', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, inviteCode })
                });

                if (res.ok) {
                    const result = await res.json();
                    document.getElementById('join-group-form').reset();
                    alert(`🎉 ${result.message}`);
                    await loadGroups();
                    loadTimeline();
                } else {
                    const err = await res.json();
                    alert(err.message || 'グループへの参加に失敗しました。');
                }
            }

            async function loadTimeline() {
                const selectedGroupId = document.getElementById('group-filter')?.value || 'all';
                const endpoint = selectedGroupId === 'all'
                    ? `/api/timeline?limit=15`
                    : `/api/groups/${selectedGroupId}/timeline?limit=15`;

                const res = await fetch(endpoint);
                const items = await res.json();
                const container = document.getElementById('timeline-list');

                if (!items || items.length === 0) {
                    container.innerHTML = '<p>まだタイムラインに記録がありません。</p>';
                    return;
                }

                const quickEmojis = ['👍', '🔥', '🎉', '❤️', '👏'];

                container.innerHTML = items.map(item => `
                    <div class="timeline-item">
                        <div class="timeline-header">
                            <div class="user-info">
                                <span class="user-emoji">${escapeHtml(item.userEmoji || '👤')}</span>
                                <span>${escapeHtml(item.userName)}</span>
                            </div>
                            <small style="color: #718096;">${new Date(item.executedAt).toLocaleString('ja-JP')}</small>
                        </div>
                        <div class="habit-title">
                            <span class="task-emoji">${escapeHtml(item.habitEmoji || '📝')}</span>
                            <span>${escapeHtml(item.habitTitle)}</span>
                        </div>
                        ${item.comment ? `<p style="margin: 4px 0 8px; color: #4a5568;">コメント: ${escapeHtml(item.comment)}</p>` : ''}

                        <div class="reaction-bar">
                            ${(item.reactions || []).map(r => `
                                <span class="reaction-badge" title="タップしてリアクションを切り替え" onclick="toggleReaction(${item.logId}, '${escapeHtml(r.emoji)}')">${escapeHtml(r.emoji)} ${r.count}</span>
                            `).join('')}
                            <span style="color: #cbd5e0;">|</span>
                            ${quickEmojis.map(emoji => `
                                <button class="btn-reaction" onclick="toggleReaction(${item.logId}, '${emoji}')">${emoji}</button>
                            `).join('')}
                        </div>

                        <!-- コメントセクション -->
                        <div class="comments-section">
                            <div id="comments-list-${item.logId}">
                                ${(item.comments || []).map(c => `
                                    <div class="comment-item">
                                        <div>
                                            <span>${escapeHtml(c.userEmoji)} <strong>${escapeHtml(c.userName)}</strong>: ${escapeHtml(c.commentText)}</span>
                                        </div>
                                        <div style="display: flex; align-items: center; gap: 6px;">
                                            <small style="color: #a0aec0;">${new Date(c.createdAt).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })}</small>
                                            ${c.userId === currentUserId ? `<button style="background: none; border: none; color: #e53e3e; cursor: pointer; font-size: 0.8em;" onclick="deleteComment(${c.id})">❌</button>` : ''}
                                        </div>
                                    </div>
                                `).join('')}
                            </div>
                            <form class="comment-form" onsubmit="postComment(event, ${item.logId})">
                                <input type="text" id="comment-input-${item.logId}" class="comment-input" placeholder="応援コメントを入力..." required>
                                <button type="submit" class="btn" style="padding: 6px 12px; font-size: 0.85em;">送信</button>
                            </form>
                        </div>
                    </div>
                `).join('');
            }

            async function toggleReaction(logId, emoji) {
                const res = await fetch(`/api/logs/${logId}/reactions`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, reactionType: emoji })
                });

                if (res.ok) {
                    loadTimeline();
                } else {
                    alert('リアクションの送信に失敗しました。');
                }
            }

            async function postComment(e, logId) {
                e.preventDefault();
                const inputEl = document.getElementById(`comment-input-${logId}`);
                const text = inputEl ? inputEl.value : '';

                if (!text.trim()) return;

                const res = await fetch(`/api/logs/${logId}/comments`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ userId: currentUserId, commentText: text })
                });

                if (res.ok) {
                    loadTimeline();
                } else {
                    alert('コメントの送信に失敗しました。');
                }
            }

            async function deleteComment(commentId) {
                if (!confirm('このコメントを削除しますか？')) return;

                const res = await fetch(`/api/comments/${commentId}`, {
                    method: 'DELETE'
                });

                if (res.ok) {
                    loadTimeline();
                } else {
                    alert('コメントの削除に失敗しました。');
                }
            }

            function escapeHtml(str) {
                if (!str) return '';
                return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
            }

            // 初期ロード
            loadUser();
            loadGroups();
            loadHabits();
            loadTimeline();
        </script>
    </body>
    </html>
    """, "text/html; charset=utf-8");
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
/// ログイン中のユーザーのパスワードを変更します。
/// </summary>
app.MapPost("/api/users/change-password", (HttpContext context, ChangePasswordDto dto) =>
{
    string? username = context.User.Identity?.Name;
    if (string.IsNullOrEmpty(username))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(dto.CurrentPassword) || string.IsNullOrWhiteSpace(dto.NewPassword))
    {
        return Results.BadRequest(new { message = "現在のパスワードと新しいパスワードを両方入力してください。" });
    }

    using var connection = new SqliteConnection(connectionString);
    var user = connection.QuerySingleOrDefault<User>(
        "SELECT Id, Name, Password FROM Users WHERE Name = @Name",
        new { Name = username });

    if (user is null)
    {
        return Results.NotFound(new { message = "ユーザーが見つかりません。" });
    }

    if (user.Password != dto.CurrentPassword)
    {
        return Results.BadRequest(new { message = "現在のパスワードが正しくありません。" });
    }

    connection.Execute(
        "UPDATE Users SET Password = @NewPassword WHERE Id = @Id",
        new { NewPassword = dto.NewPassword.Trim(), Id = user.Id });

    return Results.Ok(new { message = "パスワードが正常に変更されました。" });
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
/// パスワード変更用の DTO リクエストモデルです。
/// </summary>
public record ChangePasswordDto(string CurrentPassword, string NewPassword);

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
