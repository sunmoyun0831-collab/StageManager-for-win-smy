using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Native
{
	public enum WallpaperPosition
	{
		Center = 0,
		Tile = 1,
		Stretch = 2,
		Fit = 3,
		Fill = 4,
		Span = 5
	}

	/// <param name="Path">The image file, null for a plain color</param>
	/// <param name="BackgroundColor">Shown where the image does not cover the screen, as 0xRRGGBB</param>
	public record WallpaperInfo(string? Path, WallpaperPosition Position, int BackgroundColor);

	/// <summary>
	/// Reads the desktop wallpaper of a screen
	/// </summary>
	internal static class Wallpaper
	{
		private const uint SPI_GETDESKWALLPAPER = 0x0073;
		private const int COLOR_DESKTOP = 1;

		[ComImport]
		[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IDesktopWallpaper
		{
			void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

			[return: MarshalAs(UnmanagedType.LPWStr)]
			string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);

			[return: MarshalAs(UnmanagedType.LPWStr)]
			string GetMonitorDevicePathAt(uint monitorIndex);

			uint GetMonitorDevicePathCount();

			MonitorRect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);

			void SetBackgroundColor(uint color);

			uint GetBackgroundColor();

			void SetPosition(WallpaperPosition position);

			WallpaperPosition GetPosition();
		}

		[ComImport]
		[Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
		private class DesktopWallpaper
		{
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MonitorRect
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

		[DllImport("user32.dll")]
		private static extern uint GetSysColor(int nIndex);

		/// <summary>
		/// Gets the wallpaper of the screen with the given bounds in physical pixels
		/// </summary>
		public static WallpaperInfo Get(System.Drawing.Rectangle screenBounds)
		{
			try
			{
				var wallpaper = (IDesktopWallpaper)new DesktopWallpaper();
				try
				{
					var monitorId = FindMonitor(wallpaper, screenBounds);
					var path = wallpaper.GetWallpaper(monitorId);

					return new WallpaperInfo(ExistingFile(path), wallpaper.GetPosition(), ToRgb(wallpaper.GetBackgroundColor()));
				}
				finally
				{
					Marshal.ReleaseComObject(wallpaper);
				}
			}
			catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is ArgumentException)
			{
				// older ways to read it, without knowing about several screens
				var path = new StringBuilder(260);
				SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0);
				return new WallpaperInfo(ExistingFile(path.ToString()), WallpaperPosition.Fill, ToRgb(GetSysColor(COLOR_DESKTOP)));
			}
		}

		private static string? FindMonitor(IDesktopWallpaper wallpaper, System.Drawing.Rectangle screenBounds)
		{
			var count = wallpaper.GetMonitorDevicePathCount();

			for (uint i = 0; i < count; i++)
			{
				var id = wallpaper.GetMonitorDevicePathAt(i);
				var rect = wallpaper.GetMonitorRECT(id);

				if (rect.Left == screenBounds.Left && rect.Top == screenBounds.Top)
					return id;
			}

			// null asks for the wallpaper shared by all screens
			return null;
		}

		private static string? ExistingFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

		/// <summary>
		/// Windows stores colors as 0xBBGGRR
		/// </summary>
		private static int ToRgb(uint colorRef) => (int)(((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF));
	}
}
