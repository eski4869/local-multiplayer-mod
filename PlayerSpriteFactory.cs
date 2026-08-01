using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JumpKing;
using JumpKing.Player;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Recolours the king sprite per player so the extra players are
    /// distinguishable. Purely cosmetic; it has no bearing on the player context.
    /// </summary>
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
                        maximum,
                        minimum + (chroma * 7 + 7) / 15,
                        minimum,
                        source.A
                    );
                case 3:
                    return new Color(
                        minimum,
                        maximum,
                        minimum + (chroma + 1) / 3,
                        source.A
                    );
                case 4:
                    int silver = minimum + (chroma * 82 + 50) / 100;
                    return new Color(
                        silver,
                        silver,
                        silver,
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
}
