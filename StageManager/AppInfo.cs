namespace StageManager
{
	/// <param name="Key">The window group key of the app, see <see cref="SceneManager"/></param>
	/// <param name="Name">A readable name to show to the user</param>
	public record AppInfo(string Key, string Name, bool IsPinned);
}
