// Reads a supported Sony controller through SDL2. It reuses Playnite's already-open
// controller when available. In Desktop mode it can initialize only SDL's game-controller
// subsystem for this bridge, without polling or consuming Playnite UI events.
using System;
using System.Runtime.InteropServices;

namespace PlayniteSense
{
    public sealed class SdlPhysicalPad : IPhysicalPad
    {
        private const string SdlLibrary = "SDL2.dll";
        private const uint SdlInitGameController = 0x00002000;

        private const ushort SonyVendorId = 0x054C;
        private const ushort DualSenseProductId = 0x0CE6;
        private const ushort DualSenseEdgeProductId = 0x0DF2;
        private const ushort DualShock4V1ProductId = 0x05C4;
        private const ushort DualShock4V2ProductId = 0x09CC;

        private IntPtr controller = IntPtr.Zero;
        private bool ownsController;
        private bool ownsGameControllerSubsystem;
        private bool reusedHostController;
        private RealPadState state = new RealPadState();

        private readonly string detectedDeviceInstanceId;
        private readonly string detectedDeviceName;
        private readonly string detectedConnectionType;
        private readonly ushort preferredProductId;

        public SdlPhysicalPad(
            string deviceInstanceId,
            string deviceName,
            string connectionType,
            ushort productId)
        {
            detectedDeviceInstanceId = deviceInstanceId;
            detectedDeviceName = deviceName;
            detectedConnectionType = connectionType;
            preferredProductId = productId;
        }

        public bool IsConnected
        {
            get
            {
                if (controller == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    return SDL_GameControllerGetAttached(controller) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public string DeviceName { get; private set; }
        public string DeviceInstanceId => detectedDeviceInstanceId;
        public string ConnectionType => string.IsNullOrWhiteSpace(detectedConnectionType)
            ? "SDL"
            : detectedConnectionType;
        public string InputBackend
        {
            get
            {
                if (reusedHostController)
                {
                    return "SDL2 (host controller)";
                }

                return ownsGameControllerSubsystem
                    ? "SDL2 (plugin controller subsystem)"
                    : "SDL2";
            }
        }
        public string LastError { get; private set; }
        public RealPadState State => state.Clone();

        public bool TryConnect()
        {
            if (IsConnected)
            {
                return true;
            }

            Disconnect();
            LastError = null;

            try
            {
                if ((SDL_WasInit(SdlInitGameController) & SdlInitGameController) == 0)
                {
                    if (SDL_InitSubSystem(SdlInitGameController) != 0)
                    {
                        LastError = "SDL could not initialize controller input: " + GetSdlError();
                        return false;
                    }

                    ownsGameControllerSubsystem = true;
                }

                // Keep SDL's state current without consuming events from Playnite's queue.
                SDL_GameControllerUpdate();

                int count = SDL_NumJoysticks();
                if (count < 0)
                {
                    LastError = "SDL could not enumerate controllers: " + GetSdlError();
                    return false;
                }

                int selectedIndex = FindControllerIndex(count, requirePreferredProduct: true);
                if (selectedIndex < 0)
                {
                    selectedIndex = FindControllerIndex(count, requirePreferredProduct: false);
                }

                if (selectedIndex < 0)
                {
                    LastError = "SDL did not expose a supported Sony controller.";
                    return false;
                }

                int instanceId = SDL_JoystickGetDeviceInstanceID(selectedIndex);
                if (instanceId >= 0)
                {
                    controller = SDL_GameControllerFromInstanceID(instanceId);
                }

                if (controller == IntPtr.Zero)
                {
                    controller = SDL_GameControllerOpen(selectedIndex);
                    ownsController = controller != IntPtr.Zero;
                    reusedHostController = false;
                }
                else
                {
                    // The controller belongs to the host SDL lifecycle. Do not close it.
                    ownsController = false;
                    reusedHostController = true;
                }

                if (controller == IntPtr.Zero)
                {
                    LastError = "SDL could not open the controller: " + GetSdlError();
                    return false;
                }

                DeviceName = PointerToString(SDL_GameControllerName(controller));
                if (string.IsNullOrWhiteSpace(DeviceName))
                {
                    DeviceName = detectedDeviceName;
                }
                if (string.IsNullOrWhiteSpace(DeviceName))
                {
                    DeviceName = "Sony Wireless Controller";
                }

                if (!Poll())
                {
                    LastError = "SDL opened the controller but did not provide a readable state.";
                    Disconnect();
                    return false;
                }

                return true;
            }
            catch (DllNotFoundException)
            {
                LastError = "SDL2.dll is not available in this Playnite process.";
            }
            catch (EntryPointNotFoundException ex)
            {
                LastError = "Playnite's SDL compatibility library is missing a required SDL2 API: " + ex.Message;
            }
            catch (BadImageFormatException)
            {
                LastError = "The SDL2 library architecture does not match the plugin architecture.";
            }
            catch (Exception ex)
            {
                LastError = "SDL input failed: " + ex.Message;
            }

            Disconnect();
            return false;
        }

        public bool Poll()
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                SDL_GameControllerUpdate();

                var buttons = new bool[20];

                // SDL's semantic mapping is A=Cross, B=Circle, X=Square, Y=Triangle.
                buttons[0] = GetButton(SdlControllerButton.X);
                buttons[1] = GetButton(SdlControllerButton.A);
                buttons[2] = GetButton(SdlControllerButton.B);
                buttons[3] = GetButton(SdlControllerButton.Y);
                buttons[4] = GetButton(SdlControllerButton.LeftShoulder);
                buttons[5] = GetButton(SdlControllerButton.RightShoulder);
                buttons[6] = GetAxis(SdlControllerAxis.TriggerLeft) > 1024;
                buttons[7] = GetAxis(SdlControllerAxis.TriggerRight) > 1024;
                buttons[8] = GetButton(SdlControllerButton.Back);
                buttons[9] = GetButton(SdlControllerButton.Start);
                buttons[10] = GetButton(SdlControllerButton.LeftStick);
                buttons[11] = GetButton(SdlControllerButton.RightStick);
                buttons[12] = GetButton(SdlControllerButton.Guide);
                buttons[13] = GetButton(SdlControllerButton.Touchpad);
                buttons[14] = GetButton(SdlControllerButton.Misc1);
                buttons[15] = GetButton(SdlControllerButton.Paddle1);
                buttons[16] = GetButton(SdlControllerButton.Paddle2);
                buttons[17] = GetButton(SdlControllerButton.Paddle3);
                buttons[18] = GetButton(SdlControllerButton.Paddle4);

                bool up = GetButton(SdlControllerButton.DpadUp);
                bool down = GetButton(SdlControllerButton.DpadDown);
                bool left = GetButton(SdlControllerButton.DpadLeft);
                bool right = GetButton(SdlControllerButton.DpadRight);

                state = new RealPadState
                {
                    X = ExpandStick(GetAxis(SdlControllerAxis.LeftX)),
                    Y = ExpandStick(GetAxis(SdlControllerAxis.LeftY)),
                    Z = ExpandStick(GetAxis(SdlControllerAxis.RightX)),
                    RotationZ = ExpandStick(GetAxis(SdlControllerAxis.RightY)),
                    RotationX = ExpandTrigger(GetAxis(SdlControllerAxis.TriggerLeft)),
                    RotationY = ExpandTrigger(GetAxis(SdlControllerAxis.TriggerRight)),
                    Buttons = buttons,
                    PointOfViewControllers = new[] { DpadToPov(up, down, left, right) }
                };

                return true;
            }
            catch (Exception ex)
            {
                LastError = "SDL state polling failed: " + ex.Message;
                return false;
            }
        }

        public void Disconnect()
        {
            IntPtr current = controller;
            bool closeOwnedReference = ownsController;
            bool quitOwnedSubsystem = ownsGameControllerSubsystem;

            controller = IntPtr.Zero;
            ownsController = false;
            ownsGameControllerSubsystem = false;
            reusedHostController = false;
            DeviceName = null;
            state = new RealPadState();

            if (closeOwnedReference && current != IntPtr.Zero)
            {
                try { SDL_GameControllerClose(current); } catch { }
            }

            if (quitOwnedSubsystem)
            {
                try { SDL_QuitSubSystem(SdlInitGameController); } catch { }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private int FindControllerIndex(int count, bool requirePreferredProduct)
        {
            for (int index = 0; index < count; index++)
            {
                if (SDL_IsGameController(index) == 0)
                {
                    continue;
                }

                ushort vendor = SDL_JoystickGetDeviceVendor(index);
                ushort product = SDL_JoystickGetDeviceProduct(index);
                string name = PointerToString(SDL_GameControllerNameForIndex(index));

                if (requirePreferredProduct && preferredProductId != 0 && product != preferredProductId)
                {
                    continue;
                }

                if (vendor == SonyVendorId && IsSupportedProduct(product))
                {
                    return index;
                }

                // Some Bluetooth stacks do not expose VID/PID through this SDL API.
                if (vendor == 0 && LooksLikeSonyController(name))
                {
                    return index;
                }
            }

            return -1;
        }

        private short GetAxis(SdlControllerAxis axis)
        {
            return SDL_GameControllerGetAxis(controller, axis);
        }

        private bool GetButton(SdlControllerButton button)
        {
            return SDL_GameControllerGetButton(controller, button) != 0;
        }

        private static int ExpandStick(short value)
        {
            return value - short.MinValue;
        }

        private static int ExpandTrigger(short value)
        {
            int positive = Math.Max(0, (int)value);
            return Math.Min(65535, positive * 2 + (positive > 0 ? 1 : 0));
        }

        private static int DpadToPov(bool up, bool down, bool left, bool right)
        {
            if (up && right && !down && !left) return 4500;
            if (down && right && !up && !left) return 13500;
            if (down && left && !up && !right) return 22500;
            if (up && left && !down && !right) return 31500;
            if (up && !down && !left && !right) return 0;
            if (right && !left && !up && !down) return 9000;
            if (down && !up && !left && !right) return 18000;
            if (left && !right && !up && !down) return 27000;
            return -1;
        }

        private static bool IsSupportedProduct(ushort product)
        {
            return product == DualSenseProductId ||
                   product == DualSenseEdgeProductId ||
                   product == DualShock4V1ProductId ||
                   product == DualShock4V2ProductId;
        }

        private static bool LooksLikeSonyController(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("DUALSHOCK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string PointerToString(IntPtr value)
        {
            return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value);
        }

        private static string GetSdlError()
        {
            try
            {
                string error = PointerToString(SDL_GetError());
                return string.IsNullOrWhiteSpace(error) ? "unknown SDL error" : error;
            }
            catch
            {
                return "unknown SDL error";
            }
        }

        private enum SdlControllerAxis
        {
            Invalid = -1,
            LeftX,
            LeftY,
            RightX,
            RightY,
            TriggerLeft,
            TriggerRight
        }

        private enum SdlControllerButton
        {
            Invalid = -1,
            A,
            B,
            X,
            Y,
            Back,
            Guide,
            Start,
            LeftStick,
            RightStick,
            LeftShoulder,
            RightShoulder,
            DpadUp,
            DpadDown,
            DpadLeft,
            DpadRight,
            Misc1,
            Paddle1,
            Paddle2,
            Paddle3,
            Paddle4,
            Touchpad
        }

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_WasInit(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GameControllerUpdate();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_NumJoysticks();

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_IsGameController(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_JoystickGetDeviceVendor(int deviceIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_JoystickGetDeviceProduct(int deviceIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerNameForIndex(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerFromInstanceID(int joystickInstanceId);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerOpen(int joystickIndex);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GameControllerClose(IntPtr gameController);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GameControllerGetAttached(IntPtr gameController);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerName(IntPtr gameController);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern short SDL_GameControllerGetAxis(IntPtr gameController, SdlControllerAxis axis);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SDL_GameControllerGetButton(IntPtr gameController, SdlControllerButton button);

        [DllImport(SdlLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();
    }
}
