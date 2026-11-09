using System.Runtime.InteropServices;

namespace AirShift.App;

/// <summary>Reads the first Xbox controller. Hold View and press the D-pad to drive the air.</summary>
internal sealed class Controller
{
    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort Back = 0x0020;

    private ushort _previous;

    public enum Action
    {
        None,
        Up,
        Down,
        Parked,
    }

    public Action Poll()
    {
        ushort buttons = 0;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out var state) == 0)
                {
                    buttons = state.Gamepad.Buttons;
                    break;
                }
            }
        }
        catch (DllNotFoundException)
        {
            return Action.None;
        }

        var pressed = (ushort)(buttons & ~_previous);
        _previous = buttons;
        if ((buttons & Back) == 0) return Action.None;
        if ((pressed & DpadUp) != 0) return Action.Up;
        if ((pressed & DpadDown) != 0) return Action.Down;
        if ((pressed & DpadLeft) != 0) return Action.Parked;
        return Action.None;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint index, out State state);
}
