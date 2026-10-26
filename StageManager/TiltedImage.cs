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
	/// All images share one eye (see <see cref="EyeProperty"/>), so their edges run towards the same vanishing point.
	/// While highlighted, the image turns towards the viewer and grows a bit.
	/// </summary>
	public class TiltedImage : FrameworkElement
	{
		private const double HIGHLIGHT_ANGLE = 6;
		private const double HIGHLIGHT_SCALE = 1.05;

		/// <summary>
		/// Windows further back in a stack move away from the viewer and a bit to the left
		/// </summary>
		private const double STACK_DEPTH = 40;
		private const double STACK_SHIFT = 14;

		/// <summary>
		/// Room around the element for the parts of the image that stick out, like windows further back in a stack.
		/// Content outside of the viewport would be clipped.
		/// </summary>
		private const double VIEWPORT_MARGIN = 0.5;

		private static readonly Duration _highlightDuration = new Duration(TimeSpan.FromMilliseconds(180));

		private readonly Viewport3D _viewport;
		private readonly MatrixCamera _camera;
		private readonly MeshGeometry3D _mesh;
		private readonly ImageBrush _brush;
		private readonly AxisAngleRotation3D _rotation;
		private readonly ScaleTransform3D _scale;
		private readonly TranslateTransform3D _translation;
		private Rect _lastBounds = Rect.Empty;

		public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(TiltedImage),
			new FrameworkPropertyMetadata(null, (d, _) => ((TiltedImage)d).OnSourceChanged()));

		public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(TiltedImage),
			new FrameworkPropertyMetadata(28.0, (d, _) => ((TiltedImage)d).OnGeometryChanged()));

		public static readonly DependencyProperty IsHighlightedProperty = DependencyProperty.Register(nameof(IsHighlighted), typeof(bool), typeof(TiltedImage),
			new PropertyMetadata(false, (d, _) => ((TiltedImage)d).AnimateHighlight()));

		public static readonly DependencyProperty StackIndexProperty = DependencyProperty.Register(nameof(StackIndex), typeof(int), typeof(TiltedImage),
			new PropertyMetadata(0, (d, _) => ((TiltedImage)d).OnGeometryChanged()));

		/// <summary>
		/// The position of the viewer's eye: X and Y relative to the window the images are shown in,
		/// Z is the distance to the screen. All in device independent units. Inherited, so it can be set once on the window.
		/// </summary>
		public static readonly DependencyProperty EyeProperty = DependencyProperty.RegisterAttached("Eye", typeof(Point3D), typeof(TiltedImage),
			new FrameworkPropertyMetadata(new Point3D(1000, 600, 1000), FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as TiltedImage)?.OnGeometryChanged()));

		public static Point3D GetEye(DependencyObject element) => (Point3D)element.GetValue(EyeProperty);

		public static void SetEye(DependencyObject element, Point3D value) => element.SetValue(EyeProperty, value);

		public TiltedImage()
		{
			_mesh = new MeshGeometry3D
			{
				TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
				TextureCoordinates = new PointCollection { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) }
			};

			_brush = new ImageBrush { Stretch = Stretch.Fill };
			_scale = new ScaleTransform3D(1, 1, 1);
			_rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), Angle);
			_translation = new TranslateTransform3D();

			// the image is centered at the origin, sized, turned and then moved to its place in front of the eye
			var transform = new Transform3DGroup();
			transform.Children.Add(_scale);
			transform.Children.Add(new RotateTransform3D(_rotation));
			transform.Children.Add(_translation);

			// the back gets the picture as well, so the card is drawn no matter which side the renderer considers the front
			var material = new DiffuseMaterial(_brush);
			var model = new GeometryModel3D(_mesh, material) { BackMaterial = material, Transform = transform };

			var scene = new Model3DGroup();
			scene.Children.Add(new AmbientLight(Colors.White));
			scene.Children.Add(model);

			_camera = new MatrixCamera(Matrix3D.Identity, Matrix3D.Identity);
			_viewport = new Viewport3D { Camera = _camera, ClipToBounds = false };
			_viewport.Children.Add(new ModelVisual3D { Content = scene });

			AddVisualChild(_viewport);

			// the projection depends on where the element is in the window, which can change without a new arrange of this element
			LayoutUpdated += (_, _) => UpdateGeometry();
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

		/// <summary>
		/// The position in a stack of images, 0 is the front one
		/// </summary>
		public int StackIndex
		{
			get => (int)GetValue(StackIndexProperty);
			set => SetValue(StackIndexProperty, value);
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
			var marginX = finalSize.Width * VIEWPORT_MARGIN;
			var marginY = finalSize.Height * VIEWPORT_MARGIN;
			_viewport.Arrange(new Rect(-marginX, -marginY, finalSize.Width + 2 * marginX, finalSize.Height + 2 * marginY));

			OnGeometryChanged();
			return finalSize;
		}

		private void OnSourceChanged()
		{
			_brush.ImageSource = Source;
			OnGeometryChanged();
		}

		private void OnGeometryChanged()
		{
			_lastBounds = Rect.Empty;
			UpdateGeometry();
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
		/// Places the image in front of the shared eye and sizes it to fit the bounds of the element.
		/// </summary>
		private void UpdateGeometry()
		{
			if (Source is null || Source.Width <= 0 || Source.Height <= 0 || RenderSize.Width <= 0 || RenderSize.Height <= 0)
				return;

			if (Window.GetWindow(this) is not Window window || !window.IsAncestorOf(this))
				return;

			var topLeft = TransformToAncestor(window).Transform(new Point(0, 0));
			var bounds = new Rect(topLeft, RenderSize);

			// LayoutUpdated is raised for every layout pass in the window, skip the work if nothing moved
			if (bounds == _lastBounds)
				return;

			_lastBounds = bounds;

			var eye = GetEye(this);
			var marginX = bounds.Width * VIEWPORT_MARGIN;
			var marginY = bounds.Height * VIEWPORT_MARGIN;
			var viewportBounds = new Rect(bounds.X - marginX, bounds.Y - marginY, bounds.Width + 2 * marginX, bounds.Height + 2 * marginY);

			_camera.ProjectionMatrix = CreateProjection(viewportBounds, eye);

			// the front image of the stack fills the element, the ones behind it have the same size but are further away
			var center = new Point3D(bounds.X + bounds.Width / 2, -(bounds.Y + bounds.Height / 2), 0);
			var width = FitWidth(bounds, center, Source.Height / Source.Width, eye);
			var halfWidth = width / 2;
			var halfHeight = width * Source.Height / Source.Width / 2;

			_mesh.Positions = new Point3DCollection
			{
				new Point3D(-halfWidth, -halfHeight, 0),
				new Point3D(halfWidth, -halfHeight, 0),
				new Point3D(halfWidth, halfHeight, 0),
				new Point3D(-halfWidth, halfHeight, 0)
			};

			_translation.OffsetX = center.X - StackIndex * STACK_SHIFT;
			_translation.OffsetY = center.Y;
			_translation.OffsetZ = -StackIndex * STACK_DEPTH;
		}

		/// <summary>
		/// The largest width an image with the given aspect ratio can have to stay within the bounds at all angles it can take
		/// </summary>
		private double FitWidth(Rect bounds, Point3D center, double aspect, Point3D eye)
		{
			double low = 0, high = bounds.Width * 2;

			for (var i = 0; i < 24; i++)
			{
				var width = (low + high) / 2;
				var fits = Fits(bounds, center, width, aspect, Angle, 1.0, eye) && Fits(bounds, center, width, aspect, HIGHLIGHT_ANGLE, HIGHLIGHT_SCALE, eye);

				if (fits)
					low = width;
				else
					high = width;
			}

			return low;
		}

		private static bool Fits(Rect bounds, Point3D center, double width, double aspect, double angle, double scale, Point3D eye)
		{
			var radians = angle * Math.PI / 180;
			var halfWidth = width / 2 * scale;
			var halfHeight = width * aspect / 2 * scale;

			foreach (var x in new[] { -halfWidth, halfWidth })
			{
				foreach (var y in new[] { -halfHeight, halfHeight })
				{
					// turning around the y axis moves the right edge (positive x) away from the eye for positive angles
					var point = new Point3D(center.X + x * Math.Cos(radians), center.Y + y, center.Z - x * Math.Sin(radians));
					var projected = Project(point, eye);

					if (!bounds.Contains(projected))
						return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Projects a point onto the screen as seen from the eye. 3D coordinates have Y pointing up, window coordinates have it pointing down.
		/// </summary>
		private static Point Project(Point3D point, Point3D eye)
		{
			var factor = eye.Z / (eye.Z - point.Z);
			var x = eye.X + (point.X - eye.X) * factor;
			var y = eye.Y - (point.Y + eye.Y) * factor;
			return new Point(x, y);
		}

		/// <summary>
		/// A projection for the eye looking straight at the screen, mapped to the part of the screen covered by the viewport.
		/// It is the same projection for all images, just cut to each viewport (an off-axis projection),
		/// which gives all images the same vanishing point.
		/// The depth stays between 0 and 1 and grows with the distance from the eye like with WPF's own cameras.
		/// It must not be proportional to w, which would make the matrix impossible to invert for hit testing.
		/// </summary>
		private static Matrix3D CreateProjection(Rect viewport, Point3D eye)
		{
			var left = 2 * viewport.X / viewport.Width + 1;
			var top = 2 * viewport.Y / viewport.Height + 1;
			var distance = eye.Z;

			return new Matrix3D(
				2 / viewport.Width, 0, 0, 0,
				0, 2 / viewport.Height, 0, 0,
				-2 * eye.X / (viewport.Width * distance) + left / distance, 2 * eye.Y / (viewport.Height * distance) - top / distance, -0.75 / distance, -1 / distance,
				-left, top, 0.5, 1);
		}
	}
}
