using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JumpKing;
using JumpKing.JKMemory;
using JumpKing.MiscEntities.WorldItems;
using JumpKing.Player.Skins;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Builds a player's king sprite from that player's own equipped items.
    ///
    /// Equipped items are drawn as sprite layers, and the base game keeps one
    /// layer list shared by everyone: <c>SkinManager.EnableSkin</c> pushes the
    /// item's sprite into the shared <c>LayeredSprite</c> that every player then
    /// draws from. So while the item *effects* can be resolved per player, the
    /// appearance could not - additional players showed player 1's equipment.
    ///
    /// This rebuilds the layer list per player instead of copying the shared one.
    ///
    /// Only additional players ever reach this code. Player 1 keeps drawing the
    /// game's own shared sprites untouched, which is automatically correct: the
    /// global equip state is what the local pad toggles, and that is player 1's.
    /// In single player nothing here is constructed or called at all.
    /// </summary>
    internal static class PlayerSkinComposer
    {
        /// <summary>
        /// Back to front, the order <c>SkinManager._AddSkin</c> applies layers in.
        /// Note this is not the declaration order of the enum.
        /// </summary>
        private static readonly SkinLayer[] LayerOrder =
        {
            SkinLayer.Cape,
            SkinLayer.Boots,
            SkinLayer.Shirt,
            SkinLayer.SnakeRing,
            SkinLayer.Hat
        };

        private static readonly Type SkinManagerType = AccessTools.TypeByName(
            "JumpKing.Player.Skins.SkinManager"
        );
        private static readonly FieldInfo AppliedSkinsField =
            SkinManagerType == null ? null :
                AccessTools.Field(SkinManagerType, "m_applied_skins");
        private static readonly FieldInfo KingSpritesField =
            SkinManagerType == null ? null :
                AccessTools.Field(SkinManagerType, "m_king_sprites");
        private static readonly FieldInfo SettingsField =
            SkinManagerType == null ? null :
                AccessTools.Field(SkinManagerType, "m_settings");

        private static readonly Type SpriteGroupType =
            AccessTools.TypeByName("JumpKing.JKMemory.IKingSpriteGroup");
        private static readonly MethodInfo GetSpriteMethod =
            SpriteGroupType == null ? null : AccessTools.Method(
                SpriteGroupType,
                "_GetSprite",
                new[] { typeof(int) }
            );

        private static readonly Type LayeredSpriteType = AccessTools.TypeByName(
            "JumpKing.XnaWrappers.LayeredSprite"
        );
        private static readonly PropertyInfo LayeredSpritesProperty =
            LayeredSpriteType == null ? null :
                AccessTools.Property(LayeredSpriteType, "Sprites");
        private static readonly ConstructorInfo LayeredSpriteConstructor =
            LayeredSpriteType == null ? null : AccessTools.Constructor(
                LayeredSpriteType,
                new[] { typeof(Sprite), typeof(Sprite[]) }
            );

        /// <summary>Shared state sprite to its key within the regular sprite group.</summary>
        private static Dictionary<Sprite, int> _stateKeys;

        private static readonly Dictionary<CacheKey, Sprite> Cache =
            new Dictionary<CacheKey, Sprite>();

        public static void Release()
        {
            Cache.Clear();
            _stateKeys = null;
        }

        /// <summary>
        /// Returns a sprite showing this player's own equipment, or null when the
        /// source is not one of the layered player sprites, in which case the
        /// caller keeps what it already had.
        /// </summary>
        public static Sprite Compose(Sprite source, PlayerContext context)
        {
            if (source == null || context == null)
            {
                return null;
            }

            if (LayeredSpriteConstructor == null || GetSpriteMethod == null ||
                LayeredSpriteType == null)
            {
                return null;
            }

            if (!LayeredSpriteType.IsInstanceOfType(source))
            {
                return null;
            }

            Dictionary<Sprite, int> stateKeys = GetStateKeys();
            int stateKey;
            if (stateKeys == null || !stateKeys.TryGetValue(source, out stateKey))
            {
                return null;
            }

            List<Skin> skins = BuildSkinList(context);
            if (skins == null)
            {
                return null;
            }

            var key = new CacheKey(context.Number, stateKey, Signature(skins));
            Sprite cached;
            if (Cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var layers = LayeredSpritesProperty.GetValue(source, null) as IList;
            if (layers == null || layers.Count == 0)
            {
                return null;
            }

            var baseSprite = (Sprite)layers[0];
            var composedLayers = new List<Sprite>(skins.Count);
            var kingSprites =
                KingSpritesField.GetValue(null) as Dictionary<Items, KingSprites>;

            for (int i = 0; i < skins.Count; i++)
            {
                KingSprites sprites;
                if (kingSprites == null ||
                    !kingSprites.TryGetValue(skins[i].item, out sprites) ||
                    sprites.m_groups == null || sprites.m_groups.Count == 0)
                {
                    continue;
                }

                // Group 0 is the regular king; the key is the same index the
                // shared layer list was built from, which is what makes the
                // layer line up with the pose being drawn.
                var layer = GetSpriteMethod.Invoke(
                    sprites.m_groups[0],
                    new object[] { stateKey }
                ) as Sprite;
                if (layer != null)
                {
                    composedLayers.Add(layer);
                }
            }

            Sprite composed = (Sprite)LayeredSpriteConstructor.Invoke(
                new object[] { baseSprite, composedLayers.ToArray() }
            );
            Cache[key] = composed;
            return composed;
        }

        /// <summary>
        /// The globally applied skins, with this player's own per-player items
        /// added or removed, then put back into the game's layer order.
        /// </summary>
        private static List<Skin> BuildSkinList(PlayerContext context)
        {
            var applied = AppliedSkinsField == null ? null :
                AppliedSkinsField.GetValue(null) as List<Skin>;
            if (applied == null)
            {
                return null;
            }

            var result = new List<Skin>(applied);
            foreach (Items item in ItemToggles.PerPlayerItems)
            {
                bool wanted = context.Items.IsEquipped(item);
                int index = result.FindIndex(s => s.item == item);
                if (wanted && index < 0)
                {
                    Skin skin;
                    if (TryGetSkin(item, out skin))
                    {
                        result.Add(skin);
                    }
                }
                else if (!wanted && index >= 0)
                {
                    result.RemoveAt(index);
                }
            }

            result.Sort((a, b) => LayerRank(a).CompareTo(LayerRank(b)));
            return result;
        }

        private static bool TryGetSkin(Items item, out Skin skin)
        {
            skin = default(Skin);
            object settings = SettingsField == null ? null : SettingsField.GetValue(null);
            if (settings == null)
            {
                return false;
            }

            try
            {
                MethodInfo getSkin = AccessTools.Method(settings.GetType(), "GetSkin");
                if (getSkin == null)
                {
                    return false;
                }

                skin = (Skin)getSkin.Invoke(settings, new object[] { item });
                return true;
            }
            catch (Exception)
            {
                // GetSkin throws when the map ships no art for the item.
                return false;
            }
        }

        private static int LayerRank(Skin skin)
        {
            if (skin.layers == null || skin.layers.Length == 0)
            {
                return LayerOrder.Length;
            }

            int rank = Array.IndexOf(LayerOrder, skin.layers[0]);
            return rank < 0 ? LayerOrder.Length : rank;
        }

        private static Dictionary<Sprite, int> GetStateKeys()
        {
            if (_stateKeys != null)
            {
                return _stateKeys;
            }

            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return null;
            }

            Type keyType = AccessTools.TypeByName(
                "JumpKing.JKMemory.KingSpriteLayers.Regular+SpriteKey"
            );
            if (keyType == null || !keyType.IsEnum)
            {
                return null;
            }

            JKContentManager.PlayerSprites sprites =
                Game1.instance.contentManager.playerSprites;
            var map = new Dictionary<Sprite, int>();

            // The sprite properties and the enum members share their names, which
            // is what ties a pose to the key its layers were stored under.
            foreach (string name in Enum.GetNames(keyType))
            {
                PropertyInfo property = AccessTools.Property(
                    typeof(JKContentManager.PlayerSprites),
                    name
                );
                var sprite = property == null ? null :
                    property.GetValue(sprites, null) as Sprite;
                if (sprite != null)
                {
                    map[sprite] = (int)Enum.Parse(keyType, name);
                }
            }

            _stateKeys = map.Count > 0 ? map : null;
            return _stateKeys;
        }

        private static string Signature(List<Skin> skins)
        {
            var parts = new string[skins.Count];
            for (int i = 0; i < skins.Count; i++)
            {
                parts[i] = ((int)skins[i].item).ToString();
            }

            return string.Join(",", parts);
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly int _player;
            private readonly int _stateKey;
            private readonly string _skins;

            public CacheKey(int player, int stateKey, string skins)
            {
                _player = player;
                _stateKey = stateKey;
                _skins = skins;
            }

            public bool Equals(CacheKey other)
            {
                return _player == other._player &&
                    _stateKey == other._stateKey &&
                    _skins == other._skins;
            }

            public override bool Equals(object obj)
            {
                return obj is CacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                int hash = _player * 397;
                hash = (hash ^ _stateKey) * 397;
                return hash ^ (_skins == null ? 0 : _skins.GetHashCode());
            }
        }
    }
}
