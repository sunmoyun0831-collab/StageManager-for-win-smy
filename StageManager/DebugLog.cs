using StageManager.Native.Window;
using StageManager.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StageManager
{
	/// <summary>
	/// A detailed record of what Stage Manager does with the windows, turned on from the tray menu to track down problems.
	/// Off by default and not remembered, so it can't be left running by accident.
	/// </summary>
	public static class DebugLog
	{
		private const long MAX_SIZE = 5 * 1024 * 1024;

		private static readonly object _lock = new object();
		private static bool _enabled;

		public static string Path => System.IO.Path.Combine(SettingsStore.DefaultDirectory, "debug.log");

		public static bool Enabled
		{
			get => _enabled;
			set
			{
				_enabled = value;
				Write(value ? "Debug log started" : "Debug log stopped", force: true);
			}
		}

		public static void Write(string message) => Write(message, force: false);

		private static void Write(string message, bool force)
		{
			if (!_enabled && !force)
				return;

			try
			{
				lock (_lock)
				{
					Directory.CreateDirectory(SettingsStore.DefaultDirectory);

					var file = new FileInfo(Path);
					if (file.Exists && file.Length > MAX_SIZE)
						file.Delete();

					File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
				}
			}
			catch (Exception)
			{
				// logging must never cause another error
			}
		}

		public static string Describe(IWindow? window)
		{
			if (window is null)
				return "(none)";

			var title = window.Title;
			if (title.Length > 40)
				title = title.Substring(0, 40) + "...";

			return $"0x{window.Handle.ToInt64():X} {window.ProcessName} \"{title}\"";
		}

		public static string Describe(Scene? scene) => scene is null ? "(desktop)" : $"[{string.Join(", ", scene.Windows.Select(w => w.ProcessName).Distinct())}]";

		public static string Describe(IEnumerable<IntPtr> handles) => string.Join(", ", handles.Select(h => $"0x{h.ToInt64():X}"));
	}
}
