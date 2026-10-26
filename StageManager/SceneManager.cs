using AsyncAwaitBestPractices;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Settings;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager
{
	public class SceneManager
	{
		private readonly Desktop _desktop;
		private readonly SettingsStore _settingsStore;
		private readonly AppSettings _settings;
		private readonly IBackdrop _backdrop;
		private readonly List<Scene> _recentScenes = new List<Scene>();
		private readonly HashSet<string> _pinnedApps;
		private readonly AppGroups _appGroups;
		private readonly HashSet<IWindow> _hiddenWindows = new HashSet<IWindow>();
		private readonly object _hiddenWindowsLock = new object();
		private List<Scene> _scenes;
		private Scene _current;
		private bool _suspend = false;
		private Guid? _priorSceneId;
		private DateTime _switchedAt;
		private int _stopped;

		/// <summary>
		/// Windows usually get activated right after they were shown, this is how long we wait for that to happen.
		/// </summary>
		private const int NEW_WINDOW_ACTIVATION_DELAY_MILLISECONDS = 500;

		/// <summary>
		/// Windows activates another window when the active one goes away, this is how long we wait for that.
		/// The same goes for minimizing all windows with Win+D.
		/// </summary>
		private const int WINDOWS_REACTION_DELAY_MILLISECONDS = 300;

		public event EventHandler<SceneChangedEventArgs> SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs> CurrentSceneSelectionChanged;

		private IWindowStrategy WindowStrategy { get; } = new NormalizeAndMinimizeWindowStrategy(); // new WindowNormalizeStrategy/OpacityWindowStrategy/ShowAndHideWindowStrategy

		public WindowsManager WindowsManager { get; }

		public SceneManager(WindowsManager windowsManager, SettingsStore settingsStore, IBackdrop backdrop)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
			_backdrop = backdrop ?? throw new ArgumentNullException(nameof(backdrop));
			_settings = settingsStore.Load();
			_pinnedApps = new HashSet<string>(_settings.PinnedApps, StringComparer.OrdinalIgnoreCase);
			_appGroups = new AppGroups(_settings.AppGroups);
			_desktop = new Desktop();

			// older versions hid the desktop icons, bring them back if one of them did not exit cleanly
			if (_settings.DesktopIconsVisibleBeforeStart is bool iconsWereVisible)
			{
				if (iconsWereVisible)
					_desktop.ShowIcons();

				_settings.DesktopIconsVisibleBeforeStart = null;
				SaveSettings();
			}
		}

		public async Task Start()
		{
			if (Thread.CurrentThread.ManagedThreadId != 1)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.UntrackedFocus += WindowsManager_UntrackedFocus;

			await WindowsManager.Start();

			GetScenes();
		}

		/// <summary>
		/// Brings back the windows and desktop icons hidden by Stage Manager.
		/// Can be called more than once and from any thread, as it is also used when the app crashes.
		/// </summary>
		internal void Stop()
		{
			if (Interlocked.Exchange(ref _stopped, 1) == 1)
				return;

			// the windows behind the backdrop show up again once it is gone
			_backdrop.HideFromAnyThread();

			WindowsManager.Stop();

			IWindow[] hiddenWindows;
			lock (_hiddenWindowsLock)
			{
				hiddenWindows = _hiddenWindows.ToArray();
				_hiddenWindows.Clear();
			}

			foreach (var w in hiddenWindows)
				WindowStrategy.Show(w);
		}

		private void ShowWindow(IWindow window)
		{
			lock (_hiddenWindowsLock)
				_hiddenWindows.Remove(window);

			WindowStrategy.Show(window);
		}

		/// <summary>
		/// Takes windows off the stage by putting them behind the backdrop, where they keep running.
		/// Without a stage (in the desktop view) they are minimized.
		/// </summary>
		private void MoveOffStage(IReadOnlyCollection<IWindow> windows)
		{
			if (!_backdrop.IsVisible)
			{
				HideWindows(windows);
				return;
			}

			foreach (var window in windows)
				_backdrop.PutBehind(window.Handle);

			WindowSnapshots.CaptureInBackground(windows);
		}

		/// <summary>
		/// Puts the windows of the scene and the pinned windows in front of the backdrop, all other windows end up behind it
		/// </summary>
		private void BringOnStage(Scene scene)
		{
			// windows minimized by the desktop view or by the user come back with their scene
			foreach (var window in scene.Windows)
				ShowWindow(window);

			if (!_backdrop.IsVisible)
				_backdrop.Show();

			_backdrop.Arrange(scene.Windows.Concat(GetPinnedWindows()).Select(w => w.Handle));
		}

		/// <summary>
		/// The desktop lies behind all windows, so they have to be minimized to show it
		/// </summary>
		private void ShowDesktop()
		{
			_backdrop.Hide();
			HideWindows(GetSceneableWindows().ToArray());
		}

		/// <summary>
		/// Minimizes windows and remembers them to bring them back when Stage Manager quits
		/// </summary>
		private void HideWindows(IEnumerable<IWindow> windows)
		{
			// windows the user minimized on their own are left alone, they should not be restored on quit
			var visibleWindows = windows.Where(w => !w.IsMinimized).ToArray();

			// the sidebar shows the windows as they looked right before they were hidden, minimized windows can't be captured
			WindowSnapshots.Capture(visibleWindows);

			foreach (var window in visibleWindows)
			{
				lock (_hiddenWindowsLock)
					_hiddenWindows.Add(window);

				WindowStrategy.Hide(window);
			}
		}

		private void SaveSettings()
		{
			try
			{
				_settingsStore.Save(_settings);
			}
			catch (Exception ex)
			{
				App.Log(ex);
			}
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (_suspend)
				return;

			if (type == WindowUpdateType.Foreground)
				SwitchToSceneByWindow(window).SafeFireAndForget();
		}

		public bool IsPinned(IWindow window) => IsPinned(GetWindowGroupKey(window));

		public bool IsPinned(string appKey) => _pinnedApps.Contains(appKey);

		/// <summary>
		/// Pinned apps are not part of any scene. Their windows stay visible all the time and focusing them does not switch scenes.
		/// </summary>
		public void SetPinned(string appKey, bool pinned)
		{
			var changed = pinned ? _pinnedApps.Add(appKey) : _pinnedApps.Remove(appKey);
			if (!changed)
				return;

			_settings.PinnedApps = _pinnedApps.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
			SaveSettings();

			var appWindows = WindowsManager.Windows.Where(w => string.Equals(GetWindowGroupKey(w), appKey, StringComparison.OrdinalIgnoreCase)).ToArray();

			if (pinned)
			{
				foreach (var window in appWindows)
				{
					if (FindSceneForWindow(window) is Scene scene)
						RemoveFromScene(scene, window);

					ShowWindow(window);
				}

				if (_current is not null)
					BringOnStage(_current);
			}
			else
			{
				// the app is part of the scenes again, show its scene so that none of its windows suddenly disappears
				Scene? appScene = null;
				foreach (var window in appWindows.Where(IsSceneable))
					appScene = AddToScene(window);

				if (appScene is not null)
					SwitchTo(appScene).SafeFireAndForget();
			}
		}

		/// <summary>
		/// Lists the apps with visible windows plus the pinned ones that are not running.
		/// </summary>
		public IEnumerable<AppInfo> GetApps()
		{
			var runningApps = WindowsManager.Windows
				.Where(HasWindowContent)
				.GroupBy(GetWindowGroupKey, StringComparer.OrdinalIgnoreCase)
				.Select(group => new AppInfo(group.Key, group.First().AppName, IsPinned(group.Key)))
				.ToList();

			var pinnedAppsNotRunning = _pinnedApps
				.Where(key => !runningApps.Any(app => string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase)))
				.Select(key => new AppInfo(key, key, true));

			return runningApps.Concat(pinnedAppsNotRunning).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase);
		}

		public IEnumerable<AppInfo> GetApps(Scene scene)
		{
			return scene.Windows
				.GroupBy(GetWindowGroupKey, StringComparer.OrdinalIgnoreCase)
				.Select(group => new AppInfo(group.Key, group.First().AppName, IsPinned(group.Key)));
		}

		public Scene? CurrentScene => _current;

		/// <summary>
		/// Moves all apps of the given scene into the current scene, like dragging an app onto the stage on macOS.
		/// The apps are remembered as group and stay together from now on.
		/// </summary>
		public void AddToCurrentScene(Scene source)
		{
			if (source is null || source.Equals(_current) || !_scenes.Contains(source))
				return;

			// there is no stage to add to in the desktop view
			if (_current is null)
			{
				SwitchTo(source).SafeFireAndForget();
				return;
			}

			var target = _current;
			var windows = source.Windows.ToArray();

			_appGroups.Join(GetAppKeys(source).Concat(GetAppKeys(target)));
			SaveAppGroups();

			try
			{
				_suspend = true;

				foreach (var window in windows)
				{
					source.Remove(window);
					target.Add(window);
				}

				BringOnStage(target);
				RemoveScene(source, windows.Last());
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, windows.Last(), ChangeType.Updated));

				windows.First().Focus();
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>
		/// Moves an app with all its windows from its scene to another one.
		/// </summary>
		public void MoveAppToScene(IWindow appWindow, Scene target)
		{
			var source = FindSceneForWindow(appWindow);
			if (source is null || target is null || source.Equals(target) || !_scenes.Contains(target))
				return;

			var appKey = GetWindowGroupKey(appWindow);
			var windows = GetAppWindows(source, appKey);

			_appGroups.Detach(appKey);
			_appGroups.Join(GetAppKeys(target).Append(appKey));
			SaveAppGroups();

			try
			{
				_suspend = true;

				foreach (var window in windows)
				{
					source.Remove(window);
					target.Add(window);
				}

				if (target.Equals(_current))
					BringOnStage(target);
				else
					MoveOffStage(windows);

				if (source.Windows.Any())
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, appWindow, ChangeType.Updated));
				else
					RemoveScene(source, appWindow);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, appWindow, ChangeType.Updated));
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>
		/// Takes an app out of a scene with several apps, so that it becomes a scene of its own again.
		/// </summary>
		public void SeparateApp(Scene scene, string appKey)
		{
			if (scene is null || !_scenes.Contains(scene))
				return;

			var windows = GetAppWindows(scene, appKey);
			if (windows.Length == 0 || windows.Length == scene.Windows.Count())
				return;

			_appGroups.Detach(appKey);
			SaveAppGroups();

			try
			{
				_suspend = true;

				foreach (var window in windows)
					scene.Remove(window);

				if (scene.Equals(_current))
					MoveOffStage(windows);

				var separated = new Scene(windows);
				_scenes.Add(separated);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, windows.First(), ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(separated, windows.First(), ChangeType.Created));
			}
			finally
			{
				_suspend = false;
			}
		}

		public void SeparateApp(IWindow appWindow) => SeparateApp(FindSceneForWindow(appWindow), GetWindowGroupKey(appWindow));

		public bool HasSeveralApps(Scene scene) => GetAppKeys(scene).Skip(1).Any();

		private IEnumerable<string> GetAppKeys(Scene scene) => scene.Windows.Select(GetWindowGroupKey).Distinct(StringComparer.OrdinalIgnoreCase);

		private IWindow[] GetAppWindows(Scene scene, string appKey) => scene.Windows.Where(w => string.Equals(GetWindowGroupKey(w), appKey, StringComparison.OrdinalIgnoreCase)).ToArray();

		private void SaveAppGroups()
		{
			_settings.AppGroups = _appGroups.ToList();
			SaveSettings();
		}

		private void WindowsManager_UntrackedFocus(object? sender, IntPtr e)
		{
			if (_suspend)
				return;

			if (!_desktop.HasDesktopView)
				_desktop.TrySetDesktopView(e);

			if (_desktop.HasDesktopView && _desktop.DesktopViewHandle == e)
			{
				OnDesktopFocused().SafeFireAndForget();
				return;
			}

			// a dialog of an app behind the backdrop comes in front when it gets activated, its app has to come along
			var owner = Win32.GetAncestor(e, Win32.GA.GA_ROOTOWNER);
			if (owner != e && WindowsManager.Windows.FirstOrDefault(w => w.Handle == owner) is IWindow ownerWindow)
				SwitchToSceneByWindow(ownerWindow).SafeFireAndForget();
		}

		/// <summary>
		/// The desktop gets focused with Win+D, but also when it is clicked on another screen.
		/// Only Win+D minimizes the windows of the scene, which is when the desktop view makes sense.
		/// </summary>
		private async Task OnDesktopFocused()
		{
			await Task.Delay(WINDOWS_REACTION_DELAY_MILLISECONDS).ConfigureAwait(true);

			if (_current is not null && _current.Windows.All(w => w.IsMinimized))
				await SwitchTo(null);
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			lock (_hiddenWindowsLock)
				_hiddenWindows.Remove(window);

			WindowSnapshots.Remove(window.Handle);

			var scene = FindSceneForWindow(window);

			if (scene is not null)
				RemoveFromScene(scene, window);
		}

		private void RemoveFromScene(Scene scene, IWindow window)
		{
			scene.Remove(window);

			if (scene.Windows.Any())
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			else
				RemoveScene(scene, window);
		}

		private void RemoveScene(Scene scene, IWindow window)
		{
			_scenes.Remove(scene);
			_recentScenes.Remove(scene);

			// don't keep a scene that no longer exists as current, otherwise it can never be switched away from properly
			if (scene.Equals(_current))
			{
				_current = null;
				FollowUpEmptyStage().SafeFireAndForget();
			}

			SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));
		}

		/// <summary>
		/// When the last window of the current scene is gone, Windows activates the next window, which switches to its scene.
		/// If it does not, the most recently used scene comes on stage, like on macOS.
		/// </summary>
		private async Task FollowUpEmptyStage()
		{
			await Task.Delay(WINDOWS_REACTION_DELAY_MILLISECONDS).ConfigureAwait(true);

			if (_current is not null || _stopped == 1)
				return;

			var next = _recentScenes.FirstOrDefault(_scenes.Contains) ?? _scenes.FirstOrDefault();

			if (next is not null)
				await SwitchTo(next);
			else
				_backdrop.Hide();
		}

		public Scene FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene FindSceneForWindow(IntPtr handle) => _scenes?.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));

		/// <summary>
		/// Finds the scene with the given app or with any app that was grouped with it
		/// </summary>
		private Scene FindSceneForApp(string appKey)
		{
			var group = _appGroups.GroupOf(appKey);
			return _scenes.FirstOrDefault(s => s.Windows.Any(w => group.Contains(GetWindowGroupKey(w))));
		}

		/// <summary>
		/// Adds the window to the scene of its app (or app group) or creates a new scene for it.
		/// </summary>
		private Scene AddToScene(IWindow window)
		{
			if (FindSceneForWindow(window) is Scene existing)
				return existing;

			var scene = FindSceneForApp(GetWindowGroupKey(window));

			if (scene is null)
			{
				scene = new Scene(window);
				_scenes.Add(scene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}
			else
			{
				scene.Add(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			return scene;
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			if (!IsSceneable(window))
				return;

			AddToScene(window);

			// A new window usually gets activated right after it was shown, which switches to its scene.
			// Windows that open in the background must not pull the user out of the current scene, so they go behind the backdrop.
			// Without a current scene there is no stage to keep clean.
			await Task.Delay(NEW_WINDOW_ACTIVATION_DELAY_MILLISECONDS).ConfigureAwait(true);

			if (_stopped == 0 && _current is not null && FindSceneForWindow(window) is Scene scene && !scene.Equals(_current) && !window.IsFocused)
				MoveOffStage(new[] { window });
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
			// pinned apps stay visible next to every scene, focusing them must not switch away
			if (IsPinned(window))
				return;

			var scene = FindSceneForWindow(window);

			if (scene is null && IsSceneable(window))
				scene = AddToScene(window);

			if (scene is not null && !IsReentrancy(scene))
				await SwitchTo(scene);
		}

		/// <summary>
		/// Determines if a scene gets activated shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidden,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// Only used for switches caused by window activation, the user can always switch back.
		/// </summary>
		private bool IsReentrancy(Scene scene)
		{
			return Guid.Equals(scene.Id, _priorSceneId) && DateTime.UtcNow - _switchedAt < TimeSpan.FromSeconds(1);
		}

		public async Task SwitchTo(Scene? scene)
		{
			if (object.Equals(scene, _current))
				return;

			try
			{
				_suspend = true;

				// windows that got their title after they were shown are in no scene yet,
				// they would be hidden without showing up in the sidebar
				foreach (var window in GetSceneableWindows().Where(w => FindSceneForWindow(w) is null).ToArray())
					AddToScene(window);

				var prior = _current;
				_current = scene;
				_priorSceneId = prior?.Id;
				_switchedAt = DateTime.UtcNow;

				if (scene is not null)
				{
					_recentScenes.Remove(scene);
					_recentScenes.Insert(0, scene);
				}

				foreach (var s in _scenes)
					s.IsSelected = s.Equals(scene);

				if (scene is not null)
					BringOnStage(scene);
				else
					ShowDesktop();

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				// the windows that left the stage keep running behind the backdrop, so the pictures don't hold up the switch
				if (prior is not null && scene is not null)
					WindowSnapshots.CaptureInBackground(prior.Windows.ToArray());
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>
		/// Windows without title are mostly helper windows of apps that should not show up as scenes.
		/// </summary>
		private static bool HasWindowContent(IWindow window) => !string.IsNullOrEmpty(window.ProcessFileName) && !string.IsNullOrEmpty(window.Title);

		private bool IsSceneable(IWindow window) => HasWindowContent(window) && !IsPinned(window);

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager?.Windows?.Where(IsSceneable);

		public IEnumerable<Scene> GetScenes()
		{
			if (_scenes is null)
			{
				_scenes = GetSceneableWindows()
							.GroupBy(w => _appGroups.CanonicalKey(GetWindowGroupKey(w)), StringComparer.OrdinalIgnoreCase)
							.Select(group => new Scene(group.ToArray()))
							.ToList();
			}

			return _scenes;
		}

		/// <summary>
		/// The windows of the current scene (or of all scenes in the desktop view) and the pinned ones.
		/// </summary>
		public IEnumerable<IWindow> GetVisibleWindows() => (_current?.Windows ?? GetSceneableWindows()).Concat(GetPinnedWindows());

		private IEnumerable<IWindow> GetPinnedWindows() => WindowsManager.Windows.Where(w => HasWindowContent(w) && IsPinned(w));

		/// <summary>
		/// macOS shows the desktop when the wallpaper is clicked, this can be turned off
		/// </summary>
		public bool ClickWallpaperShowsDesktop
		{
			get => _settings.ClickWallpaperShowsDesktop;
			set
			{
				_settings.ClickWallpaperShowsDesktop = value;
				SaveSettings();
			}
		}

		private string GetWindowGroupKey(IWindow window) => window.ProcessName;
	}
}
