using StageManager.Native;
using StageManager.Native.Window;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace StageManager
{
	/// <summary>
	/// Pictures of the windows taken right before Stage Manager hid them, shown in the sidebar.
	/// </summary>
	public static class WindowSnapshots
	{
		/// <summary>
		/// Wide enough to stay sharp on a zoomed card at 200% scaling
		/// </summary>
		private const int MAX_WIDTH = 640;

		/// <summary>
		/// Taking the pictures delays hiding the windows, don't wait longer for apps that don't respond
		/// </summary>
		private const int CAPTURE_TIMEOUT_MILLISECONDS = 300;

		private static readonly ConcurrentDictionary<IntPtr, BitmapSource> _snapshots = new();

		/// <summary>
		/// Raised on the thread that called <see cref="Capture"/> for each window that got a new picture
		/// </summary>
		public static event Action<IntPtr>? Updated;

		public static BitmapSource? Get(IntPtr handle) => _snapshots.TryGetValue(handle, out var snapshot) ? snapshot : null;

		public static void Remove(IntPtr handle) => _snapshots.TryRemove(handle, out _);

		/// <summary>
		/// Takes pictures of all windows at once and waits for them a limited time
		/// </summary>
		public static void Capture(IWindow[] windows)
		{
			if (windows.Length == 0)
				return;

			var captures = windows.Select(w => Task.Run(() => (w.Handle, Snapshot: WindowCapture.Capture(w, MAX_WIDTH)))).ToArray();

			try
			{
				Task.WaitAll(captures, CAPTURE_TIMEOUT_MILLISECONDS);
			}
			catch (AggregateException ex)
			{
				App.Log(ex);
			}

			foreach (var capture in captures.Where(c => c.IsCompletedSuccessfully && c.Result.Snapshot is not null))
			{
				_snapshots[capture.Result.Handle] = capture.Result.Snapshot!;
				Updated?.Invoke(capture.Result.Handle);
			}
		}
	}
}
