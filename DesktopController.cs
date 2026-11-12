using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PlayniteSense
{
    // Windows input is emitted only in the explicitly activated desktop mode.
    internal sealed class DesktopController
    {
        private readonly Action<string> report;
        private readonly Func<byte, bool> setVibration;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private const byte FeedbackStrength = 100;
        private const int FeedbackPulseMs = 130;
        private const int FeedbackGapMs = 110;
        private int feedbackStage;
        private long feedbackDeadline;
        private bool secondPulse;
        private bool feedbackErrorReported;
        private bool vibrationOn;
        private int stopRetries;
        private long toggleSince = -1;
        private long closeSince = -1;
        private bool toggleFired, closeFired, tabPressed, leftPressed, rightPressed;
        private bool waitingForRelease, inputErrorReported;
        private int touchId = -1;
        private double touchX, touchY, remainderX, remainderY;
        private long lastUpdate;
        private bool active;

        public DesktopController(Action<string> report, Func<byte, bool> setVibration)
        {
            this.report = report;
            this.setVibration = setVibration;
        }

        // Returns true when the virtual controller should receive a neutral report.
        public bool Update(RealPadState state)
        {
            long now = clock.ElapsedMilliseconds;
            bool[] b = state.Buttons;
            bool toggle = b[8] && b[9];
            if (toggle)
            {
                if (toggleSince < 0)
                {
                    toggleSince = now;
                    ResetGesture();
                }
                if (!toggleFired && now - toggleSince >= 600)
                {
                    toggleFired = true;
                    active = !active;
                    waitingForRelease = true;
                    ResetGesture();
                    report("Desktop controls " + (active ? "enabled." : "disabled."));
                    StartFeedback(active);
                }
            }
            else
            {
                toggleSince = -1;
                toggleFired = false;
            }

            if (waitingForRelease)
            {
                if (!b[8] && !b[13] && !b[9] && !b[1] && !b[2] && !b[4] && !b[5])
                    waitingForRelease = false;
                return true;
            }

            if (!active || toggle)
                return toggle;

            double elapsed = Math.Min(30, Math.Max(0, now - lastUpdate));
            lastUpdate = now;
            double dx = 0, dy = 0;
            if (state.TouchId >= 0)
            {
                if (touchId == state.TouchId)
                {
                    dx = (state.TouchX - touchX) * 1800;
                    dy = (state.TouchY - touchY) * 1000;
                }
                touchX = state.TouchX;
                touchY = state.TouchY;
            }
            else
            {
                // Also works with Bluetooth basic reports that omit touch coordinates.
                dx = StickVelocity(state.Z) * elapsed;
                dy = StickVelocity(state.RotationZ) * elapsed;
            }
            touchId = state.TouchId;
            remainderX += dx;
            remainderY += dy;
            int moveX = (int)remainderX;
            int moveY = (int)remainderY;
            remainderX -= moveX;
            remainderY -= moveY;
            if (moveX != 0 || moveY != 0)
                Send(Mouse(0x0001, moveX, moveY));

            bool left = b[13] || b[1];
            bool right = b[2];
            if (left && !leftPressed) Send(Mouse(0x0002), Mouse(0x0004));
            if (right && !rightPressed) Send(Mouse(0x0008), Mouse(0x0010));
            leftPressed = left;
            rightPressed = right;

            bool close = b[5] && b[9] && !b[4];
            bool tab = b[4] && b[9] && !b[5];
            if (tab && !tabPressed) Shortcut(0x09);
            tabPressed = tab;
            if (close)
            {
                if (closeSince < 0) closeSince = now;
                if (!closeFired && now - closeSince >= 1000)
                {
                    closeFired = true;
                    Shortcut(0x73);
                }
            }
            else
            {
                closeSince = -1;
                closeFired = false;
            }
            return true;
        }

        private void StartFeedback(bool enabled)
        {
            StopFeedback();
            secondPulse = !enabled;
            if (TryVibrate(FeedbackStrength))
            {
                feedbackStage = 1;
                feedbackDeadline = clock.ElapsedMilliseconds + FeedbackPulseMs;
            }
        }

        // Advance the pattern on the normal controller loop, without sleeping between pulses.
        public void UpdateFeedback()
        {
            if (feedbackStage == 0 || clock.ElapsedMilliseconds < feedbackDeadline)
                return;

            if (feedbackStage == 2)
            {
                feedbackStage = TryVibrate(FeedbackStrength) ? 3 : 0;
                feedbackDeadline = clock.ElapsedMilliseconds + FeedbackPulseMs;
                return;
            }

            if (feedbackStage == 4)
            {
                if (TryVibrate(0) || --stopRetries == 0)
                    feedbackStage = 0;
                feedbackDeadline = clock.ElapsedMilliseconds + FeedbackGapMs;
                return;
            }

            bool repeat = feedbackStage == 1 && secondPulse;
            bool stopped = TryVibrate(0);
            feedbackStage = repeat && stopped ? 2 : 0;
            if (!stopped && vibrationOn)
            {
                stopRetries = 3;
                feedbackStage = 4;
            }
            feedbackDeadline = clock.ElapsedMilliseconds + FeedbackGapMs;
        }

        public void StopFeedback()
        {
            feedbackStage = 0;
            if (vibrationOn)
                TryVibrate(0);
        }

        private bool TryVibrate(byte strength)
        {
            try
            {
                if (setVibration(strength))
                {
                    vibrationOn = strength != 0;
                    return true;
                }
            }
            catch (Exception)
            {
                // Feedback is optional and must never interrupt controller input.
            }

            if (!feedbackErrorReported)
            {
                feedbackErrorReported = true;
                report("Controller vibration feedback unavailable on the current input backend.");
            }
            return false;
        }

        private void ResetGesture()
        {
            touchId = -1;
            remainderX = remainderY = 0;
            lastUpdate = clock.ElapsedMilliseconds;
            closeSince = -1;
            closeFired = tabPressed = leftPressed = rightPressed = false;
        }

        private static double StickVelocity(int value)
        {
            double axis = (value - 32768) / 32768.0;
            double magnitude = Math.Max(0, Math.Abs(axis) - 0.18) / 0.82;
            return Math.Sign(axis) * magnitude * magnitude * 1.4;
        }

        private void Shortcut(ushort key)
        {
            Send(Key(0x12, false), Key(key, false), Key(key, true), Key(0x12, true));
        }

        private void Send(params Input[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
            if (sent != inputs.Length && !inputErrorReported)
            {
                inputErrorReported = true;
                report("Windows could not deliver desktop input (Win32 " + Marshal.GetLastWin32Error() + "). Check the foreground application's permissions.");
            }
        }

        private static Input Mouse(uint flags, int dx = 0, int dy = 0)
        {
            return new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = dx, Y = dy, Flags = flags } } };
        }

        private static Input Key(ushort key, bool up)
        {
            return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort Key, Scan;
            public uint Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
