using System;
using System.Collections.Generic;
using JumpKing;
using JumpKing.Player;
using JumpKing.SaveThread;
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

        /// <summary>
        /// How many maximum-speed hits win a round.
        ///
        /// This is the one number in the combat model that is a design decision
        /// rather than a consequence of the game's own physics. Everything else
        /// below is derived from constants the base game already defines, so
        /// there is nothing to re-tune when one of them changes.
        /// </summary>
        private const int CleanHitsPerRound = 5;

        /// <summary>
        /// The lift a walking shove gives, in jump charge frames.
        ///
        /// The one bias in the collision model, and an unavoidable one:
        /// <c>Walk</c> rewrites <c>Velocity.X</c> every frame a player is
        /// standing, so a shove that leaves them on the ground is erased before
        /// it moves anyone. Something has to lift them, and no law in the game
        /// says by how much. The lift scales with the impulse actually
        /// delivered; this only fixes where that scale is anchored.
        /// </summary>
        private const float WalkShoveChargeFrames = 2f;

        /// <summary>Charge frames in a full jump.</summary>
        private static readonly float FullChargeFrames =
            PlayerValues.JUMP_TIME * PlayerValues.FPS;

        /// <summary>
        /// How much of the victim's box counts as the head.
        ///
        /// Exactly one frame of terminal velocity: on the first frame two boxes
        /// overlap, an attacker who was above can have descended at most
        /// <c>MAX_FALL</c>, so this is the smallest band that never misses a
        /// stomp and never mistakes a level approach for one.
        /// </summary>
        private static readonly int HeadBand = (int)PlayerValues.MAX_FALL;

        /// <summary>
        /// Frames the victim cannot be hit again: the time the game itself
        /// gives a splatted player to get up.
        ///
        /// These being out of step was what let a single landed stomp chain
        /// into a whole round - the victim was still locked in their splat when
        /// they became hittable again.
        /// </summary>
        private static readonly int HitInvulnerabilityFrames =
            (int)Math.Round(PlayerValues.SPLAT_TIME * PlayerValues.FPS);

        /// <summary>
        /// The closing speed a contact needs: what a walking player brings.
        /// Below that they are drifting into someone, not hitting them.
        /// </summary>
        private static readonly float MinimumApproachSpeed =
            PlayerValues.WALK_SPEED;

        /// <summary>
        /// The impulse a walking player delivers to someone standing still.
        /// The reference the lift is calibrated against, derived rather than
        /// measured so it follows the restitution and walk speed.
        /// </summary>
        private static readonly float WalkShoveImpulse =
            ((1f + PlayerValues.BOUNCE) / 2f) * PlayerValues.WALK_SPEED;

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


        /// <summary>The head band, shared with props so a stomp means one thing.</summary>
        internal static int HeadBandSize { get { return HeadBand; } }

        /// <summary>The rebound for landing on something, shared with props.</summary>
        internal static float ReboundOff(float impactSpeed)
        {
            return Bounce(impactSpeed);
        }

        internal static Texture2D Pixel { get { return GetPixel(); } }

        /// <summary>Current health, so a prop can tell whether its hit landed.</summary>
        internal static int HealthOf(PlayerContext context)
        {
            Fighter fighter = GetFighter(context);
            return fighter == null ? 0 : fighter.Health;
        }

        /// <summary>
        /// Restores health, capped at full. Returns false when there was
        /// nothing to restore, so a pickup can decline to be consumed.
        /// </summary>
        internal static bool Heal(PlayerContext context, int amount)
        {
            Fighter fighter = GetFighter(context);
            if (fighter == null || fighter.Health >= MaxHealth || amount <= 0)
            {
                return false;
            }

            fighter.Health = Math.Min(MaxHealth, fighter.Health + amount);
            return true;
        }

        /// <summary>
        /// Damage from something that is not another player. Goes through the
        /// same invulnerability window, so a hazard cannot drain someone who is
        /// already down and cannot stack with a stomp in the same instant.
        /// </summary>
        /// <summary>
        /// The launch a hit of a given size throws you with.
        ///
        /// Read straight back out of the damage law: a hit worth this much
        /// damage carries the impact speed that would have caused it, so the
        /// same number decides how much it hurts and how far it throws you.
        /// Expressed as a jump charge, because that is the only launch the game
        /// itself produces.
        ///
        /// Nothing erases it. Both <c>Walk</c> and <c>FailState</c> sit behind
        /// the player tree's <c>IsOnGround</c> guard, so neither runs while the
        /// player is in the air - the horizontal survives the whole flight even
        /// though they are flattened for it.
        /// </summary>
        internal static void Launch(PlayerContext context, int amount, float awayFromX)
        {
            if (context == null || context.Body == null)
            {
                return;
            }

            float scale = (float)MaxHealth / CleanHitsPerRound;
            float intensity = (float)Math.Sqrt(
                Math.Min(1f, Math.Max(0f, amount / scale))
            );

            int direction =
                Math.Sign(context.Body.GetHitbox().Center.X - awayFromX);
            if (direction == 0)
            {
                direction = 1;
            }

            context.Body.Velocity.Y = PlayerValues.JUMP * intensity;
            context.Body.Velocity.X = direction * PlayerValues.SPEED * intensity;
        }

        internal static void Hurt(PlayerContext context, int amount)
        {
            Fighter fighter = GetFighter(context);
            if (fighter == null || fighter.Invulnerable > 0 ||
                amount <= 0 || _roundOverFrames > 0)
            {
                return;
            }

            fighter.Health -= amount;
            fighter.Invulnerable = HitInvulnerabilityFrames;
            ForceSplat(context.Player);
            PlayHitSound();

            if (fighter.Health <= 0)
            {
                fighter.Health = 0;
                EndRound(context, MultiplayerRuntime.GetActiveContexts());
            }
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
            BattleProps.Reset();
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
            BattleProps.Update(contexts);
        }

        /// <summary>
        /// Props are world objects, so they draw in the per-view world pass
        /// alongside the players rather than with the screen-space UI.
        /// </summary>
        internal static void DrawWorldProps()
        {
            if (!IsEnabled)
            {
                return;
            }

            BattleProps.Draw();
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
        /// Whether a player is in the game's own splat: flattened, and held
        /// there until they press something.
        /// </summary>
        private static bool IsDowned(PlayerContext context)
        {
            if (context == null || context.Player == null ||
                FailStateField == null)
            {
                return false;
            }

            try
            {
                var failState = FailStateField.GetValue(context.Player)
                    as FailState;
                return failState != null && failState.IsRunning();
            }
            catch (Exception)
            {
                return false;
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
            float impact = attacker.Body.Velocity.Y;
            Fighter victimFighter = GetFighter(victim);
            int damage = ImpactDamage(impact);

            victimFighter.Health -= damage;
            victimFighter.Invulnerable = HitInvulnerabilityFrames;

            // A player already flattened absorbs the landing instead of
            // returning it - there is no spring left in a limp body. So hitting
            // someone who is already down gives no height back, and every
            // follow-up has to be set up from the ground again. That is what
            // keeps standing over a fallen opponent from being worth much,
            // without taking the exchange away: the picture stays, the rate
            // does not.
            float rebound = IsDowned(victim) ? 0f : Bounce(impact);
            attacker.Body.Velocity.Y = 0f - rebound;
            ForceSplat(victim.Player);
            PlayHitSound();

            if (victimFighter.Health <= 0)
            {
                victimFighter.Health = 0;
                EndRound(victim, contexts);
            }
        }

        /// <summary>
        /// How hard the attacker comes back off a head: the same restitution
        /// the game gives a wall. A head is something solid you hit, and it
        /// returns the same share of your speed.
        ///
        /// No ceiling is needed. Terminal velocity is the fastest anyone can
        /// arrive, and half of it is the rebound - the cap that used to sit
        /// here was that number written down twice.
        ///
        /// This also decides how many hits a bounce chain lands, together with
        /// the recovery window. Off a full-speed dive the first rebound stays
        /// airborne longer than the victim's splat lasts, so a second stomp
        /// connects; the one after that does not, and the chain ends at two.
        /// </summary>
        private static float Bounce(float impactSpeed)
        {
            return impactSpeed * PlayerValues.BOUNCE;
        }

        /// <summary>
        /// Damage is the energy of the impact - speed squared, as a fraction of
        /// terminal velocity.
        ///
        /// One law covers both ways a player loses health. A stomp hands the
        /// impact to whoever is underneath; a splat is the same impact absorbed
        /// by the player who made it. A full-speed landing therefore costs
        /// exactly what a full-speed stomp deals, which is what makes climbing
        /// worth it and falling hurt.
        ///
        /// Squared rather than linear because that is what an impact actually
        /// carries, and because it settles the balance question by itself:
        /// stepping off a ledge onto someone is worth almost nothing, so
        /// trading pokes on level ground is never a way to win a round.
        /// </summary>
        private static int ImpactDamage(float impactSpeed)
        {
            float ratio = Math.Abs(impactSpeed) / PlayerValues.MAX_FALL;
            if (ratio > 1f)
            {
                ratio = 1f;
            }

            float scale = (float)MaxHealth / CleanHitsPerRound;
            int damage = (int)Math.Round(scale * ratio * ratio);
            return damage < 1 ? 1 : damage;
        }

        /// <summary>
        /// How long a lift keeps someone off the ground, from the game's own
        /// gravity. Used as the window before the same pair can trade another
        /// shove: you can be shoved again once you have your feet back.
        /// </summary>
        private static int FlightFrames(float upwardVelocity)
        {
            return (int)Math.Ceiling(
                2f * Math.Abs(upwardVelocity) / PlayerValues.GRAVITY
            );
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

            // The same restitution the game gives a wall. One player hitting
            // another is the same kind of event as hitting terrain, so it keeps
            // the same share of the speed that went into it.
            float keep = (1f - PlayerValues.BOUNCE) / 2f;
            float give = (1f + PlayerValues.BOUNCE) / 2f;

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

            PlayCollisionSound();
        }

        private static void Apply(
            PlayerContext context,
            Fighter fighter,
            float velocityX
        )
        {
            float velocity = Math.Max(
                0f - PlayerValues.SPEED,
                Math.Min(PlayerValues.SPEED, velocityX)
            );

            // How much speed this player actually gained or lost in the
            // collision, which is the impulse the other one delivered.
            float impulse = Math.Abs(velocity - fighter.StartVelocityX);
            context.Body.Velocity.X = velocity;

            if (fighter.WasAirborne)
            {
                // Already off the ground, so the horizontal survives on its own
                // and there is nothing to lift.
                return;
            }

            // Standing players have to leave the ground for any of this to
            // survive: Walk rewrites Velocity.X every frame a player is on the
            // floor. The lift is charged as a jump, because that is the only
            // vertical motion the game itself produces, and scales with the
            // impulse - a running jump into someone knocks them further off
            // their feet than a walk does.
            float chargeFrames =
                WalkShoveChargeFrames * (impulse / WalkShoveImpulse);
            float lift = PlayerValues.JUMP * (chargeFrames / FullChargeFrames);
            context.Body.Velocity.Y = lift;
            fighter.PushCooldown = FlightFrames(lift);
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
            if (context == null || _roundOverFrames > 0 || context.Body == null)
            {
                return;
            }


            // Charged on the speed actually landed at, not on the assumption
            // that a splat is always terminal velocity.
            //
            // It usually is: FailState only starts when LastVelocity.Y equals
            // MAX_FALL exactly. But the node sits behind the player tree's
            // IsOnGround guard, so a splatted player who leaves the ground has
            // it suspended mid-run, and ResumeRun calls Start again on landing
            // without re-checking the speed. Assuming terminal velocity there
            // turned a half-pixel nudge into a full-speed fall, which is what
            // let a downed player be walked to death - a free twenty every time
            // they were shoved.
            //
            // Reading the real speed makes that same nudge worth the one point
            // an impact that small is worth, and leaves a genuine fall at the
            // twenty it always was. One rule, no special case.
            Fighter fighter = GetFighter(context);
            int fallDamage = ImpactDamage(context.Body.LastVelocity.Y);

            fighter.Health -= fallDamage;
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

        /// <summary>
        /// Both players go back to the level's own spawn point, not just back
        /// to full health. Otherwise the winner keeps whatever height won them
        /// the round, and starts the next one already standing over the loser -
        /// the fight would only ever go one way after the first hit.
        /// </summary>
        private static void StartNextRound(List<PlayerContext> contexts)
        {
            _winner = 0;
            BattleProps.ResetAll();
            SaveState spawn = SaveState.GetDefault();

            for (int i = 0; i < contexts.Count; i++)
            {
                PlayerContext context = contexts[i];
                Fighter fighter = GetFighter(context);
                fighter.Health = MaxHealth;
                fighter.Invulnerable = 0;
                fighter.LastY = float.NaN;
                fighter.Descended = false;

                if (context.Body == null)
                {
                    continue;
                }

                // Each player goes back to their own start, the same one the
                // level placed them at. Sending everyone to player 1's spawn
                // stacked the whole field on one side after every round.
                Vector2 position;
                Vector2 velocity;
                if (!MultiplayerStartPositions.TryGet(
                        context.Number, out position, out velocity))
                {
                    position = spawn.position;
                    velocity = spawn.velocity;
                }

                context.Body.Position = position;
                context.Body.Velocity = velocity;
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

        /// <summary>
        /// The same click the ring and boots make when they go on. Picking
        /// something up and equipping something are the same kind of event to
        /// a player, so they sound alike.
        /// </summary>
        internal static void PlayPickupSound()
        {
            if (Game1.instance == null || Game1.instance.contentManager == null)
            {
                return;
            }

            try
            {
                Game1.instance.contentManager.audio.menu.OnItemToggle();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The game's own impact, shared with props so a hit sounds like a hit.</summary>
        internal static void PlaySplatSound()
        {
            PlayHitSound();
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
            if (health <= ImpactDamage(PlayerValues.MAX_FALL))
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

        internal static void Fill(
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
