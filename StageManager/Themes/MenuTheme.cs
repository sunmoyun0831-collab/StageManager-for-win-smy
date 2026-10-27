using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Media;

namespace StageManager.Themes
{
	/// <summary>
	/// Colors the menus like Windows 11 does, light or dark like the taskbar and with the accent color for switches.
	/// Follows changes to the Windows settings while Stage Manager runs.
	/// </summary>
	internal static class MenuTheme
	{
		private const string PERSONALIZE_KEY = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
		private const string ACCENT_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

		/// <summary>
		/// The accent palette holds 8 colors from light to dark, Windows 11 uses a lighter one on dark and a darker one on light backgrounds
		/// </summary>
		private const int ACCENT_ON_DARK = 1;
		private const int ACCENT_ON_LIGHT = 4;

		public static void Start(Application application)
		{
			Apply(application.Resources);

			SystemEvents.UserPreferenceChanged += (_, e) =>
			{
				if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
					application.Dispatcher.BeginInvoke(() => Apply(application.Resources));
			};
		}

		private static void Apply(ResourceDictionary resources)
		{
			// a broken setting must not keep the menus from opening, they keep their colors then
			try
			{
				var isLight = IsLightTheme();

				Set(resources, "Menu.Background", isLight ? 0xFFF9F9F9 : 0xFF2C2C2C);
				Set(resources, "Menu.Border", isLight ? 0x1F000000 : 0x1FFFFFFF);
				Set(resources, "Menu.Foreground", isLight ? 0xE4000000 : 0xFFFFFFFF);
				Set(resources, "Menu.SecondaryForeground", isLight ? 0x9B000000 : 0xC5FFFFFF);
				Set(resources, "Menu.DisabledForeground", isLight ? 0x5C000000 : 0x5DFFFFFF);
				Set(resources, "Menu.Hover", isLight ? 0x0F000000 : 0x0FFFFFFF);
				Set(resources, "Menu.Pressed", isLight ? 0x08000000 : 0x0AFFFFFF);
				Set(resources, "Menu.Separator", isLight ? 0x14000000 : 0x15FFFFFF);
				Set(resources, "Menu.ToggleOff", isLight ? 0x9B000000 : 0x8BFFFFFF);
				Set(resources, "Menu.OnAccent", isLight ? 0xFFFFFFFF : 0xFF000000);
				resources["Menu.Accent"] = Freeze(new SolidColorBrush(GetAccent(isLight)));
			}
			catch (Exception ex)
			{
				App.Log(ex);
			}
		}

		/// <summary>
		/// The menus belong to the taskbar and sidebar, so they follow the Windows mode rather than the app mode
		/// </summary>
		private static bool IsLightTheme()
		{
			using var key = Registry.CurrentUser.OpenSubKey(PERSONALIZE_KEY);
			var value = key?.GetValue("SystemUsesLightTheme") ?? key?.GetValue("AppsUseLightTheme");
			return value is not int light || light != 0;
		}

		private static Color GetAccent(bool isLight)
		{
			using var key = Registry.CurrentUser.OpenSubKey(ACCENT_KEY);

			if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
			{
				// red, green, blue and alpha for each color
				var offset = (isLight ? ACCENT_ON_LIGHT : ACCENT_ON_DARK) * 4;
				return Color.FromRgb(palette[offset], palette[offset + 1], palette[offset + 2]);
			}

			// the default blue of Windows 11
			return isLight ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF);
		}

		private static void Set(ResourceDictionary resources, string key, long argb)
		{
			var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
			resources[key] = Freeze(new SolidColorBrush(color));
		}

		private static SolidColorBrush Freeze(SolidColorBrush brush)
		{
			brush.Freeze();
			return brush;
		}
	}
}
