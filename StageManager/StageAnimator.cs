using AsyncAwaitBestPractices;
using StageManager.Native;
using StageManager.Native.Interop;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StageManager
{
	/// <summary>
	/// Plays how windows leave the stage and come on stage, see <see cref="StageAnimator"/>
	/// </summary>
	public interface IStageAnimator
	{
		/// <summary>
		/// True while windows are flying
		/// </summary>
		bool IsRunning { get; }

		/// <summary>
		/// Lets the leaving windows fly into the sidebar and the coming ones out of it
		/// </summary>
		/// <param name="slot">The scene whose place in the sidebar the windows of the current scene take</param>
		/// <param name="front">A window Windows brought to the front already, like with Alt+Tab. The windows fly behind it.</param>
		/// <param name="hide">Minimizes the leaving windows, called once they are covered by their pictures</param>
		/// <param name="show">Restores the coming windows, called once their pictures arrived</param>
		void Animate(IReadOnlyCollection<IWindow> leaving, IReadOnlyCollection<IWindow> coming, Scene slot, IntPtr front, Action hide, Action show);

		/// <summary>
		/// Stops the windows flying. Hiding and showing is left undone if it did not happen yet,
		/// the stage has to be set again from the windows as they are.
		/// </summary>
		void Interrupt();
	}

	/// <summary>
	/// Lets windows fly between the stage and the sidebar like on macOS. The windows themselves are minimized and restored
	/// with the Windows animation turned off, what flies are live DWM thumbnails of them on a transparent window on top.
	/// DWM draws the thumbnails and they are moved from a thread of their own, so a busy UI thread can't make them stutter.
	/// </summary>
	public class StageAnimator : IStageAnimator
	{
		private const double FLIGHT_MILLISECONDS = 280;

		/// <summary>
		/// The leaving windows fade into their card once they arrived
		/// </summary>
		private const double FADE_MILLISECONDS = 120;

		/// <summary>
		/// The overlay has to be on screen before the windows are minimized, otherwise they would be gone for a moment
		/// </summary>
		private const int OVERLAY_FRAMES = 3;

		/// <summary>
		/// The pictures of the coming windows stay until the windows are back and had the time to paint
		/// </summary>
		private const int RESTORE_TIMEOUT_MILLISECONDS = 500;
		private const int PAINT_FRAMES = 3;

		/// <summary>
		/// Apps minimize and restore their windows when they get to it, the Windows animation stays off until then
		/// </summary>
		private const int SYSTEM_ANIMATION_DELAY_MILLISECONDS = 300;

		private readonly Func<IntPtr, Scene, Rect> _findCard;
		private readonly Dictionary<IntPtr, Departure> _departures = new Dictionary<IntPtr, Departure>();
		private Overlay? _overlay;
		private Run? _run;

		/// <param name="findCard">Where a window is shown in the sidebar, in physical pixels</param>
		public StageAnimator(Func<IntPtr, Scene, Rect> findCard)
		{
			_findCard = findCard ?? throw new ArgumentNullException(nameof(findCard));
		}

		public bool IsRunning => _run is not null;

		public void Animate(IReadOnlyCollection<IWindow> leaving, IReadOnlyCollection<IWindow> coming, Scene slot, IntPtr front, Action hide, Action show)
		{
			Interrupt();

			Run? run = null;

			try
			{
				run = Prepare(leaving, coming, slot, front, hide, show);
			}
			catch (Exception ex)
			{
				// switching scenes has to work anyway, just without flying
				App.Log(ex);
			}

			if (run is null)
			{
				hide();
				show();
				return;
			}

			_run = run;
			Play(run).SafeFireAndForget();
		}

		public void Interrupt()
		{
			if (_run is Run run)
				End(run);
		}

		private Run? Prepare(IReadOnlyCollection<IWindow> leaving, IReadOnlyCollection<IWindow> coming, Scene slot, IntPtr front, Action hide, Action show)
		{
			foreach (var closed in _departures.Keys.Where(h => !Win32.IsWindow(h)).ToArray())
				_departures.Remove(closed);

			var flights = new List<Flight>();

			// the back windows first, thumbnails registered later are drawn on top
			foreach (var handle in ZOrder.SortTopToBottom(leaving.Select(w => w.Handle)).Reverse())
			{
				if (!Win32.IsWindowVisible(handle) || Win32.IsIconic(handle) || Win32Helper.IsCloaked(handle))
					continue;

				var rect = new Win32.Rect();
				if (!Win32.GetWindowRect(handle, ref rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
					continue;

				var bounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
				_departures[handle] = new Departure(bounds, WindowPlacement.GetNormalPosition(handle));
				flights.Add(new Flight(handle, bounds, Fit(bounds, _findCard(handle, slot)), isLeaving: true));
			}

			foreach (var handle in ZOrder.SortTopToBottom(coming.Select(w => w.Handle)).Reverse())
			{
				if (GetArrival(handle) is Rect bounds && bounds.Width >= 1 && bounds.Height >= 1)
					flights.Add(new Flight(handle, Fit(bounds, _findCard(handle, slot)), bounds, isLeaving: false));
			}

			if (flights.Count == 0)
				return null;

			var area = flights.Aggregate(Rect.Empty, (union, flight) => Rect.Union(Rect.Union(union, flight.From), flight.To));
			var screens = System.Windows.Forms.SystemInformation.VirtualScreen;
			area.Intersect(new Rect(screens.Left, screens.Top, screens.Width, screens.Height));

			if (area.IsEmpty)
				return null;

			area = new Rect(new Point(Math.Floor(area.Left), Math.Floor(area.Top)), new Point(Math.Ceiling(area.Right), Math.Ceiling(area.Bottom)));

			// shown first, WPF may place the window when showing it
			var overlay = _overlay ??= new Overlay();
			overlay.Show();
			overlay.Cover(area, front);

			var run = new Run(flights, area.TopLeft, hide, show);

			try
			{
				run.Register(overlay.Handle);
			}
			catch
			{
				run.Cancel();
				overlay.Hide();
				throw;
			}

			if (run.Flights.Count == 0)
			{
				overlay.Hide();
				return null;
			}

			return run;
		}

		/// <summary>
		/// Where a minimized window will be once it is restored. Windows that left the stage come back to where they were,
		/// which also covers windows snapped to a side of the screen, unless they were moved since.
		/// </summary>
		private Rect? GetArrival(IntPtr handle)
		{
			if (_departures.TryGetValue(handle, out var departure) && departure.NormalPosition.Equals(WindowPlacement.GetNormalPosition(handle)))
				return departure.Bounds;

			return WindowPlacement.GetRestoredBounds(handle);
		}

		/// <summary>
		/// The bounds of a window shrunk to fit into a card of the sidebar
		/// </summary>
		private static Rect Fit(Rect window, Rect card)
		{
			var scale = Math.Min(card.Width / window.Width, card.Height / window.Height);
			var width = window.Width * scale;
			var height = window.Height * scale;
			return new Rect(card.Left + (card.Width - width) / 2, card.Top + (card.Height - height) / 2, width, height);
		}

		private async Task Play(Run run)
		{
			MinimizeAnimation.Suppress();

			try
			{
				for (var i = 0; i < OVERLAY_FRAMES; i++)
					await NextFrame();

				if (run.IsCancelled)
					return;

				run.Hide();

				var flight = Task.Factory.StartNew(() => Fly(run), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

				await run.Landed.Task;

				if (run.IsCancelled)
					return;

				run.Show();

				var clock = Stopwatch.StartNew();
				while (!run.IsCancelled && run.Flights.Any(f => !f.IsLeaving && Win32.IsIconic(f.Window)) && clock.ElapsedMilliseconds < RESTORE_TIMEOUT_MILLISECONDS)
					await NextFrame();

				for (var i = 0; i < PAINT_FRAMES; i++)
					await NextFrame();

				run.ReleaseComing();

				await flight;
			}
			catch (Exception ex)
			{
				App.Log(ex);

				// whatever went wrong, the windows have to end up where they belong
				if (!run.IsCancelled)
				{
					run.Hide();
					run.Show();
				}
			}
			finally
			{
				if (ReferenceEquals(_run, run))
					End(run);
			}
		}

		private void End(Run run)
		{
			_run = null;
			run.Cancel();
			_overlay?.Hide();
			MinimizeAnimation.Allow(SYSTEM_ANIMATION_DELAY_MILLISECONDS);
		}

		/// <summary>
		/// Moves the thumbnails once per frame on screen, runs on a thread of its own
		/// </summary>
		private static void Fly(Run run)
		{
			try
			{
				var clock = Stopwatch.StartNew();

				while (true)
				{
					var time = clock.Elapsed.TotalMilliseconds;
					var progress = Math.Min(1, time / FLIGHT_MILLISECONDS);
					var fade = Math.Clamp((time - FLIGHT_MILLISECONDS) / FADE_MILLISECONDS, 0, 1);

					if (!run.Move(Ease(progress), fade))
						return;

					if (progress >= 1)
						run.Landed.TrySetResult();

					if (fade >= 1)
						return;

					// waits until the screen was drawn with the thumbnails where they are now
					var drawn = clock.Elapsed.TotalMilliseconds;
					if (NativeMethods.DwmFlush() != 0 || clock.Elapsed.TotalMilliseconds - drawn < 1)
						Thread.Sleep(5);
				}
			}
			finally
			{
				run.Landed.TrySetResult();
			}
		}

		/// <summary>
		/// Starts slowly, speeds up and slows down again before it arrives
		/// </summary>
		private static double Ease(double progress) => progress < 0.5
			? 4 * progress * progress * progress
			: 1 - Math.Pow(-2 * progress + 2, 3) / 2;

		/// <summary>
		/// Completes when WPF draws the next frame, must be awaited on the UI thread
		/// </summary>
		private static Task NextFrame()
		{
			var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			EventHandler? rendering = null;
			rendering = (_, _) =>
			{
				CompositionTarget.Rendering -= rendering;
				frame.TrySetResult();
			};

			CompositionTarget.Rendering += rendering;
			return frame.Task;
		}

		/// <summary>
		/// Where a window was when it left the stage, and its normal position then to find out if it was moved since
		/// </summary>
		private readonly record struct Departure(Rect Bounds, RECT? NormalPosition);

		private sealed class Flight
		{
			public Flight(IntPtr window, Rect from, Rect to, bool isLeaving)
			{
				Window = window;
				From = from;
				To = to;
				IsLeaving = isLeaving;
			}

			public IntPtr Window { get; }

			public Rect From { get; }

			public Rect To { get; }

			public bool IsLeaving { get; }

			public IntPtr Thumbnail { get; set; }
		}

		/// <summary>
		/// One switch of the stage. The thumbnails are moved from another thread, changing them is locked.
		/// </summary>
		private sealed class Run
		{
			private readonly object _lock = new object();
			private readonly Action _hide;
			private readonly Action _show;
			private readonly Point _origin;
			private bool _hidden;
			private bool _shown;
			private volatile bool _isCancelled;

			public Run(List<Flight> flights, Point origin, Action hide, Action show)
			{
				Flights = flights;
				_origin = origin;
				_hide = hide;
				_show = show;
			}

			public List<Flight> Flights { get; }

			public bool IsCancelled => _isCancelled;

			public TaskCompletionSource Landed { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			public void Hide()
			{
				if (_hidden)
					return;

				_hidden = true;
				_hide();
			}

			public void Show()
			{
				if (_shown)
					return;

				_shown = true;
				_show();
			}

			/// <summary>
			/// Puts a thumbnail of each window where its flight starts. Windows that can't have one don't fly.
			/// </summary>
			public void Register(IntPtr overlay)
			{
				lock (_lock)
				{
					foreach (var flight in Flights.ToArray())
					{
						if (NativeMethods.DwmRegisterThumbnail(overlay, flight.Window, out var thumbnail) != 0)
						{
							Flights.Remove(flight);
							continue;
						}

						flight.Thumbnail = thumbnail;
						Update(flight, 0, 255);
					}
				}
			}

			/// <returns>False once the run was cancelled</returns>
			public bool Move(double progress, double fade)
			{
				lock (_lock)
				{
					if (_isCancelled)
						return false;

					foreach (var flight in Flights.Where(f => f.Thumbnail != IntPtr.Zero))
						Update(flight, progress, flight.IsLeaving ? (byte)Math.Round(255 * (1 - fade)) : (byte)255);

					return true;
				}
			}

			/// <summary>
			/// The coming windows are back, their thumbnails can go
			/// </summary>
			public void ReleaseComing()
			{
				lock (_lock)
				{
					foreach (var flight in Flights.Where(f => !f.IsLeaving))
						Release(flight);
				}
			}

			public void Cancel()
			{
				lock (_lock)
				{
					_isCancelled = true;

					foreach (var flight in Flights)
						Release(flight);
				}

				Landed.TrySetResult();
			}

			private static void Release(Flight flight)
			{
				if (flight.Thumbnail == IntPtr.Zero)
					return;

				NativeMethods.DwmUnregisterThumbnail(flight.Thumbnail);
				flight.Thumbnail = IntPtr.Zero;
			}

			private void Update(Flight flight, double progress, byte opacity)
			{
				var from = flight.From;
				var to = flight.To;

				// relative to the overlay
				var left = from.Left + (to.Left - from.Left) * progress - _origin.X;
				var top = from.Top + (to.Top - from.Top) * progress - _origin.Y;
				var width = from.Width + (to.Width - from.Width) * progress;
				var height = from.Height + (to.Height - from.Height) * progress;

				var properties = new DWM_THUMBNAIL_PROPERTIES
				{
					dwFlags = (int)(DWM_TNP.DWM_TNP_VISIBLE | DWM_TNP.DWM_TNP_OPACITY | DWM_TNP.DWM_TNP_RECTDESTINATION | DWM_TNP.DWM_TNP_SOURCECLIENTAREAONLY),
					fVisible = true,
					opacity = opacity,
					fSourceClientAreaOnly = false,
					rcDestination = new RECT
					{
						left = (int)Math.Round(left),
						top = (int)Math.Round(top),
						right = (int)Math.Round(left + width),
						bottom = (int)Math.Round(top + height)
					}
				};

				NativeMethods.DwmUpdateThumbnailProperties(flight.Thumbnail, ref properties);
			}
		}

		/// <summary>
		/// A transparent window that the thumbnails fly on, on top of the windows. Clicks go through it.
		/// </summary>
		private sealed class Overlay : Window
		{
			private const int WM_MOUSEACTIVATE = 0x0021;
			private const int WM_DPICHANGED = 0x02E0;
			private const int MA_NOACTIVATE = 3;
			private const uint SWP_NOACTIVATE = 0x0010;
			private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

			[DllImport("user32.dll")]
			private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

			public Overlay()
			{
				WindowStyle = WindowStyle.None;
				AllowsTransparency = true;

				// almost transparent instead of fully, so there is something to draw the thumbnails on
				Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
				ResizeMode = ResizeMode.NoResize;
				ShowInTaskbar = false;
				ShowActivated = false;
				Title = "Stage Manager animation";
				Width = 1;
				Height = 1;

				Handle = new WindowInteropHelper(this).EnsureHandle();
			}

			public IntPtr Handle { get; }

			protected override void OnSourceInitialized(EventArgs e)
			{
				base.OnSourceInitialized(e);

				// never activated, not listed in Alt+Tab and clicks go through to the windows below
				var handle = new WindowInteropHelper(this).Handle;
				var exStyle = Win32.GetWindowExStyleLongPtr(handle);
				Win32.SetWindowStyleExLongPtr(handle, exStyle | Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_NOACTIVATE | Win32.WS_EX.WS_EX_TRANSPARENT);

				HwndSource.FromHwnd(handle).AddHook(WndProc);
			}

			/// <summary>
			/// Places the overlay over the given area of the screens, in physical pixels.
			/// It lies on top of all windows, or right behind the given one.
			/// </summary>
			public void Cover(Rect area, IntPtr behind)
			{
				// placed after a window that is not topmost, the overlay is no longer topmost either
				var insertAfter = behind != IntPtr.Zero && Win32.IsWindow(behind) ? behind : HWND_TOPMOST;
				SetWindowPos(Handle, insertAfter, (int)area.Left, (int)area.Top, (int)area.Width, (int)area.Height, SWP_NOACTIVATE);
			}

			private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
			{
				switch (msg)
				{
					// placed in physical pixels, WPF must not resize it when it gets to a screen with another scaling
					case WM_DPICHANGED:
						handled = true;
						break;

					case WM_MOUSEACTIVATE:
						handled = true;
						return new IntPtr(MA_NOACTIVATE);
				}

				return IntPtr.Zero;
			}
		}
	}
}
