using AutoMapper;
using ClientManagement.Application.Clients.Queries.GetClients;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Supports.Queries
{
    public class SupportDto : IMapFrom<Support>
    {
        public int SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; } = DateTime.Now;
        public int StaffMemberId { get; set; }
        public int ClientId { get; set; }
        public string StaffMemberInfo { get; set; }
        public ClientLookUpDto Client { get; set; }
        public bool IsActif { get; set; }
        public string Note { get; set; }
        public string? ReasonOfClosure { get; set; }
        public bool HasNote { get; set; }
        public bool IsLastSupport { get; set; }
        public void Mapping(Profile profile)
        {
            profile.CreateMap<Core.Entities.Support, SupportDto>()
                .ForMember(b => b.SupportId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.StaffMemberInfo,
                    opt =>
                        opt.MapFrom(e =>
                            e.SocialWorkerId != null
                                ? e.SocialWorker.FullName + ' ' + '(' + e.SocialWorker.TeamAcronym + ')'
                                : string.Empty))
                .ForMember(s => s.HasNote, 
                    opt => 
                        opt.MapFrom(e =>  e.Note.Length > 0 ? true : false))
                .ForMember(l => l.IsLastSupport,
                    opt =>
                        opt.MapFrom(s => (s.Client.Supports.Where(s => s.Softdelete != true).OrderBy(c => c.Id).Last().Id == s.Id) ? true : false))
                .ForMember(r => r.ReasonOfClosure,
                    opt => opt.MapFrom(e => e.ReasonOfClosure));
        }
    }
}
