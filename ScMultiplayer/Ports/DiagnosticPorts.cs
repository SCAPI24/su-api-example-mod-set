using ScMultiplayer.Diagnostics;

namespace ScMultiplayer.Ports
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:7.2 异步化边界
    // The sink receives bounded records after the game-thread apply budget. HeadlessRenderingMod
    // can enqueue them to its existing background file writer without a hard dependency here.
    internal interface IDiagnosticSink
    {
        void Consume(in DiagnosticRecord record);

        void ConsumeDrop(DiagnosticRecordKind kind, long count);
    }
}
