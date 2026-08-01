using System;
using System.Collections.Generic;
using EntityComponent;
using JumpKing.MiscEntities.WorldItems;
using JumpKing.Player;
using JumpKing.SaveThread;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Everything the base game keeps in a one-player global, owned per player.
    /// Player 1 is not special: it gets a context exactly like the others.
    /// </summary>
    public interface IPlayerContext
    {
        int Number { get; }
        PlayerEntity Player { get; }
        BodyComp Body { get; }

        /// <summary>
        /// Screen index and offset that <see cref="JumpKing.Camera" /> is set to
        /// while this player is the scoped player. Drives both collision and draw.
        /// </summary>
        int CameraScreen { get; }
        Vector2 CameraOffset { get; }

        /// <summary>
        /// Per-player item equip state. Items without an explicit override fall
        /// through to the base game global, so an untouched context behaves
        /// exactly like single player.
        /// </summary>
        IPlayerItems Items { get; }

        /// <summary>
        /// Free-form per-player storage for consumer mods. Use this instead of a
        /// static field for any state that is conceptually per player.
        /// </summary>
        IDictionary<object, object> State { get; }
    }

    public interface IPlayerItems
    {
        bool IsEquipped(Items item);
        void SetEquipped(Items item, bool equipped);
        void ClearOverride(Items item);
        bool HasOverride(Items item);
    }

    internal sealed class PlayerContext : IPlayerContext
    {
        private readonly int _number;
        private readonly PlayerEntity _player;
        private readonly BodyComp _body;
        private readonly PlayerItems _items;
        private readonly Dictionary<object, object> _state =
            new Dictionary<object, object>();

        public int Screen;
        public Vector2 Offset;
        public SaveState SaveState;

        /// <summary>
        /// True once the camera has been seeded from the live game camera. Until
        /// then the context adopts whatever the camera reads on first scope entry.
        /// </summary>
        public bool CameraSeeded;

        /// <summary>
        /// True once every mod's <c>[OnLevelStart]</c> has been replayed against
        /// this player, so a mid-run player-count change does not replay twice.
        /// </summary>
        public bool LevelStartReplayed;

        public PlayerContext(int number, PlayerEntity player)
        {
            _number = number;
            _player = player;
            _body = player == null ? null : player.GetComponent<BodyComp>();
            _items = new PlayerItems();
        }

        public int Number { get { return _number; } }
        public PlayerEntity Player { get { return _player; } }
        public BodyComp Body { get { return _body; } }
        public int CameraScreen { get { return Screen; } }
        public Vector2 CameraOffset { get { return Offset; } }
        public IPlayerItems Items { get { return _items; } }
        public IDictionary<object, object> State { get { return _state; } }

        public PlayerItems ItemsInternal { get { return _items; } }

        public bool IsAlive
        {
            get { return _player != null && _player.IsAlive; }
        }

        /// <summary>
        /// Player 1 owns the real save slot and the real camera; the others carry
        /// their own copies.
        /// </summary>
        public bool IsPrimary { get { return _number == 1; } }
    }

    internal sealed class PlayerItems : IPlayerItems
    {
        private readonly Dictionary<Items, bool> _overrides =
            new Dictionary<Items, bool>();

        public bool TryGetOverride(Items item, out bool equipped)
        {
            return _overrides.TryGetValue(item, out equipped);
        }

        public bool IsEquipped(Items item)
        {
            bool equipped;
            if (_overrides.TryGetValue(item, out equipped))
            {
                return equipped;
            }

            return GlobalItemState.IsWearingSkinUnpatched(item);
        }

        public void SetEquipped(Items item, bool equipped)
        {
            _overrides[item] = equipped;
        }

        public void ClearOverride(Items item)
        {
            _overrides.Remove(item);
        }

        public bool HasOverride(Items item)
        {
            return _overrides.ContainsKey(item);
        }

        public void Clear()
        {
            _overrides.Clear();
        }
    }
}
