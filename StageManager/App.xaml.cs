using AsyncAwaitBestPractices;
using StageManager.Settings;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	public partial class App : Application
	{
		private const string SINGLE_INSTANCE_MUTEX = @"Local\StageManager.SingleInstance";
		private const long MAX_LOG_SIZE = 1024 * 1024;

		private static readonly object _logLock = new object();
		private Mutex? _singleInstanceMutex;

		/// <summary>
		/// Brings back the windows and desktop icons hidden by Stage Manager. Has to be callable from any thread.
		/// </summary>
		internal static Action? RestoreState { get; set; }

		public static string LogPath => Path.Combine(SettingsStore.DefaultDirectory, "error.log");

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			_singleInstanceMutex = new Mutex(initiallyOwned: true, SINGLE_INSTANCE_MUTEX, out var isFirstInstance);
			if (!isFirstInstance)
			{
				// a second instance would fight with the first one over the windows
				Shutdown();
				return;
			}

			DispatcherUnhandledException += App_DispatcherUnhandledException;
			AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
			TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
			SafeFireAndForgetExtensions.SetDefaultExceptionHandling(Log);

			new MainWindow().Show();
		}

		private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			// errors on the UI thread are mostly caused by windows that disappeared in the meantime,
			// keep running instead of leaving the user with hidden windows
			Log(e.Exception);
			e.Handled = true;
		}

		private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			// the process is going down, bring back everything we hid before it is gone
			Log(e.ExceptionObject as Exception);

			try
			{
				RestoreState?.Invoke();
			}
			catch (Exception restoreException)
			{
				Log(restoreException);
			}
		}

		private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
		{
			Log(e.Exception);
			e.SetObserved();
		}

		public static void Log(Exception? exception)
		{
			if (exception is null)
				return;

			try
			{
				lock (_logLock)
				{
					Directory.CreateDirectory(SettingsStore.DefaultDirectory);

					var file = new FileInfo(LogPath);
					if (file.Exists && file.Length > MAX_LOG_SIZE)
						file.Delete();

					File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}{Environment.NewLine}");
				}
			}
			catch (Exception)
			{
				// logging must never cause another error
			}
		}
	}
}
