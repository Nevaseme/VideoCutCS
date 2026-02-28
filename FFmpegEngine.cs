using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;

namespace VideoCutCS
{
	public class FFmpegEngine
	{
		private readonly string _ffmpegPath;
		private readonly string _ffprobePath;

		// ハードウェアエンコーダー検出キャッシュ
		private string? _cachedHwEncoder;
		private bool _hwEncoderDetected;

		public FFmpegEngine()
		{
			// ★重要変更: 単一EXE化した場合、AppDomain...BaseDirectoryは一時フォルダを指すことがある。
			// 実際にEXEがある場所を取得するには Environment.ProcessPath からディレクトリを取る。
			string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

			// .csprojの設定で直下にコピーされるようにしたため、フォルダ指定なしで結合
			_ffmpegPath = Path.Combine(baseDir, "ffmpeg.exe");
			_ffprobePath = Path.Combine(baseDir, "ffprobe.exe");
		}

		public async Task<string> GetFFmpegVersionAsync()
		{
			if (!File.Exists(_ffmpegPath)) return "エラー: FFmpegが見つかりません。";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, "-version").ConfigureAwait(false);
		}

		public async Task<string> SaveSnapshotAsync(string inputPath, string outputPath, TimeSpan position)
		{
			string args = $"-ss {position} -i \"{inputPath}\" -frames:v 1 -c:v png -y \"{outputPath}\"";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, args).ConfigureAwait(false);
		}

		public async Task<string> CutVideoSimpleAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
		{
			var duration = end - start;
			string args = $"-ss {start} -i \"{inputPath}\" -t {duration} -c copy -map 0 -avoid_negative_ts make_zero -y \"{outputPath}\"";
			return await ExecuteFFmpegCommandAsync(_ffmpegPath, args).ConfigureAwait(false);
		}

		/// <summary>
		/// 複数セグメントをカットして1ファイルに結合する。
		/// </summary>
		public async Task<string> BatchCutAndMergeAsync(
			string inputPath,
			string outputPath,
			IReadOnlyList<VideoSegment> segments,
			IProgress<(int current, int total)>? progress = null,
			CancellationToken cancellationToken = default)
		{
			if (segments.Count == 0) return "エラー: セグメントが空です。";

			// 1セグメントの場合は通常カットを実行
			if (segments.Count == 1)
			{
				progress?.Report((1, 1));
				return await CutVideoSimpleAsync(inputPath, outputPath, segments[0].Start, segments[0].End).ConfigureAwait(false);
			}

			string tempDir = Path.GetDirectoryName(outputPath) ?? "";
			string id = Guid.NewGuid().ToString("N")[..8];
			var tempFiles = new List<string>();
			string tempList = Path.Combine(tempDir, $"temp_batchlist_{id}.txt");

			try
			{
				int total = segments.Count + 1; // +1 は結合ステップ分

				for (int i = 0; i < segments.Count; i++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string tempFile = Path.Combine(tempDir, $"temp_bseg{i}_{id}.mp4");
					tempFiles.Add(tempFile);
					await CutVideoSimpleAsync(inputPath, tempFile, segments[i].Start, segments[i].End).ConfigureAwait(false);
					progress?.Report((i + 1, total));
				}

				cancellationToken.ThrowIfCancellationRequested();
				string listContent = string.Join("\n", tempFiles.Select(f => $"file '{Path.GetFileName(f)}'"));
				await File.WriteAllTextAsync(tempList, listContent, cancellationToken).ConfigureAwait(false);

				string argsConcat = $"-f concat -safe 0 -i \"{tempList}\" -c copy -y \"{outputPath}\"";
				progress?.Report((total, total));
				await ExecuteFFmpegCommandAsync(_ffmpegPath, argsConcat, workingDirectory: tempDir).ConfigureAwait(false);

				return $"バッチカット完了 ({segments.Count} セグメント)";
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				return $"バッチカット エラー: {ex.Message}";
			}
			finally
			{
				foreach (var f in tempFiles) DeleteFile(f);
				DeleteFile(tempList);
			}
		}

		public async Task<VideoInfo> GetVideoInfoAsync(string inputPath)
		{
			var info = new VideoInfo();
			if (!File.Exists(_ffprobePath)) return info;

			string args = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,codec_name,bit_rate -of default=noprint_wrappers=1:nokey=0 \"{inputPath}\"";
			string output = await ExecuteFFmpegCommandAsync(_ffprobePath, args).ConfigureAwait(false);

			ParseVideoInfo(output, info);
			return info;
		}

		public async Task<List<TimeSpan>> GetKeyframesAsync(string inputPath, CancellationToken cancellationToken = default)
		{
			var keyframes = new List<TimeSpan>(4096);
			if (!File.Exists(_ffprobePath)) return keyframes;

			string args = $"-v error -hide_banner -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{inputPath}\"";
			var startInfo = CreateStartInfo(_ffprobePath, args);

			using (var process = new Process { StartInfo = startInfo })
			{
				process.Start();
				try
				{
					var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

					string? line;
					while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
					{
						if (line.Length == 0) continue;
						if (TryParseKeyframeLine(line, out double sec))
						{
							keyframes.Add(TimeSpan.FromSeconds(sec));
						}
					}
					await Task.WhenAll(errorTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					process.Kill(entireProcessTree: true);
					throw;
				}
			}
			keyframes.Sort();
			return keyframes;
		}

		/// <summary>
		/// 利用可能なハードウェアエンコーダーを検出して返す。見つからなければ null。
		/// 結果はキャッシュされるため2回目以降は即座に返る。
		/// </summary>
		public async Task<string?> DetectHardwareEncoderAsync()
		{
			if (_hwEncoderDetected) return _cachedHwEncoder;
			if (!File.Exists(_ffmpegPath)) { _hwEncoderDetected = true; return null; }

			string output = await ExecuteCommandGetOutputAsync(_ffmpegPath, "-hide_banner -encoders").ConfigureAwait(false);
			string[] candidates = ["h264_nvenc", "h264_qsv", "h264_amf"];
			_cachedHwEncoder = candidates.FirstOrDefault(e => output.Contains(e, StringComparison.OrdinalIgnoreCase));
			_hwEncoderDetected = true;
			return _cachedHwEncoder;
		}

		public async Task<string> SmartCutVideoAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end, List<TimeSpan> keyframes)
		{
			// --- 開始点: start の直後のキーフレームを検索 ---
			int sIdx = keyframes.BinarySearch(start);
			if (sIdx >= 0) sIdx++;
			else sIdx = ~sIdx;
			var startSplit = sIdx < keyframes.Count ? keyframes[sIdx] : TimeSpan.Zero;
			bool reencStart = startSplit != TimeSpan.Zero && startSplit < end && (startSplit - start).TotalSeconds >= 0.2;

			// --- 終了点: end の直前のキーフレームを検索 ---
			int eIdx = keyframes.BinarySearch(end);
			TimeSpan endSplit;
			if (eIdx >= 0)
				endSplit = TimeSpan.Zero; // end がキーフレーム上 → ストリームコピーで精密カット可能
			else
			{
				int prev = ~eIdx - 1;
				endSplit = prev >= 0 ? keyframes[prev] : TimeSpan.Zero;
			}
			bool reencEnd = endSplit != TimeSpan.Zero && endSplit > start && (end - endSplit).TotalSeconds >= 0.2;

			// 両端とも再エンコード不要 → 高速カット
			if (!reencStart && !reencEnd)
			{
				Debug.WriteLine("[SmartCut] 再エンコード不要と判断し、通常カットを実行します。");
				return await CutVideoSimpleAsync(inputPath, outputPath, start, end).ConfigureAwait(false);
			}

			// 中間ストリームコピー区間がない（両端の再エンコード区間が重複） → 全区間再エンコード
			if (reencStart && reencEnd && startSplit >= endSplit)
			{
				Debug.WriteLine("[SmartCut] 中間区間なし。全区間を再エンコードします。");
				return await ReencodeRangeAsync(inputPath, outputPath, start, end).ConfigureAwait(false);
			}

			return await ExecuteSmartCutInternalAsync(inputPath, outputPath, start, end,
				reencStart ? startSplit : null, reencEnd ? endSplit : null).ConfigureAwait(false);
		}

		/// <summary>全区間を再エンコードする（中間ストリームコピー区間が存在しない場合）。</summary>
		private async Task<string> ReencodeRangeAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan end)
		{
			var info = await GetVideoInfoAsync(inputPath).ConfigureAwait(false);
			string args = $"-ss {start} -to {end} -i \"{inputPath}\" {BuildVideoEncoderArgs(info)}-c:a copy -y \"{outputPath}\"";
			await ExecuteFFmpegCommandAsync(_ffmpegPath, args).ConfigureAwait(false);
			return "スマートカット完了（全区間再エンコード）";
		}

		/// <summary>
		/// 最大3パートに分割してスマートカットを実行する。
		/// startSplit != null → 開始側を再エンコード (start → startSplit)
		/// endSplit   != null → 終了側を再エンコード (endSplit → end)
		/// 中間はストリームコピー。
		/// </summary>
		private async Task<string> ExecuteSmartCutInternalAsync(
			string inputPath, string outputPath,
			TimeSpan start, TimeSpan end,
			TimeSpan? startSplit, TimeSpan? endSplit)
		{
			Debug.WriteLine($"[SmartCut] 処理開始: Start={start} | StartSplit={startSplit} | EndSplit={endSplit} | End={end}");
			string tempDir = Path.GetDirectoryName(outputPath) ?? "";
			string id = Guid.NewGuid().ToString("N")[..8];
			var tempFiles = new List<string>();
			string tempList = Path.Combine(tempDir, $"temp_list_{id}.txt");

			try
			{
				var info = await GetVideoInfoAsync(inputPath).ConfigureAwait(false);
				string enc = BuildVideoEncoderArgs(info);
				var tasks = new List<Task>();
				int partIdx = 0;

				// --- 開始側: 再エンコード ---
				TimeSpan midStart = startSplit ?? start;
				if (startSplit.HasValue)
				{
					string f = Path.Combine(tempDir, $"temp_{partIdx++}_{id}.mp4");
					tempFiles.Add(f);
					tasks.Add(ExecuteFFmpegCommandAsync(_ffmpegPath,
						$"-ss {start} -to {startSplit.Value} -i \"{inputPath}\" {enc}-c:a copy -y \"{f}\""));
				}

				// --- 中間: ストリームコピー ---
				TimeSpan midEnd = endSplit ?? end;
				{
					string f = Path.Combine(tempDir, $"temp_{partIdx++}_{id}.mp4");
					tempFiles.Add(f);
					tasks.Add(ExecuteFFmpegCommandAsync(_ffmpegPath,
						$"-ss {midStart} -to {midEnd} -i \"{inputPath}\" -c copy -video_track_timescale 90000 -y \"{f}\""));
				}

				// --- 終了側: 再エンコード ---
				if (endSplit.HasValue)
				{
					string f = Path.Combine(tempDir, $"temp_{partIdx++}_{id}.mp4");
					tempFiles.Add(f);
					tasks.Add(ExecuteFFmpegCommandAsync(_ffmpegPath,
						$"-ss {endSplit.Value} -to {end} -i \"{inputPath}\" {enc}-c:a copy -y \"{f}\""));
				}

				await Task.WhenAll(tasks).ConfigureAwait(false);

				// --- 結合 ---
				string listContent = string.Join("\n", tempFiles.Select(f => $"file '{Path.GetFileName(f)}'"));
				await File.WriteAllTextAsync(tempList, listContent).ConfigureAwait(false);

				await ExecuteFFmpegCommandAsync(_ffmpegPath,
					$"-f concat -safe 0 -i \"{tempList}\" -c copy -y \"{outputPath}\"",
					workingDirectory: tempDir).ConfigureAwait(false);

				return "スマートカット完了";
			}
			catch (Exception ex)
			{
				return $"スマートカット エラー: {ex.Message}";
			}
			finally
			{
				foreach (var f in tempFiles) DeleteFile(f);
				DeleteFile(tempList);
			}
		}

		/// <summary>
		/// 設定と検出済みエンコーダーに応じてビデオエンコーダー引数を組み立てる。
		/// </summary>
		private string BuildVideoEncoderArgs(VideoInfo info)
		{
			string bitrateArg = info.IsValid ? $"-b:v {info.BitRate} " : "";

			if (AppSettings.Current.UseHardwareAccel && _cachedHwEncoder != null)
			{
				return _cachedHwEncoder switch
				{
					"h264_nvenc" => $"-c:v h264_nvenc -preset p4 -rc vbr -cq:v 23 {bitrateArg}-video_track_timescale 90000 ",
					"h264_qsv"   => $"-c:v h264_qsv -global_quality 23 -preset medium {bitrateArg}-video_track_timescale 90000 ",
					"h264_amf"   => $"-c:v h264_amf -quality balanced -qp_i 23 -qp_p 23 {bitrateArg}-video_track_timescale 90000 ",
					_            => $"-c:v libx264 -preset fast -crf 23 {bitrateArg}-video_track_timescale 90000 "
				};
			}
			return $"-c:v libx264 -preset fast -crf 23 {bitrateArg}-video_track_timescale 90000 ";
		}

		private async Task<string> ExecuteFFmpegCommandAsync(string exePath, string args, string? workingDirectory = null)
		{
			var startInfo = CreateStartInfo(exePath, args, workingDirectory);
			using (var p = new Process { StartInfo = startInfo })
			{
				p.Start();
				var stdoutTask = p.StandardOutput.ReadToEndAsync();
				var stderrTask = p.StandardError.ReadToEndAsync();
				await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync()).ConfigureAwait(false);
				return stderrTask.Result;
			}
		}

		/// <summary>stdout と stderr を結合して返す。エンコーダー一覧取得など stdout が必要な場合に使用。</summary>
		private async Task<string> ExecuteCommandGetOutputAsync(string exePath, string args)
		{
			var startInfo = CreateStartInfo(exePath, args);
			using var p = new Process { StartInfo = startInfo };
			p.Start();
			var stdoutTask = p.StandardOutput.ReadToEndAsync();
			var stderrTask = p.StandardError.ReadToEndAsync();
			await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync()).ConfigureAwait(false);
			return stdoutTask.Result + stderrTask.Result;
		}

		private static bool TryParseKeyframeLine(string line, out double seconds)
		{
			seconds = 0;
			ReadOnlySpan<char> span = line.AsSpan();
			int comma = span.IndexOf(',');
			if (comma <= 0 || comma >= span.Length - 1) return false;
			if (!span.Slice(comma + 1).Contains('K')) return false;
			return double.TryParse(span.Slice(0, comma), NumberStyles.Any, CultureInfo.InvariantCulture, out seconds);
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

		internal void ParseVideoInfo(string rawOutput, VideoInfo info)
		{
			foreach (var line in rawOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Split('=', StringSplitOptions.TrimEntries);
				if (parts.Length != 2) continue;
				string val = parts[1];
				switch (parts[0])
				{
					case "width": if (int.TryParse(val, out int w)) info.Width = w; break;
					case "height": if (int.TryParse(val, out int h)) info.Height = h; break;
					case "codec_name": info.VideoCodec = val; break;
					case "bit_rate": if (long.TryParse(val, out long br)) info.BitRate = br; break;
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