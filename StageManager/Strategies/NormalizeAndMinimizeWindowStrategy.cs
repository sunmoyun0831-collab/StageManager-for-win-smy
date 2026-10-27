using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Strategies
{
    internal class NormalizeAndMinimizeWindowStrategy : IWindowStrategy
	{
		// ShowWindowAsync because ShowWindow waits for the app, which blocks Stage Manager as long as an app hangs

		public void Show(IWindow window)
		{
			// restoring a window that is not minimized would turn a maximized window into a normal one,
			// and hidden windows have to stay hidden, even if they are minimized
			if (window.IsMinimized && Win32.IsWindowVisible(window.Handle))
				Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_SHOWNOACTIVATE);
		}

		public void Hide(IWindow window)
		{
			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_MINIMIZE);
		}
	}
}
