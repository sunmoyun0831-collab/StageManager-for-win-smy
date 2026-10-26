using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Interop;
using StageManager.Native.Window;
using StageManager.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private const int TIMERINTERVAL_MILLISECONDS = 500;
		private const int MAX_SCENES = 6;
		private const string APP_NAME = "StageManager";
		private const int EDGE_TRIGGER_WIDTH = 44;
		private const int EDGE_DWELL_MILLISECONDS = 300;
		private IntPtr _thisHandle;
		private TaskPoolGlobalHook _hook;
		private WindowMode _mode;
		private int _modeVersion;
		private DispatcherTimer _overlapCheckTimer;
		private DispatcherTimer _edgeDwellTimer;
		private Point _mouse = new Point(0, 0);
		private volatile bool _mouseAtEdge;
		private Rect _sidebarZone;
		private Rect _edgeZone;
		private bool _hasScreenLeftOfSidebar;
		private SceneModel _removedCurrentScene;
		private SceneModel? _mouseDownScene;
		private volatile bool _isPullingScene;
		private IWindow? _draggedWindow;
		private Size _draggedWindowSize;
		private volatile bool _isDraggingWindow;
		private int _dragFeedbackPending;

		public static readonly DependencyProperty DropHintProperty = DependencyProperty.Register(nameof(DropHint), typeof(string), typeof(MainWindow));

		public MainWindow()
		{
			InitializeComponent();

			DataContext = this;

			// runs on the UI thread, the scenes must not be read while they are changed
			_overlapCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TIMERINTERVAL_MILLISECONDS) };
			_overlapCheckTimer.Tick += (_, _) => OverlapCheck();

			_edgeDwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EDGE_DWELL_MILLISECONDS) };
			_edgeDwellTimer.Tick += EdgeDwellTimer_Tick;

			DpiChanged += (_, _) => Dispatcher.BeginInvoke(UpdateSidebarGeometry);
			Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

			SwitchSceneCommand = new ActionCommand(model => OnSceneClicked((SceneModel)model));
		}

		protected override void OnInitialized(EventArgs e)
		{
			base.OnInitialized(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

			StartHook();	
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			UpdateSidebarGeometry();
		}

		private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(UpdateSidebarGeometry);
		}

		protected override void OnClosed(EventArgs e)
		{
			StopHook();

			trayIcon.Dispose();

			_overlapCheckTimer.Stop();
			Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
			SceneManager?.Stop();
			App.RestoreState = null;

			base.OnClosed(e);

			Environment.Exit(0);
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

			var windowsManager = new WindowsManager();
			SceneManager = new SceneManager(windowsManager, SettingsStore.CreateDefault());
			App.RestoreState = SceneManager.Stop;
			await SceneManager.Start().ConfigureAwait(true);

			SceneManager.SceneChanged += SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged += SceneManager_CurrentSceneSelectionChanged;
			SceneManager.WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;

			AddInitialScenes();

			var foreground = Win32.GetForegroundWindow();
			var foregroundScene = SceneManager.FindSceneForWindow(foreground);
			if (foregroundScene is object)
				await SceneManager.SwitchTo(foregroundScene).ConfigureAwait(true);

			_overlapCheckTimer.Start();
		}

		private void AddInitialScenes()
		{
			var initialScenes = SceneManager.GetScenes().ToArray();
			for (int i = 0; i < initialScenes.Length; i++)
			{
				var model = SceneModel.FromScene(initialScenes[i]);
				model.IsVisible = i <= MAX_SCENES; // i is zero based, so it should be i+1 but one scene gets selected (and removed from the sidebar) that makes i+0 again
				Scenes.Add(model);
			}
		}

		private void SceneManager_CurrentSceneSelectionChanged(object? sender, CurrentSceneSelectionChangedEventArgs args)
		{
			var currentModel = args.Current is null ? null : Scenes.FirstOrDefault(m => m.Id == args.Current.Id);

			if (currentModel is object)
			{
				var currentIndex = Scenes.IndexOf(currentModel);
				Scenes.RemoveAt(currentIndex);

				if (_removedCurrentScene is object)
					Scenes.Insert(currentIndex, _removedCurrentScene);
			}
			else
			{
				if (_removedCurrentScene is object)
					Scenes.Add(_removedCurrentScene);
			}

			_removedCurrentScene = currentModel;

			SyncVisibilityByUpdatedTimeStamp();
		}

		/// <summary>
		/// The sidebar sits on the left edge of the primary screen
		/// </summary>
		private static System.Windows.Forms.Screen SidebarScreen => System.Windows.Forms.Screen.PrimaryScreen!;

		/// <summary>
		/// Places the sidebar on its screen and caches the areas used to check for windows and the mouse.
		/// Screen coordinates are physical pixels (the app is per monitor DPI aware), the window itself is placed in device independent units.
		/// </summary>
		private void UpdateSidebarGeometry()
		{
			var screen = SidebarScreen;
			var workArea = screen.WorkingArea;
			var dpi = VisualTreeHelper.GetDpi(this);

			_sidebarZone = new Rect(workArea.Left, workArea.Top, Width * dpi.DpiScaleX, workArea.Height);
			_edgeZone = new Rect(workArea.Left, workArea.Top, EDGE_TRIGGER_WIDTH * dpi.DpiScaleX, workArea.Height);
			_hasScreenLeftOfSidebar = System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Right <= screen.Bounds.Left && s.Bounds.Bottom > workArea.Top && s.Bounds.Top < workArea.Bottom);

			Top = workArea.Top / dpi.DpiScaleY;
			Height = workArea.Height / dpi.DpiScaleY;

			if (Mode != WindowMode.OffScreen)
			{
				BeginAnimation(LeftProperty, null);
				Left = OnScreenLeft;
			}
		}

		private double OnScreenLeft => _sidebarZone.Left / VisualTreeHelper.GetDpi(this).DpiScaleX;

		private void SceneManager_SceneChanged(object sender, SceneChangedEventArgs e)
		{
			this.Dispatcher.Invoke(() =>
			{
				switch (e.Change)
				{
					case ChangeType.Created:
						Scenes.Add(SceneModel.FromScene(e.Scene));
						SyncVisibilityByUpdatedTimeStamp();
						break;
					case ChangeType.Updated:
						if (AllScenes.FirstOrDefault(s => s.Id == e.Scene.Id) is SceneModel toUpdate)
							toUpdate.UpdateFromScene(e.Scene);
						break;
					case ChangeType.Removed:
						if (AllScenes.FirstOrDefault(s => s.Id == e.Scene.Id) is SceneModel toRemove)
						{
							if (toRemove.Equals(_removedCurrentScene))
								_removedCurrentScene = null;
							else
								Scenes.Remove(toRemove);
						}
						SyncVisibilityByUpdatedTimeStamp();
						break;
				}
			});
		}

		private void OnSceneClicked(SceneModel model)
		{
			if (SceneManager is null)
				return;

			// shift click adds the apps of the scene to the current one, like on macOS
			if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0)
				SceneManager.AddToCurrentScene(model.Scene);
			else
				SceneManager.SwitchTo(model.Scene).SafeFireAndForget();
		}

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			if (e.Data.Button != SharpHook.Native.MouseButton.Button1)
				return;

			var screenPoint = new Point(e.Data.X, e.Data.Y);
			Dispatcher.BeginInvoke(() =>
			{
				_mouseDownScene = IsSidebarAt(screenPoint) ? FindSceneByPoint(screenPoint) : null;
				_isPullingScene = _mouseDownScene is not null;
			});
		}

		private void OnMouseReleased(object? sender, MouseHookEventArgs e)
		{
			if (e.Data.Button != SharpHook.Native.MouseButton.Button1)
				return;

			var screenPoint = new Point(e.Data.X, e.Data.Y);
			Dispatcher.BeginInvoke(() =>
			{
				var pulledScene = _mouseDownScene;
				_mouseDownScene = null;
				_isPullingScene = false;

				if (pulledScene is null)
					return;

				SetDropFeedback(null, null);

				// dragging a scene out of the sidebar onto the stage adds its apps to the current scene
				if (screenPoint.X >= _sidebarZone.Right)
					SceneManager?.AddToCurrentScene(pulledScene.Scene);
			});
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (type == WindowUpdateType.MoveStart)
				OnWindowDragStarted(window);
			else if (type == WindowUpdateType.MoveEnd)
				OnWindowDragEnded(window);
		}

		private void OnWindowDragStarted(IWindow window)
		{
			// only windows on the stage can be dragged into the sidebar, pinned windows are on no stage
			if (SceneManager?.CurrentScene is not Scene current || !current.Windows.Contains(window))
				return;

			var location = window.Location;
			_draggedWindow = window;
			_draggedWindowSize = new Size(location.Width, location.Height);
			_isDraggingWindow = true;
		}

		/// <summary>
		/// A window of the current scene dropped on another scene moves its app over there,
		/// dropped anywhere else on the sidebar it takes its app out of the current scene.
		/// </summary>
		private void OnWindowDragEnded(IWindow window)
		{
			if (_draggedWindow != window)
				return;

			_draggedWindow = null;
			_isDraggingWindow = false;
			SetDropFeedback(null, null);

			// resizing (and snapping to a screen edge) also starts a move, only plain moves can drop windows
			var location = window.Location;
			if (location.Width != _draggedWindowSize.Width || location.Height != _draggedWindowSize.Height)
				return;

			if (SceneManager is null || !IsVisible || !Win32.GetCursorPos(out var cursor))
				return;

			var point = new Point(cursor.X, cursor.Y);
			if (!_sidebarZone.Contains(point) || SceneManager.FindSceneForWindow(window) is not Scene scene)
				return;

			var target = FindSceneByPoint(point);
			if (target is null && !SceneManager.HasSeveralApps(scene))
				return;

			// put the window back to where the drag started, so that it is there again when its scene is shown
			if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
				Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y, 0, 0, Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);

			if (target is not null)
				SceneManager.MoveAppToScene(window, target.Scene);
			else
				SceneManager.SeparateApp(window);
		}

		private void UpdateDragFeedback()
		{
			_dragFeedbackPending = 0;

			if (SceneManager is null)
				return;

			var point = _mouse;
			SceneModel? target = null;
			string? hint = null;

			if (_draggedWindow is IWindow window)
			{
				if (IsVisible && _sidebarZone.Contains(point))
				{
					target = FindSceneByPoint(point);

					if (target is not null)
						hint = $"Move {window.AppName} to this stage";
					else if (SceneManager.CurrentScene is Scene current && SceneManager.HasSeveralApps(current))
						hint = $"Drop to take {window.AppName} out of this stage";
				}
			}
			else if (_mouseDownScene is SceneModel pulled)
			{
				target = pulled;

				if (point.X >= _sidebarZone.Right)
					hint = SceneManager.CurrentScene is null ? "Release to show this stage" : "Release to add this to the current stage";
			}

			SetDropFeedback(target, hint);
		}

		private void SetDropFeedback(SceneModel? target, string? hint)
		{
			foreach (var scene in Scenes)
				scene.IsDropTarget = scene.Equals(target);

			DropHint = hint;
		}

		private bool IsSidebarAt(Point screenPoint)
		{
			var handle = Win32.WindowFromPoint(new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y));
			return handle != IntPtr.Zero && Win32.GetAncestor(handle, Win32.GA.GA_ROOT) == _thisHandle;
		}

		/// <param name="p">A point in physical screen coordinates</param>
		private SceneModel FindSceneByPoint(Point p)
		{
			if (!IsVisible)
				return null;

			var pointOnWindow = PointFromScreen(p);

			SceneModel model = null;

			var element = VisualTreeHelper.HitTest(this, pointOnWindow)?.VisualHit;

			while (element is not null)
			{
				if (element is FrameworkElement { DataContext: SceneModel m })
				{
					model = m;
					break;
				}

				element = element.GetParentObject();
			}

			return model;
		}
		private void SyncVisibilityByUpdatedTimeStamp()
		{
			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();
			for (int i = 0; i < scenes.Length; i++)
				scenes[i].IsVisible = i < MAX_SCENES;
		}

		public ObservableCollection<SceneModel> Scenes { get; } = new ObservableCollection<SceneModel>();

		/// <summary>
		/// The scenes in the sidebar plus the current one, which is not shown in the sidebar
		/// </summary>
		public IEnumerable<SceneModel> AllScenes => _removedCurrentScene is null ? Scenes : Scenes.Append(_removedCurrentScene);

		public ICommand SwitchSceneCommand { get; }

		/// <summary>
		/// Explains what happens when the thing dragged right now is dropped, null hides the hint
		/// </summary>
		public string? DropHint
		{
			get => (string?)GetValue(DropHintProperty);
			set => SetValue(DropHintProperty, value);
		}

		public SceneManager SceneManager { get; private set; }

		public IntPtr Handle => _thisHandle;

		public WindowMode Mode
		{
			get => _mode;
			set
			{
				if (value == _mode)
					return;

				_mode = value;

				this.Topmost = value == WindowMode.Flyover;

				ApplyWindowMode();
			}
		}

		/// <summary>
		/// Slides the sidebar in or out. Once out, the window is hidden as "out" would be on top of another screen left of the sidebar.
		/// </summary>
		private void ApplyWindowMode()
		{
			var version = ++_modeVersion;
			var isOffScreen = Mode == WindowMode.OffScreen;

			if (!isOffScreen && !IsVisible)
				Show();

			// sliding out to the left would move the sidebar over the other screen, so just hide and show it there
			if (_hasScreenLeftOfSidebar)
			{
				BeginAnimation(LeftProperty, null);
				Left = OnScreenLeft;

				if (isOffScreen)
					Hide();

				return;
			}

			var newLeft = isOffScreen ? OnScreenLeft - Width : OnScreenLeft;
			if (Left == newLeft)
				return;

			var easingMode = isOffScreen ? EasingMode.EaseIn : EasingMode.EaseOut;

			var animation = new DoubleAnimationUsingKeyFrames();
			animation.Duration = new Duration(TimeSpan.FromSeconds(0.5));
			var easingFunction = new PowerEase { EasingMode = easingMode };
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(Left, KeyTime.FromPercent(0)));
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(newLeft, KeyTime.FromPercent(1.0), easingFunction));
			animation.Completed += (_, _) =>
			{
				if (version == _modeVersion && Mode == WindowMode.OffScreen)
					Hide();
			};

			BeginAnimation(LeftProperty, animation);
		}

		private void StartHook()
		{
			_hook = new TaskPoolGlobalHook();

			_hook.MousePressed += OnMousePressed;
			_hook.MouseReleased += OnMouseReleased;
			_hook.MouseMoved += _hook_MouseMoved;
			_hook.MouseDragged += _hook_MouseMoved;

			Task.Run(_hook.Run);
		}

		private void StopHook()
		{
			_hook.MousePressed -= OnMousePressed;
			_hook.MouseReleased -= OnMouseReleased;
			_hook.MouseMoved -= _hook_MouseMoved;
			_hook.MouseDragged -= _hook_MouseMoved;

			try
			{
				_hook.Dispose();
			}
			catch (HookException)
			{
			}
		}

		/// <summary>
		/// Called on a thread pool thread for every mouse move, also while dragging (windows, files, ...)
		/// </summary>
		private void _hook_MouseMoved(object? sender, MouseHookEventArgs e)
		{
			_mouse = new Point(e.Data.X, e.Data.Y);

			if ((_isDraggingWindow || _isPullingScene) && Interlocked.Exchange(ref _dragFeedbackPending, 1) == 0)
				Dispatcher.BeginInvoke(UpdateDragFeedback);

			var atEdge = _edgeZone.Contains(_mouse);
			if (atEdge == _mouseAtEdge)
				return;

			_mouseAtEdge = atEdge;
			Dispatcher.BeginInvoke(() => OnMouseAtEdgeChanged(atEdge));
		}

		private void OnMouseAtEdgeChanged(bool atEdge)
		{
			if (!atEdge)
			{
				_edgeDwellTimer.Stop();
				return;
			}

			if (Mode != WindowMode.OffScreen)
				return;

			// with another screen to the left, the mouse crosses the edge whenever it moves between the screens.
			// Only fly in if it rests at the edge for a moment then.
			if (_hasScreenLeftOfSidebar)
				_edgeDwellTimer.Start();
			else
				Mode = WindowMode.Flyover;
		}

		private void EdgeDwellTimer_Tick(object? sender, EventArgs e)
		{
			_edgeDwellTimer.Stop();

			if (_mouseAtEdge && Mode == WindowMode.OffScreen)
				Mode = WindowMode.Flyover;
		}

		private void OverlapCheck()
		{
			if (SceneManager is null)
				return;

			UpdateModeByWindows(SceneManager.GetVisibleWindows());
		}

		private bool OverlapsSidebar(IWindow window)
		{
			if (window.IsMinimized)
				return false;

			var bounds = window.VisibleBounds;
			return bounds.Left < _sidebarZone.Right && bounds.Right > _sidebarZone.Left
				&& bounds.Top < _sidebarZone.Bottom && bounds.Bottom > _sidebarZone.Top;
		}

		private void UpdateModeByWindows(IEnumerable<IWindow> windows)
		{
			var anyOverlappingWindows = windows.Any(OverlapsSidebar);

			var containsMouse = _sidebarZone.Contains(_mouse);
			var setMode = Mode == WindowMode.OnScreen && !containsMouse
							|| Mode == WindowMode.OffScreen
							|| (Mode == WindowMode.Flyover && !containsMouse);

			if (setMode)
				Mode = anyOverlappingWindows ? WindowMode.OffScreen : WindowMode.OnScreen;
		}

		private void NavigateToProjectPage()
		{
			Process.Start(new ProcessStartInfo("https://github.com/awaescher/StageManager")
			{
				UseShellExecute = true
			});
		}

		public static bool StartsWithWindows
		{ 
			get => AutoStart.IsStartup(APP_NAME);
			set => AutoStart.SetStartup(APP_NAME, value);
		}

		private void MenuItem_ProjectPage_Click(object sender, RoutedEventArgs e)
		{
			NavigateToProjectPage();
		}

		private void MenuItem_Quit_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void ContextMenu_Closed(object sender, RoutedEventArgs e)
		{
			StartHook();
		}

		private void ContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			StopHook();
			FillPinnedAppsMenu();
		}

		private void SceneContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			StopHook();
		}

		private void FillPinnedAppsMenu()
		{
			pinnedAppsMenu.Items.Clear();

			if (SceneManager is null)
				return;

			pinnedAppsMenu.Items.Add(new MenuItem { Header = "Pinned apps are never hidden and don't switch scenes", IsEnabled = false });
			pinnedAppsMenu.Items.Add(new Separator());

			foreach (var app in SceneManager.GetApps())
			{
				var item = new MenuItem { Header = app.Name, ToolTip = app.Key, IsCheckable = true, IsChecked = app.IsPinned };
				item.Click += (_, _) => SceneManager.SetPinned(app.Key, item.IsChecked);
				pinnedAppsMenu.Items.Add(item);
			}
		}

		private void Scene_ContextMenuOpening(object sender, ContextMenuEventArgs e)
		{
			if (SceneManager is null || sender is not FrameworkElement { DataContext: SceneModel model, ContextMenu: ContextMenu menu })
			{
				e.Handled = true;
				return;
			}

			menu.Items.Clear();

			var apps = SceneManager.GetApps(model.Scene).ToArray();

			if (SceneManager.CurrentScene is not null)
			{
				var add = new MenuItem { Header = "Add to current stage (Shift+Click)" };
				add.Click += (_, _) => SceneManager.AddToCurrentScene(model.Scene);
				menu.Items.Add(add);
			}

			if (apps.Length > 1)
			{
				foreach (var app in apps)
				{
					var separate = new MenuItem { Header = $"Take {app.Name} out of this stage" };
					separate.Click += (_, _) => SceneManager.SeparateApp(model.Scene, app.Key);
					menu.Items.Add(separate);
				}
			}

			if (menu.Items.Count > 0)
				menu.Items.Add(new Separator());

			foreach (var app in apps)
			{
				var pin = new MenuItem { Header = $"Pin {app.Name} (always visible)" };
				pin.Click += (_, _) => SceneManager.SetPinned(app.Key, true);
				menu.Items.Add(pin);
			}
		}
	}

	public enum WindowMode
	{
		OnScreen,
		OffScreen,
		Flyover
	}
}
