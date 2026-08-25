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
        /// Keyed on the owner's identity, not on its type name.
        ///
        /// These singletons are replaced rather than cleared - <c>DataSand.Reset</c>
        /// sets the backing instance to null so the next access builds a fresh one,
        /// and the fresh one may also have been loaded from the save file. A key
        /// built from the type name would survive that and hand the new level the
        /// previous run's values: a player who had entered the sand would still
        /// count as inside it, the sand would not be solid for them, and they would
        /// pass straight through.
        ///
        /// Identity keys make a replaced singleton read as unwritten, so the first
        /// read falls through and seeds from whatever the new instance holds.
        /// </summary>
        private sealed class ScopedKey
        {
            private readonly object _owner;
            private readonly string _name;

            public ScopedKey(object owner, string name)
            {
                _owner = owner;
                _name = name;
            }

            public override bool Equals(object obj)
            {
                var other = obj as ScopedKey;
                return other != null &&
                    ReferenceEquals(_owner, other._owner) &&
                    _name == other._name;
            }

            public override int GetHashCode()
            {
                return System.Runtime.CompilerServices.RuntimeHelpers
                    .GetHashCode(_owner) ^ _name.GetHashCode();
            }

            public override string ToString()
            {
                return _owner.GetType().FullName + "." + _name;
            }
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

            return context.State.TryGetValue(new ScopedKey(owner, name), out value);
        }

        /// <summary>
        /// One named player's copy, for reporting. The normal read path goes
        /// through the ambient scope; this exists so a diagnostic can show what
        /// each player is contributing without entering their scope.
        /// </summary>
        public static bool TryReadFor(
            PlayerContext context,
            object owner,
            string name,
            out object value
        )
        {
            value = null;
            if (context == null || owner == null)
            {
                return false;
            }

            return context.State.TryGetValue(new ScopedKey(owner, name), out value);
        }

        /// <summary>
        /// Combines every player's copy with AND, for a value that is read from
        /// outside any player scope.
        ///
        /// Some of this state is written per player but consulted globally.
        /// SwitchBlocks resets <c>CanSwitchSafely</c> at the start of each
        /// player's block pass and clears it if that player is standing where a
        /// switch would trap them, then its logic entity reads it once per frame
        /// to decide whether the switch may flip. With one player those are the
        /// same question. With several, the honest reading of "is it safe to
        /// switch" is **safe for everyone**, so the values combine rather than
        /// one of them winning.
        ///
        /// False when no player has written one yet, in which case the caller
        /// must let the mod's own accessor run and answer from its own field.
        /// </summary>
        public static bool TryReadAll(object owner, string name, out bool combined)
        {
            combined = true;

            if (owner == null)
            {
                return false;
            }

            bool any = false;
            for (int number = 1; number <= MultiplayerRuntime.MaximumPlayers; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || !context.IsAlive)
                {
                    continue;
                }

                object value;
                if (!context.State.TryGetValue(new ScopedKey(owner, name), out value))
                {
                    continue;
                }

                any = true;
                combined = combined && (bool)value;
            }

            return any;
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

            context.State[new ScopedKey(owner, name)] = value;
            return true;
        }

        /// <summary>
        /// Returns a player's private copy of a mutable set. Value properties can
        /// fall through to the source singleton on their first read; a collection
        /// cannot, because mutating that returned reference would mutate every
        /// player at once.
        /// </summary>
        public static bool TryGetOrCreateIntSet(
            object owner,
            string name,
            HashSet<int> source,
            out HashSet<int> value
        )
        {
            value = null;
            PlayerContext context = PlayerScope.Current;
            return context != null && TryGetOrCreateIntSetFor(
                context,
                owner,
                name,
                source,
                out value
            );
        }

        public static bool TryGetOrCreateIntSetFor(
            PlayerContext context,
            object owner,
            string name,
            HashSet<int> source,
            out HashSet<int> value
        )
        {
            value = null;
            if (context == null || owner == null)
            {
                return false;
            }

            var key = new ScopedKey(owner, name);
            object stored;
            if (context.State.TryGetValue(key, out stored))
            {
                value = (HashSet<int>)stored;
                return true;
            }

            value = source == null
                ? new HashSet<int>()
                : new HashSet<int>(source);
            context.State[key] = value;
            return true;
        }

        /// <summary>Seeds a value before the player's first scoped read.</summary>
        public static void SeedFor(
            PlayerContext context,
            object owner,
            string name,
            object value
        )
        {
            if (context == null || owner == null)
            {
                return;
            }

            var key = new ScopedKey(owner, name);
            if (!context.State.ContainsKey(key))
            {
                context.State[key] = value;
            }
        }
    }
}
