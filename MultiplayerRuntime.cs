using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EntityComponent;
using HarmonyLib;
using JumpKing;
using JumpKing.API;
using JumpKing.GameManager.MultiEnding;
using JumpKing.Level;
using JumpKing.Player;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    internal static class MultiplayerRuntime
    {
        private const int MaximumPlayers = 4;
        private static PlayerEntity _player1;
        private static readonly PlayerEntity[] AdditionalPlayers =
            new PlayerEntity[MaximumPlayers - 1];
        private static bool _levelStarted;
        private static bool _raceComplete;
        private static bool _blockBehavioursSynchronized;
        private static readonly FieldInfo BlockBehaviourLookupField = AccessTools.Field(
            typeof(BodyComp),
            "m_blockBehaviourLookup"
        );

        public static bool IsActive
        {
            get
            {
                if (ModEntry.PlayerCount <= 1 || !_levelStarted || _raceComplete)
                {
                    return false;
                }

                for (int playerNumber = 2; playerNumber <= ModEntry.PlayerCount; playerNumber++)
                {
                    PlayerEntity player = GetPlayer(playerNumber);
                    if (player == null || !player.IsAlive)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public static int PlayerCount
        {
            get { return IsActive ? ModEntry.PlayerCount : 1; }
        }

        public static PlayerEntity GetPlayer(int playerNumber)
        {
            if (playerNumber == 1)
            {
                if (_player1 != null && _player1.IsAlive)
                {
                    return _player1;
                }

                _player1 = EntityManager.instance == null ? null :
                    EntityManager.instance.Find<PlayerEntity>();
                return _player1;
            }

            int index = playerNumber - 2;
            return index >= 0 && index < AdditionalPlayers.Length ?
                AdditionalPlayers[index] : null;
        }

        public static void OnLevelStart()
        {
            _player1 = EntityManager.instance == null ? null :
                EntityManager.instance.Find<PlayerEntity>();
            _levelStarted = true;
            _raceComplete = false;

            if (ModEntry.IsMultiplayerEnabled)
            {
                StartAdditionalPlayers(ModEntry.PlayerCount);
            }
        }

        public static void OnLevelEnd()
        {
            _levelStarted = false;
            StopAdditionalPlayers();
            _player1 = null;
        }

        public static void SetPlayerCount(int playerCount)
        {
            if (playerCount <= 1)
            {
                StopAdditionalPlayers();
                return;
            }

            _raceComplete = false;
            if (_levelStarted)
            {
                StartAdditionalPlayers(playerCount);
            }
        }

        public static int GetPlayerNumber(PlayerEntity player)
        {
            if (player == null)
            {
                return 0;
            }

            for (int i = 0; i < AdditionalPlayers.Length; i++)
            {
                if (ReferenceEquals(player, AdditionalPlayers[i]))
                {
                    return i + 2;
                }
            }

            return ReferenceEquals(player, GetPlayer(1)) ? 1 : 0;
        }

        public static int GetPlayerNumber(InputComponent input)
        {
            return input == null || input.gameObject == null ? 0 :
                GetPlayerNumber(input.gameObject as PlayerEntity);
        }

        public static void FinishRace()
        {
            _raceComplete = true;
            StopAdditionalPlayers();
        }

        public static void SynchronizeBlockBehaviours()
        {
            if (_blockBehavioursSynchronized || !IsActive ||
                BlockBehaviourLookupField == null || EntityManager.instance == null)
            {
                return;
            }

            PlayerEntity player1 = GetPlayer(1);
            BodyComp player1Body = player1 == null ? null : player1.GetComponent<BodyComp>();
            if (player1Body == null)
            {
                return;
            }

            IDictionary sourceLookup = BlockBehaviourLookupField.GetValue(player1Body) as IDictionary;
            if (sourceLookup == null)
            {
                return;
            }

            for (int playerNumber = 2; playerNumber <= ModEntry.PlayerCount; playerNumber++)
            {
                PlayerEntity player = GetPlayer(playerNumber);
                BodyComp body = player == null ? null : player.GetComponent<BodyComp>();
                IDictionary targetLookup = body == null ? null :
                    BlockBehaviourLookupField.GetValue(body) as IDictionary;
                if (targetLookup == null)
                {
                    return;
                }

                foreach (DictionaryEntry entry in sourceLookup)
                {
                    Type blockType = entry.Key as Type;
                    IBlockBehaviour sourceBehaviour = entry.Value as IBlockBehaviour;
                    if (blockType == null || sourceBehaviour == null ||
                        targetLookup.Contains(blockType))
                    {
                        continue;
                    }

                    IBlockBehaviour behaviour = CreateBlockBehaviour(
                        sourceBehaviour,
                        player,
                        body
                    );
                    if (behaviour == null)
                    {
                        JumpKing.Program.crashLog.AddErrorMessage(
                            "Local Multiplayer cannot construct block behaviour: " +
                            sourceBehaviour.GetType().FullName
                        );
                        continue;
                    }

                    body.RegisterBlockBehaviour(blockType, behaviour);
                }
            }

            _blockBehavioursSynchronized = true;
        }

        private static void StartAdditionalPlayers(int playerCount)
        {
            if (EntityManager.instance == null)
            {
                return;
            }

            PlayerEntity player1 = GetPlayer(1);
            if (player1 == null)
            {
                return;
            }

            BodyComp player1Body = player1.GetComponent<BodyComp>();
            if (player1Body == null)
            {
                return;
            }

            TrimAdditionalPlayers(playerCount);

            for (int playerNumber = 2; playerNumber <= playerCount; playerNumber++)
            {
                int index = playerNumber - 2;
                if (AdditionalPlayers[index] != null && AdditionalPlayers[index].IsAlive)
                {
                    continue;
                }

                try
                {
                    PlayerEntity player = new PlayerEntity();
                    AdditionalPlayers[index] = player;
                    PlayerSpriteFactory.Prepare(playerNumber);
                    _blockBehavioursSynchronized = false;

                    BodyComp body = player.GetComponent<BodyComp>();
                    if (body != null)
                    {
                        body.Position = player1Body.Position;
                        body.Velocity = Vector2.Zero;
                    }

                    Component[] components = player.GetComponents();
                    for (int i = 0; i < components.Length; i++)
                    {
                        if (components[i].GetType().FullName == "JumpKing.Player.CameraFollowComp")
                        {
                            components[i].Enabled = false;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AdditionalPlayers[index] = null;
                    JumpKing.Program.crashLog.AddErrorMessage(
                        "Local Multiplayer player " + playerNumber + " start failed: " + ex.Message
                    );
                }
            }
        }

        private static void StopAdditionalPlayers()
        {
            MultiplayerSplitRenderer.Release();

            for (int i = 0; i < AdditionalPlayers.Length; i++)
            {
                if (AdditionalPlayers[i] != null && AdditionalPlayers[i].IsAlive)
                {
                    AdditionalPlayers[i].Destroy();
                }

                AdditionalPlayers[i] = null;
            }

            _blockBehavioursSynchronized = false;
            PlayerSpriteFactory.Release();
        }

        private static void TrimAdditionalPlayers(int playerCount)
        {
            for (int playerNumber = Math.Max(2, playerCount + 1);
                playerNumber <= MaximumPlayers;
                playerNumber++)
            {
                int index = playerNumber - 2;
                if (AdditionalPlayers[index] != null && AdditionalPlayers[index].IsAlive)
                {
                    AdditionalPlayers[index].Destroy();
                }

                AdditionalPlayers[index] = null;
            }

            _blockBehavioursSynchronized = false;
            MultiplayerSplitRenderer.Release();
        }

        private static IBlockBehaviour CreateBlockBehaviour(
            IBlockBehaviour sourceBehaviour,
            PlayerEntity player,
            BodyComp body
        )
        {
            Type behaviourType = sourceBehaviour.GetType();
            ConstructorInfo[] constructors = behaviourType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] parameters = constructors[i].GetParameters();
                object[] arguments = new object[parameters.Length];
                bool supported = true;

                for (int j = 0; j < parameters.Length; j++)
                {
                    object argument = ResolveBehaviourArgument(
                        parameters[j],
                        sourceBehaviour,
                        player,
                        body
                    );
                    if (argument == UnsupportedArgument.Value)
                    {
                        supported = false;
                        break;
                    }

                    arguments[j] = argument;
                }

                if (!supported)
                {
                    continue;
                }

                try
                {
                    return constructors[i].Invoke(arguments) as IBlockBehaviour;
                }
                catch
                {
                }
            }

            return null;
        }

        private static object ResolveBehaviourArgument(
            ParameterInfo parameter,
            IBlockBehaviour sourceBehaviour,
            PlayerEntity player,
            BodyComp body
        )
        {
            Type parameterType = parameter.ParameterType;

            if (parameterType.IsInstanceOfType(player))
            {
                return player;
            }

            if (parameterType.IsInstanceOfType(body))
            {
                return body;
            }

            InputComponent input = player.GetComponent<InputComponent>();
            if (input != null && parameterType.IsInstanceOfType(input))
            {
                return input;
            }

            if (LevelManager.Instance != null &&
                parameterType.IsInstanceOfType(LevelManager.Instance))
            {
                return LevelManager.Instance;
            }

            object configuredValue;
            if (TryReadConfiguredValue(sourceBehaviour, parameter, out configuredValue))
            {
                return configuredValue;
            }

            return UnsupportedArgument.Value;
        }

        private static bool TryReadConfiguredValue(
            object source,
            ParameterInfo parameter,
            out object value
        )
        {
            value = null;
            Type sourceType = source.GetType();
            Type parameterType = parameter.ParameterType;
            string parameterName = NormalizeMemberName(parameter.Name);
            PropertyInfo[] properties = sourceType.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !parameterType.IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                if (NormalizeMemberName(property.Name) == parameterName)
                {
                    value = property.GetValue(source, null);
                    return true;
                }
            }

            PropertyInfo singleProperty = null;
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (property.CanRead && property.GetIndexParameters().Length == 0 &&
                    parameterType.IsAssignableFrom(property.PropertyType))
                {
                    if (singleProperty != null)
                    {
                        singleProperty = null;
                        break;
                    }

                    singleProperty = property;
                }
            }

            if (singleProperty != null)
            {
                value = singleProperty.GetValue(source, null);
                return true;
            }

            FieldInfo[] fields = sourceType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (parameterType.IsAssignableFrom(field.FieldType) &&
                    NormalizeMemberName(field.Name) == parameterName)
                {
                    value = field.GetValue(source);
                    return true;
                }
            }

            FieldInfo singleField = null;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!parameterType.IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                if (singleField != null)
                {
                    return false;
                }

                singleField = field;
            }

            if (singleField == null)
            {
                return false;
            }

            value = singleField.GetValue(source);
            return true;
        }

        private static string NormalizeMemberName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var characters = new List<char>(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsLetterOrDigit(name[i]))
                {
                    characters.Add(char.ToLowerInvariant(name[i]));
                }
            }

            const string backingFieldSuffix = "kbackingfield";
            string normalized = new string(characters.ToArray());
            if (normalized.EndsWith(backingFieldSuffix, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(
                    0,
                    normalized.Length - backingFieldSuffix.Length
                );
            }

            return normalized;
        }

        private sealed class UnsupportedArgument
        {
            public static readonly UnsupportedArgument Value = new UnsupportedArgument();

            private UnsupportedArgument()
            {
            }
        }
    }

    internal static class AdditionalPlayerInputStatePatch
    {
        public static bool Prefix(
            InputComponent __instance,
            ref InputComponent.State __result
        )
        {
            int playerNumber = MultiplayerRuntime.GetPlayerNumber(__instance);
            if (playerNumber <= 1)
            {
                return true;
            }

            __result = new InputComponent.State();
            return false;
        }
    }

    internal static class AdditionalPlayerSaveUpdatePatch
    {
        public static bool Prefix(PlayerEntity __instance)
        {
            return MultiplayerRuntime.GetPlayerNumber(__instance) <= 1;
        }
    }

    internal static class AdditionalPlayerScreenUpdatePatch
    {
        private const int NotAdditionalPlayer = -1;
        private static readonly FieldInfo CameraScreenField = AccessTools.Field(
            typeof(Camera),
            "_current_screen"
        );

        public static void Prefix(Entity __instance, out int __state)
        {
            PlayerEntity player = __instance as PlayerEntity;
            if (MultiplayerRuntime.GetPlayerNumber(player) <= 1 || CameraScreenField == null)
            {
                __state = NotAdditionalPlayer;
                return;
            }

            BodyComp body = player.GetComponent<BodyComp>();
            if (body == null)
            {
                __state = NotAdditionalPlayer;
                return;
            }

            __state = Camera.CurrentScreen;
            int screen = -(int)Math.Floor(body.GetHitbox().Center.Y / 360f);
            screen = Math.Max(0, Math.Min(LevelManager.TotalScreens - 1, screen));
            CameraScreenField.SetValue(null, screen);
        }

        public static void Postfix(int __state)
        {
            if (__state != NotAdditionalPlayer)
            {
                CameraScreenField.SetValue(null, __state);
            }
        }
    }

    internal static class AdditionalPlayerDrawPatch
    {
        public static void Prefix(PlayerEntity __instance, ref Sprite ___m_sprite)
        {
            int playerNumber = MultiplayerRuntime.GetPlayerNumber(__instance);
            if (playerNumber > 1)
            {
                PlayerSpriteFactory.ApplyForDraw(
                    __instance,
                    ref ___m_sprite,
                    playerNumber
                );
            }
        }
    }

    internal static class PlayerSpriteFactory
    {
        private const int FirstColoredPlayer = 2;
        private const int ColoredPlayerCount = 3;
        private static readonly Type LayeredSpriteType = AccessTools.TypeByName(
            "JumpKing.XnaWrappers.LayeredSprite"
        );
        private static readonly PropertyInfo LayeredSpritesProperty =
            LayeredSpriteType == null ? null : AccessTools.Property(
                LayeredSpriteType,
                "Sprites"
            );
        private static readonly ConstructorInfo LayeredSpriteConstructor =
            LayeredSpriteType == null ? null : AccessTools.Constructor(
                LayeredSpriteType,
                new Type[] { typeof(Sprite), typeof(Sprite[]) }
            );
        private static readonly Dictionary<Sprite, Sprite>[] Sprites =
            CreateSpriteCaches();
        private static readonly Dictionary<Sprite, LayeredSpriteCopy>[] LayeredSprites =
            CreateLayeredSpriteCaches();
        private static readonly Dictionary<Texture2D, Texture2D>[] Textures =
            CreateTextureCaches();
        private static readonly Dictionary<PlayerEntity, Sprite> AppliedDrawSprites =
            new Dictionary<PlayerEntity, Sprite>();

        public static void ApplyForDraw(
            PlayerEntity player,
            ref Sprite sprite,
            int playerNumber
        )
        {
            Sprite applied;
            if (AppliedDrawSprites.TryGetValue(player, out applied) &&
                ReferenceEquals(applied, sprite))
            {
                return;
            }

            sprite = Get(sprite, playerNumber);
            AppliedDrawSprites[player] = sprite;
        }

        public static Sprite Get(Sprite source, int playerNumber)
        {
            if (source == null)
            {
                return null;
            }

            int paletteIndex = playerNumber - FirstColoredPlayer;
            if (paletteIndex < 0 || paletteIndex >= ColoredPlayerCount)
            {
                return source;
            }

            if (LayeredSpriteType != null && LayeredSpriteType.IsInstanceOfType(source))
            {
                return GetLayeredSprite(source, playerNumber, paletteIndex);
            }

            Sprite sprite;
            if (Sprites[paletteIndex].TryGetValue(source, out sprite))
            {
                return sprite;
            }

            sprite = Sprite.CreateSpriteWithCenter(
                source.texture == null ? null :
                    GetTexture(source.texture, playerNumber, paletteIndex),
                source.source,
                source.center
            );
            sprite.SetColor(source.GetColor());
            Sprites[paletteIndex].Add(source, sprite);
            return sprite;
        }

        public static void Prepare(int playerNumber)
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            JKContentManager.PlayerSprites sprites =
                Game1.instance.contentManager.playerSprites;
            Get(sprites.idle, playerNumber);
            Get(sprites.walk_one, playerNumber);
            Get(sprites.walk_smear, playerNumber);
            Get(sprites.walk_two, playerNumber);
            Get(sprites.jump_charge, playerNumber);
            Get(sprites.jump_up, playerNumber);
            Get(sprites.jump_fall, playerNumber);
            Get(sprites.jump_bounce, playerNumber);
            Get(sprites.splat, playerNumber);
            Get(sprites.look_up, playerNumber);
            Get(sprites.stretch_one, playerNumber);
            Get(sprites.stretch_smear, playerNumber);
            Get(sprites.stretch_two, playerNumber);
        }

        public static void Release()
        {
            for (int i = 0; i < ColoredPlayerCount; i++)
            {
                foreach (Texture2D texture in Textures[i].Values)
                {
                    texture.Dispose();
                }

                Sprites[i].Clear();
                LayeredSprites[i].Clear();
                Textures[i].Clear();
            }

            AppliedDrawSprites.Clear();
        }

        private static Sprite GetLayeredSprite(
            Sprite source,
            int playerNumber,
            int paletteIndex
        )
        {
            if (LayeredSpritesProperty == null || LayeredSpriteConstructor == null)
            {
                throw new InvalidOperationException("LayeredSprite metadata is unavailable.");
            }

            IList sourceLayers = LayeredSpritesProperty.GetValue(source, null) as IList;
            if (sourceLayers == null || sourceLayers.Count == 0)
            {
                throw new InvalidOperationException("LayeredSprite has no layers.");
            }

            LayeredSpriteCopy cached;
            if (LayeredSprites[paletteIndex].TryGetValue(source, out cached) &&
                cached.Matches(sourceLayers))
            {
                return cached.Sprite;
            }

            Sprite[] sourceSprites = new Sprite[sourceLayers.Count];
            Sprite[] extraLayers = new Sprite[sourceLayers.Count - 1];
            for (int i = 0; i < sourceLayers.Count; i++)
            {
                Sprite sourceSprite = (Sprite)sourceLayers[i];
                sourceSprites[i] = sourceSprite;
                if (i > 0)
                {
                    extraLayers[i - 1] = Get(sourceSprite, playerNumber);
                }
            }

            Sprite sprite = (Sprite)LayeredSpriteConstructor.Invoke(
                new object[] { Get(sourceSprites[0], playerNumber), extraLayers }
            );
            LayeredSprites[paletteIndex][source] = new LayeredSpriteCopy(sprite, sourceSprites);
            return sprite;
        }

        private static Texture2D GetTexture(
            Texture2D source,
            int playerNumber,
            int paletteIndex
        )
        {
            Texture2D texture;
            if (Textures[paletteIndex].TryGetValue(source, out texture))
            {
                return texture;
            }

            Color[] pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);

            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                if (!IsBodyBlue(pixel))
                {
                    continue;
                }

                int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                int chroma = maximum - minimum;
                pixels[i] = RecolorBody(pixel, playerNumber, minimum, maximum, chroma);
            }

            texture = new Texture2D(
                Game1.instance.GraphicsDevice,
                source.Width,
                source.Height
            );
            texture.SetData(pixels);
            Textures[paletteIndex].Add(source, texture);
            return texture;
        }

        private static Color RecolorBody(
            Color source,
            int playerNumber,
            int minimum,
            int maximum,
            int chroma
        )
        {
            switch (playerNumber)
            {
                case 2:
                    return new Color(
                        minimum + chroma * 3 / 4,
                        minimum,
                        maximum,
                        source.A
                    );
                case 3:
                    return new Color(maximum, maximum, minimum, source.A);
                case 4:
                    return new Color(
                        minimum,
                        maximum,
                        minimum + chroma / 4,
                        source.A
                    );
                default:
                    return source;
            }
        }

        private static Dictionary<Sprite, Sprite>[] CreateSpriteCaches()
        {
            var caches = new Dictionary<Sprite, Sprite>[ColoredPlayerCount];
            for (int i = 0; i < caches.Length; i++)
            {
                caches[i] = new Dictionary<Sprite, Sprite>();
            }

            return caches;
        }

        private static Dictionary<Sprite, LayeredSpriteCopy>[] CreateLayeredSpriteCaches()
        {
            var caches = new Dictionary<Sprite, LayeredSpriteCopy>[ColoredPlayerCount];
            for (int i = 0; i < caches.Length; i++)
            {
                caches[i] = new Dictionary<Sprite, LayeredSpriteCopy>();
            }

            return caches;
        }

        private static Dictionary<Texture2D, Texture2D>[] CreateTextureCaches()
        {
            var caches = new Dictionary<Texture2D, Texture2D>[ColoredPlayerCount];
            for (int i = 0; i < caches.Length; i++)
            {
                caches[i] = new Dictionary<Texture2D, Texture2D>();
            }

            return caches;
        }

        private static bool IsBodyBlue(Color pixel)
        {
            if (pixel.A == 0)
            {
                return false;
            }

            int threshold = Math.Max(4, pixel.A / 16);
            int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            return maximum - minimum >= threshold &&
                pixel.B >= pixel.G - threshold &&
                pixel.G >= pixel.R + threshold / 2 &&
                pixel.B >= pixel.R + threshold;
        }

        private sealed class LayeredSpriteCopy
        {
            public readonly Sprite Sprite;
            private readonly Sprite[] _sourceLayers;

            public LayeredSpriteCopy(Sprite sprite, Sprite[] sourceLayers)
            {
                Sprite = sprite;
                _sourceLayers = sourceLayers;
            }

            public bool Matches(IList sourceLayers)
            {
                if (sourceLayers.Count != _sourceLayers.Length)
                {
                    return false;
                }

                for (int i = 0; i < _sourceLayers.Length; i++)
                {
                    if (!ReferenceEquals(sourceLayers[i], _sourceLayers[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    internal static class MultiplayerEndingPatch
    {
        public static void Postfix(
            ref bool __result,
            ref IEnding __0,
            List<IEnding> ___m_endings
        )
        {
            if (!MultiplayerRuntime.IsActive)
            {
                return;
            }

            if (__result)
            {
                MultiplayerRuntime.FinishRace();
                return;
            }

            if (___m_endings == null)
            {
                return;
            }

            for (int playerNumber = 2;
                playerNumber <= MultiplayerRuntime.PlayerCount;
                playerNumber++)
            {
                PlayerEntity player = MultiplayerRuntime.GetPlayer(playerNumber);
                if (player == null)
                {
                    continue;
                }

                for (int i = 0; i < ___m_endings.Count; i++)
                {
                    IEnding ending = ___m_endings[i];
                    if (!ending.CheckWin(player))
                    {
                        continue;
                    }

                    PlayerEntity player1 = MultiplayerRuntime.GetPlayer(1);
                    BodyComp player1Body = player1 == null ? null :
                        player1.GetComponent<BodyComp>();
                    BodyComp winnerBody = player.GetComponent<BodyComp>();

                    if (player1Body == null || winnerBody == null)
                    {
                        return;
                    }

                    player1Body.Position = winnerBody.Position;
                    player1Body.Velocity = winnerBody.Velocity;

                    if (ending.CheckWin(player1))
                    {
                        __0 = ending;
                        __result = true;
                        MultiplayerRuntime.FinishRace();
                    }

                    return;
                }
            }
        }
    }

    internal static class MultiplayerDrawPatch
    {
        public static bool Prefix(JumpGame __instance)
        {
            return MultiplayerSplitRenderer.PrefixDraw(__instance);
        }
    }

    internal static class MultiplayerSplitRenderer
    {
        private const int Width = 480;
        private const int Height = 360;
        private const int HalfWidth = Width / 2;
        private const int HalfHeight = Height / 2;
        private static readonly FieldInfo CameraScreenField = AccessTools.Field(
            typeof(Camera),
            "_current_screen"
        );
        private static readonly MethodInfo BodyIsOnBlockMethod = AccessTools.Method(
            typeof(BodyComp),
            "IsOnBlock",
            new Type[] { typeof(Type) }
        );

        private static readonly RenderTarget2D[] PlayerTargets = new RenderTarget2D[4];
        private static readonly int[] ViewTargetIndexes = new int[4];
        private static readonly int[] ViewScreens = new int[4];
        private static readonly Vector2[] ViewOffsets = new Vector2[4];
        private static bool _drawingPass;
        private static readonly bool[] CameraInitialized = new bool[4];
        private static readonly int[] CameraScreens = new int[4];
        private static readonly Vector2[] CameraOffsets = new Vector2[4];

        public static bool PrefixDraw(JumpGame game)
        {
            if (_drawingPass || !MultiplayerRuntime.IsActive)
            {
                return true;
            }

            Game1 host = Game1.instance;
            int playerCount = MultiplayerRuntime.PlayerCount;
            if (host == null || CameraScreenField == null ||
                (playerCount != 2 && playerCount != 4))
            {
                return true;
            }

            for (int playerNumber = 1; playerNumber <= playerCount; playerNumber++)
            {
                if (MultiplayerRuntime.GetPlayer(playerNumber) == null)
                {
                    return true;
                }
            }

            GraphicsDevice graphics = host.GraphicsDevice;
            EnsureTargets(graphics, playerCount);

            RenderTargetBinding[] previousTargets = graphics.GetRenderTargets();
            int previousScreen = Camera.CurrentScreen;
            Vector2 previousOffset = Camera.Offset;

            host.EndBatch();

            try
            {
                for (int playerNumber = 1; playerNumber <= playerCount; playerNumber++)
                {
                    PlayerEntity player = MultiplayerRuntime.GetPlayer(playerNumber);
                    int screen = previousScreen;
                    Vector2 offset = previousOffset;
                    if (playerNumber > 1)
                    {
                        screen = GetPlayerScreen(player, playerNumber, out offset);
                    }

                    int viewIndex = playerNumber - 1;
                    ViewScreens[viewIndex] = screen;
                    ViewOffsets[viewIndex] = offset;
                    ViewTargetIndexes[viewIndex] = FindMatchingView(viewIndex);

                }

                for (int playerNumber = 1; playerNumber <= playerCount; playerNumber++)
                {
                    int viewIndex = playerNumber - 1;
                    if (ViewTargetIndexes[viewIndex] != viewIndex)
                    {
                        continue;
                    }

                    Camera.Offset = ViewOffsets[viewIndex];
                    DrawView(
                        game,
                        host,
                        graphics,
                        PlayerTargets[viewIndex],
                        ViewScreens[viewIndex],
                        GetViewPlayerMask(viewIndex, playerCount)
                    );
                }
            }
            finally
            {
                _drawingPass = false;
                LocalMultiplayerApi.SetCurrentViewPlayerMask(1);
                RestoreTargets(graphics, previousTargets);
                CameraScreenField.SetValue(null, previousScreen);
                Camera.Offset = previousOffset;
                host.StartBatch();
            }

            if (playerCount == 2)
            {
                if (ModEntry.TwoPlayerLayout == TwoPlayerLayout.Compact)
                {
                    DrawCompactTwoPlayerViews();
                }
                else
                {
                    DrawTwoPlayerViews();
                }
            }
            else
            {
                DrawFourPlayerViews();
            }

            return false;
        }

        public static void Release()
        {
            for (int i = 0; i < PlayerTargets.Length; i++)
            {
                DisposeTarget(ref PlayerTargets[i]);
                ViewTargetIndexes[i] = i;
                ViewScreens[i] = 0;
                ViewOffsets[i] = Vector2.Zero;
                CameraInitialized[i] = false;
                CameraScreens[i] = 0;
                CameraOffsets[i] = Vector2.Zero;
            }
        }

        private static void DrawTwoPlayerViews()
        {
            for (int i = 0; i < 2; i++)
            {
                PlayerEntity player = MultiplayerRuntime.GetPlayer(i + 1);
                Game1.spriteBatch.Draw(
                    PlayerTargets[ViewTargetIndexes[i]],
                    new Rectangle(i * HalfWidth, 0, HalfWidth, Height),
                    GetPlayerViewport(player),
                    Color.White
                );
            }
        }

        private static void DrawCompactTwoPlayerViews()
        {
            Game1.instance.GraphicsDevice.Clear(Color.Black);
            var source = new Rectangle(0, 0, Width, Height);
            int destinationY = (Height - HalfHeight) / 2;

            for (int i = 0; i < 2; i++)
            {
                Game1.spriteBatch.Draw(
                    PlayerTargets[ViewTargetIndexes[i]],
                    new Rectangle(
                        i * HalfWidth,
                        destinationY,
                        HalfWidth,
                        HalfHeight
                    ),
                    source,
                    Color.White
                );
            }
        }

        private static Rectangle GetPlayerViewport(PlayerEntity player)
        {
            BodyComp body = player == null ? null : player.GetComponent<BodyComp>();
            int centerX = body == null ? Width / 2 : body.GetHitbox().Center.X;
            int sourceX = centerX < HalfWidth ? 0 : HalfWidth;
            return new Rectangle(sourceX, 0, HalfWidth, Height);
        }

        private static void DrawFourPlayerViews()
        {
            var source = new Rectangle(0, 0, Width, Height);
            for (int i = 0; i < 4; i++)
            {
                int column = i % 2;
                int row = i / 2;
                Game1.spriteBatch.Draw(
                    PlayerTargets[ViewTargetIndexes[i]],
                    new Rectangle(
                        column * HalfWidth,
                        row * HalfHeight,
                        HalfWidth,
                        HalfHeight
                    ),
                    source,
                    Color.White
                );
            }
        }

        private static int FindMatchingView(int viewIndex)
        {
            for (int i = 0; i < viewIndex; i++)
            {
                if (ViewScreens[i] == ViewScreens[viewIndex] &&
                    ViewOffsets[i] == ViewOffsets[viewIndex])
                {
                    return ViewTargetIndexes[i];
                }
            }

            return viewIndex;
        }

        private static void DrawView(
            JumpGame game,
            Game1 host,
            GraphicsDevice graphics,
            RenderTarget2D target,
            int screen,
            int playerMask
        )
        {
            graphics.SetRenderTarget(target);
            graphics.Clear(Color.Black);
            CameraScreenField.SetValue(null, screen);
            host.StartBatch();
            _drawingPass = true;
            LocalMultiplayerApi.SetCurrentViewPlayerMask(playerMask);

            try
            {
                game.Draw();
            }
            finally
            {
                _drawingPass = false;
                LocalMultiplayerApi.SetCurrentViewPlayerMask(1);
                host.EndBatch();
            }
        }

        private static int GetViewPlayerMask(int targetIndex, int playerCount)
        {
            int mask = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (ViewTargetIndexes[i] == targetIndex)
                {
                    mask |= 1 << i;
                }
            }

            return mask;
        }

        private static int GetPlayerScreen(
            PlayerEntity player,
            int playerNumber,
            out Vector2 offset
        )
        {
            BodyComp body = player.GetComponent<BodyComp>();
            if (body == null)
            {
                offset = Camera.Offset;
                return Camera.CurrentScreen;
            }

            int index = playerNumber - 1;
            int hostScreen = Camera.CurrentScreen;
            Vector2 hostOffset = Camera.Offset;
            if (!CameraInitialized[index])
            {
                CameraScreens[index] = hostScreen;
                CameraOffsets[index] = hostOffset;
                CameraInitialized[index] = true;
            }

            CameraScreenField.SetValue(null, CameraScreens[index]);
            Camera.Offset = CameraOffsets[index];

            try
            {
                bool isOnSand = BodyIsOnBlockMethod != null &&
                    (bool)BodyIsOnBlockMethod.Invoke(
                        body,
                        new object[] { typeof(SandBlock) }
                    );

                if (!isOnSand)
                {
                    Camera.UpdateCameraWithVelocity(
                        body.GetHitbox().Center,
                        body.Velocity
                    );
                }

                CameraScreens[index] = Camera.CurrentScreen;
                CameraOffsets[index] = Camera.Offset;
                offset = CameraOffsets[index];
                return CameraScreens[index];
            }
            finally
            {
                CameraScreenField.SetValue(null, hostScreen);
                Camera.Offset = hostOffset;
            }
        }

        private static void EnsureTargets(GraphicsDevice graphics, int playerCount)
        {
            for (int i = 0; i < playerCount; i++)
            {
                if (PlayerTargets[i] == null || PlayerTargets[i].IsDisposed)
                {
                    PlayerTargets[i] = new RenderTarget2D(graphics, Width, Height);
                }
            }
        }

        private static void RestoreTargets(
            GraphicsDevice graphics,
            RenderTargetBinding[] previousTargets
        )
        {
            if (previousTargets == null || previousTargets.Length == 0)
            {
                graphics.SetRenderTarget(null);
            }
            else
            {
                graphics.SetRenderTargets(previousTargets);
            }
        }

        private static void DisposeTarget(ref RenderTarget2D target)
        {
            if (target != null)
            {
                target.Dispose();
                target = null;
            }
        }
    }
}
