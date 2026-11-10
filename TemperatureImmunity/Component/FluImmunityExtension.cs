using Game;
using SuAPI;

namespace TemperatureImmunity
{
    // Source: ComponentFlu.cs:HasFlu/Update — clear real host fields, never shadow copies.
    internal sealed class FluImmunityBindings
    {
        private readonly ModFieldRef<ComponentFlu, float>[] m_fields;

        public FluImmunityBindings(IModParentField fields)
        {
            m_fields = new[]
            {
                fields.BindFieldRef<ComponentFlu, float>("m_fluOnset"),
                fields.BindFieldRef<ComponentFlu, float>("m_fluDuration"),
                fields.BindFieldRef<ComponentFlu, float>("m_coughDuration"),
                fields.BindFieldRef<ComponentFlu, float>("m_sneezeDuration"),
                fields.BindFieldRef<ComponentFlu, float>("m_blackoutDuration"),
                fields.BindFieldRef<ComponentFlu, float>("m_blackoutFactor")
            };
        }

        public void Clear(ComponentFlu host)
        {
            foreach (var field in m_fields) field(host) = 0f;
        }
    }

    internal sealed class FluImmunityExtension : UpdateExtension<ComponentFlu>
    {
        private readonly FluImmunityBindings m_state;

        public FluImmunityExtension(ComponentFlu host, FluImmunityBindings state) : base(host)
        {
            m_state = state;
        }

        public override void Attached() => m_state.Clear(Host);

        // Source: ComponentFlu.cs:Update; UpdateExtension.cs:Update(float,bool).
        // Veto the native flu body; other Mods' callbacks still participate in SuAPI dispatch.
        public override void Update(float dt)
        {
            m_state.Clear(Host);
            base.Update(dt, false);
        }

        public override void AfterUpdate(float dt, InvocationResult result) => m_state.Clear(Host);
    }
}
