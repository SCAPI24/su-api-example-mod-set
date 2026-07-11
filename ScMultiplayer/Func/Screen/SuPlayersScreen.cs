using Engine;
using Engine.Input;
using Engine.Media;
using Game;
using SuAPI;
using System;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuPlayersScreen : PlayersScreen
    {
        public SuPlayersScreen() : base() { }

        public override void Enter(object[] parameters)
        {
            base.Enter(parameters);

            // In network mode, disable add/layout buttons
            if (ScMultiplayer.client != null && ScMultiplayer.client.IsConnected)
            {
                var addBtn = Children.Find<ButtonWidget>("AddPlayerButton");
                if (addBtn != null) addBtn.IsVisible = false;

                var layoutBtn = Children.Find<ButtonWidget>("ScreenLayoutButton");
                if (layoutBtn != null) layoutBtn.IsVisible = false;

                Log.Information("[SuPlayers] Network mode: add/layout buttons hidden");
            }
        }
    }
}
