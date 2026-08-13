using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using JumpKing;
using JumpKing.Workshop;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// How a player met a prop. The geometry is the same one players use on
    /// each other, so the skill transfers: coming down on something's head is
    /// an attack, walking into its side is not.
    /// </summary>
    internal enum PropTouch
    {
        Side,
        Stomp
    }

    /// <summary>
    /// Anything in the arena that is not a player and that a player can run
    /// into. One interface, so adding a kind of prop later means adding a class
    /// and an element name, and touching nothing else.
    /// </summary>
    internal interface IBattleProp
    {
        Rectangle Hitbox { get; }

        /// <summary>False while waiting to respawn - no collision, no drawing.</summary>
        bool IsActive { get; }

        void Update();

        /// <summary>
        /// Runs for a player overlapping this prop. Returns the rebound to give
        /// the player, or null to leave their motion alone.
        /// </summary>
        float? OnTouch(PlayerContext player, PropTouch touch);

        void Draw();

        void Reset();
    }

    /// <summary>
    /// Reads the props a map defines and runs them.
    ///
    /// Placement lives in the level's own <c>level_settings.xml</c> rather than
    /// in this mod, so a battle map is authored the same way its spawn points
    /// are - by the person building the map, without rebuilding anything.
    /// </summary>
    internal static class BattleProps
    {
        private static readonly List<IBattleProp> Props = new List<IBattleProp>();
        private static string _loadedRoot;

        internal static IList<IBattleProp> Active { get { return Props; } }

        internal static void Reset()
        {
            Props.Clear();
            _loadedRoot = null;
            BattleSprites.Reset();
        }

        private static void EnsureLoaded()
        {
            string root = Game1.instance?.contentManager?.root;
            if (string.IsNullOrEmpty(root) || root == "Content")
            {
                return;
            }

            if (_loadedRoot == root)
            {
                return;
            }

            _loadedRoot = root;
            Props.Clear();

            BattlePropsData data = LocalMultiplayerLevelFile.Load()?.BattleProps;
            if (data == null)
            {
                return;
            }

            for (int i = 0; i < data.Heal.Count; i++)
            {
                Props.Add(new HealProp(data.Heal[i]));
            }

            for (int i = 0; i < data.Walker.Count; i++)
            {
                Props.Add(new WalkerProp(data.Walker[i]));
            }
        }

        /// <summary>
        /// Advances every prop and resolves it against every player, using the
        /// same head band that decides a stomp between two players.
        /// </summary>
        internal static void Update(List<PlayerContext> contexts)
        {
            EnsureLoaded();
            if (Props.Count == 0)
            {
                return;
            }

            for (int i = 0; i < Props.Count; i++)
            {
                IBattleProp prop = Props[i];
                prop.Update();
                if (!prop.IsActive)
                {
                    continue;
                }

                Rectangle propBox = prop.Hitbox;
                for (int p = 0; p < contexts.Count; p++)
                {
                    PlayerContext context = contexts[p];
                    if (context.Body == null)
                    {
                        continue;
                    }

                    Rectangle playerBox = context.Body.GetHitbox();
                    if (!playerBox.Intersects(propBox))
                    {
                        continue;
                    }

                    PropTouch touch =
                        context.Body.Velocity.Y > 0f &&
                        playerBox.Bottom <= propBox.Top + BattleMode.HeadBandSize
                            ? PropTouch.Stomp
                            : PropTouch.Side;

                    float? rebound = prop.OnTouch(context, touch);
                    if (rebound.HasValue)
                    {
                        context.Body.Velocity.Y = rebound.Value;
                    }

                    if (!prop.IsActive)
                    {
                        break;
                    }
                }
            }
        }

        internal static void Draw()
        {
            for (int i = 0; i < Props.Count; i++)
            {
                if (Props[i].IsActive)
                {
                    Props[i].Draw();
                }
            }
        }

        internal static void ResetAll()
        {
            for (int i = 0; i < Props.Count; i++)
            {
                Props[i].Reset();
            }
        }
    }

    /// <summary>
    /// Heals whoever touches it, then goes away for a while. Worth placing
    /// somewhere that has to be climbed to: wanting it is then a reason to take
    /// on the fall risk, which is the same trade the rest of the fight runs on.
    /// </summary>
    internal sealed class HealProp : IBattleProp
    {
        /// <summary>
        /// Drawn from an explicit pixel map rather than stacked rectangles.
        /// At this size the shape is the whole readability problem - bands of
        /// colour just make a sandwich - and the thing that says hot dog is the
        /// sausage overhanging the bun at both ends, which needs the ends drawn
        /// deliberately.
        ///
        /// o outline, b bun, B bun shadow, s sausage, S sausage shadow,
        /// m mustard, . transparent.
        /// </summary>
        private static readonly string[] Sprite =
        {
            "...oooooooooooo...",
            "..obbbbbbbbbbbbo..",
            "..obbbbbbbbbbbbo..",
            ".oosssssssssssssoo",
            "osmsssmsssmsssmsso",
            "osssssssssssssssso",
            ".ooSSSSSSSSSSSSSoo",
            "..obbbbbbbbbbbbo..",
            "..obbbbbbbbbbbbo..",
            "..oBBBBBBBBBBBBo..",
            "...oooooooooooo..."
        };

        private static readonly int Width = Sprite[0].Length;
        private static readonly int Height = Sprite.Length;

        private readonly HealPropData _data;
        private int _respawnFrames;

        public HealProp(HealPropData data)
        {
            _data = data;
        }

        public Rectangle Hitbox
        {
            get
            {
                return new Rectangle(
                    (int)_data.Position.X - Width / 2,
                    (int)_data.Position.Y - Height / 2,
                    Width,
                    Height
                );
            }
        }

        public bool IsActive { get { return _respawnFrames <= 0; } }

        public void Update()
        {
            if (_respawnFrames > 0)
            {
                _respawnFrames--;
            }
        }

        public float? OnTouch(PlayerContext player, PropTouch touch)
        {
            if (!BattleMode.Heal(player, _data.Amount))
            {
                // Already at full health - leave it standing for someone who
                // needs it rather than burning it for nothing.
                return null;
            }

            BattleMode.PlayPickupSound();
            _respawnFrames =
                (int)Math.Round(_data.RespawnSeconds * PlayerValues.FPS);
            return null;
        }

        public void Draw()
        {
            Texture2D pixel = BattleMode.Pixel;
            if (pixel == null)
            {
                return;
            }

            Rectangle box = Hitbox;
            Vector2 screen = Camera.TransformVector2(
                new Vector2(box.X, box.Y)
            );
            int x = (int)screen.X;
            int y = (int)screen.Y;

            // Runs of the same colour are drawn as one rectangle rather than a
            // draw call per pixel, which keeps this to a handful of quads.
            for (int row = 0; row < Sprite.Length; row++)
            {
                string line = Sprite[row];
                int start = 0;
                while (start < line.Length)
                {
                    char symbol = line[start];
                    int end = start;
                    while (end + 1 < line.Length && line[end + 1] == symbol)
                    {
                        end++;
                    }

                    if (symbol != '.')
                    {
                        BattleMode.Fill(
                            pixel,
                            x + start,
                            y + row,
                            end - start + 1,
                            1,
                            ColorFor(symbol)
                        );
                    }

                    start = end + 1;
                }
            }
        }

        private static Color ColorFor(char symbol)
        {
            switch (symbol)
            {
                case 'b': return new Color(228, 180, 110);
                case 'B': return new Color(188, 140, 82);
                case 's': return new Color(202, 88, 60);
                case 'S': return new Color(150, 58, 40);
                case 'm': return new Color(248, 208, 64);
                default: return new Color(52, 30, 16);
            }
        }

        public void Reset()
        {
            _respawnFrames = 0;
        }
    }

    /// <summary>
    /// A slow patrol between two points. Slow on purpose: the players it shares
    /// an arena with are being driven through chat, and a hazard they cannot
    /// react to is not a hazard, it is a tax.
    ///
    /// It is also the answer to a stalemate. Two players who refuse to commit
    /// can hold their ground indefinitely against each other, but not against
    /// something that keeps arriving.
    /// </summary>
    internal sealed class WalkerProp : IBattleProp
    {
        /// <summary>
        /// The base game's own archaeologist, four frames of walk on the lower
        /// row of a 4x2 sheet. Borrowing a sprite the game already ships keeps
        /// the arena looking like Jump King rather than like a mod drew shapes
        /// on it.
        /// </summary>
        private const string SpriteAsset =
            "props/textures/old_man/archaeologist";

        private const int FrameWidth = 32;
        private const int FrameHeight = 40;
        private const int WalkFrames = 4;

        /// <summary>The row of the sheet that faces the camera.</summary>
        private const int WalkRow = 1;

        /// <summary>Frames each walk frame is held for.</summary>
        private const int FrameHold = 12;

        // The drawn sprite is bigger than what it can be hit on: the art has a
        // hat and a pack that read as silhouette but should not count as the
        // creature's body. The box is the figure inside it.
        private const int Width = 18;
        private const int Height = 26;

        private readonly WalkerPropData _data;
        private Vector2 _position;
        private bool _towardTo;
        private int _respawnFrames;
        private int _animation;

        public WalkerProp(WalkerPropData data)
        {
            _data = data;
            _position = data.From;
            _towardTo = true;
        }

        public Rectangle Hitbox
        {
            get
            {
                return new Rectangle(
                    (int)_position.X - Width / 2,
                    (int)_position.Y - Height,
                    Width,
                    Height
                );
            }
        }

        public bool IsActive { get { return _respawnFrames <= 0; } }

        public void Update()
        {
            if (_respawnFrames > 0)
            {
                _respawnFrames--;
                if (_respawnFrames == 0)
                {
                    _position = _data.From;
                    _towardTo = true;
                }

                return;
            }

            _animation++;

            Vector2 target = _towardTo ? _data.To : _data.From;
            Vector2 delta = target - _position;
            float distance = delta.Length();
            if (distance <= _data.Speed)
            {
                _position = target;
                _towardTo = !_towardTo;
                return;
            }

            delta.Normalize();
            _position += delta * _data.Speed;
        }

        public float? OnTouch(PlayerContext player, PropTouch touch)
        {
            if (touch == PropTouch.Stomp)
            {
                // Killed by exactly the move that beats a player, so nothing
                // new has to be learned to deal with one - and it lands with
                // the same sound, because it is the same hit.
                BattleMode.PlaySplatSound();
                _respawnFrames =
                    (int)Math.Round(_data.RespawnSeconds * PlayerValues.FPS);
                return 0f - BattleMode.ReboundOff(player.Body.Velocity.Y);
            }

            // Damage first, so the launch is not applied to someone the hit
            // bounced off because they were still invulnerable.
            int before = BattleMode.HealthOf(player);
            BattleMode.Hurt(player, _data.Damage);
            if (BattleMode.HealthOf(player) != before)
            {
                BattleMode.Launch(player, _data.Damage, Hitbox.Center.X);
            }

            return null;
        }

        public void Draw()
        {
            Texture2D sheet = BattleSprites.Archaeologist;
            Rectangle box = Hitbox;

            if (sheet == null)
            {
                // Falling back to a solid block rather than drawing nothing:
                // an invisible thing that damages you is worse than an ugly one.
                Texture2D pixel = BattleMode.Pixel;
                if (pixel == null)
                {
                    return;
                }

                Vector2 fallback = Camera.TransformVector2(
                    new Vector2(box.X, box.Y)
                );
                BattleMode.Fill(pixel, (int)fallback.X, (int)fallback.Y,
                    Width, Height, new Color(150, 60, 160));
                return;
            }

            int frame = (_animation / FrameHold) % WalkFrames;
            var source = new Rectangle(
                frame * FrameWidth,
                WalkRow * FrameHeight,
                FrameWidth,
                FrameHeight
            );

            // The sheet's figure is wider and taller than the hitbox, so it is
            // hung off the box's bottom centre - the feet line up with what the
            // collision calls the floor, whatever the art does above that.
            Vector2 screen = Camera.TransformVector2(
                new Vector2(
                    box.Center.X - FrameWidth / 2f,
                    box.Bottom - FrameHeight
                )
            );

            Game1.spriteBatch.Draw(
                sheet,
                new Rectangle(
                    (int)screen.X,
                    (int)screen.Y,
                    FrameWidth,
                    FrameHeight
                ),
                source,
                Color.White,
                0f,
                Vector2.Zero,
                _towardTo ? SpriteEffects.None : SpriteEffects.FlipHorizontally,
                0f
            );
        }

        public void Reset()
        {
            _respawnFrames = 0;
            _position = _data.From;
            _towardTo = true;
            _animation = 0;
        }
    }
}
