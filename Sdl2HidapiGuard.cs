using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PlayniteSense
{
    /// <summary>
    /// Reads the SDL2 HIDAPI startup state and provides explicit user-scope
    /// set/remove actions. It never changes the running process environment.
    /// </summary>
    public sealed class Sdl2HidapiGuard
    {
        public const string VariableName = "SDL_JOYSTICK_HIDAPI";

        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

        private readonly string startupProcessValue;
        private readonly bool isFullscreenProcess;

        private bool WasDisabledWhenProcessStarted => IsDisabledValue(startupProcessValue);
        private bool UserScopeIsConfigured => IsDisabledValue(GetUserValue());
        public bool RequiresFullscreenRestart => isFullscreenProcess && !WasDisabledWhenProcessStarted;
        public string LastConfigurationError { get; private set; }

        public Sdl2HidapiGuard()
        {
            // Capture the inherited value before doing anything. This is the value SDL2 saw.
            startupProcessValue = GetCurrentProcessValue();
            isFullscreenProcess = DetectFullscreenProcess();
        }

        /// <summary>
        /// Rewrites the persistent user variable to 0 without changing the running process.
        /// </summary>
        public bool ResetUserValueToZero()
        {
            LastConfigurationError = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    VariableName,
                    "0",
                    EnvironmentVariableTarget.User);
                BroadcastEnvironmentChange();
                return UserScopeIsConfigured;
            }
            catch (Exception ex)
            {
                LastConfigurationError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }


        /// <summary>
        /// Removes the persistent user variable so SDL2 returns to its default behavior
        /// for applications started after the environment change is observed.
        /// </summary>
        public bool RemoveUserValue()
        {
            LastConfigurationError = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    VariableName,
                    null,
                    EnvironmentVariableTarget.User);
                BroadcastEnvironmentChange();
                return string.IsNullOrWhiteSpace(GetUserValue());
            }
            catch (Exception ex)
            {
                LastConfigurationError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public string BuildDiagnosticText()
        {
            string startupValue = FormatValue(startupProcessValue);
            string userValue = FormatValue(GetUserValue());

            if (!isFullscreenProcess)
            {
                return "Desktop: startup=" + startupValue + "; user=" + userValue +
                       ". No SDL setting is changed in Desktop mode.";
            }

            if (RequiresFullscreenRestart)
            {
                return "Fullscreen: startup=" + startupValue + "; user=" + userValue +
                       ". Restart Fullscreen after setting " + VariableName + "=0.";
            }

            return "Fullscreen: startup=0; user=" + userValue + ". HIDAPI is disabled.";
        }

        private static string GetCurrentProcessValue()
        {
            return Environment.GetEnvironmentVariable(
                VariableName,
                EnvironmentVariableTarget.Process);
        }

        public static string GetUserValue()
        {
            return Environment.GetEnvironmentVariable(
                VariableName,
                EnvironmentVariableTarget.User);
        }

        private static bool DetectFullscreenProcess()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    string executablePath = process.MainModule == null
                        ? null
                        : process.MainModule.FileName;
                    string executableName = string.IsNullOrWhiteSpace(executablePath)
                        ? string.Empty
                        : Path.GetFileName(executablePath);

                    return string.Equals(
                        executableName,
                        "Playnite.FullscreenApp.exe",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDisabledValue(string value)
        {
            return string.Equals(
                value == null ? null : value.Trim(),
                "0",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<not set>" : "\"" + value + "\"";
        }

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(
                    HwndBroadcast,
                    WmSettingChange,
                    UIntPtr.Zero,
                    "Environment",
                    SmtoAbortIfHung,
                    2000,
                    out result);
            }
            catch
            {
                // The registry value remains saved if the broadcast is unavailable.
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);
    }
}
