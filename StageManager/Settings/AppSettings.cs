using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StageManager.Settings
{
	public class AppSettings
	{
		/// <summary>
		/// Apps (window group keys) that stay visible all the time and never switch scenes.
		/// </summary>
		public List<string> PinnedApps { get; set; } = new List<string>();

		/// <summary>
		/// Apps the user combined into one scene, see <see cref="Settings.AppGroups"/>.
		/// </summary>
		public List<List<string>> AppGroups { get; set; } = new List<List<string>>();

		/// <summary>
		/// macOS shows the desktop when the wallpaper is clicked
		/// </summary>
		public bool ClickWallpaperShowsDesktop { get; set; } = true;

		/// <summary>
		/// How the windows of the other scenes get out of the way
		/// </summary>
		[JsonConverter(typeof(JsonStringEnumConverter))]
		public HideMode HideMode { get; set; } = HideMode.Minimize;

		/// <summary>
		/// Windows fly between the stage and the sidebar when switching scenes
		/// </summary>
		public bool Animations { get; set; } = true;

		/// <summary>
		/// The desktop icon visibility before older versions hid the icons. Only set while they were running,
		/// so a value found on startup means one of them did not exit cleanly and the icons have to be restored.
		/// </summary>
		public bool? DesktopIconsVisibleBeforeStart { get; set; }
	}

	public enum HideMode
	{
		/// <summary>
		/// The windows of the other scenes are minimized, which works with Alt+Tab and the taskbar
		/// </summary>
		Minimize,

		/// <summary>
		/// The windows of the other scenes lie behind a copy of the wallpaper, where they keep running
		/// </summary>
		Backdrop
	}
}
