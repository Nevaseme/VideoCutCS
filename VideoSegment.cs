using System;

namespace VideoCutCS
{
	public class VideoSegment
	{
		public TimeSpan Start { get; set; }
		public TimeSpan End { get; set; }
		public int Index { get; set; }

		// リスト表示用などに使用する読み取り専用プロパティ
		public TimeSpan Duration => End - Start;
		public string DisplayString => $"{Index}: {Start:hh\\:mm\\:ss} - {End:hh\\:mm\\:ss} ({Duration:mm\\:ss})";
	}
}