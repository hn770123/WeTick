using System.Collections.Generic;
using System.IO;

namespace HabitTracker.Views
{
    /// <summary>
    /// HTMLビューファイルの読み込みおよびテンプレート変数の置換を行うレンダラークラスです。
    /// </summary>
    public static class ViewRenderer
    {
        /// <summary>
        /// 指定されたビューファイルを読み込み、プレースホルダーを指定された値で置換したHTML文字列を返します。
        /// </summary>
        /// <param name="viewPath">Views フォルダ配下のビューファイル相対パス（例: "login.html"）</param>
        /// <param name="replacements">置換用キーワードと値のディクショナリ（例: "{{errorHtml}}" -> "<p>...</p>"）</param>
        /// <returns>レンダリングされたHTML文字列</returns>
        public static string Render(string viewPath, Dictionary<string, string>? replacements = null)
        {
            string fullPath = Path.Combine("Views", viewPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"指定されたビューファイルが見つかりません: {fullPath}");
            }

            string content = File.ReadAllText(fullPath);

            if (replacements != null)
            {
                foreach (var kvp in replacements)
                {
                    content = content.Replace(kvp.Key, kvp.Value);
                }
            }

            return content;
        }
    }
}
