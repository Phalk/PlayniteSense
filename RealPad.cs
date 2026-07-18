// Native Windows HID reader for supported Sony controllers.
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PlayniteSense
{
    public sealed class RealPadState
    {
        public int X { get; internal set; } = 32768;
        public int Y { get; internal set; } = 32768;
        public int Z { get; internal set; } = 32768;
        public int RotationX { get; internal set; }
        public int RotationY { get; internal set; }
        public int RotationZ { get; internal set; } = 32768;
        public bool[] Buttons { get; internal set; } = new bool[20];
        public int[] PointOfViewControllers { get; internal set; } = new[] { -1 };

        internal RealPadState Clone()
        {
            return new RealPadState
            {
                X = X,
                Y = Y,
                Z = Z,
                RotationX = RotationX,
                RotationY = RotationY,
                RotationZ = RotationZ,
                Buttons = (bool[])Buttons.Clone(),
                PointOfViewControllers = (int[])PointOfViewControllers.Clone()
            };
        }
    }

    public sealed class RealPad : IPhysicalPad
    {
        private const ushort SonyVendorId = 0x054C;
        private const ushort DualSenseProductId = 0x0CE6;
        private const ushort DualSenseEdgeProductId = 0x0DF2;
        private const ushort DualShock4V1ProductId = 0x05C4;
        private const ushort DualShock4V2ProductId = 0x09CC;

        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int HidpStatusSuccess = 0x00110000;
        private const int ErrorOperationAborted = 995;
        private const int ErrorDeviceNotConnected = 1167;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private readonly object stateSync = new object();
        private readonly object lifecycleSync = new object();
        private readonly AutoResetEvent firstReportEvent = new AutoResetEvent(false);

        private IntPtr deviceHandle = InvalidHandleValue;
        private Thread readerThread;
        private volatile bool readerRunning;
        private volatile bool connectionLost;
        private volatile bool hasReport;
        private RealPadState state = new RealPadState();
        private int inputReportLength = 78;
        private ushort productId;
        private PhysicalReportMode reportMode;

        public bool IsConnected
        {
            get
            {
                IntPtr handle = deviceHandle;
                return handle != IntPtr.Zero &&
                       handle != InvalidHandleValue &&
                       readerRunning &&
                       !connectionLost;
            }
        }

        public string DeviceName { get; private set; }
        public string DeviceInstanceId { get; private set; }
        public string DevicePath { get; private set; }
        public string ConnectionType { get; private set; }
        public string LastError { get; private set; }
        public string InputBackend => "Native Windows HID";
        public PhysicalReportMode ReportMode => reportMode;
        public ushort ProductId => productId;

        // These values survive a failed read attempt so the SDL fallback can still
        // identify and cloak the same physical device through HidHide.
        public string LastDetectedDeviceName { get; private set; }
        public string LastDetectedDeviceInstanceId { get; private set; }
        public string LastDetectedConnectionType { get; private set; }
        public ushort LastDetectedProductId { get; private set; }

        public RealPadState State
        {
            get
            {
                lock (stateSync)
                {
                    return state.Clone();
                }
            }
        }

        public bool TryConnect()
        {
            lock (lifecycleSync)
            {
                if (IsConnected)
                {
                    return true;
                }

                DisconnectInternal();
                LastError = null;
                connectionLost = false;
                hasReport = false;
                reportMode = PhysicalReportMode.Unknown;
                LastDetectedDeviceName = null;
                LastDetectedDeviceInstanceId = null;
                LastDetectedConnectionType = null;
                LastDetectedProductId = 0;

                Guid hidGuid;
                HidD_GetHidGuid(out hidGuid);

                IntPtr deviceInfoSet = SetupDiGetClassDevs(
                    ref hidGuid,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    DigcfPresent | DigcfDeviceInterface);

                if (deviceInfoSet == InvalidHandleValue)
                {
                    LastError = "SetupDiGetClassDevs failed: Win32 " + Marshal.GetLastWin32Error();
                    return false;
                }

                try
                {
                    uint index = 0;
                    while (true)
                    {
                        var interfaceData = new SpDeviceInterfaceData
                        {
                            Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData))
                        };

                        if (!SetupDiEnumDeviceInterfaces(
                            deviceInfoSet,
                            IntPtr.Zero,
                            ref hidGuid,
                            index,
                            ref interfaceData))
                        {
                            break;
                        }

                        index++;

                        string path;
                        string instanceId;
                        if (!TryGetInterfaceDetails(deviceInfoSet, ref interfaceData, out path, out instanceId))
                        {
                            continue;
                        }

                        // Keep enough identity information for the SDL fallback even when SDL's
                        // HIDAPI handle prevents this reader from opening the interface.
                        ushort identityProductId;
                        if (TryGetSupportedProductFromIdentity(path, instanceId, out identityProductId))
                        {
                            LastDetectedProductId = identityProductId;
                            LastDetectedDeviceName = ProductNameFromId(identityProductId);
                            LastDetectedDeviceInstanceId = instanceId;
                            LastDetectedConnectionType = IsBluetoothIdentity(path, instanceId)
                                ? "Bluetooth"
                                : "USB";
                        }

                        IntPtr handle = OpenSharedHid(path);
                        if (handle == InvalidHandleValue)
                        {
                            continue;
                        }

                        bool keepHandle = false;
                        try
                        {
                            var attributes = new HiddAttributes
                            {
                                Size = Marshal.SizeOf(typeof(HiddAttributes))
                            };

                            if (!HidD_GetAttributes(handle, ref attributes) ||
                                attributes.VendorId != SonyVendorId ||
                                !IsSupportedProduct(attributes.ProductId))
                            {
                                continue;
                            }

                            HidpCaps caps;
                            if (!TryGetCaps(handle, out caps) ||
                                caps.UsagePage != 0x01 ||
                                (caps.Usage != 0x04 && caps.Usage != 0x05) ||
                                caps.InputReportByteLength == 0)
                            {
                                continue;
                            }

                            deviceHandle = handle;
                            keepHandle = true;
                            productId = attributes.ProductId;
                            inputReportLength = Math.Max(10, (int)caps.InputReportByteLength);
                            DevicePath = path;
                            DeviceInstanceId = instanceId;
                            DeviceName = ReadHidString(handle, HidD_GetProductString);
                            if (string.IsNullOrWhiteSpace(DeviceName))
                            {
                                DeviceName = ProductNameFromId(productId);
                            }

                            ConnectionType = inputReportLength >= 78 ||
                                             path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "Bluetooth"
                                : "USB";

                            LastDetectedDeviceName = DeviceName;
                            LastDetectedDeviceInstanceId = DeviceInstanceId;
                            LastDetectedConnectionType = ConnectionType;
                            LastDetectedProductId = productId;

                            while (firstReportEvent.WaitOne(0)) { }
                            readerRunning = true;
                            connectionLost = false;
                            readerThread = new Thread(ReadLoop)
                            {
                                IsBackground = true,
                                Name = "PlayniteSenseNativeHidReader"
                            };
                            readerThread.Start();

                            if (!firstReportEvent.WaitOne(1200))
                            {
                                LastError = "The HID interface opened, but no input report arrived within 1.2 seconds.";
                                DisconnectInternal();
                                continue;
                            }

                            if (!hasReport || connectionLost)
                            {
                                if (string.IsNullOrWhiteSpace(LastError))
                                {
                                    LastError = "The HID interface stopped before the first valid controller report.";
                                }
                                DisconnectInternal();
                                continue;
                            }

                            return true;
                        }
                        finally
                        {
                            if (!keepHandle && handle != IntPtr.Zero && handle != InvalidHandleValue)
                            {
                                CloseHandle(handle);
                            }
                        }
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }

                if (string.IsNullOrWhiteSpace(LastError))
                {
                    LastError = "No readable DualSense/DualSense Edge HID gamepad interface was found.";
                }

                return false;
            }
        }

        public bool Poll()
        {
            return IsConnected && hasReport;
        }

        private void ReadLoop()
        {
            IntPtr handle = deviceHandle;
            byte[] report = new byte[Math.Max(10, inputReportLength)];

            while (readerRunning)
            {
                uint bytesRead;
                bool ok = ReadFile(handle, report, (uint)report.Length, out bytesRead, IntPtr.Zero);
                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (readerRunning && error != ErrorOperationAborted)
                    {
                        LastError = error == ErrorDeviceNotConnected
                            ? "The physical controller disconnected."
                            : "Native HID ReadFile failed: Win32 " + error;
                    }

                    connectionLost = true;
                    readerRunning = false;
                    firstReportEvent.Set();
                    return;
                }

                if (bytesRead == 0)
                {
                    continue;
                }

                if (TryParseReport(report, (int)bytesRead))
                {
                    hasReport = true;
                    firstReportEvent.Set();
                }
            }
        }

        private bool TryParseReport(byte[] report, int count)
        {
            if (count < 10)
            {
                return false;
            }

            if (productId == DualSenseProductId || productId == DualSenseEdgeProductId)
            {
                return TryParseDualSense(report, count);
            }

            return TryParseDualShock4(report, count);
        }

        private bool TryParseDualSense(byte[] report, int count)
        {
            int axesOffset;
            int buttonsOffset;
            int leftTriggerOffset;
            int rightTriggerOffset;

            if (report[0] == 0x31 && count >= 12)
            {
                reportMode = PhysicalReportMode.BluetoothEnhanced;
                // Bluetooth enhanced report: Report ID + one-byte transport header,
                // followed by the same 63-byte common payload used by USB.
                axesOffset = 2;
                leftTriggerOffset = axesOffset + 4;
                rightTriggerOffset = axesOffset + 5;
                buttonsOffset = axesOffset + 7;
            }
            else if (report[0] == 0x01 &&
                     string.Equals(ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                     count >= 10)
            {
                reportMode = PhysicalReportMode.BluetoothSimple;
                // Bluetooth simple/compatibility report. Windows can pad this report to
                // the collection's full input-report length, so connection type matters.
                axesOffset = 1;
                buttonsOffset = 5;
                leftTriggerOffset = 8;
                rightTriggerOffset = 9;
            }
            else if (report[0] == 0x01 && count >= 64)
            {
                reportMode = PhysicalReportMode.Usb;
                // USB full report.
                axesOffset = 1;
                leftTriggerOffset = axesOffset + 4;
                rightTriggerOffset = axesOffset + 5;
                buttonsOffset = axesOffset + 7;
            }
            else if (report[0] == 0x01 && count >= 10)
            {
                reportMode = string.Equals(ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                    ? PhysicalReportMode.BluetoothSimple
                    : PhysicalReportMode.Usb;
                // Fallback for a short compatibility report with an unusual descriptor.
                axesOffset = 1;
                buttonsOffset = 5;
                leftTriggerOffset = 8;
                rightTriggerOffset = 9;
            }
            else
            {
                return false;
            }

            if (rightTriggerOffset >= count || buttonsOffset + 2 >= count)
            {
                return false;
            }

            ApplyCommonState(
                report[axesOffset],
                report[axesOffset + 1],
                report[axesOffset + 2],
                report[axesOffset + 3],
                report[leftTriggerOffset],
                report[rightTriggerOffset],
                report[buttonsOffset],
                report[buttonsOffset + 1],
                report[buttonsOffset + 2]);

            return true;
        }

        private bool TryParseDualShock4(byte[] report, int count)
        {
            int offset;
            if (report[0] == 0x11 && count >= 12)
            {
                reportMode = PhysicalReportMode.BluetoothEnhanced;
                offset = 3;
            }
            else if (report[0] == 0x01 && count >= 10)
            {
                reportMode = string.Equals(ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                    ? PhysicalReportMode.BluetoothSimple
                    : PhysicalReportMode.Usb;
                offset = 1;
            }
            else
            {
                return false;
            }

            int buttonsOffset = offset + 4;
            int leftTriggerOffset = offset + 7;
            int rightTriggerOffset = offset + 8;
            if (rightTriggerOffset >= count || buttonsOffset + 2 >= count)
            {
                return false;
            }

            ApplyCommonState(
                report[offset],
                report[offset + 1],
                report[offset + 2],
                report[offset + 3],
                report[leftTriggerOffset],
                report[rightTriggerOffset],
                report[buttonsOffset],
                report[buttonsOffset + 1],
                report[buttonsOffset + 2]);

            return true;
        }

        private void ApplyCommonState(
            byte lx,
            byte ly,
            byte rx,
            byte ry,
            byte leftTrigger,
            byte rightTrigger,
            byte buttons0,
            byte buttons1,
            byte buttons2)
        {
            var buttons = new bool[20];
            buttons[0] = (buttons0 & 0x10) != 0; // Square
            buttons[1] = (buttons0 & 0x20) != 0; // Cross
            buttons[2] = (buttons0 & 0x40) != 0; // Circle
            buttons[3] = (buttons0 & 0x80) != 0; // Triangle
            buttons[4] = (buttons1 & 0x01) != 0; // L1
            buttons[5] = (buttons1 & 0x02) != 0; // R1
            buttons[6] = (buttons1 & 0x04) != 0; // L2 digital
            buttons[7] = (buttons1 & 0x08) != 0; // R2 digital
            buttons[8] = (buttons1 & 0x10) != 0; // Create/Share
            buttons[9] = (buttons1 & 0x20) != 0; // Options
            buttons[10] = (buttons1 & 0x40) != 0; // L3
            buttons[11] = (buttons1 & 0x80) != 0; // R3
            buttons[12] = (buttons2 & 0x01) != 0; // PS
            buttons[13] = (buttons2 & 0x02) != 0; // Touchpad
            buttons[14] = (buttons2 & 0x04) != 0; // Mute
            buttons[15] = (buttons2 & 0x10) != 0; // Edge Fn1
            buttons[16] = (buttons2 & 0x20) != 0; // Edge Fn2
            buttons[17] = (buttons2 & 0x40) != 0; // Edge left paddle
            buttons[18] = (buttons2 & 0x80) != 0; // Edge right paddle

            int pov = HatToPov(buttons0 & 0x0F);

            lock (stateSync)
            {
                state = new RealPadState
                {
                    X = ExpandByte(lx),
                    Y = ExpandByte(ly),
                    Z = ExpandByte(rx),
                    RotationZ = ExpandByte(ry),
                    RotationX = ExpandByte(leftTrigger),
                    RotationY = ExpandByte(rightTrigger),
                    Buttons = buttons,
                    PointOfViewControllers = new[] { pov }
                };
            }
        }

        private static int ExpandByte(byte value)
        {
            return value * 257;
        }

        private static int HatToPov(int hat)
        {
            switch (hat)
            {
                case 0: return 0;
                case 1: return 4500;
                case 2: return 9000;
                case 3: return 13500;
                case 4: return 18000;
                case 5: return 22500;
                case 6: return 27000;
                case 7: return 31500;
                default: return -1;
            }
        }

        private static bool TryGetSupportedProductFromIdentity(
            string path,
            string instanceId,
            out ushort product)
        {
            product = 0;
            string identity = ((path ?? string.Empty) + "|" + (instanceId ?? string.Empty))
                .ToUpperInvariant();

            if (identity.IndexOf("054C", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            if (identity.IndexOf("0CE6", StringComparison.Ordinal) >= 0)
            {
                product = DualSenseProductId;
            }
            else if (identity.IndexOf("0DF2", StringComparison.Ordinal) >= 0)
            {
                product = DualSenseEdgeProductId;
            }
            else if (identity.IndexOf("05C4", StringComparison.Ordinal) >= 0)
            {
                product = DualShock4V1ProductId;
            }
            else if (identity.IndexOf("09CC", StringComparison.Ordinal) >= 0)
            {
                product = DualShock4V2ProductId;
            }

            return product != 0;
        }

        private static bool IsBluetoothIdentity(string path, string instanceId)
        {
            string identity = ((path ?? string.Empty) + "|" + (instanceId ?? string.Empty))
                .ToUpperInvariant();
            return identity.IndexOf("00001124", StringComparison.Ordinal) >= 0 ||
                   identity.IndexOf("BTHENUM", StringComparison.Ordinal) >= 0 ||
                   identity.IndexOf("BTHLE", StringComparison.Ordinal) >= 0;
        }

        private static bool IsSupportedProduct(ushort pid)
        {
            return pid == DualSenseProductId ||
                   pid == DualSenseEdgeProductId ||
                   pid == DualShock4V1ProductId ||
                   pid == DualShock4V2ProductId;
        }

        private static string ProductNameFromId(ushort pid)
        {
            switch (pid)
            {
                case DualSenseProductId: return "DualSense Wireless Controller";
                case DualSenseEdgeProductId: return "DualSense Edge Wireless Controller";
                case DualShock4V1ProductId:
                case DualShock4V2ProductId:
                    return "DUALSHOCK 4 Wireless Controller";
                default: return "Sony HID Gamepad";
            }
        }

        private static IntPtr OpenSharedHid(string path)
        {
            IntPtr handle = CreateFile(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle != InvalidHandleValue)
            {
                return handle;
            }

            return CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
        }

        private static bool TryGetCaps(IntPtr handle, out HidpCaps caps)
        {
            caps = default(HidpCaps);
            IntPtr preparsedData;
            if (!HidD_GetPreparsedData(handle, out preparsedData) || preparsedData == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return HidP_GetCaps(preparsedData, out caps) == HidpStatusSuccess;
            }
            finally
            {
                HidD_FreePreparsedData(preparsedData);
            }
        }

        private static bool TryGetInterfaceDetails(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData interfaceData,
            out string path,
            out string instanceId)
        {
            path = null;
            instanceId = null;

            uint requiredSize;
            var deviceInfoData = new SpDevinfoData
            {
                Size = Marshal.SizeOf(typeof(SpDevinfoData))
            };

            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                IntPtr.Zero,
                0,
                out requiredSize,
                ref deviceInfoData);

            if (requiredSize == 0)
            {
                return false;
            }

            IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                deviceInfoData.Size = Marshal.SizeOf(typeof(SpDevinfoData));

                if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    detailBuffer,
                    requiredSize,
                    out requiredSize,
                    ref deviceInfoData))
                {
                    return false;
                }

                path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                instanceId = GetDeviceInstanceId(deviceInfoSet, ref deviceInfoData);
                return !string.IsNullOrWhiteSpace(path);
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        private static string GetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData)
        {
            uint requiredSize;
            var buffer = new StringBuilder(512);
            if (SetupDiGetDeviceInstanceId(
                deviceInfoSet,
                ref deviceInfoData,
                buffer,
                buffer.Capacity,
                out requiredSize))
            {
                return buffer.ToString().ToUpperInvariant();
            }

            if (requiredSize > buffer.Capacity)
            {
                buffer = new StringBuilder((int)requiredSize);
                if (SetupDiGetDeviceInstanceId(
                    deviceInfoSet,
                    ref deviceInfoData,
                    buffer,
                    buffer.Capacity,
                    out requiredSize))
                {
                    return buffer.ToString().ToUpperInvariant();
                }
            }

            return null;
        }

        private delegate bool HidStringReader(IntPtr deviceObject, IntPtr buffer, uint bufferLength);

        private static string ReadHidString(IntPtr handle, HidStringReader reader)
        {
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                for (int i = 0; i < 512; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                if (!reader(handle, buffer, 512))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(buffer)?.Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Disconnect()
        {
            lock (lifecycleSync)
            {
                DisconnectInternal();
            }
        }

        private void DisconnectInternal()
        {
            readerRunning = false;
            hasReport = false;

            IntPtr handle = deviceHandle;
            deviceHandle = InvalidHandleValue;

            Thread thread = readerThread;
            readerThread = null;

            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                // Cancel the synchronous ReadFile first and let the reader exit before closing the
                // HID handle. Closing it immediately could leave a short-lived stale read while
                // HidHide is exposing the controller to another Playnite mode.
                try { CancelIoEx(handle, IntPtr.Zero); } catch { }
            }

            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(1000); } catch { }
            }

            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                try { CloseHandle(handle); } catch { }
            }

            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
            {
                try { thread.Join(500); } catch { }
            }

            connectionLost = false;
            DeviceName = null;
            DeviceInstanceId = null;
            DevicePath = null;
            ConnectionType = null;
            productId = 0;
            inputReportLength = 78;
            reportMode = PhysicalReportMode.Unknown;

            lock (stateSync)
            {
                state = new RealPadState();
            }
        }

        public void Dispose()
        {
            Disconnect();
            firstReportEvent.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public int Size;
            public Guid ClassGuid;
            public int DevInst;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            public ushort Reserved01;
            public ushort Reserved02;
            public ushort Reserved03;
            public ushort Reserved04;
            public ushort Reserved05;
            public ushort Reserved06;
            public ushort Reserved07;
            public ushort Reserved08;
            public ushort Reserved09;
            public ushort Reserved10;
            public ushort Reserved11;
            public ushort Reserved12;
            public ushort Reserved13;
            public ushort Reserved14;
            public ushort Reserved15;
            public ushort Reserved16;
            public ushort Reserved17;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, IntPtr buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            ref SpDevinfoData deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            StringBuilder deviceInstanceId,
            int deviceInstanceIdSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            IntPtr file,
            [Out] byte[] buffer,
            uint numberOfBytesToRead,
            out uint numberOfBytesRead,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
