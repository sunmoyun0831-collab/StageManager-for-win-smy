using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// Some apps keep windows that are visible to Windows but not to the user, to handle messages for example.
	/// Minimizing them would show their title bar in a corner of the screen.
	/// </summary>
	internal static class WindowVisibility
	{
		/// <summary>
		/// Smaller windows are no windows anyone works with
		/// </summary>
		private const int MIN_SIZE = 10;

		private const uint LWA_ALPHA = 0x2;

		[DllImport("user32.dll")]
		private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

		/// <summary>
		/// True if the window has a size, lies on a screen and is not fully transparent.
		/// Minimized windows are checked where they would be when restored.
		/// </summary>
		public static bool CanBeSeen(IWindow window)
		{
			System.Drawing.Rectangle bounds;

			if (window.IsMinimized)
			{
				if (WindowPlacement.GetRestoredBounds(window.Handle) is not System.Windows.Rect restored)
					return true;

				bounds = new System.Drawing.Rectangle((int)restored.X, (int)restored.Y, (int)restored.Width, (int)restored.Height);
			}
			else
			{
				bounds = window.VisibleBounds;
			}

			if (bounds.Width < MIN_SIZE || bounds.Height < MIN_SIZE)
				return false;

			if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(bounds)))
				return false;

			return !IsTransparent(window.Handle);
		}

		private static bool IsTransparent(IntPtr handle)
		{
			if (!Win32.GetWindowExStyleLongPtr(handle).HasFlag(Win32.WS_EX.WS_EX_LAYERED))
				return false;

			return GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags) && (flags & LWA_ALPHA) != 0 && alpha == 0;
		}
	}
}
