using System;
using HarmonyLib;
using JumpKing;
using JumpKing.Level;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Makes both machines read the same wind clock.
    ///
    /// The wind is not random and never was: <c>WindManager.CurrentVelocityRaw</c>
    /// is a function of <c>AchievementManager...timeSpan</c>, and that is a frame
    /// count - <c>AchievementManager.Update</c> increments <c>_ticks</c> once per
    /// unpaused game update. It is why the game can be time-attacked, and why
    /// rollback never disturbed it: resimulation replays through
    /// <c>EntityManager.Update</c>, which never reaches the achievement tick.
    ///
    /// What breaks in a session is only that the two machines count from
    /// different places. Each one's clock started when its own player's attempt
    /// did, so they begin at different points in the same cycle; and the catch-up
    /// path advances the session frame without advancing <c>_ticks</c>, so they
    /// separate further the longer one machine trails.
    ///
    /// So the fix is to count from the origin the two already agreed on. Frames
    /// since the session began is a number both hold exactly, and it is the same
    /// rule single player follows - the attempt clock starts at zero there too,
    /// which is what makes a run reproducible.
    /// </summary>
    internal static class WindSync
    {
        // Straight from WindManager. Copied rather than reached, because they are
        // private consts; if the game ever changes them this is where it shows.
        private const float Speed = 0.48124886f;
        private const float Strength = 0.1f;
        private const float IntensityScale = 0.0125f;

        public static void Install(Harmony harmony)
        {
            try
            {
                var getter = AccessTools.PropertyGetter(
                    typeof(WindManager),
                    "CurrentVelocityRaw"
                );
                if (getter == null)
                {
                    return;
                }

                harmony.Patch(
                    getter,
                    new HarmonyMethod(
                        typeof(WindSync).GetMethod(
                            "Prefix",
                            System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.NonPublic
                        )
                    )
                );
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer could not synchronise the wind: " +
                    ex.Message
                );
            }
        }

        /// <summary>
        /// Answers only when the answer would otherwise differ between the two
        /// machines. Outside a session, and on every screen whose wind does not
        /// consult the clock, the game's own getter runs untouched - so single
        /// player is not merely unaffected in practice, it is unreached.
        /// </summary>
        private static bool Prefix(ref float __result)
        {
            long frames = ModEntry.Netplay.SessionFrames;
            if (frames < 0)
            {
                return true;
            }

            LevelScreen screen = LevelManager.CurrentScreen;
            if (screen == null || !screen.WindEndabled)
            {
                return true;
            }

            // A screen that names its direction returns a fixed value and never
            // reads the clock. Those were always identical on both machines, and
            // taking them over would be copying behaviour for no reason.
            if (screen.WindIntensity != 0f && screen.WindDirection.HasValue)
            {
                return true;
            }

            double seconds =
                frames * Game1.instance.TargetElapsedTime.TotalSeconds;

            float phase = (float)seconds * Speed;
            float value = (float)Math.Sin(phase);
            value = ((float)Math.Cos(phase) > 0f)
                ? value * 2f + 1f
                : value * 2f - 1f;

            if (value < -1f)
            {
                value = -1f;
            }

            if (value > 1f)
            {
                value = 1f;
            }

            __result = screen.WindIntensity != 0f
                ? value * (IntensityScale * screen.WindIntensity)
                : value * Strength;

            return false;
        }
    }
}
