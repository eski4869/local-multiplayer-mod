using JumpKing;
using JumpKing.Util;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Draws each player's Steam name above their head, during a netplay session.
    ///
    /// Over the network the two kings are the same sprite in different colours, and
    /// which colour is whose is not something either player agreed to - so the only
    /// reliable way to tell who is who is to say so. Locally it is never a question:
    /// the players are in the same room.
    ///
    /// Drawn from the additional-player draw patch, which already runs once per
    /// player per view and already has that player's identity to hand.
    /// </summary>
    internal static class NetplayNameTags
    {
        /// <summary>Pixels above the body's top edge.</summary>
        private const int Above = 12;

        public static void Draw(PlayerContext context)
        {
            if (context == null || context.Body == null ||
                !ModEntry.Netplay.IsPlaying)
            {
                return;
            }

            string name = NameFor(context);
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // Only while the player it names is actually on this screen. The tag is
            // drawn at a world position offset by the camera, and a player on
            // another screen lands outside the view - but the offset only accounts
            // for the current screen, so the tag was left hanging over empty ground
            // belonging to somebody who is nowhere near it.
            if (context.Screen != Camera.CurrentScreen)
            {
                return;
            }

            Rectangle body = context.Body.GetHitbox();

            // Camera-relative, because everything drawn in the world is: the same
            // world position lands somewhere different depending on which player's
            // view is being drawn, and the camera is what carries that.
            var at = new Vector2(body.Center.X, body.Top - Above) - Camera.Offset;

            TextHelper.DrawString(
                Game1.instance.contentManager.font.MenuFontSmall,
                name,
                at,
                Color.White,

                // Centred horizontally, sitting on the given line vertically, so
                // the tag stays over the head whatever the name's length.
                new Vector2(0.5f, 1f),
                false
            );
        }

        private static string NameFor(PlayerContext context)
        {
            if (context.IsLocallyDriven)
            {
                return NetplayTransport.LocalName;
            }

            string peer = ModEntry.Netplay.PeerName;
            return string.IsNullOrEmpty(peer) ? "Player 2" : peer;
        }
    }
}
