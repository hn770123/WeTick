# Cloud Run デプロイ手順書 (GUI & GitHub Actions版)

本書は、.NET アプリケーションを **Google Cloud Console (Web GUI)** を中心に設定し、**GitHub Actions ワークフロー** を用いて自動デプロイを行うための手順書です。
シークレット（機密情報）の発行・登録手順や、GUI のみでは実施が困難な設定（API有効化、Workload Identity、IAM権限など）に必要な CLI コマンドも併せて解説します。

---

## 1. 概要と構成要素

- **アプリケーション構成**: .NET Minimal API + SQLite
- **デプロイ先**: Google Cloud Run
- **データベース永続化**: Google Cloud Storage (GCS) ボリュームマウント (`/app/data`)
- **インスタンス制限**: `最大インスタンス数 = 1` (SQLiteのデータ整合性を保つため必須)
- **CI/CD**: GitHub Actions ワークフローによる自動デプロイ
- **認証・機密情報管理**:
  - Google Cloud Secret Manager (アプリケーションの実行時シークレット)
  - Workload Identity Federation (GitHub Actions 用のキーレス GCP 認証)
  - GitHub Repository Secrets (ワークフロー設定用)

---

## 2. 前提条件と GUI で設定できない項目 (gcloud CLI 設定)

GCP の各種 API 有効化や、セキュリティに関する IAM・Workload Identity Federation の構築は、コマンドライン (gcloud CLI) を使用するのが安全かつ確実です。Google Cloud Shell やローカル端末の `gcloud` CLI で以下の設定を行ってください。

### 2.1. GCP プロジェクトの設定と API 有効化
```bash
# プロジェクトIDの設定
gcloud config set project YOUR_PROJECT_ID

# 必要な API の有効化
gcloud services enable \
    run.googleapis.com \
    artifactregistry.googleapis.com \
    storage.googleapis.com \
    secretmanager.googleapis.com \
    iamcredentials.googleapis.com
```

### 2.2. Artifact Registry リポジトリと GCS バケットの作成
```bash
# コンテナイメージ保存用リポジトリの作成
gcloud artifacts repositories create habit-tracker-repo \
    --repository-format=docker \
    --location=asia-northeast1 \
    --description="Habit Tracker Docker Repository"

# SQLite 永続化用 GCS バケットの作成
gcloud storage buckets create gs://YOUR_PROJECT_ID-db-bucket --location=asia-northeast1
```

### 2.3. GitHub Actions 用 IAM サービスアカウントと Workload Identity の構築
パスワードやサービスアカウントキーを発行せず、安全に GitHub Actions と GCP を連携させます。

```bash
# 1. デプロイ用サービスアカウントの作成
gcloud iam service-accounts create github-actions-deployer \
    --display-name="GitHub Actions Deployer"

# 2. サービスアカウントに必要な権限（ロール）を付与
gcloud projects add-iam-policy-binding YOUR_PROJECT_ID \
    --member="serviceAccount:github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
    --role="roles/run.developer"

gcloud projects add-iam-policy-binding YOUR_PROJECT_ID \
    --member="serviceAccount:github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
    --role="roles/artifactregistry.writer"

gcloud projects add-iam-policy-binding YOUR_PROJECT_ID \
    --member="serviceAccount:github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
    --role="roles/iam.serviceAccountUser"

gcloud projects add-iam-policy-binding YOUR_PROJECT_ID \
    --member="serviceAccount:github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com" \
    --role="roles/secretmanager.secretAccessor"

# 3. Workload Identity プールとプロバイダの作成
gcloud iam workload-identity-pools create "github-pool" \
    --location="global" \
    --display-name="GitHub Actions Pool"

gcloud iam workload-identity-pools providers create-oidc "github-provider" \
    --location="global" \
    --workload-identity-pool="github-pool" \
    --display-name="GitHub Provider" \
    --attribute-mapping="google.subject=assertion.sub,attribute.actor=assertion.actor,attribute.repository=assertion.repository" \
    --issuer-uri="https://token.actions.githubusercontent.com"

# 4. 特定のリポジトリからの認証を許可
# (YOUR_GITHUB_OWNER/YOUR_REPO をご自身のリポジトリ情報に置き換えてください)
gcloud iam service-accounts add-iam-policy-binding \
    github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com \
    --role="roles/iam.workloadIdentityUser" \
    --member="principalSet://iam.googleapis.com/projects/YOUR_PROJECT_NUMBER/locations/global/workloadIdentityPools/github-pool/attribute.repository/YOUR_GITHUB_OWNER/YOUR_REPO"
```

---

## 3. シークレットの発行と登録手順

機密情報（データベース接続文字列や API キー、認証トークン等）はコードに直書きせず、適切に暗号化して管理します。

### 3.1. Google Secret Manager でのシークレット作成と登録
アプリケーション実行時に必要なシークレットを Google Cloud で管理します。

#### 【Web GUI (Google Cloud Console) からの作成手順】
1. Google Cloud Console メニューから **[Secret Manager]** を開きます。
2. 上部の **[シークレットを作成]** をクリックします。
3. **名前**: 例 `APP_SECRET_KEY` や `DATABASE_URL` などを入力します。
4. **シークレットの値**: 秘密の値（APIキーや暗号化キーなど）を入力します。
5. **[シークレットを作成]** ボタンをクリックして保存します。

#### 【Cloud Run 実行用サービスアカウントへの権限付与】
Cloud Run が Secret Manager から値を取得できるように権限を設定します。
- 通常、Cloud Run はデフォルトの Compute Engine サービスアカウント (`YOUR_PROJECT_NUMBER-compute@developer.gserviceaccount.com`) で動作します。
- Secret Manager の該当シークレットの [権限] タブから、対象のサービスアカウントに `シークレット参照者 (roles/secretmanager.secretAccessor)` ロールを付与してください。

---

### 3.2. GitHub Secrets への登録
GitHub Actions ワークフローで GCP へアクセス・デプロイするために必要な情報をリポジトリの Secret に登録します。

1. GitHub の対象リポジトリを開き、**[Settings]** サービス -> **[Secrets and variables]** -> **[Actions]** を選択します。
2. **[New repository secret]** をクリックし、以下の項目をそれぞれ追加します。

| Secret 名 | 設定する値の説明 |
| :--- | :--- |
| `GCP_PROJECT_ID` | GCP のプロジェクト ID (例: `my-sample-project-12345`) |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | Workload Identity プロバイダのフルパス<br>`projects/YOUR_PROJECT_NUMBER/locations/global/workloadIdentityPools/github-pool/providers/github-provider` |
| `GCP_SERVICE_ACCOUNT` | 作成したデプロイ用 IAM サービスアカウントのメールアドレス<br>`github-actions-deployer@YOUR_PROJECT_ID.iam.gserviceaccount.com` |

---

## 4. Google Cloud Run の Web GUI (Google Cloud Console) 設定

初回設定やサービスパラメータの管理を Web GUI で行う手順です。

### 4.1. Cloud Run サービスの新規作成・設定手順
1. Google Cloud Console の **[Cloud Run]** 画面を開き、**[サービスを作成]** をクリックします。
2. **コンテナのデプロイ**:
   - 初回デプロイ時は「サンプルコンテナからリビジョンをデプロイする」または作成済みの Artifact Registry イメージを選択します。
3. **サービス名 & リージョン**:
   - サービス名: `habit-tracker-service`
   - リージョン: `asia-northeast1 (東京)`
4. **一般設定 / 入力トラフィック**:
   - 外部公開する場合は「すべてのトラフィックを許可する」を選択します。
   - 認証: 「未認証の呼び出しを許可」を選択します。

### 4.2. スケーリング設定 (重要)
- **最小インスタンス数**: `0` (コスト優先) または `1` (Cold Start 回避)
- **最大インスタンス数**: **`1`**
  > **注意**: SQLite ファイルを GCS ボリュームマウントで共有しているため、複数インスタンスからの同時書き込みによるデータベース破損を防ぐため、**必ず `1` に設定**してください。

### 4.3. コンテナ・ボリューム・シークレット・CPUブースト設定
画面下部の **[コンテナ、ボリューム、ネットワーク、セキュリティ]** のアコーディオンを展開して設定します。

1. **[コンテナ] タブ**:
   - **コンテナポート**: `8080`
   - **CPU の割り当てとパラレル処理**: 「リクエスト処理中のみ CPU を割り当てる」または「CPU を常時割り当てる」
   - **CPU ブースト**: **「CPU ブーストを有効にする」にチェック** (起動時間が大幅に短縮されます)
   - **環境変数とシークレット**:
     - **[変数を出力 / 環境変数を追加]**: `PORT=8080` や `ASPNETCORE_URLS=http://+:8080`
     - **[シークレットをリファレンス]**: Secret Manager に登録したシークレットを選択し、環境変数またはファイルとしてコンテナにマウントします。

2. **[ボリューム] タブ (GCS 永続化マウント)**:
   - **[ボリュームを追加]** をクリックし、タイプで **[Cloud Storage バケット]** を選択します。
     - ボリューム名: `db-volume`
     - バケット名: `YOUR_PROJECT_ID-db-bucket`
   - **[コンテナ] タブに戻り、[ボリュームのマウント]** を設定:
     - マウントするボリューム: `db-volume`
     - マウントパス: `/app/data`

3. **[作成]** ボタンを押して設定を保存・完了します。

---

## 5. GitHub Actions による CI/CD ワークフロー設定

コード更新時に自動で Docker ビルドし、Cloud Run へデプロイするワークフローを作成します。

リポジトリ直下に `.github/workflows/deploy.yml` を作成・配置します。

```yaml
name: Build and Deploy to Cloud Run

on:
  push:
    branches:
      - main

env:
  REGION: 'asia-northeast1'
  REPOSITORY: 'habit-tracker-repo'
  SERVICE: 'habit-tracker-service'
  GCS_BUCKET: 'YOUR_PROJECT_ID-db-bucket'

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
          workload_identity_provider: ${{ secrets.GCP_WORKLOAD_IDENTITY_PROVIDER }}
          service_account: ${{ secrets.GCP_SERVICE_ACCOUNT }}

      - name: Set up Cloud SDK
        uses: google-github-actions/setup-gcloud@v2

      - name: Authorize Docker Push to Artifact Registry
        run: |
          gcloud auth configure-docker ${{ env.REGION }}-docker.pkg.dev --quiet

      - name: Build and Push Docker Image
        run: |
          IMAGE_TAG="${{ env.REGION }}-docker.pkg.dev/${{ secrets.GCP_PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:${{ github.sha }}"
          LATEST_TAG="${{ env.REGION }}-docker.pkg.dev/${{ secrets.GCP_PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:latest"

          docker build -t $IMAGE_TAG -t $LATEST_TAG .
          docker push $IMAGE_TAG
          docker push $LATEST_TAG

      - name: Deploy to Cloud Run
        run: |
          gcloud run deploy ${{ env.SERVICE }} \
            --image=${{ env.REGION }}-docker.pkg.dev/${{ secrets.GCP_PROJECT_ID }}/${{ env.REPOSITORY }}/${{ env.SERVICE }}:${{ github.sha }} \
            --region=${{ env.REGION }} \
            --platform=managed \
            --allow-unauthenticated \
            --port=8080 \
            --max-instances=1 \
            --cpu-boost \
            --add-volume=name=db-volume,type=cloud-storage,bucket=${{ secrets.GCP_PROJECT_ID }}-db-bucket \
            --mount-volume=volume=db-volume,mount-path=/app/data
```

---

## 6. 運用のポイント・注意事項

1. **GUI と GitHub Actions の併用について**
   - Cloud Run のサービス基本設定（マウントパスや環境変数、インスタンス数の制限など）は一度 GUI または `gcloud run deploy` コマンドで構成しておくと、以後の GitHub Actions からの修正・更新も安全に引き継がれます。
2. **SQLite と GCS Volume Mount (gcsfuse)**
   - GCS バケットをマウントして利用するため、ネットワーク経由でのファイル操作となります。データ破損やデータ不整合を抑止するため、`最大インスタンス数 = 1` の設定は必須です。
3. **起動時のパフォーマンス（Cold Start 対策）**
   - Cloud Run の設定で **「CPU ブースト」** を有効にし、最小インスタンス数を `1` に維持（必要に応じて）することで Cold Start 遅延を低減できます。
