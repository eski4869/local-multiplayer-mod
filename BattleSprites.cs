using System;
using System.Reflection;
using HarmonyLib;
using JumpKing;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Base game artwork borrowed for battle props.
    ///
    /// Loaded through the game's own <c>SmartLoad</c>, which resolves a level's
    /// copy of an asset first and falls back to the game's, so a map that wants
    /// its own version of one of these can simply ship it at the same path.
    /// </summary>
    internal static class BattleSprites
    {
        private const string ArchaeologistAsset =
            "props/textures/old_man/archaeologist";

        private static Texture2D _archaeologist;
        private static bool _archaeologistTried;

        internal static Texture2D Archaeologist
        {
            get
            {
                if (!_archaeologistTried)
                {
                    _archaeologistTried = true;
                    _archaeologist = Load(ArchaeologistAsset);
                }

                return _archaeologist;
            }
        }

        /// <summary>Drops the cache, so a level change reloads against its own content.</summary>
        internal static void Reset()
        {
            _archaeologist = null;
            _archaeologistTried = false;
        }

        private static Texture2D Load(string asset)
        {
            try
            {
                ContentManager content = GetContentManager();
                if (content == null)
                {
                    return null;
                }

                return JKContentManager.SmartLoad<Texture2D>(content, asset);
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer could not load " + asset + ": " +
                    ex.Message
                );
                return null;
            }
        }

        /// <summary>
        /// The XNA content manager inside <c>JKContentManager</c>, which is
        /// internal - hence the reflection rather than a direct reference.
        /// </summary>
        private static ContentManager GetContentManager()
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return null;
            }

            if (ContentManagerField == null)
            {
                return null;
            }

            return ContentManagerField.GetValue(Game1.instance.contentManager)
                as ContentManager;
        }

        private static readonly FieldInfo ContentManagerField =
            AccessTools.Field(typeof(JKContentManager), "contentManager");
    }
}
