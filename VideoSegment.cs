using System;
using System.ComponentModel;

namespace VideoCutCS
{
	public class VideoSegment : INotifyPropertyChanged
	{
		private int _index;

		public event PropertyChangedEventHandler? PropertyChanged;

		public TimeSpan Start { get; set; }
		public TimeSpan End { get; set; }

		public int Index
		{
			get => _index;
			set
			{
				_index = value;
				// DisplayString は Index に依存するため合わせて通知
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayString)));
			}
		}

		public TimeSpan Duration => End - Start;
		public string DisplayString => $"{Index,2}: {Start:hh\\:mm\\:ss} - {End:hh\\:mm\\:ss} ({Duration:hh\\:mm\\:ss})";
	}
}
