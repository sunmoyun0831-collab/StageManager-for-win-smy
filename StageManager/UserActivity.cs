using System;
using System.Threading;

namespace StageManager
{
	/// <summary>
	/// Remembers when the user last clicked or pressed Alt or the Windows key. Switching to another window takes one of those
	/// (clicking it, the taskbar or a notification, Alt+Tab, Win+number), apps that bring themselves to the front don't.
	/// Written from the hook thread, read on the UI thread.
	/// </summary>
	public class UserActivity
	{
		private long _lastAction;

		public void Acted() => Interlocked.Exchange(ref _lastAction, Environment.TickCount64);

		public bool ActedWithin(int milliseconds) => Environment.TickCount64 - Interlocked.Read(ref _lastAction) <= milliseconds;
	}
}
