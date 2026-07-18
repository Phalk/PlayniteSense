// Physical Sony controller input bridged to a ViGEm virtual controller.
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayniteSense
{
    public enum TargetType { Xbox360, DualShock4 }

    public sealed class VirtualPadBridge : IDisposable
    {
        private IPhysicalPad physicalPad;
        private readonly object feedSync = new object();
        private readonly object cleanupSync = new object();
        private ViGEmClient client;
        private IVirtualGamepad virtualPad;
        private TargetType currentTarget;
        private Thread loopThread;
        private volatile bool running;
        private bool hidHideCloakActive;
        private bool hidHideDeviceAddedBySession;
        private bool? previousCloakState;
        private bool? previousInverseState;
        private string cloakedDeviceInstanceId;
        private RealPadState lastSubmittedState;

        public event Action<string> StatusChanged;

        public bool IsRunning => running;
        public bool TryGetVirtualFeedSnapshot(out TargetType target, out RealPadState state)
        {
            lock (feedSync)
            {
                target = currentTarget;
                state = lastSubmittedState == null ? null : lastSubmittedState.Clone();
                return running && virtualPad != null && state != null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;
        private const uint OpenExisting = 3;

        public bool IsHidHideInstalled()
        {
            IntPtr handle = CreateFile(
                @"\\.\HidHide",
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                CloseHandle(handle);
                return true;
            }

            return false;
        }

        public bool VerifyDependenciesExist()
        {
            if (!IsHidHideInstalled() || !HidHideManager.IsAvailable())
            {
                return false;
            }

            try
            {
                using (new ViGEmClient())
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Start(TargetType type)
        {
            if (running)
            {
                return;
            }

            // Clear any resources left by a previous interrupted/disconnected session before
            // opening the physical controller again. This avoids requiring a controller replug.
            CleanupStoppedSession();

            currentTarget = type;

            if (!VerifyDependenciesExist())
            {
                StatusChanged?.Invoke("Prerequisites check failed: ViGEmBus and HidHide must be operational.");
                return;
            }

            if (!EnsurePlayniteIsWhitelisted())
            {
                return;
            }

            if (!TryOpenPhysicalController())
            {
                return;
            }

            StatusChanged?.Invoke(
                "Physical controller opened through " + physicalPad.InputBackend + ": " +
                physicalPad.DeviceName + " (" + physicalPad.ConnectionType + ").");

            if (!ConfigureHidHide())
            {
                ReleaseSessionResources(waitForDeviceVisibility: true);
                return;
            }

            try
            {
                client = new ViGEmClient();
                virtualPad = type == TargetType.DualShock4
                    ? (IVirtualGamepad)client.CreateDualShock4Controller()
                    : (IVirtualGamepad)client.CreateXbox360Controller();
                virtualPad.Connect();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Failed to create virtual gamepad: " + ex.Message);
                ReleaseSessionResources(waitForDeviceVisibility: true);
                return;
            }

            running = true;
            loopThread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "PlayniteSenseControllerBridge"
            };
            loopThread.Start();

            StatusChanged?.Invoke("Active: " + physicalPad.DeviceName + " via " + physicalPad.InputBackend + " -> Virtual " + type);
        }

        private bool TryOpenPhysicalController()
        {
            var nativePad = new RealPad();
            bool nativeConnected = nativePad.TryConnect();

            // Keep the native reader for normal USB/Bluetooth reports. If another SDL
            // application (Steam, Playnite Fullscreen, etc.) has switched the controller
            // to Bluetooth enhanced reports, prefer SDL in every Playnite mode.
            if (nativeConnected &&
                nativePad.ReportMode != PhysicalReportMode.BluetoothEnhanced)
            {
                physicalPad = nativePad;
                return true;
            }

            string deviceName = nativeConnected
                ? nativePad.DeviceName
                : nativePad.LastDetectedDeviceName;
            string deviceInstanceId = nativeConnected
                ? nativePad.DeviceInstanceId
                : nativePad.LastDetectedDeviceInstanceId;
            string connectionType = nativeConnected
                ? nativePad.ConnectionType
                : nativePad.LastDetectedConnectionType;
            ushort productId = nativeConnected
                ? nativePad.ProductId
                : nativePad.LastDetectedProductId;

            string fallbackReason = nativeConnected
                ? "Bluetooth enhanced reports were detected."
                : "Native HID did not receive a readable report: " +
                  (nativePad.LastError ?? "unknown error");

            StatusChanged?.Invoke(fallbackReason + " Trying SDL controller input.");

            // Keep nativePad alive until SDL has opened successfully. This gives Desktop
            // mode a safe fallback when SDL2.dll is unavailable or its controller subsystem
            // cannot be initialized in the current Playnite process.
            var sdlPad = new SdlPhysicalPad(
                deviceInstanceId,
                deviceName,
                connectionType,
                productId);

            if (sdlPad.TryConnect())
            {
                nativePad.Dispose();
                physicalPad = sdlPad;
                return true;
            }

            string sdlError = sdlPad.LastError ?? "unknown error";
            sdlPad.Dispose();

            if (nativeConnected)
            {
                StatusChanged?.Invoke(
                    "SDL controller input was unavailable (" + sdlError +
                    "). Continuing with native enhanced HID input.");
                physicalPad = nativePad;
                return true;
            }

            nativePad.Dispose();
            StatusChanged?.Invoke("SDL could not open the physical controller: " + sdlError);
            return false;
        }

        public void Stop()
        {
            bool hadResources = running ||
                                (physicalPad != null && physicalPad.IsConnected) ||
                                virtualPad != null ||
                                hidHideCloakActive ||
                                hidHideDeviceAddedBySession ||
                                previousCloakState.HasValue ||
                                previousInverseState.HasValue;
            if (!hadResources)
            {
                return;
            }

            running = false;
            Thread thread = loopThread;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(1500); } catch { }
            }

            bool restored = ReleaseSessionResources(waitForDeviceVisibility: true);
            loopThread = null;
            StatusChanged?.Invoke(restored
                ? "Stopped. The physical controller is available again."
                : "Stopped: HidHide could not be fully restored; check the diagnostic log.");
        }

        public bool EnsurePlayniteIsWhitelisted()
        {
            string currentExePath;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    currentExePath = process.MainModule == null
                        ? null
                        : process.MainModule.FileName;
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Could not resolve Playnite's executable path: " + ex.Message);
                return false;
            }

            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                StatusChanged?.Invoke("Could not resolve Playnite's executable path.");
                return false;
            }

            string installDirectory = System.IO.Path.GetDirectoryName(currentExePath);
            string[] candidates =
            {
                currentExePath,
                System.IO.Path.Combine(installDirectory, "Playnite.DesktopApp.exe"),
                System.IO.Path.Combine(installDirectory, "Playnite.FullscreenApp.exe")
            };

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!System.IO.File.Exists(candidate))
                {
                    continue;
                }

                CliResult result = HidHideManager.RegisterApplication(candidate);
                if (!result.Success)
                {
                    StatusChanged?.Invoke(
                        "Failed to whitelist Playnite in HidHide: " + candidate + " (" + result.Output + ").");
                    return false;
                }
            }

            return true;
        }

        private bool ConfigureHidHide()
        {
            if (physicalPad == null || string.IsNullOrWhiteSpace(physicalPad.DeviceInstanceId))
            {
                StatusChanged?.Invoke(
                    "Controller discovery did not resolve a device instance ID; the physical controller cannot be cloaked.");
                return false;
            }

            previousCloakState = null;
            previousInverseState = null;
            hidHideDeviceAddedBySession = false;
            hidHideCloakActive = false;
            cloakedDeviceInstanceId = physicalPad.DeviceInstanceId;

            bool state;
            CliResult cloakStateResult = HidHideManager.GetCloakState();
            if (!HidHideManager.TryReadSwitchState(
                cloakStateResult,
                "--cloak-on",
                "--cloak-off",
                out state))
            {
                StatusChanged?.Invoke(
                    "Could not read the current HidHide cloak state: " + cloakStateResult.Output);
                return false;
            }
            previousCloakState = state;

            CliResult inverseStateResult = HidHideManager.GetInverseState();
            if (!HidHideManager.TryReadSwitchState(
                inverseStateResult,
                "--inv-on",
                "--inv-off",
                out state))
            {
                StatusChanged?.Invoke(
                    "Could not read the current HidHide inverse state: " + inverseStateResult.Output);
                previousCloakState = null;
                return false;
            }
            previousInverseState = state;

            // Normal cloak mode is required: whitelisted Playnite can see the physical pad while
            // ordinary applications (including the launched game and joy.cpl) cannot.
            if (previousInverseState == true)
            {
                CliResult inverseOffResult = HidHideManager.SetInverseState(false);
                if (!inverseOffResult.Success)
                {
                    RestoreHidHideState();
                    StatusChanged?.Invoke(
                        "Could not disable HidHide inverse mode: " + inverseOffResult.Output);
                    return false;
                }
            }

            CliResult devicesBefore = HidHideManager.ListDevices();
            if (!devicesBefore.Success)
            {
                RestoreHidHideState();
                StatusChanged?.Invoke(
                    "Could not read HidHide's hidden-device list: " + devicesBefore.Output);
                return false;
            }

            bool wasAlreadyHidden = HidHideManager.ListContainsDevice(
                devicesBefore,
                physicalPad.DeviceInstanceId);

            CliResult hideResult = HidHideManager.HideDevice(physicalPad.DeviceInstanceId);
            if (!hideResult.Success)
            {
                RestoreHidHideState();
                StatusChanged?.Invoke(
                    "Could not add the physical controller to HidHide: " + hideResult.Output);
                return false;
            }

            hidHideDeviceAddedBySession = !wasAlreadyHidden;

            CliResult cloakResult = HidHideManager.SetCloakState(true);
            if (!cloakResult.Success)
            {
                RestoreHidHideState();
                StatusChanged?.Invoke("Could not enable HidHide: " + cloakResult.Output);
                return false;
            }

            CliResult verification = HidHideManager.ListDevices();
            if (!HidHideManager.ListContainsDevice(verification, physicalPad.DeviceInstanceId))
            {
                RestoreHidHideState();
                StatusChanged?.Invoke(
                    "HidHide did not retain the physical controller in its hidden-device list.");
                return false;
            }

            hidHideCloakActive = true;
            StatusChanged?.Invoke(
                "HidHide active: the physical controller is hidden from games; Playnite remains whitelisted.");
            return true;
        }

        private bool RemoveHidHideCloak()
        {
            if (!hidHideCloakActive &&
                !hidHideDeviceAddedBySession &&
                previousCloakState == null &&
                previousInverseState == null)
            {
                cloakedDeviceInstanceId = null;
                return true;
            }

            return RestoreHidHideState();
        }

        private bool RestoreHidHideState()
        {
            bool restored = true;

            // If cloak was originally off, disable it before removing the temporary device entry.
            // This makes the physical controller visible as early as possible after its HID handle
            // has been released.
            if (previousCloakState == false)
            {
                CliResult cloakOffResult = HidHideManager.SetCloakState(false);
                if (!cloakOffResult.Success)
                {
                    restored = false;
                    StatusChanged?.Invoke(
                        "Could not restore HidHide cloak state: " + cloakOffResult.Output);
                }
            }

            if (hidHideDeviceAddedBySession && !string.IsNullOrWhiteSpace(cloakedDeviceInstanceId))
            {
                CliResult unhideResult = HidHideManager.UnhideDevice(cloakedDeviceInstanceId);
                if (!unhideResult.Success)
                {
                    restored = false;
                    StatusChanged?.Invoke(
                        "Could not remove the physical controller from HidHide: " + unhideResult.Output);
                }
            }

            if (previousInverseState.HasValue)
            {
                CliResult inverseResult = HidHideManager.SetInverseState(previousInverseState.Value);
                if (!inverseResult.Success)
                {
                    restored = false;
                    StatusChanged?.Invoke(
                        "Could not restore HidHide inverse mode: " + inverseResult.Output);
                }
            }

            if (previousCloakState == true)
            {
                CliResult cloakOnResult = HidHideManager.SetCloakState(true);
                if (!cloakOnResult.Success)
                {
                    restored = false;
                    StatusChanged?.Invoke(
                        "Could not restore HidHide cloak state: " + cloakOnResult.Output);
                }
            }

            hidHideCloakActive = false;
            hidHideDeviceAddedBySession = false;
            previousCloakState = null;
            previousInverseState = null;
            cloakedDeviceInstanceId = null;
            return restored;
        }

        private void Loop()
        {
            try
            {
                while (running)
                {
                    IPhysicalPad pad = physicalPad;
                    if (pad == null)
                    {
                        running = false;
                        break;
                    }

                    if (!pad.Poll())
                    {
                        if (!pad.IsConnected)
                        {
                            StatusChanged?.Invoke(
                                "Physical controller input stopped: " +
                                (pad.LastError ?? "controller disconnected"));
                            running = false;
                            break;
                        }

                        Thread.Sleep(2);
                        continue;
                    }

                    RealPadState state = pad.State;
                    if (currentTarget == TargetType.Xbox360)
                    {
                        SubmitXbox360(state);
                    }
                    else
                    {
                        SubmitDualShock4(state);
                    }

                    lock (feedSync)
                    {
                        lastSubmittedState = state.Clone();
                    }

                    Thread.Sleep(4);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Virtual report submission failed: " + ex.Message);
                running = false;
            }
            finally
            {
                // Always release the selected input source before exposing the physical controller.
                ReleaseSessionResources(waitForDeviceVisibility: true);

                if (Thread.CurrentThread == loopThread)
                {
                    loopThread = null;
                }
            }
        }

        private void CleanupStoppedSession()
        {
            Thread thread = loopThread;
            if (thread != null && thread != Thread.CurrentThread)
            {
                running = false;
                try { thread.Join(1500); } catch { }
            }

            ReleaseSessionResources(waitForDeviceVisibility: true);
            loopThread = null;
        }

        private bool ReleaseSessionResources(bool waitForDeviceVisibility)
        {
            lock (cleanupSync)
            {
                bool hidHideWasConfigured = hidHideCloakActive ||
                                            hidHideDeviceAddedBySession ||
                                            previousCloakState.HasValue ||
                                            previousInverseState.HasValue;

                DisposeVirtualPad();

                // Release our input source before HidHide exposes the physical device again.
                if (physicalPad != null)
                {
                    try { physicalPad.Disconnect(); } catch { }
                    try { physicalPad.Dispose(); } catch { }
                    physicalPad = null;
                }

                bool restored = RemoveHidHideCloak();
                if (waitForDeviceVisibility && hidHideWasConfigured && restored)
                {
                    // HidHide changes are asynchronous from the perspective of applications that
                    // enumerate controllers. Give Windows a brief moment to publish the device.
                    Thread.Sleep(300);
                }

                return restored;
            }
        }

        private void SubmitXbox360(RealPadState state)
        {
            var controller = (IXbox360Controller)virtualPad;

            controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(state.X));
            controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(state.Y, true));
            controller.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(state.Z));
            controller.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(state.RotationZ, true));
            controller.SetSliderValue(Xbox360Slider.LeftTrigger, ToTrigger(state.RotationX));
            controller.SetSliderValue(Xbox360Slider.RightTrigger, ToTrigger(state.RotationY));

            bool[] buttons = state.Buttons;
            SetButtonXbox(controller, Xbox360Button.X, buttons, 0);
            SetButtonXbox(controller, Xbox360Button.A, buttons, 1);
            SetButtonXbox(controller, Xbox360Button.B, buttons, 2);
            SetButtonXbox(controller, Xbox360Button.Y, buttons, 3);
            SetButtonXbox(controller, Xbox360Button.LeftShoulder, buttons, 4);
            SetButtonXbox(controller, Xbox360Button.RightShoulder, buttons, 5);
            SetButtonXbox(controller, Xbox360Button.Back, buttons, 8);
            SetButtonXbox(controller, Xbox360Button.Start, buttons, 9);
            SetButtonXbox(controller, Xbox360Button.LeftThumb, buttons, 10);
            SetButtonXbox(controller, Xbox360Button.RightThumb, buttons, 11);
            SetButtonXbox(controller, Xbox360Button.Guide, buttons, 12);

            int pov = state.PointOfViewControllers.Length > 0
                ? state.PointOfViewControllers[0]
                : -1;
            SetDpadXbox(controller, pov);
            controller.SubmitReport();
        }

        private void SubmitDualShock4(RealPadState state)
        {
            var controller = (IDualShock4Controller)virtualPad;

            controller.SetAxisValue(DualShock4Axis.LeftThumbX, ToDs4Axis(state.X));
            controller.SetAxisValue(DualShock4Axis.LeftThumbY, ToDs4Axis(state.Y));
            controller.SetAxisValue(DualShock4Axis.RightThumbX, ToDs4Axis(state.Z));
            controller.SetAxisValue(DualShock4Axis.RightThumbY, ToDs4Axis(state.RotationZ));
            controller.SetSliderValue(DualShock4Slider.LeftTrigger, ToTrigger(state.RotationX));
            controller.SetSliderValue(DualShock4Slider.RightTrigger, ToTrigger(state.RotationY));

            bool[] buttons = state.Buttons;
            SetButtonDs4(controller, DualShock4Button.Square, buttons, 0);
            SetButtonDs4(controller, DualShock4Button.Cross, buttons, 1);
            SetButtonDs4(controller, DualShock4Button.Circle, buttons, 2);
            SetButtonDs4(controller, DualShock4Button.Triangle, buttons, 3);
            SetButtonDs4(controller, DualShock4Button.ShoulderLeft, buttons, 4);
            SetButtonDs4(controller, DualShock4Button.ShoulderRight, buttons, 5);
            SetButtonDs4(controller, DualShock4Button.Share, buttons, 8);
            SetButtonDs4(controller, DualShock4Button.Options, buttons, 9);
            SetButtonDs4(controller, DualShock4Button.ThumbLeft, buttons, 10);
            SetButtonDs4(controller, DualShock4Button.ThumbRight, buttons, 11);
            controller.SetButtonState(
                DualShock4SpecialButton.Ps,
                buttons.Length > 12 && buttons[12]);
            int pov = state.PointOfViewControllers.Length > 0
                ? state.PointOfViewControllers[0]
                : -1;
            SetDpadDs4(controller, pov);
            controller.SubmitReport();
        }

        private static void SetButtonXbox(
            IXbox360Controller controller,
            Xbox360Button button,
            bool[] source,
            int index)
        {
            controller.SetButtonState(button, index < source.Length && source[index]);
        }

        private static void SetButtonDs4(
            IDualShock4Controller controller,
            DualShock4Button button,
            bool[] source,
            int index)
        {
            controller.SetButtonState(button, index < source.Length && source[index]);
        }

        private static void SetDpadXbox(IXbox360Controller controller, int pov)
        {
            bool up = false;
            bool down = false;
            bool left = false;
            bool right = false;

            switch (pov)
            {
                case 0: up = true; break;
                case 4500: up = true; right = true; break;
                case 9000: right = true; break;
                case 13500: down = true; right = true; break;
                case 18000: down = true; break;
                case 22500: down = true; left = true; break;
                case 27000: left = true; break;
                case 31500: up = true; left = true; break;
            }

            controller.SetButtonState(Xbox360Button.Up, up);
            controller.SetButtonState(Xbox360Button.Down, down);
            controller.SetButtonState(Xbox360Button.Left, left);
            controller.SetButtonState(Xbox360Button.Right, right);
        }

        private static void SetDpadDs4(IDualShock4Controller controller, int pov)
        {
            DualShock4DPadDirection direction = DualShock4DPadDirection.None;
            switch (pov)
            {
                case 0: direction = DualShock4DPadDirection.North; break;
                case 4500: direction = DualShock4DPadDirection.Northeast; break;
                case 9000: direction = DualShock4DPadDirection.East; break;
                case 13500: direction = DualShock4DPadDirection.Southeast; break;
                case 18000: direction = DualShock4DPadDirection.South; break;
                case 22500: direction = DualShock4DPadDirection.Southwest; break;
                case 27000: direction = DualShock4DPadDirection.West; break;
                case 31500: direction = DualShock4DPadDirection.Northwest; break;
            }

            controller.SetDPadDirection(direction);
        }

        private static short ToAxis(int raw, bool invert = false)
        {
            int centered = raw - 32768;
            if (invert)
            {
                centered = -centered;
            }

            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, centered));
        }

        private static byte ToDs4Axis(int raw)
        {
            return (byte)Math.Max(0, Math.Min(255, raw >> 8));
        }

        private static byte ToTrigger(int raw)
        {
            return (byte)Math.Max(0, Math.Min(255, raw >> 8));
        }

        private void DisposeVirtualPad()
        {
            try { virtualPad?.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
            virtualPad = null;
            client = null;

            lock (feedSync)
            {
                lastSubmittedState = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
