using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using JumpKing;
using JumpKing.Workshop;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    // These types are top-level and public because XmlSerializer compiles a
    // reader assembly at runtime that constructs them directly, and it cannot
    // reach a type nested inside an internal one.

    /// <summary>Where each additional player starts, if the map says.</summary>
    public class LocalMultiplayerStartPositions
    {
        public Level.StartPosition? Player2;
        public Level.StartPosition? Player3;
        public Level.StartPosition? Player4;
    }

    /// <summary>A healing pickup. Taken by touching it, comes back later.</summary>
    public class HealPropData
    {
        public Vector2 Position;
        public int Amount;
        public float RespawnSeconds;
    }

    /// <summary>
    /// A slow patrolling hazard. Walks between two points; touching its sides
    /// costs health, landing on its head kills it for a while.
    /// </summary>
    public class WalkerPropData
    {
        public Vector2 From;
        public Vector2 To;
        public float Speed;
        public int Damage;
        public float RespawnSeconds;
    }

    public class BattlePropsData
    {
        [XmlElement("Heal")]
        public List<HealPropData> Heal = new List<HealPropData>();

        [XmlElement("Walker")]
        public List<WalkerPropData> Walker = new List<WalkerPropData>();
    }

    [XmlRoot("LocalMultiplayerLevel")]
    public class LocalMultiplayerLevelDocument
    {
        public LocalMultiplayerStartPositions StartPositions;
        public BattlePropsData BattleProps;
    }

    /// <summary>
    /// Everything a map can tell this mod, in a file of its own next to the
    /// level.
    ///
    /// This deliberately does not live in <c>level_settings.xml</c>. Worldsmith
    /// round-trips that file through its own settings type when it saves, and
    /// anything it does not recognise is dropped - so per-player spawns and
    /// props written there survive right up until the author next opens the
    /// editor, which is the worst possible way for them to disappear. A
    /// separate file is never rewritten, so it survives.
    /// </summary>
    internal static class LocalMultiplayerLevelFile
    {
        internal const string FileName = "local_multiplayer.xml";

        private static LocalMultiplayerLevelDocument _cached;
        private static string _cachedRoot;

        /// <summary>Drops the cached read, so a level change picks up its own file.</summary>
        internal static void Reset()
        {
            _cached = null;
            _cachedRoot = null;
        }

        internal static LocalMultiplayerLevelDocument Load()
        {
            string root = Game1.instance?.contentManager?.root;
            if (string.IsNullOrEmpty(root) || root == "Content")
            {
                // The base game's own bundled level never carries this; only
                // custom levels opt in.
                return null;
            }

            if (_cachedRoot == root)
            {
                return _cached;
            }

            _cachedRoot = root;
            _cached = null;

            string path = Path.Combine(root, FileName);
            if (!File.Exists(path))
            {
                // A map that says nothing is the common case, not an error.
                return null;
            }

            try
            {
                _cached = XmlSerializerHelper
                    .Deserialize<LocalMultiplayerLevelDocument>(path);
            }
            catch (Exception ex)
            {
                // A malformed file is worth reporting: the author wrote it on
                // purpose and would otherwise see it silently do nothing.
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer could not read " + FileName + ": " +
                    ex.Message
                );
            }

            return _cached;
        }
    }
}
