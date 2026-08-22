using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ControlAnimal;

public class ControlAnimalUiComponent : Component, IUpdateable
{
    private ControlAnimalComponent m_controlAnimal;

    // Source: Survivalcraft/Game/UpdateOrder.cs:UpdateOrder.Views
    // ComponentGui updates native visibility at Default; restore reused controls afterwards.
    public UpdateOrder UpdateOrder => UpdateOrder.Views;

    protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        m_controlAnimal = Entity.FindComponent<ControlAnimalComponent>(throwOnError: true);
    }

    void IUpdateable.Update(float dt)
    {
        m_controlAnimal.UpdateReusedControlButtons();
    }
}
