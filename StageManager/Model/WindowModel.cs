using StageManager.Native;
using StageManager.Native.Window;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageManager.Model
{
	[System.Diagnostics.DebuggerDisplay("{Title}")]
	public class WindowModel : INotifyPropertyChanged
	{
		//If you get 'dllimport unknown'-, then add 'using System.Runtime.InteropServices;'
		[DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool DeleteObject([In] IntPtr hObject);

		/// <summary>
		/// Like on macOS, only the front windows of a scene are shown as a stack in the sidebar
		/// </summary>
		private const int MAX_STACKED_WINDOWS = 3;

		private IWindow _window;
		private ImageSource _iconSource;
		private int _stackIndex;

		public event PropertyChangedEventHandler PropertyChanged;

		public WindowModel(IWindow window)
		{
			Window = window ?? throw new ArgumentNullException(nameof(window));
		}

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}

		public string Title => _window.Title.Length > 20 ? _window.Title.Substring(0, 17) + " ..." : _window.Title;

		public ImageSource ImageSourceFromBitmap(System.Drawing.Bitmap bmp)
		{
			if (bmp is null)
				return null;

			var handle = bmp.GetHbitmap();
			try
			{
				return Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			}
			finally { DeleteObject(handle); }
		}

		/// <summary>
		/// One picture per program, shared by all its windows. Reading the icon from the executable every time a window
		/// shows up in the sidebar kept adding icon handles while Stage Manager ran.
		/// </summary>
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

		private static ImageSource? GetIcon(IWindow window)
		{
			if (window is not WindowsWindow windowsWindow || string.IsNullOrEmpty(windowsWindow.ProcessFileName))
				return null;

			return _icons.GetOrAdd(windowsWindow.ProcessFileName, _ =>
			{
				using var icon = windowsWindow.ExtractIcon();
				if (icon is null)
					return null;

				var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return source;
			});
		}

		public ImageSource? Icon => _iconSource ??= GetIcon(Window);

		public IWindow Window
		{
			get => _window;
			set
			{
				_window = value;

				RaisePropertyChanged();
				RaisePropertyChanged(nameof(Title));
				RaisePropertyChanged(nameof(Handle));
			}
		}

		public IntPtr Handle => _window?.Handle ?? IntPtr.Zero;

		/// <summary>
		/// The picture taken when the window was hidden, null if it was minimized before Stage Manager could take one
		/// </summary>
		public ImageSource? Snapshot => WindowSnapshots.Get(Handle);

		public bool HasSnapshot => Snapshot is not null;

		public void RefreshSnapshot()
		{
			RaisePropertyChanged(nameof(Snapshot));
			RaisePropertyChanged(nameof(HasSnapshot));
		}

		/// <summary>
		/// The position in the stack of windows of a scene, 0 is the front window
		/// </summary>
		public int StackIndex
		{
			get => _stackIndex;
			set
			{
				if (_stackIndex == value)
					return;

				_stackIndex = value;
				RaisePropertyChanged();
				RaisePropertyChanged(nameof(ZIndex));
				RaisePropertyChanged(nameof(StackOffsetX));
				RaisePropertyChanged(nameof(StackOffsetY));
				RaisePropertyChanged(nameof(StackScale));
				RaisePropertyChanged(nameof(StackVisibility));
			}
		}

		public int ZIndex => -StackIndex;

		// windows further back peek out to the left and above the front window
		public double StackOffsetX => StackIndex * -10;

		public double StackOffsetY => StackIndex * -6;

		public double StackScale => Math.Pow(0.94, StackIndex);

		public Visibility StackVisibility => StackIndex < MAX_STACKED_WINDOWS ? Visibility.Visible : Visibility.Collapsed;
	}
}
