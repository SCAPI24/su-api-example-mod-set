using Comms.Drt;
using Engine;
using Engine.Input;
using Game;
using SuMod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuPlayScreen : PlayScreen
    {
        public static ListPanelWidget m_worldsListWidget;
        public List<WorldInfo> m_worldInfos;
        public Dictionary<string, (DateTime, float)> ServerWorldName = new Dictionary<string, (DateTime, float)>();
        public bool BaseUpdate = true;
        public static bool IsGameJoined = false;
        public static byte[] WorldData;
        public WorldInfo SelectedItem;

        public SuPlayScreen() : base()
        {
            m_worldsListWidget = Children.Find<ListPanelWidget>("WorldsList");

            if (m_worldInfos == null)
            {
                m_worldInfos = Game.Program.ModManager.ModParentField.GetStaticField<List<WorldInfo>>(typeof(Game.WorldsManager), "m_worldInfos");
            }






            m_worldsListWidget.ItemWidgetFactory = (Func<object, Widget>)Delegate.Combine(m_worldsListWidget.ItemWidgetFactory, (Func<object, Widget>)delegate (object item)
            {
                WorldInfo worldInfo = (WorldInfo)item;
                XElement node2 = ContentManager.Get<XElement>("Widgets/SavedWorldItem");
                ContainerWidget containerWidget = (ContainerWidget)Widget.LoadWidget(this, node2, null);
                LabelWidget labelWidget = containerWidget.Children.Find<LabelWidget>("WorldItem.Name");
                LabelWidget labelWidget2 = containerWidget.Children.Find<LabelWidget>("WorldItem.Details");
                containerWidget.Tag = worldInfo;
                labelWidget.Text = worldInfo.WorldSettings.Name;
                labelWidget2.Text = string.Format("{0} | {1:dd MMM yyyy HH:mm} | {2} | {3} | {4}", DataSizeFormatter.Format(worldInfo.Size), worldInfo.LastSaveTime.ToLocalTime(), (worldInfo.PlayerInfos.Count > 1) ? $"{worldInfo.PlayerInfos.Count} players" : "1 player", worldInfo.WorldSettings.GameMode.ToString(), worldInfo.WorldSettings.EnvironmentBehaviorMode.ToString());
                if (worldInfo.SerializationVersion != VersionsManager.SerializationVersion)
                {
                    labelWidget2.Text = labelWidget2.Text + " | " + (string.IsNullOrEmpty(worldInfo.SerializationVersion) ? "(unknown)" : ("(" + worldInfo.SerializationVersion + ")"));
                }
                foreach (var name in ServerWorldName)
                {
                    if (labelWidget.Text == name.Key) {
                        if (worldInfo.LastSaveTime == name.Value.Item1)
                        {
                                labelWidget2.Text = labelWidget2.Text + " | " + "Net " + ("(Ping " + (int)name.Value.Item2 * 1000f + "ms)");
                        }
                    }
                }

                return containerWidget;

            });
            

            // 获取事件字段（通常是带事件名的字段）
            var eventField = ScMultiplayer.ModManager.ModParentField.GetParentField(m_worldsListWidget, "ItemClicked",
                m_worldsListWidget.GetType()) as Action<object>;
                if (eventField != null)
                {
                    foreach (var handler in eventField.GetInvocationList())
                    {
                        m_worldsListWidget.ItemClicked -= (Action<object>)handler;
                    }
                }
            m_worldsListWidget.ItemClicked += delegate (object item)
            {
                if (item != null && m_worldsListWidget.SelectedItem == item)
                {
                    ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
                    if (a.GameDescriptions.Length == 0)
                    {
                        GameCreate(item);
                        Play(item);
                        /*ScreensManager.SwitchScreen("GameLoading", item, null);
                        m_worldsListWidget.SelectedItem = null;*/
                        return;
                    }
                    foreach (var gameDescription in a.GameDescriptions)
                    {
                        switch (Message.Read(gameDescription.GameDescriptionBytes))
                        {
                            case GameWorldInfoMessage gameWorldInfoMessage:
                                if (gameWorldInfoMessage.GetSenderPort() == ScMultiplayer.client.Address.Port)
                                {

                                }
                                else
                                {
                                    GameJoin(item, gameDescription);
                                    
                                    return;
                                }
                                break;
                        }
                    }
                }
                


            };
        }
        public override void Update()
        {
            if (m_worldsListWidget.SelectedItem != null)
            {
                SelectedItem = (WorldInfo)m_worldsListWidget.SelectedItem;
            }
            else
            {
                SelectedItem = null;
            }
            if (Children.Find<ButtonWidget>("Play").IsClicked && m_worldsListWidget.SelectedItem != null)
            {
                WorldInfo worldInfo = (WorldInfo)m_worldsListWidget.SelectedItem;
                ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
                foreach (var item in a.GameDescriptions)
                {
                    switch (Message.Read(item.GameDescriptionBytes))
                    {
                        case GameWorldInfoMessage gameWorldInfoMessage:
                            if (gameWorldInfoMessage.GetSenderPort() == ScMultiplayer.client.Address.Port)
                            {
                                GameCreate(m_worldsListWidget.SelectedItem);
                            }
                            else
                            {
                                GameJoin(m_worldsListWidget.SelectedItem, item);
                            }
                            break;
                    }
                }

            }
            base.Update();
        }
        private void GameCreate(object item)
        {
            ScMultiplayer.IsHost=true;
            WorldInfo worldInfo = (WorldInfo)item;
            ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
            ScMultiplayer.client.CreateGame(a.Address, Message.Write(new GameWorldInfoMessage(worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime, worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode, worldInfo.SerializationVersion, ScMultiplayer.client.Address), ScMultiplayer.client.Address), ScMultiplayer.client.Address.Port.ToString());
            using (MemoryStream memoryStream = new MemoryStream())
            {
                WorldsManager.ExportWorld(worldInfo.DirectoryName, memoryStream);
                WorldData = memoryStream.ToArray();
            }

        }
        private void GameJoin(object item1, GameDescription item)
        {
            ScMultiplayer.IsHost = false;
            WorldInfo worldInfo = (WorldInfo)item1;

           // ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();

            ScMultiplayer.client.JoinGame(item.ServerDescription.Address, item.GameID,
                Message.Write(
                    new GameWorldInfoMessage(worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime, worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode, worldInfo.SerializationVersion, ScMultiplayer.client.Address),
                    ScMultiplayer.client.Address), ScMultiplayer.client.Address.Port.ToString());
        }

        public override void Enter(object[] parameters)
        {

            if (ScMultiplayer.client.IsConnected)
            {
                ScMultiplayer.client.LeaveGame();
            }

            Task.Run(delegate
            {
                Dispatcher.Dispatch(delegate
                {
                    Task.Run(delegate
                    {
                        WorldInfo selectedItem = (WorldInfo)m_worldsListWidget.SelectedItem;
                        Dispatcher.Dispatch(delegate
                        {
                            ServerWorldName.Clear();
                            ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
                            foreach (var item in a.GameDescriptions)
                            {
                                switch (Message.Read(item.GameDescriptionBytes))
                                {
                                    case GameWorldInfoMessage gameWorldInfoMessage:
                                        if (ScMultiplayer.client.Address.Equals(gameWorldInfoMessage.HostAddress))
                                        {
                                            continue;
                                        }
                                        HandleGameWorldInfoMessage(gameWorldInfoMessage, item);
                                        break;
                                    default:
                                        Log.Error("InputBytes Is ERR");
                                        break;
                                }

                            }
                            Log.Information(a.GameDescriptions.Count());

                        });
                    });

                });

            });
            base.Enter(parameters);

            //client.JoinGame(a.Address, ((GameDescription)item).GameID, Message.Write(new ChatMessage("ss", "ss")), client.ClientID.ToString());
            /*            */


        }
        private void HandleGameWorldInfoMessage(GameWorldInfoMessage gameWorldInfoMessage, GameDescription item)
        {

            WorldInfo worldInfo = new WorldInfo
            {
                DirectoryName = "data:/Worlds/" + gameWorldInfoMessage.Name,
                Size = gameWorldInfoMessage.Size,
                LastSaveTime = gameWorldInfoMessage.LastSaveTime,

                PlayerInfos = new List<PlayerInfo>(),
                SerializationVersion = gameWorldInfoMessage.SerializationVersion,
                WorldSettings = new WorldSettings
                {
                    Name = gameWorldInfoMessage.Name
                }
            };
            for (int i = 0; i < item.ClientsCount; i++)
            {
                worldInfo.PlayerInfos.Add(new PlayerInfo());
            }
            ServerWorldName.Add(gameWorldInfoMessage.Name,( gameWorldInfoMessage.LastSaveTime, item.ServerDescription.Ping ));
            m_worldInfos.Add(worldInfo);
            m_worldsListWidget.AddItem(worldInfo);
            /*   if (selectedItem != null)
               {
                   m_worldsListWidget.SelectedItem = worldInfos.FirstOrDefault((WorldInfo wi) => wi.DirectoryName == selectedItem.DirectoryName);
               }*/
        }
        public static void Play(object item)
        {
            ScreensManager.SwitchScreen("GameLoading", item, null);
            m_worldsListWidget.SelectedItem = null;
        }



    }
}
/*
               if (item != null && item.Equals(SelectedItem))
               {
                   foreach (var name in ServerWorldName)
                   {
                       if (name.Key == ((WorldInfo)item).WorldSettings.Name)
                       {
                           GameJoin(m_worldsListWidget.SelectedItem, a.);
                       }
                   }
                   item.Equals(SelectedItem);
                   GameCreate(item);
               }*/
/*if (m_worldsListWidget == null)
{
    m_worldsListWidget = Game.Program.ModManager.ModParentField.GetParentField<ListPanelWidget>(this, "m_worldsListWidget", typeof(PlayScreen));//

}*/

//m_worldsListWidget = m_worldsListWidget.ParentWidget. Children.Find<ListPanelWidget>("WorldsList");
/*            var parent = Children.Find<ContainerWidget>("WorldsListParent"); 
                m_worldsListWidget = Chil.Children.Find<ListPanelWidget>("WorldsList");*/
// m_worldsListWidget = m_worldsListWidget.ParentWidget.Children.Find<ListPanelWidget>("WorldsList");
// 打包世界数据到内存流
/*                using (MemoryStream memoryStream = new MemoryStream())
                {
                    WorldsManager.ExportWorld(worldInfo.DirectoryName, memoryStream);
                    ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
                    ScMultiplayer.client.CreateGame(a.Address, Message.Write(new GameWorldInfoMessage(worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime, worldInfo.PlayerInfos.Count, worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode, worldInfo.SerializationVersion, memoryStream.ToArray())), ScMultiplayer.client.ClientID.ToString());
                    Log.Information("sd");

                }*/

//WorldsManager.ImportWorld(new MemoryStream(gamePakWorldMessage.WorldData));d

//BaseUpdate = false;
/*ScMultiplayer.client.GameJoined += delegate
{
    if (IsGameJoined)
    {

    }

    BaseUpdate = true;
};*/

//ScreensManager.SwitchScreen(ScreensManager.FindScreen<Screen>("Play"));
//ScreensManager.SwitchScreen("GameLoading", item, null);
/* if (item != null && item.Equals(SelectedItem))
 {
     ServerDescription a = ScMultiplayer.explorer.DiscoveredServers.FirstOrDefault();
     if (a.GameDescriptions.Length == 0) {
         GameCreate(item);
         return;
     }
     foreach (var gameDescription in a.GameDescriptions)
     {
         switch (Message.Read(gameDescription.GameDescriptionBytes))
         {
             case GameWorldInfoMessage gameWorldInfoMessage:
                 if (gameWorldInfoMessage.GetSenderPort() == ScMultiplayer.client.Address.Port)
                 {

                 }
                 else
                 {
                     GameJoin(item, gameDescription);
                     ScreensManager.SwitchScreen(ScreensManager.FindScreen<Screen>("Play"));
                     //ScreensManager.SwitchScreen("GameLoading", item, null);
                     return;
                 }
                 break;
         }
     }


 }*/
