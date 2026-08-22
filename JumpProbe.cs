using System;
using System.Reflection;
using HarmonyLib;
using JumpKing;
using JumpKing.Player;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Records every jump as it is launched, and the height it actually reaches.
    ///
    /// Written for a report that two players jumping at the same moment reach a
    /// different height than the same two jumping one after the other. That
    /// splits cleanly in two, and the launch values say which half to look at:
    /// if the intensity and the velocity leaving the ground match, the jumps were
    /// identical and something downstream - gravity, a collision, a block
    /// behaviour - is treating them differently. If they do not match, the charge
    /// itself is being shared.
    ///
    /// One line per jump and one when it turns over, so this is quiet enough to
    /// leave on while playing normally.
    /// </summary>
    internal static class JumpProbe
    {
        private const int MaximumPlayers = 5;

        private static readonly float[] LaunchY = new float[MaximumPlayers];
        private static readonly float[] PeakY = new float[MaximumPlayers];
        private static readonly bool[] Rising = new bool[MaximumPlayers];
        private static readonly float[] LaunchIntensity = new float[MaximumPlayers];

        private static Func<int> _readJumpFrames;
        private static bool _jumpFramesBound;

        public static void Install(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(JumpState), "DoJump");
            if (target == null)
            {
                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer jump probe: JumpState.DoJump not found."
                );
                return;
            }

            harmony.Patch(
                target,
                null,
                new HarmonyMethod(AccessTools.Method(typeof(JumpProbe), "Postfix"))
            );
        }

        /// <summary>
        /// After the original, so the velocity read here is the one the jump
        /// actually leaves with - including anything another mod's prefix did to
        /// the intensity on the way in.
        /// </summary>
        private static void Postfix(float p_intensity, JumpState __instance)
        {
            if (!ModEntry.Diagnostics.Jump || __instance == null)
            {
                return;
            }

            PlayerEntity player = __instance.player;
            int number = MultiplayerRuntime.GetPlayerNumber(player);
            if (number < 1 || number >= MaximumPlayers || player == null ||
                player.m_body == null)
            {
                return;
            }

            BodyComp body = player.m_body;
            LaunchY[number] = body.Position.Y;
            PeakY[number] = body.Position.Y;
            LaunchIntensity[number] = p_intensity;
            Rising[number] = true;

            Program.crashLog.AddErrorMessage(
                "Local Multiplayer jump: player " + number +
                " intensity=" + p_intensity.ToString("0.####") +
                " velocity=" + Describe(body.Velocity) +
                " multipliers=" + body.GetMultipliers().ToString("0.####") +
                " onGround=" + body.IsOnGround +
                " pos=" + Describe(body.Position) +
                " jumpFrames=" + ReadJumpFrames() +
                " othersCharging=" + CountOtherPlayersCharging(number)
            );
        }

        /// <summary>
        /// Called once per frame from the update patch. Reports the height when
        /// the body stops rising, which is the number the report is actually
        /// about - the launch values alone cannot say whether the arc was cut
        /// short on the way up.
        /// </summary>
        public static void SamplePeak(int number, PlayerContext context)
        {
            if (!ModEntry.Diagnostics.Jump || context == null ||
                context.Body == null || number < 1 || number >= MaximumPlayers ||
                !Rising[number])
            {
                return;
            }

            BodyComp body = context.Body;

            // Y decreases upwards, so the peak is the smallest value seen.
            if (body.Position.Y < PeakY[number])
            {
                PeakY[number] = body.Position.Y;
                return;
            }

            if (body.Velocity.Y < 0f)
            {
                return;
            }

            Rising[number] = false;
            Program.crashLog.AddErrorMessage(
                "Local Multiplayer jump peak: player " + number +
                " intensity=" + LaunchIntensity[number].ToString("0.####") +
                " rise=" + (LaunchY[number] - PeakY[number]).ToString("0.##") +
                " launchY=" + LaunchY[number].ToString("0.##") +
                " peakY=" + PeakY[number].ToString("0.##")
            );
        }

        /// <summary>
        /// How many other players were mid-charge when this one launched. The
        /// report is about simultaneous jumps, so this is the variable being
        /// tested, and reading it at launch avoids having to line up two logs by
        /// eye afterwards.
        /// </summary>
        private static int CountOtherPlayersCharging(int number)
        {
            int charging = 0;

            for (int other = 1; other <= MultiplayerRuntime.PlayerCount; other++)
            {
                if (other == number)
                {
                    continue;
                }

                PlayerContext context = MultiplayerRuntime.GetContext(other);
                if (context != null && context.IsAlive && Rising[other])
                {
                    charging++;
                }
            }

            return charging;
        }

        /// <summary>
        /// JumpKing-Expansion-Blocks counts charge frames into a static that
        /// several of its blocks read to pick a gravity or a slide speed. It is
        /// incremented once per player per frame, so with two players charging it
        /// advances twice as fast - worth seeing next to the launch values even on
        /// a screen where nothing reads it.
        /// </summary>
        private static string ReadJumpFrames()
        {
            if (!_jumpFramesBound)
            {
                _jumpFramesBound = true;
                _readJumpFrames = BindJumpFrames();
            }

            return _readJumpFrames == null ? "n/a" : _readJumpFrames().ToString();
        }

        private static Func<int> BindJumpFrames()
        {
            try
            {
                Type type = AccessTools.TypeByName(
                    "JumpKing_Expansion_Blocks.Patches.PatchedJumpState"
                );
                PropertyInfo property = type == null ? null : type.GetProperty(
                    "JumpFrames",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                );
                MethodInfo getter = property == null ? null :
                    property.GetGetMethod(true);

                return getter == null ? null :
                    (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), getter);
            }
            catch
            {
                return null;
            }
        }

        private static string Describe(Vector2 value)
        {
            return "(" + value.X.ToString("0.###") + "," +
                value.Y.ToString("0.###") + ")";
        }
    }
}
