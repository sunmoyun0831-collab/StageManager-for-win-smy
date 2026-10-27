using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StageManager.Native
{
	/// <summary>
	/// Turns off the Windows animation for minimizing and restoring windows while Stage Manager plays its own.
	/// The change is not saved, so it is gone after signing out even if Stage Manager can't turn it back on.
	/// </summary>
	internal static class MinimizeAnimation
	{
		private const uint SPI_GETANIMATION = 0x0048;
		private const uint SPI_SETANIMATION = 0x0049;

		/// <summary>
		/// The longest the animation stays off, in case turning it back on gets lost
		/// </summary>
		private const int SAFETY_MILLISECONDS = 3000;

		[StructLayout(LayoutKind.Sequential)]
		private struct ANIMATIONINFO
		{
			public uint cbSize;
			public int iMinAnimate;
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

		private static readonly object _lock = new object();
		private static bool _suppressed;
		private static int _version;

		/// <summary>
		/// Turns the animation off, if the user has it on
		/// </summary>
		public static void Suppress()
		{
			int version;

			lock (_lock)
			{
				version = ++_version;

				if (!_suppressed && IsEnabled() && Set(false))
					_suppressed = true;
			}

			RestoreLater(version, SAFETY_MILLISECONDS);
		}

		/// <summary>
		/// Turns the animation back on after a moment. Windows are told asynchronously to minimize or restore,
		/// they have to get to it before the animation is on again.
		/// </summary>
		public static void Allow(int delayMilliseconds)
		{
			int version;

			lock (_lock)
				version = _version;

			RestoreLater(version, delayMilliseconds);
		}

		/// <summary>
		/// Turns the animation back on right away. Can be called from any thread, as it is also used when the app crashes.
		/// </summary>
		public static void Restore()
		{
			lock (_lock)
			{
				if (_suppressed)
					Set(true);

				_suppressed = false;
			}
		}

		private static async void RestoreLater(int version, int delayMilliseconds)
		{
			await Task.Delay(delayMilliseconds).ConfigureAwait(false);

			lock (_lock)
			{
				// suppressed again in the meantime, the later call decides
				if (version == _version)
					Restore();
			}
		}

		private static bool IsEnabled()
		{
			var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
			return SystemParametersInfo(SPI_GETANIMATION, info.cbSize, ref info, 0) && info.iMinAnimate != 0;
		}

		private static bool Set(bool enabled)
		{
			// 0 as last parameter: neither saved nor broadcast to all windows
			var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = enabled ? 1 : 0 };
			return SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ref info, 0);
		}
	}
}
