#!/bin/sh
# ============================================================
# HabitTracker - Google Cloud Run デプロイスクリプト
# ============================================================

set -e

# デフォルト環境変数の設定
REGION="${REGION:-asia-northeast1}"
SERVICE_NAME="${SERVICE_NAME:-habit-tracker-app}"
BUCKET_NAME="${BUCKET_NAME:-habit-tracker-db-bucket}"

echo "============================================================"
echo "HabitTracker のデプロイを開始します"
echo "リージョン: ${REGION}"
echo "サービス名: ${SERVICE_NAME}"
echo "バケット名: ${BUCKET_NAME}"
echo "============================================================"

# 1. GCS バケットの存在チェックおよび自動作成
if ! gcloud storage buckets describe "gs://${BUCKET_NAME}" >/dev/null 2>&1; then
    echo "📦 GCSバケット gs://${BUCKET_NAME} を作成しています..."
    gcloud storage buckets create "gs://${BUCKET_NAME}" --location="${REGION}"
else
    echo "📦 GCSバケット gs://${BUCKET_NAME} は既に存在します。"
fi

# 2. Cloud Run へのビルド＆デプロイ
echo "🚀 Cloud Run へのデプロイを実行中..."
gcloud run deploy "${SERVICE_NAME}" \
    --source=. \
    --region="${REGION}" \
    --allow-unauthenticated \
    --max-instances=1 \
    --concurrency=1000 \
    --cpu-boost \
    --add-volume="name=db-volume,type=cloud-storage,bucket=${BUCKET_NAME}" \
    --add-volume-mount="volume=db-volume,mount-path=/app/data" \
    --set-env-vars="DB_DIR=/app/data"

echo "============================================================"
echo "✅ デプロイが完了しました！"
echo "============================================================"
