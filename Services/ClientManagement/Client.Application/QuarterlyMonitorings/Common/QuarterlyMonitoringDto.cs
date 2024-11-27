using AutoMapper;
using Client.Application.Common.Mappings;
using Client.Core.Entities;

namespace Client.Application.QuarterlyMonitorings.Common
{
    public class QuarterlyMonitoringDto : IMapFrom<QuarterlyMonitoring>
    {
        public int QMonitoringId { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; }
        public string ClientLastName { get; set; }
        public string ClientFirstName { get; set; }
        public string ClientNiss { get; set; }
        public string ClientDossier { get; set; }
        public int StaffId { get; set; }
        public string StaffName { get; set; }
        public string StaffLastName { get; set; }
        public string StaffFirstName { get; set; }
        public int MonitoringActionId { get; set; }
        public string MonitoringActionLabel { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }
        public string Quarter { get; set; }
        public bool Softdelete { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<QuarterlyMonitoring, QuarterlyMonitoringDto>()
                .ForMember(d => d.QMonitoringId, 
                    opt => opt.MapFrom(q => q.Id))
                .ForMember(d => d.ClientId, 
                    opt => opt.MapFrom(q => q.ClientId))
                .ForMember(d => d.ClientName,
                    opt => opt.MapFrom(q => q.Client.FullName))
                .ForMember(d => d.ClientLastName,
                    opt => opt.MapFrom(q => q.Client.LastName))
                .ForMember(d => d.ClientFirstName,
                    opt => opt.MapFrom(q => q.Client.FirstName))
                .ForMember(d => d.ClientNiss,
                    opt => opt.MapFrom(q => q.Client.Niss))
                .ForMember(d => d.ClientDossier,
                    opt => opt.MapFrom(q => q.Client.ReferenceNumber))
                .ForMember(d => d.StaffId,
                    opt => opt.MapFrom(q => q.StaffId))
                .ForMember(d => d.StaffName,
                    opt => opt.MapFrom(q => q.Staff.FullName))
                .ForMember(d => d.StaffLastName,
                    opt => opt.MapFrom(q => q.Staff.LastName))
                .ForMember(d => d.StaffFirstName,
                    opt => opt.MapFrom(q => q.Staff.FirstName))
                .ForMember(d => d.MonitoringActionId,
                    opt => opt.MapFrom(q => q.MonitoringActionId))
                .ForMember(d => d.MonitoringActionLabel,
                    opt => opt.MapFrom(q => q.MonitoringAction.Action))
                .ForMember(d => d.ActionDate,
                    opt => opt.MapFrom(q => q.ActionDate))
                .ForMember(d => d.ActionComment,
                    opt => opt.MapFrom(q => q.ActionComment))
                .ForMember(d => d.Quarter,
                    opt => opt.MapFrom(q => q.Quarter))
                .ForMember(d => d.Softdelete,
                    opt => opt.MapFrom(q => q.Softdelete));
        }
    }
}
