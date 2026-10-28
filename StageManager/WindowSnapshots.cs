using StageManager.Native;
using StageManager.Native.Window;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace StageManager
{
	/// <summary>
	/// Pictures of the windows shown in the sidebar. DWM thumbnails are live but flat, pictures can be shown in perspective.
	/// </summary>
	public static class WindowSnapshots
	{
		/// <summary>
		/// Wide enough to stay sharp on a highlighted card (200 units, a bit bigger on hover) at 200% scaling
		/// </summary>
		private const int MAX_WIDTH = 440;

		/// <summary>
		/// Taking the pictures delays minimizing the windows, don't wait longer for apps that don't respond
		/// </summary>
		private const int CAPTURE_TIMEOUT_MILLISECONDS = 300;

		private static readonly ConcurrentDictionary<IntPtr, BitmapSource> _snapshots = new();
		private static readonly ConcurrentDictionary<IntPtr, byte> _capturing = new();

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		/// <summary>
		/// Raised on the thread that asked for the picture, for each window that got a new one
		/// </summary>
		public static event Action<IntPtr>? Updated;

		public static BitmapSource? Get(IntPtr handle) => _snapshots.TryGetValue(handle, out var snapshot) ? snapshot : null;

		public static void Remove(IntPtr handle) => _snapshots.TryRemove(handle, out _);

		public static int Count => _snapshots.Count;

		/// <summary>
		/// Drops the pictures of windows that are gone without Stage Manager noticing
		/// </summary>
		public static void ForgetClosedWindows()
		{
			foreach (var handle in _snapshots.Keys.Where(h => !IsWindow(h)).ToArray())
				_snapshots.TryRemove(handle, out _);
		}

		/// <summary>
		/// Takes pictures of all windows at once and waits for them a limited time
		/// </summary>
		public static void Capture(IWindow[] windows)
		{
			if (windows.Length == 0)
				return;

			// a window that is slow to draw keeps its capture busy, don't pile up more of them
			var captures = windows
				.Where(w => _capturing.TryAdd(w.Handle, 0))
				.Select(w => Task.Run(() =>
				{
					try
					{
						return (w.Handle, Snapshot: WindowCapture.Capture(w, MAX_WIDTH));
					}
					finally
					{
						_capturing.TryRemove(w.Handle, out _);
					}
				}))
				.ToArray();

			try
			{
				Task.WaitAll(captures, CAPTURE_TIMEOUT_MILLISECONDS);
			}
			catch (AggregateException ex)
			{
				App.Log(ex);
			}

			foreach (var capture in captures.Where(c => c.IsCompletedSuccessfully && c.Result.Snapshot is not null))
				Store(capture.Result.Handle, capture.Result.Snapshot!);
		}

		private static void Store(IntPtr handle, BitmapSource snapshot)
		{
			// the window may have closed while it was captured
			if (!IsWindow(handle))
				return;

			_snapshots[handle] = snapshot;
			Updated?.Invoke(handle);
		}
	}
}
