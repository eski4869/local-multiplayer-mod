using System;
using System.IO;
using JumpKing;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// What the two clients have to agree on before a session can mean anything.
    ///
    /// **The level is the check, not the mod list.** A workshop map ships no
    /// assemblies and declares no mods in its own files - it is `level.xnb`,
    /// screens, props, audio and settings. The gimmick mods a map needs come from
    /// Steam's required-item dependencies, so subscribing to the map is what brings
    /// them. Verifying the mod set separately would be checking a consequence
    /// instead of the cause.
    ///
    /// It is the level's **content** rather than its id, because a workshop item
    /// updates in place under the same id. Two players can both be "on" the same map
    /// and hold different block layouts, which is precisely the difference that
    /// makes a shared simulation disagree - and the one that would otherwise present
    /// as a peer mysteriously standing on nothing.
    ///
    /// `visual_level.xnb` is deliberately not included. It is a separate strip for
    /// appearance and carries no collision, so hashing it would refuse sessions over
    /// a difference the simulation cannot observe.
    /// </summary>
    internal static class NetplayLevelIdentity
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static string _cachedRoot;
        private static ulong _cachedHash;

        /// <summary>
        /// A hash of the loaded level's collision data, or zero when there is none
        /// to read.
        /// </summary>
        public static ulong Current
        {
            get
            {
                string root = Game1.instance == null ||
                    Game1.instance.contentManager == null
                        ? null
                        : Game1.instance.contentManager.root;

                if (string.IsNullOrEmpty(root))
                {
                    return 0;
                }

                if (_cachedRoot == root)
                {
                    return _cachedHash;
                }

                _cachedRoot = root;
                _cachedHash = HashLevel(root);
                return _cachedHash;
            }
        }

        /// <summary>
        /// The level's folder name - for a workshop map, its item id.
        ///
        /// Carried alongside the hash because the hash cannot be acted on. A joiner
        /// told "the host is on a different level" can do nothing with that; told
        /// the id, they can find it in their subscriptions. The hash stays the
        /// thing that decides, since an item updates in place and two people can
        /// hold different content under one id.
        /// </summary>
        public static string CurrentId
        {
            get
            {
                string root = Game1.instance == null ||
                    Game1.instance.contentManager == null
                        ? null
                        : Game1.instance.contentManager.root;

                if (string.IsNullOrEmpty(root))
                {
                    return null;
                }

                return Path.GetFileName(root.TrimEnd('\\', '/'));
            }
        }

        /// <summary>
        /// Recomputes on the next read. Called when a level is loaded, because the
        /// content root can be reused for a map that has since been updated.
        /// </summary>
        public static void Invalidate()
        {
            _cachedRoot = null;
            _cachedHash = 0;
        }

        private static ulong HashLevel(string root)
        {
            string path = Path.Combine(root, "level.xnb");
            if (!File.Exists(path))
            {
                return 0;
            }

            try
            {
                ulong hash = FnvOffsetBasis;
                var chunk = new byte[64 * 1024];

                using (FileStream stream = File.OpenRead(path))
                {
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            hash ^= chunk[i];
                            hash *= FnvPrime;
                        }
                    }
                }

                // Zero is the "no level" answer, so a real level must never produce
                // it. One collision in 2^64 is worth removing for free.
                return hash == 0 ? 1UL : hash;
            }
            catch (Exception ex)
            {
                NetplayLog.Write(
                    "Local Multiplayer could not hash the level: " + ex.Message
                );
                return 0;
            }
        }
    }
}
