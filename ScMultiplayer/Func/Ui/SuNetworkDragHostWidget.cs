using Engine;
using Game;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/DragHostWidget.cs:DragHostWidget.Update
    // Preserve the source inventory slot before ViewWidget.DragDrop removes it.
    public sealed class SuNetworkDragHostWidget : DragHostWidget
    {
        public override void Update()
        {
            if (!Input.Drag.HasValue && ScMultiplayer.currentInstance != null &&
                ScMultiplayer.ModManager?.ModParentField != null)
            {
                Widget dragWidget = ScMultiplayer.ModManager.ModParentField
                    .GetParentField(this, "m_dragWidget", typeof(DragHostWidget)) as Widget;
                if (dragWidget != null)
                {
                    object dragData = ScMultiplayer.ModManager.ModParentField
                        .GetParentField(this, "m_dragData", typeof(DragHostWidget));
                    Vector2 dragPosition = ScMultiplayer.ModManager.ModParentField
                        .GetParentField<Vector2>(this, "m_dragPosition", typeof(DragHostWidget));
                    IDragTargetWidget target = HitTestGlobal(dragPosition,
                        widget => widget is IDragTargetWidget) as IDragTargetWidget;
                    if (target is ViewWidget viewWidget &&
                        ScMultiplayer.currentInstance.TryHandlePlayerInventoryDragDrop(
                            viewWidget, dragWidget, dragData))
                    {
                        EndDrag();
                        return;
                    }
                }
            }
            base.Update();
        }
    }
}
