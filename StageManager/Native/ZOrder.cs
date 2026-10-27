using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StageManager.Native
{
	/// <summary>
	/// The order in which windows lie on top of each other
	/// </summary>
	internal static class ZOrder
	{
		/// <summary>
		/// The window lying on top of the others
		/// </summary>
		public static IntPtr Top(IEnumerable<IntPtr> handles) => SortTopToBottom(handles).FirstOrDefault();

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
