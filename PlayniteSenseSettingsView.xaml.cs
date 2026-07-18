using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using SharpDX.XInput;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Threading;

namespace PlayniteSense
{
    public partial class PlayniteSenseSettingsView : UserControl
    {
        private readonly VirtualPadBridge bridge;
        private readonly RealPad diagnosticPad = new RealPad();
        private readonly DispatcherTimer liveTimer;
        private bool manualBridgeStartedByView;

        public PlayniteSenseSettingsView(PlayniteSenseSettingsViewModel viewModel, VirtualPadBridge bridge)
        {
            InitializeComponent();
            this.bridge = bridge;
            DataContext = viewModel;

            bridge.StatusChanged += OnBridgeStatusChanged;

            liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            liveTimer.Tick += LiveTimer_Tick;
            liveTimer.Start();

            Unloaded += (s, e) =>
            {
                liveTimer.Stop();
                bridge.StatusChanged -= OnBridgeStatusChanged;

                if (manualBridgeStartedByView)
                {
                    bridge.Stop();
                    manualBridgeStartedByView = false;
                }

                diagnosticPad.Dispose();
            };

            Log("Diagnostic panel loaded.");
        }

        private void OnBridgeStatusChanged(string status) =>
            Dispatcher.Invoke(() => Log("[Bridge] " + status));

        private void Log(string message)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n");
            txtLog.ScrollToEnd();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch { }
        }

        private void BtnTestPad_Click(object sender, RoutedEventArgs e)
        {
            if (bridge.IsRunning)
            {
                Log("Stop the controller bridge before testing the physical controller directly.");
                return;
            }

            if (diagnosticPad.IsConnected)
            {
                Log("Controller already connected through native Windows HID — check the live data below.");
                return;
            }

            if (diagnosticPad.TryConnect())
            {
                Log("SUCCESS: Native HID opened \"" + diagnosticPad.DeviceName + "\" over " +
                    diagnosticPad.ConnectionType + ".");
                Log("Report mode: " + diagnosticPad.ReportMode + ".");
                Log("Device instance ID: " + (diagnosticPad.DeviceInstanceId ?? "(not resolved)"));
            }
            else
            {
                Log("FAILED: Native HID could not open the controller. " +
                    (diagnosticPad.LastError ?? "Check USB/Bluetooth connectivity."));
            }
        }

        private void BtnCheckDriver_Click(object sender, RoutedEventArgs e)
        {
            bool vigemOk = false;
            bool hidHideOk = bridge.IsHidHideInstalled();

            try
            {
                using (new ViGEmClient())
                {
                    vigemOk = true;
                    Log("SUCCESS: ViGEmBus kernel driver detected and operational.");
                }
            }
            catch (VigemBusNotFoundException)
            {
                Log("FAILED: ViGEmBus driver is missing or not installed on this system.");
            }
            catch (Exception ex)
            {
                Log("FAILED: ViGEmBus initialization threw an exception: " + ex.Message);
            }

            Log(hidHideOk
                ? "SUCCESS: HidHide control driver node found and accessible."
                : "FAILED: HidHide driver node is missing or non-functional.");

            Log(vigemOk && hidHideOk
                ? "STATUS: ViGEmBus and HidHide are ready."
                : "STATUS: One or more required dependencies are unavailable.");
        }

        private void BtnStartBridge_Click(object sender, RoutedEventArgs e)
        {
            if (!bridge.VerifyDependenciesExist())
            {
                Log("Cannot start: ViGEmBus or HidHide is unavailable.");
                return;
            }

            TargetType mode = cmbTargetMode.SelectedIndex == 1
                ? TargetType.DualShock4
                : TargetType.Xbox360;

            try
            {
                // Never keep the separate diagnostic HID reader open while the bridge is active.
                // Releasing it first prevents two readers from holding the same controller.
                diagnosticPad.Disconnect();
                txtRawPad.Text = "Released while the controller bridge is running.";
                Thread.Sleep(120);

                bool wasAlreadyRunning = bridge.IsRunning;
                txtXInput.Text = "Starting controller bridge...";
                bridge.Start(mode);
                manualBridgeStartedByView = !wasAlreadyRunning && bridge.IsRunning;

                if (!bridge.IsRunning)
                {
                    txtXInput.Text = "Bridge could not be started. Check the diagnostic log.";
                }
            }
            catch (Exception ex)
            {
                txtXInput.Text = "Bridge could not be started.";
                Log("Failed to start the controller bridge: " + ex.GetType().FullName + ": " +
                    ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void BtnStopBridge_Click(object sender, RoutedEventArgs e)
        {
            bridge.Stop();
            manualBridgeStartedByView = false;
            txtXInput.Text = "Bridge is stopped. The physical controller is available.";
            txtRawPad.Text = "Click Test Physical Controller to read direct input.";
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
        }

        private void LiveTimer_Tick(object sender, EventArgs e)
        {
            if (diagnosticPad.IsConnected && diagnosticPad.Poll())
            {
                RealPadState state = diagnosticPad.State;
                var pressed = state.Buttons
                    .Select((pressedState, index) => new { pressedState, index })
                    .Where(item => item.pressedState)
                    .Select(item => item.index);
                int pov = state.PointOfViewControllers.Length > 0
                    ? state.PointOfViewControllers[0]
                    : -1;

                txtRawPad.Text =
                    "X:" + state.X + " Y:" + state.Y + " Z:" + state.Z +
                    " | RX:" + state.RotationX + " RY:" + state.RotationY +
                    " RZ:" + state.RotationZ + " | POV:" + pov +
                    " | Buttons:[" + string.Join(",", pressed) + "]";
            }

            TargetType target;
            RealPadState virtualState;
            if (!bridge.TryGetVirtualFeedSnapshot(out target, out virtualState))
            {
                txtXInput.Text = "Bridge is stopped.";
                return;
            }

            if (target == TargetType.DualShock4)
            {
                txtXInput.Text =
                    "Virtual DS4 | LX:" + ToByteAxis(virtualState.X) +
                    " LY:" + ToByteAxis(virtualState.Y) +
                    " | RX:" + ToByteAxis(virtualState.Z) +
                    " RY:" + ToByteAxis(virtualState.RotationZ) +
                    " | L2:" + ToByteAxis(virtualState.RotationX) +
                    " R2:" + ToByteAxis(virtualState.RotationY);
                return;
            }

            for (int i = 0; i < 4; i++)
            {
                var controller = new Controller((UserIndex)i);
                if (controller.IsConnected)
                {
                    var gamepad = controller.GetState().Gamepad;
                    txtXInput.Text =
                        "Virtual X360 — Slot " + i + " | LX:" + gamepad.LeftThumbX +
                        " LY:" + gamepad.LeftThumbY + " | RX:" + gamepad.RightThumbX +
                        " RY:" + gamepad.RightThumbY + " | LT:" + gamepad.LeftTrigger +
                        " RT:" + gamepad.RightTrigger;
                    return;
                }
            }

            txtXInput.Text =
                "Virtual X360 reports active | LX:" + ToSignedAxis(virtualState.X, false) +
                " LY:" + ToSignedAxis(virtualState.Y, true) +
                " | RX:" + ToSignedAxis(virtualState.Z, false) +
                " RY:" + ToSignedAxis(virtualState.RotationZ, true) +
                " | LT:" + ToByteAxis(virtualState.RotationX) +
                " RT:" + ToByteAxis(virtualState.RotationY);
        }

        private static int ToByteAxis(int raw)
        {
            return Math.Max(0, Math.Min(255, raw >> 8));
        }

        private static int ToSignedAxis(int raw, bool invert)
        {
            int value = raw - 32768;
            if (invert)
            {
                value = -value;
            }

            return Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
        }
    }
}
