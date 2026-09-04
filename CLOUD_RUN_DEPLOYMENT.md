# Cloud Run デプロイ手順書 (2026年版)

本書は、.NET 10 アプリケーション (.NET Minimal API + SQLite) を Google Cloud Run へ**最小のコマンド操作・パラメータ指定**でデプロイするための手順書です。

2026年現在のベストプラクティスとして、GUI (Webコンソール) や `gcloud` の長いパラメータフラグを使わず、リポジトリ内の設定ファイル (`service.yaml`) による**宣言的デプロイ**を採用しています。

---

## 1. 概要と構成要素

- **アプリケーション構成**: .NET 10 Minimal API + SQLite
- **コンテナビルド**: ASP.NET Core 10 Alpine ランタイム (マルチステージビルド)
- **構成管理**: `service.yaml` (Cloud Run サービス定義ファイル)
- **データベース永続化**: Google Cloud Storage (GCS) ボリュームマウント (`/app/data`)
- **インスタンス制限**: `maxScale: 1` (SQLiteのデータ競合・破損防止)

---

## 2. リポジトリ内の構成ファイル

デプロイ設定はリポジトリ内のコードとして管理されます。

### 📄 `service.yaml` (Cloud Run サービス定義)

```yaml
apiVersion: serving.knative.dev/v1
kind: Service
metadata:
  name: habit-tracker-service
  labels:
    cloud.googleapis.com/location: asia-northeast1
spec:
  template:
    metadata:
      annotations:
        autoscaling.knative.dev/maxScale: "1"
        run.googleapis.com/startup-cpu-boost: "true"
    spec:
      containerConcurrency: 80
      containers:
        - image: asia-northeast1-docker.pkg.dev/PROJECT_ID/habit-tracker-repo/habit-tracker:latest
          ports:
            - name: http1
              containerPort: 8080
          resources:
            limits:
              cpu: "1"
              memory: 512Mi
          volumeMounts:
            - name: db-volume
              mountPath: /app/data
      volumes:
        - name: db-volume
          csi:
            driver: gcsfuse.run.googleapis.net
            volumeAttributes:
              bucketName: PROJECT_ID-db-bucket
```

※ `PROJECT_ID` 部分をご自身の GCP プロジェクト ID に置き換えて使用します。

---

## 3. 初回セットアップ (環境準備)

初回のみ、以下のリソース作成と初期設定を行います。

```bash
# 1. GCP プロジェクトの指定と必要 API の有効化
gcloud config set project YOUR_PROJECT_ID
gcloud services enable run.googleapis.com artifactregistry.googleapis.com storage.googleapis.com

# 2. Artifact Registry リポジトリの作成
gcloud artifacts repositories create habit-tracker-repo \
    --repository-format=docker \
    --location=asia-northeast1

# 3. SQLite 永続化用 GCS バケットの作成
gcloud storage buckets create gs://YOUR_PROJECT_ID-db-bucket --location=asia-northeast1
```

---

## 4. デプロイ手順 (宣言的デプロイ)

長いオプション指定や GUI 操作は不要です。イメージのビルドと設定ファイルの適用のみで完了します。

### ステップ 1: コンテナイメージのビルド & プッシュ

```bash
gcloud builds submit --tag asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest .
```

### ステップ 2: サービス定義の適用 (デプロイ)

`service.yaml` を指定してサービスをデプロイします。

```bash
gcloud run services replace service.yaml
```

認証なしアクセス (公開) を許可する場合は、初回のみ以下のコマンドを併せ実行します。

```bash
gcloud run services set-iam-policy habit-tracker-service --region=asia-northeast1 policy.yaml
```
※ または `gcloud run deploy habit-tracker-service --image=... --region=asia-northeast1 --allow-unauthenticated` の短縮コマンド等でも設定可能です。

---

## 5. GitHub Actions による CI/CD ワークフロー例

`.github/workflows/deploy.yml` でも `service.yaml` を読み込むことで、ワークフローの定義を非常にシンプルに保つことができます。

```yaml
name: Deploy to Cloud Run

on:
  push:
    branches:
      - main

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
          workload_identity_provider: 'projects/123456789/locations/global/workloadIdentityPools/my-pool/providers/my-provider'
          service_account: 'github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com'

      - name: Set up Cloud SDK
        uses: google-github-actions/setup-gcloud@v2

      - name: Build and Push Container
        run: |
          gcloud builds submit --tag asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:${{ github.sha }} .
          gcloud builds submit --tag asia-northeast1-docker.pkg.dev/YOUR_PROJECT_ID/habit-tracker-repo/habit-tracker:latest .

      - name: Deploy to Cloud Run using service.yaml
        run: |
          gcloud run services replace service.yaml
```

---

## 6. 運用のポイント・注意事項

1. **宣言的設定管理 (`service.yaml`) のメリット**
   - リソース制限・環境変数・ボリュームマウント設定がすべてコード化され、`gcloud` コマンドの長いオプション指定やコンソール画面での誤操作を防げます。
2. **SQLite と GCS Volume Mount (`gcsfuse`)**
   - Cloud Run 上の SQLite データの破損を防止するため、`autoscaling.knative.dev/maxScale: "1"` の設定を維持してください。
3. **起動速度の最適化 (`startup-cpu-boost`)**
   - `run.googleapis.com/startup-cpu-boost: "true"` を有効化することで、コンテナ起動時の CPU が一時的に強化され、JIT コンパイルのオーバーヘッドを削減できます。
