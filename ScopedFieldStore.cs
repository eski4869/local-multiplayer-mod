using System.Collections.Generic;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Holds, per player, values that a gimmick mod keeps in a singleton.
    ///
    /// Gimmick mods store their state in one place because there used to be one
    /// player, and most of what they store really does belong to the level -
    /// which switch is solid, how far the animation has run. Some of it does not.
    /// "This contact has already toggled the lever" and "is inside the sand"
    /// describe a player, and with two players sharing one copy they overwrite
    /// each other every frame.
    ///
    /// This is where the per-player copies live. The values sit in
    /// <c>PlayerContext.State</c>, so they belong to the player and die with them,
    /// which is also what makes them reachable for a netplay snapshot. A static
    /// here would recreate the problem being fixed.
    ///
    /// **Single player never reaches this.** <c>PlayerScope.Current</c> is null
    /// unless multiplayer is enabled, so every read and write falls through to the
    /// mod's own field. That is structural, not a check that happens to pass.
    /// </summary>
    internal static class ScopedFieldStore
    {
        /// <summary>
        /// Owners are singletons, so the type name identifies the instance and
        /// makes the stored key readable when it is dumped.
        /// </summary>
        private static string KeyFor(object owner, string name)
        {
            return owner.GetType().FullName + "." + name;
        }

        /// <summary>
        /// False when the caller should let the mod's own accessor run: either no
        /// player is being processed, or this player has not written the value yet.
        ///
        /// Falling through on the first read is deliberate. The mod's field still
        /// holds whatever the level started with, so the first read a player makes
        /// seeds them from the level rather than from <c>default</c>.
        /// </summary>
        public static bool TryRead(object owner, string name, out object value)
        {
            value = null;

            PlayerContext context = PlayerScope.Current;
            if (context == null || owner == null)
            {
                return false;
            }

            return context.State.TryGetValue(KeyFor(owner, name), out value);
        }

        /// <summary>
        /// False when there is no player to write for, in which case the caller
        /// must let the mod's own accessor run.
        /// </summary>
        public static bool TryWrite(object owner, string name, object value)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || owner == null)
            {
                return false;
            }

            context.State[KeyFor(owner, name)] = value;
            return true;
        }
    }
}
