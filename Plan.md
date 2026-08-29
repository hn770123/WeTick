# 習慣トラッキング＆タイムラインWebアプリケーション 実装計画書 (Plan.md)

## 1. システム概要・目的
本システムは、ユーザーが日常の定例的なタスク（習慣）を登録し、ワンタップで実行記録を保存・共有できるWebアプリケーションである。
グループ機能により、家族やチーム、友人同士で習慣の達成状況をタイムライン形式で確認・応援（いいね）し合うことができる。

---

## 2. 主要機能要件

### 2.1 ユーザー管理・認証
- ユーザー登録 / ログイン / プロフィール管理
- チーム・グループへの所属管理

### 2.2 習慣（定例タスク）管理
- 習慣の新規登録・編集・削除
- 達成条件や頻度の設定（毎日、毎週特定曜日など）

### 2.3 ワンタップ実行記録
- 登録された習慣一覧からワンタップで実行ログ（実績）を登録
- メモや写真（任意）の添付機能

### 2.4 グループ機能
- グループの作成・参加・招待コード共有
- メンバーの進捗状況およびグループ内タスクの管理

### 2.5 タイムライン＆応援（いいね）機能
- 自分およびグループメンバーの実行記録が時系列で並ぶタイムライン画面
- タイムライン上の投稿に対するリアクション（いいね・応援スタンプ・コメント）

---

## 3. システムアーキテクチャ・技術選定

- **フロントエンド**: PWA (Petite-Vue / Tailwind CSS / htmx)
- **バックエンド**: C# Minimal API (.NET 9 Native AOT) / Cloud Run
- **データベース**: SQLite (GCS Mount) / LocalStorage (Stale-While-Revalidate 同期)
- **ストレージ**: Google Cloud Storage (GCS)

---

## 4. データ構造・DB設計 (概要)

### 4.1 Users (ユーザー)
- `Id`: INTEGER (PK)
- `Name`: TEXT
- `Email`: TEXT
- `CreatedAt`: TEXT

### 4.2 Groups (グループ)
- `Id`: INTEGER (PK)
- `Name`: TEXT
- `InviteCode`: TEXT
- `CreatedAt`: TEXT

### 4.3 GroupMembers (グループ所属)
- `GroupId`: INTEGER (FK)
- `UserId`: INTEGER (FK)
- `Role`: TEXT

### 4.4 Habits (習慣タスク)
- `Id`: INTEGER (PK)
- `UserId`: INTEGER (FK)
- `Title`: TEXT
- `Description`: TEXT
- `Frequency`: TEXT
- `CreatedAt`: TEXT

### 4.5 ExecutionLogs (実行記録)
- `Id`: INTEGER (PK)
- `HabitId`: INTEGER (FK)
- `UserId`: INTEGER (FK)
- `ExecutedAt`: TEXT
- `Comment`: TEXT

### 4.6 Likes (いいね・リアクション)
- `Id`: INTEGER (PK)
- `ExecutionLogId`: INTEGER (FK)
- `UserId`: INTEGER (FK)
- `ReactionType`: TEXT
- `CreatedAt`: TEXT

---

## 5. 画面設計・UI構成

1. **ダッシュボード / マイ習慣画面**
   - 本日の習慣一覧およびワンタップ実行ボタン
2. **タイムライン画面**
   - 自分とグループメンバーの実行記録一覧、いいね・コメントボタン
3. **グループ管理画面**
   - 所属グループ一覧、新規作成・参加、メンバー一覧
4. **設定 / プロフィール画面**
   - ユーザー情報編集、通知設定等

---

## 6. 実装ステップ・スケジュール

- **Phase 1**: 基本構造・DB設計およびMinimal APIのセットアップ
- **Phase 2**: 習慣登録＆ワンタップ実行記録機能の実装
- **Phase 3**: グループ機能およびタイムライン画面の実装
- **Phase 4**: いいね・応援リアクション機能の実装
- **Phase 5**: PWA対応・オフライン対応 (LocalStorage sync)
- **Phase 6**: テスト・デプロイ設定 (Cloud Run + GCS)
