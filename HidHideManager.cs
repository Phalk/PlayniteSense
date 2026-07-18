using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace PlayniteSense
{
    public struct CliResult
    {
        public bool Success;
        public string Output;
    }

    /// <summary>
    /// Small wrapper around HidHideCLI.exe, the supported HidHide integration surface.
    /// Playnite must be whitelisted because this plugin runs inside the Playnite process and
    /// still needs to read the physical controller after the device is cloaked from games.
    /// </summary>
    public static class HidHideManager
    {
        private const int CliTimeoutMs = 5000;
        private static string cachedCliPath;

        public static string FindCliPath()
        {
            if (cachedCliPath != null && File.Exists(cachedCliPath))
            {
                return cachedCliPath;
            }

            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(
                    @"SOFTWARE\Nefarius Software Solutions e.U.\Nefarius Software Solutions e.U. HidHide"))
                {
                    var installPath = key == null ? null : key.GetValue("Path") as string;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        string candidate = Path.Combine(installPath, "x64", "HidHideCLI.exe");
                        if (File.Exists(candidate))
                        {
                            cachedCliPath = candidate;
                            return cachedCliPath;
                        }

                        candidate = Path.Combine(installPath, "HidHideCLI.exe");
                        if (File.Exists(candidate))
                        {
                            cachedCliPath = candidate;
                            return cachedCliPath;
                        }
                    }
                }
            }
            catch
            {
                // Continue with the standard installation paths.
            }

            string[] fallbacks =
            {
                @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
                @"C:\Program Files\Nefarius Software Solutions e.U\HidHide\x64\HidHideCLI.exe"
            };

            foreach (string path in fallbacks)
            {
                if (File.Exists(path))
                {
                    cachedCliPath = path;
                    return cachedCliPath;
                }
            }

            return null;
        }

        public static bool IsAvailable()
        {
            return FindCliPath() != null;
        }

        private static CliResult RunCli(string arguments)
        {
            string cliPath = FindCliPath();
            if (cliPath == null)
            {
                return new CliResult
                {
                    Success = false,
                    Output = "HidHideCLI.exe was not found."
                };
            }

            try
            {
                var startInfo = new ProcessStartInfo(cliPath, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new CliResult
                        {
                            Success = false,
                            Output = "Windows could not start HidHideCLI.exe."
                        };
                    }

                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(CliTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return new CliResult
                        {
                            Success = false,
                            Output = "HidHideCLI.exe timed out."
                        };
                    }

                    string output = (stdout + stderr).Trim();
                    return new CliResult
                    {
                        Success = process.ExitCode == 0,
                        Output = output
                    };
                }
            }
            catch (Exception ex)
            {
                return new CliResult
                {
                    Success = false,
                    Output = "HidHideCLI error: " + ex.Message
                };
            }
        }

        public static CliResult RegisterApplication(string exePath)
        {
            return RunCli("--app-reg \"" + exePath + "\"");
        }

        public static CliResult HideDevice(string deviceInstancePath)
        {
            return RunCli("--dev-hide \"" + deviceInstancePath + "\"");
        }

        public static CliResult UnhideDevice(string deviceInstancePath)
        {
            return RunCli("--dev-unhide \"" + deviceInstancePath + "\"");
        }

        public static CliResult SetCloakState(bool enabled)
        {
            return RunCli(enabled ? "--cloak-on" : "--cloak-off");
        }

        public static CliResult SetInverseState(bool enabled)
        {
            return RunCli(enabled ? "--inv-on" : "--inv-off");
        }

        public static CliResult GetCloakState()
        {
            return RunCli("--cloak-state");
        }

        public static CliResult GetInverseState()
        {
            return RunCli("--inv-state");
        }

        public static CliResult ListDevices()
        {
            return RunCli("--dev-list");
        }

        public static bool TryReadSwitchState(CliResult result, string enabledToken, string disabledToken, out bool enabled)
        {
            enabled = false;
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                return false;
            }

            if (result.Output.IndexOf(enabledToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                enabled = true;
                return true;
            }

            if (result.Output.IndexOf(disabledToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                enabled = false;
                return true;
            }

            return false;
        }

        public static bool ListContainsDevice(CliResult result, string deviceInstancePath)
        {
            return result.Success &&
                   !string.IsNullOrWhiteSpace(deviceInstancePath) &&
                   result.Output != null &&
                   result.Output.IndexOf(deviceInstancePath, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
