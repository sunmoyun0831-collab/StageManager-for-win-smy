using System.Collections.Generic;

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
		/// The desktop icon visibility before Stage Manager hid the icons. Only set while Stage Manager is running,
		/// so a value found on startup means the last session did not exit cleanly and the icons have to be restored.
		/// </summary>
		public bool? DesktopIconsVisibleBeforeStart { get; set; }
	}
}
