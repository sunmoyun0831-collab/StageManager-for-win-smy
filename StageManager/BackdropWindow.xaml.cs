using StageManager.Native;
using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageManager
{
	/// <summary>
	/// Covers the screen with the desktop wallpaper. The windows of the current scene lie in front of it,
	/// all other windows behind it. Unlike minimized windows, the windows behind it stay alive, so their
	/// thumbnails keep updating and no minimize animations play when switching scenes.
	/// If Stage Manager ends for whatever reason, the backdrop goes away and all windows are there again.
	/// </summary>
	public partial class BackdropWindow : Window, IBackdrop
	{
		private const int WM_WINDOWPOSCHANGING = 0x0046;
		private const int WM_MOUSEACTIVATE = 0x0021;
		private const int MA_NOACTIVATE = 3;
		private const uint SWP_NOZORDER = 0x0004;

		[StructLayout(LayoutKind.Sequential)]
		private struct WINDOWPOS
		{
			public IntPtr hwnd;
			public IntPtr hwndInsertAfter;
			public int x;
			public int y;
			public int cx;
			public int cy;
			public uint flags;
		}

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

		private IntPtr _handle;
		private bool _isArranging;
		private System.Windows.Forms.Screen _screen;
		private WallpaperInfo? _wallpaper;
		private DateTime _wallpaperFileTime;

		/// <summary>
		/// Raised when the user clicks the wallpaper
		/// </summary>
		public event EventHandler? Clicked;

		public BackdropWindow(System.Windows.Forms.Screen screen)
		{
			InitializeComponent();
			_screen = screen;
			MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);

			// Windows resizes windows that move to a screen with another DPI, cover the screen again afterwards
			DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateScreen(_screen));
		}

		public IntPtr Handle => _handle;

		/// <summary>
		/// Windows that always have to lie in front of the backdrop, like the sidebar
		/// </summary>
		public IntPtr Sidebar { get; set; }

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			_handle = new WindowInteropHelper(this).Handle;

			// not listed in Alt+Tab and never activated, clicking it must not bring it in front of the windows
			var exStyle = Win32.GetWindowExStyleLongPtr(_handle);
			Win32.SetWindowStyleExLongPtr(_handle, exStyle | Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_NOACTIVATE);

			HwndSource.FromHwnd(_handle).AddHook(WndProc);

			UpdateScreen(_screen);
		}

		/// <summary>
		/// Places the backdrop on the given screen and shows its wallpaper
		/// </summary>
		public void UpdateScreen(System.Windows.Forms.Screen screen)
		{
			_screen = screen;

			if (_handle == IntPtr.Zero)
				return;

			// physical pixels, the app is per monitor DPI aware
			var bounds = screen.Bounds;
			SetWindowPos(_handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOZORDER | 0x0010 /* SWP_NOACTIVATE */);

			ReloadWallpaper();
		}

		/// <summary>
		/// Reads the wallpaper again, if it changed
		/// </summary>
		public void ReloadWallpaper()
		{
			// a wallpaper that can't be read must never keep the backdrop from working, it stays plain then
			try
			{
				var wallpaper = Wallpaper.Get(_screen.Bounds);

				var fileTime = wallpaper.Path is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(wallpaper.Path);
				if (wallpaper == _wallpaper && fileTime == _wallpaperFileTime)
					return;

				_wallpaper = wallpaper;
				_wallpaperFileTime = fileTime;
				ShowWallpaper(wallpaper);
			}
			catch (Exception ex)
			{
				App.Log(ex);
			}
		}

		private void ShowWallpaper(WallpaperInfo wallpaper)
		{
			var color = Color.FromRgb((byte)(wallpaper.BackgroundColor >> 16), (byte)(wallpaper.BackgroundColor >> 8), (byte)wallpaper.BackgroundColor);
			Background = new SolidColorBrush(color);

			image.Source = null;
			tiles.Visibility = Visibility.Collapsed;

			var picture = LoadPicture(wallpaper.Path);
			if (picture is null)
				return;

			var dpi = VisualTreeHelper.GetDpi(this);

			switch (wallpaper.Position)
			{
				case WallpaperPosition.Tile:
					// tiles keep the size of the picture in physical pixels
					tiles.Fill = new ImageBrush(picture)
					{
						TileMode = TileMode.Tile,
						Stretch = Stretch.None,
						ViewportUnits = BrushMappingMode.Absolute,
						Viewport = new Rect(0, 0, picture.PixelWidth / dpi.DpiScaleX, picture.PixelHeight / dpi.DpiScaleY)
					};
					tiles.Visibility = Visibility.Visible;
					break;

				case WallpaperPosition.Center:
					image.Stretch = Stretch.None;
					image.Width = picture.PixelWidth / dpi.DpiScaleX;
					image.Height = picture.PixelHeight / dpi.DpiScaleY;
					image.Source = picture;
					break;

				default:
					image.Width = double.NaN;
					image.Height = double.NaN;
					image.Stretch = wallpaper.Position switch
					{
						WallpaperPosition.Stretch => Stretch.Fill,
						WallpaperPosition.Fit => Stretch.Uniform,
						_ => Stretch.UniformToFill // Fill and Span
					};
					image.Source = picture;
					break;
			}
		}

		private BitmapSource? LoadPicture(string? path)
		{
			if (path is null)
				return null;

			try
			{
				var picture = new BitmapImage();
				picture.BeginInit();
				picture.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
				picture.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // the file stays the same when a slideshow changes the wallpaper
				picture.UriSource = new Uri(path);
				picture.DecodePixelWidth = Math.Max(_screen.Bounds.Width, 1); // no need to keep more pixels than the screen has
				picture.EndInit();
				picture.Freeze();
				return picture;
			}
			catch (Exception ex)
			{
				// images can fail in many ways, from missing codecs to broken files
				App.Log(ex);
				return null;
			}
		}

		/// <summary>
		/// Puts the given windows in front of the backdrop, followed by the sidebar and the backdrop. All other windows end up behind it.
		/// </summary>
		public void Arrange(IEnumerable<IntPtr> front)
		{
			if (_handle == IntPtr.Zero)
				return;

			var frontWindows = front.ToArray();

			try
			{
				_isArranging = true;
				ZOrder.Arrange(frontWindows, new[] { Sidebar, _handle });
			}
			finally
			{
				_isArranging = false;
			}

			ReportWindowsBehind(frontWindows);
		}

		private int _reportedArrangeProblems;

		/// <summary>
		/// Logs if windows that should be in front of the backdrop ended up behind it, to find out why if that happens
		/// </summary>
		private void ReportWindowsBehind(IntPtr[] front)
		{
			if (_reportedArrangeProblems >= 3)
				return;

			var behind = ZOrder.FindBehind(front, _handle);
			if (behind.Count == 0)
				return;

			_reportedArrangeProblems++;
			App.Log(new InvalidOperationException($"{behind.Count} of {front.Length} windows of the scene ended up behind the backdrop: {string.Join(", ", behind.Select(h => h.ToString("X")))}"));
		}

		public void PutBehind(IntPtr window)
		{
			if (_handle != IntPtr.Zero)
				ZOrder.PutBehind(window, _handle);
		}

		/// <summary>
		/// Can be called from any thread, as it is also used when the app crashes
		/// </summary>
		public void HideFromAnyThread()
		{
			if (_handle != IntPtr.Zero)
				Win32.ShowWindowAsync(_handle, Win32.SW.SW_HIDE);
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (msg == WM_WINDOWPOSCHANGING && !_isArranging)
			{
				// only Stage Manager decides where the backdrop lies, it must never come in front of the windows on its own
				var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
				position.flags |= SWP_NOZORDER;
				Marshal.StructureToPtr(position, lParam, false);
			}
			else if (msg == WM_MOUSEACTIVATE)
			{
				handled = true;
				return new IntPtr(MA_NOACTIVATE);
			}

			return IntPtr.Zero;
		}
	}

	/// <summary>
	/// The surface the windows of the current scene lie on, see <see cref="BackdropWindow"/>
	/// </summary>
	public interface IBackdrop
	{
		IntPtr Handle { get; }

		bool IsVisible { get; }

		void Show();

		void Hide();

		void HideFromAnyThread();

		void Arrange(IEnumerable<IntPtr> front);

		void PutBehind(IntPtr window);
	}
}
