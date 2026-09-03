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
