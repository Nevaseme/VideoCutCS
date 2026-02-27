# VideoCutCS

WinUI 3 製の軽量動画カットツールです。FFmpeg を内部で使用し、再エンコードなしの高速カット・スマートカット・スナップショット保存などに対応しています。

## ダウンロード

**[最新版はこちら → Releases](https://github.com/Nevaseme/VideoCutCS/releases/latest)**

ZIPを解凍して `VideoCutCS.exe` を実行するだけで使えます。.NETランタイム・Windows App SDK は同梱済みのため、別途インストール不要です。

> **注意:** Visual C++ 再頒布可能パッケージが必要ですが、Windows 10/11 では通常インストール済みです。

## 動作環境

| 項目 | 要件 |
|---|---|
| OS | Windows 10 (1809以降) / Windows 11 |
| アーキテクチャ | x64 |
| ランタイム | 同梱済み（別途インストール不要） |

## 機能

### カット
| 機能 | 説明 |
|---|---|
| **高速カット** | 再エンコードなし。フレーム単位の誤差はあるが瞬時に完了 |
| **スマートカット** | 開始点のみ再エンコードし、フレーム精度で正確にカット |
| **キーフレーム吸着** | 開始・終了をキーフレームに自動スナップ（高速カットと組み合わせて誤差ゼロ） |
| **バッチカット** | 複数セグメントを登録して一括カット＆結合 |

### 再生・操作
| 機能 | 説明 |
|---|---|
| タイムライン | ズームイン/アウト対応。ドラッグでシーク |
| キーフレームジャンプ | 前後のキーフレームへ瞬時に移動 |
| 再生速度変更 | 0.1x ～ 10.0x |
| スナップショット | 現在フレームをPNGで保存 |
| D&Dで読み込み | ファイルをウィンドウにドロップして開く |

### 出力形式
- MP4 / MPEG-TS / MOV（カット）
- PNG（スナップショット）

## キーボードショートカット

| キー | 動作 |
|---|---|
| `Space` | 再生 / 一時停止 |
| `←` / `→` | 5秒シーク |
| `Ctrl + ←` / `Ctrl + →` | 1秒シーク |
| `Alt + ←` / `Alt + →` | 前後のキーフレームへジャンプ |
| `I` | 開始点を現在位置にセット |
| `F` | 終了点を現在位置にセット |
| `S` | スナップショット保存 |
| `Delete` | 選択中のセグメントを削除 |
| `Shift + ホイール` | タイムラインズーム |

## スクリーンショット

> *(準備中)*

## ビルド方法（開発者向け）

### 必要なもの
- Visual Studio 2022 または .NET 8 SDK
- Windows App SDK
- `Executables/` フォルダに `ffmpeg.exe` と `ffprobe.exe` を配置

```bash
git clone https://github.com/Nevaseme/VideoCutCS.git
cd VideoCutCS

# ffmpeg を Executables/ に手動配置後
dotnet build
dotnet publish -c Release -r win-x64 --self-contained -o publish/
```

### テスト実行

```bash
dotnet test VideoCutCS.Tests/
```

## ライセンス

本ソフトウェアに同梱の FFmpeg は [LGPL v2.1](https://ffmpeg.org/legal.html) のもとで配布されています。