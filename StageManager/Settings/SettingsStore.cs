using System;
using System.IO;
using System.Text.Json;

namespace StageManager.Settings
{
	public class SettingsStore
	{
		private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

		public SettingsStore(string path)
		{
			Path = path ?? throw new ArgumentNullException(nameof(path));
		}

		public static string DefaultDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageManager");

		public static SettingsStore CreateDefault() => new SettingsStore(System.IO.Path.Combine(DefaultDirectory, "settings.json"));

		public string Path { get; }

		/// <summary>
		/// Loads the settings. A missing or unreadable file results in default settings, so a broken file never prevents startup.
		/// </summary>
		public AppSettings Load()
		{
			try
			{
				if (!File.Exists(Path))
					return new AppSettings();

				var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), _jsonOptions) ?? new AppSettings();
				settings.PinnedApps ??= new();
				settings.ExcludedApps ??= new();
				settings.AppGroups ??= new();
				return settings;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
			{
				return new AppSettings();
			}
		}

		public void Save(AppSettings settings)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

			// write to a temp file first so that a crash while writing cannot leave a truncated settings file
			var tempPath = Path + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _jsonOptions));
			File.Move(tempPath, Path, overwrite: true);
		}
	}
}
