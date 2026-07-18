using System;

namespace PlayniteSense
{
    public interface IPhysicalPad : IDisposable
    {
        bool IsConnected { get; }
        string DeviceName { get; }
        string DeviceInstanceId { get; }
        string ConnectionType { get; }
        string InputBackend { get; }
        string LastError { get; }
        RealPadState State { get; }

        bool TryConnect();
        bool Poll();
        void Disconnect();
    }

    public enum PhysicalReportMode
    {
        Unknown,
        Usb,
        BluetoothSimple,
        BluetoothEnhanced
    }
}
