## 📂 追加・変更するファイル（構成は変わらずシンプル）
ファイル数は増やさず、Program.cs への追記と、ログイン画面用の .razor コンポーネントを1つ追加するだけで対応できます。

📁 MyHonoApp/
 ├── 📄 MyHonoApp.csproj
 ├── 📄 Program.cs               (認証ロジック、ログインAPI、認証制限を追加)
 ├── 📄 TimelineItemsView.razor
 ├── 📄 LoginView.razor          (★追加：シンプルなログイン画面)
 └── 📄 Dockerfile

------------------------------
## 1. 📄 Program.cs の変更点（Cookie認証の追加）
数行のコードを追加するだけで、セッション維持のための安全な暗号化Cookieが自動発行されるようになります。

using Microsoft.AspNetCore.Authentication.Cookies;using Microsoft.Data.Sqlite;using Dapper;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
// 💡 1. .NET標準のCookie認証サービスを追加
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login"; // 未認証の場合に飛ばすURL
        options.ExpireTimeSpan = TimeSpan.FromDays(7); // ログイン有効期限
    });
builder.Services.AddAuthorization(); // 認可機能
var app = builder.Build();
// 💡 2. 認証ミドルウェアを有効化（順番が重要）
app.UseAuthentication();
app.UseAuthorization();
// --- 🔐 認証付きのAPI（タイムライン表示や投稿） ---
// 💡 RequireAuthorization() をつけるだけで、未ログインなら自動で /login に弾く
app.MapGet("/", () => Results.Content("...ベースHTML...", "text/html"))
   .RequireAuthorization();

app.MapGet("/timeline-items", async (int page, HttpContext context) => {
    // 💡 ログインしているユーザー名をC#側で簡単に取得できる
    string currentUser = context.User.Identity?.Name ?? "不明";
    // ...データ取得処理...
}).RequireAuthorization();

app.MapPost("/submit", async (HttpContext context) => {
    string currentUser = context.User.Identity?.Name ?? "不明";
    // 💡 フォームの「User」入力を廃止し、ログイン中の名前で強制固定して安全にDB登録
}).RequireAuthorization();

// --- 🔓 誰でもアクセスできるAPI（ログイン処理） ---
// ログイン画面の表示
app.MapGet("/login", () => Results.Extensions.RazorComponent<LoginView>());
// ログインの実行（POST）
app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    string username = form["Username"].ToString();
    string password = form["Password"].ToString();

    // 💡 パスワード検証（社内ツール等なら簡易チェック、本格運用ならハッシュ化DB照合）
    if (username == "admin" && password == "secret-password")
    {
        // ログイン成功：ユーザー情報をCookieに焼き付ける
        var claims = new List<System.Security.Claims.Claim> { new(System.Security.Claims.ClaimTypes.Name, username) };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(new System.Security.Claims.ClaimsPrincipal(identity));

        return Results.Redirect("/");
    }

    // 失敗したらエラーメッセージ付きで再表示
    return Results.Extensions.RazorComponent<LoginView>(new { ErrorMessage = "ユーザー名またはパスワードが違います。" });
});
// ログアウト
app.MapGet("/logout", async (HttpContext context) => {
    await context.SignOutAsync();
    return Results.Redirect("/login");
});

------------------------------
## 2. 📄 LoginView.razor (ログイン画面コンポーネント)
タイムラインと同様、Pico.cssを使ってシンプルに仕上げます。

@code {
    [Parameter] public string? ErrorMessage { get; set; }
}
<!DOCTYPE html>
<html lang="ja">
<head>
    <meta charset="UTF-8">
    <title>ログイン</title>
    <link rel="stylesheet" href="https://jsdelivr.net">
</head>
<body class="container" style="max-width: 400px; padding-top: 5rem;">
    <article>
        <h2>ログイン</h2>
        @if (!string.IsNullOrEmpty(ErrorMessage))
        {
            <p style="color: red;"><small>@ErrorMessage</small></p>
        }
        <form action="/login" method="post">
            <label>ユーザー名: <input type="text" name="Username" required></label>
            <label>パスワード: <input type="password" name="Password" required></label>
            <button type="submit">ログイン</button>
        </form>
    </article>
</body>
</html>

------------------------------
## 💡 なぜ「Cookie認証」がこの構成で最強なのか？

   1. Cloud Run（ステートレス）と完璧にマッチする
   .NETのCookie認証は、ユーザー情報を暗号化してCookie（ブラウザ側）に持たせます。サーバー側はセッション記憶などの「状態（ステート）」を持たずに毎回Cookieを復号して検証するだけなので、Cloud Runのインスタンスが急に消えたり増えたりしても、ユーザーが勝手にログアウトされることはありません。
   2. データベース（SQLite）の負荷が増えない
   リクエストのたびに「このセッションIDは有効か？」をDBに問い合わせに行く必要がありません。暗号の検証だけで認証が完了するため、SQLiteへの読み書き回数を減らし、動作を圧倒的に軽く保てます。
   3. セキュリティの担保
   .NETが裏側で自動的にCookieの暗号化キーを管理（データ保護API）してくれるため、開発者が自分で複雑なセキュリティコードを書く必要がなく、安全です。
