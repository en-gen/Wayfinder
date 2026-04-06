using AutoMapper;
using Flow.Grains.Executables;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;

namespace Flow.Grains.Infrastructure.AutoMapper
{
    public class CaseFlowProfile : Profile
    {
        public CaseFlowProfile()
        {
            CreateMap<Iso8601, Iso8601Snapshot>();

            CreateMap<CriterionStore, CriterionSnapshot>();

            CreateMap<StageBehaviorStore, StageBehaviorSnapshot>();
            CreateMap<TimerEventListenerBehaviorStore, TimerEventListenerBehaviorSnapshot>();

            CreateMap<PlanItemStore, PlanItemSnapshot>()
                .ForMember(dest => dest.BehaviorExtension, opts => opts.MapFrom(MapBehaviorStore));

            CreateMap<CaseStore, CaseSnapshot>()
                .ForMember(dest => dest.CasePlanModel, opts => opts.MapFrom(src => src.PlanItemDefinition));
        }

        private static object MapBehaviorStore(
            PlanItemStore src,
            PlanItemSnapshot dest,
            object destProp,
            ResolutionContext ctx)
        {
            switch (src.BehaviorExtension)
            {
                case StageBehaviorStore sbs:
                {
                    return ctx.Mapper.Map<StageBehaviorSnapshot>(sbs);
                }
                case TimerEventListenerBehaviorStore telbs:
                {
                    return ctx.Mapper.Map<TimerEventListenerBehaviorSnapshot>(telbs);
                }
                default:
                {
                    return null;
                }
            }
        }
    }
}
