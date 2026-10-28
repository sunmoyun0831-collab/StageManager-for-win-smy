using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// Lets the taskbar button of a window flash, like Windows does for apps it does not let come to the front
	/// </summary>
	internal static class WindowAttention
	{
		private const uint FLASHW_TRAY = 0x2;
		private const uint FLASHW_TIMERNOFG = 0xC;

		[StructLayout(LayoutKind.Sequential)]
		private struct FLASHWINFO
		{
			public uint cbSize;
			public IntPtr hwnd;
			public uint dwFlags;
			public uint uCount;
			public uint dwTimeout;
		}

		[DllImport("user32.dll")]
		private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

		/// <summary>
		/// Flashes until the window comes to the front
		/// </summary>
		public static void Flash(IntPtr handle)
		{
			var info = new FLASHWINFO
			{
				cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
				hwnd = handle,
				dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG
			};

			FlashWindowEx(ref info);
		}
	}
}
