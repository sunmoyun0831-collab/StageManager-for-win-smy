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
		/// Windows fly between the stage and the sidebar when switching scenes
		/// </summary>
		public bool Animations { get; set; } = true;

		/// <summary>
		/// The desktop icon visibility before older versions hid the icons. Only set while they were running,
		/// so a value found on startup means one of them did not exit cleanly and the icons have to be restored.
		/// </summary>
		public bool? DesktopIconsVisibleBeforeStart { get; set; }
	}
}
