using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using JumpKing.API;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Writes out what each player's body actually ended up with, and a hash of it.
    ///
    /// This is a measurement, not a feature. Two questions have been unanswerable
    /// without it. Which mods were treated as block mods, and how many behaviours
    /// each player finished with - the SwitchBlocks report of switch types that do
    /// not respond for additional players has been waiting on exactly that, and was
    /// deferred rather than guessed at. And whether replaying a mod's hook and
    /// copying its behaviours produce the same result, which is the only way to
    /// retire the replay without hoping.
    ///
    /// It reads the bodies rather than the records, so it describes what happened
    /// rather than what was intended. A mechanism that believes its own bookkeeping
    /// cannot detect its own failure.
    ///
    /// The hash is the same string the file shows, so two machines comparing hashes
    /// are comparing something a human can also read and diff. Session agreement in
    /// netplay is meant to use it.
    /// </summary>
    internal static class PlayerSetupManifest
    {
        private const string FileName = "eski4869.LocalMultiplayerMod.SetupManifest.txt";

        private static readonly FieldInfo LookupField = AccessTools.Field(
            typeof(BodyComp),
            "m_blockBehaviourLookup"
        );

        public static string Build()
        {
            var text = new StringBuilder();

            for (int number = 1; number <= ModEntry.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || !context.IsAlive)
                {
                    continue;
                }

                List<string> lines = Describe(context.Body);
                text.Append("player ").Append(number)
                    .Append("  (").Append(lines.Count).Append(")").AppendLine();

                for (int i = 0; i < lines.Count; i++)
                {
                    text.Append("  ").AppendLine(lines[i]);
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Block type and behaviour type per registration, sorted by block type so
        /// that dictionary ordering cannot make two identical setups look different.
        /// </summary>
        private static List<string> Describe(BodyComp body)
        {
            var lines = new List<string>();
            if (body == null || LookupField == null)
            {
                return lines;
            }

            var lookup = LookupField.GetValue(body) as Dictionary<Type, IBlockBehaviour>;
            if (lookup == null)
            {
                return lines;
            }

            foreach (KeyValuePair<Type, IBlockBehaviour> entry in lookup)
            {
                Type behaviourType = entry.Value == null
                    ? null
                    : entry.Value.GetType();

                lines.Add(
                    Describe(entry.Key) + "  ->  " +
                    (behaviourType == null ? "(null)" : Describe(behaviourType))
                );
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        private static string Describe(Type type)
        {
            Assembly assembly = type.Assembly;
            string owner = assembly == null ? "?" : assembly.GetName().Name;
            return owner + "." + type.Name;
        }

        /// <summary>
        /// Stable across runs and machines: a plain FNV-1a over the manifest text,
        /// not <c>string.GetHashCode</c>, which is randomised per process.
        /// </summary>
        public static string Hash(string manifest)
        {
            if (manifest == null)
            {
                return "0";
            }

            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;

                uint hash = offset;
                for (int i = 0; i < manifest.Length; i++)
                {
                    char c = manifest[i];
                    if (c == '\r')
                    {
                        continue;
                    }

                    hash = (hash ^ c) * prime;
                }

                return hash.ToString("x8");
            }
        }

        public static void Write(string reason)
        {
            try
            {
                string manifest = Build();
                string path = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    FileName
                );

                var text = new StringBuilder();
                text.Append("# ").Append(reason)
                    .Append("  hash=").Append(Hash(manifest))
                    .Append("  recorded=").Append(BlockBehaviourRecorder.Count)
                    .AppendLine();
                text.AppendLine();
                text.Append(manifest);

                File.WriteAllText(path, text.ToString());
            }
            catch (Exception ex)
            {
                NetplayLog.Write(
                    "Local Multiplayer setup manifest could not be written: " +
                    ex.Message
                );
            }
        }
    }
}
