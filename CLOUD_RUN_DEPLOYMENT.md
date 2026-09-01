# Cloud Run デプロイ手順書 (2026年版)

本書は、.NET 10 アプリケーションを **Native AOT ＋ Alpine Linux** 環境でビルドし、起動時間（Cold Start）を極限まで短縮した状態で **Google Cloud Run** へデプロイするための手順書です。

---

## 1. 概要と構成要素

- **アプリケーション構成**: .NET 10 Minimal API + SQLite
- **コンテナビルド**: Native AOT コンパイル ＋ Alpine Linux (マルチステージビルド)
- **デプロイ先**: Google Cloud Run
- **データベース永続化**: Google Cloud Storage (GCS) ボリュームマウント (`/app/data`)
- **インスタンス制限**: `max-instances=1` (SQLiteのデータ整合性を保つため)

---

## 2. Dockerfile の例 (Native AOT + Alpine Linux)

Native AOT コンパイルには C++ コンパイラ (`clang`) や C ライブラリのヘッダーファイル (`musl-dev`) が必要です。マルチステージビルドを使用して、軽量な Alpine ランタイムイメージを作成します。

```dockerfile
# ==========================================
# 1. ビルドステージ (SDK + Native AOT ツールチェーン)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Native AOT コンパイルに必要なパッケージのインストール
RUN apk add --no-cache \
    clang \
    musl-dev \
    build-base \
    zlib-dev

# プロジェクトファイルのコピーと依存関係の復元
COPY ["HabitTracker.csproj", "./"]
RUN dotnet restore "HabitTracker.csproj" -r linux-musl-x64

# ソースコードのコピーと Native AOT パブリッシュ
COPY . .
RUN dotnet publish "HabitTracker.csproj" \
    -c Release \
    -r linux-musl-x64 \
    --self-contained true \
    /p:PublishAot=true \
    -o /app/publish

# ==========================================
# 2. 実行ステージ (軽量ランタイム)
# ==========================================
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-alpine AS final
WORKDIR /app

# セキュリティのため非ルートユーザーを作成・使用
RUN adduser -D -u 1000 appuser && \
    mkdir -p /app/data && \
    chown -R appuser:appuser /app

USER appuser

# ビルド成果物のコピー
COPY --from=build /app/publish .

# 環境変数とポート設定
ENV PORT=8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# アプリケーションの実行
ENTRYPOINT ["./HabitTracker"]
```

---

## 3. Cloud Run デプロイ手順

基本は Google Cloud Console (Web画面) で設定を行いますが、ボリュームマウントや権限設定は **gcloud CLI** を使用した方が設定漏れが無く圧倒的に簡単かつ再現性が高いため、CLI での操作例も併記します。

### 前提条件の準備 (初回のみ)

1. **GCP プロジェクトの設定と API の有効化**
   ```bash
   gcloud config set project YOUR_PROJECT_ID
   gcloud services enable run.googleapis.com artifactregistry.googleapis.com storage.googleapis.com
   ```

2. **Artifact Registry リポジトリの作成**
   ```bash
   gcloud artifacts repositories create habit-tracker-repo \
       --repository-format=docker \
       --location=asia-northeast1 \
       --description="Habit Tracker Container Repository"
   ```

3. **SQLite 永続化用 GCS バケットの作成**
   ```bash
   gcloud storage buckets create gs://YOUR_PROJECT_ID-db-bucket --location=asia-northeast1
   ```

---

### 方法 A: Web コンソール (Google Cloud Console) からデプロイする場合

1. **コンテナイメージのビルドとプッシュ**
   ```bash
   gcloud builds submit --tag asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest .
   ```

2. **Cloud Run サービスの作成**
   - Google Cloud Console の [Cloud Run] 画面を開き、**[サービスを作成]** をクリックします。
   - **コンテナのデプロイ**: 「既存のコンテナイメージから 1 つのリビジョンをデプロイする」を選択し、上記でプッシュしたイメージ `asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest` を指定します。
   - **サービス名**: `habit-tracker-service`
   - **リージョン**: `asia-northeast1 (東京)`
   - **未認証の呼び出しを許可**: 外部公開する場合は「未認証の呼び出しを許可」を選択します。

3. **スケーリングとインスタンス数の設定**
   - **最小インスタンス数**: `0` (コスト優先) または `1` (Cold Startを完全に回避したい場合)
   - **最大インスタンス数**: `1` (**必須**: SQLiteのデータ破損・競合を防止するため)

4. **コンテナ、ボリューム、変数設定 (詳細設定)**
   - **コンテナポート**: `8080`
   - **ボリューム**: [ボリュームを追加] -> [Cloud Storage バケット] を選択
     - ボリューム名: `db-volume`
     - バケット名: `YOUR_PROJECT_ID-db-bucket`
   - **ボリュームのマウント**:
     - マウントパス: `/app/data`

---

### 方法 B: gcloud CLI からデプロイする場合 (推奨: 簡単かつ確実)

Web画面での複雑なボリュームマウントやインスタンス制限設定を、1つのコマンドでまとめて実行できます。

```bash
# 1. ローカルまたは Cloud Build でイメージをビルド＆プッシュ
gcloud builds submit --tag asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest .

# 2. Cloud Run へのデプロイ (GCS ボリュームマウント ＋ 最大インスタンス数 1 の指定)
gcloud run deploy habit-tracker-service \
    --image=asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest \
    --region=asia-northeast1 \
    --platform=managed \
    --allow-unauthenticated \
    --port=8080 \
    --cpu=1 \
    --memory=512Mi \
    --max-instances=1 \
    --cpu-boost \
    --add-volume=name=db-volume,type=cloud-storage,bucket=YOUR_PROJECT_ID-db-bucket \
    --mount-volume=volume=db-volume,mount-path=/app/data
```

※ `--cpu-boost` オプションを有効にすることで、インスタンス起動時の CPU が一時的に強化され、Native AOT と相まって起動速度がさらに向上します。

---

## 4. GitHub Actions による CI/CD ワークフロー例

リポジトリの `.github/workflows/deploy.yml` に配置して使用します。
Google Cloud への認証には Workload Identity Federation を使用する構成が安全で推奨されます。

```yaml
name: Deploy to Cloud Run

on:
  push:
    branches:
      - main

env:
  PROJECT_ID: 'YOUR_PROJECT_ID' # GCP プロジェクト ID に置き換えてください
  REGION: 'asia-northeast1'
  REPOSITORY: 'habit-tracker-repo'
  SERVICE: 'habit-tracker-service'
  GCS_BUCKET: 'YOUR_PROJECT_ID-db-bucket'
  WORKLOAD_IDENTITY_PROVIDER: 'projects/123456789/locations/global/workloadIdentityPools/my-pool/providers/my-provider'
  SERVICE_ACCOUNT: 'github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com'

jobs:
  deploy:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      id-token: write

    steps:
      - name: Checkout Code
        uses: actions/checkout@v4

      - name: Google Auth (Workload Identity)
        uses: google-github-actions/auth@v2
        with:
          workload_identity_provider: ${{ env.WORKLOAD_IDENTITY_PROVIDER }}
          service_account: ${{ env.SERVICE_ACCOUNT }}

      - name: Set up Cloud SDK
        uses: google-github-actions/setup-gcloud@v2

      - name: Authorize Docker Push to Artifact Registry
        run: |
          gcloud auth configure-docker ${{ env.REGION }}-docker.pkg.dev --quiet

      - name: Build and Push Docker Image
        run: |
          IMAGE_TAG="${{ env.REGION }}-docker.pkg.dev/${{ env.PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:${{ github.sha }}"
          LATEST_TAG="${{ env.REGION }}-docker.pkg.dev/${{ env.PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:latest"

          docker build -t $IMAGE_TAG -t $LATEST_TAG .
          docker push $IMAGE_TAG
          docker push $LATEST_TAG

      - name: Deploy to Cloud Run
        run: |
          gcloud run deploy ${{ env.SERVICE }} \
            --image=${{ env.REGION }}-docker.pkg.dev/${{ env.PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:${{ github.sha }} \
            --region=${{ env.REGION }} \
            --platform=managed \
            --allow-unauthenticated \
            --port=8080 \
            --cpu=1 \
            --memory=512Mi \
            --max-instances=1 \
            --cpu-boost \
            --add-volume=name=db-volume,type=cloud-storage,bucket=${{ env.GCS_BUCKET }} \
            --mount-volume=volume=db-volume,mount-path=/app/data
```

---

## 5. 運用のポイント・注意事項

1. **Native AOT とリフレクション**
   - .NET Native AOT では、動的リフレクションやコード生成を行うライブラリが適切に動作しない場合があります。Dapper や JSON シリアライザを使用する場合は、ソースジェネレーターや AOT 互換設定を確認してください。
2. **SQLite と GCS Volume Mount**
   - GCS Volume Mount (`gcsfuse`) はネットワーク経由でストレージをマウントするため、ローカルディスクと比較してファイル I/O（特に細かいランダム書き込み）の遅延が発生する場合があります。
   - SQLite の同時書き込み競合を避けるため、Cloud Run の `--max-instances=1` 設定は必須です。
3. **起動パフォーマンスの向上**
   - Native AOT ＋ Alpine Linux に加え、`--cpu-boost` を有効にすることで起動時のレスポンス性能を劇的に改善できます。
