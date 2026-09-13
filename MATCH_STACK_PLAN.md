# MATCH Stack (.NET 10) 移行計画書 (MATCH_STACK_PLAN.md)

## 1. 概要・目的
本ドキュメントは、現在の HabitTracker Web アプリケーション（ASP.NET Core Minimal API + Pico.css + クライアントサイド JavaScript / Razor View）を、最新のモダンWebスタックである **MATCH Stack** (.NET 10 / ASP.NET Core 10) へ作り替える（リファクタリング・再構築する）ための移行計画書である。

### MATCH Stack の定義
- **M**: Minimal APIs (ASP.NET Core 10)
- **A**: Alpine.js (軽量フロントエンドUIフレームワーク / 状態管理)
- **T**: Tailwind CSS (ユーティリティファーストCSS)
- **C**: C# (.NET 10 バックエンド言語 / Dapper / SQLite)
- **H**: htmx (サーバー駆動HTMLフラグメント更新 & Ajax通信)

---

## 2. 現行アーキテクチャの課題と MATCH Stack 導入のメリット

### 2.1 現行アーキテクチャの課題
1. **クライアント側 JavaScript の肥大化**:
   - `Index.cshtml` 内に `fetch()`API 呼出や DOM 生成 (`innerHTML` 組み立て) の大規模な JavaScript コードが埋め込まれており、保守性・可読性が低下している。
2. **デザイン表現力の制限**:
   - Pico.css (classless CSS) を使用しているため、モバイルフレンドリーで細やかなカスタムデザイン（タイムラインカード、リアクションバッジ等）の実現に制約がある。
3. **サーバー / クライアント間の二重実装**:
   - JSON データを API で返し、クライアント JavaScript で HTML を動的に組み立てているため、描画ロジックがクライアントに偏重している。

### 2.2 MATCH Stack 導入による効果
1. **htmx による HTML フラグメント駆動**:
   - サーバー（Minimal API / Razor Fragment）から直接 HTML フラグメントを返却し、DOM を部分置換することで JavaScript コード量を大幅削減（No-Build / Low-JS）。
2. **Alpine.js によるローカル状態の簡便な操作**:
   - モーダル開閉やインライン編集のトグル、タブ切替などの UI 状態を宣言的に記述可能。
3. **Tailwind CSS による柔軟かつ高品質な UI**:
   - Pico.css から Tailwind CSS (CDN または CLI) に移行し、モダンスタイル（ダークモード、レスポンシブボトムナビ、アニメーション）を高速に開発可能。
4. **.NET 10 / Minimal API / C# の最適化**:
   - C# 13 / .NET 10 の言語機能と Minimal API の Route Group、Typed Results を活用し、高速かつシンプルなバックエンドを実現。

---

## 3. システムアーキテクチャ構成

```
[ Browser Client ]
   │
   ├─ Alpine.js (クライアントローカル状態・UIバインディング)
   ├─ htmx (hx-get / hx-post による非同期HTMLフラグメント取得)
   └─ Tailwind CSS (レスポンシブデザイン)
   │
   ▼ HTTP (HTML Fragments / JSON)
[ ASP.NET Core 10 Minimal APIs ]
   │
   ├─ Endpoints (Route Groups)
   ├─ Razor Fragment Rendering (Partial Views / Typed Results)
   ├─ Authentication & Cookie Session
   └─ Dapper (ORM / Data Access)
   │
   ▼
[ SQLite Database (habittracker.db) ]
```

---

## 4. 段階的移行ロードマップ (Migration Phases)

### Phase 1: プロジェクト基盤＆パッケージの更新 (.NET 10 & Tailwind / Alpine / htmx 導入)
- **1.1 `.csproj` の調整**:
  - TargetFramework を `net10.0` に設定（確定）。
  - Dapper, Microsoft.Data.Sqlite の最新互換パッケージの確認。
- **1.2 静的アセット (CSS/JS) の MATCH Stack 構成化**:
  - Pico.css を削除し、Tailwind CSS (CDN またはビルド設定)、htmx (v2.x)、Alpine.js (v3.x) を `wwwroot` または HTML ヘッダー (`_Layout.cshtml`) へ導入。
  - レスポンシブボトムナビゲーションおよびダークモードベースの共通レイアウトを作成。

### Phase 2: バックエンド (Minimal API) のモジュール化・再構築
- **2.1 Route Groups によるエンドポイント分離**:
  - `Program.cs` の巨大化を防ぐため、`MapGroup` を使用してエンドポイントを機能別に分離。
    - `/api/auth` (認証関連)
    - `/api/users` (ユーザープロファイル管理)
    - `/api/habits` (習慣タスク管理)
    - `/api/groups` (グループ・メンバー管理)
    - `/api/timeline` (タイムライン・リアクション・コメント)
- **2.2 HTML フラグメント返却（Razor Partials / Results.Extensions）対応**:
  - htmx からのリクエスト (`HX-Request` ヘッダー判定) に対応し、JSON ではなく HTML パーシャル (`_TimelinePartial.cshtml`, `_HabitListPartial.cshtml` 等) を返却するエンドポイントを追加。

### Phase 3: UI/UX (Views & Components) の MATCH Stack 化
- **3.1 タイムライン画面 (Timeline View)**:
  - `hx-get="/timeline/feed"` による自動更新およびスクロール読み込み。
  - リアクションボタン・コメント送信を `hx-post` 化。送信後に最新のリアクション/コメントエリアのみを部分更新。
- **3.2 タスクのチェック画面 (Habit Execution View)**:
  - ワンタップ実行ボタンを `hx-post="/habits/{id}/execute"` に変換。
  - 実行成功時、Alpine.js によるトースト通知表示およびプログレス更新。
- **3.3 設定＆管理画面 (Settings View)**:
  - 習慣登録・編集モーダルを Alpine.js (`x-data="{ open: false }"`) で制御。
  - インライン編集・削除を htmx (`hx-put`, `hx-delete`) でシームレス化。
  - グループ作成・参加フォームの非同期処理化。

### Phase 4: PWA / オフライン / テスト & パフォーマンス検証
- **4.1 Service Worker & LocalStorage (Stale-While-Revalidate)**:
  - htmx のオフラインキャッシュまたは Alpine.js ストアとの連携によるオフライン実行記録の保持。
- **4.2 動作検証 & 性能テスト**:
  - 既存機能の全テスト (認証、習慣CRUD、ワンタップ実行、グループタイムライン、リアクション、パスワード変更)。
  - `dotnet build` / `dotnet test` の通過確認。
  - Google Cloud Run デプロイ要件 (GCS ボウント / SQLite `--max-instances=1`) との整合性検証。

---

## 5. 主要コンポーネント実装例

### 5.1 Razor Layout (`Views/Shared/_Layout.cshtml`)
```html
<!DOCTYPE html>
<html lang="ja" class="dark h-full bg-slate-900 text-slate-100">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - HabitTracker</title>
    <!-- Tailwind CSS (CDN) -->
    <script src="https://cdn.tailwindcss.com"></script>
    <!-- htmx -->
    <script src="https://unpkg.com/htmx.org@2.0.0"></script>
    <!-- Alpine.js -->
    <script defer src="https://unpkg.com/alpinejs@3.x.x/dist/cdn.min.js"></script>
</head>
<body class="h-full flex flex-col justify-between pb-20">
    <main class="container mx-auto px-4 py-6 max-w-lg">
        @RenderBody()
    </main>
    <!-- MATCH Stack Bottom Nav Component -->
    @await Html.PartialAsync("_BottomNav")
</body>
</html>
```

### 5.2 htmx + Alpine.js による習慣ワンタップ実行 (`_HabitItem.cshtml`)
```html
<div x-data="{ commentOpen: false }" class="bg-slate-800 p-4 rounded-xl mb-3 flex flex-col gap-2 border border-slate-700">
    <div class="flex justify-between items-center">
        <div class="flex items-center gap-3">
            <span class="text-2xl">@Model.Emoji</span>
            <div>
                <h4 class="font-bold text-slate-100">@Model.Title</h4>
                <p class="text-xs text-slate-400">@Model.Frequency</p>
            </div>
        </div>
        <button hx-post="/habits/@Model.Id/execute"
                hx-target="#habit-item-@Model.Id"
                hx-swap="outerHTML"
                class="bg-blue-600 hover:bg-blue-500 text-white font-bold px-4 py-2 rounded-lg text-sm transition">
            ✅ 実行
        </button>
    </div>
</div>
```

---

## 6. まとめ・今後の進め方
本計画書に基づき、既存機能との互換性を完全に保ちながら、JavaScriptコードを約70%削減し、保守性と拡張性に優れた **MATCH Stack (.NET 10)** アプリケーションへの移行を段階的に進める。
