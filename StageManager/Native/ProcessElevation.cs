using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	internal static class ProcessElevation
	{
		private const uint TOKEN_QUERY = 0x0008;
		private const int TokenElevation = 20;

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

		/// <summary>
		/// Whether the process runs as administrator. Windows of such processes can't be moved or reordered by
		/// Stage Manager unless it runs as administrator itself. Null if it can't be told.
		/// </summary>
		public static bool? IsElevated(int processId)
		{
			var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
			if (process == IntPtr.Zero)
				return null;

			try
			{
				// the token of an elevated process can't be read from a process that is not elevated
				if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
					return true;

				try
				{
					return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null;
				}
				finally
				{
					Win32.CloseHandle(token);
				}
			}
			finally
			{
				Win32.CloseHandle(process);
			}
		}
	}
}
