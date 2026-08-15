using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EntityComponent;
using HarmonyLib;
using JumpKing;
using JumpKing.GameManager.MultiEnding;
using JumpKing.Level;
using JumpKing.MiscEntities.WorldItems;
using JumpKing.Player;
using JumpKing.Util.Tags;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Owns the player contexts and the per-player lifecycle.
    ///
    /// Player 1 is not privileged here: it gets a context like everybody else, so
    /// the scope contract consumer mods see is the same regardless of which player
    /// is being processed.
    /// </summary>
    internal static class MultiplayerRuntime
    {
        private const int MaximumPlayers = 4;

        private static readonly PlayerContext[] Contexts =
            new PlayerContext[MaximumPlayers];
        private static bool _levelStarted;
        private static bool _raceComplete;

        public static bool IsActive
        {
            get
            {
                if (ModEntry.PlayerCount <= 1 || !_levelStarted || _raceComplete)
                {
                    return false;
                }

                for (int number = 1; number <= ModEntry.PlayerCount; number++)
                {
                    PlayerContext context = GetContext(number);
                    if (context == null || !context.IsAlive)
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

        public static PlayerContext GetContext(int playerNumber)
        {
            if (playerNumber < 1 || playerNumber > MaximumPlayers)
            {
                return null;
            }

            PlayerContext context = Contexts[playerNumber - 1];
            if (playerNumber == 1 && (context == null || !context.IsAlive))
            {
                context = CreatePrimaryContext();
            }

            return context;
        }

        public static PlayerEntity GetPlayer(int playerNumber)
        {
            PlayerContext context = GetContext(playerNumber);
            return context == null ? null : context.Player;
        }

        public static PlayerContext GetContext(PlayerEntity player)
        {
            if (player == null)
            {
                return null;
            }

            for (int i = 0; i < Contexts.Length; i++)
            {
                if (Contexts[i] != null &&
                    ReferenceEquals(Contexts[i].Player, player))
                {
                    return Contexts[i];
                }
            }

            return null;
        }

        public static int GetPlayerNumber(PlayerEntity player)
        {
            PlayerContext context = GetContext(player);
            if (context != null)
            {
                return context.Number;
            }

            // The primary context may not be built yet during early frames.
            return ReferenceEquals(player, GetPlayer(1)) ? 1 : 0;
        }

        public static int GetPlayerNumber(InputComponent input)
        {
            return input == null || input.gameObject == null ? 0 :
                GetPlayerNumber(input.gameObject as PlayerEntity);
        }

        public static bool IsManagedPlayer(Entity entity)
        {
            var player = entity as PlayerEntity;
            return player != null && GetContext(player) != null;
        }

        /// <summary>
        /// Called just before the base ModLoader dispatches <c>[OnLevelStart]</c>.
        /// Every player must exist by then, because block mods look up "the player"
        /// inside that hook and register their behaviours on its body.
        /// </summary>
        public static void BeforeModLevelStart()
        {
            _levelStarted = true;
            _raceComplete = false;
            MultiplayerStartPositions.Reset();
            Contexts[0] = CreatePrimaryContext();

            if (ModEntry.IsMultiplayerEnabled)
            {
                StartAdditionalPlayers(ModEntry.PlayerCount);
            }
        }

        /// <summary>
        /// Called right after the base dispatch, to give every additional player
        /// the same hook the first player just received.
        /// </summary>
        public static void AfterModLevelStart()
        {
            ReplayForAdditionalPlayers();
        }

        public static void OnLevelStart()
        {
            // Safety net: if the ModLoader patch did not apply, still build the
            // contexts so the rest of the mod degrades to something coherent.
            if (Contexts[0] == null || !Contexts[0].IsAlive)
            {
                BeforeModLevelStart();
            }

            BattleMode.OnLevelStart();
        }

        public static void OnLevelEnd()
        {
            _levelStarted = false;
            StopAdditionalPlayers();
            Contexts[0] = null;
        }

        public static void SetPlayerCount(int playerCount)
        {
            if (playerCount <= 1)
            {
                StopAdditionalPlayers();
                return;
            }

            _raceComplete = false;
            if (!_levelStarted)
            {
                return;
            }

            StartAdditionalPlayers(playerCount);
            // Mid-run change: the mod hooks already ran for the players that
            // existed then, so the new ones need their own pass.
            ReplayForAdditionalPlayers();
        }

        public static void FinishRace()
        {
            _raceComplete = true;
            StopAdditionalPlayers();
        }

        /// <summary>
        /// Snapshot of the live contexts, player 1 first, used by the level-start
        /// replay and the split renderer.
        /// </summary>
        public static List<PlayerContext> GetActiveContexts()
        {
            var result = new List<PlayerContext>(MaximumPlayers);
            for (int number = 1; number <= ModEntry.PlayerCount; number++)
            {
                PlayerContext context = GetContext(number);
                if (context != null && context.IsAlive)
                {
                    result.Add(context);
                }
            }

            return result;
        }

        private static void ReplayForAdditionalPlayers()
        {
            if (!ModEntry.IsMultiplayerEnabled)
            {
                return;
            }

            List<PlayerContext> contexts = GetActiveContexts();
            var pending = new List<PlayerContext>(contexts.Count);
            for (int i = 0; i < contexts.Count; i++)
            {
                PlayerContext context = contexts[i];
                if (context.IsPrimary || context.LevelStartReplayed)
                {
                    continue;
                }

                context.LevelStartReplayed = true;
                pending.Add(context);
            }

            PlayerSetup.Run(pending);
        }

        private static PlayerContext CreatePrimaryContext()
        {
            PlayerContext existing = Contexts[0];
            if (existing != null && existing.IsAlive)
            {
                return existing;
            }

            if (EntityManager.instance == null)
            {
                return null;
            }

            // Find must see the real entity order here: while a level-start replay
            // is in flight the lookup is redirected to the scoped player, and
            // adopting that as player 1 would corrupt the whole context table.
            bool redirect = PlayerScope.RedirectPrimaryPlayer;
            PlayerScope.RedirectPrimaryPlayer = false;
            PlayerEntity player;
            try
            {
                player = EntityManager.instance.Find<PlayerEntity>();
            }
            finally
            {
                PlayerScope.RedirectPrimaryPlayer = redirect;
            }

            if (player == null)
            {
                return null;
            }

            var context = new PlayerContext(1, player);
            Contexts[0] = context;
            ItemToggles.Seed(context);
            return context;
        }

        private static void StartAdditionalPlayers(int playerCount)
        {
            PlayerContext primary = GetContext(1);
            if (EntityManager.instance == null || primary == null ||
                primary.Body == null)
            {
                return;
            }

            TrimAdditionalPlayers(playerCount);
            ItemToggles.SeedIfUnset(primary);

            using (PlayerScope.PreserveGlobalCamera())
            {
                CreateAdditionalPlayers(playerCount, primary);
            }
        }

        private static void CreateAdditionalPlayers(
            int playerCount,
            PlayerContext primary
        )
        {
            for (int number = 2; number <= playerCount; number++)
            {
                int index = number - 1;
                if (Contexts[index] != null && Contexts[index].IsAlive)
                {
                    continue;
                }

                try
                {
                    var player = new PlayerEntity();
                    var context = new PlayerContext(number, player);
                    Contexts[index] = context;
                    ItemToggles.Seed(context);
                    PlayerSpriteFactory.Prepare(number);

                    Vector2 spawnPosition;
                    Vector2 spawnVelocity;
                    bool hasOwnSpawn = MultiplayerStartPositions.TryGet(
                        number,
                        out spawnPosition,
                        out spawnVelocity
                    );

                    if (context.Body != null)
                    {
                        context.Body.Position =
                            hasOwnSpawn ? spawnPosition : primary.Body.Position;
                        context.Body.Velocity =
                            hasOwnSpawn ? spawnVelocity : Vector2.Zero;
                    }

                    // The camera now lives in the context and is driven by this
                    // player's own CameraFollowComp inside its scope, so the
                    // component stays enabled - unlike the previous design, which
                    // disabled it and recomputed the screen in two separate places.
                    //
                    // A map-defined spawn gets its own screen computed the same
                    // way the base game's teleport handler does, rather than
                    // inheriting player 1's - the two can be on different screens
                    // entirely. Seeding it here means PlayerScope.Enter's own
                    // seed-from-the-live-camera path, which is only correct when
                    // this player starts wherever player 1's camera already is,
                    // never runs for this player.
                    if (hasOwnSpawn)
                    {
                        context.Screen = -(int)Math.Floor(spawnPosition.Y / 360f);
                        context.Offset = Vector2.Zero;
                        context.CameraSeeded = true;
                    }
                    else
                    {
                        context.Screen = primary.Screen;
                        context.Offset = primary.Offset;
                        context.CameraSeeded = primary.CameraSeeded;
                    }

                    context.SaveState = SaveLubeAccess.GetPlayerPosition();
                }
                catch (Exception ex)
                {
                    Contexts[index] = null;
                    JumpKing.Program.crashLog.AddErrorMessage(
                        "Local Multiplayer player " + number + " start failed: " +
                        ex.Message
                    );
                }
            }
        }

        private static void StopAdditionalPlayers()
        {
            MultiplayerSplitRenderer.Release();

            for (int i = 1; i < Contexts.Length; i++)
            {
                DestroyContext(i);
            }

            // Back to one player: give the items to the base game global again.
            ItemToggles.ClearOverrides(Contexts[0]);
            PlayerSpriteFactory.Release();
        }

        private static void TrimAdditionalPlayers(int playerCount)
        {
            for (int number = Math.Max(2, playerCount + 1);
                number <= MaximumPlayers;
                number++)
            {
                DestroyContext(number - 1);
            }

            MultiplayerSplitRenderer.Release();
        }

        private static void DestroyContext(int index)
        {
            PlayerContext context = Contexts[index];
            if (context != null && context.IsAlive)
            {
                context.Player.Destroy();
            }

            Contexts[index] = null;
        }
    }

    /// <summary>
    /// Wraps every player's component update in its own scope.
    ///
    /// This is the load-bearing patch: <c>Camera.CurrentScreen</c> is what
    /// <c>LevelManager.CheckCollision</c>, <c>GetCollisionInfo</c> and
    /// <c>IsInWater</c> resolve against, so without the right camera installed a
    /// player collides against another player's screen.
    /// </summary>
    internal static class PlayerUpdateScopePatch
    {
        public static void Prefix(Entity __instance, out PlayerScope.Scope __state)
        {
            __state = default(PlayerScope.Scope);

            // Single player must be left exactly as the base game runs it. A
            // scope would install the context's camera at the start of every
            // player update, reverting anything that moved the camera since the
            // last one - screen shake, weather, another mod - and there is
            // nothing to gain from it when there is only one player to resolve.
            if (!ModEntry.IsMultiplayerEnabled)
            {
                return;
            }

            var player = __instance as PlayerEntity;
            PlayerContext context = player == null ? null :
                MultiplayerRuntime.GetContext(player);
            if (context != null)
            {
                __state = PlayerScope.Enter(context);
            }
        }

        public static void Postfix(PlayerScope.Scope __state)
        {
            __state.Dispose();
        }
    }

    /// <summary>
    /// Additional players have no physical controller. The original is skipped so
    /// the local pad does not drive all of them at once; postfixes from input mods
    /// still run and fill in that player's remote input.
    /// </summary>
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

            PlayerContext primary = MultiplayerRuntime.GetContext(1);
            if (primary == null || primary.Body == null)
            {
                return;
            }

            for (int number = 2;
                number <= MultiplayerRuntime.PlayerCount;
                number++)
            {
                PlayerContext winner = MultiplayerRuntime.GetContext(number);
                if (winner == null || !winner.IsAlive || winner.Body == null)
                {
                    continue;
                }

                // Every ending opens with "if (Camera.CurrentScreen !=
                // ENDING_SCREEN0) return false", so the question has to be asked
                // with that player's camera installed. Asked from outside a
                // scope it is really being asked about player 1's screen, and an
                // additional player standing on the ending screen never wins.
                IEnding ending = null;
                using (PlayerScope.Enter(winner, false, false))
                {
                    for (int i = 0; i < ___m_endings.Count; i++)
                    {
                        if (___m_endings[i].CheckWin(winner.Player))
                        {
                            ending = ___m_endings[i];
                            break;
                        }
                    }
                }

                if (ending == null)
                {
                    continue;
                }

                // The engine can only end the run for the player it knows about,
                // so player 1 takes the winner's place. Its camera has to move
                // too: moving the body alone leaves player 1 on its own screen,
                // and the same first check rejects the handover.
                Vector2 savedPosition = primary.Body.Position;
                Vector2 savedVelocity = primary.Body.Velocity;
                int savedScreen = primary.Screen;
                Vector2 savedOffset = primary.Offset;

                primary.Body.Position = winner.Body.Position;
                primary.Body.Velocity = winner.Body.Velocity;
                primary.Screen = winner.Screen;
                primary.Offset = winner.Offset;

                bool accepted;
                using (PlayerScope.Enter(primary, false, false))
                {
                    accepted = ending.CheckWin(primary.Player);
                }

                if (accepted)
                {
                    __0 = ending;
                    __result = true;
                    MultiplayerRuntime.FinishRace();
                    PlayerScope.SetGlobalCamera(winner.Screen, winner.Offset);
                }
                else
                {
                    primary.Body.Position = savedPosition;
                    primary.Body.Velocity = savedVelocity;
                    primary.Screen = savedScreen;
                    primary.Offset = savedOffset;
                }

                return;
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

        private static readonly RenderTarget2D[] PlayerTargets =
            new RenderTarget2D[4];
        private static readonly int[] ViewTargetIndexes = new int[4];
        private static readonly PlayerContext[] ViewContexts = new PlayerContext[4];
        private static bool _drawingPass;

        /// <summary>
        /// True during the single full-screen pass that draws the screen-space UI
        /// over the composited views. The world half of <c>JumpGame.Draw</c> is
        /// suppressed while it is set.
        /// </summary>
        public static bool IsScreenSpacePass { get; private set; }

        public static bool PrefixDraw(JumpGame game)
        {
            if (_drawingPass || !MultiplayerRuntime.IsActive)
            {
                return true;
            }

            Game1 host = Game1.instance;
            int playerCount = MultiplayerRuntime.PlayerCount;
            if (host == null || !PlayerScope.IsAvailable ||
                (playerCount != 2 && playerCount != 4))
            {
                return true;
            }

            for (int i = 0; i < playerCount; i++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(i + 1);
                if (context == null || !context.IsAlive)
                {
                    return true;
                }

                ViewContexts[i] = context;
            }

            if (playerCount == 2 &&
                ModEntry.TwoPlayerLayout == TwoPlayerLayout.Shared)
            {
                // One camera, so there is nothing to composite - the render
                // targets are skipped entirely rather than drawn and blitted at
                // full size for no gain.
                DrawSharedView(game);
                return false;
            }

            GraphicsDevice graphics = host.GraphicsDevice;
            EnsureTargets(graphics, playerCount);

            RenderTargetBinding[] previousTargets = graphics.GetRenderTargets();

            host.EndBatch();

            try
            {
                for (int i = 0; i < playerCount; i++)
                {
                    ViewTargetIndexes[i] = FindMatchingView(i);
                }

                for (int i = 0; i < playerCount; i++)
                {
                    if (ViewTargetIndexes[i] != i)
                    {
                        continue;
                    }

                    DrawView(
                        host,
                        graphics,
                        PlayerTargets[i],
                        ViewContexts[i],
                        GetViewPlayerMask(i, playerCount)
                    );
                }
            }
            finally
            {
                _drawingPass = false;
                LocalMultiplayerApi.SetCurrentViewPlayerMask(1);
                RestoreTargets(graphics, previousTargets);
                host.StartBatch();
            }

            if (playerCount == 2)
            {
                switch (ModEntry.TwoPlayerLayout)
                {
                    case TwoPlayerLayout.Compact:
                        DrawCompactTwoPlayerViews();
                        break;
                    case TwoPlayerLayout.Stacked:
                        DrawStackedTwoPlayerViews();
                        break;
                    default:
                        DrawTwoPlayerViews();
                        break;
                }
            }
            else
            {
                DrawFourPlayerViews();
            }

            DrawScreenSpaceUi(game);

            // The round result belongs to the match, not to a camera, so it is
            // drawn once over the composited views like the rest of the UI.
            BattleMode.DrawRoundResult();
            return false;
        }

        /// <summary>
        /// The whole screen on player 1's camera, with everyone drawn into it.
        ///
        /// The batch Game1 opened is still current here - unlike the split path
        /// there are no render targets to swap - so the world can be drawn
        /// straight into it. Both players count as being in this view, since
        /// that is literally true, which is what world-anchored overlays from
        /// other mods ask before drawing themselves.
        /// </summary>
        private static void DrawSharedView(JumpGame game)
        {
            _drawingPass = true;
            LocalMultiplayerApi.SetCurrentViewPlayerMask(SharedViewPlayerMask);

            try
            {
                using (PlayerScope.Enter(ViewContexts[0], false, false))
                {
                    DrawWorld();
                }
            }
            finally
            {
                _drawingPass = false;
                LocalMultiplayerApi.SetCurrentViewPlayerMask(1);
            }

            DrawScreenSpaceUi(game);
            BattleMode.DrawRoundResult();
        }

        /// <summary>Both players, since one view holds them both.</summary>
        private const int SharedViewPlayerMask = (1 << 0) | (1 << 1);

        /// <summary>
        /// Draws the screen-space UI once over the composited views, at full size.
        ///
        /// The pause menu and the run timer both live in <c>GameLoop.Draw</c>, so
        /// drawing the whole game per view put a copy of each inside every view.
        /// This runs the same <c>JumpGame.Draw</c> with its world half suppressed,
        /// which leaves the state UI and nothing else.
        /// </summary>
        private static void DrawScreenSpaceUi(JumpGame game)
        {
            IsScreenSpacePass = true;
            _drawingPass = true;

            try
            {
                game.Draw();
            }
            finally
            {
                _drawingPass = false;
                IsScreenSpacePass = false;
            }
        }

        public static void Release()
        {
            for (int i = 0; i < PlayerTargets.Length; i++)
            {
                DisposeTarget(ref PlayerTargets[i]);
                ViewTargetIndexes[i] = i;
                ViewContexts[i] = null;
            }
        }

        private static void DrawTwoPlayerViews()
        {
            for (int i = 0; i < 2; i++)
            {
                Game1.spriteBatch.Draw(
                    PlayerTargets[ViewTargetIndexes[i]],
                    new Rectangle(i * HalfWidth, 0, HalfWidth, Height),
                    GetPlayerViewport(ViewContexts[i]),
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

        private static Rectangle GetPlayerViewport(PlayerContext context)
        {
            BodyComp body = context == null ? null : context.Body;
            int centerX = body == null ? Width / 2 : body.GetHitbox().Center.X;
            int sourceX = centerX < HalfWidth ? 0 : HalfWidth;
            return new Rectangle(sourceX, 0, HalfWidth, Height);
        }

        /// <summary>
        /// Player 1 on top, player 2 below, each keeping the full screen width.
        ///
        /// Suits a map whose route runs sideways, where a half-width view cuts
        /// off what the player needs to see. Each band is a one to one slice of
        /// that player's own render, chosen by which half of the screen they are
        /// standing in, so nothing is squashed.
        /// </summary>
        private static void DrawStackedTwoPlayerViews()
        {
            for (int i = 0; i < 2; i++)
            {
                Game1.spriteBatch.Draw(
                    PlayerTargets[ViewTargetIndexes[i]],
                    new Rectangle(0, i * HalfHeight, Width, HalfHeight),
                    GetPlayerBandViewport(ViewContexts[i]),
                    Color.White
                );
            }
        }

        private static Rectangle GetPlayerBandViewport(PlayerContext context)
        {
            BodyComp body = context == null ? null : context.Body;
            if (body == null)
            {
                return new Rectangle(0, 0, Width, HalfHeight);
            }

            // World Y runs negative upward and every screen is exactly one
            // screen height, so the remainder is the position within whichever
            // screen the player is on. The extra term keeps it positive.
            int withinScreen = ((body.GetHitbox().Center.Y % Height) + Height) % Height;
            int sourceY = withinScreen < HalfHeight ? 0 : HalfHeight;
            return new Rectangle(0, sourceY, Width, HalfHeight);
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
                if (ViewContexts[i].Screen == ViewContexts[viewIndex].Screen &&
                    ViewContexts[i].Offset == ViewContexts[viewIndex].Offset)
                {
                    return ViewTargetIndexes[i];
                }
            }

            return viewIndex;
        }

        private static void DrawView(
            Game1 host,
            GraphicsDevice graphics,
            RenderTarget2D target,
            PlayerContext context,
            int playerMask
        )
        {
            graphics.SetRenderTarget(target);
            graphics.Clear(Color.Black);

            // Read-only: the draw pass must not feed the camera back into the
            // context, otherwise a scrolling ending would move the player's view.
            using (PlayerScope.Enter(context, false, false))
            {
                host.StartBatch();
                _drawingPass = true;
                LocalMultiplayerApi.SetCurrentViewPlayerMask(playerMask);

                try
                {
                    DrawWorld();
                }
                finally
                {
                    _drawingPass = false;
                    LocalMultiplayerApi.SetCurrentViewPlayerMask(1);
                    host.EndBatch();
                }
            }
        }

        /// <summary>
        /// The world half of <c>JumpGame.Draw</c>, which is everything that has to
        /// be drawn once per view because it depends on that player's camera.
        ///
        /// IForeground stays here rather than moving to the screen-space pass:
        /// the interface is a layer, not a coordinate space, and the overlays that
        /// use it are world-anchored - one transforms the player's hitbox through
        /// the camera, another already asks which view is being drawn. Moving them
        /// would pin both to player 1's camera.
        /// </summary>
        private static void DrawWorld()
        {
            LevelScreen screen = LevelManager.CurrentScreen;
            if (screen == null)
            {
                return;
            }

            screen.Draw();
            DrawWorldEntities();
            BattleMode.DrawWorldProps();
            screen.DrawForeground();

            IReadOnlyList<Entity> entities = EntityManager.instance.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                var foreground = entities[i] as IForeground;
                if (foreground != null && !IsScreenSpaceUi(entities[i]))
                {
                    foreground.ForegroundDraw();
                }
            }
        }

        /// <summary>
        /// EntityManager.Draw would draw every entity, including the pause menu,
        /// so the loop is repeated here with the screen-space ones left out.
        /// </summary>
        private static void DrawWorldEntities()
        {
            IReadOnlyList<Entity> entities = EntityManager.instance.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                if (!IsScreenSpaceUi(entities[i]))
                {
                    entities[i].Draw();
                }
            }
        }

        /// <summary>
        /// The pause menu is an Entity that also implements IForeground, and its
        /// ForegroundDraw just calls Draw, so the world pass would render it
        /// twice per view. It is screen-space UI at fixed coordinates and the
        /// base game already draws it from GameLoop.Draw, which the split
        /// renderer runs once over the composited views - so every copy the world
        /// pass makes is a duplicate.
        ///
        /// The other IForeground types in the base game - the old man, the
        /// merchant, lightning, location text - are anchored in the world and
        /// belong in the per-view pass, so they are deliberately not listed here.
        /// </summary>
        private static bool IsScreenSpaceUi(Entity entity)
        {
            return PauseManagerType != null &&
                PauseManagerType.IsInstanceOfType(entity);
        }

        private static readonly Type PauseManagerType =
            AccessTools.TypeByName("JumpKing.PauseMenu.PauseManager");

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
