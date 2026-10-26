using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace StageManager
{
	/// <summary>
	/// Shows an image turned around its vertical axis in perspective, like the window cards in the macOS Stage Manager sidebar.
	/// While highlighted, the image turns towards the viewer and grows a bit.
	/// </summary>
	public class TiltedImage : FrameworkElement
	{
		private const double FIELD_OF_VIEW = 30;
		private const double CAMERA_DISTANCE = 3;
		private const double HIGHLIGHT_ANGLE = 6;
		private const double HIGHLIGHT_SCALE = 1.05;
		private const double MARGIN = 0.94;

		private static readonly Duration _highlightDuration = new Duration(TimeSpan.FromMilliseconds(180));

		private readonly Viewport3D _viewport;
		private readonly MeshGeometry3D _mesh;
		private readonly ImageBrush _brush;
		private readonly AxisAngleRotation3D _rotation;
		private readonly ScaleTransform3D _scale;

		public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(TiltedImage),
			new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((TiltedImage)d).OnSourceChanged()));

		public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(TiltedImage),
			new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((TiltedImage)d).AnimateHighlight()));

		public static readonly DependencyProperty IsHighlightedProperty = DependencyProperty.Register(nameof(IsHighlighted), typeof(bool), typeof(TiltedImage),
			new PropertyMetadata(false, (d, _) => ((TiltedImage)d).AnimateHighlight()));

		public TiltedImage()
		{
			_mesh = new MeshGeometry3D
			{
				TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
				TextureCoordinates = new PointCollection { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) }
			};

			_brush = new ImageBrush { Stretch = Stretch.Fill };
			_rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), Angle);
			_scale = new ScaleTransform3D(1, 1, 1);

			var transform = new Transform3DGroup();
			transform.Children.Add(_scale);
			transform.Children.Add(new RotateTransform3D(_rotation));

			var model = new GeometryModel3D(_mesh, new DiffuseMaterial(_brush)) { Transform = transform };

			var scene = new Model3DGroup();
			scene.Children.Add(new AmbientLight(Colors.White));
			scene.Children.Add(model);

			_viewport = new Viewport3D
			{
				Camera = new PerspectiveCamera(new Point3D(0, 0, CAMERA_DISTANCE), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), FIELD_OF_VIEW),
				ClipToBounds = false
			};
			_viewport.Children.Add(new ModelVisual3D { Content = scene });

			AddVisualChild(_viewport);
		}

		public ImageSource? Source
		{
			get => (ImageSource?)GetValue(SourceProperty);
			set => SetValue(SourceProperty, value);
		}

		/// <summary>
		/// Degrees the image is turned by, positive values move its right edge away from the viewer
		/// </summary>
		public double Angle
		{
			get => (double)GetValue(AngleProperty);
			set => SetValue(AngleProperty, value);
		}

		public bool IsHighlighted
		{
			get => (bool)GetValue(IsHighlightedProperty);
			set => SetValue(IsHighlightedProperty, value);
		}

		protected override int VisualChildrenCount => 1;

		protected override Visual GetVisualChild(int index) => _viewport;

		protected override Size MeasureOverride(Size availableSize)
		{
			_viewport.Measure(availableSize);
			return new Size(0, 0);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			_viewport.Arrange(new Rect(finalSize));
			UpdateMesh(finalSize);
			return finalSize;
		}

		private void OnSourceChanged()
		{
			_brush.ImageSource = Source;
			UpdateMesh(RenderSize);
		}

		private void AnimateHighlight()
		{
			var angle = IsHighlighted ? HIGHLIGHT_ANGLE : Angle;
			var scale = IsHighlighted ? HIGHLIGHT_SCALE : 1.0;
			var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

			_rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(angle, _highlightDuration) { EasingFunction = easing });
			_scale.BeginAnimation(ScaleTransform3D.ScaleXProperty, new DoubleAnimation(scale, _highlightDuration) { EasingFunction = easing });
			_scale.BeginAnimation(ScaleTransform3D.ScaleYProperty, new DoubleAnimation(scale, _highlightDuration) { EasingFunction = easing });
		}

		/// <summary>
		/// Sizes the image so that it keeps its aspect ratio and stays within the bounds of the control at all angles it can take.
		/// </summary>
		private void UpdateMesh(Size size)
		{
			if (Source is null || size.Width <= 0 || size.Height <= 0 || Source.Width <= 0 || Source.Height <= 0)
			{
				_mesh.Positions = null;
				return;
			}

			var aspect = Source.Height / Source.Width;
			var viewportAspect = size.Width / size.Height;

			// the projection grows with the image size, so search the largest width that fits
			double low = 0, high = 4;
			for (var i = 0; i < 24; i++)
			{
				var width = (low + high) / 2;
				var fits = Fits(width, aspect, Angle, 1.0, viewportAspect) && Fits(width, aspect, HIGHLIGHT_ANGLE, HIGHLIGHT_SCALE, viewportAspect);

				if (fits)
					low = width;
				else
					high = width;
			}

			var halfWidth = low / 2;
			var halfHeight = low * aspect / 2;

			_mesh.Positions = new Point3DCollection
			{
				new Point3D(-halfWidth, -halfHeight, 0),
				new Point3D(halfWidth, -halfHeight, 0),
				new Point3D(halfWidth, halfHeight, 0),
				new Point3D(-halfWidth, halfHeight, 0)
			};
		}

		/// <summary>
		/// Checks if the corners of an image with the given width, turned by the given angle, are projected inside the viewport
		/// </summary>
		private static bool Fits(double width, double aspect, double angle, double scale, double viewportAspect)
		{
			var radians = angle * Math.PI / 180;
			var halfVisibleWidth = Math.Tan(FIELD_OF_VIEW / 2 * Math.PI / 180);
			var halfVisibleHeight = halfVisibleWidth / viewportAspect;

			foreach (var x in new[] { -width / 2 * scale, width / 2 * scale })
			{
				foreach (var y in new[] { -width * aspect / 2 * scale, width * aspect / 2 * scale })
				{
					// rotating around the y axis moves the right edge (positive x) away from the camera for positive angles
					var rotatedX = x * Math.Cos(radians);
					var rotatedZ = -x * Math.Sin(radians);
					var distance = CAMERA_DISTANCE - rotatedZ;

					if (Math.Abs(rotatedX / distance) > halfVisibleWidth * MARGIN || Math.Abs(y / distance) > halfVisibleHeight * MARGIN)
						return false;
				}
			}

			return true;
		}
	}
}
