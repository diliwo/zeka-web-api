using AutoMapper;
using Client.Application.Clients.Queries.GetClients;
using Client.Application.Common.Mappings;
using Client.Core.Entities;

namespace Client.Application.Tracks.Queries
{
    public class TrackDto : IMapFrom<Track>
    {
        public int SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; } = DateTime.Now;
        public int StaffId { get; set; }
        public int ClientId { get; set; }
        public string StaffInfo { get; set; }
        public ClientLookUpDto Client { get; set; }
        public bool IsActif { get; set; }
        public string Note { get; set; }
        public string? ReasonOfClosure { get; set; }
        public bool HasNote { get; set; }
        public bool IsLastSupport { get; set; }
        public void Mapping(Profile profile)
        {
            profile.CreateMap<Track, TrackDto>()
                .ForMember(b => b.SupportId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.StaffInfo,
                    opt =>
                        opt.MapFrom(e =>
                            e.StaffId != null
                                ? e.Staff.FullName + ' ' + '(' + e.Staff.Service.Acronym + ')'
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
