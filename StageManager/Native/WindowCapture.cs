using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageManager.Native
{
	/// <summary>
	/// Takes pictures of windows for the sidebar. DWM thumbnails are live but can only be drawn as flat rectangles,
	/// pictures can be shown in perspective. Windows hidden by Stage Manager are minimized and don't change anyway.
	/// </summary>
	internal static class WindowCapture
	{
		private const uint PW_RENDERFULLCONTENT = 2;
		private const int HALFTONE = 4;
		private const int SRCCOPY = 0x00CC0020;

		[DllImport("user32.dll")]
		private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

		[DllImport("gdi32.dll")]
		private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr hObject);

		[DllImport("gdi32.dll")]
		private static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);

		[DllImport("gdi32.dll")]
		private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

		[DllImport("gdi32.dll")]
		private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

		/// <summary>
		/// Captures what the window shows, without the invisible resize borders and scaled down to the given width.
		/// Works for covered and hardware accelerated windows, but not for minimized ones. Can be called from any thread.
		/// </summary>
		public static BitmapSource? Capture(IWindow window, int maxWidth)
		{
			var windowRect = new Win32.Rect();
			if (!Win32.GetWindowRect(window.Handle, ref windowRect))
				return null;

			var width = windowRect.Right - windowRect.Left;
			var height = windowRect.Bottom - windowRect.Top;
			if (width <= 0 || height <= 0)
				return null;

			var visible = window.VisibleBounds;
			var cropX = Math.Clamp(visible.Left - windowRect.Left, 0, width - 1);
			var cropY = Math.Clamp(visible.Top - windowRect.Top, 0, height - 1);
			var cropWidth = Math.Clamp(visible.Width, 1, width - cropX);
			var cropHeight = Math.Clamp(visible.Height, 1, height - cropY);

			var scale = Math.Min(1.0, maxWidth / (double)cropWidth);
			var targetWidth = Math.Max(1, (int)Math.Round(cropWidth * scale));
			var targetHeight = Math.Max(1, (int)Math.Round(cropHeight * scale));

			var screenDc = GetDC(IntPtr.Zero);
			var fullDc = CreateCompatibleDC(screenDc);
			var fullBitmap = CreateCompatibleBitmap(screenDc, width, height);
			var oldFullBitmap = SelectObject(fullDc, fullBitmap);
			var smallDc = CreateCompatibleDC(screenDc);
			var smallBitmap = CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
			var oldSmallBitmap = SelectObject(smallDc, smallBitmap);

			try
			{
				// PW_RENDERFULLCONTENT also captures content drawn with DirectComposition, like browsers do
				if (!PrintWindow(window.Handle, fullDc, PW_RENDERFULLCONTENT))
					return null;

				SetStretchBltMode(smallDc, HALFTONE);
				SetBrushOrgEx(smallDc, 0, 0, IntPtr.Zero);
				if (!StretchBlt(smallDc, 0, 0, targetWidth, targetHeight, fullDc, cropX, cropY, cropWidth, cropHeight, SRCCOPY))
					return null;

				SelectObject(smallDc, oldSmallBitmap);
				oldSmallBitmap = IntPtr.Zero;

				var source = Imaging.CreateBitmapSourceFromHBitmap(smallBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

				// copy into an opaque bitmap, GDI leaves the alpha channel empty. Frozen, it can be used on the UI thread.
				var stride = targetWidth * 4;
				var pixels = new byte[stride * targetHeight];
				source.CopyPixels(pixels, stride, 0);

				var picture = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
				picture.Freeze();
				return picture;
			}
			finally
			{
				if (oldSmallBitmap != IntPtr.Zero)
					SelectObject(smallDc, oldSmallBitmap);

				SelectObject(fullDc, oldFullBitmap);
				DeleteObject(smallBitmap);
				DeleteObject(fullBitmap);
				DeleteDC(smallDc);
				DeleteDC(fullDc);
				ReleaseDC(IntPtr.Zero, screenDc);
			}
		}
	}
}
