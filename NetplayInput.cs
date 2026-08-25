using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The three bits that describe a frame of play, and the conversion to the
    /// game's own input state.
    ///
    /// <c>InputComponent.State</c> is exactly <c>left</c>, <c>right</c> and
    /// <c>jump</c> - <c>dpad</c> is derived from the first two rather than stored -
    /// so a frame really does fit in three bits, with the rest of the byte spare.
    /// That is what makes sending inputs cheaper than sending a position as well as
    /// safer: no float ever enters the sync path.
    /// </summary>
    internal static class NetplayInput
    {
        public const byte Left = 1 << 0;
        public const byte Right = 1 << 1;
        public const byte Jump = 1 << 2;

        public static byte Pack(InputComponent.State state)
        {
            byte packed = 0;
            if (state.left)
            {
                packed |= Left;
            }

            if (state.right)
            {
                packed |= Right;
            }

            if (state.jump)
            {
                packed |= Jump;
            }

            return packed;
        }

        public static InputComponent.State Unpack(byte packed)
        {
            return new InputComponent.State
            {
                left = (packed & Left) != 0,
                right = (packed & Right) != 0,
                jump = (packed & Jump) != 0
            };
        }
    }
}
