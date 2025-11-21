using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;

namespace VideoCutCS
{
	public class FFmpegEngine
	{
		private readonly string _ffmpegPath;
		private readonly string _ffprobePath;

		public FFmpegEngine()
		{
			// ★重要変更: 単一EXE化した場合、AppDomain...BaseDirectoryは一時フォルダを指すことがある。
			// 実際にEXEがある場所を取得するには Environment.ProcessPath からディレクトリを取る。
			string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

			// .csprojの設定で直下にコピーされるようにしたため、フォルダ指定なしで結合
			_ffmpegPath = Path.Combine(baseDir, "ffmpeg.exe");
			_ffprobePath = Path.Combine(baseDir, "ffprobe.exe");
		}

		// =========================================================
		// 以下、ロジックに変更はありません (そのまま使用)
		// =========================================================

		public async Task<string> GetFFmpegVersionAsync()
		{
			if (!File.Exists(_ffmpegPath)) return "エラー: FFmpegが見つかりません。";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, "-version");
		}

		public async Task<string> SaveSnapshotAsync(string inputPath, string outputPath, TimeSpan position)
		{
			string args = $"-ss {position} -i \"{inputPath}\" -frames:v 1 -c:v png -y \"{outputPath}\"";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, args);
		}

		public async Task<string> CutVideoSimpleAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
		{
			string args = $"-ss {start} -i \"{inputPath}\" -to {end} -c copy -map 0 -y \"{outputPath}\"";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, args);
		}

		public async Task<VideoInfo> GetVideoInfoAsync(string inputPath)
		{
			var info = new VideoInfo();
			if (!File.Exists(_ffprobePath)) return info;

			string args = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,codec_name,bit_rate -of default=noprint_wrappers=1:nokey=0 \"{inputPath}\"";
			string output = await ExecuteFFmpegCommandAsync(_ffprobePath, args);

			ParseVideoInfo(output, info);
			return info;
		}

		public async Task<List<TimeSpan>> GetKeyframesAsync(string inputPath)
		{
			var keyframes = new List<TimeSpan>();
			if (!File.Exists(_ffprobePath)) return keyframes;

			string args = $"-v error -hide_banner -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{inputPath}\"";
			var startInfo = CreateStartInfo(_ffprobePath, args);

			using (var process = new Process { StartInfo = startInfo })
			{
				process.Start();
				var errorTask = process.StandardError.ReadToEndAsync();

				string? line;
				while ((line = await process.StandardOutput.ReadLineAsync()) != null)
				{
					if (string.IsNullOrWhiteSpace(line)) continue;
					var parts = line.Split(',');
					if (parts.Length >= 2 && parts[1].Contains("K"))
					{
						if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double sec))
						{
							keyframes.Add(TimeSpan.FromSeconds(sec));
						}
					}
				}
				await Task.WhenAll(errorTask, process.WaitForExitAsync());
			}
			return keyframes.OrderBy(x => x).ToList();
		}

		public async Task<string> SmartCutVideoAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end, List<TimeSpan> keyframes)
		{
			var nextKeyframe = keyframes.FirstOrDefault(k => k > start);
			bool isSimpleCut = (nextKeyframe == TimeSpan.Zero) || (nextKeyframe >= end) || ((nextKeyframe - start).TotalSeconds < 0.2);

			if (isSimpleCut)
			{
				Debug.WriteLine("[SmartCut] 再エンコード不要と判断し、通常カットを実行します。");
				return await CutVideoSimpleAsync(inputPath, outputPath, start, end);
			}
			return await ExecuteSmartCutInternalAsync(inputPath, outputPath, start, end, nextKeyframe);
		}

		private async Task<string> ExecuteSmartCutInternalAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end, TimeSpan splitPoint)
		{
			Debug.WriteLine($"[SmartCut] 処理開始: Start={start} -> Split={splitPoint} -> End={end}");
			string tempDir = Path.GetDirectoryName(outputPath) ?? "";
			string id = Guid.NewGuid().ToString("N").Substring(0, 8);
			string tempPartA = Path.Combine(tempDir, $"temp_A_{id}.mp4");
			string tempPartB = Path.Combine(tempDir, $"temp_B_{id}.mp4");
			string tempList = Path.Combine(tempDir, $"temp_list_{id}.txt");

			try
			{
				var info = await GetVideoInfoAsync(inputPath);
				string argsA = $"-ss {start} -to {splitPoint} -i \"{inputPath}\" " +
							   $"-c:v libx264 -preset fast -crf 23 -video_track_timescale 90000 " +
							   (info.IsValid ? $"-b:v {info.BitRate} " : "") +
							   $"-c:a copy -y \"{tempPartA}\"";
				string argsB = $"-ss {splitPoint} -to {end} -i \"{inputPath}\" " +
							   $"-c copy -video_track_timescale 90000 -y \"{tempPartB}\"";

				var taskA = ExecuteFFmpegCommandAsync(_ffmpegPath, argsA);
				var taskB = ExecuteFFmpegCommandAsync(_ffmpegPath, argsB);
				await Task.WhenAll(taskA, taskB);

				string listContent = $"file '{Path.GetFileName(tempPartA)}'\nfile '{Path.GetFileName(tempPartB)}'";
				await File.WriteAllTextAsync(tempList, listContent);

				string argsConcat = $"-f concat -safe 0 -i \"{tempList}\" -c copy -y \"{outputPath}\"";
				await ExecuteFFmpegCommandAsync(_ffmpegPath, argsConcat, workingDirectory: tempDir);

				return "スマートカット完了";
			}
			catch (Exception ex)
			{
				return $"スマートカット エラー: {ex.Message}";
			}
			finally
			{
				DeleteFile(tempPartA);
				DeleteFile(tempPartB);
				DeleteFile(tempList);
			}
		}

		private async Task<string> ExecuteFFmpegCommandAsync(string exePath, string args, string? workingDirectory = null)
		{
			var startInfo = CreateStartInfo(exePath, args, workingDirectory);
			using (var p = new Process { StartInfo = startInfo })
			{
				p.Start();
				var stdoutTask = p.StandardOutput.ReadToEndAsync();
				var stderrTask = p.StandardError.ReadToEndAsync();
				await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync());
				return await stderrTask;
			}
		}

		private ProcessStartInfo CreateStartInfo(string fileName, string args, string? workingDirectory = null)
		{
			return new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = args,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = workingDirectory ?? ""
			};
		}

		private void ParseVideoInfo(string rawOutput, VideoInfo info)
		{
			foreach (var line in rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Split('=');
				if (parts.Length != 2) continue;
				string val = parts[1].Trim();
				switch (parts[0].Trim())
				{
					case "width": info.Width = int.Parse(val); break;
					case "height": info.Height = int.Parse(val); break;
					case "codec_name": info.VideoCodec = val; break;
					case "bit_rate": info.BitRate = long.Parse(val); break;
					case "r_frame_rate":
						var frParts = val.Split('/');
						if (frParts.Length == 2 && double.TryParse(frParts[0], out double n) && double.TryParse(frParts[1], out double d) && d != 0)
							info.FrameRate = n / d;
						break;
				}
			}
		}

		private void DeleteFile(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}
	}
}