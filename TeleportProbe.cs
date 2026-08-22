using System;
using HarmonyLib;
using JumpKing;
using JumpKing.BodyCompBehaviours;
using JumpKing.Level;
using JumpKing.Player;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Records what the base game's screen teleport sees, for the player it is
    /// actually running for.
    ///
    /// <c>HandlePlayerTeleportBehaviour</c> resolves two things from two different
    /// baselines: which teleport link to use comes from
    /// <c>LevelManager.CurrentScreen</c>, which is <c>m_screens[Camera.CurrentScreen]</c>,
    /// while how far to move comes from the body's own <c>Position.Y</c>. If those
    /// disagree the teleport either finds no link and silently does nothing, or
    /// moves the player by a wrong multiple of 360.
    ///
    /// This probe does not judge which is happening - it prints both baselines at
    /// the moment of the attempt so the next reproduction settles it. Temporary.
    /// </summary>
    internal static class TeleportProbe
    {
        /// <summary>
        /// The teleport triggers once the hitbox centre leaves the screen. Logging
        /// a little before that catches the approach as well as the crossing,
        /// which matters when the interesting case is the one that never fires.
        /// </summary>
        private const int EdgeMargin = 8;
        private const int ScreenWidth = 480;

        /// <summary>
        /// A failed teleport leaves the player out of bounds, so the condition can
        /// hold for as long as they keep falling. Capped per crossing rather than
        /// deduplicated, because the position changing every frame is exactly what
        /// a stuck player looks like and dedup would print it forever.
        /// </summary>
        private const int MaxLinesPerCrossing = 12;

        private static readonly int[] Remaining = new int[5];
        private static readonly bool[] WasOutside = new bool[5];

        public static void Install(Harmony harmony)
        {
            MethodBaseSafePatch(
                harmony,
                AccessTools.Method(
                    typeof(HandlePlayerTeleportBehaviour),
                    "ExecuteBehaviour"
                )
            );
        }

        private static void MethodBaseSafePatch(
            Harmony harmony,
            System.Reflection.MethodInfo target
        )
        {
            if (target == null)
            {
                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer teleport probe: " +
                    "HandlePlayerTeleportBehaviour.ExecuteBehaviour not found."
                );
                return;
            }

            _target = target;

            // Last, so this observes the state the original is about to see
            // rather than the state before every other mod's prefix. The prefix
            // and the original disagreeing is exactly the open question, and at
            // default priority there is no way to tell which side moved.
            var prefix = new HarmonyMethod(AccessTools.Method(
                typeof(TeleportProbe),
                "Prefix"
            ));
            prefix.priority = Priority.Last;

            harmony.Patch(
                target,
                prefix,
                new HarmonyMethod(AccessTools.Method(
                    typeof(TeleportProbe),
                    "Postfix"
                ))
            );
        }

        private static System.Reflection.MethodInfo _target;
        private static bool _describedPatches;

        /// <summary>
        /// Who else is on this method. Asked once, at the first crossing rather
        /// than at install, because other mods patch during their own level-load
        /// hook and the order between mods is explicitly not a contract.
        /// </summary>
        private static void DescribePatchesOnce()
        {
            if (_describedPatches || _target == null)
            {
                return;
            }

            _describedPatches = true;

            try
            {
                Patches info = Harmony.GetPatchInfo(_target);
                if (info == null)
                {
                    Program.crashLog.AddErrorMessage(
                        "Local Multiplayer teleport patches: none"
                    );
                    return;
                }

                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer teleport patches:" +
                    " prefixes=" + Owners(info.Prefixes) +
                    " postfixes=" + Owners(info.Postfixes) +
                    " transpilers=" + Owners(info.Transpilers) +
                    " finalizers=" + Owners(info.Finalizers)
                );
            }
            catch (Exception ex)
            {
                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer teleport patches unavailable: " + ex.Message
                );
            }
        }

        private static string Owners(
            System.Collections.Generic.IList<Patch> patches
        )
        {
            if (patches == null || patches.Count == 0)
            {
                return "[]";
            }

            string result = "";
            for (int i = 0; i < patches.Count; i++)
            {
                result += (i == 0 ? "" : ",") + patches[i].owner +
                    "/" + patches[i].PatchMethod.DeclaringType.FullName +
                    "." + patches[i].PatchMethod.Name +
                    "@" + patches[i].priority;
            }

            return "[" + result + "]";
        }

        /// <summary>
        /// The attempt is recorded here rather than only on the way out, because
        /// the base game throws when it finds no valid link - and a Harmony
        /// postfix does not run on that path. The case most worth seeing is
        /// exactly the one that would have left no trace.
        /// </summary>
        private static void Prefix(
            BehaviourContext behaviourContext,
            out Vector2 __state
        )
        {
            __state = Vector2.Zero;

            int number;
            BodyComp body;
            if (!TryBegin(behaviourContext, out number, out body))
            {
                return;
            }

            __state = body.Position;
            DescribePatchesOnce();

            Program.crashLog.AddErrorMessage(
                "Local Multiplayer teleport attempt: player " + number +
                " cameraScreen=" + Camera.CurrentScreen +
                " contextScreen=" + PlayerScope.Current.Screen +
                " realScreen=" + RealScreen(body.Position.Y) +
                " " + DescribeLinks() +
                " centreX=" + body.GetHitbox().Center.X +
                " pos=" + Describe(body.Position) +
                " vel=" + Describe(body.Velocity) +
                " " + Predict(body)
            );
        }

        private static void Postfix(
            BehaviourContext behaviourContext,
            Vector2 __state
        )
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || behaviourContext == null ||
                behaviourContext.BodyComp == null)
            {
                return;
            }

            BodyComp body = behaviourContext.BodyComp;
            if (body.Position == __state || __state == Vector2.Zero)
            {
                return;
            }

            Program.crashLog.AddErrorMessage(
                "Local Multiplayer teleport moved: player " + context.Number +
                " from=" + Describe(__state) +
                " to=" + Describe(body.Position) +
                " deltaY=" + (body.Position.Y - __state.Y) +
                " screens=" + ((body.Position.Y - __state.Y) / 360f).ToString("0.##") +
                " cameraScreen=" + Camera.CurrentScreen
            );
        }

        /// <summary>
        /// True when this call is a crossing worth recording, with the budget for
        /// it already taken.
        /// </summary>
        private static bool TryBegin(
            BehaviourContext behaviourContext,
            out int number,
            out BodyComp body
        )
        {
            number = 0;
            body = null;

            PlayerContext context = PlayerScope.Current;
            if (context == null || behaviourContext == null ||
                behaviourContext.BodyComp == null)
            {
                return false;
            }

            number = context.Number;
            if (number < 1 || number >= Remaining.Length)
            {
                return false;
            }

            body = behaviourContext.BodyComp;
            Point centre = body.GetHitbox().Center;
            bool outside = centre.X <= EdgeMargin ||
                centre.X >= ScreenWidth - EdgeMargin;

            if (!outside)
            {
                WasOutside[number] = false;
                return false;
            }

            if (!WasOutside[number])
            {
                WasOutside[number] = true;
                Remaining[number] = MaxLinesPerCrossing;
            }

            if (Remaining[number] <= 0)
            {
                return false;
            }

            Remaining[number]--;
            return true;
        }

        /// <summary>
        /// The same formula the base game uses everywhere else to turn a position
        /// into a screen, so a disagreement with the camera is visible directly
        /// rather than having to be worked out from the raw Y.
        /// </summary>
        private static int RealScreen(float y)
        {
            return -(int)Math.Floor(y / 360f);
        }

        private static string Describe(Vector2 position)
        {
            return "(" + position.X.ToString("0.##") + "," +
                position.Y.ToString("0.##") + ")";
        }

        /// <summary>
        /// Runs the base game's own arithmetic on the values visible here, so the
        /// prediction can be held against what actually happened. They should
        /// agree; where they do not, the behaviour saw different inputs than this
        /// prefix did, which is a different problem from the arithmetic being
        /// wrong about the screen.
        /// </summary>
        private static string Predict(BodyComp body)
        {
            try
            {
                LevelScreen screen = LevelManager.CurrentScreen;
                if (screen == null || !screen.CanTeleport)
                {
                    return "predict=none";
                }

                bool leftSide = body.GetHitbox().Center.X < 0;
                int linkIndex = screen.IsTwoTeleports ? (leftSide ? 0 : 1) : 0;
                TeleportLink[] teleport = screen.teleport;
                if (teleport == null || teleport.Length <= linkIndex ||
                    teleport[linkIndex] == null)
                {
                    return "predict=noLink";
                }

                int target = teleport[linkIndex].GetIndex0();
                int here = (int)((0f - (body.Position.Y - 360f)) / 360f);
                return "predict=[side=" + (leftSide ? "left" : "right") +
                    " link=" + linkIndex + " target=" + target +
                    " here=" + here + " deltaY=" + (360 * (here - target)) + "]";
            }
            catch (Exception ex)
            {
                return "predict unavailable (" + ex.Message + ")";
            }
        }

        /// <summary>
        /// What the teleport lookup itself would find - read through the same
        /// static the base game reads, so it reflects whatever camera is installed
        /// at this instant rather than a recomputation of what it ought to be.
        ///
        /// <c>screenIndex</c> is the screen object's own index, which is the one
        /// value that does not depend on believing the camera: if it disagrees
        /// with <c>cameraScreen</c> then the lookup and the camera are not talking
        /// about the same screen.
        /// </summary>
        private static string DescribeLinks()
        {
            try
            {
                LevelScreen screen = LevelManager.CurrentScreen;
                if (screen == null)
                {
                    return "screen=null";
                }

                string links = "";
                TeleportLink[] teleport = screen.teleport;
                if (teleport != null)
                {
                    for (int i = 0; i < teleport.Length; i++)
                    {
                        links += (i == 0 ? "" : ",") +
                            (teleport[i] == null
                                ? "null"
                                : teleport[i].GetIndex0().ToString());
                    }
                }

                return "screenIndex=" + screen.GetIndex0() +
                    " canTeleport=" + screen.CanTeleport +
                    " twoTeleports=" + screen.IsTwoTeleports +
                    " links=[" + links + "]";
            }
            catch (Exception ex)
            {
                return "links unavailable (" + ex.Message + ")";
            }
        }
    }
}
