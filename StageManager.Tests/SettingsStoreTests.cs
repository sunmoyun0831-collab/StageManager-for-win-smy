using System;
using System.IO;
using StageManager.Settings;
using Xunit;

namespace StageManager.Tests
{
	public class SettingsStoreTests : IDisposable
	{
		private readonly string _directory = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"));

		private string SettingsPath => Path.Combine(_directory, "nested", "settings.json");

		public void Dispose()
		{
			if (Directory.Exists(_directory))
				Directory.Delete(_directory, recursive: true);
		}

		[Fact]
		public void Missing_File_Loads_Defaults()
		{
			var settings = new SettingsStore(SettingsPath).Load();

			Assert.Empty(settings.PinnedApps);
			Assert.Empty(settings.AppGroups);
			Assert.Null(settings.DesktopIconsVisibleBeforeStart);
		}

		[Fact]
		public void Saved_Settings_Round_Trip()
		{
			var store = new SettingsStore(SettingsPath);
			store.Save(new AppSettings
			{
				PinnedApps = { "explorer" },
				AppGroups = { new() { "Code", "chrome" } },
				DesktopIconsVisibleBeforeStart = true
			});

			var loaded = store.Load();

			Assert.Equal(new[] { "explorer" }, loaded.PinnedApps);
			Assert.Equal(new[] { "Code", "chrome" }, loaded.AppGroups.Single());
			Assert.True(loaded.DesktopIconsVisibleBeforeStart);
			Assert.False(File.Exists(SettingsPath + ".tmp"));
		}

		[Fact]
		public void Corrupt_File_Loads_Defaults()
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			File.WriteAllText(SettingsPath, "{ not json");

			var settings = new SettingsStore(SettingsPath).Load();

			Assert.Empty(settings.PinnedApps);
		}

		[Fact]
		public void Null_Lists_In_File_Load_As_Empty()
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			File.WriteAllText(SettingsPath, "{ \"PinnedApps\": null, \"AppGroups\": null }");

			var settings = new SettingsStore(SettingsPath).Load();

			Assert.NotNull(settings.PinnedApps);
			Assert.NotNull(settings.AppGroups);
		}
	}
}
