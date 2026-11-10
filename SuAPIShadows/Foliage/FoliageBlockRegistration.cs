using System;
using Engine;
using Game;
using SuAPI;

namespace SuAPIShadows.Foliage;

// Source: IModInjector.cs:RegisterBlock/ApplyBlocks; BlocksManager.cs:Initialize; ModInjector.cs:FlushBlockOverrides.
internal sealed class FoliageBlockRegistration : IDisposable
{
    private sealed class Entry
    {
        public readonly int Index;
        public readonly Type Native;
        public readonly Type Replacement;
        public Block Original;
        public Block Installed;
        public Entry(int index, Type native, Type replacement) { Index = index; Native = native; Replacement = replacement; }
    }
    private readonly Entry[] m_entries = new[]
    {
        new Entry(19, typeof(TallGrassBlock), typeof(FoliageTallGrassBlock)),
        new Entry(20, typeof(RedFlowerBlock), typeof(FoliageRedFlowerBlock)),
        new Entry(25, typeof(WhiteFlowerBlock), typeof(FoliageWhiteFlowerBlock)),
        new Entry(24, typeof(PurpleFlowerBlock), typeof(FoliagePurpleFlowerBlock)),
        new Entry(174, typeof(RyeBlock), typeof(FoliageRyeBlock)),
        new Entry(204, typeof(CottonBlock), typeof(FoliageCottonBlock)),
        new Entry(131, typeof(PumpkinBlock), typeof(FoliagePumpkinBlock)),
        new Entry(244, typeof(RottenPumpkinBlock), typeof(FoliageRottenPumpkinBlock)),
        new Entry(28, typeof(DryBushBlock), typeof(FoliageDryBushBlock)),
        new Entry(99, typeof(LargeDryBushBlock), typeof(FoliageLargeDryBushBlock)),
        new Entry(232, typeof(KelpBlock), typeof(FoliageKelpBlock)),
        new Entry(233, typeof(SeagrassBlock), typeof(FoliageSeagrassBlock)),
        new Entry(13, typeof(BirchLeavesBlock), typeof(FoliageBirchLeavesBlock)),
        new Entry(256, typeof(MimosaLeavesBlock), typeof(FoliageMimosaLeavesBlock)),
        new Entry(12, typeof(OakLeavesBlock), typeof(FoliageOakLeavesBlock)),
        new Entry(263, typeof(PoplarLeavesBlock), typeof(FoliagePoplarLeavesBlock)),
        new Entry(14, typeof(SpruceLeavesBlock), typeof(FoliageSpruceLeavesBlock)),
        new Entry(225, typeof(TallSpruceLeavesBlock), typeof(FoliageTallSpruceLeavesBlock)),
    };
    private static bool s_registered;
    private IModEventBus m_events;
    private IModInjector m_injector;
    private EventSubscriptionToken m_token;
    private Block[] m_blocks;
    private Exception m_failure;
    private bool m_disposed;
    private bool m_recordsRegistered;
    private const string Owner = "SuAPIShadows.Foliage";

    public static FoliageBlockRegistration Install(IModEventBus events, IModInjector injector)
    {
        if (events == null) throw new ArgumentNullException(nameof(events));
        if (injector == null) throw new ArgumentNullException(nameof(injector));
        // The framework has no unregister API; a second registration would leave duplicate persistent records.
        if (s_registered) throw new InvalidOperationException("[SuAPI] Foliage blocks were already registered in this process; reload requires a fresh Mod injector/process.");
        var registration = new FoliageBlockRegistration { m_events = events, m_injector = injector };
        s_registered = true;
        registration.m_token = events.SubscribeEvent("BlocksManager.Initialize", registration.Apply, EventPriority.HIGHEST);
        return registration;
    }

    private object[] Apply(object[] args)
    {
        if (m_disposed) throw new ObjectDisposedException(nameof(FoliageBlockRegistration));
        try
        {
            if (args == null || args.Length != 1)
                throw new InvalidOperationException("[SuAPIShadows] Invalid block initialization arguments.");
            // Source: ClothingBlock.cs:Initialize — the native game also uses this event name for ClothingData[].
            // This subscriber decorates Block[] only; it does not participate in clothing-data initialization.
            if (args[0] is ClothingData[]) return new object[] { false, args[0] };
            if (!(args[0] is Block[] blocks))
                throw new InvalidOperationException("[SuAPIShadows] Unsupported initialization payload: " + args[0]?.GetType());
            foreach (Entry entry in m_entries)
            {
                if (entry.Index >= blocks.Length || blocks[entry.Index] == null || blocks[entry.Index].GetType() != entry.Native)
                    throw new InvalidOperationException("[SuAPI] Foliage requires native block at index " + entry.Index + " (" + entry.Native.FullName + ").");
                entry.Original = blocks[entry.Index];
            }
            // Source: ModInjector.ApplyBlocks — every subscriber applies all pending records in the shared injector.
            // Register only after capturing native instances, so earlier subscribers cannot apply our records prematurely.
            if (!m_recordsRegistered)
            {
                m_recordsRegistered = true;
                foreach (Entry entry in m_entries)
                    m_injector.RegisterBlock(entry.Index, entry.Replacement, Owner, ConflictAction.Error,
                        info => RecordFailure(new InvalidOperationException(
                            "[SuAPI] Foliage block conflict at index " + info.BlockIndex + ": replaced by " + info.LaterModName + " (" + info.FinalBlockType + ").")));
            }
            m_injector.ApplyBlocks(blocks, Owner);
            m_blocks = blocks;
            foreach (Entry entry in m_entries)
            {
                if (blocks[entry.Index].GetType() != entry.Replacement)
                    throw new InvalidOperationException("[SuAPI] Foliage replacement failed at index " + entry.Index + ".");
                entry.Installed = blocks[entry.Index];
            }
            return new object[] { true, blocks };
        }
        catch (Exception error) { RecordFailure(error); throw; }
    }

    private void RecordFailure(Exception error)
    {
        // Framework event/override dispatch catches exceptions: retain the original failure for the owning world/Draw boundary.
        if (!m_disposed && m_failure == null)
        {
            m_failure = error;
            Log.Error("[SuAPIShadows] Foliage registration failed: " + error);
        }
    }

    public void Check()
    {
        if (m_disposed) throw new ObjectDisposedException(nameof(FoliageBlockRegistration));
        if (m_failure != null) throw new InvalidOperationException("[SuAPI] Foliage registration failed.", m_failure);
        if (m_blocks == null) throw new InvalidOperationException("[SuAPI] Foliage blocks have not completed BlocksManager.Initialize.");
        foreach (Entry entry in m_entries)
            if (!ReferenceEquals(m_blocks[entry.Index], entry.Installed) || m_blocks[entry.Index].GetType() != entry.Replacement)
                throw new InvalidOperationException("[SuAPI] Foliage block ownership was lost at index " + entry.Index + ".");
    }

    public static bool IsTrackedBlock(int contents) => HasAir(contents) || HasWater(contents);
    public static bool HasWater(int contents) => contents == KelpBlock.Index || contents == SeagrassBlock.Index;
    public static bool HasAir(int contents) => contents == TallGrassBlock.Index ||
        contents == RedFlowerBlock.Index || contents == WhiteFlowerBlock.Index || contents == PurpleFlowerBlock.Index ||
        contents == RyeBlock.Index || contents == CottonBlock.Index || contents == PumpkinBlock.Index || contents == RottenPumpkinBlock.Index ||
        contents == DryBushBlock.Index || contents == LargeDryBushBlock.Index || contents == BirchLeavesBlock.Index ||
        contents == MimosaLeavesBlock.Index || contents == OakLeavesBlock.Index || contents == PoplarLeavesBlock.Index ||
        contents == SpruceLeavesBlock.Index || contents == TallSpruceLeavesBlock.Index;

    public void Dispose()
    {
        if (m_disposed) return;
        m_disposed = true;
        if (m_token != null) m_events.UnsubscribeEvent(m_token);
        m_token = null;
        // BlocksManager initializes replacements after the event, so preserved native instances must initialize before restoration.
        if (m_blocks != null)
            foreach (Entry entry in m_entries)
                if (entry.Installed != null && ReferenceEquals(m_blocks[entry.Index], entry.Installed))
                {
                    entry.Original.Initialize();
                    m_blocks[entry.Index] = entry.Original;
                }
        m_blocks = null;
        foreach (Entry entry in m_entries) { entry.Installed = null; entry.Original = null; }
        m_events = null;
        m_injector = null;
    }
}
