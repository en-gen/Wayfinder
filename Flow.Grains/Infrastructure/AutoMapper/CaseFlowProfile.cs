using System.Linq;
using AutoMapper;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;

namespace Flow.Grains.Infrastructure.AutoMapper
{
    public class CaseFlowProfile : Profile
    {
        public CaseFlowProfile()
        {
            CreateMap<PlanItemStore.CriterionStore, PlanItemSnapshot.CriterionSnapshot>();
            CreateMap<PlanItemStore, PlanItemSnapshot>()
                .ForMember(dest => dest.Definition, opts => opts.Ignore())
                .ForMember(dest => dest.Children, opts => opts.PreCondition(src => src.Children.Any()));
            CreateMap<IBehaviorHost, PlanItemSnapshot>()
                .AfterMap((src, dest, ctx) => ctx.Mapper.Map(src.State, dest))
                .ForMember(dest => dest.Definition, opts => opts.MapFrom(src => src.State.Definition))
                .ForAllOtherMembers(opts => opts.Ignore());
        }
    }
}
