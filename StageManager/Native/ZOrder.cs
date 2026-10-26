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
		private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

		[DllImport("user32.dll")]
		private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

		[DllImport("user32.dll")]
		private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

		[DllImport("user32.dll")]
		private static extern bool IsHungAppWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		/// <summary>
		/// Puts the given windows on top of all other windows (topmost ones aside) and keeps their order among each other.
		/// The windows behind them follow in the given order right after them.
		/// </summary>
		/// <param name="front">Windows that end up on top, in any order</param>
		/// <param name="behind">Windows that come right after the front windows, in this order</param>
		public static void Arrange(IEnumerable<IntPtr> front, IReadOnlyList<IntPtr> behind)
		{
			var chain = SortTopToBottom(front.Where(CanBeOrdered).Distinct())
				.Concat(behind.Where(CanBeOrdered))
				.ToList();

			if (chain.Count == 0)
				return;

			// one batch changes the order at once, without the windows flickering through intermediate orders
			var batch = BeginDeferWindowPos(chain.Count);
			var insertAfter = HWND_TOP;

			foreach (var handle in chain)
			{
				if (batch == IntPtr.Zero)
					break;

				batch = DeferWindowPos(batch, handle, insertAfter, 0, 0, 0, 0, FLAGS);
				insertAfter = handle;
			}

			if (batch != IntPtr.Zero && EndDeferWindowPos(batch))
				return;

			// a window that went away in the meantime fails the whole batch, so do it one by one then
			insertAfter = HWND_TOP;
			foreach (var handle in chain.Where(IsWindow))
			{
				SetWindowPos(handle, insertAfter, 0, 0, 0, 0, FLAGS);
				insertAfter = handle;
			}
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
		/// The window lying on top of the others
		/// </summary>
		public static IntPtr Top(IEnumerable<IntPtr> handles) => SortTopToBottom(handles).FirstOrDefault();

		public static bool IsTopmost(IntPtr handle) => Win32.GetWindowExStyleLongPtr(handle).HasFlag(Win32.WS_EX.WS_EX_TOPMOST);

		/// <summary>
		/// Topmost windows always stay above the others, hanging windows would block reordering
		/// </summary>
		private static bool CanBeOrdered(IntPtr handle) => handle != IntPtr.Zero && IsWindow(handle) && !IsTopmost(handle) && !IsHungAppWindow(handle);

		private static IEnumerable<IntPtr> SortTopToBottom(IEnumerable<IntPtr> handles)
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
