using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using SharpHook.Native;
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
using System.Windows.Media.Media3D;
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

		/// <summary>
		/// How far the eye looking at the tilted sidebar cards is away from the screen, relative to the screen width.
		/// Smaller values give a stronger perspective.
		/// </summary>
		private const double EYE_DISTANCE_FACTOR = 0.5;
		private const int DRAG_HOVER_MILLISECONDS = 600;
		private const int QUIT_TIMEOUT_MILLISECONDS = 3000;

		/// <summary>
		/// How often windows that are gone without Windows telling are forgotten, and how many of those rounds pass between resource logs
		/// </summary>
		private const int MAINTENANCE_MINUTES = 1;
		private const int RESOURCE_LOG_ROUNDS = 5;
		private const string PROJECT_PAGE = "https://github.com/sunmoyun0831-collab/smy-room";
		private IntPtr _thisHandle;
		private SimpleGlobalHook? _hook;
		private volatile bool _isMenuOpen;
		private readonly UserActivity _userActivity = new UserActivity();
		private WindowMode _mode;
		private int _modeVersion;
		private DispatcherTimer _overlapCheckTimer;
		private DispatcherTimer _edgeDwellTimer;
		private DispatcherTimer _maintenanceTimer;
		private int _maintenanceRounds;
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
		private bool _isQuitting;
		private volatile string _quitStep = "";
		private DispatcherTimer _dragHoverTimer;
		private SceneModel? _dragHoverScene;

		public static readonly DependencyProperty DropHintProperty = DependencyProperty.Register(nameof(DropHint), typeof(string), typeof(MainWindow));

		public MainWindow()
		{
			InitializeComponent();

			DataContext = this;
			titleMenuItem.Header = $"Stage Manager {GetType().Assembly.GetName().Version?.ToString(3)}";

			// runs on the UI thread, the scenes must not be read while they are changed
			_overlapCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TIMERINTERVAL_MILLISECONDS) };
			_overlapCheckTimer.Tick += (_, _) => OverlapCheck();

			_edgeDwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EDGE_DWELL_MILLISECONDS) };
			_edgeDwellTimer.Tick += EdgeDwellTimer_Tick;

			_dragHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DRAG_HOVER_MILLISECONDS) };
			_dragHoverTimer.Tick += DragHoverTimer_Tick;

			_maintenanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(MAINTENANCE_MINUTES) };
			_maintenanceTimer.Tick += MaintenanceTimer_Tick;

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
			_isQuitting = true;
			StartQuitWatchdog();

			// bring back the windows first, the rest matters less if anything goes wrong
			RunQuitStep("restoring windows", () => SceneManager?.Stop());
			App.RestoreState = null;

			RunQuitStep("stopping the mouse hook", StopHook);
			RunQuitStep("removing the tray icon", trayIcon.Dispose);

			_overlapCheckTimer.Stop();
			_maintenanceTimer.Stop();
			Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

			base.OnClosed(e);

			_quitStep = "exiting";
			Environment.Exit(0);
		}

		/// <summary>
		/// Cleaning up talks to other apps and Explorer, which can hang.
		/// Whatever happens, quitting must not leave the process behind.
		/// </summary>
		private void StartQuitWatchdog()
		{
			var watchdog = new Thread(() =>
			{
				Thread.Sleep(QUIT_TIMEOUT_MILLISECONDS);
				App.Log(new TimeoutException($"Quitting got stuck while {_quitStep}, ending the process"));
				Process.GetCurrentProcess().Kill();
			})
			{
				IsBackground = true,
				Name = "QuitWatchdog"
			};

			watchdog.Start();
		}

		private void RunQuitStep(string step, Action action)
		{
			_quitStep = step;

			try
			{
				action();
			}
			catch (Exception ex)
			{
				App.Log(ex);
			}
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

			var windowsManager = new WindowsManager();
			SceneManager = new SceneManager(windowsManager, SettingsStore.CreateDefault(), new StageAnimator(FindCard), _userActivity);
			App.RestoreState = SceneManager.Stop;
			await SceneManager.Start().ConfigureAwait(true);

			SceneManager.SceneChanged += SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged += SceneManager_CurrentSceneSelectionChanged;
			SceneManager.AttentionRequested += SceneManager_AttentionRequested;
			SceneManager.WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowSnapshots.Updated += WindowSnapshots_Updated;

			AddInitialScenes();

			// start with the scene of the active window, or of the top window if the active one is in no scene (like a pinned one).
			// Minimized windows are left alone, if all of them are minimized there is no scene on stage.
			var foregroundScene = SceneManager.FindSceneForWindow(Win32.GetForegroundWindow());
			if (foregroundScene is null)
			{
				var shownWindows = SceneManager.GetScenes().SelectMany(s => s.Windows).Where(w => !w.IsMinimized).ToArray();
				foregroundScene = SceneManager.FindSceneForWindow(ZOrder.Top(shownWindows.Select(w => w.Handle)));
			}

			// nothing to fly to yet, the sidebar is just being filled
			if (foregroundScene is object)
				await SceneManager.SwitchTo(foregroundScene, animate: false).ConfigureAwait(true);

			_overlapCheckTimer.Start();
			_maintenanceTimer.Start();
		}

		/// <summary>
		/// Stage Manager runs for days, nothing may pile up meanwhile
		/// </summary>
		private void MaintenanceTimer_Tick(object? sender, EventArgs e)
		{
			if (SceneManager is null)
				return;

			SceneManager.WindowsManager.ForgetClosedWindows();
			WindowSnapshots.ForgetClosedWindows();

			if (++_maintenanceRounds % RESOURCE_LOG_ROUNDS == 0)
				DebugLog.WriteResources(SceneManager.WindowsManager.Windows.Count());
		}

		private void WindowSnapshots_Updated(IntPtr handle)
		{
			foreach (var window in AllScenes.SelectMany(s => s.Windows).Where(w => w.Handle == handle))
				window.RefreshSnapshot();
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

		private void SceneManager_AttentionRequested(object? sender, Scene scene)
		{
			if (AllScenes.FirstOrDefault(m => m.Id == scene.Id) is SceneModel model)
				model.NeedsAttention = true;
		}

		private void SceneManager_CurrentSceneSelectionChanged(object? sender, CurrentSceneSelectionChangedEventArgs args)
		{
			var currentModel = args.Current is null ? null : Scenes.FirstOrDefault(m => m.Id == args.Current.Id);

			// the user went to see what the app wanted
			if (currentModel is not null)
				currentModel.NeedsAttention = false;

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

			// all cards are seen from one eye in front of the center of the screen, relative to the sidebar on screen
			var bounds = screen.Bounds;
			var eye = new Point3D(
				(bounds.Left + bounds.Width / 2.0 - workArea.Left) / dpi.DpiScaleX,
				(bounds.Top + bounds.Height / 2.0 - workArea.Top) / dpi.DpiScaleY,
				bounds.Width / dpi.DpiScaleX * EYE_DISTANCE_FACTOR);
			TiltedImage.SetEye(this, eye);

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
			{
				SceneManager.AddToCurrentScene(model.Scene);
				return;
			}

			SceneManager.SwitchTo(model.Scene).SafeFireAndForget();

			// the click activated the sidebar, hand the focus to the scene's front window so typing goes there
			var top = ZOrder.Top(model.Scene.Windows.Select(w => w.Handle));
			model.Scene.Windows.FirstOrDefault(w => w.Handle == top)?.Focus();
		}

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			_userActivity.Acted();

			if (e.Data.Button != SharpHook.Native.MouseButton.Button1 || _isMenuOpen)
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
			_userActivity.Acted();

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

		/// <summary>
		/// Files (or anything else) dragged over a scene switch to it after a moment, so they can be dropped on the windows of that scene.
		/// Nothing can be dropped on the sidebar itself.
		/// </summary>
		private void Window_DragOver(object sender, DragEventArgs e)
		{
			e.Effects = DragDropEffects.None;
			e.Handled = true;

			var hovered = FindSceneAt(e.GetPosition(this));
			if (ReferenceEquals(hovered, _dragHoverScene))
				return;

			_dragHoverScene = hovered;
			_dragHoverTimer.Stop();

			if (hovered is null)
			{
				SetDropFeedback(null, null);
				return;
			}

			SetDropFeedback(hovered, "Keep holding to open this stage");
			_dragHoverTimer.Start();
		}

		private void Window_Drop(object sender, DragEventArgs e)
		{
			e.Effects = DragDropEffects.None;
			e.Handled = true;

			ResetDragHover();
		}

		private void DragHoverTimer_Tick(object? sender, EventArgs e)
		{
			var hovered = _dragHoverScene;
			ResetDragHover();

			if (hovered is null || SceneManager is null || !Win32.GetCursorPos(out var cursor))
				return;

			// there is no reliable event when a drag leaves the sidebar, so check that it is still there
			if (!ReferenceEquals(FindSceneByPoint(new Point(cursor.X, cursor.Y)), hovered))
				return;

			SceneManager.SwitchTo(hovered.Scene).SafeFireAndForget();
		}

		private void ResetDragHover()
		{
			_dragHoverTimer.Stop();
			_dragHoverScene = null;
			SetDropFeedback(null, null);
		}

		/// <param name="p">A point in physical screen coordinates</param>
		private SceneModel? FindSceneByPoint(Point p)
		{
			if (!IsVisible)
				return null;

			return FindSceneAt(PointFromScreen(p));
		}

		/// <param name="pointOnWindow">A point relative to the sidebar window in device independent units</param>
		private SceneModel? FindSceneAt(Point pointOnWindow)
		{
			SceneModel? model = null;

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

		/// <summary>
		/// One hook for the whole time Stage Manager runs, on a thread of its own. Starting and stopping it for every menu
		/// could leave hooks behind that kept slowing down the mouse. The handlers run on the hook thread and must be quick,
		/// they only take note and hand the work to the UI thread.
		/// </summary>
		private void StartHook()
		{
			var hook = new SimpleGlobalHook();
			_hook = hook;

			hook.MousePressed += OnMousePressed;
			hook.MouseReleased += OnMouseReleased;
			hook.MouseMoved += _hook_MouseMoved;
			hook.MouseDragged += _hook_MouseMoved;
			hook.KeyPressed += OnKeyChanged;
			hook.KeyReleased += OnKeyChanged;

			var thread = new Thread(() =>
			{
				try
				{
					hook.Run();
				}
				catch (Exception ex) when (ex is HookException or ObjectDisposedException)
				{
					App.Log(ex);
				}
			})
			{
				IsBackground = true,
				Name = "MouseHook"
			};

			thread.Start();
		}

		private void StopHook()
		{
			if (_hook is not SimpleGlobalHook hook)
				return;

			_hook = null;

			try
			{
				hook.Dispose();
			}
			catch (HookException ex)
			{
				// happens when the hook did not run (yet), nothing to stop then
				DebugLog.Write($"Stopping the mouse hook failed: {ex.Message}");
			}
		}

		/// <summary>
		/// Alt+Tab and the Windows key switch windows, see <see cref="UserActivity"/>. Other keys are not looked at.
		/// </summary>
		private void OnKeyChanged(object? sender, KeyboardHookEventArgs e)
		{
			if (e.Data.KeyCode is KeyCode.VcLeftAlt or KeyCode.VcRightAlt or KeyCode.VcLeftMeta or KeyCode.VcRightMeta)
				_userActivity.Acted();
		}

		/// <summary>
		/// Called on the hook thread for every mouse move, also while dragging (windows, files, ...)
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

		/// <summary>
		/// Like on macOS, windows may reach into the sidebar, which then lies behind them to leave them the room.
		/// It only gets out of the way once a window covers more than half of the scene cards.
		/// </summary>
		private static bool OverlapsSidebar(IWindow window, Rect cards)
		{
			if (window.IsMinimized)
				return false;

			var bounds = window.VisibleBounds;
			var coveredWidth = Math.Min(bounds.Right, cards.Right) - Math.Max(bounds.Left, cards.Left);
			var coversVertically = bounds.Top < cards.Bottom && bounds.Bottom > cards.Top;

			return coversVertically && coveredWidth > cards.Width / 2;
		}

		/// <summary>
		/// The part of the screen the scene cards take when the sidebar is shown, in physical pixels
		/// </summary>
		private Rect GetCardsZone()
		{
			if (scenesControl.ActualWidth <= scenesControl.Padding.Left || scenesControl.ActualHeight <= 0)
				return _sidebarZone;

			var dpi = VisualTreeHelper.GetDpi(this);
			var cards = scenesControl.TransformToAncestor(this).TransformBounds(new Rect(scenesControl.RenderSize));
			var padding = scenesControl.Padding.Left;

			// relative to the sidebar's place on screen, as it may be sliding or hidden right now
			return new Rect(
				_sidebarZone.Left + (cards.Left + padding) * dpi.DpiScaleX,
				_sidebarZone.Top + cards.Top * dpi.DpiScaleY,
				(cards.Width - padding) * dpi.DpiScaleX,
				cards.Height * dpi.DpiScaleY);
		}

		/// <summary>
		/// Where a window is shown in the sidebar, in physical pixels. Windows without a card of their own, like the ones of the
		/// current scene, take the place of the given scene. If that has no card either, they go to the middle of the sidebar.
		/// </summary>
		private Rect FindCard(IntPtr window, Scene slot)
		{
			var dpi = VisualTreeHelper.GetDpi(this);

			var model = Scenes.FirstOrDefault(s => s.IsVisible && s.Windows.Any(w => w.Handle == window))
				?? Scenes.FirstOrDefault(s => s.IsVisible && s.Id == slot.Id);

			if (model is not null
				&& scenesControl.ItemContainerGenerator.ContainerFromItem(model) is FrameworkElement container
				&& FindFirst<Border>(container) is Border cards
				&& cards.ActualWidth > 0 && cards.ActualHeight > 0
				&& cards.IsDescendantOf(this))
			{
				// the windows are drawn inside the border of the cards
				var bounds = cards.TransformToAncestor(this).TransformBounds(new Rect(cards.RenderSize));
				var insetX = cards.BorderThickness.Left + cards.Padding.Left;
				var insetY = cards.BorderThickness.Top + cards.Padding.Top;

				if (bounds.Width > 2 * insetX && bounds.Height > 2 * insetY)
					bounds.Inflate(-insetX, -insetY);

				return ToScreen(bounds, dpi);
			}

			const double CARD_WIDTH = 200;
			const double CARD_HEIGHT = 130;
			var middle = new Rect(scenesControl.Padding.Left, (ActualHeight - CARD_HEIGHT) / 2, CARD_WIDTH, CARD_HEIGHT);
			return ToScreen(middle, dpi);
		}

		/// <summary>
		/// Converts bounds relative to the sidebar into physical screen coordinates.
		/// Relative to the sidebar's place on screen, as it may be sliding or hidden right now.
		/// </summary>
		private Rect ToScreen(Rect bounds, DpiScale dpi) => new Rect(
			_sidebarZone.Left + bounds.Left * dpi.DpiScaleX,
			_sidebarZone.Top + bounds.Top * dpi.DpiScaleY,
			bounds.Width * dpi.DpiScaleX,
			bounds.Height * dpi.DpiScaleY);

		private static T? FindFirst<T>(DependencyObject parent) where T : DependencyObject
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				var child = VisualTreeHelper.GetChild(parent, i);
				if (child is T match)
					return match;

				if (FindFirst<T>(child) is T descendant)
					return descendant;
			}

			return null;
		}

		private void UpdateModeByWindows(IEnumerable<IWindow> windows)
		{
			var cards = GetCardsZone();
			var anyOverlappingWindows = windows.Any(w => OverlapsSidebar(w, cards));

			var containsMouse = _sidebarZone.Contains(_mouse);
			var setMode = Mode == WindowMode.OnScreen && !containsMouse
							|| Mode == WindowMode.OffScreen
							|| (Mode == WindowMode.Flyover && !containsMouse);

			if (setMode)
				Mode = anyOverlappingWindows ? WindowMode.OffScreen : WindowMode.OnScreen;
		}

		private void NavigateToProjectPage()
		{
			Process.Start(new ProcessStartInfo(PROJECT_PAGE)
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
			_isMenuOpen = false;
		}

		private void ContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			_isMenuOpen = true;
			FillAppMenus();
			debugLogMenuItem.IsChecked = DebugLog.Enabled;
			animationsMenuItem.IsChecked = SceneManager?.Animations ?? true;
			appsSwitchStagesMenuItem.IsChecked = SceneManager?.AppsMaySwitchStages ?? false;
		}

		private void MenuItem_AppsSwitchStages_Click(object sender, RoutedEventArgs e)
		{
			if (SceneManager is not null)
				SceneManager.AppsMaySwitchStages = appsSwitchStagesMenuItem.IsChecked;
		}

		private void MenuItem_Animations_Click(object sender, RoutedEventArgs e)
		{
			if (SceneManager is not null)
				SceneManager.Animations = animationsMenuItem.IsChecked;
		}

		private void MenuItem_DebugLog_Click(object sender, RoutedEventArgs e)
		{
			DebugLog.Enabled = debugLogMenuItem.IsChecked;
			DebugLog.WriteResources(SceneManager?.WindowsManager.Windows.Count() ?? 0);

			// show the file when the log is stopped, that's when it gets sent
			if (!DebugLog.Enabled && System.IO.File.Exists(DebugLog.Path))
				Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{DebugLog.Path}\"") { UseShellExecute = true });
		}

		private void SceneContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			_isMenuOpen = true;
		}

		/// <summary>
		/// Lists the apps with a switch each, to pin them or to exclude them from Stage Manager
		/// </summary>
		private void FillAppMenus()
		{
			pinnedAppsMenu.Items.Clear();
			excludedAppsMenu.Items.Clear();

			if (SceneManager is null)
				return;

			pinnedAppsMenu.Items.Add(new MenuItem { Header = "Pinned apps stay visible on every stage and don't switch stages.", Style = (Style)FindResource("Menu.Caption") });
			excludedAppsMenu.Items.Add(new MenuItem { Header = "Stage Manager leaves excluded apps alone: their windows are neither hidden nor shown and are on no stage. Good for apps that run in the background.", Style = (Style)FindResource("Menu.Caption") });

			foreach (var app in SceneManager.GetApps())
			{
				var pin = new MenuItem { Header = app.Name, ToolTip = app.Key, IsCheckable = true, IsChecked = app.IsPinned, StaysOpenOnClick = true };
				var exclude = new MenuItem { Header = app.Name, ToolTip = app.Key, IsCheckable = true, IsChecked = app.IsExcluded, StaysOpenOnClick = true };

				// an app is either pinned or excluded
				pin.Click += (_, _) =>
				{
					SceneManager.SetPinned(app.Key, pin.IsChecked);
					exclude.IsChecked = SceneManager.IsExcluded(app.Key);
				};
				exclude.Click += (_, _) =>
				{
					SceneManager.SetExcluded(app.Key, exclude.IsChecked);
					pin.IsChecked = SceneManager.IsPinned(app.Key);
				};

				pinnedAppsMenu.Items.Add(pin);
				excludedAppsMenu.Items.Add(exclude);
			}
		}

		/// <summary>
		/// A symbol of the Windows icon font, see <see cref="Themes.MenuTheme"/>
		/// </summary>
		private static TextBlock MenuIcon(string glyph) => new TextBlock { Text = glyph };

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
				var add = new MenuItem { Header = "Add to current stage (Shift+Click)", Icon = MenuIcon("\uE710") };
				add.Click += (_, _) => SceneManager.AddToCurrentScene(model.Scene);
				menu.Items.Add(add);
			}

			if (apps.Length > 1)
			{
				foreach (var app in apps)
				{
					var separate = new MenuItem { Header = $"Take {app.Name} out of this stage", Icon = MenuIcon("\uE738") };
					separate.Click += (_, _) => SceneManager.SeparateApp(model.Scene, app.Key);
					menu.Items.Add(separate);
				}
			}

			if (menu.Items.Count > 0)
				menu.Items.Add(new Separator());

			foreach (var app in apps)
			{
				var pin = new MenuItem { Header = $"Pin {app.Name} (always visible)", Icon = MenuIcon("\uE718") };
				pin.Click += (_, _) => SceneManager.SetPinned(app.Key, true);
				menu.Items.Add(pin);
			}

			foreach (var app in apps)
			{
				var exclude = new MenuItem { Header = $"Exclude {app.Name} from Stage Manager", Icon = MenuIcon("\uED1A") };
				exclude.Click += (_, _) => SceneManager.SetExcluded(app.Key, true);
				menu.Items.Add(exclude);
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
