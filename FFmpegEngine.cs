using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace VideoCutCS
{
    public class FFmpegEngine
    {
        private readonly string _ffmpegPath;

        public FFmpegEngine()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _ffmpegPath = Path.Combine(baseDir, "Executables", "ffmpeg.exe");
        }

        public async Task<string> GetFFmpegVersionAsync()
        {
            if (!File.Exists(_ffmpegPath)) return "エラー: FFmpegが見つかりません。";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return output;
                }
            }
            catch (Exception ex) { return $"実行時エラー: {ex.Message}"; }
        }

        public async Task<string> CutVideoAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
        {
            if (!File.Exists(_ffmpegPath)) return "エラー: FFmpegが見つかりません。";
            if (!File.Exists(inputPath)) return $"エラー: 入力ファイルが見つかりません: {inputPath}";

            string arguments = $"-ss {start} -i \"{inputPath}\" -to {end} -c copy -map 0 -y \"{outputPath}\"";

            return await RunFFmpegAsync(arguments);
        }

        // ★修正: 画像をPNG形式で保存するメソッドに変更
        public async Task<string> SaveSnapshotAsync(string inputPath, string outputPath, TimeSpan position)
        {
            if (!File.Exists(_ffmpegPath)) return "エラー: FFmpegが見つかりません。";
            if (!File.Exists(inputPath)) return $"エラー: 入力ファイルが見つかりません: {inputPath}";

            // -frames:v 1 : 1フレームだけ出力
            // -c:v png   : PNG形式でエンコード
            string arguments = $"-ss {position} -i \"{inputPath}\" -frames:v 1 -c:v png -y \"{outputPath}\"";

            return await RunFFmpegAsync(arguments);
        }

        private async Task<string> RunFFmpegAsync(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    string output = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return $"完了 (ExitCode: {process.ExitCode})\n{output}";
                }
            }
            catch (Exception ex)
            {
                return $"実行時エラー: {ex.Message}";
            }
        }
    }
}