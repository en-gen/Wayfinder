namespace Wayfinder.Contracts.V1
{
    // ADO #32 - the wire projection of Wayfinder.Grains.Interfaces.Model.PlanItemTransition (CMMN 1.1
    // Table 8.4's user/discretionary transitions). A deliberate 1:1 mirror kept in lockstep with
    // that enum's members - see PlanItemState.cs's remarks for why Wayfinder.Contracts does not simply
    // reference the domain enum. A future Wayfinder.Api layer maps this to the domain PlanItemTransition
    // when translating a TriggerCaseRequest into a TriggerCaseCommand (Unit 2 - out of ADO #32's
    // scope fence).
    public enum PlanItemTransition
    {
        Close,
        Complete,
        Create,
        Disable,
        Enable,
        Exit,
        Fault,
        ManualStart,
        Occur,
        ParentResume,
        ParentSuspend,
        Reactivate,
        Reenable,
        Resume,
        Start,
        Suspend,
        Terminate,
        ParentTerminate
    }
}
