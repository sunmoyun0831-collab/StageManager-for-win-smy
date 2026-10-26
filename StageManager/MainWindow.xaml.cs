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
		private SceneModel _mouseDownScene;

		public bool EnableWindowDropToScene = false;
		public bool EnableWindowPullToScene = true;

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

			SwitchSceneCommand = new ActionCommand(async model => await SceneManager!.SwitchTo(((SceneModel)model).Scene));
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

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			if (e.Data.Button != SharpHook.Native.MouseButton.Button1)
				return;

			// if it's allowed to drag windows into scenes, we cannot hide the scenes
			if (EnableWindowDropToScene)
				Dispatcher.Invoke(_overlapCheckTimer.Stop);

			var foregroundWindow = Win32.GetForegroundWindow();
			if (foregroundWindow != _thisHandle)
				return;

			if (EnableWindowPullToScene)
			{
				var screenPoint = new Point(e.Data.X, e.Data.Y);
				this.Dispatcher.Invoke(() =>
				{
					_mouseDownScene = FindSceneByPoint(screenPoint);
				});
			}
		}

		private void OnMouseReleased(object? sender, MouseHookEventArgs e)
		{
			if (e.Data.Button != SharpHook.Native.MouseButton.Button1)
				return;

			// if it's allowed to drag windows into scenes, we cannot hide the scenes
			if (EnableWindowDropToScene)
			{
				Dispatcher.Invoke(_overlapCheckTimer.Start);

				var foregroundWindow = Win32.GetForegroundWindow();

				if (foregroundWindow == _thisHandle)
					return;

				var screenPoint = new Point(e.Data.X, e.Data.Y);
				this.Dispatcher.Invoke(() =>
				{
					var sceneModel = FindSceneByPoint(screenPoint);

					if (sceneModel is object)
						SceneManager.MoveWindow(foregroundWindow, sceneModel.Scene).SafeFireAndForget();
				});
			}

			if (EnableWindowPullToScene)
			{
				if (e.Data.X >= _sidebarZone.Right && _mouseDownScene is object)
				{
					this.Dispatcher.Invoke(() =>
					{
						SceneManager.PopWindowFrom(_mouseDownScene.Scene).SafeFireAndForget();
					});
				}
			}
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

		public IEnumerable<SceneModel> AllScenes => Scenes.Union(new[] { _removedCurrentScene });

		public ICommand SwitchSceneCommand { get; }

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

			foreach (var app in SceneManager.GetApps(model.Scene))
			{
				var item = new MenuItem { Header = $"Pin {app.Name} (always visible)" };
				item.Click += (_, _) => SceneManager.SetPinned(app.Key, true);
				menu.Items.Add(item);
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
