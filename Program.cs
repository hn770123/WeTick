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
}

// ------------------------------------------------------------
// ルーティング定義 (Hono風 Minimal API)
// ------------------------------------------------------------

/// <summary>
/// ヘルスチェックおよび動作確認用エンドポイント
/// </summary>
app.MapGet("/", () => Results.Ok(new { status = "healthy", message = "HabitTracker Minimal API Phase 1 Initialized" }));

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

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");
