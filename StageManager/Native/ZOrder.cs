using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// Changes the order in which windows lie on top of each other, without moving, sizing or activating them.
	/// </summary>
	internal static class ZOrder
	{
		private const uint SWP_NOSIZE = 0x0001;
		private const uint SWP_NOMOVE = 0x0002;
		private const uint SWP_NOACTIVATE = 0x0010;
		private const uint SWP_ASYNCWINDOWPOS = 0x4000;
		private const uint FLAGS = SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE;

		private static readonly IntPtr HWND_TOP = IntPtr.Zero;

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

		[DllImport("user32.dll")]
		private static extern bool IsHungAppWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		/// <summary>
		/// Puts our own windows on top of all windows (topmost ones aside) and raises the given windows of other apps above them.
		/// </summary>
		/// <param name="front">Windows of other apps that end up on top, in any order</param>
		/// <param name="ownWindows">Windows of this app that come right behind the front windows, the first one in front</param>
		public static void Arrange(IEnumerable<IntPtr> front, IReadOnlyList<IntPtr> ownWindows)
		{
			// Windows of other apps only move once their app gets to it, so where they are can't be relied on.
			// Placing our windows relative to them put our windows where the other windows were, not where they ended up.
			// Our own windows move at once, so they go on top first (the back one first) and the other windows follow above them.
			foreach (var own in ownWindows.Reverse().Where(CanBeOrdered))
				SetWindowPos(own, HWND_TOP, 0, 0, 0, 0, FLAGS);

			Raise(front);
		}

		/// <summary>
		/// Puts the windows on top of all others (topmost ones aside), keeping their order among each other
		/// </summary>
		public static void Raise(IEnumerable<IntPtr> handles)
		{
			// the back one first, each one goes on top of the ones before.
			// Asynchronous, a window that hangs must not block Stage Manager.
			foreach (var handle in SortTopToBottom(handles.Where(CanBeOrdered).Distinct()).Reverse())
				SetWindowPos(handle, HWND_TOP, 0, 0, 0, 0, FLAGS | SWP_ASYNCWINDOWPOS);
		}

		/// <summary>
		/// Puts the window right behind another one
		/// </summary>
		public static void PutBehind(IntPtr handle, IntPtr other)
		{
			// asynchronous, a window that hangs must not block Stage Manager
			SetWindowPos(handle, other, 0, 0, 0, 0, FLAGS | SWP_ASYNCWINDOWPOS);
		}

		/// <summary>
		/// The windows that lie behind the given one, of the ones that can be ordered at all
		/// </summary>
		public static IReadOnlyList<IntPtr> FindBehind(IEnumerable<IntPtr> handles, IntPtr other)
		{
			var order = new Dictionary<IntPtr, int>();

			Win32.EnumWindows((handle, _) =>
			{
				order[handle] = order.Count;
				return true;
			}, IntPtr.Zero);

			if (!order.TryGetValue(other, out var otherIndex))
				return Array.Empty<IntPtr>();

			// minimized and cloaked windows can't be seen anyway
			return handles
				.Where(h => CanBeOrdered(h) && !Win32.IsIconic(h) && !Win32Helper.IsCloaked(h))
				.Where(h => order.TryGetValue(h, out var index) && index > otherIndex)
				.ToList();
		}

		/// <summary>
		/// Everything about a window that matters for ordering it, for logs
		/// </summary>
		public static string Describe(IntPtr handle)
		{
			var title = new System.Text.StringBuilder(256);
			Win32.GetWindowText(handle, title, title.Capacity);
			Win32.GetWindowThreadProcessId(handle, out var processId);

			string processName;
			try
			{
				processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
			}
			catch (Exception)
			{
				processName = "?";
			}

			var elevated = ProcessElevation.IsElevated((int)processId);

			return $"0x{handle.ToInt64():X} {processName} \"{title}\" class {Win32Helper.GetClassName(handle)}, " +
				$"elevated: {(elevated is null ? "unknown" : elevated.Value ? "yes" : "no")}, topmost: {IsTopmost(handle)}, owner: 0x{Win32.GetWindow(handle, Win32.GW.GW_OWNER).ToInt64():X}";
		}

		/// <summary>
		/// The window lying on top of the others
		/// </summary>
		public static IntPtr Top(IEnumerable<IntPtr> handles) => SortTopToBottom(handles).FirstOrDefault();

		public static bool IsTopmost(IntPtr handle) => Win32.GetWindowExStyleLongPtr(handle).HasFlag(Win32.WS_EX.WS_EX_TOPMOST);

		/// <summary>
		/// Topmost windows always stay above the others, hanging windows would block reordering
		/// </summary>
		private static bool CanBeOrdered(IntPtr handle) => handle != IntPtr.Zero && IsWindow(handle) && !IsTopmost(handle) && !IsHungAppWindow(handle);

		/// <summary>
		/// The windows in the order they lie on top of each other, the top one first
		/// </summary>
		public static IEnumerable<IntPtr> SortTopToBottom(IEnumerable<IntPtr> handles)
		{
			var wanted = handles.ToHashSet();
			var sorted = new List<IntPtr>();

			// EnumWindows lists the top level windows from top to bottom
			Win32.EnumWindows((handle, _) =>
			{
				if (wanted.Contains(handle))
					sorted.Add(handle);

				return true;
			}, IntPtr.Zero);

			// owned windows are not listed by EnumWindows in every case, keep them anyway
			return sorted.Concat(wanted.Except(sorted));
		}
	}
}
