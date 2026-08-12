using System;
using System.Collections.Generic;
using JumpKing;
using JumpKing.Player;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Battle mode: a health gauge over each king, damage for stomping on
    /// another player's head, and damage for a splat landing.
    ///
    /// The two damage sources are deliberately in tension. Climbing above an
    /// opponent is what lets you land on them, so height is an advantage - but
    /// the same height is what turns a missed stomp into a splat, and a splat
    /// costs you health too. Attacking from higher up is worth more and risks
    /// more.
    ///
    /// Everything here is inert unless <see cref="MultiplayerRuntime.IsActive"/>
    /// is true, which single player can never make true. The feature is reached
    /// only through the additional-player paths, never by wrapping something the
    /// base game already does on its own.
    /// </summary>
    internal static class BattleMode
    {
        internal const int MaxHealth = 100;

        /// <summary>Landing on a head. Five clean stomps win a round.</summary>
        private const int StompDamage = 20;

        /// <summary>
        /// A splat landing. Lower than a stomp so that falling is a setback
        /// rather than the main way rounds are decided, but high enough that
        /// spamming height is punished.
        /// </summary>
        private const int FallDamage = 12;

        /// <summary>
        /// Ceiling on the rebound, about a sixteen frame charge against a full
        /// jump's thirty-six. Enough to break contact and read as a bounce,
        /// short of being free height - staying on top is the position that was
        /// already winning.
        /// </summary>
        private const float MaximumBounce = 4f;

        /// <summary>
        /// How much of the victim's box counts as the head. The attacker's feet
        /// have to be inside this band, so passing through someone sideways or
        /// rising into them from below is not a hit.
        /// </summary>
        private const int HeadBand = 12;

        /// <summary>
        /// Frames the victim cannot be hit again. Players have no collision with
        /// each other, so without this the boxes stay overlapped and one landing
        /// would register every frame.
        /// </summary>
        private const int HitInvulnerabilityFrames = 24;

        /// <summary>
        /// A side hit is charged as a jump rather than as an invented impulse,
        /// because that is the only motion the game already knows how to give a
        /// player. Two frames of charge: enough to leave the ground, which is
        /// what actually matters - <c>Walk</c> rewrites <c>Velocity.X</c> every
        /// frame a player is standing, so a purely horizontal shove is erased
        /// before it moves anyone. The lift is what lets the push land at all.
        ///
        /// The horizontal half is not scaled: <c>DoJump</c> adds a full
        /// <c>SPEED</c> whatever the charge, so a nudged player travels sideways
        /// exactly as fast as one who jumped there themselves.
        /// </summary>
        private const int PushJumpFrames = 2;

        /// <summary>
        /// Charge frames in a full jump - <c>JUMP_TIME</c> is 0.6s at 60fps.
        /// </summary>
        private const float FullChargeFrames = 36f;

        /// <summary>
        /// Frames before the same pair can trade another push. Separate from the
        /// damage window: being shoved should not also make someone briefly
        /// immune to being stomped.
        /// </summary>
        private const int PushCooldownFrames = 12;

        /// <summary>
        /// How fast the attacker has to be closing horizontally. Below this it
        /// is drifting into someone, not hitting them.
        /// </summary>
        private const float MinimumApproachSpeed = 1f;

        /// <summary>
        /// How much of the trade survives the impact. At 1 the two players swap
        /// horizontal velocity exactly, which sends both further than either
        /// arrived; this keeps most of the exchange while losing enough that a
        /// collision settles instead of escalating. The game's own wall bounce
        /// keeps half, so this is the gentler end of the same idea.
        /// </summary>
        private const float Restitution = 0.7f;

        /// <summary>How long the winner is announced before healing everyone.</summary>
        private const int RoundOverFrames = 180;

        /// <summary>Base size of the result text, over a 480 x 360 screen.</summary>
        private const float BannerScale = 2.2f;

        /// <summary>Frames the result spends shrinking into place.</summary>
        private const int PunchFrames = 12;

        /// <summary>
        /// When the flourish follows the impact. Landing both on the same frame
        /// makes one muddy noise; letting the banner finish settling first turns
        /// it into two beats.
        /// </summary>
        private const int FanfareDelayFrames = 14;

        private const int GaugeWidth = 24;
        private const int GaugeHeight = 4;

        /// <summary>Pixels between the top of the hitbox and the gauge.</summary>
        private const int GaugeGap = 9;

        private static readonly object FighterKey = new object();

        private static int _roundOverFrames;
        private static int _winner;

        internal static bool IsEnabled
        {
            get { return ModEntry.IsBattleMode && MultiplayerRuntime.IsActive; }
        }

        /// <summary>
        /// A player's standing in the current round. Kept in the context's state
        /// bag rather than a static array so it dies with the player it belongs
        /// to - a player count change destroys and rebuilds the contexts, and a
        /// static keyed by number would carry the old health across.
        /// </summary>
        private sealed class Fighter
        {
            public int Health = MaxHealth;
            public int Invulnerable;

            /// <summary>
            /// Where the player was last frame, and whether they actually fell
            /// since. Velocity alone is not enough: it keeps its last value
            /// while the game is paused, so an overlap held across a pause would
            /// score a hit every time the invulnerability ran out. Position is
            /// the honest question - if the player did not move down, nobody
            /// stomped anyone.
            ///
            /// Starts at NaN so the first frame compares false rather than
            /// treating any position below zero as a fall.
            /// </summary>
            public float LastY = float.NaN;
            public bool Descended;

            /// <summary>Frames before this player can be shoved again.</summary>
            public int PushCooldown;

            /// <summary>
            /// This frame's velocity, read before any contact is applied. Both
            /// players in a head-on collision have to be judged against what
            /// they were doing when they met, not against what the first
            /// resolved pair already did to them.
            /// </summary>
            public float StartVelocityX;
            public bool WasAirborne;
        }


        private static Fighter GetFighter(PlayerContext context)
        {
            if (context == null)
            {
                return null;
            }

            object existing;
            if (context.State.TryGetValue(FighterKey, out existing))
            {
                return (Fighter)existing;
            }

            var fighter = new Fighter();
            context.State[FighterKey] = fighter;
            return fighter;
        }

        internal static void OnLevelStart()
        {
            _roundOverFrames = 0;
            _winner = 0;
        }

        /// <summary>
        /// Drops every fighter, so the next read rebuilds it at full health.
        /// Used when the mode is switched on or off mid-run: a round that was
        /// half fought should not resume when the mode comes back.
        /// </summary>
        internal static void ResetRound()
        {
            _roundOverFrames = 0;
            _winner = 0;

            List<PlayerContext> contexts = MultiplayerRuntime.GetActiveContexts();
            for (int i = 0; i < contexts.Count; i++)
            {
                contexts[i].State.Remove(FighterKey);
            }
        }

        /// <summary>
        /// Runs once per frame after every player has been updated, which is why
        /// it hangs off <c>Game1.Update</c> rather than the per-player scope: a
        /// stomp is a fact about two players, and reading it mid-way through the
        /// first one would compare this frame's attacker against last frame's
        /// victim.
        /// </summary>
        internal static void Update()
        {
            if (!IsEnabled)
            {
                return;
            }

            List<PlayerContext> contexts = MultiplayerRuntime.GetActiveContexts();
            if (contexts.Count < 2)
            {
                return;
            }

            for (int i = 0; i < contexts.Count; i++)
            {
                Fighter fighter = GetFighter(contexts[i]);
                if (fighter.Invulnerable > 0)
                {
                    fighter.Invulnerable--;
                }

                if (fighter.PushCooldown > 0)
                {
                    fighter.PushCooldown--;
                }

                BodyComp body = contexts[i].Body;
                if (body == null)
                {
                    fighter.Descended = false;
                    continue;
                }

                fighter.Descended = body.Position.Y > fighter.LastY;
                fighter.LastY = body.Position.Y;
                fighter.StartVelocityX = body.Velocity.X;
                fighter.WasAirborne = !body.IsOnGround;
            }

            if (_roundOverFrames > 0)
            {
                _roundOverFrames--;
                if (RoundOverFrames - _roundOverFrames == FanfareDelayFrames)
                {
                    PlayVictoryFanfare();
                }

                if (_roundOverFrames == 0)
                {
                    StartNextRound(contexts);
                }

                return;
            }

            ResolveContacts(contexts);
        }

        /// <summary>
        /// Every pair is judged once, not once per direction. A contact is one
        /// event between two players - asking "did A hit B" and then "did B hit
        /// A" about the same overlap would resolve it twice.
        /// </summary>
        private static void ResolveContacts(List<PlayerContext> contexts)
        {
            for (int a = 0; a < contexts.Count; a++)
            {
                PlayerContext first = contexts[a];
                if (first.Body == null)
                {
                    continue;
                }

                for (int b = a + 1; b < contexts.Count; b++)
                {
                    PlayerContext second = contexts[b];
                    if (second.Body == null)
                    {
                        continue;
                    }

                    Rectangle firstBox = first.Body.GetHitbox();
                    Rectangle secondBox = second.Body.GetHitbox();
                    if (!firstBox.Intersects(secondBox))
                    {
                        continue;
                    }

                    if (ScreenOf(firstBox) != ScreenOf(secondBox))
                    {
                        continue;
                    }

                    // A stomp outranks a shove: someone who came down on a head
                    // did not merely bump into a side.
                    if (IsStomp(first, firstBox, second, secondBox))
                    {
                        ApplyStomp(first, second, contexts);
                        continue;
                    }

                    if (IsStomp(second, secondBox, first, firstBox))
                    {
                        ApplyStomp(second, first, contexts);
                        continue;
                    }

                    ApplyCollision(first, firstBox, second, secondBox);
                }
            }
        }

        /// <summary>
        /// Which screen a box is on, by the same rule the camera uses.
        ///
        /// Screens are stacked in world space 360 apart, so two players in
        /// rooms the map joins left-to-right through a teleport are physically
        /// only a screen height apart - close enough that someone standing on
        /// the upper room's floor overlaps someone at the lower room's ceiling.
        /// They are not near each other in any sense the players can see, so
        /// they do not touch.
        /// </summary>
        private static int ScreenOf(Rectangle box)
        {
            return -(int)Math.Floor(box.Center.Y / 360f);
        }

        private static bool IsStomp(
            PlayerContext attacker,
            Rectangle attackBox,
            PlayerContext victim,
            Rectangle victimBox
        )
        {
            if (attacker.Body.Velocity.Y <= 0f || !GetFighter(attacker).Descended)
            {
                // Only a descending player can stomp. Rising into someone from
                // below is the attacker's mistake, not a hit.
                return false;
            }

            if (GetFighter(victim).Invulnerable > 0)
            {
                return false;
            }

            // The feet have to be in the victim's head, not level with them.
            return attackBox.Bottom <= victimBox.Top + HeadBand;
        }

        private static void ApplyStomp(
            PlayerContext attacker,
            PlayerContext victim,
            List<PlayerContext> contexts
        )
        {
            Fighter victimFighter = GetFighter(victim);
            victimFighter.Health -= StompDamage;
            victimFighter.Invulnerable = HitInvulnerabilityFrames;
            attacker.Body.Velocity.Y = 0f - Bounce(attacker.Body.Velocity.Y);
            ForceSplat(victim.Player);
            PlayHitSound();

            if (victimFighter.Health <= 0)
            {
                victimFighter.Health = 0;
                EndRound(victim, contexts);
            }
        }

        /// <summary>
        /// How hard the attacker comes back off a head, from how hard they
        /// arrived. A fixed rebound was the same whether the attacker dropped
        /// from a rooftop or stepped off a ledge, so the gentle case reversed
        /// far more speed than it carried in and looked like the player had
        /// been fired upward by nothing.
        ///
        /// The game's own wall bounce keeps half the speed it took, so a head
        /// does the same.
        /// </summary>
        private static float Bounce(float impactSpeed)
        {
            return Math.Min(impactSpeed * PlayerValues.BOUNCE, MaximumBounce);
        }

        /// <summary>
        /// Side contact, modelled as two equal masses trading horizontal
        /// velocity. A straight swap is what an ideal bounce would do; the
        /// restitution below keeps some of it back, so a collision loses energy
        /// the way hitting terrain does rather than firing both players away
        /// harder than they arrived.
        ///
        /// Only X is traded. Swapping the vertical too would let a falling
        /// player hand off their fall and hang in the air, which is not a
        /// collision anyone would recognise.
        /// </summary>
        private static void ApplyCollision(
            PlayerContext first,
            Rectangle firstBox,
            PlayerContext second,
            Rectangle secondBox
        )
        {
            Fighter firstFighter = GetFighter(first);
            Fighter secondFighter = GetFighter(second);
            if (firstFighter.PushCooldown > 0 || secondFighter.PushCooldown > 0)
            {
                return;
            }

            // Which way round they are. Exactly level centres carry no
            // direction, and the next frame will have separated them.
            int direction = Math.Sign(secondBox.Center.X - firstBox.Center.X);
            if (direction == 0)
            {
                return;
            }

            float firstVelocity = firstFighter.StartVelocityX;
            float secondVelocity = secondFighter.StartVelocityX;

            // Closing speed along the line between them. Drifting together
            // slowly, or already moving apart, is not an impact.
            float approach = (firstVelocity - secondVelocity) * direction;
            if (approach < MinimumApproachSpeed)
            {
                return;
            }

            float keep = (1f - Restitution) / 2f;
            float give = (1f + Restitution) / 2f;

            Apply(
                first,
                firstFighter,
                keep * firstVelocity + give * secondVelocity
            );
            Apply(
                second,
                secondFighter,
                give * firstVelocity + keep * secondVelocity
            );

            firstFighter.PushCooldown = PushCooldownFrames;
            secondFighter.PushCooldown = PushCooldownFrames;
            PlayCollisionSound();
        }

        private static void Apply(
            PlayerContext context,
            Fighter fighter,
            float velocityX
        )
        {
            context.Body.Velocity.X = Math.Max(
                0f - PlayerValues.SPEED,
                Math.Min(PlayerValues.SPEED, velocityX)
            );

            if (fighter.WasAirborne)
            {
                return;
            }

            // Standing players need lifting off the ground for any of this to
            // survive: Walk rewrites Velocity.X every frame a player is on the
            // floor, so a shove that leaves them standing is erased before it
            // moves them. The lift is charged as a jump so the height is one the
            // game already produces.
            context.Body.Velocity.Y =
                PlayerValues.JUMP * (PushJumpFrames / FullChargeFrames);
        }

        /// <summary>
        /// Puts the stomped player into the game's own splat: flattened, and
        /// held there until they press something, the same as a bad landing.
        /// The state carries its own consequences - it zeroes horizontal
        /// velocity, so being caught mid-jump also costs the rest of that jump.
        /// </summary>
        private static void ForceSplat(PlayerEntity player)
        {
            if (player == null || FailStateField == null)
            {
                return;
            }

            try
            {
                var failState = FailStateField.GetValue(player) as FailState;
                if (failState != null)
                {
                    failState.ForceFail();
                }
            }
            catch (Exception)
            {
                // Losing the animation is not worth losing the hit over.
            }
        }

        private static readonly System.Reflection.FieldInfo FailStateField =
            HarmonyLib.AccessTools.Field(typeof(PlayerEntity), "m_fail_state");

        /// <summary>
        /// A splat landing costs health. The base game already decides what
        /// counts as a splat - it needs the landing to happen at terminal
        /// velocity - so this reuses that judgement rather than inventing a
        /// second fall threshold that would disagree with the animation.
        /// </summary>
        internal static void OnSplat(PlayerEntity player)
        {
            if (!IsEnabled || player == null)
            {
                return;
            }

            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null || _roundOverFrames > 0)
            {
                return;
            }

            Fighter fighter = GetFighter(context);
            fighter.Health -= FallDamage;
            if (fighter.Health <= 0)
            {
                fighter.Health = 0;
                EndRound(context, MultiplayerRuntime.GetActiveContexts());
            }
        }

        /// <summary>
        /// The round goes to whoever is left standing with the most health. With
        /// two players that is just the other one; with four it keeps a player
        /// who splatted themselves out of the win without needing a separate
        /// rule for self-inflicted knockouts.
        /// </summary>
        private static void EndRound(
            PlayerContext loser,
            List<PlayerContext> contexts
        )
        {
            _winner = 0;
            int best = -1;

            for (int i = 0; i < contexts.Count; i++)
            {
                PlayerContext context = contexts[i];
                if (context.Number == loser.Number)
                {
                    continue;
                }

                Fighter fighter = GetFighter(context);
                if (fighter.Health > best)
                {
                    best = fighter.Health;
                    _winner = context.Number;
                }
            }

            _roundOverFrames = RoundOverFrames;
            PlayVictoryImpact();

            // Nobody can be knocked out while the winner is on screen, so a
            // second player running out of health in the same breath does not
            // overwrite the result.
            for (int i = 0; i < contexts.Count; i++)
            {
                GetFighter(contexts[i]).Invulnerable = RoundOverFrames;
            }
        }

        private static void StartNextRound(List<PlayerContext> contexts)
        {
            _winner = 0;
            for (int i = 0; i < contexts.Count; i++)
            {
                Fighter fighter = GetFighter(contexts[i]);
                fighter.Health = MaxHealth;
                fighter.Invulnerable = 0;
            }
        }

        /// <summary>
        /// The wall bump, which is already what this is: the player hit
        /// something solid and traded speed with it. Keeping it distinct from
        /// the splat used for damage means a round can be read by ear.
        /// </summary>
        private static void PlayCollisionSound()
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            try
            {
                Game1.instance.contentManager.audio.player.Bump.PlayOneShot();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The blow that ends the round, on the frame the banner is at its
        /// largest. The title screen's own hit is the heaviest single sound the
        /// game ships and it is not used during play, so it carries weight
        /// without being confused for anything else.
        /// </summary>
        private static void PlayVictoryImpact()
        {
            PlaySound(SoundKind.Impact);
        }

        /// <summary>
        /// The flourish, once the banner has settled. A separate sound from the
        /// impact rather than a second copy of it: <c>JKSound</c> holds one
        /// instance per sound, so replaying the same one cuts its own tail off
        /// instead of building on it.
        /// </summary>
        private static void PlayVictoryFanfare()
        {
            PlaySound(SoundKind.Fanfare);
        }

        private enum SoundKind
        {
            Impact,
            Fanfare
        }

        private static void PlaySound(SoundKind kind)
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            try
            {
                JKContentManager.Audio audio = Game1.instance.contentManager.audio;
                if (kind == SoundKind.Impact)
                {
                    audio.menu.TitleHit.PlayOneShot();
                }
                else
                {
                    audio.babe.Scream.PlayOneShot();
                }
            }
            catch (Exception)
            {
                // A missing sound is not worth interrupting a round for.
            }
        }

        private static void PlayHitSound()
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            try
            {
                // The splat sound is the game's own impact, so a stomp lands
                // with a noise the player already reads as "that hurt".
                Game1.instance.contentManager.audio.player.Splat.PlayOneShot();
            }
            catch (Exception)
            {
                // A missing sound is not worth interrupting a round for.
            }
        }

        /// <summary>
        /// Draws one player's gauge, called from a postfix on that player's own
        /// Draw so it inherits the transform the sprite was just drawn with. In
        /// split screen every player is drawn in every view, so each view shows
        /// its opponent's health too, and a player outside the view falls
        /// outside it here as well.
        /// </summary>
        internal static void DrawGauge(PlayerEntity player)
        {
            if (!IsEnabled || player == null)
            {
                return;
            }

            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null || context.Body == null)
            {
                return;
            }

            Texture2D pixel = GetPixel();
            if (pixel == null)
            {
                return;
            }

            Fighter fighter = GetFighter(context);
            Rectangle hitbox = context.Body.GetHitbox();
            Vector2 anchor = Camera.TransformVector2(
                new Vector2(hitbox.Center.X, hitbox.Top)
            );

            int left = (int)anchor.X - GaugeWidth / 2;
            int top = (int)anchor.Y - GaugeGap - GaugeHeight;

            // Outline first, then the track, so the bar reads at one pixel per
            // four health without the fill bleeding into the border.
            Fill(pixel, left - 1, top - 1, GaugeWidth + 2, GaugeHeight + 2,
                new Color(0, 0, 0, 200));
            Fill(pixel, left, top, GaugeWidth, GaugeHeight,
                new Color(110, 24, 24));

            int filled = fighter.Health * GaugeWidth / MaxHealth;
            if (filled > 0)
            {
                Fill(pixel, left, top, filled, GaugeHeight,
                    HealthColor(fighter.Health));
            }
        }

        /// <summary>
        /// Green while healthy, amber past the halfway mark, red once a single
        /// stomp would finish it. The last band is the one that matters: it says
        /// "this player can be closed out now" without needing a number.
        /// </summary>
        private static Color HealthColor(int health)
        {
            if (health <= StompDamage)
            {
                return new Color(226, 62, 52);
            }

            if (health * 2 <= MaxHealth)
            {
                return new Color(232, 196, 54);
            }

            return new Color(74, 200, 82);
        }

        /// <summary>
        /// The round result, drawn once over the composited views rather than
        /// per view, because it is about the match and not about anyone's
        /// camera.
        /// </summary>
        internal static void DrawRoundResult()
        {
            if (!IsEnabled || _roundOverFrames <= 0 || _winner <= 0)
            {
                return;
            }

            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            JKContentManager.Font fonts = Game1.instance.contentManager.font;
            SpriteFont font = fonts.StyleFont ?? fonts.MenuFont;
            if (font == null)
            {
                return;
            }

            int elapsed = RoundOverFrames - _roundOverFrames;

            // Overshoot and settle. A result that simply appears reads as a
            // label; one that lands reads as the end of a fight.
            float punch = 1f;
            if (elapsed < PunchFrames)
            {
                float remaining = 1f - (float)elapsed / PunchFrames;
                punch = 1f + 0.7f * remaining * remaining;
            }

            float scale = BannerScale * punch;
            string text = "PLAYER " + _winner + " WINS";
            Vector2 size = font.MeasureString(text);
            Vector2 origin = size / 2f;
            Vector2 centre = new Vector2(
                JumpGame.GAME_RECT.Width / 2f,
                JumpGame.GAME_RECT.Height / 2f
            );

            DrawBanner(centre, size.Y * scale);

            // Outline by redrawing around the text, which stays crisp at any
            // scale where a single offset shadow would just look blurred.
            for (int i = 0; i < OutlineOffsets.Length; i++)
            {
                DrawCentred(
                    font,
                    text,
                    centre + OutlineOffsets[i],
                    origin,
                    scale,
                    Color.Black
                );
            }

            DrawCentred(font, text, centre, origin, scale, WinnerColor(_winner));
        }

        private static readonly Vector2[] OutlineOffsets =
        {
            new Vector2(-2f, 0f), new Vector2(2f, 0f),
            new Vector2(0f, -2f), new Vector2(0f, 2f),
            new Vector2(-2f, -2f), new Vector2(2f, -2f),
            new Vector2(-2f, 2f), new Vector2(2f, 2f)
        };

        private static void DrawCentred(
            SpriteFont font,
            string text,
            Vector2 position,
            Vector2 origin,
            float scale,
            Color color
        )
        {
            Game1.spriteBatch.DrawString(
                font,
                text,
                position,
                color,
                0f,
                origin,
                scale,
                SpriteEffects.None,
                0f
            );
        }

        /// <summary>
        /// A dimmed band across the screen so the text is readable over whatever
        /// the level happens to be, with a bright rule at each edge to stop it
        /// looking like a rendering fault.
        /// </summary>
        private static void DrawBanner(Vector2 centre, float textHeight)
        {
            Texture2D pixel = GetPixel();
            if (pixel == null)
            {
                return;
            }

            int height = (int)textHeight + 20;
            int top = (int)(centre.Y - height / 2f);
            int width = JumpGame.GAME_RECT.Width;

            Fill(pixel, 0, top, width, height, new Color(0, 0, 0, 170));
            Fill(pixel, 0, top, width, 2, new Color(255, 255, 255, 90));
            Fill(pixel, 0, top + height - 2, width, 2, new Color(255, 255, 255, 90));
        }

        /// <summary>
        /// One colour per player, so a four-way match says who won without
        /// anyone reading the number.
        /// </summary>
        private static Color WinnerColor(int playerNumber)
        {
            switch (playerNumber)
            {
                case 2:
                    return new Color(120, 190, 255);
                case 3:
                    return new Color(140, 230, 140);
                case 4:
                    return new Color(255, 190, 120);
                default:
                    return new Color(255, 236, 140);
            }
        }

        private static void Fill(
            Texture2D pixel,
            int x,
            int y,
            int width,
            int height,
            Color color
        )
        {
            Game1.spriteBatch.Draw(
                pixel,
                new Rectangle(x, y, width, height),
                color
            );
        }

        private static Texture2D GetPixel()
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return null;
            }

            PixelTexture pixel = Game1.instance.contentManager.Pixel;
            return pixel == null ? null : pixel.texture;
        }
    }

    /// <summary>
    /// Resolves stomps once per frame, after every player has moved.
    /// </summary>
    internal static class BattleUpdatePatch
    {
        public static void Postfix()
        {
            BattleMode.Update();
        }
    }

    /// <summary>
    /// Hangs the gauge off the player's own draw. A postfix rather than a
    /// prefix so the bar sits over the sprite that was just drawn.
    /// </summary>
    internal static class BattlePlayerDrawPatch
    {
        public static void Postfix(PlayerEntity __instance)
        {
            BattleMode.DrawGauge(__instance);
        }
    }

    /// <summary>
    /// Fall damage. <c>FailState.Start</c> is the moment the base game commits
    /// to a splat, which it only does for a landing at terminal velocity, so
    /// hooking it here keeps the damage and the animation in agreement.
    /// </summary>
    internal static class BattleSplatPatch
    {
        public static void Postfix(FailState __instance)
        {
            if (__instance == null)
            {
                return;
            }

            BattleMode.OnSplat(__instance.player);
        }
    }
}
