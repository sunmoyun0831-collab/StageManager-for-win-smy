using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace StageManager.Native
{
	/// <summary>
	/// Where minimized windows go when they are restored
	/// </summary>
	internal static class WindowPlacement
	{
		private const int WPF_RESTORETOMAXIMIZED = 0x0002;
		private const int SM_CXFRAME = 32;
		private const int SM_CYFRAME = 33;
		private const int SM_CXPADDEDBORDER = 92;

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int x;
			public int y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct WINDOWPLACEMENT
		{
			public int length;
			public int flags;
			public int showCmd;
			public POINT ptMinPosition;
			public POINT ptMaxPosition;
			public Interop.RECT rcNormalPosition;
		}

		[DllImport("user32.dll")]
		private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

		[DllImport("user32.dll")]
		private static extern uint GetDpiForWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

		/// <summary>
		/// The position the window has when it is not maximized, in workspace coordinates.
		/// It changes whenever the user moves or sizes the window, but not when the window gets snapped to a side of the screen.
		/// </summary>
		public static Interop.RECT? GetNormalPosition(IntPtr handle) => TryGet(handle, out var placement) ? placement.rcNormalPosition : null;

		/// <summary>
		/// The bounds a minimized window gets when it is restored, in physical screen coordinates.
		/// Windows snapped to a side of the screen are restored to the snapped bounds, which can't be found out here.
		/// </summary>
		public static Rect? GetRestoredBounds(IntPtr handle)
		{
			if (!TryGet(handle, out var placement))
				return null;

			// workspace coordinates leave out the taskbar if it is on the left or on top
			var screen = System.Windows.Forms.Screen.FromHandle(handle);
			var normal = placement.rcNormalPosition;
			var normalBounds = new Rect(
				normal.left + screen.WorkingArea.Left - screen.Bounds.Left,
				normal.top + screen.WorkingArea.Top - screen.Bounds.Top,
				Math.Max(0, normal.right - normal.left),
				Math.Max(0, normal.bottom - normal.top));

			if ((placement.flags & WPF_RESTORETOMAXIMIZED) == 0)
				return normalBounds;

			// maximized windows reach beyond the work area by their invisible resize borders
			var dpi = GetDpiForWindow(handle);
			var paddedBorder = dpi == 0 ? 0 : GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
			var frameX = dpi == 0 ? 0 : GetSystemMetricsForDpi(SM_CXFRAME, dpi) + paddedBorder;
			var frameY = dpi == 0 ? 0 : GetSystemMetricsForDpi(SM_CYFRAME, dpi) + paddedBorder;
			var workArea = screen.WorkingArea;

			return new Rect(workArea.Left - frameX, workArea.Top - frameY, workArea.Width + 2 * frameX, workArea.Height + 2 * frameY);
		}

		private static bool TryGet(IntPtr handle, out WINDOWPLACEMENT placement)
		{
			placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
			return GetWindowPlacement(handle, ref placement);
		}
	}
}
