# HabitTracker デプロイ手順書 (DEPLOY.md)

本ドキュメントでは、HabitTracker Web アプリケーションを Google Cloud Run および Cloud Storage (GCS) ボリュームマウント環境にデプロイする手順を解説します。

---

## 1. 前提条件

- [Google Cloud SDK (gcloud CLI)](https://cloud.google.com/sdk/docs/install) がインストールされていること
- GCP プロジェクトが作成され、適切な権限 (Cloud Run 管理者、Storage 管理者等) が付与されていること
- `gcloud auth login` および `gcloud config set project YOUR_PROJECT_ID` でログイン済みであること

---

## 2. 自動デプロイスクリプトを使用したデプロイ

リポジトリ直下の `deploy.sh` スクリプトを使用して自動デプロイを行うことができます。

```bash
# 実行権限の付与
chmod +x deploy.sh

# デプロイの実行（デフォルト設定: リージョン asia-northeast1）
./deploy.sh
```

### カスタム環境変数の指定

必要に応じて以下の環境変数を上書きして実行できます。

```bash
REGION="asia-northeast1" \
SERVICE_NAME="my-habit-tracker" \
BUCKET_NAME="my-habit-tracker-bucket" \
./deploy.sh
```

---

## 3. 手動デプロイ手順

### Step 1: SQLite DB 永続化用 GCS バケットの作成

```bash
gcloud storage buckets create gs://habit-tracker-db-bucket --location=asia-northeast1
```

### Step 2: Cloud Run へのソースコードデプロイ

SQLite のシングルライター構成に対応するため、`--max-instances=1` を指定してデプロイします。

```bash
gcloud run deploy habit-tracker-app \
    --source=. \
    --region=asia-northeast1 \
    --allow-unauthenticated \
    --max-instances=1 \
    --concurrency=1000 \
    --cpu-boost \
    --add-volume=name=db-volume,type=cloud-storage,bucket=habit-tracker-db-bucket \
    --add-volume-mount=volume=db-volume,mount-path=/app/data \
    --set-env-vars=DB_DIR=/app/data
```

---

## 4. アーキテクチャと設定ポイント

1. **GCS ボリュームマウント (`/app/data`)**
   - SQLite データベース (`habittracker.db`) を Cloud Storage バケット上に永続化します。
2. **最大インスタンス数 (`--max-instances=1`)**
   - SQLite のファイルロック競合を防ぐため、コンテナインスタンス数を1に制限します。
3. **Startup CPU Boost (`--cpu-boost`)**
   - サーバーレスのコールドスタートを高速化します。
4. **.NET 10 コンテナ化 (`Dockerfile`)**
   - マルチステージビルドにより、軽量な実行用コンテナイメージを自動生成します。
