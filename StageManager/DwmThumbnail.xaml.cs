using StageManager.Native.Interop;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for DwmThumbnail.xaml
	/// </summary>
	public partial class DwmThumbnail : UserControl
	{
		public DwmThumbnail()
		{
			InitializeComponent();
			LayoutUpdated += DwmThumbnail_LayoutUpdated;
			Unloaded += (_, _) => StopCapture();
		}

		private IntPtr _dwmThumbnail;
		private Window? _window;
		private Point? _dpiScaleFactor;

		public static readonly DependencyProperty PreviewHandleProperty = DependencyProperty.Register(nameof(PreviewHandle),
			   typeof(IntPtr),
			   typeof(DwmThumbnail),
			   new PropertyMetadata(IntPtr.Zero));

		public IntPtr PreviewHandle
		{
			get { return (IntPtr)GetValue(PreviewHandleProperty); }
			set { SetValue(PreviewHandleProperty, value); }
		}

		private Point GetDpiScaleFactor()
		{
			if (_dpiScaleFactor is null)
			{
				var source = PresentationSource.FromVisual(this);
				_dpiScaleFactor = source?.CompositionTarget != null ? new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22) : new Point(1.0d, 1.0d);
			}

			return _dpiScaleFactor.Value;
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			_dpiScaleFactor = null;
			base.OnDpiChanged(oldDpi, newDpi);
		}

		protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);

			if (e.Property == PreviewHandleProperty)
			{
				StopCapture();
				StartCapture();
			}

			// a thumbnail is drawn by DWM no matter if WPF shows the control or not, so it has to be removed while invisible.
			// It has to come back once the control is visible again, like when a hidden sidebar or scene is shown.
			if (e.Property == IsVisibleProperty)
			{
				if ((bool)e.NewValue)
					StartCapture();
				else
					StopCapture();
			}
		}

		private void DwmThumbnail_LayoutUpdated(object? sender, EventArgs e)
		{
			UpdateThumbnailProperties();
		}

		public static Rect BoundsRelativeTo(FrameworkElement element, Visual relativeTo)
		{
			return element.TransformToVisual(relativeTo)
						  .TransformBounds(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element));
		}

		private void StartCapture()
		{
			if (_dwmThumbnail != IntPtr.Zero || PreviewHandle == IntPtr.Zero || !IsVisible || FindWindow() is not Window window)
				return;

			var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
			if (windowHandle == IntPtr.Zero)
				return;

			if (NativeMethods.DwmRegisterThumbnail(windowHandle, PreviewHandle, out var thumbnail) == 0)
			{
				_dwmThumbnail = thumbnail;
				UpdateThumbnailProperties();
			}
		}

		private void StopCapture()
		{
			if (_dwmThumbnail == IntPtr.Zero)
				return;

			NativeMethods.DwmUnregisterThumbnail(_dwmThumbnail);
			_dwmThumbnail = IntPtr.Zero;
		}

		private Window? FindWindow() => _window ??= Window.GetWindow(this);

		private void UpdateThumbnailProperties()
		{
			if (_dwmThumbnail == IntPtr.Zero)
				return;

			var dpi = GetDpiScaleFactor();

			if (FindWindow() is not Window window)
				return;

			var previewBounds = BoundsRelativeTo(this, window);

			var thumbnailRect = new RECT
			{
				top = (int)(previewBounds.Top * dpi.Y),
				left = (int)(previewBounds.Left * dpi.X),
				bottom = (int)((previewBounds.Bottom - Margin.Top - Margin.Bottom) * dpi.Y) + 1,
				right = (int)((previewBounds.Right - Margin.Left - Margin.Right) * dpi.X) + 1
			};

			var props = new DWM_THUMBNAIL_PROPERTIES
			{
				fVisible = true,
				dwFlags = (int)(DWM_TNP.DWM_TNP_VISIBLE | DWM_TNP.DWM_TNP_OPACITY | DWM_TNP.DWM_TNP_RECTDESTINATION | DWM_TNP.DWM_TNP_SOURCECLIENTAREAONLY),
				opacity = 255,
				rcDestination = thumbnailRect,
				fSourceClientAreaOnly = true
			};
			NativeMethods.DwmUpdateThumbnailProperties(_dwmThumbnail, ref props);
		}
	}
}
