using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Engine;
using Engine.Content;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

internal static class Program
{
    private static int s_passed;
    private static int s_failed;

    // Source: LabelWidget.cs:LabelWidget; ContentCache.cs:Set; BitmapFont.cs:BitmapFont
    private static int Main()
    {
        var glyphs = Enumerable.Range(32, 95).Select(code =>
            new BitmapFont.Glyph((char)code, Vector2.Zero, Vector2.Zero, Vector2.Zero, 8f)).ToArray();
        using var font = new BitmapFont(null, glyphs, '?', 16f, Vector2.Zero, 1f);
        ContentCache.Set("Fonts/Pericles32", font);

        Test("slot 2 requires clock contents and positive count", () =>
        {
            using var f = new Fixture();
            View view = f.AddView();
            f.LoadWatch(); f.Bind(); f.Step();
            Check(Panel(view) == null, "empty slot showed a watch");
            view.SetSlot(1, 187, 1); f.Step();
            Check(Panel(view) == null, "wrong slot showed a watch");
            view.SetSlot(2, 186, 1); f.Step();
            Check(Panel(view) == null, "wrong block showed a watch");
            view.SetSlot(2, 187, 0); f.Step();
            Check(Panel(view) == null, "zero count showed a watch");
            view.SetSlot(2, Terrain.MakeBlockValue(187, 7, 3), 1); f.Step();
            Check(Panel(view) != null, "clock contents with metadata did not show");
            CanvasWidget old = Panel(view);
            view.SetSlot(2, 187, 0); f.Step();
            Check(Panel(view) == null && old.ParentWidget == null, "removed clock left its UI attached");
        });

        Test("game clock format and original panel presentation", () =>
        {
            using var f = new Fixture();
            View view = f.AddView(clock: true);
            f.SetGameTime(1800d); // DayStart and offset are explicitly zero in this fixture: day 1.5.
            f.LoadWatch(); f.Bind(); f.Step();
            CanvasWidget panel = Panel(view);
            Check(panel != null && panel.Size == new Vector2(64f, 48f));
            Check(panel.Margin == new Vector2(0f, 3f));
            Check(TimeLabel(view).Text == "12:00" && DayLabel(view).Text == "Day 2");
            Check(TimeLabel(view).FontScale == 0.65f && DayLabel(view).FontScale == 0.45f);
            f.SetGameTime(2400d); f.Step();
            Check(TimeLabel(view).Text == "00:00" && DayLabel(view).Text == "Day 3");
        });

        Test("split screen slots belong to their own view", () =>
        {
            using var f = new Fixture();
            View first = f.AddView(clock: true);
            View second = f.AddView();
            f.LoadWatch(); f.Bind(); f.Step();
            Check(Panel(first) != null && Panel(second) == null);
            first.SetSlot(2, 187, 0); second.SetSlot(2, 187, 1); f.Step();
            Check(Panel(first) == null && Panel(second) != null, "slots leaked across players");
            first.SetSlot(2, 187, 1); f.Step();
            Check(!ReferenceEquals(Panel(first), Panel(second)), "views shared a panel instance");
            Check(!ReferenceEquals(TimeLabel(first), TimeLabel(second)), "views shared label state");
        });

        Test("respawn reads the current entity crafting table", () =>
        {
            using var f = new Fixture();
            View view = f.AddView(clock: true);
            f.LoadWatch(); f.Bind(); f.Step();
            CanvasWidget old = Panel(view);
            view.Respawn(); f.Step();
            Check(Panel(view) == null && old.ParentWidget == null, "old entity retained its watch");
            view.SetSlot(2, 187, 1); f.Step();
            Check(Panel(view) != null, "new entity was not observed");
            view.ClearPlayer(); f.Step();
            Check(Panel(view) == null, "player absence retained the old UI");
        });

        Test("view removal and later arrival reconcile owned UI", () =>
        {
            using var f = new Fixture();
            View removed = f.AddView(clock: true);
            f.LoadWatch(); f.Bind(); f.Step();
            CanvasWidget old = Panel(removed);
            f.RemoveView(removed); f.Step();
            Check(old.ParentWidget == null, "removed view retained watch UI");
            View added = f.AddView(clock: true); f.Step();
            Check(Panel(added) != null && !ReferenceEquals(Panel(added), old));
        });

        Test("empty initial views attach when players arrive", () =>
        {
            using var f = new Fixture();
            f.LoadWatch(); f.Bind(); f.Step();
            View added = f.AddView(clock: true); f.Step();
            Check(Panel(added) != null);
        });

        foreach (bool peerFirst in new[] { false, true })
        {
            bool order = peerFirst;
            Test("Watch + independent peer share one Views host; peerFirst=" + order, () =>
            {
                using var f = new Fixture();
                View view = f.AddView(clock: true);
                if (order) f.LoadPeer();
                f.LoadWatch();
                if (!order) f.LoadPeer();
                f.Bind(); f.Step();
                Check(view.Camera.Updates == 1 && f.Peer.Updates == 1, "host or peer dispatched more than once");
                Check(Panel(view) != null && view.Left.Children.Contains(f.Peer.Marker));
                Check(f.ProxyCount == 1 && !f.NativeHostScheduled, "original and runner both scheduled");
                Check(f.Host.UpdateOrder == UpdateOrder.Views);
                f.Step();
                Check(view.Camera.Updates == 2 && f.Peer.Updates == 2);
                CanvasWidget old = Panel(view);
                f.UnloadWatch(); f.Step();
                Check(old.ParentWidget == null && Panel(view) == null);
                Check(view.Left.Children.Contains(f.Peer.Marker), "Watch removed another Mod's UI");
                Check(view.Camera.Updates == 3 && f.Peer.Detaches == 0);
                f.UnloadPeer(); f.Step(); f.Step();
                Check(f.ProxyCount == 0 && f.NativeHostScheduled, "last unload failed to restore native host");
                Check(view.Camera.Updates == 5 && f.Peer.Detaches == 1);
            });
        }

        Test("independent peer veto still runs Watch AfterUpdate without invoking host", () =>
        {
            using var f = new Fixture();
            View view = f.AddView(clock: true);
            f.LoadWatch(); f.LoadPeer(allowHost: false); f.Bind(); f.Step();
            Check(f.Peer.Updates == 1 && view.Camera.Updates == 0);
            Check(Panel(view) != null, "peer veto incorrectly skipped other extension callbacks");
            f.Peer.AllowHost = true; f.Step();
            Check(view.Camera.Updates == 1, "prior veto leaked into next round");
        });

        Test("unload and reload create one fresh panel and preserve unrelated siblings", () =>
        {
            using var f = new Fixture();
            View view = f.AddView(clock: true);
            var unrelated = new CanvasWidget { Name = "Independent.Unrelated" };
            view.Left.Children.Add(unrelated);
            f.LoadWatch(); f.Bind(); f.Step();
            CanvasWidget old = Panel(view);
            f.UnloadWatch(); f.Step(); f.Step();
            Check(old.ParentWidget == null && view.Left.Children.Contains(unrelated));
            f.LoadWatch(); f.Step(); f.Step();
            Check(Panel(view) != null && !ReferenceEquals(old, Panel(view)));
            Check(view.Left.Children.Count(w => w.Name == "WatchMod.Panel") == 1);
            Check(view.Left.Children.Contains(unrelated));
        });

        Test("binding disposal clears UI before native project disposal", () =>
        {
            using var f = new Fixture();
            View view = f.AddView(clock: true);
            f.LoadWatch(); f.Bind(); f.Step();
            CanvasWidget old = Panel(view);
            f.DisposeBinding();
            Check(old.ParentWidget == null && !f.Registry.HasBoundProjects);
        });

        Test("separate worlds and Mod instances do not share UI or teardown", () =>
        {
            using var first = new Fixture();
            using var second = new Fixture();
            View a = first.AddView(clock: true);
            View b = second.AddView(clock: true);
            first.SetGameTime(0); second.SetGameTime(1800);
            first.LoadWatch(); first.Bind(); first.Step();
            second.LoadWatch(); second.Bind(); second.Step();
            Check(!ReferenceEquals(Panel(a), Panel(b)));
            Check(TimeLabel(a).Text == "00:00" && TimeLabel(b).Text == "12:00");
            first.UnloadWatch(); first.Step(); second.Step();
            Check(Panel(a) == null && Panel(b) != null, "teardown crossed worlds");
        });

        Console.WriteLine($"Watch integration: {s_passed} passed, {s_failed} failed.");
        Console.WriteLine("Boundary: actual built WatchMod, SuAPI registry/binding/runner, native SubsystemUpdate and real widgets; reflection-built in-memory world, no game window, rendering, database Load or world files.");
        return s_failed == 0 ? 0 : 1;
    }

    private static CanvasWidget Panel(View view) => view.Left.Children.Find<CanvasWidget>("WatchMod.Panel", false);
    private static LabelWidget TimeLabel(View view) => view.Left.Children.Find<LabelWidget>("WatchMod.Time", true);
    private static LabelWidget DayLabel(View view) => view.Left.Children.Find<LabelWidget>("WatchMod.Day", true);
    private static void Check(bool condition, string message = "assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Test(string name, Action action)
    {
        try { action(); s_passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { s_failed++; Console.WriteLine("FAIL " + name + Environment.NewLine + ex); }
    }
    private static FieldInfo Field(Type type, string name)
    {
        for (Type current = type; current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new InvalidOperationException("Fixture field missing: " + type.FullName + "." + name);
    }
    private static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);
    private static void SetProperty(object target, string name, object value) => Set(target, "<" + name + ">k__BackingField", value);

    // Source: tests/SuAPI.UpdateIntegrationTests/Program.cs:Fixture; SubsystemUpdate.cs:Update
    private sealed class Fixture : IDisposable
    {
        public readonly ModEventBus Bus = new ModEventBus();
        public readonly Project Project = (Project)RuntimeHelpers.GetUninitializedObject(typeof(Project));
        public readonly SubsystemUpdate Scheduler = new SubsystemUpdate();
        public readonly SubsystemGameWidgets Host = new SubsystemGameWidgets();
        public readonly SubsystemGameInfo Info = new SubsystemGameInfo();
        public readonly SubsystemTimeOfDay TimeOfDay = new SubsystemTimeOfDay();
        public readonly UpdateExtensionRegistry Registry;
        private readonly Coordinator m_coordinator = new Coordinator();
        private readonly List<View> m_createdViews = new List<View>();
        private UpdateExtensionProjectBinding m_binding;
        private WatchMod.WatchMod m_watch;
        private UpdateExtensionToken m_peerToken;
        public PeerExtension Peer;
        private List<GameWidget> Widgets => (List<GameWidget>)Field(typeof(SubsystemGameWidgets), "m_gameWidgets").GetValue(Host);

        public Fixture()
        {
            var subsystems = new List<Subsystem> { Scheduler, m_coordinator, Host, Info, TimeOfDay };
            Set(Project, "m_subsystems", subsystems);
            Set(Project, "m_entities", new Dictionary<Entity, bool>());
            foreach (Subsystem subsystem in subsystems) Set(subsystem, "m_project", Project);
            SetProperty(Host, "GamesWidget", new GamesWidget());
            SetProperty(Info, "WorldSettings", new WorldSettings { TimeOfDayMode = TimeOfDayMode.Changing });
            Set(TimeOfDay, "m_subsystemGameInfo", Info);
            SetProperty(TimeOfDay, "DayStart", 0f);
            TimeOfDay.TimeOfDayOffset = 0;
            var time = new SubsystemTime();
            Set(time, "m_subsystemPlayers", new SubsystemPlayers());
            Set(time, "m_subsystemUpdate", Scheduler);
            Set(Scheduler, "m_subsystemTime", time);
            Scheduler.UpdatesPerFrame = 1;
            Scheduler.AddUpdateable(m_coordinator);
            Scheduler.AddUpdateable(Host);
            Registry = UpdateExtensionRegistry.For(Bus);
            Registry.BindGameThread();
        }

        public void SetGameTime(double value) => SetProperty(Info, "TotalElapsedGameTime", value);
        public View AddView(bool clock = false)
        {
            var view = new View(this, m_createdViews.Count);
            m_createdViews.Add(view);
            Widgets.Add(view.Widget);
            Host.GamesWidget.Children.Add(view.Widget);
            if (clock) view.SetSlot(2, 187, 1);
            return view;
        }
        public void RemoveView(View view)
        {
            Widgets.Remove(view.Widget);
            Host.GamesWidget.Children.Remove(view.Widget);
        }
        public void LoadWatch()
        {
            Check(m_watch == null, "test tried loading Watch twice");
            m_watch = new WatchMod.WatchMod();
            m_watch.OnLoad(Bus, new ModInjector());
        }
        public void UnloadWatch() { m_watch?.OnUnload(); m_watch = null; }
        public void LoadPeer(bool allowHost = true)
        {
            m_peerToken = Bus.RegisterUpdateExtension<SubsystemGameWidgets>("IndependentPeer", host =>
                Peer = new PeerExtension(host) { AllowHost = allowHost });
        }
        public void UnloadPeer() { m_peerToken?.Dispose(); m_peerToken = null; }
        public void Bind()
        {
            m_binding = Registry.BindProject(Project, Scheduler, m_coordinator);
            m_coordinator.Binding = m_binding;
        }
        public void Step()
        {
            m_coordinator.Failure = null;
            Scheduler.Update();
            if (m_coordinator.Failure != null) throw new InvalidOperationException("Native coordinator failed.", m_coordinator.Failure);
            var errors = (Queue<Exception>)Field(typeof(UpdateExtensionRegistry), "m_pendingErrors").GetValue(Registry);
            if (errors.Count > 0) throw new InvalidOperationException("Framework reported an extension failure.", errors.Peek());
        }
        private IDictionary NativeUpdates => (IDictionary)Field(typeof(SubsystemUpdate), "m_updateables").GetValue(Scheduler);
        public int ProxyCount => NativeUpdates.Keys.Cast<object>().Count(key => key.GetType().FullName.Contains("UpdateExtensionProjectBinding+HostRunner"));
        public bool NativeHostScheduled => NativeUpdates.Contains(Host);
        public void DisposeBinding() { m_binding?.Dispose(); m_binding = null; m_coordinator.Binding = null; }
        public void Dispose()
        {
            DisposeBinding();
            UnloadWatch();
            UnloadPeer();
            Project.Dispose();
            foreach (View view in m_createdViews) view.Widget.Dispose();
        }
    }

    // Source: GameWidget.cs; ContainerWidget.cs:ContainerWidget; CanvasWidget.cs:WidgetRemoved
    // Skip GameWidget XML/assets initialization, but use actual Canvas/Widget base state and WidgetsList ownership.
    private sealed class View
    {
        private readonly Fixture m_fixture;
        private ComponentCraftingTable m_inventory;
        public readonly GameWidget Widget;
        public readonly StackPanelWidget Left;
        public readonly PlayerData Player;
        public readonly CountingCamera Camera;

        public View(Fixture fixture, int index)
        {
            m_fixture = fixture;
            Player = (PlayerData)RuntimeHelpers.GetUninitializedObject(typeof(PlayerData));
            Widget = (GameWidget)RuntimeHelpers.GetUninitializedObject(typeof(GameWidget));
            var initializedCanvas = new CanvasWidget();
            for (Type type = typeof(CanvasWidget); type != null; type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.Name != "Children") field.SetValue(Widget, field.GetValue(initializedCanvas));
            Set(Widget, "Children", new WidgetsList(Widget));
            SetProperty(Widget, "PlayerData", Player);
            SetProperty(Widget, "GameWidgetIndex", index);
            SetProperty(Widget, "SubsystemGameWidgets", fixture.Host);
            SetProperty(Player, "SubsystemGameWidgets", fixture.Host);
            Set(Player, "m_gameWidget", Widget);
            Left = new StackPanelWidget { Name = "LeftControlsContainer", Direction = LayoutDirection.Vertical };
            var gui = new CanvasWidget { Name = "Gui" };
            gui.Children.Add(Left);
            Widget.Children.Add(gui);
            SetProperty(Widget, "GuiWidget", gui);
            Camera = new CountingCamera(Widget);
            Set(Widget, "m_activeCamera", Camera);
            Set(Widget, "m_cameras", new List<Camera> { Camera });
            Respawn();
        }

        // Source: ComponentInventoryBase.cs:Slot/GetSlotValue/GetSlotCount; ComponentPlayer.cs:PlayerData
        public void Respawn()
        {
            var entity = (Entity)RuntimeHelpers.GetUninitializedObject(typeof(Entity));
            var player = new ComponentPlayer();
            m_inventory = new ComponentCraftingTable();
            Set(entity, "m_project", m_fixture.Project);
            Set(entity, "m_isAddedToProject", true);
            Set(entity, "m_components", new List<Component> { player, m_inventory });
            Set(entity, "m_valuesDictionary", new ValuesDictionary());
            Set(player, "m_entity", entity);
            Set(m_inventory, "m_entity", entity);
            SetProperty(player, "PlayerData", Player);
            SetProperty(Player, "ComponentPlayer", player);
            Widget.Target = player;
            var slots = (IList)Field(typeof(ComponentInventoryBase), "m_slots").GetValue(m_inventory);
            Type slotType = typeof(ComponentInventoryBase).GetNestedType("Slot", BindingFlags.NonPublic);
            for (int i = 0; i < 6; i++) slots.Add(Activator.CreateInstance(slotType, nonPublic: true));
        }
        public void ClearPlayer() { SetProperty(Player, "ComponentPlayer", null); Widget.Target = null; }
        public void SetSlot(int index, int value, int count)
        {
            var slots = (IList)Field(typeof(ComponentInventoryBase), "m_slots").GetValue(m_inventory);
            Set(slots[index], "Value", value);
            Set(slots[index], "Count", count);
        }
    }

    // Source: SubsystemGameWidgets.cs:Update; LoadingCamera.cs:Update
    // This probe wraps the real camera Update; the tested host remains exact SubsystemGameWidgets.
    private sealed class CountingCamera : LoadingCamera
    {
        public int Updates;
        public CountingCamera(GameWidget widget) : base(widget) { }
        public override void Update(float dt) { Updates++; base.Update(dt); }
    }

    // Source: tests/SuAPI.UpdateIntegrationTests/Program.cs:HarnessCoordinator
    private sealed class Coordinator : Subsystem, IUpdateable
    {
        public UpdateExtensionProjectBinding Binding;
        public Exception Failure;
        public UpdateOrder UpdateOrder => UpdateOrder.Reset;
        public void Update(float dt)
        {
            try { Binding?.Advance(); }
            catch (Exception ex) { Failure = ex; throw; }
        }
    }

    // Source: UpdateExtension.cs:Update/Attached/Detached; TestMenuA.cs:MenuExtension
    // A separate participant that never references WatchMod or its state.
    private sealed class PeerExtension : UpdateExtension<SubsystemGameWidgets>
    {
        public bool AllowHost = true;
        public int Updates;
        public int Detaches;
        public CanvasWidget Marker;
        public PeerExtension(SubsystemGameWidgets host) : base(host) { }
        public override void Attached()
        {
            Marker = new CanvasWidget { Name = "IndependentPeer.Marker" };
            Host.GameWidgets[0].Children.Find<StackPanelWidget>("LeftControlsContainer", true).Children.Add(Marker);
        }
        public override void Update(float dt) { Updates++; base.Update(dt, AllowHost); }
        public override void Detached()
        {
            Detaches++;
            Marker?.ParentWidget?.Children.Remove(Marker);
            Marker?.Dispose();
        }
    }
}
