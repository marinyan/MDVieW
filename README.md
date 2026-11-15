<img src="src/MDVieW/Assets/AppIcon.png" width="72" height="72" alt="MDVieW">

# MDVieW

**Windows ネイティブの Markdown / WYSIWYG エディタ。**

C# / .NET 10 / WPF で実装しています。テキスト編集と、仕上がりを見ながらの直接編集を同時に使えます。

## 使い方

配布フォルダ内の `MDVieW.exe` をダブルクリックして起動します。インストーラーや管理者権限は不要です。

- **複数ファイル**: 「開く」で複数選択、ファイルをウィンドウにドロップ、または「＋」で新しいタブを作成。
- **ファイルのドラッグ＆ドロップ**: テキスト・WYSIWYG・タブバーの上にもドロップできます。複数ファイルはそれぞれタブで開き、開いているファイルは既存のタブに切り替えます。編集中の内容と元ファイルは保持します。フォルダーは対象外です。
- **双方向編集**: Markdown と WYSIWYG のどちらで編集しても、もう一方に反映。
- **左右・上下の配置**: 右上の「左右」「上下」で切り替えます。それぞれの分割比率を保持します。
- **幅・高さの変更**: 中央の境界線をドラッグ。ダブルクリックで 50 : 50 に戻ります。境界線にフォーカスして矢印キーでも調整できます。
- **位置の入れ替え**: 「MARKDOWN」または「WYSIWYG」の見出しをつかみ、反対側の領域へドラッグ＆ドロップ。左右・上下のどちらでも使えます。
- **書式パレットの縦横**: パレットの「縦置き」「横置き」ボタン、または「表示 → 書式パレット」で、上部の横置き・左側の縦置きを切り替えます。
- **タブの切り替え**: 各タブのカーソル位置、スクロール位置、編集履歴を保持。変更のあるタブには ● を表示。
- **保存**: 「保存」「名前を付けて保存」「すべて保存」に対応。変更のあるタブを閉じるときや終了するときに確認します。
- **表示**: 左右同時表示・テキストのみ・WYSIWYG のみを切り替えられます。

ファイルを実行ファイルへ渡して開くこともできます。

```powershell
.\MDVieW.exe .\README.md .\notes.md
```

## ショートカット

| 操作 | キー |
| --- | --- |
| 新しいタブ | Ctrl+N |
| 開く（複数選択可） | Ctrl+O |
| 保存 | Ctrl+S |
| 名前を付けて保存 | Ctrl+Shift+S |
| すべて保存 | Ctrl+Alt+S |
| タブを閉じる | Ctrl+W / タブを中クリック |
| 次 / 前のタブ | Ctrl+Tab / Ctrl+Shift+Tab |
| 検索 | Ctrl+F |
| 太字 / 斜体 | Ctrl+B / Ctrl+I |
| 元に戻す / やり直し | Ctrl+Z / Ctrl+Y |
| 拡大 / 縮小 / リセット | Ctrl++ / Ctrl+- / Ctrl+0 |

## Markdown 対応範囲

見出し、太字、斜体、取り消し線、インラインコード、コードブロック、引用、箇条書き、番号付きリスト、タスクリスト、リンク、水平線、パイプ形式の表に対応します。チェックボックスは WYSIWYG 側で直接切り替えられます。

- テキスト側だけの編集では、Markdown の記法を変換せず保存します。
- WYSIWYG 側で変更したブロックは Markdown に変換します。変更していないブロックの元の記法は保持します。
- HTML や未対応のブロックは、元のテキストとして表示・保持します。任意の HTML / JavaScript は実行しません。
- 相対パスのローカル画像を表示できます。リモート画像はリンク先と代替テキストのプレースホルダーとして表示し、自動取得しません。
- 読み書きは **UTF-8**。既存ファイルの BOM と LF / CRLF を保持します。他の文字コードは UTF-8 に変換してから開いてください。
- WYSIWYG への貼り付けはプレーンテキストです。Markdown で表せない装飾を持ち込みません。
- 編集履歴はタブ・ペインごとです。別ペインから内容を同期すると、同期先の履歴はリセットされます。

## 開発

Windows と **.NET 10 SDK** が必要です。Visual Studio は任意です。

```powershell
dotnet build MDVieW.slnx -c Release -m:1
dotnet run --project src/MDVieW
```

変換・ファイル保存・タブ・同期のチェック:

```powershell
dotnet run --project tests/MDVieW.SmokeTests -c Release
```

配布版（.NET 同梱、Windows x64）を作成:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

`artifacts/MDVieW-win-x64/MDVieW.exe` と ZIP を生成します。配布版は .NET の事前インストールが不要です。ARM64 向けは `-Runtime win-arm64`、.NET Desktop Runtime を別途使う小さい配布版は `-FrameworkDependent` を指定してください。

## 構成

- `src/MDVieW`: WPF アプリ、タブとペイン、Markdown 変換、ファイル入出力
- `tests/MDVieW.SmokeTests`: STA スレッド上で動くネイティブコントロールのチェック
- `scripts/publish.ps1`: 配布 EXE / ZIP の生成
- `src/MDVieW/Assets`: 共通アイコン（SVG 原稿、PNG、16〜256px の複数サイズ入り ICO）
- `scripts/generate-icon.ps1`: SVG 原稿から Windows 用アイコンを再生成（Windows / STA モード）
- `.github/workflows/build.yml`: Windows でのビルド・チェック・配布版生成

Markdown 解析には [Markdig](https://github.com/xoofx/markdig) を使用しています。ライセンスは `THIRD-PARTY-NOTICES.txt` を参照してください。
